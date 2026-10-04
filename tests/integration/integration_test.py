import base64
import json
import os
import re
import socket
import subprocess
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "src" / "LanPong"
DLL = PROJECT / "bin" / "Release" / "net10.0" / "LanPong.dll"
MSGPACK_HELPER = ROOT / "tests" / "integration" / "msgpack_interop.mjs"
SNAPSHOT_FIELDS = (
    "version", "role", "connection", "message", "udpPort", "localAddresses",
    "peerAddress", "leftY", "rightY", "ballX", "ballY", "ballVx", "ballVy",
    "leftScore", "rightScore", "phase", "countdown", "tick", "roundId", "pingMs",
)
ROLES = ("none", "host", "guest")
CONNECTIONS = ("idle", "waiting", "connecting", "connected")
PHASES = ("waiting", "countdown", "playing", "gameover")


def msgpack_helper(operation, payload=b""):
    result = subprocess.run(
        ["node", str(MSGPACK_HELPER), operation],
        input=base64.b64encode(payload), capture_output=True, check=True, cwd=ROOT,
    )
    return json.loads(result.stdout)


def control_packets():
    return {int(axis): base64.b64decode(encoded) for axis, encoded in
            msgpack_helper("encode-controls").items()}


def decode_snapshot(frame):
    assert frame is not None and frame[0] == 0x2, frame
    values = msgpack_helper("decode", frame[1])
    assert isinstance(values, list) and len(values) == len(SNAPSHOT_FIELDS), values
    snapshot = dict(zip(SNAPSHOT_FIELDS, values))
    assert snapshot["version"] == 1, snapshot
    snapshot["role"] = ROLES[snapshot["role"]]
    snapshot["connection"] = CONNECTIONS[snapshot["connection"]]
    snapshot["phase"] = PHASES[snapshot["phase"]]
    assert isinstance(snapshot["localAddresses"], list), snapshot
    assert isinstance(snapshot["message"], str), snapshot
    return snapshot


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
        # Do not consume bytes from the first snapshot after the HTTP headers.
        chunk = conn.recv(1)
        assert chunk, response
        response += chunk
    assert b"101 Switching Protocols" in response, response
    return conn


def send_frame(conn, opcode, data, final=True):
    length = len(data)
    if length < 126:
        header = bytes([(0x80 if final else 0) | opcode, 0x80 | length])
    elif length <= 65535:
        header = bytes([(0x80 if final else 0) | opcode, 0x80 | 126]) + length.to_bytes(2, "big")
    else:
        header = bytes([(0x80 if final else 0) | opcode, 0x80 | 127]) + length.to_bytes(8, "big")
    mask = os.urandom(4)
    masked = bytes(byte ^ mask[index % 4] for index, byte in enumerate(data))
    conn.sendall(header + mask + masked)


def send_text(conn, text):
    send_frame(conn, 0x1, text.encode())


def send_binary(conn, data):
    send_frame(conn, 0x2, data)


def send_fragmented_binary(conn, data):
    middle = len(data) // 2
    send_frame(conn, 0x2, data[:middle], final=False)
    send_frame(conn, 0x0, data[middle:])


def recv_exact(conn, count):
    data = bytearray()
    while len(data) < count:
        chunk = conn.recv(count - len(data))
        if not chunk:
            raise EOFError("WebSocket closed")
        data.extend(chunk)
    return bytes(data)


def recv_frame(conn):
    try:
        first, second = recv_exact(conn, 2)
        assert first & 0x80, "Unexpected fragmented server frame"
        assert not second & 0x80, "Server frame must not be masked"
        length = second & 0x7f
        if length == 126:
            length = int.from_bytes(recv_exact(conn, 2), "big")
        elif length == 127:
            length = int.from_bytes(recv_exact(conn, 8), "big")
        assert length < 1_000_000, length
        return first & 0x0f, recv_exact(conn, length)
    except EOFError:
        return None


def expect_prompt_close(conn, seconds=2):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        conn.settimeout(max(0.01, deadline - time.monotonic()))
        try:
            frame = recv_frame(conn)
        except ConnectionResetError:
            return
        except socket.timeout:
            break
        if frame is None or frame[0] == 0x8:
            return
    raise AssertionError("WebSocket did not close after the client close frame")


def expect_shutdown_close(conn, seconds=2):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        conn.settimeout(max(0.01, deadline - time.monotonic()))
        try:
            frame = recv_frame(conn)
        except socket.timeout as exc:
            raise AssertionError("Timed out waiting for the server WebSocket close frame") from exc
        if frame is None:
            raise AssertionError("WebSocket closed without a server close frame")
        if frame[0] == 0x8:
            assert len(frame[1]) >= 2, "Server close frame has no status code"
            code = int.from_bytes(frame[1][:2], "big")
            assert code == 1001, f"Expected Going Away (1001), got {code}"
            return
    raise AssertionError("Server WebSocket close frame was not received")


def launch(port, log):
    return subprocess.Popen(
        ["dotnet", str(DLL)],
        cwd=PROJECT, stdout=log, stderr=subprocess.STDOUT,
        env={**os.environ, "ASPNETCORE_URLS": f"http://127.0.0.1:{port}"},
    )


