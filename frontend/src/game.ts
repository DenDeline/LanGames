import { parseBotCatalog } from "./botCatalog.js";
import { ArenaRenderer } from "./arena.js";
import { FeedbackController } from "./feedback.js";
import { InputController } from "./input.js";
import { MotionModel } from "./motion.js";
import { GameSession } from "./session.js";
import { isRecord, parseSnapshot } from "./snapshot.js";
import { SoundController } from "./sound.js";
import {
  getNickname,
  getSelectedBotId,
  getPort,
  loadNickname,
  isSetupLocked,
  noteQuickGameStart,
  acceptQuickGameStart,
  clearQuickGameIntent,
  openBotPicker,
  closeBotPicker,
  render,
  setBotCatalog,
  updateBotSelection,
  saveNickname,
  setGameMode,
  showToast,
  ui,
} from "./view.js";
import { decodeWsSnapshot, encodeWsAxis } from "./wsProtocol.js";

interface DiscoveredHost {
  address: string;
  port: number;
  nickname: string;
}

const SOCKET_RECONNECT_MS = 1500;
const CONTROL_SEND_INTERVAL_MS = 33;
const STATUS_POLL_INTERVAL_MS = 4000;
const ACTION_STATUS_TIMEOUT_MS = 1000;

let socket: WebSocket | null = null;
let reconnectTimer: number | undefined;
let busy = false;
let actionRefreshesCatalog = false;
let discovering = false;
let webSocketSnapshotVersion = 0;
let botCatalogRequestVersion = 0;
let observedBotStatus: string | null = null;
let selectedHost: DiscoveredHost | null = null;
let discoveryButtons: Array<{ host: DiscoveredHost; button: HTMLButtonElement }> = [];

const motion = new MotionModel();
const sound = new SoundController(ui.soundToggle, ui.volumeRange);
const feedback = new FeedbackController(ui.leftScore, ui.rightScore, sound);
const input = new InputController(ui.moveUp, ui.moveDown, sendAxis);
const arena = new ArenaRenderer(
  ui.canvas,
  motion,
  () => session.snapshot,
  () => input.axis,
  () => feedback.pulse,
);
const session = new GameSession(
  motion,
  feedback,
  () => arena.resetTrail(),
  () => {
    const nextBotStatus =
      session.snapshot.opponentMode === "bot"
        ? `${session.snapshot.requestedBotId}|${session.snapshot.effectiveBotId}|${session.snapshot.opponentFallbackActive}`
        : null;
    const botChanged = observedBotStatus !== null && observedBotStatus !== nextBotStatus;
    observedBotStatus = nextBotStatus;
    render(session.snapshot, busy, discovering);
    // A POST already refreshes after its resulting snapshot. Unsolicited failure/fallback
    // transitions need one refresh; ordinary simulation ticks never fetch the catalog.
    if (botChanged && !(busy && actionRefreshesCatalog)) void refreshBotCatalog();
  },
);

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

async function refreshStatus(signal?: AbortSignal): Promise<void> {
  const requestVersion = webSocketSnapshotVersion;
  try {
    const response = await fetch("/api/status", { cache: "no-store", signal });
    const data = await readJson(response);
    if (requestVersion === webSocketSnapshotVersion) session.apply(data);
  } catch {
    // WebSocket переподключится; текущий кадр остаётся на экране.
  }
}

async function refreshBotCatalog(): Promise<void> {
  const requestVersion = ++botCatalogRequestVersion;
  try {
    const response = await fetch("/api/bots", { cache: "no-store" });
    const catalog = parseBotCatalog(await readJson(response));
    if (requestVersion !== botCatalogRequestVersion) return;
    setBotCatalog(catalog);
  } catch {
    if (requestVersion !== botCatalogRequestVersion) return;
    setBotCatalog(null, "Не удалось загрузить ботов. Попробуйте снова.");
  }
  render(session.snapshot, busy, discovering);
}

