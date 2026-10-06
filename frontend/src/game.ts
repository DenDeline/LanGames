import { ArenaRenderer } from "./arena.js";
import { FeedbackController } from "./feedback.js";
import { InputController } from "./input.js";
import { MotionModel } from "./motion.js";
import { GameSession } from "./session.js";
import { isRecord } from "./snapshot.js";
import { SoundController } from "./sound.js";
import {
  getNickname,
  getPort,
  loadNickname,
  render,
  saveNickname,
  setTab,
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

let socket: WebSocket | null = null;
let reconnectTimer: number | undefined;
let busy = false;
let discovering = false;
let webSocketSnapshotVersion = 0;
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
  () => render(session.snapshot, busy, discovering),
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

async function refreshStatus(): Promise<void> {
  const requestVersion = webSocketSnapshotVersion;
  try {
    const response = await fetch("/api/status", { cache: "no-store" });
    const data = await readJson(response);
    if (requestVersion === webSocketSnapshotVersion) session.apply(data);
  } catch {
    // WebSocket переподключится; текущий кадр остаётся на экране.
  }
}

async function postAction(
  path: string,
  body?: { port?: number; address?: string; nickname?: string },
): Promise<void> {
  if (busy) return;
  busy = true;
  render(session.snapshot, busy, discovering);
  const requestVersion = webSocketSnapshotVersion;
  try {
    const response = await fetch(path, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: body === undefined ? "{}" : JSON.stringify(body),
    });
    const data = await readJson(response);
    if (data.role) {
      if (requestVersion === webSocketSnapshotVersion) session.apply(data);
    } else {
      await refreshStatus();
    }
  } catch (error) {
    showToast(error instanceof Error ? error.message : "Не удалось выполнить действие.");
  } finally {
    busy = false;
    render(session.snapshot, busy, discovering);
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
  }
}

function clearSelectedHost(): void {
  selectedHost = null;
  renderSelectedHost();
}

async function discoverHosts(): Promise<void> {
  if (busy || discovering) return;
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
        item.append(nickname, document.createTextNode(` · порт ${host.port}`));
        item.addEventListener("click", () => {
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
  ui.hostTab.addEventListener("click", () => setTab("host"));
  ui.joinTab.addEventListener("click", () => setTab("join"));
  ui.hostForm.addEventListener("submit", (event) => {
    event.preventDefault();
    const nickname = getNickname();
    const port = getPort(ui.hostPort);
    if (nickname !== null && port !== null) postAction("/api/host", { port, nickname });
  });
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
  connectSocket();
}