def terminate_connected_process(process, port, remote_port, remote_connection, role):
    with websocket(port) as conn:
        assert decode_snapshot(recv_frame(conn))["connection"] == "connected"

        started = time.monotonic()
        process.terminate()
        expect_shutdown_close(conn)
        try:
            send_frame(conn, 0x8, (1001).to_bytes(2, "big"))
        except (BrokenPipeError, ConnectionResetError):
            # The close frame is the contract; the process may already have exited.
            pass
        wait_until(f"{role} shutdown Bye reaches remote peer", lambda:
                   request(remote_port, "/api/status")["connection"] == remote_connection,
                   seconds=max(0, 2 - (time.monotonic() - started)))
        remaining = 4 - (time.monotonic() - started)
        assert remaining > 0, f"{role} shutdown exceeded four seconds"
        try:
            exit_code = process.wait(timeout=remaining)
        except subprocess.TimeoutExpired as exc:
            raise AssertionError(f"{role} did not exit within four seconds") from exc
        assert exit_code == 0, f"{role} exited with code {exit_code}"


def send_oversized_udp_datagrams(*ports):
    # 0xc1 is reserved by MessagePack. The first size is exactly one byte over
    # the protocol limit; the second exceeds the new receive buffer by far.
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
        for port in ports:
            for length in (1201, 8 * 1024):
                payload = bytes([0xc1]) * length
                assert sender.sendto(payload, ("127.0.0.1", port)) == length


subprocess.run(["pnpm", "build"], cwd=ROOT, check=True)
subprocess.run(["dotnet", "build", str(PROJECT / "LanPong.csproj"), "-c", "Release"],
               cwd=ROOT, check=True)
