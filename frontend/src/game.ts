import { decodeWsSnapshot, encodeWsAxis, type PongSnapshot } from "./wsProtocol";

interface MotionSample {
  tick: number;
  arrivedAt: number;
  x: number;
  y: number;
  vx: number;
  vy: number;
  leftY: number;
  rightY: number;
}

interface LocalPaddle {
  y: number;
  lastFrameTime: number;
  lastAxis: number;
  releaseUntil: number;
}

interface ArenaCache {
  width: number;
  height: number;
  dpr: number;
  pixelWidth: number;
  pixelHeight: number;
  backgroundCanvas: HTMLCanvasElement;
  vignetteCanvas: HTMLCanvasElement;
}

interface DiscoveredHost {
  address: string;
  port?: number | string | null;
}

function element<T extends HTMLElement>(id: string): T {
  const found = document.getElementById(id);
  if (!found) throw new Error(`Missing required element: #${id}`);
  return found as T;
}

const ui = {
  canvas: element<HTMLCanvasElement>("game-canvas"),
  overlay: element("game-overlay"),
  overlayKicker: element("overlay-kicker"),
  overlayTitle: element("overlay-title"),
  overlayDescription: element("overlay-description"),
  arenaTitle: element("arena-title"),
  leftScore: element("left-score"),
  rightScore: element("right-score"),
  leftPlayer: element("left-player"),
  rightPlayer: element("right-player"),
  roleBadge: element("role-badge"),
  connectionPill: element("connection-pill"),
  connectionLabel: element("connection-label"),
  roleDetail: element("role-detail"),
  peerDetail: element("peer-detail"),
  pingRow: element("ping-row"),
  pingValue: element("ping-value"),
  sessionMessage: element("session-message"),
  liveIndicator: element("live-indicator"),
  liveLabel: element("live-label"),
  hostTab: element<HTMLButtonElement>("tab-host"),
  joinTab: element<HTMLButtonElement>("tab-join"),
  hostPanel: element("host-panel"),
  joinPanel: element("join-panel"),
  hostForm: element<HTMLFormElement>("host-form"),
  joinForm: element<HTMLFormElement>("join-form"),
  hostPort: element<HTMLInputElement>("host-port"),
  joinPort: element<HTMLInputElement>("join-port"),
  peerAddress: element<HTMLInputElement>("peer-address"),
  hostButton: element<HTMLButtonElement>("host-button"),
  joinButton: element<HTMLButtonElement>("join-button"),
  discoverButton: element<HTMLButtonElement>("discover-button"),
  discoveryResults: element("discovery-results"),
  shareBox: element("share-box"),
  shareAddresses: element("share-addresses"),
  sharePort: element("share-port"),
  restartButton: element<HTMLButtonElement>("restart-button"),
  leaveButton: element<HTMLButtonElement>("leave-button"),
  moveUp: element<HTMLButtonElement>("move-up"),
  moveDown: element<HTMLButtonElement>("move-down"),
  toast: element("toast"),
};

const DEFAULT_UDP_PORT = 47777;
const TOAST_DURATION_MS = 5000;
const defaultSnapshot: PongSnapshot = {
  role: "none",
  connection: "idle",
  message: "",
  udpPort: DEFAULT_UDP_PORT,
  localAddresses: [],
  peerAddress: null,
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
};

