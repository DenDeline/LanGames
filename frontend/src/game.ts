import { ArenaRenderer } from "./arena.js";
import { FeedbackController } from "./feedback.js";
import { InputController } from "./input.js";
import { MotionModel } from "./motion.js";
import { GameSession } from "./session.js";
import { isRecord } from "./snapshot.js";
import { SoundController } from "./sound.js";
import { getPort, render, setTab, showToast, ui } from "./view.js";
import { decodeWsSnapshot, encodeWsAxis } from "./wsProtocol.js";

interface DiscoveredHost {
  address: string;
  port: number;
}

const SOCKET_RECONNECT_MS = 1500;
const CONTROL_SEND_INTERVAL_MS = 33;
const STATUS_POLL_INTERVAL_MS = 4000;

let socket: WebSocket | null = null;
let reconnectTimer: number | undefined;
let busy = false;
let discovering = false;
let webSocketSnapshotVersion = 0;

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

async function postAction(path: string, body?: { port?: number; address?: string }): Promise<void> {
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
    typeof value.port === "number" &&
    Number.isInteger(value.port) &&
    value.port >= 1 &&
    value.port <= 65535
  );
}

async function discoverHosts(): Promise<void> {
  if (busy || discovering) return;
  discovering = true;
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
      const first = hosts[0];
      ui.peerAddress.value = first.address;
      ui.joinPort.value = String(first.port);
      for (const host of hosts) {
        const item = document.createElement("button");
        item.type = "button";
        item.className = "discovery-result";
        const address = document.createElement("strong");
        address.textContent = host.address;
        item.append(address, document.createTextNode(` · порт ${host.port}`));
        item.addEventListener("click", () => {
          ui.peerAddress.value = host.address;
          ui.joinPort.value = String(host.port);
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
  sound.loadSettings();
  window.addEventListener("pointerdown", () => sound.unlockAudio(), { capture: true });
  window.addEventListener("keydown", () => sound.unlockAudio(), { capture: true });
  sound.bindControls();
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
