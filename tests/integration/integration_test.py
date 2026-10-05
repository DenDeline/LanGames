import base64
import json
import math
import os
import re
import socket
import subprocess
import threading
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
    "recentEvents",
)
ROLES = ("none", "host", "guest")
CONNECTIONS = ("idle", "waiting", "connecting", "connected")
PHASES = ("waiting", "countdown", "playing", "gameover")
EVENT_KINDS = ("serve", "paddle", "wall", "goal", "match")


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
    assert snapshot["version"] == 2, snapshot
    snapshot["role"] = ROLES[snapshot["role"]]
    snapshot["connection"] = CONNECTIONS[snapshot["connection"]]
    snapshot["phase"] = PHASES[snapshot["phase"]]
    assert isinstance(snapshot["localAddresses"], list), snapshot
    assert isinstance(snapshot["message"], str), snapshot
    events = snapshot["recentEvents"]
    assert isinstance(events, list) and len(events) <= 12, snapshot
    event_ids = set()
    previous_tick = -1
    for event in events:
        assert isinstance(event, list) and len(event) == 5, event
        event_id, kind, tick, x, y = event
        assert isinstance(event_id, str) and 0 < len(event_id) <= 64, event
        assert event_id not in event_ids, event
        event_ids.add(event_id)
        assert type(kind) is int and 1 <= kind <= len(EVENT_KINDS), event
        assert type(tick) is int and previous_tick <= tick <= snapshot["tick"], event
        assert all(type(value) in (int, float) and math.isfinite(value) and 0 <= value <= 1
                   for value in (x, y)), event
        previous_tick = tick
    return snapshot


def wait_for_ws_event(conn, kind, event_id=None, seconds=3):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        conn.settimeout(max(0.01, deadline - time.monotonic()))
        try:
            snapshot = decode_snapshot(recv_frame(conn))
        except socket.timeout:
            break
        for event in snapshot["recentEvents"]:
            if event[1] == kind and (event_id is None or event[0] == event_id):
                return snapshot, event
    raise AssertionError(f"WebSocket event {EVENT_KINDS[kind - 1]} was not received")


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