async function postAction(
  path: string,
  body?: { port?: number; address?: string; nickname?: string; botId?: string },
): Promise<void> {
  if (busy) return;
  if (["/api/local-opponent", "/api/quick", "/api/join"].includes(path) && isSetupLocked()) return;
  if (path === "/api/quick") noteQuickGameStart();
  const expectedBotId =
    path === "/api/local-opponent"
      ? body?.botId
      : path === "/api/restart" && session.snapshot.opponentMode === "bot"
        ? session.snapshot.requestedBotId
        : null;
  const refreshBots =
    path === "/api/local-opponent" || path === "/api/leave" || path === "/api/restart";
  actionRefreshesCatalog = refreshBots;
  busy = true;
  render(session.snapshot, busy, discovering);
  const requestVersion = webSocketSnapshotVersion;
  try {
    const response = await fetch(path, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: body === undefined ? "{}" : JSON.stringify(body),
    });
    // A confirmed rejection cannot own a guest session that arrived meanwhile.
    // Transport failures keep any observed quick start because it may have succeeded.
    if (path === "/api/quick" && !response.ok) clearQuickGameIntent();
    const data = await readJson(response);
    if (data.role) {
      const actionSnapshot = parseSnapshot(data);
      if (
        path === "/api/quick" &&
        (actionSnapshot.connection === "searching" || actionSnapshot.opponentMode === "lan")
      )
        acceptQuickGameStart();
      else if (
        path === "/api/leave" ||
        (path === "/api/local-opponent" && actionSnapshot.opponentMode === "bot") ||
        (path === "/api/join" && actionSnapshot.opponentMode === "lan")
      )
        clearQuickGameIntent();
      if (requestVersion === webSocketSnapshotVersion) session.apply(actionSnapshot);
      const successfulBotAction =
        typeof expectedBotId === "string" &&
        actionSnapshot.opponentMode === "bot" &&
        actionSnapshot.requestedBotId === expectedBotId;
      // A newer pre-action idle/game-over frame may precede the successful HTTP reply.
      // Reconcile briefly while preserving staleness checks and subsequent session changes.
      if (
        successfulBotAction &&
        requestVersion !== webSocketSnapshotVersion &&
        (session.snapshot.opponentMode === "none" ||
          (session.snapshot.opponentMode === "bot" &&
            session.snapshot.requestedBotId === expectedBotId &&
            session.snapshot.roundId !== actionSnapshot.roundId))
      ) {
        await refreshStatus(AbortSignal.timeout(ACTION_STATUS_TIMEOUT_MS));
      }
      if (
        successfulBotAction &&
        session.snapshot.opponentMode === "bot" &&
        session.snapshot.requestedBotId === expectedBotId &&
        session.snapshot.roundId === actionSnapshot.roundId
      ) {
        input.clear();
        ui.arenaPanel.scrollIntoView({ behavior: "instant", block: "start" });
        ui.arenaPanel.focus({ preventScroll: true });
      }
    } else {
      await refreshStatus();
    }
  } catch (error) {
    showToast(
      error instanceof TypeError || error instanceof RangeError
        ? "Приложение вернуло некорректный ответ. Обновите страницу."
        : error instanceof Error
          ? error.message
          : "Не удалось выполнить действие.",
    );
  } finally {
    busy = false;
    actionRefreshesCatalog = false;
    render(session.snapshot, busy, discovering);
    if (refreshBots) await refreshBotCatalog();
  }
}

function isDiscoveredHost(value: unknown): value is DiscoveredHost {
  return (
    isRecord(value) &&
    typeof value.address === "string" &&
    value.address.trim().length > 0 &&
    typeof value.nickname === "string" &&
    value.nickname.trim().length > 0 &&
    typeof value.port === "number" &&
    Number.isInteger(value.port) &&
    value.port >= 1 &&
    value.port <= 65535
  );
}

function renderSelectedHost(): void {
  ui.selectedHost.hidden = selectedHost === null;
  ui.selectedHostName.textContent = selectedHost?.nickname ?? "";
  for (const { host, button } of discoveryButtons) {
    const selected = host === selectedHost;
    button.classList.toggle("is-selected", selected);
    button.setAttribute("aria-pressed", String(selected));
    button.disabled = isSetupLocked();
  }
}

function clearSelectedHost(): void {
  selectedHost = null;
  renderSelectedHost();
}

async function discoverHosts(): Promise<void> {
  if (busy || discovering || isSetupLocked()) return;
  discovering = true;
  clearSelectedHost();
  discoveryButtons = [];
  ui.discoveryResults.hidden = false;
  ui.discoveryResults.replaceChildren();
  const searching = document.createElement("p");
  searching.className = "discovery-empty";
  searching.textContent = "Ищем игры в сети…";
  ui.discoveryResults.append(searching);
  render(session.snapshot, busy, discovering);
  try {
    const response = await fetch("/api/discover", {
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
      empty.textContent = "Игр не найдено. Введите адрес и порт друга.";
      ui.discoveryResults.append(empty);
    } else {
      for (const host of hosts) {
        const item = document.createElement("button");
        item.type = "button";
        item.className = "discovery-result";
        item.setAttribute("aria-pressed", "false");
        const nickname = document.createElement("strong");
        nickname.textContent = host.nickname;
        item.append(nickname);
        item.addEventListener("click", () => {
          if (isSetupLocked()) return;
          selectedHost = host;
          ui.peerAddress.value = "";
          ui.joinPort.value = String(host.port);
          renderSelectedHost();
        });
        discoveryButtons.push({ host, button: item });
        ui.discoveryResults.append(item);
      }
    }
  } catch (error) {
    ui.discoveryResults.hidden = true;
    showToast(error instanceof Error ? error.message : "Не удалось найти игры в сети.");
  } finally {
    discovering = false;
    render(session.snapshot, busy, discovering);
    renderSelectedHost();
  }
}

function sendAxis(): void {
  if (socket?.readyState === WebSocket.OPEN) socket.send(encodeWsAxis(input.axis));
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
      session.apply(data, "websocket");
    } catch {
      currentSocket.close();
    }
  });
  currentSocket.addEventListener("close", () => {
    if (socket !== currentSocket) return;
    feedback.markReconnect();
    clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(connectSocket, SOCKET_RECONNECT_MS);
  });
  currentSocket.addEventListener("error", () => currentSocket.close());
}

