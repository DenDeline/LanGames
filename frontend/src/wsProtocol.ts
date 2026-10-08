import { Decoder, encode } from "@msgpack/msgpack";
import { parseSnapshot, type PongSnapshot } from "./snapshot.js";

export type {
  PeerRole,
  OpponentMode,
  ConnectionState,
  GamePhase,
  GameEventKind,
  GameEvent,
  PongSnapshot,
} from "./snapshot.js";

// The WebSocket array layout is independent of the JSON HTTP response shape.
// Change the version whenever indices or enum ordinals change.
const VERSION = 8;
const SNAPSHOT_FIELDS = 30;
const MAX_SNAPSHOT_BYTES = 16 * 1024;
const ROLES = ["none", "host", "guest"] as const;
const OPPONENT_MODES = ["none", "lan", "bot"] as const;
const CONNECTIONS = [
  "idle",
  "waiting",
  "connecting",
  "connected",
  "incomingChallenge",
  "awaitingAcceptance",
  "searching",
] as const;
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
    localNickname,
    peerNickname,
    opponentMode,
    requestedBotId,
    requestedBotName,
    effectiveBotId,
    effectiveBotName,
    opponentFallbackActive,
    botFallbackReason,
  ]: unknown[] = frame;

  if (
    !isIndex(role, ROLES.length) ||
    !isIndex(opponentMode, OPPONENT_MODES.length) ||
    !isIndex(connection, CONNECTIONS.length) ||
    !isIndex(phase, PHASES.length) ||
    !Array.isArray(events) ||
    !events.every(
      (event) =>
        Array.isArray(event) &&
        event.length === 5 &&
        Number.isInteger(event[1]) &&
        event[1] >= 1 &&
        event[1] <= EVENT_KINDS.length,
    )
  )
    return null;
  try {
    return parseSnapshot({
      version: VERSION,
      role: ROLES[role],
      connection: CONNECTIONS[connection],
      phase: PHASES[phase],
      opponentMode: OPPONENT_MODES[opponentMode],
      requestedBotId,
      requestedBotName,
      effectiveBotId,
      effectiveBotName,
      opponentFallbackActive,
      botFallbackReason,
      message,
      udpPort,
      localAddresses,
      peerAddress,
      localNickname,
      peerNickname,
      leftY,
      rightY,
      ballX,
      ballY,
      ballVx,
      ballVy,
      leftScore,
      rightScore,
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
    });
  } catch {
    return null;
  }
}
