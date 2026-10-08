import base64
from contextlib import ExitStack
from concurrent.futures import ThreadPoolExecutor
import json
import math
import os
import re
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "src" / "LanPong"
DLL = PROJECT / "bin" / "Release" / "net10.0" / "LanPong.dll"
TEST_BINARY = Path(os.environ["LANPONG_TEST_BINARY"]).resolve() if os.environ.get("LANPONG_TEST_BINARY") else None
MSGPACK_HELPER = ROOT / "tests" / "integration" / "msgpack_interop.mjs"
SNAPSHOT_FIELDS = (
    "version", "role", "connection", "message", "udpPort", "localAddresses",
    "peerAddress", "leftY", "rightY", "ballX", "ballY", "ballVx", "ballVy",
    "leftScore", "rightScore", "phase", "countdown", "tick", "roundId", "pingMs",
    "recentEvents", "localNickname", "peerNickname", "opponentMode",
    "requestedBotId", "requestedBotName", "effectiveBotId", "effectiveBotName",
    "opponentFallbackActive", "botFallbackReason", "localSide", "matchId",
    "sourceId", "snapshotSequence", "canRematch",
)
ROLES = ("none", "host", "guest")
OPPONENT_MODES = ("none", "lan", "bot")
CONNECTIONS = (
    "idle", "waiting", "connecting", "connected", "incomingChallenge", "awaitingAcceptance",
    "searching",
)
PHASES = ("waiting", "countdown", "playing", "gameover")
EVENT_KINDS = ("serve", "paddle", "wall", "goal", "match")
CHALLENGE_ID = "0123456789abcdef0123456789abcdef"
OTHER_CHALLENGE_ID = "fedcba9876543210fedcba9876543210"
CANCELED_CHALLENGE_ID = "00112233445566778899aabbccddeeff"
HOST_NICKNAME = "Хозяин"
GUEST_NICKNAME = "Гость"
RAW_NICKNAME = "RawGuest"
BOT_NAMES = {"lada": "Лада", "iskra": "Искра", "vektor": "Вектор"}
LONG_BOT_NAME = "Настроенный тренировочный соперник ".ljust(64, "я")
CATALOG_BOT_FIELDS = {
    "id", "name", "description", "style", "difficulty", "category", "order", "glyph",
    "enabled", "fallbackBotId", "availability", "availabilityReason", "canPlay",
}


def wire_challenge_packet(tag, request_id, nickname=None):
    # NativeGuidResolver writes a 16-byte MessagePack binary value in .NET Guid byte order.
    assert len(request_id) == 32 and all(char in "0123456789abcdef" for char in request_id)
    packet = (b"\x92" + bytes([tag]) + (b"\x93" if nickname is not None else b"\x92")
              + b"\x09\xc4\x10" + uuid.UUID(hex=request_id).bytes_le)
    if nickname is not None:
        encoded = nickname.encode("utf-8")
        assert 1 <= len(encoded) <= 31
        packet += bytes([0xa0 | len(encoded)]) + encoded
    return packet


HELLO_PACKET = wire_challenge_packet(2, CHALLENGE_ID, RAW_NICKNAME)


def host_payload():
    return {"port": 47888, "nickname": HOST_NICKNAME}


def quick_payload(nickname):
    return {"nickname": nickname}


def join_payload(address="127.0.0.1", port=47888):
    return {"address": address, "port": port, "nickname": GUEST_NICKNAME}


def msgpack_helper(operation, payload=b""):
    result = subprocess.run(
        ["node", str(MSGPACK_HELPER), operation],
        input=base64.b64encode(payload), capture_output=True, check=True, cwd=ROOT,
        timeout=5,
    )
    return json.loads(result.stdout)


def expect_challenge_reply(sock, tag, request_id):
    sock.settimeout(2)
    packet, _ = sock.recvfrom(1201)
    payload = [9, list(uuid.UUID(hex=request_id).bytes_le)]
    if tag == 10:
        payload.append(HOST_NICKNAME)
    assert msgpack_helper("decode", packet) == [tag, payload], packet


def msgpack_helper_bytes(value):
    return base64.b64decode(msgpack_helper("encode", json.dumps(value).encode()))


def control_packets(snapshot):
    return {int(axis): base64.b64decode(encoded) for axis, encoded in
            msgpack_helper("encode-controls", json.dumps({"roundId": snapshot["roundId"], "matchId": snapshot["matchId"]}).encode()).items()}


def decode_snapshot(frame):
    assert frame is not None and frame[0] == 0x2, frame
    values = msgpack_helper("decode", frame[1])
    assert isinstance(values, list) and len(values) == len(SNAPSHOT_FIELDS), values
    snapshot = dict(zip(SNAPSHOT_FIELDS, values))
    assert snapshot["version"] == 9, snapshot
    snapshot["role"] = ROLES[snapshot["role"]]
    side = snapshot["localSide"]
    assert side is None or type(side) is int and side in (1, 2), snapshot
    snapshot["localSide"] = None if side is None else ("left" if side == 1 else "right")
    snapshot["opponentMode"] = OPPONENT_MODES[snapshot["opponentMode"]]
    snapshot["connection"] = CONNECTIONS[snapshot["connection"]]
    snapshot["phase"] = PHASES[snapshot["phase"]]
    assert isinstance(snapshot["localAddresses"], list), snapshot
    assert isinstance(snapshot["message"], str), snapshot
    validate_snapshot_identity(snapshot)
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


def validate_snapshot_identity(snapshot):
    assert snapshot["version"] == 9 and "requestedOpponentMode" not in snapshot, snapshot
    assert snapshot["opponentMode"] in OPPONENT_MODES, snapshot
    assert snapshot["localSide"] in (None, "left", "right"), snapshot
    assert (snapshot["localSide"] is not None) == (snapshot["connection"] == "connected"), snapshot
    assert (snapshot["matchId"] is not None) == (snapshot["localSide"] is not None), snapshot
    assert snapshot["matchId"] is None or re.fullmatch(r"[0-9a-f]{32}", snapshot["matchId"]) and snapshot["matchId"] != "0" * 32, snapshot
    assert isinstance(snapshot["sourceId"], str) and re.fullmatch(r"[0-9a-f]{32}", snapshot["sourceId"]) and snapshot["sourceId"] != "0" * 32, snapshot
    assert type(snapshot["snapshotSequence"]) is int and 0 < snapshot["snapshotSequence"] <= 9007199254740991, snapshot
    assert type(snapshot["canRematch"]) is bool, snapshot
    assert not snapshot["canRematch"] or snapshot["connection"] == "connected" and snapshot["phase"] == "gameover" and snapshot["localSide"] is not None, snapshot
    assert isinstance(snapshot["localNickname"], str) and 0 < len(snapshot["localNickname"]) <= 24, snapshot
    assert (snapshot["peerNickname"] is None or
            isinstance(snapshot["peerNickname"], str) and 0 < len(snapshot["peerNickname"]) <= 24), snapshot
    assert type(snapshot["opponentFallbackActive"]) is bool, snapshot
    for field in ("requestedBotId", "effectiveBotId"):
        value = snapshot[field]
        assert value is None or isinstance(value, str) and re.fullmatch(r"[a-z][a-z0-9]*(?:-[a-z0-9]+)*", value), snapshot
    for field in ("requestedBotName", "effectiveBotName"):
        value = snapshot[field]
        assert value is None or isinstance(value, str) and 0 < len(value) <= 64, snapshot
    reason = snapshot["botFallbackReason"]
    assert reason is None or isinstance(reason, str) and reason.strip(), snapshot
    if snapshot["opponentMode"] == "bot":
        assert snapshot["peerNickname"] is None, snapshot
        assert all(snapshot[field] is not None for field in
                   ("requestedBotId", "requestedBotName", "effectiveBotId", "effectiveBotName")), snapshot
        assert snapshot["opponentFallbackActive"] is (snapshot["requestedBotId"] != snapshot["effectiveBotId"]), snapshot
        if snapshot["opponentFallbackActive"]:
            assert reason is not None and re.search("[А-Яа-яЁё]", reason), snapshot
    else:
        assert all(snapshot[field] is None for field in
                   ("requestedBotId", "requestedBotName", "effectiveBotId", "effectiveBotName", "botFallbackReason")), snapshot
        assert snapshot["opponentFallbackActive"] is False, snapshot


