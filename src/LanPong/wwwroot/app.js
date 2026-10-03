"use strict";

const $ = (id) => document.getElementById(id);
const ui = {
  canvas: $("game-canvas"),
  overlay: $("game-overlay"),
  overlayKicker: $("overlay-kicker"),
  overlayTitle: $("overlay-title"),
  overlayDescription: $("overlay-description"),
  arenaTitle: $("arena-title"),
  leftScore: $("left-score"),
  rightScore: $("right-score"),
  leftPlayer: $("left-player"),
  rightPlayer: $("right-player"),
  roleBadge: $("role-badge"),
  connectionPill: $("connection-pill"),
  connectionLabel: $("connection-label"),
  roleDetail: $("role-detail"),
  peerDetail: $("peer-detail"),
  pingRow: $("ping-row"),
  pingValue: $("ping-value"),
  sessionMessage: $("session-message"),
  liveIndicator: $("live-indicator"),
  liveLabel: $("live-label"),
  hostTab: $("tab-host"),
  joinTab: $("tab-join"),
  hostPanel: $("host-panel"),
  joinPanel: $("join-panel"),
  hostForm: $("host-form"),
  joinForm: $("join-form"),
  hostPort: $("host-port"),
  joinPort: $("join-port"),
  peerAddress: $("peer-address"),
  hostButton: $("host-button"),
  joinButton: $("join-button"),
  discoverButton: $("discover-button"),
  discoveryResults: $("discovery-results"),
  shareBox: $("share-box"),
  shareAddresses: $("share-addresses"),
  sharePort: $("share-port"),
  restartButton: $("restart-button"),
  leaveButton: $("leave-button"),
  moveUp: $("move-up"),
  moveDown: $("move-down"),
  toast: $("toast")
};

const defaultSnapshot = {
  role: "none",
  connection: "idle",
  message: "",
  udpPort: 47777,
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
  pingMs: null
};

let snapshot = { ...defaultSnapshot };
let socket = null;
let reconnectTimer = null;
let toastTimer = null;
let busy = false;
let discovering = false;
let lastAddressKey = null;
let lastUiSignature = null;
const pressedKeys = new Set();
const pressedTouch = new Set();
const ctx = ui.canvas.getContext("2d");
// Mirror the normalized gameplay geometry in GameConstants.cs for prediction and drawing.
const TICKS_PER_SECOND = 60;
const LEFT_PADDLE_CENTER_X = 0.045;
const RIGHT_PADDLE_CENTER_X = 0.955;
const PADDLE_HALF_WIDTH = 0.009;
const PADDLE_HALF_HEIGHT = 0.09;
const PADDLE_SPEED = 0.85;
const BALL_RADIUS_Y = 0.012;
const BALL_RADIUS_X = BALL_RADIUS_Y * 9 / 16;
const MIN_PADDLE_Y = PADDLE_HALF_HEIGHT;
const MAX_PADDLE_Y = 1 - PADDLE_HALF_HEIGHT;
const LEFT_CONTACT_X = LEFT_PADDLE_CENTER_X + PADDLE_HALF_WIDTH + BALL_RADIUS_X;
const RIGHT_CONTACT_X = RIGHT_PADDLE_CENTER_X - PADDLE_HALF_WIDTH - BALL_RADIUS_X;
const TOP_CONTACT_Y = BALL_RADIUS_Y;
const BOTTOM_CONTACT_Y = 1 - BALL_RADIUS_Y;
const MIN_INTERPOLATION_MS = 50;
const MAX_INTERPOLATION_MS = 110;
const MAX_EXTRAPOLATION_TICKS = TICKS_PER_SECOND * 0.035;
const MAX_MOTION_SAMPLES = 16;
const MAX_CONTIGUOUS_TICK_GAP = 6;
const motionSamples = [];
let renderTick = null;
let lastFrameTime = null;
let interpolationDelayMs = 60;
let localPaddle = null;
let arenaCache = null;
let webSocketSnapshotVersion = 0;

function resetMotionHistory() {
  motionSamples.length = 0;
  renderTick = null;
  lastFrameTime = null;
  interpolationDelayMs = 60;
  localPaddle = null;
}

function clamp(value, min, max) {
  const number = Number(value);
  return Number.isFinite(number) ? Math.min(max, Math.max(min, number)) : min;
}

function getPort(input) {
  const port = Number(input.value);
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    input.setCustomValidity("Введите порт от 1 до 65535.");
    input.reportValidity();
    input.setCustomValidity("");
    return null;
  }
  return port;
}