function animate(now: number): void {
  arena.draw(now);
  requestAnimationFrame(animate);
}

export function startGame(): void {
  loadNickname();
  sound.loadSettings();
  window.addEventListener("pointerdown", () => sound.unlockAudio(), { capture: true });
  window.addEventListener("keydown", () => sound.unlockAudio(), { capture: true });
  sound.bindControls();
  ui.gameMode.addEventListener("change", () => setGameMode(ui.gameMode.value));
  ui.botPickerOpen.addEventListener("click", () => {
    if (isSetupLocked()) return;
    input.clear();
    openBotPicker();
  });
  ui.botPickerClose.addEventListener("click", () => closeBotPicker());
  ui.botPickerDone.addEventListener("click", () => closeBotPicker());
  ui.botPicker.addEventListener("cancel", (event) => {
    event.preventDefault();
    closeBotPicker();
  });
  ui.botPicker.addEventListener("keydown", (event) => {
    if (!ui.botPicker.open || event.key !== "Tab") return;
    // Keep endpoint navigation in the modal instead of moving to browser chrome.
    const focused = document.activeElement;
    if (event.shiftKey && (focused === ui.botPickerClose || focused === ui.botPickerTitle)) {
      event.preventDefault();
      ui.botPickerDone.focus();
    } else if (!event.shiftKey && focused === ui.botPickerDone) {
      event.preventDefault();
      ui.botPickerClose.focus();
    }
  });
  ui.quickForm.addEventListener("submit", (event) => {
    event.preventDefault();
    const nickname = getNickname();
    if (nickname !== null) postAction("/api/quick", { nickname });
  });
  ui.botForm.addEventListener("submit", (event) => {
    event.preventDefault();
    const nickname = getNickname();
    const botId = getSelectedBotId();
    if (nickname !== null && botId !== null) postAction("/api/local-opponent", { nickname, botId });
  });
  ui.botCatalog.addEventListener("change", () => {
    updateBotSelection();
    render(session.snapshot, busy, discovering);
  });
  ui.botCatalogRetry.addEventListener("click", () => refreshBotCatalog());
  ui.joinForm.addEventListener("submit", (event) => {
    event.preventDefault();
    const nickname = getNickname();
    const port = getPort(ui.joinPort);
    const address = selectedHost?.address ?? ui.peerAddress.value.trim();
    if (!address) {
      ui.peerAddress.setCustomValidity("Выберите найденную игру или введите адрес друга.");
      ui.peerAddress.reportValidity();
      ui.peerAddress.setCustomValidity("");
    }
    if (nickname !== null && port !== null && address)
      postAction("/api/join", { address, port, nickname });
  });
  ui.playerNickname.addEventListener("input", saveNickname);
  ui.peerAddress.addEventListener("input", clearSelectedHost);
  ui.joinPort.addEventListener("input", clearSelectedHost);
  ui.clearSelectedHost.addEventListener("click", () => {
    if (isSetupLocked()) return;
    clearSelectedHost();
    ui.peerAddress.focus();
  });
  ui.discoverButton.addEventListener("click", discoverHosts);
  ui.acceptButton.addEventListener("click", () => postAction("/api/accept"));
  ui.declineButton.addEventListener("click", () => postAction("/api/decline"));
  ui.restartButton.addEventListener("click", () => postAction("/api/restart"));
  ui.leaveButton.addEventListener("click", () => {
    input.clear();
    postAction("/api/leave");
  });
  input.bind();

  if (typeof ResizeObserver !== "undefined") {
    new ResizeObserver(([entry]) => {
      if (entry) arena.resize(entry.contentRect.width, entry.contentRect.height);
    }).observe(ui.canvas);
  }
  window.addEventListener("resize", () =>
    arena.resize(ui.canvas.clientWidth, ui.canvas.clientHeight),
  );
  arena.resize(ui.canvas.clientWidth, ui.canvas.clientHeight);
  setInterval(sendAxis, CONTROL_SEND_INTERVAL_MS);
  setInterval(() => {
    if (socket?.readyState !== WebSocket.OPEN) refreshStatus();
  }, STATUS_POLL_INTERVAL_MS);
  render(session.snapshot, busy, discovering);
  requestAnimationFrame(animate);
  refreshStatus();
  refreshBotCatalog();
  connectSocket();
}
