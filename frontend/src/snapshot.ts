export type PeerRole = "none" | "host" | "guest";
export type OpponentMode = "none" | "lan" | "bot";
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
  version: 8;
  role: PeerRole;
  requestedBotId: string | null;
  requestedBotName: string | null;
  effectiveBotId: string | null;
  effectiveBotName: string | null;
  botFallbackReason: string | null;
  opponentMode: OpponentMode;
  opponentFallbackActive: boolean;
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
  version: 8,
  role: "none",
  requestedBotId: null,
  requestedBotName: null,
  effectiveBotId: null,
  effectiveBotName: null,
  botFallbackReason: null,
  opponentMode: "none",
  opponentFallbackActive: false,
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
const OPPONENT_MODES: OpponentMode[] = ["none", "lan", "bot"];

export function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isOneOf<T extends string>(value: unknown, choices: readonly T[]): value is T {
  return typeof value === "string" && choices.some((choice) => choice === value);
}

export function parseGameEvent(value: unknown): GameEvent | null {
  if (!isRecord(value) || !validText(value.id, 80)) return null;
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

export function validBotId(value: unknown): value is string {
  return (
    typeof value === "string" &&
    value.length <= 64 &&
    !/[^a-z0-9-]/.test(value) &&
    /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/.test(value)
  );
}

export function validText(value: unknown, maximumLength: number): value is string {
  return typeof value === "string" && value.trim().length > 0 && value.length <= maximumLength;
}

function validNickname(value: unknown): value is string {
  return validText(value, 24) && !/[\u0000-\u001f\u007f-\u009f]/.test(value);
}

function finite(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

function nonnegativeInteger(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}

export function parseSnapshot(data: Record<string, unknown>): PongSnapshot {
  if (data.version !== 8) throw new RangeError("Unsupported snapshot version");
  const rawEvents = data.recentEvents ?? data.events;
  const events = Array.isArray(rawEvents) ? rawEvents.map(parseGameEvent) : null;
  if (
    !isOneOf(data.role, ["none", "host", "guest"]) ||
    !isOneOf(data.opponentMode, OPPONENT_MODES) ||
    !isOneOf(data.connection, [
      "idle",
      "waiting",
      "connecting",
      "connected",
      "incomingChallenge",
      "awaitingAcceptance",
      "searching",
      "disconnected",
    ]) ||
    !isOneOf(data.phase, ["waiting", "countdown", "playing", "gameover"]) ||
    typeof data.message !== "string" ||
    !nonnegativeInteger(data.udpPort) ||
    data.udpPort > 65535 ||
    !Array.isArray(data.localAddresses) ||
    !data.localAddresses.every((address) => typeof address === "string") ||
    (data.peerAddress !== null && typeof data.peerAddress !== "string") ||
    !validNickname(data.localNickname) ||
    (data.peerNickname !== null && !validNickname(data.peerNickname)) ||
    !finite(data.leftY) ||
    !finite(data.rightY) ||
    !finite(data.ballX) ||
    !finite(data.ballY) ||
    !finite(data.ballVx) ||
    !finite(data.ballVy) ||
    !finite(data.countdown) ||
    data.countdown < 0 ||
    !nonnegativeInteger(data.leftScore) ||
    !nonnegativeInteger(data.rightScore) ||
    !nonnegativeInteger(data.tick) ||
    !nonnegativeInteger(data.roundId) ||
    (data.pingMs !== null && (!finite(data.pingMs) || data.pingMs < 0)) ||
    typeof data.opponentFallbackActive !== "boolean" ||
    events === null ||
    events.length > 12 ||
    events.some((event) => event === null)
  )
    throw new TypeError("Invalid snapshot");

  const identity = [
    data.requestedBotId,
    data.requestedBotName,
    data.effectiveBotId,
    data.effectiveBotName,
  ];
  if (data.opponentMode === "bot") {
    if (
      !validBotId(data.requestedBotId) ||
      !validBotId(data.effectiveBotId) ||
      !validText(data.requestedBotName, 64) ||
      !validText(data.effectiveBotName, 64) ||
      data.role !== "host" ||
      data.connection !== "connected" ||
      data.peerNickname !== null ||
      data.udpPort !== 0 ||
      data.peerAddress !== null ||
      data.pingMs !== null ||
      (data.opponentFallbackActive
        ? data.requestedBotId === data.effectiveBotId || !validText(data.botFallbackReason, 512)
        : data.requestedBotId !== data.effectiveBotId ||
          data.requestedBotName !== data.effectiveBotName ||
          data.botFallbackReason !== null)
    )
      throw new TypeError("Invalid bot snapshot identity");
  } else if (
    identity.some((value) => value !== null) ||
    data.opponentFallbackActive ||
    data.botFallbackReason !== null
  ) {
    throw new TypeError("Bot identity outside a bot session");
  }

  return {
    version: 8,
    role: data.role,
    opponentMode: data.opponentMode,
    requestedBotId: data.requestedBotId as string | null,
    requestedBotName: data.requestedBotName as string | null,
    effectiveBotId: data.effectiveBotId as string | null,
    effectiveBotName: data.effectiveBotName as string | null,
    opponentFallbackActive: data.opponentFallbackActive,
    botFallbackReason: data.botFallbackReason as string | null,
    connection: data.connection,
    message: data.message,
    udpPort: data.udpPort,
    localAddresses: data.localAddresses as string[],
    peerAddress: data.peerAddress as string | null,
    localNickname: data.localNickname,
    peerNickname: data.peerNickname as string | null,
    leftY: data.leftY,
    rightY: data.rightY,
    ballX: data.ballX,
    ballY: data.ballY,
    ballVx: data.ballVx,
    ballVy: data.ballVy,
    leftScore: data.leftScore,
    rightScore: data.rightScore,
    phase: data.phase,
    countdown: data.countdown,
    tick: data.tick,
    roundId: data.roundId,
    pingMs: data.pingMs as number | null,
    events: events as GameEvent[],
  };
}
