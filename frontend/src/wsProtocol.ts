import { Decoder, encode } from "@msgpack/msgpack";
import type { PongSnapshot } from "./snapshot.js";

export type {
  PeerRole,
  ConnectionState,
  GamePhase,
  GameEventKind,
  GameEvent,
  PongSnapshot,
} from "./snapshot.js";

// The WebSocket array layout is independent of the JSON HTTP response shape.
// Change the version whenever indices or enum ordinals change.
const VERSION = 2;
const SNAPSHOT_FIELDS = 21;
const MAX_SNAPSHOT_BYTES = 16 * 1024;
const ROLES = ["none", "host", "guest"] as const;
const CONNECTIONS = ["idle", "waiting", "connecting", "connected"] as const;
const PHASES = ["waiting", "countdown", "playing", "gameover"] as const;
const EVENT_KINDS = ["serve", "paddle", "wall", "goal", "match"] as const;
const decoder = new Decoder({
  maxArrayLength: 64,
  maxMapLength: 0,
  maxStrLength: 4096,
  maxBinLength: 0,
  maxExtLength: 0,
});

const controlFrames = [
  Uint8Array.from(encode([VERSION, -1])).buffer,
  Uint8Array.from(encode([VERSION, 0])).buffer,
  Uint8Array.from(encode([VERSION, 1])).buffer,
];

function isIndex(value: unknown, length: number): value is number {
  return Number.isInteger(value) && (value as number) >= 0 && (value as number) < length;
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

function isNonnegativeInteger(value: unknown): value is number {
  return Number.isSafeInteger(value) && (value as number) >= 0;
}

export function encodeWsAxis(axis: number): ArrayBuffer {
  if (!Number.isInteger(axis) || axis < -1 || axis > 1) throw new RangeError("Invalid axis");
  return controlFrames[axis + 1];
}

export function decodeWsSnapshot(bytes: ArrayBuffer): PongSnapshot | null {
  if (bytes.byteLength === 0 || bytes.byteLength > MAX_SNAPSHOT_BYTES) return null;

  let frame: unknown;
  try {
    frame = decoder.decode(bytes);
  } catch {
    return null;
  }
  if (!Array.isArray(frame) || frame.length !== SNAPSHOT_FIELDS || frame[0] !== VERSION)
    return null;

  const [
    ,
    role,
    connection,
    message,
    udpPort,
    localAddresses,
    peerAddress,
    leftY,
    rightY,
    ballX,
    ballY,
    ballVx,
    ballVy,
    leftScore,
    rightScore,
    phase,
    countdown,
    tick,
    roundId,
    pingMs,
    events,
  ]: unknown[] = frame;

  if (
    !isIndex(role, ROLES.length) ||
    !isIndex(connection, CONNECTIONS.length) ||
    !isIndex(phase, PHASES.length) ||
    typeof message !== "string" ||
    !isNonnegativeInteger(udpPort) ||
    udpPort > 65535 ||
    !Array.isArray(localAddresses) ||
    !localAddresses.every((address) => typeof address === "string") ||
    (peerAddress !== null && typeof peerAddress !== "string") ||
    !isFiniteNumber(leftY) ||
    !isFiniteNumber(rightY) ||
    !isFiniteNumber(ballX) ||
    !isFiniteNumber(ballY) ||
    !isFiniteNumber(ballVx) ||
    !isFiniteNumber(ballVy) ||
    !isFiniteNumber(countdown) ||
    !isNonnegativeInteger(leftScore) ||
    !isNonnegativeInteger(rightScore) ||
    !isNonnegativeInteger(tick) ||
    !isNonnegativeInteger(roundId) ||
    (pingMs !== null && !isFiniteNumber(pingMs)) ||
    !Array.isArray(events) ||
    events.length > 12 ||
    !events.every(
      (event) =>
        Array.isArray(event) &&
        event.length === 5 &&
        typeof event[0] === "string" &&
        event[0].length > 0 &&
        event[0].length <= 80 &&
        Number.isInteger(event[1]) &&
        event[1] >= 1 &&
        event[1] <= EVENT_KINDS.length &&
        isNonnegativeInteger(event[2]) &&
        isFiniteNumber(event[3]) &&
        isFiniteNumber(event[4]) &&
        event[3] >= 0 &&
        event[3] <= 1 &&
        event[4] >= 0 &&
        event[4] <= 1,
    )
  )
    return null;

  return {
    role: ROLES[role],
    connection: CONNECTIONS[connection],
    message,
    udpPort,
    localAddresses,
    peerAddress,
    leftY,
    rightY,
    ballX,
    ballY,
    ballVx,
    ballVy,
    leftScore,
    rightScore,
    phase: PHASES[phase],
    countdown,
    tick,
    roundId,
    pingMs,
    events: events.map((event) => ({
      id: event[0],
      kind: EVENT_KINDS[event[1] - 1],
      tick: event[2],
      x: event[3],
      y: event[4],
    })),
  };
}
