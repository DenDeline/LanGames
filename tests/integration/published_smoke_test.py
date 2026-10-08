"""Smoke test a published LanPong executable without requiring the .NET SDK."""

import hashlib
import json
import os
import platform
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

from integration_test import decode_snapshot, expect_identity_parity, recv_frame, send_frame, websocket


MODEL_SHA256 = "5d5d3cf0910d967cf2d6dc60e8fe0b63f772060178bf6673f6ddc5cdba98ab5a"
MODEL_CADENCE_TICKS = 9
BOT_FIELDS = {
    "id", "name", "description", "style", "difficulty", "category", "order", "glyph",
    "enabled", "fallbackBotId", "availability", "availabilityReason", "canPlay",
}


def request_json(base_url, path, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(
        f"{base_url}{path}", data=data,
        headers={"Content-Type": "application/json"} if data is not None else {},
        method="POST" if data is not None else "GET",
    )
    with urllib.request.urlopen(request, timeout=5) as response:
        assert response.status == 200, (path, response.status)
        return json.load(response)


def verify_catalog_contract(binary, port, base_url, status):
    model = binary.parent / "Models" / "hard-v1.onnx"
    assert model.is_file() and model.stat().st_size > 0, f"Published bot model is missing: {model}"
    digest = hashlib.sha256(model.read_bytes()).hexdigest()
    assert digest == MODEL_SHA256, (MODEL_SHA256, digest)

    identity_fields = ("requestedBotId", "requestedBotName", "effectiveBotId", "effectiveBotName", "botFallbackReason")
    assert status["version"] == 9 and "requestedOpponentMode" not in status, status
    assert status["canRematch"] is False, status
    assert status["opponentMode"] == "none" and status["opponentFallbackActive"] is False, status
    assert all(status[field] is None for field in identity_fields), status
    catalog = request_json(base_url, "/api/bots")
    assert set(catalog) == {"version", "defaultBotId", "bots"} and catalog["version"] == 9, catalog
    bots = catalog["bots"]
    assert bots == sorted(bots, key=lambda bot: (bot["order"], bot["id"])), catalog
    by_id = {bot["id"]: bot for bot in bots}
    assert catalog["defaultBotId"] in by_id and {"lada", "iskra", "vektor"} <= by_id.keys(), catalog
    assert all(set(bot) == BOT_FIELDS for bot in bots), catalog
    assert by_id["vektor"]["availability"] == "notChecked", catalog
    assert all(by_id[bot_id]["availability"] == "ready" for bot_id in ("lada", "iskra")), catalog

    # Real play must execute several model decisions, beyond preparation and countdown.
    started = request_json(base_url, "/api/local-opponent", {"nickname": "Smoke", "botId": "vektor", "side": "left"})
    assert started["version"] == 9 and started["opponentMode"] == "bot", started
    assert started["localSide"] == "left", started
    assert started["canRematch"] is False, started
    assert started["requestedBotId"] == started["effectiveBotId"] == "vektor", started
    assert started["requestedBotName"] == started["effectiveBotName"] == by_id["vektor"]["name"], started
    assert started["peerNickname"] is None and started["opponentFallbackActive"] is False, started
    assert started["botFallbackReason"] is None and "requestedOpponentMode" not in started, started

    def healthy(snapshot):
        assert snapshot["version"] == 9 and snapshot["opponentMode"] == "bot", snapshot
        assert snapshot["localSide"] == "left", snapshot
        assert snapshot["canRematch"] is False, snapshot
        assert snapshot["requestedBotId"] == snapshot["effectiveBotId"] == "vektor", snapshot
        assert snapshot["requestedBotName"] == snapshot["effectiveBotName"] == by_id["vektor"]["name"], snapshot
        assert snapshot["peerNickname"] is None and snapshot["opponentFallbackActive"] is False, snapshot
        assert snapshot["botFallbackReason"] is None, snapshot

    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        playing = request_json(base_url, "/api/status")
        healthy(playing)
        if playing["phase"] == "playing":
            break
        time.sleep(0.05)
    else:
        raise AssertionError("Published Vektor did not enter Playing")

    with websocket(port) as conn:
        # Reuse the binary protocol fixture, including its exact v9 snapshot shape.
        while time.monotonic() < deadline:
            conn.settimeout(max(0.01, deadline - time.monotonic()))
            snapshot = decode_snapshot(recv_frame(conn))
            healthy(snapshot)
            if (snapshot["phase"] == "playing"
                    and snapshot["tick"] >= playing["tick"] + 2 * MODEL_CADENCE_TICKS
                    and abs(snapshot["rightY"] - started["rightY"]) > 0.001):
                break
        else:
            raise AssertionError("Published Vektor did not advance model cadence and move its paddle")
        current = request_json(base_url, "/api/status")
        healthy(current)
        assert current["phase"] == "playing" and current["tick"] >= playing["tick"] + 2 * MODEL_CADENCE_TICKS, current
        expect_identity_parity(current, snapshot)
        send_frame(conn, 0x8, (1000).to_bytes(2, "big"))

    assert next(bot for bot in request_json(base_url, "/api/bots")["bots"] if bot["id"] == "vektor")["availability"] == "ready"
    left = request_json(base_url, "/api/leave", {})
    assert left["opponentMode"] == "none" and all(left[field] is None for field in identity_fields), left
    print(f"PASS: published Vektor Playing, cadence-advanced HTTP/WebSocket identity, paddle motion and model SHA-256 {digest}")


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def stop(process):
    if process is None or process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def wait_for_status(process, base_url):
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"LanPong exited during startup with code {process.returncode}")
        try:
            with urllib.request.urlopen(f"{base_url}/api/status", timeout=1) as response:
                if response.status != 200:
                    raise AssertionError(f"/api/status returned HTTP {response.status}")
                return json.load(response)
        except (urllib.error.URLError, TimeoutError):
            time.sleep(0.1)
    raise TimeoutError("LanPong did not serve /api/status within 20 seconds")