let snapshot = { ...defaultSnapshot };
let socket: WebSocket | null = null;
let reconnectTimer: number | undefined;
let toastTimer: number | undefined;
let busy = false;
let discovering = false;
let lastAddressKey: string | null = null;
let lastUiSignature: string | null = null;
const pressedKeys = new Set<string>();
const pressedTouch = new Set<"up" | "down">();
const ctx = ui.canvas.getContext("2d");
const MILLISECONDS_PER_SECOND = 1000;
// Mirror the normalized gameplay geometry in GameConstants.cs for prediction and drawing.
const TICKS_PER_SECOND = 60;
const LEFT_PADDLE_CENTER_X = 0.045;
const RIGHT_PADDLE_CENTER_X = 0.955;
const PADDLE_HALF_WIDTH = 0.009;
const PADDLE_HALF_HEIGHT = 0.09;
const PADDLE_SPEED = 0.85;
const BALL_RADIUS_Y = 0.012;
const BALL_RADIUS_X = (BALL_RADIUS_Y * 9) / 16;
const MIN_PADDLE_Y = PADDLE_HALF_HEIGHT;
const MAX_PADDLE_Y = 1 - PADDLE_HALF_HEIGHT;
const LEFT_CONTACT_X = LEFT_PADDLE_CENTER_X + PADDLE_HALF_WIDTH + BALL_RADIUS_X;
const RIGHT_CONTACT_X = RIGHT_PADDLE_CENTER_X - PADDLE_HALF_WIDTH - BALL_RADIUS_X;
const TOP_CONTACT_Y = BALL_RADIUS_Y;
const BOTTOM_CONTACT_Y = 1 - BALL_RADIUS_Y;
// WebSocket snapshots run at 30 Hz and simulation at 60 Hz; their timers are independent.
const INITIAL_INTERPOLATION_MS = 60;
const MIN_INTERPOLATION_MS = 50;
const MAX_INTERPOLATION_MS = 110;
const INTERPOLATION_JITTER_MULTIPLIER = 2;
const INTERPOLATION_RISE_FACTOR = 0.65;
const INTERPOLATION_FALL_FACTOR = 0.035;
const MAX_EXTRAPOLATION_SECONDS = 0.035;
const MAX_EXTRAPOLATION_TICKS = TICKS_PER_SECOND * MAX_EXTRAPOLATION_SECONDS;
const MAX_MOTION_SAMPLES = 16;
const MAX_CONTIGUOUS_TICK_GAP = 6;
const MIN_BOUNCE_PROGRESS = 0.01;
const MAX_BOUNCE_PROGRESS = 1 - MIN_BOUNCE_PROGRESS;
const MAX_RENDER_FRAME_TICKS = 3;
const RENDER_CORRECTION_FACTOR = 0.18;
const MAX_RENDER_SPEEDUP_FRACTION = 0.5;
const RENDER_RESYNC_TICKS = 8;
// Local paddle prediction and reconciliation use seconds, except the RTT grace period.
const MAX_LOCAL_FRAME_SECONDS = 0.05;
const RELEASE_RTT_PADDING_MS = 50;
const RELEASE_FALLBACK_MS = 100;
const MIN_RELEASE_GRACE_MS = 100;
const MAX_RELEASE_GRACE_MS = 250;
const FALLBACK_PREDICTION_RTT_MS = 90;
const PREDICTION_RTT_PADDING_SECONDS = 0.05;
const MIN_PREDICTION_LEAD_Y = 0.09;
const MAX_PREDICTION_LEAD_Y = 0.25;
const PADDLE_RECONCILIATION_SECONDS = 0.12;
const MAX_CANVAS_DPR = 3;
const SOCKET_RECONNECT_MS = 1500;
const CONTROL_SEND_INTERVAL_MS = 33;
const STATUS_POLL_INTERVAL_MS = 4000;
const motionSamples: MotionSample[] = [];
let renderTick: number | null = null;
let lastFrameTime: number | null = null;
let interpolationDelayMs = INITIAL_INTERPOLATION_MS;
let localPaddle: LocalPaddle | null = null;
let arenaCache: ArenaCache | null = null;
let webSocketSnapshotVersion = 0;

function resetMotionHistory(): void {
  motionSamples.length = 0;
  renderTick = null;
  lastFrameTime = null;
  interpolationDelayMs = INITIAL_INTERPOLATION_MS;
  localPaddle = null;
}

function clamp(value: unknown, min: number, max: number): number {
  const number = Number(value);
  return Number.isFinite(number) ? Math.min(max, Math.max(min, number)) : min;
}

function getPort(input: HTMLInputElement): number | null {
  const port = Number(input.value);
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    input.setCustomValidity("Введите порт от 1 до 65535.");
    input.reportValidity();
    input.setCustomValidity("");
    return null;
  }
  return port;
}

function showToast(message: string): void {
  ui.toast.textContent = message;
  ui.toast.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => {
    ui.toast.hidden = true;
  }, TOAST_DURATION_MS);
}

function writeText(element: HTMLElement, value: string | number): void {
  const text = String(value);
  if (element.textContent !== text) element.textContent = text;
}