class UdpRelay:
    """Relay one local guest to a host, with a controllable host-to-guest pause."""

    def __init__(self, host_port):
        self.host_port = host_port
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.socket.bind(("127.0.0.1", 0))
        self.socket.settimeout(0.05)
        self.port = self.socket.getsockname()[1]
        self.guest_address = None
        self.pause_host_packets = threading.Event()
        self.pause_guest_inputs = threading.Event()
        self.stop = threading.Event()
        self.dropped_state_packets = 0
        self.forwarded_state_packets = 0
        self.buffered_input_count = 0
        self.latest_buffered_input = None
        self.input_lock = threading.Lock()
        self.thread = threading.Thread(target=self._relay, daemon=True)

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *_):
        self.stop.set()
        self.socket.close()
        self.thread.join(timeout=1)
        assert not self.thread.is_alive(), "UDP relay did not stop"

    def release_latest_input(self):
        with self.input_lock:
            packet = self.latest_buffered_input
            self.latest_buffered_input = None
        if packet is not None:
            self.socket.sendto(packet, ("127.0.0.1", self.host_port))
        return packet

    def _relay(self):
        while not self.stop.is_set():
            try:
                packet, address = self.socket.recvfrom(65535)
                if address == ("127.0.0.1", self.host_port):
                    if self.pause_host_packets.is_set():
                        if packet.startswith(b"\x92\x05"):  # StatePacket union tag.
                            self.dropped_state_packets += 1
                    elif self.guest_address is not None:
                        self.socket.sendto(packet, self.guest_address)
                        if packet.startswith(b"\x92\x05"):
                            self.forwarded_state_packets += 1
                else:
                    self.guest_address = address
                    if self.pause_guest_inputs.is_set() and packet.startswith(b"\x92\x04"):
                        with self.input_lock:
                            self.latest_buffered_input = packet
                            self.buffered_input_count += 1
                    else:
                        self.socket.sendto(packet, ("127.0.0.1", self.host_port))
            except socket.timeout:
                pass
            except OSError:
                if not self.stop.is_set():
                    raise


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
        snapshot, host_serve = wait_for_ws_event(host_ws, 1)
        assert snapshot["role"] == "host" and snapshot["connection"] == "connected", snapshot
        assert "tick" in snapshot and "leftY" in snapshot and "rightY" in snapshot, snapshot
        guest_snapshot, guest_serve = wait_for_ws_event(guest_ws, 1, host_serve[0])
        assert guest_snapshot["role"] == "guest" and guest_snapshot["connection"] == "connected", guest_snapshot
        assert guest_serve == host_serve, (host_serve, guest_serve)

        send_text(host_ws, '{"axis":1}')
        send_binary(host_ws, b"\x90")  # Wrong array shape.
        send_binary(host_ws, b"\x92\x01\x01")  # Wrong protocol version.
        send_binary(host_ws, b"\x92\x02\xa2up")  # Non-numeric axis.
        send_binary(host_ws, b"\x92\x02\x02")  # Axis outside -1..1.
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
    with UdpRelay(47888) as relay:
        rejoining = request(5181, "/api/join", {"address": "127.0.0.1", "port": relay.port})
        assert rejoining["role"] == "guest" and rejoining["connection"] == "connecting", rejoining
        wait_until("UDP reconnect after receiver cancellation", lambda:
                   request(5180, "/api/status")["connection"] == "connected"
                   and request(5181, "/api/status")["connection"] == "connected")
        rejoined_tick = request(5181, "/api/status")["tick"]
        wait_until("state sync after reconnect", lambda:
                   request(5181, "/api/status")["tick"] >= rejoined_tick + 10)
        wait_until("rejoined gameplay", lambda:
                   request(5181, "/api/status")["phase"] == "playing")

        # Delay only guest inputs. Releasing the latest packet must make the
        # host replay the earlier ticks carried in its redundant input history.
        with websocket(5181) as rollback_ws:
            assert decode_snapshot(recv_frame(rollback_ws))["connection"] == "connected"
            before_rollback = request(5180, "/api/status")
            guest_tick = request(5181, "/api/status")["tick"]
            axis = 1 if before_rollback["rightY"] < 0.5 else -1
            relay.pause_guest_inputs.set()
            deadline = time.monotonic() + 0.35
            target_tick = guest_tick + 6
            while guest_tick < target_tick and time.monotonic() < deadline:
                send_binary(rollback_ws, controls[axis])
                time.sleep(0.012)
                guest_tick = request(5181, "/api/status")["tick"]
            assert guest_tick >= target_tick, guest_tick
            assert relay.buffered_input_count >= 4, relay.buffered_input_count
            before_release = request(5180, "/api/status")
            assert abs(before_release["rightY"] - before_rollback["rightY"]) < 0.01, (
                before_rollback, before_release)
            assert relay.release_latest_input() is not None

            def rollback_visible():
                current = request(5180, "/api/status")
                elapsed_ticks = current["tick"] - before_release["tick"]
                ordinary_movement = elapsed_ticks * 0.85 / 60
                movement = (current["rightY"] - before_release["rightY"]) * axis
                return current if movement > ordinary_movement + 0.025 else None

            wait_until("host replays delayed guest paddle input", rollback_visible, seconds=2)
            relay.pause_guest_inputs.clear()

        restarted = request(5180, "/api/restart", {})
        wait_until("restarted gameplay before guest prediction", lambda:
                   request(5180, "/api/status")["phase"] == "playing"
                   and request(5181, "/api/status")["phase"] == "playing"
                   and request(5181, "/api/status")["roundId"] == restarted["roundId"])

        # A predictive guest keeps moving its paddle and advancing simulation
        # ticks while authoritative states are briefly held by the relay.
        with websocket(5181) as predictive_ws:
            assert decode_snapshot(recv_frame(predictive_ws))["connection"] == "connected"
            before_pause = request(5181, "/api/status")
            axis = 1 if before_pause["rightY"] < 0.5 else -1
            relay.pause_host_packets.set()
            for _ in range(16):
                send_binary(predictive_ws, controls[axis])
                time.sleep(0.025)
            during_pause = request(5181, "/api/status")
            assert relay.dropped_state_packets >= 3, relay.dropped_state_packets
            assert during_pause["tick"] >= before_pause["tick"] + 10, (before_pause, during_pause)
            assert (during_pause["rightY"] - before_pause["rightY"]) * axis > 0.05, (
                before_pause, during_pause)
            previously_forwarded = relay.forwarded_state_packets
            relay.pause_host_packets.clear()

        wait_until("guest reconciles after paused host states", lambda:
                   relay.forwarded_state_packets >= previously_forwarded + 3
                   and abs(request(5180, "/api/status")["rightY"]
                       - request(5181, "/api/status")["rightY"]) < 0.1)
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

    print("PASS: static UI, discovery, UDP handshake, ping RTT, oversized UDP datagrams, gameplay, binary MessagePack WebSocket snapshots, shared host/guest game events, controls, malformed and fragmented controls, multiple tabs, close handshake, state sync, restart, leave, reconnect, host rollback of delayed inputs, guest prediction during paused host states, and graceful host/guest shutdown")
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
