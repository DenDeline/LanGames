import { Decoder, encode } from "@msgpack/msgpack";
import { parseSnapshot, validMatchId, type PongSnapshot } from "./snapshot.js";

export type {
  PeerRole,
  PaddleSide,
  OpponentMode,
  ConnectionState,
  GamePhase,
  GameEventKind,
  GameEvent,
  PongSnapshot,
} from "./snapshot.js";

// The WebSocket array layout is independent of the JSON HTTP response shape.
// Change the version whenever indices or enum ordinals change.
const VERSION = 9;
const SNAPSHOT_FIELDS = 35;
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

function isIndex(value: unknown, length: number): value is number {
  return Number.isInteger(value) && (value as number) >= 0 && (value as number) < length;
}

let controlRound = 0;
let controlMatch: string | null = null;
let controlFrames: ArrayBuffer[] = [];

export function encodeWsAxis(matchId: string, roundId: number, axis: number): ArrayBuffer {
  if (!validMatchId(matchId)) throw new RangeError("Invalid match");
  if (!Number.isSafeInteger(roundId) || roundId <= 0) throw new RangeError("Invalid round");
  if (!Number.isInteger(axis) || axis < -1 || axis > 1) throw new RangeError("Invalid axis");
  if (matchId !== controlMatch || roundId !== controlRound) {
    controlMatch = matchId;
    controlRound = roundId;
    controlFrames = [-1, 0, 1].map(
      (value) => Uint8Array.from(encode([VERSION, matchId, roundId, value])).buffer,
    );
  }
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
    localSide,
    matchId,
    sourceId,
    snapshotSequence,
    canRematch,
  ]: unknown[] = frame;

  if (
    (localSide !== null && localSide !== 1 && localSide !== 2) ||
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
      localSide: localSide === null ? null : localSide === 1 ? "left" : "right",
      matchId,
      sourceId,
      snapshotSequence,
      canRematch,
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