function setTab(tab: "host" | "join"): void {
  const isHost = tab === "host";
  ui.hostTab.classList.toggle("is-active", isHost);
  ui.joinTab.classList.toggle("is-active", !isHost);
  ui.hostTab.setAttribute("aria-selected", String(isHost));
  ui.joinTab.setAttribute("aria-selected", String(!isHost));
  ui.hostPanel.hidden = !isHost;
  ui.joinPanel.hidden = isHost;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isOneOf<T extends string>(value: unknown, choices: readonly T[]): value is T {
  return typeof value === "string" && choices.some((choice) => choice === value);
}

function snapshotNumber(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

function parseSnapshot(data: Record<string, unknown>): PongSnapshot {
  return {
    role: isOneOf(data.role, ["none", "host", "guest"]) ? data.role : defaultSnapshot.role,
    connection: isOneOf(data.connection, [
      "idle",
      "waiting",
      "connecting",
      "connected",
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
  };
}

function applySnapshot(data: unknown): void {
  if (!isRecord(data)) return;
  const next = parseSnapshot(data);

  const changedRound =
    next.role !== snapshot.role ||
    next.connection !== snapshot.connection ||
    next.phase !== snapshot.phase ||
    next.roundId !== snapshot.roundId ||
    next.leftScore !== snapshot.leftScore ||
    next.rightScore !== snapshot.rightScore;
  const tick = Number(next.tick);
  const previousTick = Number(snapshot.tick);
  if (
    !changedRound &&
    Number.isFinite(tick) &&
    Number.isFinite(previousTick) &&
    tick < previousTick
  )
    return;

  if (changedRound) resetMotionHistory();
  snapshot = next;

  if (
    next.connection === "connected" &&
    (next.phase === "playing" || next.phase === "countdown") &&
    Number.isFinite(tick)
  ) {
    const last = motionSamples.at(-1);
    if (!last || tick > last.tick) {
      const arrivedAt = performance.now();
      if (last) {
        const tickGap = tick - last.tick;
        const expectedMs = (tickGap * MILLISECONDS_PER_SECOND) / TICKS_PER_SECOND;
        const arrivalDeviation = Math.abs(arrivedAt - last.arrivedAt - expectedMs);
        const wantedDelay = clamp(
          MIN_INTERPOLATION_MS + arrivalDeviation * INTERPOLATION_JITTER_MULTIPLIER,
          MIN_INTERPOLATION_MS,
          MAX_INTERPOLATION_MS,
        );
        const adjustment =
          wantedDelay > interpolationDelayMs
            ? INTERPOLATION_RISE_FACTOR
            : INTERPOLATION_FALL_FACTOR;
        interpolationDelayMs = lerp(interpolationDelayMs, wantedDelay, adjustment);
        // A missing stretch can hide one or more collisions; never draw a chord across it.
        if (tickGap > MAX_CONTIGUOUS_TICK_GAP) {
          motionSamples.length = 0;
          renderTick = null;
          lastFrameTime = null;
        }
      }
      motionSamples.push({
        tick,
        arrivedAt,
        x: clamp(next.ballX, 0, 1),
        y: clamp(next.ballY, 0, 1),
        vx: Number.isFinite(Number(next.ballVx)) ? Number(next.ballVx) : 0,
        vy: Number.isFinite(Number(next.ballVy)) ? Number(next.ballVy) : 0,
        leftY: clamp(next.leftY, MIN_PADDLE_Y, MAX_PADDLE_Y),
        rightY: clamp(next.rightY, MIN_PADDLE_Y, MAX_PADDLE_Y),
      });
      if (motionSamples.length > MAX_MOTION_SAMPLES) motionSamples.shift();
    }
  }
  render();
}

function connectionText(): string {
  switch (snapshot.connection) {
    case "waiting":
      return "Ожидаем соперника";
    case "connecting":
      return "Подключаемся";
    case "connected":
      return "Игроки на связи";
    case "disconnected":
      return "Связь потеряна";
    default:
      return "Готово к игре";
  }
}

function roleText(): string {
  switch (snapshot.role) {
    case "host":
      return "Первый игрок";
    case "guest":
      return "Второй игрок";
    default:
      return "Не в игре";
  }
}

function overlayContent(): [string, string, string] | null {
  if (snapshot.role === "none") {
    return [
      "Локальный матч",
      "Создайте игру",
      "Создайте матч или присоединитесь к другу в вашей сети.",
    ];
  }
  if (snapshot.connection === "disconnected") {
    return ["Сеть", "Связь потеряна", "Проверьте сеть или покиньте игру, чтобы начать заново."];
  }
  if (snapshot.phase === "gameover") {
    const winner =
      snapshot.leftScore === snapshot.rightScore
        ? "Ничья"
        : snapshot.leftScore > snapshot.rightScore
          ? "Игрок 1 победил"
          : "Игрок 2 победил";
    return ["Матч завершён", winner, "Нажмите «Новый матч», чтобы сыграть ещё раз."];
  }
  if (snapshot.phase === "countdown") {
    return [
      "Приготовьтесь",
      String(Math.max(1, Math.ceil(Number(snapshot.countdown) || 0))),
      "Ракетка движется клавишами W / S или ↑ / ↓.",
    ];
  }
  if (snapshot.phase === "playing" && snapshot.connection === "connected") return null;
  if (snapshot.connection === "connecting") {
    return ["Подключение", "Ищем соперника", "Устанавливаем прямое соединение по UDP…"];
  }
  return ["Ожидание", "Ждём соперника", "Передайте второму игроку ваш IP и UDP-порт."];
}

function renderAddresses(): void {
  const addresses = snapshot.localAddresses.filter(
    (item) => typeof item === "string" && item.length > 0,
  );
  const addressKey = JSON.stringify(addresses);
  if (addressKey === lastAddressKey) return;
  lastAddressKey = addressKey;
  ui.shareAddresses.replaceChildren();
  if (addresses.length === 0) {
    const empty = document.createElement("span");
    empty.className = "discovery-empty";
    empty.textContent = "IP-адрес пока не определён";
    ui.shareAddresses.append(empty);
  } else {
    for (const address of addresses) {
      const code = document.createElement("code");
      code.textContent = address;
      ui.shareAddresses.append(code);
    }
  }
}

function render(): void {
  // Network snapshots arrive much more often than score, ping text, or controls change.
  const uiSignature = JSON.stringify([
    snapshot.role,
    snapshot.connection,
    snapshot.message,
    snapshot.udpPort,
    snapshot.role === "host" ? snapshot.localAddresses : null,
    snapshot.peerAddress,
    snapshot.leftScore,
    snapshot.rightScore,
    snapshot.phase,
    Math.max(1, Math.ceil(Number(snapshot.countdown) || 0)),
    snapshot.pingMs === null ? null : Math.round(Number(snapshot.pingMs)),
    busy,
    discovering,
  ]);
  if (uiSignature === lastUiSignature) return;
  lastUiSignature = uiSignature;
  const inGame = snapshot.role === "host" || snapshot.role === "guest";
  const connected = snapshot.connection === "connected";
  const status = connectionText();

  ui.connectionPill.dataset.state = snapshot.connection;
  writeText(ui.connectionLabel, status);
  ui.liveIndicator.dataset.state = snapshot.connection;
  writeText(ui.liveLabel, status);
  ui.roleBadge.dataset.role = snapshot.role;
  writeText(ui.roleBadge, roleText());
  writeText(ui.roleDetail, roleText());
  writeText(ui.peerDetail, snapshot.peerAddress || (inGame ? "Ожидаем подключения" : "—"));
  ui.pingRow.hidden = !connected;
  const ping = snapshot.pingMs;
  writeText(
    ui.pingValue,
    ping !== null && ping !== undefined && Number.isFinite(Number(ping)) && Number(ping) >= 0
      ? `${Math.round(Number(ping))} мс`
      : "— мс",
  );
  writeText(
    ui.sessionMessage,
    snapshot.message || (inGame ? status + "." : "Создайте игру или присоединитесь к сопернику."),
  );
  writeText(ui.leftScore, Math.max(0, Math.trunc(Number(snapshot.leftScore) || 0)));
  writeText(ui.rightScore, Math.max(0, Math.trunc(Number(snapshot.rightScore) || 0)));
  writeText(
    ui.arenaTitle,
    snapshot.phase === "playing" && connected
      ? "Матч идёт"
      : snapshot.phase === "gameover"
        ? "Матч окончен"
        : inGame
          ? "Ожидание игры"
          : "Пора сыграть",
  );

  const overlay = overlayContent();
  ui.overlay.hidden = overlay === null;
  if (overlay !== null) {
    writeText(ui.overlayKicker, overlay[0]);
    writeText(ui.overlayTitle, overlay[1]);
    writeText(ui.overlayDescription, overlay[2]);
  }

  ui.hostButton.disabled = busy || inGame;
  ui.joinButton.disabled = busy || inGame;
  ui.discoverButton.disabled = busy || discovering || inGame;
  ui.hostPort.disabled = busy || inGame;
  ui.joinPort.disabled = busy || inGame;
  ui.peerAddress.disabled = busy || inGame;
  ui.restartButton.disabled = busy || !connected || snapshot.phase !== "gameover";
  ui.leaveButton.disabled = busy || !inGame;
  ui.shareBox.hidden = snapshot.role !== "host";
  writeText(ui.sharePort, snapshot.udpPort || Number(ui.hostPort.value) || DEFAULT_UDP_PORT);
  if (snapshot.role === "host") renderAddresses();
}

function lerp(a: number, b: number, amount: number): number {
  return a + (b - a) * amount;
}

function interpolateBall(a: MotionSample, b: MotionSample, tick: number): { x: number; y: number } {
  const duration = b.tick - a.tick;
  const progress = clamp((tick - a.tick) / duration, 0, 1);
  const xBounce = a.vx * b.vx < 0;
  const yBounce = a.vy * b.vy < 0;
  if (!xBounce && !yBounce) {
    return { x: lerp(a.x, b.x, progress), y: lerp(a.y, b.y, progress) };
  }

  // Проходим через точку отскока: прямая между снимками срезала бы угол у ракетки или стены.
  const durationSeconds = duration / TICKS_PER_SECOND;
  let bounceTime: number;
  let corner: { x: number; y: number };
  if (xBounce) {
    const bounceX = a.vx > 0 ? RIGHT_CONTACT_X : LEFT_CONTACT_X;
    bounceTime = (bounceX - a.x) / a.vx;
    corner = {
      x: bounceX,
      y: clamp(a.y + a.vy * bounceTime, TOP_CONTACT_Y, BOTTOM_CONTACT_Y),
    };
  } else {
    const bounceY = a.vy > 0 ? BOTTOM_CONTACT_Y : TOP_CONTACT_Y;
    bounceTime = (bounceY - a.y) / a.vy;
    corner = { x: clamp(a.x + a.vx * bounceTime, 0, 1), y: bounceY };
  }
  const bounceProgress = clamp(
    bounceTime / durationSeconds,
    MIN_BOUNCE_PROGRESS,
    MAX_BOUNCE_PROGRESS,
  );
  if (progress <= bounceProgress) {
    const portion = progress / bounceProgress;
    return { x: lerp(a.x, corner.x, portion), y: lerp(a.y, corner.y, portion) };
  }
  const portion = (progress - bounceProgress) / (1 - bounceProgress);
  return { x: lerp(corner.x, b.x, portion), y: lerp(corner.y, b.y, portion) };
}

function displayedMotion(now: number): Pick<PongSnapshot, "ballX" | "ballY" | "leftY" | "rightY"> {
  if (motionSamples.length === 0) {
    return {
      ballX: snapshot.ballX,
      ballY: snapshot.ballY,
      leftY: snapshot.leftY,
      rightY: snapshot.rightY,
    };
  }
  const latest = motionSamples.at(-1)!;
  const elapsedTicks =
    (Math.max(0, now - latest.arrivedAt) * TICKS_PER_SECOND) / MILLISECONDS_PER_SECOND;
  const targetTick =
    latest.tick -
    (interpolationDelayMs * TICKS_PER_SECOND) / MILLISECONDS_PER_SECOND +
    elapsedTicks;
  if (renderTick === null || lastFrameTime === null) {
    renderTick = targetTick;
  } else {
    const frameTicks = clamp(
      ((now - lastFrameTime) * TICKS_PER_SECOND) / MILLISECONDS_PER_SECOND,
      0,
      MAX_RENDER_FRAME_TICKS,
    );
    const correction = clamp(
      (targetTick - renderTick) * RENDER_CORRECTION_FACTOR,
      -frameTicks,
      frameTicks * MAX_RENDER_SPEEDUP_FRACTION,
    );
    renderTick += Math.max(0, frameTicks + correction);
    if (targetTick - renderTick > RENDER_RESYNC_TICKS) renderTick = targetTick;
  }
  lastFrameTime = now;
  renderTick = Math.min(renderTick, latest.tick + MAX_EXTRAPOLATION_TICKS);

  const first = motionSamples[0];
  if (renderTick <= first.tick) {
    return { ballX: first.x, ballY: first.y, leftY: first.leftY, rightY: first.rightY };
  }
  for (let i = 1; i < motionSamples.length; i++) {
    const next = motionSamples[i];
    if (renderTick <= next.tick) {
      const previous = motionSamples[i - 1];
      const amount = (renderTick - previous.tick) / (next.tick - previous.tick);
      const ball = interpolateBall(previous, next, renderTick);
      return {
        ballX: ball.x,
        ballY: ball.y,
        leftY: lerp(previous.leftY, next.leftY, amount),
        rightY: lerp(previous.rightY, next.rightY, amount),
      };
    }
  }
  let seconds = Math.min((renderTick - latest.tick) / TICKS_PER_SECOND, MAX_EXTRAPOLATION_SECONDS);
  // Без следующего снимка неизвестно, попал ли мяч в ракетку: прогноз останавливается у контакта.
  if (latest.vx > 0 && latest.x <= RIGHT_CONTACT_X) {
    seconds = Math.min(seconds, Math.max(0, (RIGHT_CONTACT_X - latest.x) / latest.vx));
  } else if (latest.vx < 0 && latest.x >= LEFT_CONTACT_X) {
    seconds = Math.min(seconds, Math.max(0, (LEFT_CONTACT_X - latest.x) / latest.vx));
  }
  if (latest.vy > 0 && latest.y <= BOTTOM_CONTACT_Y) {
    seconds = Math.min(seconds, Math.max(0, (BOTTOM_CONTACT_Y - latest.y) / latest.vy));
  } else if (latest.vy < 0 && latest.y >= TOP_CONTACT_Y) {
    seconds = Math.min(seconds, Math.max(0, (TOP_CONTACT_Y - latest.y) / latest.vy));
  }
  const previous = motionSamples.length > 1 ? motionSamples.at(-2) : null;
  const paddleTicks = Math.min(renderTick - latest.tick, MAX_EXTRAPOLATION_TICKS);
  const leftVelocity = previous
    ? (latest.leftY - previous.leftY) / (latest.tick - previous.tick)
    : 0;
  const rightVelocity = previous
    ? (latest.rightY - previous.rightY) / (latest.tick - previous.tick)
    : 0;
  return {
    ballX: clamp(latest.x + latest.vx * seconds, 0, 1),
    ballY: clamp(latest.y + latest.vy * seconds, TOP_CONTACT_Y, BOTTOM_CONTACT_Y),
    leftY: clamp(latest.leftY + leftVelocity * paddleTicks, MIN_PADDLE_Y, MAX_PADDLE_Y),
    rightY: clamp(latest.rightY + rightVelocity * paddleTicks, MIN_PADDLE_Y, MAX_PADDLE_Y),
  };
}

function displayedLocalPaddle(now: number): number | null {
  const isActive =
    snapshot.connection === "connected" &&
    (snapshot.phase === "countdown" || snapshot.phase === "playing") &&
    (snapshot.role === "host" || snapshot.role === "guest");
  if (!isActive) {
    localPaddle = null;
    return null;
  }

  const authoritativeY = clamp(
    snapshot.role === "host" ? snapshot.leftY : snapshot.rightY,
    MIN_PADDLE_Y,
    MAX_PADDLE_Y,
  );
  const axis = currentAxis();
  if (!localPaddle) {
    localPaddle = { y: authoritativeY, lastFrameTime: now, lastAxis: axis, releaseUntil: 0 };
    return localPaddle.y;
  }

  const seconds = clamp(
    (now - localPaddle.lastFrameTime) / MILLISECONDS_PER_SECOND,
    0,
    MAX_LOCAL_FRAME_SECONDS,
  );
  const ping = Number(snapshot.pingMs);
  if (localPaddle.lastAxis !== 0 && axis === 0) {
    localPaddle.releaseUntil =
      now +
      clamp(
        Number.isFinite(ping) ? ping + RELEASE_RTT_PADDING_MS : RELEASE_FALLBACK_MS,
        MIN_RELEASE_GRACE_MS,
        MAX_RELEASE_GRACE_MS,
      );
  }
  const predictedY = clamp(
    localPaddle.y + axis * PADDLE_SPEED * seconds,
    MIN_PADDLE_Y,
    MAX_PADDLE_Y,
  );
  const maxLead = clamp(
    PADDLE_SPEED *
      ((Number.isFinite(ping) && ping >= 0 ? ping : FALLBACK_PREDICTION_RTT_MS) /
        MILLISECONDS_PER_SECOND +
        PREDICTION_RTT_PADDING_SECONDS),
    MIN_PREDICTION_LEAD_Y,
    MAX_PREDICTION_LEAD_Y,
  );
  if (axis > 0)
    localPaddle.y = Math.max(localPaddle.y, Math.min(predictedY, authoritativeY + maxLead));
  else if (axis < 0)
    localPaddle.y = Math.min(localPaddle.y, Math.max(predictedY, authoritativeY - maxLead));

  // The latest snapshot is older than local input. Never pull the paddle backward while a key is held.
  const difference = authoritativeY - localPaddle.y;
  if ((axis === 0 && now >= localPaddle.releaseUntil) || (axis !== 0 && difference * axis > 0)) {
    const correction = 1 - Math.exp(-seconds / PADDLE_RECONCILIATION_SECONDS);
    localPaddle.y = clamp(localPaddle.y + difference * correction, MIN_PADDLE_Y, MAX_PADDLE_Y);
  }
  localPaddle.lastAxis = axis;
  localPaddle.lastFrameTime = now;
  return localPaddle.y;
}

function resizeArenaCache(
  width: number,
  height: number,
  dpr: number = Math.min(window.devicePixelRatio || 1, MAX_CANVAS_DPR),
): void {
  if (!ctx || width <= 0 || height <= 0) return;
  const pixelWidth = Math.round(width * dpr);
  const pixelHeight = Math.round(height * dpr);
  if (
    arenaCache &&
    arenaCache.width === width &&
    arenaCache.height === height &&
    arenaCache.dpr === dpr
  )
    return;
  ui.canvas.width = pixelWidth;
  ui.canvas.height = pixelHeight;

  const backgroundCanvas = document.createElement("canvas");
  backgroundCanvas.width = pixelWidth;
  backgroundCanvas.height = pixelHeight;
  const backgroundCtx = backgroundCanvas.getContext("2d");
  const vignetteCanvas = document.createElement("canvas");
  vignetteCanvas.width = pixelWidth;
  vignetteCanvas.height = pixelHeight;
  const vignetteCtx = vignetteCanvas.getContext("2d");
  if (!backgroundCtx || !vignetteCtx) return;
  backgroundCtx.setTransform(dpr, 0, 0, dpr, 0, 0);

  const background = backgroundCtx.createLinearGradient(0, 0, width, height);
  background.addColorStop(0, "#122639");
  background.addColorStop(0.5, "#0c1a2c");
  background.addColorStop(1, "#172438");
  backgroundCtx.fillStyle = background;
  backgroundCtx.fillRect(0, 0, width, height);

  backgroundCtx.strokeStyle = "rgba(126, 194, 203, 0.055)";
  backgroundCtx.lineWidth = 1;
  const step = Math.max(24, width / 28);
  backgroundCtx.beginPath();
  for (let x = step; x < width; x += step) {
    backgroundCtx.moveTo(x, 0);
    backgroundCtx.lineTo(x, height);
  }
  for (let y = step; y < height; y += step) {
    backgroundCtx.moveTo(0, y);
    backgroundCtx.lineTo(width, y);
  }
  backgroundCtx.stroke();

  backgroundCtx.strokeStyle = "rgba(189, 225, 232, 0.26)";
  backgroundCtx.lineWidth = Math.max(1, width * 0.0015);
  backgroundCtx.setLineDash([Math.max(7, height * 0.022), Math.max(7, height * 0.022)]);
  backgroundCtx.beginPath();
  backgroundCtx.moveTo(width / 2, 0);
  backgroundCtx.lineTo(width / 2, height);
  backgroundCtx.stroke();
  backgroundCtx.setLineDash([]);
  backgroundCtx.beginPath();
  backgroundCtx.arc(width / 2, height / 2, height * 0.105, 0, Math.PI * 2);
  backgroundCtx.stroke();

  vignetteCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
  const vignette = vignetteCtx.createRadialGradient(
    width / 2,
    height / 2,
    height * 0.2,
    width / 2,
    height / 2,
    width * 0.75,
  );
  vignette.addColorStop(0, "rgba(0, 0, 0, 0)");
  vignette.addColorStop(1, "rgba(2, 7, 15, 0.34)");
  vignetteCtx.fillStyle = vignette;
  vignetteCtx.fillRect(0, 0, width, height);
  arenaCache = { width, height, dpr, pixelWidth, pixelHeight, backgroundCanvas, vignetteCanvas };
}

function drawArena(now: number = performance.now()): void {
  if (!ctx) return;
  const dpr = Math.min(window.devicePixelRatio || 1, MAX_CANVAS_DPR);
  if (arenaCache && dpr !== arenaCache.dpr)
    resizeArenaCache(ui.canvas.clientWidth, ui.canvas.clientHeight, dpr);
  if (!arenaCache) return;
  const { width, height, pixelWidth, pixelHeight, backgroundCanvas, vignetteCanvas } = arenaCache;
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.drawImage(backgroundCanvas, 0, 0, pixelWidth, pixelHeight);
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

  const paddleWidth = width * PADDLE_HALF_WIDTH * 2;
  const paddleHeight = height * PADDLE_HALF_HEIGHT * 2;
  const motion = displayedMotion(now);
  const localY = displayedLocalPaddle(now);
  const leftY = snapshot.role === "host" && localY !== null ? localY : motion.leftY;
  const rightY = snapshot.role === "guest" && localY !== null ? localY : motion.rightY;
  drawPaddle(
    ctx,
    width * LEFT_PADDLE_CENTER_X - paddleWidth / 2,
    clamp(leftY, MIN_PADDLE_Y, MAX_PADDLE_Y) * height - paddleHeight / 2,
    paddleWidth,
    paddleHeight,
    "#66e8df",
  );
  drawPaddle(
    ctx,
    width * RIGHT_PADDLE_CENTER_X - paddleWidth / 2,
    clamp(rightY, MIN_PADDLE_Y, MAX_PADDLE_Y) * height - paddleHeight / 2,
    paddleWidth,
    paddleHeight,
    "#ff9f91",
  );

  const ballX = clamp(motion.ballX, 0, 1) * width;
  const ballY = clamp(motion.ballY, 0, 1) * height;
  const ballRadius = Math.max(4, height * BALL_RADIUS_Y);
  ctx.save();
  ctx.shadowColor = "#fff6df";
  ctx.shadowBlur = ballRadius * 3;
  ctx.fillStyle = "#fff7e8";
  ctx.beginPath();
  ctx.arc(ballX, ballY, ballRadius, 0, Math.PI * 2);
  ctx.fill();
  ctx.restore();

  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.drawImage(vignetteCanvas, 0, 0, pixelWidth, pixelHeight);
}

function animate(now: number): void {
  drawArena(now);
  requestAnimationFrame(animate);
}

function drawPaddle(
  context: CanvasRenderingContext2D,
  x: number,
  y: number,
  width: number,
  height: number,
  color: string,
): void {
  context.save();
  context.shadowColor = color;
  context.shadowBlur = Math.max(10, width * 1.4);
  context.fillStyle = color;
  context.beginPath();
  context.roundRect(x, y, width, height, Math.min(width / 2, 7));
  context.fill();
  context.restore();
}

async function readJson(response: Response): Promise<Record<string, unknown>> {
  const text = await response.text();
  let data: Record<string, unknown> = {};
  if (text) {
    try {
      const parsed: unknown = JSON.parse(text);
      if (!isRecord(parsed)) throw new Error();
      data = parsed;
    } catch {
      throw new Error("Приложение вернуло некорректный ответ.");
    }
  }
  if (!response.ok)
    throw new Error(
      typeof data.error === "string" && data.error ? data.error : `Ошибка ${response.status}.`,
    );
  return data;
}

async function refreshStatus(): Promise<void> {
  const requestVersion = webSocketSnapshotVersion;
  try {
    const response = await fetch("/api/status", { cache: "no-store" });
    const data = await readJson(response);
    if (requestVersion === webSocketSnapshotVersion) applySnapshot(data);
  } catch {
    // WebSocket переподключится; текущий кадр остаётся на экране.
  }
}

async function postAction(path: string, body?: { port?: number; address?: string }): Promise<void> {
  if (busy) return;
  busy = true;
  render();
  const requestVersion = webSocketSnapshotVersion;
  try {
    const response = await fetch(path, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: body === undefined ? "{}" : JSON.stringify(body),
    });
    const data = await readJson(response);
    if (data.role) {
      if (requestVersion === webSocketSnapshotVersion) applySnapshot(data);
    } else {
      await refreshStatus();
    }
  } catch (error) {
    showToast(error instanceof Error ? error.message : "Не удалось выполнить действие.");
  } finally {
    busy = false;
    render();
  }
}

function isDiscoveredHost(value: unknown): value is DiscoveredHost {
  return (
    isRecord(value) &&
    typeof value.address === "string" &&
    value.address.trim().length > 0 &&
    (value.port == null || typeof value.port === "number" || typeof value.port === "string")
  );
}

async function discoverHosts(): Promise<void> {
  if (busy || discovering) return;
  const port = getPort(ui.joinPort);
  if (port === null) return;
  discovering = true;
  ui.discoveryResults.hidden = false;
  ui.discoveryResults.replaceChildren();
  const searching = document.createElement("p");
  searching.className = "discovery-empty";
  searching.textContent = "Ищем игры в сети…";
  ui.discoveryResults.append(searching);
  render();
  try {
    const response = await fetch(`/api/discover?port=${encodeURIComponent(port)}`, {
      cache: "no-store",
    });
    const data = await readJson(response);
    const hosts = Array.isArray(data.hosts)
      ? (data.hosts as unknown[]).filter(isDiscoveredHost)
      : [];
    ui.discoveryResults.replaceChildren();
    if (hosts.length === 0) {
      const empty = document.createElement("p");
      empty.className = "discovery-empty";
      empty.textContent = "Игр не найдено. Введите IP первого игрока вручную.";
      ui.discoveryResults.append(empty);
    } else {
      const first = hosts[0];
      ui.peerAddress.value = first.address;
      if (Number.isInteger(Number(first.port))) ui.joinPort.value = String(first.port);
      for (const host of hosts) {
        const item = document.createElement("button");
        item.type = "button";
        item.className = "discovery-result";
        const address = document.createElement("strong");
        address.textContent = host.address;
        item.append(address, document.createTextNode(` · порт ${host.port ?? port}`));
        item.addEventListener("click", () => {
          ui.peerAddress.value = host.address;
          if (Number.isInteger(Number(host.port))) ui.joinPort.value = String(host.port);
          ui.peerAddress.focus();
        });
        ui.discoveryResults.append(item);
      }
    }
  } catch (error) {
    ui.discoveryResults.hidden = true;
    showToast(error instanceof Error ? error.message : "Не удалось найти игры в сети.");
  } finally {
    discovering = false;
    render();
  }
}

function currentAxis(): number {
  const up = pressedKeys.has("w") || pressedKeys.has("arrowup") || pressedTouch.has("up");
  const down = pressedKeys.has("s") || pressedKeys.has("arrowdown") || pressedTouch.has("down");
  return Number(down) - Number(up);
}

function sendAxis(): void {
  if (socket?.readyState === WebSocket.OPEN) socket.send(encodeWsAxis(currentAxis()));
}

function clearControls(): void {
  pressedKeys.clear();
  pressedTouch.clear();
  sendAxis();
}

function connectSocket(): void {
  if (
    socket &&
    (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)
  )
    return;
  const scheme = location.protocol === "https:" ? "wss:" : "ws:";
  const currentSocket = new WebSocket(`${scheme}//${location.host}/ws`);
  currentSocket.binaryType = "arraybuffer";
  socket = currentSocket;
  currentSocket.addEventListener("open", () => {
    clearTimeout(reconnectTimer);
    sendAxis();
    refreshStatus();
  });
  currentSocket.addEventListener("message", (event) => {
    if (socket !== currentSocket) return;
    try {
      const payload: unknown = event.data;
      if (!(payload instanceof ArrayBuffer)) {
        currentSocket.close();
        return;
      }
      const data = decodeWsSnapshot(payload);
      if (data === null) {
        currentSocket.close();
        return;
      }
      webSocketSnapshotVersion++;
      applySnapshot(data);
    } catch {
      currentSocket.close();
    }
  });
  currentSocket.addEventListener("close", () => {
    if (socket !== currentSocket) return;
    clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(connectSocket, SOCKET_RECONNECT_MS);
  });
  currentSocket.addEventListener("error", () => currentSocket.close());
}

export function startGame(): void {
  ui.hostTab.addEventListener("click", () => setTab("host"));
  ui.joinTab.addEventListener("click", () => setTab("join"));
  ui.hostForm.addEventListener("submit", (event) => {
    event.preventDefault();
    const port = getPort(ui.hostPort);
    if (port !== null) postAction("/api/host", { port });
  });
  ui.joinForm.addEventListener("submit", (event) => {
    event.preventDefault();
    const port = getPort(ui.joinPort);
    const address = ui.peerAddress.value.trim();
    if (port !== null && address) postAction("/api/join", { address, port });
  });
  ui.discoverButton.addEventListener("click", discoverHosts);
  ui.restartButton.addEventListener("click", () => postAction("/api/restart"));
  ui.leaveButton.addEventListener("click", () => {
    clearControls();
    postAction("/api/leave");
  });

  window.addEventListener("keydown", (event) => {
    const key = event.key.toLowerCase();
    if (!["w", "s", "arrowup", "arrowdown"].includes(key)) return;
    if (
      event.target instanceof Element &&
      event.target.closest("input, textarea, select, [contenteditable]")
    )
      return;
    event.preventDefault();
    pressedKeys.add(key);
    sendAxis();
  });
  window.addEventListener("keyup", (event) => {
    const key = event.key.toLowerCase();
    if (!pressedKeys.has(key)) return;
    pressedKeys.delete(key);
    sendAxis();
  });
  window.addEventListener("blur", clearControls);
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) clearControls();
  });
  document.addEventListener("focusin", (event) => {
    if (
      event.target instanceof Element &&
      event.target.closest("input, textarea, select, [contenteditable]")
    )
      clearControls();
  });

  function bindTouch(button: HTMLButtonElement, direction: "up" | "down"): void {
    button.addEventListener("pointerdown", (event) => {
      event.preventDefault();
      button.setPointerCapture(event.pointerId);
      pressedTouch.add(direction);
      sendAxis();
    });
    const release = (event: PointerEvent) => {
      event.preventDefault();
      pressedTouch.delete(direction);
      sendAxis();
    };
    button.addEventListener("pointerup", release);
    button.addEventListener("pointercancel", release);
    button.addEventListener("lostpointercapture", release);
  }
  bindTouch(ui.moveUp, "up");
  bindTouch(ui.moveDown, "down");

  if (typeof ResizeObserver !== "undefined") {
    new ResizeObserver(([entry]) => {
      if (entry) resizeArenaCache(entry.contentRect.width, entry.contentRect.height);
    }).observe(ui.canvas);
  }
  window.addEventListener("resize", () =>
    resizeArenaCache(ui.canvas.clientWidth, ui.canvas.clientHeight),
  );
  resizeArenaCache(ui.canvas.clientWidth, ui.canvas.clientHeight);
  setInterval(sendAxis, CONTROL_SEND_INTERVAL_MS);
  setInterval(() => {
    if (socket?.readyState !== WebSocket.OPEN) refreshStatus();
  }, STATUS_POLL_INTERVAL_MS);
  render();
  requestAnimationFrame(animate);
  refreshStatus();
  connectSocket();
}