def expect_opponent(snapshot, mode, requested_id=None, effective_id=None, fallback=False,
                    requested_name=None, effective_name=None):
    validate_snapshot_identity(snapshot)
    assert snapshot["opponentMode"] == mode, snapshot
    assert snapshot["requestedBotId"] == requested_id, snapshot
    assert snapshot["effectiveBotId"] == effective_id, snapshot
    assert snapshot["opponentFallbackActive"] is fallback, snapshot
    if requested_id is not None:
        assert snapshot["requestedBotName"] == (requested_name or BOT_NAMES[requested_id]), snapshot
        assert snapshot["effectiveBotName"] == (effective_name or BOT_NAMES[effective_id]), snapshot
    if not fallback:
        assert snapshot["botFallbackReason"] is None, snapshot


def expect_identity_parity(http_snapshot, ws_snapshot):
    fields = ("opponentMode", "requestedBotId", "requestedBotName", "effectiveBotId",
              "effectiveBotName", "opponentFallbackActive", "botFallbackReason", "peerNickname", "localSide", "matchId", "sourceId")
    assert {field: http_snapshot[field] for field in fields} == {
        field: ws_snapshot[field] for field in fields}, (http_snapshot, ws_snapshot)


def expect_catalog(port, expected_ids=("lada", "iskra", "vektor"), default_id="lada"):
    catalog = request(port, "/api/bots")
    assert set(catalog) == {"version", "defaultBotId", "bots"}, catalog
    assert catalog["version"] == 9 and catalog["defaultBotId"] == default_id, catalog
    bots = catalog["bots"]
    assert isinstance(bots, list) and [bot["id"] for bot in bots] == list(expected_ids), catalog
    assert bots == sorted(bots, key=lambda bot: (bot["order"], bot["id"])), catalog
    for bot in bots:
        # An exact public shape excludes model paths, checksums, strategy settings, and native details.
        assert set(bot) == CATALOG_BOT_FIELDS, bot
        assert isinstance(bot["name"], str) and 0 < len(bot["name"]) <= 64, bot
        assert all(isinstance(bot[field], str) and bot[field] for field in
                   ("description", "style", "difficulty", "category")), bot
        assert type(bot["order"]) is int and type(bot["enabled"]) is bool and type(bot["canPlay"]) is bool, bot
        assert bot["glyph"] is None or isinstance(bot["glyph"], str), bot
        assert bot["availability"] in ("ready", "notChecked", "unavailable", "disabled"), bot
        assert bot["availabilityReason"] is None or isinstance(bot["availabilityReason"], str), bot
        if bot["availability"] == "disabled":
            assert not bot["enabled"] and not bot["canPlay"], bot
    return {bot["id"]: bot for bot in bots}


def finish_match(ports, seconds=45):
    """Produce a genuine seventh goal through ordinary current-match controls."""
    initial = {port: request(port, "/api/status") for port in ports}
    match = next(iter(initial.values()))
    assert all(state["matchId"] == match["matchId"] and state["roundId"] == match["roundId"]
               for state in initial.values()), initial
    assert any(state["localSide"] == "right" for state in initial.values()), initial
    deadline = time.monotonic() + seconds
    current = initial
    with ExitStack() as stack:
        sockets = {port: stack.enter_context(websocket(port)) for port in ports}
        frames = {port: control_packets(state) for port, state in initial.items()}
        while time.monotonic() < deadline:
            for port, conn in sockets.items():
                send_binary(conn, frames[port][-1 if initial[port]["localSide"] == "right" else 0])
            current = {port: request(port, "/api/status") for port in ports}
            assert all(state["matchId"] == match["matchId"] and state["roundId"] == match["roundId"]
                       and state["localSide"] == initial[port]["localSide"]
                       for port, state in current.items()), current
            if all(state["phase"] == "gameover" and state["canRematch"] for state in current.values()):
                # Guest simulation may predict the seventh goal before the host packet arrives.
                # Its HTTP events contain only confirmed host events, so require the winning goal.
                host = next(state for state in current.values() if state["role"] == "host")
                winning_goal = next(event["id"] for event in reversed(host["recentEvents"]) if event["kind"] == 4)
                if not all(any(event["id"] == winning_goal for event in state["recentEvents"])
                           for state in current.values() if state["role"] == "guest"):
                    time.sleep(0.04)
                    continue
                assert all(max(state["leftScore"], state["rightScore"]) == 7 for state in current.values()), current
                assert len({(state["leftScore"], state["rightScore"]) for state in current.values()}) == 1, current
                break
            time.sleep(0.04)
        else:
            raise AssertionError(f"Genuine GameOver timed out in {seconds}s; states={current}")
    retained = {port: request(port, "/api/status") for port in ports}
    for port, state in retained.items():
        assert state["localSide"] == current[port]["localSide"], retained
        assert (state["leftScore"], state["rightScore"]) == (current[port]["leftScore"], current[port]["rightScore"]), retained
    print(f"PASS: genuine finished round retains physical score/side orientation: {[(state['role'], state['localSide'], state['leftScore'], state['rightScore']) for state in retained.values()]}")
    return retained


def rematch_finished(ports, requester):
    finished = finish_match(ports)
    old = finished[requester]
    reply = request(requester, "/api/restart", {"matchId": old["matchId"], "expectedRoundId": old["roundId"]})
    # Guest acceptance can be pending; only host-authoritative advancement confirms the swap.
    assert reply["roundId"] in (old["roundId"], old["roundId"] + 1), reply
    wait_until("authoritative finished rematch reaches every player", lambda:
               all(request(port, "/api/status")["roundId"] == old["roundId"] + 1 for port in ports))
    restarted = {port: request(port, "/api/status") for port in ports}
    for port, state in restarted.items():
        previous = finished[port]
        assert state["matchId"] == previous["matchId"] and state["roundId"] == previous["roundId"] + 1, state
        assert state["localSide"] == ("right" if previous["localSide"] == "left" else "left"), state
        assert state["leftScore"] == state["rightScore"] == 0 and state["phase"] == "countdown", state
        assert state["canRematch"] is False, state
        expect_bad_request(port, "/api/restart", {"matchId": previous["matchId"], "expectedRoundId": previous["roundId"]})
        old_frames = control_packets(previous)
        with websocket(port) as conn:
            expect_identity_parity(state, decode_snapshot(recv_frame(conn)))
            for _ in range(4):
                send_binary(conn, old_frames[1])
                time.sleep(0.03)
            checked = request(port, "/api/status")
            assert abs(checked[state["localSide"] + "Y"] - 0.5) < 0.001, checked
            current_frames = control_packets(state)
            for _ in range(4):
                send_binary(conn, current_frames[-1])
                time.sleep(0.03)
            moved = request(port, "/api/status")
            assert moved[state["localSide"] + "Y"] < 0.49, moved
            send_binary(conn, current_frames[0])
    print("PASS: finished rematch swaps once, preserves match identity, rejects stale expected-round controls/requests and routes the new paddle")
    return restarted


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
        value = json.load(response)
        if isinstance(value, dict) and "opponentMode" in value:
            validate_snapshot_identity(value)
        return value


def expect_bad_request(port, path, payload):
    try:
        request(port, path, payload)
    except urllib.error.HTTPError as error:
        assert error.code == 400, (path, payload, error.code)
        raw = error.read()
        body = json.loads(raw) if raw else {}
        if not body: return body
        assert set(body) == {"error"} and isinstance(body["error"], str) and body["error"], body
        return body
    else:
        raise AssertionError(f"Expected HTTP 400 for {path}: {payload}")


def discover_waiting_host():
    return [
        item for item in request(5181, "/api/discover")["hosts"]
        if item.get("port") == 47888 and item.get("address")
    ]


def wait_for_mdns_host_absence():
    missing_queries = 0

    def absent_twice():
        nonlocal missing_queries
        missing_queries = missing_queries + 1 if not discover_waiting_host() else 0
        return missing_queries >= 2

    # Two discovery windows avoid mistaking one lost multicast response for a goodbye.
    wait_until("connected host disappears from mDNS", absent_twice, seconds=10)


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


