import { DEFAULT_UDP_PORT, type PongSnapshot } from "./snapshot.js";

function element<T extends HTMLElement>(id: string): T {
  const found = document.getElementById(id);
  if (!found) throw new Error(`Missing required element: #${id}`);
  return found as T;
}

export const ui = {
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
  soundToggle: element<HTMLButtonElement>("sound-toggle"),
  volumeRange: element<HTMLInputElement>("volume-range"),
  toast: element("toast"),
};

const TOAST_DURATION_MS = 5000;
let lastAddressKey: string | null = null;
let lastUiSignature: string | null = null;
let toastTimer: number | undefined;

export function getPort(input: HTMLInputElement): number | null {
  const port = Number(input.value);
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    input.setCustomValidity("Введите порт от 1 до 65535.");
    input.reportValidity();
    input.setCustomValidity("");
    return null;
  }
  return port;
}

export function showToast(message: string): void {
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

export function setTab(tab: "host" | "join"): void {
  const isHost = tab === "host";
  ui.hostTab.classList.toggle("is-active", isHost);
  ui.joinTab.classList.toggle("is-active", !isHost);
  ui.hostTab.setAttribute("aria-selected", String(isHost));
  ui.joinTab.setAttribute("aria-selected", String(!isHost));
  ui.hostPanel.hidden = !isHost;
  ui.joinPanel.hidden = isHost;
}

function connectionText(snapshot: PongSnapshot): string {
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

function roleText(snapshot: PongSnapshot): string {
  switch (snapshot.role) {
    case "host":
      return "Первый игрок";
    case "guest":
      return "Второй игрок";
    default:
      return "Не в игре";
  }
}

function overlayContent(snapshot: PongSnapshot): [string, string, string] | null {
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

function renderAddresses(snapshot: PongSnapshot): void {
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

export function render(snapshot: PongSnapshot, busy: boolean, discovering: boolean): void {
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
  const status = connectionText(snapshot);

  ui.connectionPill.dataset.state = snapshot.connection;
  writeText(ui.connectionLabel, status);
  ui.liveIndicator.dataset.state = snapshot.connection;
  writeText(ui.liveLabel, status);
  ui.roleBadge.dataset.role = snapshot.role;
  writeText(ui.roleBadge, roleText(snapshot));
  writeText(ui.roleDetail, roleText(snapshot));
  writeText(
    ui.leftPlayer,
    snapshot.role === "host" ? "Вы" : snapshot.role === "guest" ? "Соперник" : "Игрок 1",
  );
  writeText(
    ui.rightPlayer,
    snapshot.role === "guest" ? "Вы" : snapshot.role === "host" ? "Соперник" : "Игрок 2",
  );
  ui.leftScore.parentElement?.classList.toggle("is-local", snapshot.role === "host");
  ui.rightScore.parentElement?.classList.toggle("is-local", snapshot.role === "guest");
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

  const overlay = overlayContent(snapshot);
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
  if (snapshot.role === "host") renderAddresses(snapshot);
}