log_dir = ROOT / ".artifacts" / "test-logs"
log_dir.mkdir(parents=True, exist_ok=True)
logs = [open(log_dir / f"pong-{port}.log", "w") for port in (5180, 5181)]
processes = []
try:
    for port, log in zip((5180, 5181), logs):
        processes.append(launch(port, log))

    wait_until("web servers", lambda: request(5180, "/api/status") and request(5181, "/api/status"))
    page = urllib.request.urlopen("http://127.0.0.1:5180/").read()
    assert b"game-canvas" in page
    assets = re.findall(rb'(?:src|href)="(/assets/[^"]+)"', page)
    assert len(assets) >= 2, assets
    for asset in assets:
        with urllib.request.urlopen(f"http://127.0.0.1:5180{asset.decode()}") as response:
            assert response.status == 200 and response.read(), asset

    for port in (5180, 5181):
        idle = request(port, "/api/status")
        assert idle["role"] == "none" and idle["connection"] == "idle", idle
        assert idle["phase"] == "waiting", idle

    host = request(5180, "/api/host", {"port": 47888})
    assert host["role"] == "host" and host["connection"] == "waiting", host
    # A datagram with a valid Hello prefix must still be rejected in full when
    # the receive buffer truncates it at the packet-size boundary.
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
        hello_prefix = bytes([0x92, 0x02, 0x91, 0x03])
        payload = hello_prefix + bytes(1201 - len(hello_prefix))
        assert sender.sendto(payload, ("127.0.0.1", 47888)) == len(payload)
    time.sleep(0.2)
    assert request(5180, "/api/status")["connection"] == "waiting"
    hosts = request(5181, "/api/discover?port=47888")["hosts"]
    assert any(item["port"] == 47888 for item in hosts), hosts
    guest = request(5181, "/api/join", {"address": "127.0.0.1", "port": 47888})
    assert guest["role"] == "guest" and guest["connection"] == "connecting", guest

    wait_until("UDP handshake", lambda:
               request(5180, "/api/status")["connection"] == "connected"
               and request(5181, "/api/status")["connection"] == "connected")
    # Once connected, a datagram from a third socket must not be treated as the peer.
    # The host offers discovery only while waiting for a player.
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as stranger:
        stranger.bind(("127.0.0.1", 0))
        stranger.settimeout(0.2)
        stranger.sendto(bytes([0x92, 0x00, 0x91, 0x03]), ("127.0.0.1", 47888))
        try:
            stranger.recvfrom(1201)
            raise AssertionError("connected host replied to an unknown UDP sender")
        except socket.timeout:
            pass
    wait_until("gameplay", lambda: request(5180, "/api/status")["phase"] == "playing")
    wait_until("UDP RTT", lambda:
               request(5180, "/api/status").get("pingMs") is not None
               and request(5181, "/api/status").get("pingMs") is not None)
    before = request(5180, "/api/status")
    before_guest = request(5181, "/api/status")
    assert 0 <= before["pingMs"] < 2000, before
    assert abs(before["ballVx"]) > 0 and before["roundId"] >= 1, before
    send_oversized_udp_datagrams(before["udpPort"], before_guest["udpPort"])
    wait_until("gameplay survives oversized UDP datagrams", lambda:
               request(5180, "/api/status")["tick"] >= before["tick"] + 10
               and request(5181, "/api/status")["tick"] >= before_guest["tick"] + 10
               and request(5180, "/api/status")["connection"] == "connected"
               and request(5181, "/api/status")["connection"] == "connected")
    with websocket(5180) as host_ws, websocket(5180) as passive_ws, websocket(5181) as guest_ws:
        controls = control_packets()
        snapshot = decode_snapshot(recv_frame(host_ws))
        assert snapshot["role"] == "host" and snapshot["connection"] == "connected", snapshot
        assert "tick" in snapshot and "leftY" in snapshot and "rightY" in snapshot, snapshot

        send_text(host_ws, '{"axis":1}')
        send_binary(host_ws, b"\x90")  # Wrong array shape.
        send_binary(host_ws, b"\x92\x02\x01")  # Wrong protocol version.
        send_binary(host_ws, b"\x92\x01\xa2up")  # Non-numeric axis.
        send_binary(host_ws, b"\x92\x01\x02")  # Axis outside -1..1.
        send_binary(host_ws, b"\xc1")  # Reserved MessagePack prefix.
        send_binary(host_ws, controls[1] + b"\x00")  # A second packed value.
        send_binary(host_ws, controls[1] + bytes(257 - len(controls[1])))
        time.sleep(0.15)
        after_invalid = request(5180, "/api/status")
        assert abs(after_invalid["leftY"] - before["leftY"]) < 0.005, (before, after_invalid)

        for _ in range(8):
            send_binary(host_ws, controls[-1])
            time.sleep(0.04)
        after_malformed = request(5180, "/api/status")
        assert after_malformed["leftY"] < before["leftY"], (before, after_malformed)

        for _ in range(8):
            send_fragmented_binary(host_ws, controls[1])
            time.sleep(0.04)
        after_fragmented = request(5180, "/api/status")
        assert after_fragmented["leftY"] > after_malformed["leftY"], (after_malformed, after_fragmented)

        for _ in range(25):
            send_binary(host_ws, controls[-1])
            send_binary(passive_ws, controls[0])
            send_binary(guest_ws, controls[1])
            time.sleep(0.04)
        moved = request(5180, "/api/status")
        assert moved["leftY"] < before["leftY"], (before, moved)
        assert moved["rightY"] > before["rightY"], (before, moved)
        synced = request(5181, "/api/status")
        assert abs(synced["leftY"] - moved["leftY"]) < 0.1, (moved, synced)

    with websocket(5180) as closing_ws:
        send_frame(closing_ws, 0x8, (1000).to_bytes(2, "big"))
        expect_prompt_close(closing_ws)

    request(5181, "/api/restart", {})
    wait_until("guest restart request", lambda: request(5180, "/api/status")["phase"] == "countdown")
    left_guest = request(5181, "/api/leave", {})
    assert left_guest["role"] == "none" and left_guest["connection"] == "idle", left_guest
    assert left_guest["phase"] == "waiting", left_guest
    wait_until("host observes guest leave", lambda: request(5180, "/api/status")["connection"] == "waiting")
    assert request(5180, "/api/status").get("pingMs") is None
    rejoining = request(5181, "/api/join", {"address": "127.0.0.1", "port": 47888})
    assert rejoining["role"] == "guest" and rejoining["connection"] == "connecting", rejoining
    wait_until("UDP reconnect after receiver cancellation", lambda:
               request(5180, "/api/status")["connection"] == "connected"
               and request(5181, "/api/status")["connection"] == "connected")
    rejoined_tick = request(5181, "/api/status")["tick"]
    wait_until("state sync after reconnect", lambda:
               request(5181, "/api/status")["tick"] >= rejoined_tick + 10)
    left_host = request(5180, "/api/leave", {})
    assert left_host["role"] == "none" and left_host["connection"] == "idle", left_host
    assert left_host["phase"] == "waiting", left_host
    request(5181, "/api/leave", {})
    request(5180, "/api/host", {"port": 47888})
    request(5181, "/api/join", {"address": "127.0.0.1", "port": 47888})
    wait_until("connected before guest SIGTERM", lambda:
               request(5180, "/api/status")["connection"] == "connected"
               and request(5181, "/api/status")["connection"] == "connected")
    terminate_connected_process(processes[1], 5181, 5180, "waiting", "guest")

    processes[1] = launch(5181, logs[1])
    wait_until("restarted guest web server", lambda: request(5181, "/api/status"))
    request(5181, "/api/join", {"address": "127.0.0.1", "port": 47888})
    wait_until("connected before host SIGTERM", lambda:
               request(5180, "/api/status")["connection"] == "connected"
               and request(5181, "/api/status")["connection"] == "connected")
    terminate_connected_process(processes[0], 5180, 5181, "connecting", "host")

    print("PASS: static UI, discovery, UDP handshake, ping RTT, oversized UDP datagrams, gameplay, binary MessagePack WebSocket snapshots and controls, malformed and fragmented controls, multiple tabs, close handshake, state sync, restart, leave, reconnect, and graceful host/guest shutdown")
finally:
    for process in processes:
        if process.poll() is None:
            process.terminate()
    for process in processes:
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
    for log in logs:
        log.close()
