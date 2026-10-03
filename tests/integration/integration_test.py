import base64
import json
import os
import socket
import subprocess
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "src" / "LanPong"
DLL = PROJECT / "bin" / "Release" / "net10.0" / "LanPong.dll"


def request(port, path, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}{path}",
        data=data,
        headers={"Content-Type": "application/json"} if data is not None else {},
        method="POST" if data is not None else "GET",
    )
    with urllib.request.urlopen(req, timeout=3) as response:
        return json.load(response)


def wait_until(label, check, seconds=8):
    until = time.monotonic() + seconds
    while time.monotonic() < until:
        try:
            result = check()
            if result:
                return result
        except (OSError, ValueError):
            pass
        time.sleep(0.1)
    raise AssertionError(f"Timed out: {label}")


def websocket(port):
    conn = socket.create_connection(("127.0.0.1", port), timeout=3)
    key = base64.b64encode(os.urandom(16)).decode()
    conn.sendall((
        "GET /ws HTTP/1.1\r\n"
        f"Host: 127.0.0.1:{port}\r\n"
        "Upgrade: websocket\r\n"
        "Connection: Upgrade\r\n"
        f"Sec-WebSocket-Key: {key}\r\n"
        "Sec-WebSocket-Version: 13\r\n\r\n"
    ).encode())
    response = b""
    while b"\r\n\r\n" not in response:
        response += conn.recv(1024)
    assert b"101 Switching Protocols" in response, response
    return conn


def send_text(conn, text):
    data = text.encode()
    assert len(data) < 126
    mask = os.urandom(4)
    masked = bytes(byte ^ mask[index % 4] for index, byte in enumerate(data))
    conn.sendall(bytes([0x81, 0x80 | len(data)]) + mask + masked)


subprocess.run(["dotnet", "build", str(PROJECT / "LanPong.csproj"), "-c", "Release"],
               cwd=ROOT, check=True)
log_dir = ROOT / ".artifacts" / "test-logs"
log_dir.mkdir(parents=True, exist_ok=True)
logs = [open(log_dir / f"pong-{port}.log", "w") for port in (5180, 5181)]
processes = []
try:
    for port, log in zip((5180, 5181), logs):
        processes.append(subprocess.Popen(
            ["dotnet", str(DLL)],
            cwd=PROJECT, stdout=log, stderr=subprocess.STDOUT,
            env={**os.environ, "ASPNETCORE_URLS": f"http://127.0.0.1:{port}"},
        ))

    wait_until("web servers", lambda: request(5180, "/api/status") and request(5181, "/api/status"))
    assert b"game-canvas" in urllib.request.urlopen("http://127.0.0.1:5180/").read()

    host = request(5180, "/api/host", {"port": 47888})
    assert host["role"] == "host" and host["connection"] == "waiting", host
    hosts = request(5181, "/api/discover?port=47888")["hosts"]
    assert any(item["port"] == 47888 for item in hosts), hosts
    guest = request(5181, "/api/join", {"address": "127.0.0.1", "port": 47888})
    assert guest["role"] == "guest" and guest["connection"] == "connecting", guest

    wait_until("UDP handshake", lambda:
               request(5180, "/api/status")["connection"] == "connected"
               and request(5181, "/api/status")["connection"] == "connected")
    wait_until("gameplay", lambda: request(5180, "/api/status")["phase"] == "playing")
    wait_until("UDP RTT", lambda:
               request(5180, "/api/status").get("pingMs") is not None
               and request(5181, "/api/status").get("pingMs") is not None)
    before = request(5180, "/api/status")
    assert 0 <= before["pingMs"] < 2000, before
    assert abs(before["ballVx"]) > 0 and before["roundId"] >= 1, before
    with websocket(5180) as host_ws, websocket(5180) as passive_ws, websocket(5181) as guest_ws:
        for _ in range(25):
            send_text(host_ws, '{"axis":-1}')
            send_text(passive_ws, '{"axis":0}')
            send_text(guest_ws, '{"axis":1}')
            time.sleep(0.04)
        moved = request(5180, "/api/status")
        assert moved["leftY"] < before["leftY"], (before, moved)
        assert moved["rightY"] > before["rightY"], (before, moved)
        synced = request(5181, "/api/status")
        assert abs(synced["leftY"] - moved["leftY"]) < 0.1, (moved, synced)

    request(5181, "/api/restart", {})
    wait_until("guest restart request", lambda: request(5180, "/api/status")["phase"] == "countdown")
    request(5181, "/api/leave", {})
    wait_until("host observes guest leave", lambda: request(5180, "/api/status")["connection"] == "waiting")
    assert request(5180, "/api/status").get("pingMs") is None
    print("PASS: static UI, discovery, UDP handshake, ping RTT, gameplay, both inputs, passive second tab, state sync, restart, leave")
finally:
    for process in processes:
        process.terminate()
    for process in processes:
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
    for log in logs:
        log.close()
