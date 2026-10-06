export type PeerRole = "none" | "host" | "guest";
export type ConnectionState =
  | "idle"
  | "waiting"
  | "connecting"
  | "connected"
  | "incomingChallenge"
  | "awaitingAcceptance"
  | "searching"
  | "disconnected";
export type GamePhase = "waiting" | "countdown" | "playing" | "gameover";
export type GameEventKind = "serve" | "paddle" | "wall" | "goal" | "match";

export interface GameEvent {
  id: string;
  kind: GameEventKind;
  tick: number;
  x: number;
  y: number;
}

export interface PongSnapshot {
  role: PeerRole;
  connection: ConnectionState;
  message: string;
  udpPort: number;
  localAddresses: string[];
  peerAddress: string | null;
  localNickname: string;
  peerNickname: string | null;
  leftY: number;
  rightY: number;
  ballX: number;
  ballY: number;
  ballVx: number;
  ballVy: number;
  leftScore: number;
  rightScore: number;
  phase: GamePhase;
  countdown: number;
  tick: number;
  roundId: number;
  pingMs: number | null;
  events: GameEvent[];
}

export const defaultSnapshot: PongSnapshot = {
  role: "none",
  connection: "idle",
  message: "",
  udpPort: 0,
  localAddresses: [],
  peerAddress: null,
  localNickname: "",
  peerNickname: null,
  leftY: 0.5,
  rightY: 0.5,
  ballX: 0.5,
  ballY: 0.5,
  ballVx: 0,
  ballVy: 0,
  leftScore: 0,
  rightScore: 0,
  phase: "waiting",
  countdown: 0,
  tick: 0,
  roundId: 0,
  pingMs: null,
  events: [],
};

const EVENT_KINDS: GameEventKind[] = ["serve", "paddle", "wall", "goal", "match"];

export function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isOneOf<T extends string>(value: unknown, choices: readonly T[]): value is T {
  return typeof value === "string" && choices.some((choice) => choice === value);
}

function snapshotNumber(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

export function parseGameEvent(value: unknown): GameEvent | null {
  if (!isRecord(value) || typeof value.id !== "string" || !value.id) return null;
  const kind =
    typeof value.kind === "number" && Number.isInteger(value.kind)
      ? EVENT_KINDS[value.kind - 1]
      : typeof value.kind === "string"
        ? value.kind.toLowerCase()
        : value.kind;
  if (!isOneOf(kind, EVENT_KINDS)) return null;
  const tick = value.tick;
  const x = value.x;
  const y = value.y;
  if (
    typeof tick !== "number" ||
    !Number.isSafeInteger(tick) ||
    tick < 0 ||
    typeof x !== "number" ||
    !Number.isFinite(x) ||
    x < 0 ||
    x > 1 ||
    typeof y !== "number" ||
    !Number.isFinite(y) ||
    y < 0 ||
    y > 1
  )
    return null;
  return { id: value.id, kind, tick, x, y };
}

export function parseSnapshot(data: Record<string, unknown>): PongSnapshot {
  const rawEvents = data.recentEvents ?? data.events;
  return {
    role: isOneOf(data.role, ["none", "host", "guest"]) ? data.role : defaultSnapshot.role,
    connection: isOneOf(data.connection, [
      "idle",
      "waiting",
      "connecting",
      "connected",
      "incomingChallenge",
      "awaitingAcceptance",
      "searching",
      "disconnected",
    ])
      ? data.connection
      : defaultSnapshot.connection,
    message: typeof data.message === "string" ? data.message : defaultSnapshot.message,
    udpPort: snapshotNumber(data.udpPort, defaultSnapshot.udpPort),
    localAddresses: Array.isArray(data.localAddresses)
      ? (data.localAddresses as unknown[]).filter(
          (item): item is string => typeof item === "string",
        )
      : [],
    peerAddress: typeof data.peerAddress === "string" ? data.peerAddress : null,
    localNickname: typeof data.localNickname === "string" ? data.localNickname : "",
    peerNickname: typeof data.peerNickname === "string" ? data.peerNickname : null,
    leftY: snapshotNumber(data.leftY, defaultSnapshot.leftY),
    rightY: snapshotNumber(data.rightY, defaultSnapshot.rightY),
    ballX: snapshotNumber(data.ballX, defaultSnapshot.ballX),
    ballY: snapshotNumber(data.ballY, defaultSnapshot.ballY),
    ballVx: snapshotNumber(data.ballVx, defaultSnapshot.ballVx),
    ballVy: snapshotNumber(data.ballVy, defaultSnapshot.ballVy),
    leftScore: snapshotNumber(data.leftScore, defaultSnapshot.leftScore),
    rightScore: snapshotNumber(data.rightScore, defaultSnapshot.rightScore),
    phase: isOneOf(data.phase, ["waiting", "countdown", "playing", "gameover"])
      ? data.phase
      : defaultSnapshot.phase,
    countdown: snapshotNumber(data.countdown, defaultSnapshot.countdown),
    tick: snapshotNumber(data.tick, defaultSnapshot.tick),
    roundId: snapshotNumber(data.roundId, defaultSnapshot.roundId),
    pingMs: typeof data.pingMs === "number" && Number.isFinite(data.pingMs) ? data.pingMs : null,
    events: Array.isArray(rawEvents)
      ? rawEvents.map(parseGameEvent).filter((event): event is GameEvent => event !== null)
      : [],
  };
}