function showToast(message) {
  ui.toast.textContent = message;
  ui.toast.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { ui.toast.hidden = true; }, 5000);
}

function writeText(element, value) {
  const text = String(value);
  if (element.textContent !== text) element.textContent = text;
}

function setTab(tab) {
  const isHost = tab === "host";
  ui.hostTab.classList.toggle("is-active", isHost);
  ui.joinTab.classList.toggle("is-active", !isHost);
  ui.hostTab.setAttribute("aria-selected", String(isHost));
  ui.joinTab.setAttribute("aria-selected", String(!isHost));
  ui.hostPanel.hidden = !isHost;
  ui.joinPanel.hidden = isHost;
}

function applySnapshot(data) {
  if (!data || typeof data !== "object") return;
  const next = { ...defaultSnapshot, ...data };
  if (!Array.isArray(next.localAddresses)) next.localAddresses = [];

  const changedRound = next.role !== snapshot.role || next.connection !== snapshot.connection ||
    next.phase !== snapshot.phase || next.roundId !== snapshot.roundId ||
    next.leftScore !== snapshot.leftScore || next.rightScore !== snapshot.rightScore;
  const tick = Number(next.tick);
  const previousTick = Number(snapshot.tick);
  if (!changedRound && Number.isFinite(tick) && Number.isFinite(previousTick) && tick < previousTick) return;

  if (changedRound) resetMotionHistory();
  snapshot = next;

  if (next.connection === "connected" && (next.phase === "playing" || next.phase === "countdown") && Number.isFinite(tick)) {
    const last = motionSamples.at(-1);
    if (!last || tick > last.tick) {
      const arrivedAt = performance.now();
      if (last) {
        const tickGap = tick - last.tick;
        const expectedMs = tickGap * 1000 / TICKS_PER_SECOND;
        const arrivalDeviation = Math.abs(arrivedAt - last.arrivedAt - expectedMs);
        const wantedDelay = clamp(MIN_INTERPOLATION_MS + arrivalDeviation * 2, MIN_INTERPOLATION_MS, MAX_INTERPOLATION_MS);
        const adjustment = wantedDelay > interpolationDelayMs ? 0.65 : 0.035;
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
        rightY: clamp(next.rightY, MIN_PADDLE_Y, MAX_PADDLE_Y)
      });
      if (motionSamples.length > MAX_MOTION_SAMPLES) motionSamples.shift();
    }
  }
  render();
}

function connectionText() {
  switch (snapshot.connection) {
    case "waiting": return "Ожидаем соперника";
    case "connecting": return "Подключаемся";
    case "connected": return "Игроки на связи";
    case "disconnected": return "Связь потеряна";
    default: return "Готово к игре";
  }
}

function roleText() {
  switch (snapshot.role) {
    case "host": return "Первый игрок";
    case "guest": return "Второй игрок";
    default: return "Не в игре";
  }
}

function overlayContent() {
  if (snapshot.role === "none") {
    return ["Локальный матч", "Создайте игру", "Создайте матч или присоединитесь к другу в вашей сети."];
  }
  if (snapshot.connection === "disconnected") {
    return ["Сеть", "Связь потеряна", "Проверьте сеть или покиньте игру, чтобы начать заново."];
  }
  if (snapshot.phase === "gameover") {
    const winner = snapshot.leftScore === snapshot.rightScore
      ? "Ничья"
      : snapshot.leftScore > snapshot.rightScore ? "Игрок 1 победил" : "Игрок 2 победил";
    return ["Матч завершён", winner, "Нажмите «Новый матч», чтобы сыграть ещё раз."];
  }
  if (snapshot.phase === "countdown") {
    return ["Приготовьтесь", String(Math.max(1, Math.ceil(Number(snapshot.countdown) || 0))), "Ракетка движется клавишами W / S или ↑ / ↓."];
  }
  if (snapshot.phase === "playing" && snapshot.connection === "connected") return null;
  if (snapshot.connection === "connecting") {
    return ["Подключение", "Ищем соперника", "Устанавливаем прямое соединение по UDP…"];
  }
  return ["Ожидание", "Ждём соперника", "Передайте второму игроку ваш IP и UDP-порт."];
}