def wait_for_quick_lobby(port, nickname):
    def waiting():
        snapshot = request(port, "/api/status")
        return snapshot if (snapshot["role"] == "host" and
                            snapshot["connection"] == "waiting" and
                            snapshot["localNickname"] == nickname) else None

    return wait_until(f"Quick Game lobby for {nickname}", waiting, seconds=12)


def wait_for_challenge():
    def pending():
        host = request(5180, "/api/status")
        guest = request(5181, "/api/status")
        return (host, guest) if (host["connection"] == "incomingChallenge"
                                 and guest["connection"] == "awaitingAcceptance") else None

    return wait_until("incoming challenge and outgoing pending request", pending)


def accept_challenge():
    wait_for_challenge()
    request(5180, "/api/accept", {})
    wait_until("challenge accepted and UDP handshake complete", lambda:
               request(5180, "/api/status")["connection"] == "connected"
               and request(5181, "/api/status")["connection"] == "connected")


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


def launch(port, log, extra_env=None):
    return subprocess.Popen(
        [str(TEST_BINARY)] if TEST_BINARY else ["dotnet", str(DLL)],
        cwd=TEST_BINARY.parent if TEST_BINARY else PROJECT, stdout=log, stderr=subprocess.STDOUT,
        env={**os.environ, "ASPNETCORE_URLS": f"http://127.0.0.1:{port}",
             **(extra_env or {})},
    )


