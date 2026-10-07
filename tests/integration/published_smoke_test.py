"""Smoke test a published LanPong executable without requiring the .NET SDK."""

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


def main():
    value = os.environ.get("LANPONG_TEST_BINARY")
    if not value:
        raise ValueError("Set LANPONG_TEST_BINARY to the published LanPong executable")
    binary = Path(value).resolve()
    if not binary.is_file():
        raise FileNotFoundError(f"Published executable does not exist: {binary}")

    verify_onnx_smoke(binary)

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