function renderAddresses() {
  const addresses = snapshot.localAddresses.filter((item) => typeof item === "string" && item.length > 0);
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

function render() {
  // Network snapshots arrive much more often than score, ping text, or controls change.
  const uiSignature = JSON.stringify([
    snapshot.role, snapshot.connection, snapshot.message, snapshot.udpPort,
    snapshot.role === "host" ? snapshot.localAddresses : null, snapshot.peerAddress,
    snapshot.leftScore, snapshot.rightScore, snapshot.phase,
    Math.max(1, Math.ceil(Number(snapshot.countdown) || 0)),
    snapshot.pingMs === null ? null : Math.round(Number(snapshot.pingMs)),
    busy, discovering
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
  writeText(ui.pingValue, ping !== null && ping !== undefined && Number.isFinite(Number(ping)) && Number(ping) >= 0
    ? `${Math.round(Number(ping))} мс` : "— мс");
  writeText(ui.sessionMessage, snapshot.message || (inGame ? status + "." : "Создайте игру или присоединитесь к сопернику."));
  writeText(ui.leftScore, Math.max(0, Math.trunc(Number(snapshot.leftScore) || 0)));
  writeText(ui.rightScore, Math.max(0, Math.trunc(Number(snapshot.rightScore) || 0)));
  writeText(ui.arenaTitle, snapshot.phase === "playing" && connected ? "Матч идёт" : snapshot.phase === "gameover" ? "Матч окончен" : inGame ? "Ожидание игры" : "Пора сыграть");

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
  writeText(ui.sharePort, snapshot.udpPort || Number(ui.hostPort.value) || 47777);
  if (snapshot.role === "host") renderAddresses();

}

function lerp(a, b, amount) {
  return a + (b - a) * amount;
}

function interpolateBall(a, b, tick) {
  const duration = b.tick - a.tick;
  const progress = clamp((tick - a.tick) / duration, 0, 1);
  const xBounce = a.vx * b.vx < 0;
  const yBounce = a.vy * b.vy < 0;
  if (!xBounce && !yBounce) {
    return { x: lerp(a.x, b.x, progress), y: lerp(a.y, b.y, progress) };
  }

  // Проходим через точку отскока: прямая между снимками срезала бы угол у ракетки или стены.
  const durationSeconds = duration / TICKS_PER_SECOND;
  const bounceX = xBounce ? (a.vx > 0 ? RIGHT_CONTACT_X : LEFT_CONTACT_X) : null;
  const bounceY = yBounce && !xBounce ? (a.vy > 0 ? BOTTOM_CONTACT_Y : TOP_CONTACT_Y) : null;
  const bounceTime = xBounce
    ? (bounceX - a.x) / a.vx
    : (bounceY - a.y) / a.vy;
  const bounceProgress = clamp(bounceTime / durationSeconds, 0.01, 0.99);
  const corner = xBounce
    ? { x: bounceX, y: clamp(a.y + a.vy * bounceTime, TOP_CONTACT_Y, BOTTOM_CONTACT_Y) }
    : { x: clamp(a.x + a.vx * bounceTime, 0, 1), y: bounceY };

  if (progress <= bounceProgress) {
    const portion = progress / bounceProgress;
    return { x: lerp(a.x, corner.x, portion), y: lerp(a.y, corner.y, portion) };
  }
  const portion = (progress - bounceProgress) / (1 - bounceProgress);
  return { x: lerp(corner.x, b.x, portion), y: lerp(corner.y, b.y, portion) };
}

function displayedMotion(now) {
  if (motionSamples.length === 0) {
    return { ballX: snapshot.ballX, ballY: snapshot.ballY, leftY: snapshot.leftY, rightY: snapshot.rightY };
  }
  const latest = motionSamples.at(-1);
  const elapsedTicks = Math.max(0, now - latest.arrivedAt) * TICKS_PER_SECOND / 1000;
  const targetTick = latest.tick - interpolationDelayMs * TICKS_PER_SECOND / 1000 + elapsedTicks;
  if (renderTick === null || lastFrameTime === null) {
    renderTick = targetTick;
  } else {
    const frameTicks = clamp((now - lastFrameTime) * TICKS_PER_SECOND / 1000, 0, 3);
    const correction = clamp((targetTick - renderTick) * 0.18, -frameTicks, frameTicks * 0.5);
    renderTick += Math.max(0, frameTicks + correction);
    if (targetTick - renderTick > 8) renderTick = targetTick;
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
        rightY: lerp(previous.rightY, next.rightY, amount)
      };
    }
  }
  let seconds = Math.min((renderTick - latest.tick) / TICKS_PER_SECOND, 0.035);
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
  const leftVelocity = previous ? (latest.leftY - previous.leftY) / (latest.tick - previous.tick) : 0;
  const rightVelocity = previous ? (latest.rightY - previous.rightY) / (latest.tick - previous.tick) : 0;
  return {
    ballX: clamp(latest.x + latest.vx * seconds, 0, 1),
    ballY: clamp(latest.y + latest.vy * seconds, TOP_CONTACT_Y, BOTTOM_CONTACT_Y),
    leftY: clamp(latest.leftY + leftVelocity * paddleTicks, MIN_PADDLE_Y, MAX_PADDLE_Y),
    rightY: clamp(latest.rightY + rightVelocity * paddleTicks, MIN_PADDLE_Y, MAX_PADDLE_Y)
  };
}

function displayedLocalPaddle(now) {
  const isActive = snapshot.connection === "connected" &&
    (snapshot.phase === "countdown" || snapshot.phase === "playing") &&
    (snapshot.role === "host" || snapshot.role === "guest");
  if (!isActive) {
    localPaddle = null;
    return null;
  }

  const authoritativeY = clamp(snapshot.role === "host" ? snapshot.leftY : snapshot.rightY, MIN_PADDLE_Y, MAX_PADDLE_Y);
  const axis = currentAxis();
  if (!localPaddle) {
    localPaddle = { y: authoritativeY, lastFrameTime: now, lastAxis: axis, releaseUntil: 0 };
    return localPaddle.y;
  }

  const seconds = clamp((now - localPaddle.lastFrameTime) / 1000, 0, 0.05);
  const ping = Number(snapshot.pingMs);
  if (localPaddle.lastAxis !== 0 && axis === 0) {
    localPaddle.releaseUntil = now + clamp(Number.isFinite(ping) ? ping + 50 : 100, 100, 250);
  }
  const predictedY = clamp(localPaddle.y + axis * PADDLE_SPEED * seconds, MIN_PADDLE_Y, MAX_PADDLE_Y);
  const maxLead = clamp(PADDLE_SPEED * ((Number.isFinite(ping) && ping >= 0 ? ping : 90) / 1000 + 0.05), 0.09, 0.25);
  if (axis > 0) localPaddle.y = Math.max(localPaddle.y, Math.min(predictedY, authoritativeY + maxLead));
  else if (axis < 0) localPaddle.y = Math.min(localPaddle.y, Math.max(predictedY, authoritativeY - maxLead));

  // The latest snapshot is older than local input. Never pull the paddle backward while a key is held.
  const difference = authoritativeY - localPaddle.y;
  if ((axis === 0 && now >= localPaddle.releaseUntil) || (axis !== 0 && difference * axis > 0)) {
    const correction = 1 - Math.exp(-seconds / 0.12);
    localPaddle.y = clamp(localPaddle.y + difference * correction, MIN_PADDLE_Y, MAX_PADDLE_Y);
  }
  localPaddle.lastAxis = axis;
  localPaddle.lastFrameTime = now;
  return localPaddle.y;
}

function resizeArenaCache(width, height, dpr = Math.min(window.devicePixelRatio || 1, 3)) {
  if (!ctx || width <= 0 || height <= 0) return;
  const pixelWidth = Math.round(width * dpr);
  const pixelHeight = Math.round(height * dpr);
  if (arenaCache && arenaCache.width === width && arenaCache.height === height && arenaCache.dpr === dpr) return;
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
  for (let x = step; x < width; x += step) { backgroundCtx.moveTo(x, 0); backgroundCtx.lineTo(x, height); }
  for (let y = step; y < height; y += step) { backgroundCtx.moveTo(0, y); backgroundCtx.lineTo(width, y); }
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
  const vignette = vignetteCtx.createRadialGradient(width / 2, height / 2, height * 0.2, width / 2, height / 2, width * 0.75);
  vignette.addColorStop(0, "rgba(0, 0, 0, 0)");
  vignette.addColorStop(1, "rgba(2, 7, 15, 0.34)");
  vignetteCtx.fillStyle = vignette;
  vignetteCtx.fillRect(0, 0, width, height);
  arenaCache = { width, height, dpr, pixelWidth, pixelHeight, backgroundCanvas, vignetteCanvas };
}

function drawArena(now = performance.now()) {
  if (!ctx) return;
  const dpr = Math.min(window.devicePixelRatio || 1, 3);
  if (arenaCache && dpr !== arenaCache.dpr) resizeArenaCache(ui.canvas.clientWidth, ui.canvas.clientHeight, dpr);
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
  drawPaddle(width * LEFT_PADDLE_CENTER_X - paddleWidth / 2, clamp(leftY, MIN_PADDLE_Y, MAX_PADDLE_Y) * height - paddleHeight / 2, paddleWidth, paddleHeight, "#66e8df");
  drawPaddle(width * RIGHT_PADDLE_CENTER_X - paddleWidth / 2, clamp(rightY, MIN_PADDLE_Y, MAX_PADDLE_Y) * height - paddleHeight / 2, paddleWidth, paddleHeight, "#ff9f91");

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

function animate(now) {
  drawArena(now);
  requestAnimationFrame(animate);
}

function drawPaddle(x, y, width, height, color) {
  ctx.save();
  ctx.shadowColor = color;
  ctx.shadowBlur = Math.max(10, width * 1.4);
  ctx.fillStyle = color;
  ctx.beginPath();
  ctx.roundRect(x, y, width, height, Math.min(width / 2, 7));
  ctx.fill();
  ctx.restore();
}

async function readJson(response) {
  const text = await response.text();
  let data = {};
  if (text) {
    try { data = JSON.parse(text); }
    catch { throw new Error("Приложение вернуло некорректный ответ."); }
  }
  if (!response.ok) throw new Error(data.error || `Ошибка ${response.status}.`);
  return data;
}

async function refreshStatus() {
  const requestVersion = webSocketSnapshotVersion;
  try {
    const response = await fetch("/api/status", { cache: "no-store" });
    const data = await readJson(response);
    if (requestVersion === webSocketSnapshotVersion) applySnapshot(data);
  } catch {
    // WebSocket переподключится; текущий кадр остаётся на экране.
  }
}

async function postAction(path, body = undefined) {
  if (busy) return;
  busy = true;
  render();
  const requestVersion = webSocketSnapshotVersion;
  try {
    const response = await fetch(path, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: body === undefined ? "{}" : JSON.stringify(body)
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

async function discoverHosts() {
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
    const response = await fetch(`/api/discover?port=${encodeURIComponent(port)}`, { cache: "no-store" });
    const data = await readJson(response);
    const hosts = Array.isArray(data.hosts)
      ? data.hosts.filter((host) => host && typeof host.address === "string" && host.address.trim())
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

function currentAxis() {
  const up = pressedKeys.has("w") || pressedKeys.has("arrowup") || pressedTouch.has("up");
  const down = pressedKeys.has("s") || pressedKeys.has("arrowdown") || pressedTouch.has("down");
  return Number(down) - Number(up);
}

function sendAxis() {
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ axis: currentAxis() }));
}

function clearControls() {
  pressedKeys.clear();
  pressedTouch.clear();
  sendAxis();
}

function connectSocket() {
  if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) return;
  const scheme = location.protocol === "https:" ? "wss:" : "ws:";
  socket = new WebSocket(`${scheme}//${location.host}/ws`);
  socket.addEventListener("open", () => { clearTimeout(reconnectTimer); sendAxis(); refreshStatus(); });
  socket.addEventListener("message", (event) => {
    try {
      const data = JSON.parse(event.data);
      if (data && typeof data === "object" && !Array.isArray(data)) {
        webSocketSnapshotVersion++;
        applySnapshot(data);
      }
    }
    catch { /* Пропускаем повреждённый кадр. */ }
  });
  socket.addEventListener("close", () => {
    clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(connectSocket, 1500);
  });
  socket.addEventListener("error", () => socket.close());
}

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
ui.leaveButton.addEventListener("click", () => { clearControls(); postAction("/api/leave"); });

window.addEventListener("keydown", (event) => {
  const key = event.key.toLowerCase();
  if (!["w", "s", "arrowup", "arrowdown"].includes(key)) return;
  if (event.target instanceof Element && event.target.closest("input, textarea, select, [contenteditable]")) return;
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
document.addEventListener("visibilitychange", () => { if (document.hidden) clearControls(); });
document.addEventListener("focusin", (event) => {
  if (event.target instanceof Element && event.target.closest("input, textarea, select, [contenteditable]")) clearControls();
});

function bindTouch(button, direction) {
  button.addEventListener("pointerdown", (event) => {
    event.preventDefault();
    button.setPointerCapture(event.pointerId);
    pressedTouch.add(direction);
    sendAxis();
  });
  const release = (event) => {
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
window.addEventListener("resize", () => resizeArenaCache(ui.canvas.clientWidth, ui.canvas.clientHeight));
resizeArenaCache(ui.canvas.clientWidth, ui.canvas.clientHeight);
setInterval(sendAxis, 33);
setInterval(() => { if (socket?.readyState !== WebSocket.OPEN) refreshStatus(); }, 4000);
render();
requestAnimationFrame(animate);
refreshStatus();
connectSocket();
