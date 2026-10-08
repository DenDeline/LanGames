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


HARD_MODEL_SHA256 = "5d5d3cf0910d967cf2d6dc60e8fe0b63f772060178bf6673f6ddc5cdba98ab5a"
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


def verify_catalog_contract(base_url, status):
    identity_fields = ("requestedBotId", "requestedBotName", "effectiveBotId", "effectiveBotName", "botFallbackReason")
    assert status["version"] == 8 and "requestedOpponentMode" not in status, status
    assert status["opponentMode"] == "none" and status["opponentFallbackActive"] is False, status
    assert all(status[field] is None for field in identity_fields), status
    catalog = request_json(base_url, "/api/bots")
    assert set(catalog) == {"version", "defaultBotId", "bots"} and catalog["version"] == 8, catalog
    bots = catalog["bots"]
    assert bots == sorted(bots, key=lambda bot: (bot["order"], bot["id"])), catalog
    by_id = {bot["id"]: bot for bot in bots}
    assert catalog["defaultBotId"] in by_id and {"lada", "iskra", "vektor"} <= by_id.keys(), catalog
    assert all(set(bot) == BOT_FIELDS for bot in bots), catalog
    assert by_id["vektor"]["availability"] == "notChecked", catalog
    assert all(by_id[bot_id]["availability"] == "ready" for bot_id in ("lada", "iskra")), catalog

    # Exercise the configured production path in the published app as well as standalone diagnostics.
    started = request_json(base_url, "/api/local-opponent", {"nickname": "Smoke", "botId": "vektor"})
    assert started["version"] == 8 and started["opponentMode"] == "bot", started
    assert started["requestedBotId"] == started["effectiveBotId"] == "vektor", started
    assert started["requestedBotName"] == started["effectiveBotName"] == by_id["vektor"]["name"], started
    assert started["peerNickname"] is None and started["opponentFallbackActive"] is False, started
    assert started["botFallbackReason"] is None and "requestedOpponentMode" not in started, started
    assert next(bot for bot in request_json(base_url, "/api/bots")["bots"] if bot["id"] == "vektor")["availability"] == "ready"
    left = request_json(base_url, "/api/leave", {})
    assert left["opponentMode"] == "none" and all(left[field] is None for field in identity_fields), left
    print("PASS: published protocol v8 status/catalog and configured model identity")


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


def verify_onnx_smoke(binary):
    model = binary.parent / "Models" / "aot-smoke.onnx"
    assert model.is_file() and model.stat().st_size > 0, f"Published ONNX model is missing: {model}"

    library_name = {
        "Darwin": "libonnxruntime.dylib",
        "Linux": "libonnxruntime.so",
        "Windows": "onnxruntime.dll",
    }[platform.system()]
    libraries = [path for path in binary.parent.rglob(library_name) if path.is_file()]
    assert libraries, f"Published ONNX Runtime native library is missing: {library_name}"

    # Keep ONNX Runtime's optional telemetry cache out of the release archive.
    with tempfile.TemporaryDirectory() as smoke_cwd:
        result = subprocess.run(
            [str(binary), "--onnx-smoke"],
            cwd=smoke_cwd,
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )
    expected = "ONNX smoke passed: 2 -> 3"
    assert result.returncode == 0 and expected in result.stdout, (
        f"Published ONNX inference failed (exit {result.returncode}).\n"
        f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}"
    )
    print(f"PASS: published ONNX inference returned 2 -> 3 with {libraries[0].name}")


def verify_hard_smoke(binary):
    model = binary.parent / "Models" / "hard-v1.onnx"
    assert model.is_file() and model.stat().st_size > 0, f"Published Hard model is missing: {model}"
    digest = hashlib.sha256(model.read_bytes()).hexdigest()
    assert digest == HARD_MODEL_SHA256, (
        f"Published Hard model SHA-256 mismatch: expected {HARD_MODEL_SHA256}, got {digest}"
    )

    # The CLI path loads the packaged model and executes the trained diagnostic controller.
    with tempfile.TemporaryDirectory() as smoke_cwd:
        result = subprocess.run(
            [str(binary), "--hard-smoke"],
            cwd=smoke_cwd,
            capture_output=True,
            text=True,
            timeout=60,
            check=False,
        )
    expected = f"Hard ONNX smoke passed: SHA-256 {HARD_MODEL_SHA256}, axis "
    assert result.returncode == 0 and expected in result.stdout, (
        f"Published Hard inference failed (exit {result.returncode}).\n"
        f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}"
    )
    print(f"PASS: published Hard ONNX inference used model SHA-256 {digest}")


def main():
    value = os.environ.get("LANPONG_TEST_BINARY")
    if not value:
        raise ValueError("Set LANPONG_TEST_BINARY to the published LanPong executable")
    binary = Path(value).resolve()
    if not binary.is_file():
        raise FileNotFoundError(f"Published executable does not exist: {binary}")

    verify_onnx_smoke(binary)
    verify_hard_smoke(binary)

    port = free_port()
    base_url = f"http://127.0.0.1:{port}"
    process = None
    with tempfile.TemporaryFile(mode="w+b") as log:
        try:
            process = subprocess.Popen(
                [str(binary), "--urls", base_url],
                cwd=binary.parent,
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            status = wait_for_status(process, base_url)
            udp_port = status.get("udpPort")
            local_addresses = status.get("localAddresses")
            assert type(udp_port) is int and 0 <= udp_port <= 65535, status
            assert isinstance(local_addresses, list) and "127.0.0.1" in local_addresses, status
            verify_catalog_contract(base_url, status)

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