def verify_native_runtime(binary):
    library_name = {
        "Darwin": "libonnxruntime.dylib",
        "Linux": "libonnxruntime.so",
        "Windows": "onnxruntime.dll",
    }[platform.system()]
    libraries = [path for path in binary.parent.rglob(library_name) if path.is_file()]
    assert libraries, f"Published ONNX Runtime native library is missing: {library_name}"
    print(f"PASS: published ONNX Runtime native library is present: {libraries[0].name}")


def main():
    value = os.environ.get("LANPONG_TEST_BINARY")
    if not value:
        raise ValueError("Set LANPONG_TEST_BINARY to the published LanPong executable")
    binary = Path(value).resolve()
    if not binary.is_file():
        raise FileNotFoundError(f"Published executable does not exist: {binary}")

    verify_native_runtime(binary)

    port = free_port()
    base_url = f"http://127.0.0.1:{port}"
    process = None
    with tempfile.TemporaryFile(mode="w+b") as log:
        try:
            process = subprocess.Popen(
                [str(binary), "--environment", "Production", "--contentRoot", str(binary.parent), "--urls", base_url],
                cwd=binary.parent,
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            status = wait_for_status(process, base_url)
            udp_port = status.get("udpPort")
            local_addresses = status.get("localAddresses")
            assert type(udp_port) is int and 0 <= udp_port <= 65535, status
            assert isinstance(local_addresses, list) and "127.0.0.1" in local_addresses, status
            verify_catalog_contract(binary, port, base_url, status)

            with urllib.request.urlopen(f"{base_url}/", timeout=3) as response:
                page = response.read()
                assert response.status == 200, response.status
                assert response.headers.get_content_type() == "text/html", response.headers
                assert b"<html" in page.lower() and b"game-canvas" in page, page[:200]
            print(f"PASS: published LanPong serves HTML and /api/status on {base_url}")
        except Exception:
            stop(process)
            log.flush()
            log.seek(0)
            print("LanPong process output:", file=sys.stderr)
            print(log.read().decode("utf-8", errors="replace") or "(empty)", file=sys.stderr)
            raise
        finally:
            stop(process)


if __name__ == "__main__":
    main()
