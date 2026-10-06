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
  challengeRequest: element("challenge-request"),
  challengePeer: element("challenge-peer"),
  acceptButton: element<HTMLButtonElement>("accept-button"),
  declineButton: element<HTMLButtonElement>("decline-button"),
  liveIndicator: element("live-indicator"),
  liveLabel: element("live-label"),
  hostTab: element<HTMLButtonElement>("tab-host"),
  joinTab: element<HTMLButtonElement>("tab-join"),
  hostPanel: element("host-panel"),
  joinPanel: element("join-panel"),
  hostForm: element<HTMLFormElement>("host-form"),
  joinForm: element<HTMLFormElement>("join-form"),
  playerNickname: element<HTMLInputElement>("player-nickname"),
  hostPort: element<HTMLInputElement>("host-port"),
  joinPort: element<HTMLInputElement>("join-port"),
  peerAddress: element<HTMLInputElement>("peer-address"),
  hostButton: element<HTMLButtonElement>("host-button"),
  joinButton: element<HTMLButtonElement>("join-button"),
  discoverButton: element<HTMLButtonElement>("discover-button"),
  discoveryResults: element("discovery-results"),
  selectedHost: element("selected-host"),
  selectedHostName: element("selected-host-name"),
  clearSelectedHost: element<HTMLButtonElement>("clear-selected-host"),
  shareBox: element("share-box"),
  shareNickname: element("share-nickname"),
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
const NICKNAME_STORAGE_KEY = "lanpong-nickname";
const MAX_NICKNAME_LENGTH = 24;
let lastAddressKey: string | null = null;
let lastUiSignature: string | null = null;
let toastTimer: number | undefined;
let nicknameWasEdited = false;
let preferredNickname: string | null = null;
let wasInGame = false;

function validNickname(value: string): boolean {
  return (
    value.length > 0 &&
    value.length <= MAX_NICKNAME_LENGTH &&
    !/[\u0000-\u001f\u007f-\u009f]/.test(value)
  );
}

export function loadNickname(): void {
  try {
    const stored = window.localStorage.getItem(NICKNAME_STORAGE_KEY)?.trim();
    if (stored && validNickname(stored)) {
      ui.playerNickname.value = stored;
      preferredNickname = stored;
      nicknameWasEdited = true;
    }
  } catch {
    // Private browsing can make localStorage unavailable.
  }
}

export function saveNickname(): void {
  nicknameWasEdited = true;
  preferredNickname = ui.playerNickname.value.trim();
  try {
    window.localStorage.setItem(NICKNAME_STORAGE_KEY, preferredNickname);
  } catch {
    // The nickname still works for this page when storage is unavailable.
  }
}

export function getNickname(): string | null {
  const nickname = ui.playerNickname.value.trim();
  if (!validNickname(nickname)) {
    ui.playerNickname.setCustomValidity("Введите ник до 24 символов без управляющих знаков.");
    ui.playerNickname.reportValidity();
    ui.playerNickname.setCustomValidity("");
    return null;
  }
  ui.playerNickname.value = nickname;
  saveNickname();
  return nickname;
}

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
    case "incomingChallenge":
      return "Вызов получен";
    case "awaitingAcceptance":
      return "Ждём согласия";
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
    return ["Локальный матч", "Создайте игру", "Или найдите игру друга."];
  }
  if (snapshot.connection === "disconnected") {
    return ["Сеть", "Связь потеряна", "Проверьте сеть или покиньте игру, чтобы начать заново."];
  }
  if (snapshot.phase === "gameover") {
    const leftName =
      snapshot.role === "host" ? localDisplayName(snapshot) : peerDisplayName(snapshot);
    const rightName =
      snapshot.role === "guest" ? localDisplayName(snapshot) : peerDisplayName(snapshot);
    const winner =
      snapshot.leftScore === snapshot.rightScore
        ? "Ничья"
        : snapshot.leftScore > snapshot.rightScore
          ? `Победа: ${leftName}`
          : `Победа: ${rightName}`;
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
    return ["Подключение", "Подключаемся", "Ждём ответ друга…"];
  }
  if (snapshot.connection === "incomingChallenge") {
    return ["Вызов на матч", "Примите вызов", "Решите, хотите ли сыграть с этим соперником."];
  }
  if (snapshot.connection === "awaitingAcceptance") {
    return ["Вызов отправлен", "Ждём согласия", "Матч начнётся, когда друг примет вызов."];
  }
  return ["Ожидание", "Ждём друга", "Друг найдёт вашу игру по нику в сети."];
}

function localDisplayName(snapshot: PongSnapshot): string {
  return snapshot.localNickname.trim() || "Вы";
}

function peerDisplayName(snapshot: PongSnapshot): string {
  return snapshot.peerNickname?.trim() || "Соперник";
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
  const inGame = snapshot.role === "host" || snapshot.role === "guest";
  if (inGame && snapshot.localNickname && ui.playerNickname.value !== snapshot.localNickname)
    ui.playerNickname.value = snapshot.localNickname;
  else if (!inGame && wasInGame && preferredNickname) ui.playerNickname.value = preferredNickname;
  else if (!nicknameWasEdited && !ui.playerNickname.value && snapshot.localNickname)
    ui.playerNickname.value = snapshot.localNickname;
  wasInGame = inGame;
  // Network snapshots arrive much more often than score, ping text, or controls change.
  const uiSignature = JSON.stringify([
    snapshot.role,
    snapshot.connection,
    snapshot.message,
    snapshot.udpPort,
    snapshot.role === "host" ? snapshot.localAddresses : null,
    snapshot.peerAddress,
    snapshot.localNickname,
    snapshot.peerNickname,
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
  const connected = snapshot.connection === "connected";
  const incomingChallenge = snapshot.role === "host" && snapshot.connection === "incomingChallenge";
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
    snapshot.role === "host"
      ? localDisplayName(snapshot)
      : snapshot.role === "guest"
        ? peerDisplayName(snapshot)
        : "Игрок 1",
  );
  writeText(
    ui.rightPlayer,
    snapshot.role === "guest"
      ? localDisplayName(snapshot)
      : snapshot.role === "host"
        ? peerDisplayName(snapshot)
        : "Игрок 2",
  );
  ui.leftScore.parentElement?.classList.toggle("is-local", snapshot.role === "host");
  ui.rightScore.parentElement?.classList.toggle("is-local", snapshot.role === "guest");
  writeText(
    ui.peerDetail,
    snapshot.peerNickname ||
      (snapshot.peerAddress ? "Соперник" : inGame ? "Ожидаем подключения" : "—"),
  );
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
  ui.challengeRequest.hidden = !incomingChallenge;
  if (incomingChallenge) writeText(ui.challengePeer, peerDisplayName(snapshot));
  ui.acceptButton.disabled = busy || !incomingChallenge;
  ui.declineButton.disabled = busy || !incomingChallenge;
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
  ui.playerNickname.disabled = busy || inGame;
  ui.peerAddress.disabled = busy || inGame;
  ui.restartButton.disabled = busy || !connected || snapshot.phase !== "gameover";
  ui.leaveButton.disabled = busy || !inGame;
  writeText(
    ui.leaveButton,
    snapshot.connection === "awaitingAcceptance" || snapshot.connection === "connecting"
      ? "Отменить вызов"
      : "Покинуть игру",
  );
  ui.shareBox.hidden = snapshot.role !== "host";
  writeText(ui.shareNickname, localDisplayName(snapshot));
  writeText(ui.sharePort, snapshot.udpPort || Number(ui.hostPort.value) || DEFAULT_UDP_PORT);
  if (snapshot.role === "host") renderAddresses(snapshot);
}