def stop_process(process):
    if process.poll() is None:
        process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def terminate_connected_process(process, port, remote_port, remote_connection, remote_role, role):
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

        def remote_left_match():
            snapshot = request(remote_port, "/api/status")
            return snapshot if (snapshot["connection"] == remote_connection
                                and snapshot["role"] == remote_role) else None

        wait_until(f"{role} shutdown Bye reaches remote peer", remote_left_match,
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


def has_ipv6_loopback():
    try:
        with socket.socket(socket.AF_INET6, socket.SOCK_DGRAM) as probe:
            probe.bind(("::1", 0))
            return True
    except OSError:
        return False


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
        self.drop_welcome = threading.Event()
        self.pause_guest_inputs = threading.Event()
        self.stop = threading.Event()
        self.dropped_state_packets = 0
        self.dropped_welcome_packets = 0
        self.forwarded_state_packets = 0
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

    def buffered_input(self):
        with self.input_lock:
            return self.latest_buffered_input

    def release_input(self, packet):
        with self.input_lock:
            if self.latest_buffered_input == packet:
                self.latest_buffered_input = None
        self.socket.sendto(packet, ("127.0.0.1", self.host_port))

    def _relay(self):
        while not self.stop.is_set():
            try:
                packet, address = self.socket.recvfrom(65535)
                if address == ("127.0.0.1", self.host_port):
                    if self.pause_host_packets.is_set():
                        if packet.startswith(b"\x92\x05"):  # StatePacket union tag.
                            self.dropped_state_packets += 1
                    elif self.drop_welcome.is_set() and packet.startswith(b"\x92\x03"):
                        self.dropped_welcome_packets += 1
                    elif self.guest_address is not None:
                        self.socket.sendto(packet, self.guest_address)
                        if packet.startswith(b"\x92\x05"):
                            self.forwarded_state_packets += 1
                else:
                    self.guest_address = address
                    if self.pause_guest_inputs.is_set() and packet.startswith(b"\x92\x04"):
                        with self.input_lock:
                            self.latest_buffered_input = packet
                    else:
                        self.socket.sendto(packet, ("127.0.0.1", self.host_port))
            except socket.timeout:
                pass
            except OSError:
                if not self.stop.is_set():
                    raise


def check_configured_tracker_process(log_dir):
    port = 5183
    bot_id = "catalog-tracker"
    bot_name = '<b>Настроенный соперник</b> «Каталог»'.ljust(64, "я")
    assert 24 < len(bot_name) <= 64
    metadata = {
        "id": bot_id,
        "name": bot_name,
        "description": "<img src=x> Соперник, добавленный только через конфигурацию.",
        "style": "Широкая зона покоя, спокойная позиция",
        "difficulty": "Настраиваемый",
        "category": "<i>Каталог конфигурации</i>",
        "order": 20,
        "glyph": "<>",
        "enabled": True,
        "fallbackBotId": None,
        "availability": "ready",
        "availabilityReason": None,
        "canPlay": True,
    }
    # The environment provider merges a fourth array entry into shipped appsettings.
    # Only its dead zone differs from Lada, making configured behavior observable.
    configured_env = {
        "Bots__DefaultBotId": bot_id,
        **{f"Bots__Entries__3__{field[0].upper() + field[1:]}":
           str(metadata[field]).lower() if isinstance(metadata[field], bool) else str(metadata[field])
           for field in ("id", "name", "description", "style", "difficulty", "category", "order", "glyph", "enabled")},
        "Bots__Entries__3__StrategyId": "tracker",
        "Bots__Entries__3__Tracker__ObservationIntervalTicks": "9",
        "Bots__Entries__3__Tracker__ObservationActivationX": "0.72",
        "Bots__Entries__3__Tracker__LookAheadSeconds": "0.25",
        "Bots__Entries__3__Tracker__TargetDeadZone": "0.5",
    }
    configured_log = open(log_dir / "pong-extra-tracker.log", "w")
    configured_process = launch(port, configured_log, configured_env)
    try:
        wait_until("configured tracker web server", lambda: request(port, "/api/status"))
        # A tied display order also proves the ordinal-ID secondary ordering.
        expected_ids = ("lada", bot_id, "iskra", "vektor")
        catalog = expect_catalog(port, expected_ids, bot_id)
        assert catalog[bot_id] == metadata, catalog[bot_id]
        assert catalog["vektor"]["availability"] == "notChecked", catalog

        baseline = request(port, "/api/local-opponent", {"nickname": "Baseline", "botId": "lada", "side": "left"})

        def baseline_moves():
            state = request(port, "/api/status")
            return state if state["phase"] == "playing" and state["rightY"] > 0.52 else None

        moving = wait_until("configured-process baseline tracker moves toward serve", baseline_moves)
        expect_opponent(moving, "bot", "lada", "lada")
        assert moving["tick"] > baseline["tick"], moving
        expect_opponent(request(port, "/api/leave", {}), "none")

        selected = request(port, "/api/local-opponent", {"nickname": "CatalogPlayer", "botId": bot_id, "side": "left"})
        expect_opponent(selected, "bot", bot_id, bot_id, requested_name=bot_name, effective_name=bot_name)
        assert selected["phase"] == "countdown" and selected["rightY"] == 0.5, selected
        with websocket(port) as selected_ws:
            expect_identity_parity(selected, decode_snapshot(recv_frame(selected_ws)))
            controls = control_packets(selected)
            for _ in range(8):
                send_binary(selected_ws, controls[-1])
                time.sleep(0.03)

            approach_seen = False
            live_samples = 0

            def configured_stays_centered():
                nonlocal approach_seen, live_samples
                state = request(port, "/api/status")
                assert state["rightY"] == 0.5, state
                if state["phase"] == "playing":
                    live_samples += 1
                    approach_seen |= state["ballVx"] > 0 and state["ballX"] >= 0.72 and abs(state["ballY"] - 0.5) > 0.02
                return state if approach_seen and state["tick"] >= selected["tick"] + 180 else None

            stationary = wait_until("configured dead zone holds paddle through live approach", configured_stays_centered)
            expect_identity_parity(selected, stationary)
            assert live_samples >= 4 and stationary["leftY"] < 0.48, stationary

        expect_bad_request(port, "/api/restart", {"matchId": selected["matchId"], "expectedRoundId": selected["roundId"]})
        unchanged = request(port, "/api/status")
        assert unchanged["roundId"] == selected["roundId"] and unchanged["localSide"] == selected["localSide"], unchanged
        departed = request(port, "/api/leave", {})
        expect_opponent(departed, "none")
        assert departed["role"] == "none" and departed["connection"] == "idle", departed
        with websocket(port) as departed_ws:
            expect_identity_parity(departed, decode_snapshot(recv_frame(departed_ws)))
        assert expect_catalog(port, expected_ids, bot_id)[bot_id] == metadata

        # The same configured process must release the bot and still host a LAN round.
        expect_opponent(request(5181, "/api/status"), "none")
        waiting = request(port, "/api/host", {"port": 47889, "nickname": "ConfiguredHost"})
        expect_opponent(waiting, "lan")
        request(5181, "/api/join", join_payload(port=47889))
        wait_until("configured host receives LAN challenge", lambda:
                   request(port, "/api/status")["connection"] == "incomingChallenge"
                   and request(5181, "/api/status")["connection"] == "awaitingAcceptance")
        request(port, "/api/accept", {})
        wait_until("configured-process LAN gameplay", lambda:
                   request(port, "/api/status")["phase"] == "playing"
                   and request(5181, "/api/status")["phase"] == "playing")
        lan_host = request(port, "/api/status")
        lan_guest = request(5181, "/api/status")
        expect_opponent(lan_host, "lan")
        expect_opponent(lan_guest, "lan")
        assert lan_host["peerNickname"] == GUEST_NICKNAME and lan_guest["peerNickname"] == "ConfiguredHost"
        with websocket(port) as host_ws, websocket(5181) as guest_ws:
            expect_identity_parity(lan_host, decode_snapshot(recv_frame(host_ws)))
            expect_identity_parity(lan_guest, decode_snapshot(recv_frame(guest_ws)))
            controls = control_packets(lan_guest)
            guest_field = lan_guest["localSide"] + "Y"
            for _ in range(8):
                send_binary(guest_ws, controls[1])
                time.sleep(0.03)
            wait_until("LAN guest controls configured host physical paddle", lambda:
                       request(port, "/api/status")[guest_field] > lan_host[guest_field] + 0.02)
        expect_opponent(request(5181, "/api/leave", {}), "none")
        wait_until("configured host returns to LAN lobby", lambda:
                   request(port, "/api/status")["connection"] == "waiting")
        expect_opponent(request(port, "/api/leave", {}), "none")
        print("PASS: configuration-only fourth tracker, exact metadata/default/order, HTTP/MessagePack identity, tuned movement, rematch/leave and configured-process LAN round, finished-only rematch rejection")
    finally:
        try:
            stop_process(configured_process)
        finally:
            configured_log.close()


def main():
    if TEST_BINARY is None:
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
        assert b'id="bot-form"' in page and b'id="bot-button"' in page
        assert b'id="bot-catalog"' in page and b'id="bot-profile"' in page
        assert b'id="bot-catalog-status"' in page
        assert b'id="opponent-fallback"' in page
        assert b'id="game-mode"' in page and b'<select' in page
        assert b'id="bot-picker"' in page and b'<dialog' in page
        assert b'id="bot-summary"' in page and b'id="bot-picker-open"' in page
        assets = re.findall(rb'(?:src|href)="(/assets/[^"]+)"', page)
        assert len(assets) >= 2, assets
        for asset in assets:
            with urllib.request.urlopen(f"http://127.0.0.1:5180{asset.decode()}") as response:
                assert response.status == 200 and response.read(), asset

        for port in (5180, 5181):
            idle = request(port, "/api/status")
            assert idle["role"] == "none" and idle["connection"] == "idle", idle
            expect_opponent(idle, "none")
            assert idle["phase"] == "waiting", idle
            catalog = expect_catalog(port)
            assert catalog["lada"]["availability"] == catalog["iskra"]["availability"] == "ready", catalog
            assert catalog["vektor"]["availability"] == "notChecked", catalog
            assert all(bot["canPlay"] for bot in catalog.values()), catalog
            assert expect_catalog(port)["vektor"]["availability"] == "notChecked", catalog

        for nickname in (None, "", "   "):
            expect_bad_request(5180, "/api/host", {"port": 47888, "nickname": nickname})
            expect_bad_request(5181, "/api/join", join_payload() | {"nickname": nickname})
        expect_bad_request(5180, "/api/host", {"port": 47888})
        expect_bad_request(5181, "/api/join", {"address": "127.0.0.1", "port": 47888})
        for nickname in (None, "", "   "):
            expect_bad_request(5180, "/api/quick", {"nickname": nickname})
        expect_bad_request(5180, "/api/quick", {})
        for nickname in (None, "", "   ", "x" * 25, "\x01Name"):
            expect_bad_request(5180, "/api/local-opponent", {"nickname": nickname, "botId": "lada", "side": "left"})
        expect_bad_request(5180, "/api/local-opponent", {})
        expect_bad_request(5180, "/api/local-opponent", {"nickname": "Player"})
        for bot_id in (None, "", "   ", "unknown", "Lada"):
            expect_bad_request(5180, "/api/local-opponent", {"nickname": "Player", "botId": bot_id, "side": "left"})
        for legacy_mode in ("simple", "hard", "lan", "expert"):
            expect_bad_request(5180, "/api/local-opponent", {"nickname": "Player", "mode": legacy_mode})

        missing_side = {"nickname": "MissingSide", "botId": "lada"}
        expect_bad_request(5180, "/api/local-opponent", missing_side)
        for invalid_side in (None, 0, 1, 2, 3, True, "0", "1", "unknown", "left,right", "left,left", "left, random", {}, []):
            expect_bad_request(5180, "/api/local-opponent", missing_side | {"side": invalid_side})
        assert request(5180, "/api/status")["localSide"] is None

        local = request(5180, "/api/local-opponent", {"nickname": "LocalPlayer", "botId": "lada", "side": "left"})
        assert local["role"] == "host" and local["connection"] == "connected", local
        expect_opponent(local, "bot", "lada", "lada")
        assert local["udpPort"] == 0 and local["peerAddress"] is None, local
        assert local["pingMs"] is None and local["peerNickname"] is None, local
        assert local["phase"] == "countdown", local
        expect_bad_request(5180, "/api/local-opponent", {"nickname": "Again", "botId": "vektor", "side": "left"})
        preserved = request(5180, "/api/status")
        expect_opponent(preserved, "bot", "lada", "lada")
        assert preserved["roundId"] == local["roundId"], (local, preserved)
        assert expect_catalog(5180)["vektor"]["availability"] == "notChecked"
        with websocket(5180) as local_ws:
            local_frame = decode_snapshot(recv_frame(local_ws))
            assert local_frame["role"] == "host" and local_frame["connection"] == "connected", local_frame
            expect_opponent(local_frame, "bot", "lada", "lada")
            expect_identity_parity(local, local_frame)
            assert local_frame["udpPort"] == 0 and local_frame["peerAddress"] is None, local_frame
            controls = control_packets(local)
            for _ in range(8):
                send_binary(local_ws, controls[-1])
                time.sleep(0.03)

            def moved_left():
                snapshot = request(5180, "/api/status")
                return snapshot if snapshot["leftY"] < 0.48 else None

            moved = wait_until("local match advances browser-controlled left paddle", moved_left)
            assert moved["tick"] > local["tick"] and moved["rightY"] == 0.5, moved

            def simple_tracks_serve():
                snapshot = request(5180, "/api/status")
                return snapshot if snapshot["phase"] == "playing" and snapshot["rightY"] > 0.52 else None

            wait_until("Simple bot tracks the live serve", simple_tracks_serve)
            expect_bad_request(5180, "/api/restart", {"matchId": local["matchId"], "expectedRoundId": local["roundId"]})
            unchanged = request(5180, "/api/status")
            assert unchanged["roundId"] == local["roundId"] and unchanged["localSide"] == "left", unchanged
        left_local = request(5180, "/api/leave", {})
        assert left_local["role"] == "none" and left_local["connection"] == "idle", left_local
        expect_opponent(left_local, "none")
        assert left_local["phase"] == "waiting" and left_local["tick"] == 0, left_local

        hard = request(5180, "/api/local-opponent", {"nickname": "ModelPlayer", "botId": "vektor", "side": "right"})
        assert hard["role"] == "host" and hard["connection"] == "connected", hard
        expect_opponent(hard, "bot", "vektor", "vektor")
        assert hard["phase"] == "countdown" and hard["peerNickname"] is None, hard
        with websocket(5180) as hard_ws:
            hard_frame = decode_snapshot(recv_frame(hard_ws))
            expect_opponent(hard_frame, "bot", "vektor", "vektor")
            expect_identity_parity(hard, hard_frame)
            controls = control_packets(hard)
            for _ in range(8):
                send_binary(hard_ws, controls[-1])
                time.sleep(0.03)

            def hard_playing():
                state = request(5180, "/api/status")
                return state if (state["phase"] == "playing" and
                                 state["tick"] > hard["tick"] + 60 and
                                 state["rightY"] < 0.48) else None

            played_hard = wait_until("Hard match enters play and accepts browser control", hard_playing)
            expect_opponent(played_hard, "bot", "vektor", "vektor")
        hard_rematch = rematch_finished([5180], 5180)[5180]
        expect_opponent(hard_rematch, "bot", "vektor", "vektor")
        assert hard_rematch["localSide"] == "left", hard_rematch
        hard_left = request(5180, "/api/leave", {})
        expect_opponent(hard_left, "none")
        assert hard_left["role"] == "none" and hard_left["connection"] == "idle", hard_left
        assert expect_catalog(5180)["vektor"]["availability"] == "ready"

        check_configured_tracker_process(log_dir)

        # Each real asset failure stays local to the selected model: the configured tracker
        # remains playable, while discovery and HTTP/WS expose only safe availability/identity.
        missing_model = log_dir / "missing-hard-v1.onnx"
        assert not missing_model.exists(), missing_model
        trained_model = (TEST_BINARY.parent if TEST_BINARY else PROJECT) / "Models" / "hard-v1.onnx"
        damaged_bytes = bytearray(trained_model.read_bytes())
        assert damaged_bytes, trained_model
        damaged_bytes[0] ^= 0xff
        corrupt_model = log_dir / "corrupt-hard-v1.onnx"
        corrupt_model.write_bytes(damaged_bytes)
        assert corrupt_model.read_bytes() != trained_model.read_bytes(), corrupt_model
        for failure_case, model_path, expected_reason in (
            ("missing", missing_model, "Модель бота не найдена."),
            ("corrupt-checksum", corrupt_model, "Модель бота не прошла проверку."),
        ):
            fallback_log = open(log_dir / f"pong-model-{failure_case}.log", "w")
            fallback_process = launch(
                5182, fallback_log,
                {
                    "Bots__Entries__0__Name": LONG_BOT_NAME,
                    "Bots__Entries__0__Order": "40",
                    "Bots__Entries__1__Enabled": "false",
                    "Bots__Entries__1__Order": "5",
                    "Bots__Entries__2__Onnx__ModelPath": str(model_path),
                    "Bots__Entries__2__Order": "40",
                    "Bots__Entries__3__Id": "unusable-model",
                    "Bots__Entries__3__Name": "Без резерва",
                    "Bots__Entries__3__Description": "Модель без настроенного резерва.",
                    "Bots__Entries__3__Style": "Обученная политика",
                    "Bots__Entries__3__Difficulty": "Продвинутый",
                    "Bots__Entries__3__Category": "Контракт",
                    "Bots__Entries__3__Order": "20",
                    "Bots__Entries__3__StrategyId": "onnx",
                    "Bots__Entries__3__Onnx__ModelPath": str(model_path),
                    "Bots__Entries__3__Onnx__ExpectedSha256": "5d5d3cf0910d967cf2d6dc60e8fe0b63f772060178bf6673f6ddc5cdba98ab5a",
                    "Bots__Entries__3__Onnx__InferenceCadenceTicks": "9",
                },
            )
            try:
                wait_until(f"{failure_case} fallback web server", lambda: request(5182, "/api/status"))
                expected_ids = ("iskra", "unusable-model", "lada", "vektor")
                catalog = expect_catalog(5182, expected_ids)
                assert catalog["iskra"]["availability"] == "disabled", catalog
                assert catalog["vektor"]["availability"] == catalog["unusable-model"]["availability"] == "notChecked", catalog
                assert catalog["lada"]["name"] == LONG_BOT_NAME, catalog
                expect_bad_request(5182, "/api/local-opponent", {"nickname": "Player", "botId": "iskra", "side": "left"})
                error = expect_bad_request(5182, "/api/local-opponent", {"nickname": "Player", "botId": "unusable-model", "side": "left"})
                assert str(model_path) not in error["error"] and re.search("[А-Яа-яЁё]", error["error"]), error
                assert error["error"] == expected_reason, error
                unavailable = expect_catalog(5182, expected_ids)["unusable-model"]
                assert unavailable["availability"] == "unavailable" and not unavailable["canPlay"], unavailable
                assert re.search("[А-Яа-яЁё]", unavailable["availabilityReason"]), unavailable
                assert str(model_path) not in unavailable["availabilityReason"], unavailable
                assert unavailable["availabilityReason"] == expected_reason, unavailable
                expect_opponent(request(5182, "/api/status"), "none")
                boundary_nickname = "Игрок" + "я" * 19
                long_named = request(5182, "/api/local-opponent", {"nickname": boundary_nickname, "botId": "lada", "side": "left"})
                assert long_named["localNickname"] == boundary_nickname, long_named
                expect_opponent(long_named, "bot", "lada", "lada", requested_name=LONG_BOT_NAME, effective_name=LONG_BOT_NAME)
                for rejected_id in ("unknown", "iskra", "unusable-model", "vektor"):
                    expect_bad_request(5182, "/api/local-opponent", {"nickname": "Other", "botId": rejected_id, "side": "left"})
                    preserved = request(5182, "/api/status")
                    expect_identity_parity(long_named, preserved)
                    assert preserved["roundId"] == long_named["roundId"], preserved
                assert expect_catalog(5182, expected_ids)["vektor"]["availability"] == "notChecked"
                with websocket(5182) as long_name_ws:
                    expect_identity_parity(long_named, decode_snapshot(recv_frame(long_name_ws)))
                request(5182, "/api/leave", {})
                fallback = request(5182, "/api/local-opponent", {"nickname": "Fallback", "botId": "vektor", "side": "right"})
                expect_opponent(fallback, "bot", "vektor", "lada", fallback=True, effective_name=LONG_BOT_NAME)
                assert fallback["role"] == "host" and fallback["connection"] == "connected" and fallback["localSide"] == "right", fallback
                assert str(model_path) not in fallback["botFallbackReason"], fallback
                assert fallback["botFallbackReason"] == expected_reason, fallback
                catalog = expect_catalog(5182, expected_ids)
                assert catalog["vektor"]["availability"] == "unavailable" and catalog["vektor"]["canPlay"], catalog
                assert catalog["vektor"]["availabilityReason"] == expected_reason, catalog
                with websocket(5182) as fallback_ws:
                    fallback_frame = decode_snapshot(recv_frame(fallback_ws))
                    expect_opponent(fallback_frame, "bot", "vektor", "lada", fallback=True, effective_name=LONG_BOT_NAME)
                    expect_identity_parity(fallback, fallback_frame)

                    def fallback_advanced():
                        state = request(5182, "/api/status")
                        return state if state["phase"] == "playing" and state["tick"] >= fallback["tick"] + 12 else None

                    advanced = wait_until(f"{failure_case} fallback match enters play and keeps advancing", fallback_advanced)
                    expect_opponent(advanced, "bot", "vektor", "lada", fallback=True, effective_name=LONG_BOT_NAME)
                    expect_bad_request(5182, "/api/restart", {"matchId": fallback["matchId"], "expectedRoundId": fallback["roundId"]})
                    unchanged = request(5182, "/api/status")
                    expect_identity_parity(fallback, unchanged)
                    assert unchanged["roundId"] == fallback["roundId"], unchanged
                fallback_left = request(5182, "/api/leave", {})
                expect_opponent(fallback_left, "none")
                assert expect_catalog(5182, expected_ids)["vektor"]["availability"] == "unavailable"
                retry = request(5182, "/api/local-opponent", {"nickname": "Retry", "botId": "vektor", "side": "left"})
                expect_opponent(retry, "bot", "vektor", "lada", fallback=True, effective_name=LONG_BOT_NAME)
                request(5182, "/api/leave", {})
            finally:
                try:
                    stop_process(fallback_process)
                finally:
                    fallback_log.close()
            print(f"PASS: {failure_case} model availability, explicit fallback play/finished-only rejection, HTTP/WS identity and leave cleanup")

        require_mdns_loopback = os.environ.get("LANPONG_REQUIRE_MDNS_LOOPBACK") == "1"
        host = request(5180, "/api/host", host_payload())
        assert host["role"] == "host" and host["connection"] == "waiting", host
        expect_opponent(host, "lan")
        assert host["localNickname"] == HOST_NICKNAME and host["peerNickname"] is None, host
        # A datagram with a valid Hello prefix must still be rejected in full when
        # the receive buffer truncates it at the packet-size boundary.
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
            hello_prefix = HELLO_PACKET
            payload = hello_prefix + bytes(1201 - len(hello_prefix))
            assert sender.sendto(payload, ("127.0.0.1", 47888)) == len(payload)
        time.sleep(0.2)
        assert request(5180, "/api/status")["connection"] == "waiting"
        hosts = request(5181, "/api/discover")["hosts"]
        assert isinstance(hosts, list), hosts
        if require_mdns_loopback:
            # Run this strict check only when same-machine mDNS multicast is supported.
            mdns_hosts = wait_until("mDNS finds waiting host",
                                    discover_waiting_host, seconds=10)
            assert all(item["nickname"] == HOST_NICKNAME for item in mdns_hosts), mdns_hosts
            print(f"PASS: mDNS found host on 47888: {mdns_hosts}")

        # UDP may reorder a guest's cancellation ahead of its initial Hello.
        # A late Hello with the same source and request ID must stay rejected.
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as challenger:
            challenger.bind(("127.0.0.1", 0))
            challenger.sendto(wire_challenge_packet(12, CANCELED_CHALLENGE_ID),
                              ("127.0.0.1", 47888))
            time.sleep(0.1)
            challenger.sendto(wire_challenge_packet(2, CANCELED_CHALLENGE_ID, RAW_NICKNAME),
                              ("127.0.0.1", 47888))
            expect_challenge_reply(challenger, 11, CANCELED_CHALLENGE_ID)
            assert request(5180, "/api/status")["connection"] == "waiting"

        # A second decline must not cause a retry of the first challenge to reopen it.
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as challenger:
            challenger.bind(("127.0.0.1", 0))
            for request_id in (CHALLENGE_ID, OTHER_CHALLENGE_ID):
                challenger.sendto(wire_challenge_packet(2, request_id, RAW_NICKNAME),
                                  ("127.0.0.1", 47888))
                expect_challenge_reply(challenger, 10, request_id)
                wait_until("host sees raw incoming challenge", lambda:
                           request(5180, "/api/status")["connection"] == "incomingChallenge")
                assert request(5180, "/api/status")["peerNickname"] == RAW_NICKNAME
                declined_raw = request(5180, "/api/decline", {})
                assert declined_raw["connection"] == "waiting", declined_raw
                expect_challenge_reply(challenger, 11, request_id)
            challenger.sendto(wire_challenge_packet(2, CHALLENGE_ID, RAW_NICKNAME),
                              ("127.0.0.1", 47888))
            expect_challenge_reply(challenger, 11, CHALLENGE_ID)
            assert request(5180, "/api/status")["connection"] == "waiting"

        guest = request(5181, "/api/join", join_payload())
        assert guest["role"] == "guest" and guest["connection"] == "connecting", guest
        assert guest["localNickname"] == GUEST_NICKNAME, guest
        pending_host, pending_guest = wait_for_challenge()
        assert pending_host["peerNickname"] == GUEST_NICKNAME, pending_host
        assert pending_guest["peerNickname"] == HOST_NICKNAME, pending_guest
        assert pending_host["phase"] == "waiting" and pending_guest["phase"] == "waiting"
        assert pending_host["tick"] == 0 and pending_guest["tick"] == 0
        time.sleep(0.2)
        still_pending_host, still_pending_guest = wait_for_challenge()
        assert still_pending_host["tick"] == 0 and still_pending_guest["tick"] == 0
        assert still_pending_host["phase"] == "waiting" and still_pending_guest["phase"] == "waiting"
        with websocket(5180) as incoming_ws, websocket(5181) as outgoing_ws:
            incoming_snapshot = decode_snapshot(recv_frame(incoming_ws))
            outgoing_snapshot = decode_snapshot(recv_frame(outgoing_ws))
            assert incoming_snapshot["connection"] == "incomingChallenge", incoming_snapshot
            assert incoming_snapshot["peerNickname"] == GUEST_NICKNAME, incoming_snapshot
            assert outgoing_snapshot["connection"] == "awaitingAcceptance", outgoing_snapshot
            assert outgoing_snapshot["peerNickname"] == HOST_NICKNAME, outgoing_snapshot

        # A guest can withdraw an unanswered request; the host remains available.
        cancelled = request(5181, "/api/leave", {})
        assert cancelled["role"] == "none" and cancelled["connection"] == "idle", cancelled
        wait_until("cancelled challenge leaves host waiting", lambda:
                   request(5180, "/api/status")["connection"] == "waiting")

        request(5181, "/api/join", join_payload())
        wait_for_challenge()
        declined = request(5180, "/api/decline", {})
        assert declined["role"] == "host" and declined["connection"] == "waiting", declined
        wait_until("declined challenge returns guest to idle", lambda:
                   request(5181, "/api/status")["connection"] == "idle")

        request(5181, "/api/join", join_payload())
        accept_challenge()
        assert request(5180, "/api/status")["peerNickname"] == GUEST_NICKNAME
        assert request(5181, "/api/status")["peerNickname"] == HOST_NICKNAME
        if require_mdns_loopback:
            wait_for_mdns_host_absence()
        # Once connected, a datagram from a third socket must not be treated as the peer.
        # A connected host must reject a valid Hello packet from an unknown sender.
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as stranger:
            stranger.bind(("127.0.0.1", 0))
            stranger.settimeout(0.2)
            stranger.sendto(HELLO_PACKET, ("127.0.0.1", 47888))
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
        host_field = before["localSide"] + "Y"
        guest_field = ("right" if before["localSide"] == "left" else "left") + "Y"
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
            controls = control_packets(before)
            snapshot, host_serve = wait_for_ws_event(host_ws, 1)
            assert snapshot["role"] == "host" and snapshot["connection"] == "connected", snapshot
            expect_opponent(snapshot, "lan")
            assert "tick" in snapshot and "leftY" in snapshot and "rightY" in snapshot, snapshot
            guest_snapshot, guest_serve = wait_for_ws_event(guest_ws, 1, host_serve[0])
            assert guest_snapshot["role"] == "guest" and guest_snapshot["connection"] == "connected", guest_snapshot
            expect_opponent(guest_snapshot, "lan")
            assert guest_serve == host_serve, (host_serve, guest_serve)

            send_text(host_ws, '{"axis":1}')
            send_binary(host_ws, b"\x90")  # Wrong array shape.
            send_binary(host_ws, b"\x93\x08\x01\x01")  # Superseded browser protocol version.
            send_binary(host_ws, b"\x93\x0a\x01\x01")  # Unsupported future browser protocol version.
            send_binary(host_ws, b"\xc1")  # Reserved MessagePack prefix.
            send_binary(host_ws, controls[1] + b"\x00")  # A second packed value.
            send_binary(host_ws, controls[1] + bytes(257 - len(controls[1])))
            time.sleep(0.15)
            after_invalid = request(5180, "/api/status")
            assert abs(after_invalid[host_field] - before[host_field]) < 0.005, (before, after_invalid)

            # Both frames use the current protocol version, so each reaches axis
            # validation rather than being rejected by the version check.
            for malformed_axis in (msgpack_helper_bytes([9, before["matchId"], before["roundId"], "up"]), msgpack_helper_bytes([9, before["matchId"], before["roundId"], 2])):
                send_binary(host_ws, malformed_axis)
                time.sleep(0.15)
                after_axis = request(5180, "/api/status")
                assert after_axis["tick"] > after_invalid["tick"], (after_invalid, after_axis)
                assert abs(after_axis[host_field] - before[host_field]) < 0.005, (
                    malformed_axis, before, after_axis)
                after_invalid = after_axis

            for _ in range(8):
                send_binary(host_ws, controls[-1])
                time.sleep(0.04)
            after_malformed = request(5180, "/api/status")
            assert after_malformed[host_field] < before[host_field], (before, after_malformed)

            for _ in range(8):
                send_fragmented_binary(host_ws, controls[1])
                time.sleep(0.04)
            after_fragmented = request(5180, "/api/status")
            assert after_fragmented[host_field] > after_malformed[host_field], (after_malformed, after_fragmented)

            for _ in range(25):
                send_binary(host_ws, controls[-1])
                send_binary(passive_ws, controls[0])
                send_binary(guest_ws, controls[1])
                time.sleep(0.04)
            moved = request(5180, "/api/status")
            assert moved[host_field] < before[host_field], (before, moved)
            assert moved[guest_field] > before[guest_field], (before, moved)
            synced = request(5181, "/api/status")
            assert abs(synced[host_field] - moved[host_field]) < 0.1, (moved, synced)

        with websocket(5180) as closing_ws:
            send_frame(closing_ws, 0x8, (1000).to_bytes(2, "big"))
            expect_prompt_close(closing_ws)

        rematch_finished([5180, 5181], 5181)
        left_guest = request(5181, "/api/leave", {})
        assert left_guest["role"] == "none" and left_guest["connection"] == "idle", left_guest
        assert left_guest["phase"] == "waiting", left_guest
        assert left_guest["peerNickname"] is None, left_guest
        wait_until("host observes guest leave", lambda: request(5180, "/api/status")["connection"] == "waiting")
        assert request(5180, "/api/status").get("pingMs") is None
        if require_mdns_loopback:
            resumed_mdns_hosts = wait_until("waiting host reappears in mDNS",
                                            discover_waiting_host, seconds=10)
            assert all(item["nickname"] == HOST_NICKNAME for item in resumed_mdns_hosts), resumed_mdns_hosts
            print(f"PASS: waiting host reappeared in mDNS: {resumed_mdns_hosts}")
        with UdpRelay(47888) as relay:
            rejoining = request(5181, "/api/join", join_payload(port=relay.port))
            assert rejoining["role"] == "guest" and rejoining["connection"] == "connecting", rejoining
            accept_challenge()
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
                guest_state = request(5181, "/api/status")
                guest_tick = guest_state["tick"]
                guest_field = guest_state["localSide"] + "Y"
                controls = control_packets(guest_state)
                axis = 1 if before_rollback[guest_field] < 0.5 else -1
                relay.pause_guest_inputs.set()
                deadline = time.monotonic() + 2
                target_tick = guest_tick + 6
                ready_input = None
                last_input = None
                decoded_packet = None
                while time.monotonic() < deadline:
                    send_binary(rollback_ws, controls[axis])
                    packet = relay.buffered_input()
                    if packet is not None:
                        if packet != decoded_packet:
                            decoded = msgpack_helper("decode", packet)
                            assert isinstance(decoded, list) and len(decoded) == 2 and decoded[0] == 4, decoded
                            fields = decoded[1]
                            assert isinstance(fields, list) and len(fields) == 6 and fields[0] == 9, decoded
                            input_tick, input_round, axes = fields[3], fields[4], fields[5]
                            assert isinstance(input_tick, int) and isinstance(axes, list), decoded
                            last_input = (input_tick, input_round, axes)
                            decoded_packet = packet
                        input_tick, input_round, axes = last_input
                        if (input_round == before_rollback["roundId"] and
                                input_tick >= target_tick and axes[:6] == [axis] * 6):
                            # Revalidate against the host snapshot immediately before
                            # forwarding these exact bytes, even if a newer packet arrived.
                            before_release = request(5180, "/api/status")
                            earliest = max(before_rollback["tick"] + 1, before_release["tick"] - 16)
                            delayed = sum(value == axis and earliest <= input_tick - index <= before_release["tick"]
                                          for index, value in enumerate(axes))
                            if delayed >= 4:
                                assert abs(before_release[guest_field] - before_rollback[guest_field]) < 0.01, (
                                    before_rollback, before_release)
                                relay.release_input(packet)
                                ready_input = packet
                                break
                    time.sleep(0.012)
                assert ready_input is not None, (target_tick, last_input)

                def rollback_visible():
                    current = request(5180, "/api/status")
                    elapsed_ticks = current["tick"] - before_release["tick"]
                    ordinary_movement = elapsed_ticks * 0.85 / 60
                    movement = (current[guest_field] - before_release[guest_field]) * axis
                    return current if movement > ordinary_movement + 0.025 else None

                wait_until("host replays delayed guest paddle input", rollback_visible, seconds=2)
                relay.pause_guest_inputs.clear()

            wait_until("continued gameplay before guest prediction", lambda:
                       request(5180, "/api/status")["phase"] == "playing"
                       and request(5181, "/api/status")["phase"] == "playing")

            # A predictive guest keeps moving its paddle and advancing simulation
            # ticks while authoritative states are briefly held by the relay.
            with websocket(5181) as predictive_ws:
                assert decode_snapshot(recv_frame(predictive_ws))["connection"] == "connected"
                before_pause = request(5181, "/api/status")
                guest_field = before_pause["localSide"] + "Y"
                controls = control_packets(before_pause)
                axis = 1 if before_pause[guest_field] < 0.5 else -1
                relay.pause_host_packets.set()
                for _ in range(16):
                    send_binary(predictive_ws, controls[axis])
                    time.sleep(0.025)
                during_pause = request(5181, "/api/status")
                assert relay.dropped_state_packets >= 3, relay.dropped_state_packets
                assert during_pause["tick"] >= before_pause["tick"] + 10, (before_pause, during_pause)
                assert (during_pause[guest_field] - before_pause[guest_field]) * axis > 0.05, (
                    before_pause, during_pause)
                previously_forwarded = relay.forwarded_state_packets
                relay.pause_host_packets.clear()

            wait_until("guest reconciles after paused host states", lambda:
                       relay.forwarded_state_packets >= previously_forwarded + 3
                       and abs(request(5180, "/api/status")[guest_field]
                           - request(5181, "/api/status")[guest_field]) < 0.1)
            left_host = request(5180, "/api/leave", {})
            assert left_host["role"] == "none" and left_host["connection"] == "idle", left_host
            assert left_host["phase"] == "waiting", left_host

            def guest_has_left():
                snapshot = request(5181, "/api/status")
                return snapshot if snapshot["role"] == "none" and snapshot["connection"] == "idle" else None

            left_by_host = wait_until("guest leaves after host Bye", guest_has_left)
            assert left_by_host["phase"] == "waiting", left_by_host

            restarted_host = request(5180, "/api/host", host_payload())
            assert restarted_host["role"] == "host" and restarted_host["connection"] == "waiting", restarted_host
            time.sleep(1.2)  # More than two Hello retry intervals.
            still_waiting = request(5180, "/api/status")
            still_idle = request(5181, "/api/status")
            assert still_waiting["connection"] == "waiting", still_waiting
            assert still_idle["role"] == "none" and still_idle["connection"] == "idle", still_idle

            manual_rejoin = request(5181, "/api/join", join_payload(port=relay.port))
            assert manual_rejoin["role"] == "guest", manual_rejoin
            accept_challenge()

            # Simulate a lost Bye: the guest must also leave after peer timeout,
            # rather than automatically joining the next game on the same port.
            relay.pause_host_packets.set()
            request(5180, "/api/leave", {})
            timed_out_guest = wait_until("guest leaves after lost host Bye", guest_has_left)
            assert timed_out_guest["phase"] == "waiting", timed_out_guest
            request(5180, "/api/host", host_payload())
            relay.pause_host_packets.clear()
            time.sleep(1.2)
            still_waiting = request(5180, "/api/status")
            still_idle = request(5181, "/api/status")
            assert still_waiting["connection"] == "waiting", still_waiting
            assert still_idle["role"] == "none" and still_idle["connection"] == "idle", still_idle

            # If every Welcome is lost after acceptance, the host's Bye must still
            # end the guest's pending connection, without a session ID.
            relay.drop_welcome.set()
            pending_join = request(5181, "/api/join", join_payload(port=relay.port))
            assert pending_join["role"] == "guest", pending_join
            wait_for_challenge()
            request(5180, "/api/accept", {})
            wait_until("host accepts challenge without delivering Welcome", lambda:
                       request(5180, "/api/status")["connection"] == "connected"
                       and relay.dropped_welcome_packets >= 1)
            pending_guest = request(5181, "/api/status")
            assert pending_guest["role"] == "guest" and pending_guest["connection"] == "awaitingAcceptance", pending_guest
            request(5180, "/api/leave", {})
            pending_guest_left = wait_until("pending guest leaves after host Bye", guest_has_left)
            assert pending_guest_left["phase"] == "waiting", pending_guest_left
            relay.drop_welcome.clear()

            request(5180, "/api/host", host_payload())
            time.sleep(1.2)
            still_waiting = request(5180, "/api/status")
            still_idle = request(5181, "/api/status")
            assert still_waiting["connection"] == "waiting", still_waiting
            assert still_idle["role"] == "none" and still_idle["connection"] == "idle", still_idle
            request(5180, "/api/leave", {})
        if has_ipv6_loopback():
            ipv6_host = request(5180, "/api/host", host_payload())
            assert "::1" in ipv6_host["localAddresses"], ipv6_host
            ipv6_guest = request(5181, "/api/join", join_payload(address="::1"))
            assert ipv6_guest["role"] == "guest", ipv6_guest
            accept_challenge()
            wait_until("IPv6 UDP state and RTT", lambda:
                       request(5181, "/api/status")["tick"] >= 10
                       and request(5180, "/api/status").get("pingMs") is not None
                       and request(5181, "/api/status").get("pingMs") is not None)
            with websocket(5181) as ipv6_ws:
                ipv6_snapshot = decode_snapshot(recv_frame(ipv6_ws))
                assert ipv6_snapshot["connection"] == "connected", ipv6_snapshot
                assert "::1" in ipv6_snapshot["peerAddress"], ipv6_snapshot
                assert ipv6_snapshot["localNickname"] == GUEST_NICKNAME, ipv6_snapshot
                assert ipv6_snapshot["peerNickname"] == HOST_NICKNAME, ipv6_snapshot
            request(5181, "/api/leave", {})
            wait_until("host observes IPv6 guest leave", lambda:
                       request(5180, "/api/status")["connection"] == "waiting")
            request(5180, "/api/leave", {})

        # Quick Game can be cancelled before its discovery window opens a lobby.
        quick_start = request(5180, "/api/quick", quick_payload("QuickCancel"))
        assert quick_start["connection"] in ("searching", "waiting"), quick_start
        cancelled_quick = request(5180, "/api/leave", {})
        assert cancelled_quick["role"] == "none" and cancelled_quick["connection"] == "idle"
        request(5180, "/api/quick", quick_payload("QuickAgain"))
        wait_for_quick_lobby(5180, "QuickAgain")
        request(5180, "/api/leave", {})
        time.sleep(1.5)
        assert request(5180, "/api/status")["connection"] == "idle"

        # The backend chooses the host port, and a direct invitation is accepted
        # automatically by a Quick Game lobby.
        request(5180, "/api/quick", quick_payload("QuickHost"))
        quick_host = wait_for_quick_lobby(5180, "QuickHost")
        assert 1 <= quick_host["udpPort"] <= 65535, quick_host
        request(5181, "/api/join", join_payload(port=quick_host["udpPort"]))
        wait_until("Quick Game automatically accepts a direct challenge", lambda:
                   request(5180, "/api/status")["connection"] == "connected"
                   and request(5181, "/api/status")["connection"] == "connected")
        assert request(5180, "/api/status")["peerNickname"] == GUEST_NICKNAME
        assert request(5181, "/api/status")["peerNickname"] == "QuickHost"
        request(5181, "/api/leave", {})
        wait_until("Quick Game host ends lobby after guest leave", lambda:
                   request(5180, "/api/status")["connection"] == "idle")
        request(5180, "/api/quick", quick_payload("QuickHost"))
        next_quick_host = wait_for_quick_lobby(5180, "QuickHost")
        request(5181, "/api/join", join_payload(port=next_quick_host["udpPort"]))
        wait_until("new Quick Game auto-accepts another opponent", lambda:
                   request(5180, "/api/status")["connection"] == "connected"
                   and request(5181, "/api/status")["connection"] == "connected")
        request(5181, "/api/leave", {})
        wait_until("second Quick Game ends", lambda:
                   request(5180, "/api/status")["connection"] == "idle")
        time.sleep(1.2)
        assert request(5180, "/api/status")["connection"] == "idle"
        assert request(5181, "/api/status")["connection"] == "idle"

        if require_mdns_loopback:
            request(5180, "/api/quick", quick_payload("QuickA"))
            open_quick = wait_for_quick_lobby(5180, "QuickA")
            found_quick = wait_until("mDNS discovers Quick Game", lambda:
                                     next((item for item in request(5181, "/api/discover")["hosts"]
                                           if item["nickname"] == "QuickA" and
                                           item["port"] == open_quick["udpPort"]), None),
                                     seconds=10)
            assert found_quick["instanceName"], found_quick
            request(5181, "/api/quick", quick_payload("QuickB"))
            wait_until("Quick Game joins an available opponent", lambda:
                       request(5180, "/api/status")["connection"] == "connected"
                       and request(5181, "/api/status")["connection"] == "connected",
                       seconds=15)
            assert request(5180, "/api/status")["peerNickname"] == "QuickB"
            assert request(5181, "/api/status")["peerNickname"] == "QuickA"
            request(5181, "/api/leave", {})
            wait_until("Quick Game host closes after match", lambda:
                       request(5180, "/api/status")["connection"] == "idle")

            # Starting at the same time must settle on one lobby, not two hosts.
            with ThreadPoolExecutor(max_workers=2) as pool:
                starts = [pool.submit(request, port, "/api/quick", quick_payload(nickname))
                          for port, nickname in ((5180, "QuickLeft"), (5181, "QuickRight"))]
                for start in starts:
                    assert start.result()["connection"] in ("searching", "waiting")

            def paired_quick_games():
                left = request(5180, "/api/status")
                right = request(5181, "/api/status")
                if left["connection"] == right["connection"] == "connected":
                    return left, right
                return None

            left, right = wait_until("simultaneous Quick Games converge", paired_quick_games,
                                     seconds=20)
            assert {left["role"], right["role"]} == {"host", "guest"}, (left, right)
            assert left["peerNickname"] == "QuickRight", left
            assert right["peerNickname"] == "QuickLeft", right
            request(5180, "/api/leave", {})
            request(5181, "/api/leave", {})

        request(5180, "/api/host", host_payload())
        request(5181, "/api/join", join_payload())
        accept_challenge()
        terminate_connected_process(processes[1], 5181, 5180, "waiting", "host", "guest")

        processes[1] = launch(5181, logs[1])
        wait_until("restarted guest web server", lambda: request(5181, "/api/status"))
        request(5181, "/api/join", join_payload())
        accept_challenge()
        terminate_connected_process(processes[0], 5180, 5181, "idle", "none", "host")

        print("PASS: configuration-only fourth tracker and tuned configured-process LAN round, configured catalog bots start/input/play/rematch/leave, missing/corrupt-checksum explicit fallback with persistent requested/effective identity, catalog metadata/availability/ordering/error cases, version 9 HTTP and MessagePack WebSocket contract, static UI, discovery, pending challenge acceptance/decline/cancel, IPv4/IPv6 UDP handshake, ping RTT, oversized UDP datagrams, gameplay, shared host/guest game events, controls, malformed and fragmented controls, multiple tabs, close handshake, state sync, restart, host leave without automatic guest rejoin (including lost Bye and Welcome), manual rejoin, host rollback of delayed inputs, guest prediction during paused host states, Quick Game port selection/cancellation/auto-accept/matching, and graceful host/guest shutdown")
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


if __name__ == "__main__":
    main()
