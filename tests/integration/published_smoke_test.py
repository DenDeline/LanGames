"""Smoke test a published LanPong executable without requiring the .NET SDK."""

import hashlib
import json
import math
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

    # Real play must execute several model decisions, beyond preparation and countdown.
    started = request_json(base_url, "/api/local-opponent", {"nickname": "Smoke", "botId": "vektor"})
    assert started["version"] == 8 and started["opponentMode"] == "bot", started
    assert started["requestedBotId"] == started["effectiveBotId"] == "vektor", started
    assert started["requestedBotName"] == started["effectiveBotName"] == by_id["vektor"]["name"], started
    assert started["peerNickname"] is None and started["opponentFallbackActive"] is False, started
    assert started["botFallbackReason"] is None and "requestedOpponentMode" not in started, started

    def healthy(snapshot):
        assert snapshot["version"] == 8 and snapshot["opponentMode"] == "bot", snapshot
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
        # Reuse the binary protocol fixture, including its exact v8 snapshot shape.
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


def verify_configured_benchmark(binary):
    # Holding the requested HTTP endpoint makes accidental server startup fail.
    # Diagnostics must load normal configuration without starting networking.
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as occupied:
        occupied.bind(("127.0.0.1", 0))
        occupied.listen()
        url = f"http://127.0.0.1:{occupied.getsockname()[1]}"
        with tempfile.TemporaryDirectory() as diagnostic_cwd:
            def run(arguments, extra_env=None, content_root=binary.parent):
                root_arguments = [] if content_root is None else ["--contentRoot", str(content_root)]
                return subprocess.run(
                    [str(binary), "--bot-benchmark", *arguments,
                     "--benchmark-samples", "100", "--benchmark-warmup", "2",
                     *root_arguments, "--urls", url],
                    cwd=diagnostic_cwd,
                    env={**os.environ, **(extra_env or {})},
                    capture_output=True, text=True, timeout=60, check=False,
                )

            def verify_report(result, bot_id, cadence, expected_hash=None):
                assert result.returncode == 0, (result.returncode, result.stdout, result.stderr)
                report = json.loads(result.stdout)
                assert report["requestedBotId"] == report["effectiveBotId"] == bot_id, report
                assert report["strategyId"] == ("onnx" if expected_hash else "tracker"), report
                assert report["cadenceTicks"] == cadence and type(report["dynamicCodeSupported"]) is bool, report
                assert report["verifiedModelSha256"] == expected_hash, report
                if expected_hash:
                    assert report["frozenModelDigest"] is True, report
                assert report["samples"] == 100 and report["warmup"] == 2, report
                assert report["scope"] and report["runtime"] and report["architecture"], report
                assert math.isfinite(report["preparationMilliseconds"]) and report["preparationMilliseconds"] >= 0, report
                workloads = report["workloads"]
                assert [workload["name"] for workload in workloads] == ["consecutive", "cadence-spaced"], report
                for workload, stride in zip(workloads, (1, cadence)):
                    assert workload["count"] == 100 and workload["tickStride"] == stride, workload
                    assert 0 < workload["firstTick"] < workload["lastTick"], workload
                    assert all(type(workload[key]) is int and 0 <= workload[key] < 2 ** 64
                               for key in ("batchChecksum", "sampledChecksum")), workload
                    for measurement in (workload, workload["timestampFloor"]):
                        assert math.isfinite(measurement["batchMeanMilliseconds"]) and measurement["batchMeanMilliseconds"] >= 0, measurement
                        assert type(measurement["batchAllocatedBytes"]) is int and measurement["batchAllocatedBytes"] >= 0, measurement
                        latency = measurement["latency"]
                        values = [latency[key] for key in
                                  ("medianMilliseconds", "p95Milliseconds", "p99Milliseconds", "worstMilliseconds")]
                        assert all(math.isfinite(value) and value >= 0 for value in values) and values == sorted(values), latency
                        assert type(latency["allocatedBytes"]) is int and latency["allocatedBytes"] >= 0, latency

            # No ID uses a normal provider override; retained CLI args also change tuning.
            configured_default = run(
                ["--Bots:Entries:1:Tracker:ObservationIntervalTicks", "4"],
                {"Bots__DefaultBotId": "iskra"},
            )
            verify_report(configured_default, "iskra", 4)
            verify_report(run(["vektor"]), "vektor", MODEL_CADENCE_TICKS, MODEL_SHA256)

            # ASP.NET Core host settings must choose a root distinct from cwd and
            # then load that environment's normal JSON provider before bot resolution.
            provider_root = Path(diagnostic_cwd) / "configuration-provider"
            provider_root.mkdir()
            (provider_root / "appsettings.json").write_bytes((binary.parent / "appsettings.json").read_bytes())
            (provider_root / "appsettings.DiagnosticProviderSmoke.json").write_text(json.dumps({
                "Bots": {"DefaultBotId": "iskra", "Entries": {
                    "1": {"Tracker": {"ObservationIntervalTicks": 6}}
                }}
            }), encoding="utf-8")
            environment_selected = run([], {
                "ASPNETCORE_ENVIRONMENT": "DiagnosticProviderSmoke",
                "ASPNETCORE_CONTENTROOT": str(provider_root),
            }, content_root=None)
            verify_report(environment_selected, "iskra", 6)

            # An unavailable selected model must fail, even though play has an explicit tracker fallback.
            missing = Path(diagnostic_cwd) / "missing-selected-model.onnx"
            rejected = run(["vektor", "--Bots:Entries:2:Onnx:ModelPath", str(missing)])
            assert rejected.returncode != 0, (rejected.stdout, rejected.stderr)
            assert '"workloads"' not in rejected.stdout, rejected.stdout
            invalid_configuration = run(["--Bots:DefaultBotId", "unknown-diagnostic-bot"])
            assert invalid_configuration.returncode != 0, (invalid_configuration.stdout, invalid_configuration.stderr)
            assert "Bots:DefaultBotId" in invalid_configuration.stderr and "unknown-diagnostic-bot" in invalid_configuration.stderr, invalid_configuration.stderr
    print("PASS: published configured benchmarks honor default/CLI/ASP.NET providers, verify model identity and reject fallback/invalid settings without starting HTTP")


def main():
    value = os.environ.get("LANPONG_TEST_BINARY")
    if not value:
        raise ValueError("Set LANPONG_TEST_BINARY to the published LanPong executable")
    binary = Path(value).resolve()
    if not binary.is_file():
        raise FileNotFoundError(f"Published executable does not exist: {binary}")

    verify_onnx_smoke(binary)
    verify_configured_benchmark(binary)

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
