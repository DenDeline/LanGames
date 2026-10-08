import type { PongSnapshot } from "./snapshot.js";
import type { BotCatalogResponse } from "./botCatalog.js";

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
  arenaModeLabel: element("arena-mode-label"),
  networkHint: element("network-hint"),
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
  opponentFallback: element("opponent-fallback"),
  challengeRequest: element("challenge-request"),
  challengePeer: element("challenge-peer"),
  acceptButton: element<HTMLButtonElement>("accept-button"),
  declineButton: element<HTMLButtonElement>("decline-button"),
  liveIndicator: element("live-indicator"),
  liveLabel: element("live-label"),
  quickTab: element<HTMLButtonElement>("tab-host"),
  joinTab: element<HTMLButtonElement>("tab-join"),
  quickPanel: element("quick-panel"),
  joinPanel: element("join-panel"),
  quickForm: element<HTMLFormElement>("quick-form"),
  botForm: element<HTMLFormElement>("bot-form"),
  botSelect: element<HTMLSelectElement>("bot-select"),
  botCatalogStatus: element("bot-catalog-status"),
  botCatalogRetry: element<HTMLButtonElement>("bot-catalog-retry"),
  joinForm: element<HTMLFormElement>("join-form"),
  playerNickname: element<HTMLInputElement>("player-nickname"),
  joinPort: element<HTMLInputElement>("join-port"),
  peerAddress: element<HTMLInputElement>("peer-address"),
  quickButton: element<HTMLButtonElement>("quick-button"),
  botButton: element<HTMLButtonElement>("bot-button"),

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
let wasActive = false;
let botCatalog: BotCatalogResponse | null = null;
let botCatalogRevision = 0;
let rememberedBotId = "";

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

export function setTab(tab: "quick" | "join"): void {
  const isQuick = tab === "quick";
  ui.quickTab.classList.toggle("is-active", isQuick);
  ui.joinTab.classList.toggle("is-active", !isQuick);
  ui.quickTab.setAttribute("aria-selected", String(isQuick));
  ui.joinTab.setAttribute("aria-selected", String(!isQuick));
  ui.quickPanel.hidden = !isQuick;
  ui.joinPanel.hidden = isQuick;
}

export function setBotCatalog(
  catalog: BotCatalogResponse | null,
  error: string | null = null,
): void {
  rememberedBotId = ui.botSelect.value || rememberedBotId;
  botCatalog = catalog;
  botCatalogRevision++;
  ui.botSelect.replaceChildren();
  ui.botCatalogRetry.hidden = error === null;
  if (catalog === null) {
    const option = document.createElement("option");
    option.value = "";
    option.textContent = error === null ? "Загружаем ботов…" : "Боты недоступны";
    ui.botSelect.append(option);
    ui.botSelect.value = "";
    writeText(ui.botCatalogStatus, error ?? "Загружаем список соперников…");
    return;
  }
  for (const bot of catalog.bots) {
    const option = document.createElement("option");
    option.value = bot.id;
    option.textContent = `${bot.name} · ${bot.difficulty}${bot.canPlay ? "" : " · недоступен"}`;
    option.disabled = !bot.canPlay;
    ui.botSelect.append(option);
  }
  const previous = catalog.bots.find((bot) => bot.id === rememberedBotId);
  const preferred = catalog.bots.find((bot) => bot.id === catalog.defaultBotId);
  ui.botSelect.value = (previous ?? preferred ?? catalog.bots.find((bot) => bot.canPlay))?.id ?? "";
  updateBotSelection();
}

export function getSelectedBotId(): string | null {
  return botCatalog?.bots.find((bot) => bot.id === ui.botSelect.value && bot.canPlay)?.id ?? null;
}

export function updateBotSelection(): void {
  rememberedBotId = ui.botSelect.value;
  botCatalogRevision++;
  const selected = botCatalog?.bots.find((bot) => bot.id === rememberedBotId);
  writeText(
    ui.botCatalogStatus,
    selected
      ? `${selected.description}${selected.availabilityReason ? ` ${selected.availabilityReason}` : ""}`
      : botCatalog?.bots.length === 0
        ? "В списке пока нет ботов."
        : "Нет доступных ботов. Вы можете сыграть с другом по сети.",
  );
}

function isLocalBot(snapshot: PongSnapshot): boolean {
  return snapshot.opponentMode === "bot";
}

function botModeLabel(snapshot: PongSnapshot): string {
  return snapshot.effectiveBotName ?? "Соперник";
}

function connectionText(snapshot: PongSnapshot): string {
  if (isLocalBot(snapshot) && snapshot.connection === "connected") return "Игра против бота";
  switch (snapshot.connection) {
    case "waiting":
      return "Ожидаем соперника";
    case "searching":
      return "Ищем соперника";
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
  if (isLocalBot(snapshot)) return "Вы — слева";
  if (snapshot.connection === "searching") return "Поиск соперника";
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
  if (snapshot.connection === "searching") {
    return ["Быстрая игра", "Ищем соперника", "Проверяем игры в локальной сети…"];
  }
  if (snapshot.role === "none") {
    return ["Выберите режим", "Пора сыграть", "Сыграйте с ботом или другом по сети."];
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
    return [
      "Матч завершён",
      winner,
      isLocalBot(snapshot)
        ? "Нажмите «Реванш с ботом», чтобы сыграть ещё раз."
        : "Нажмите «Новый матч», чтобы сыграть ещё раз.",
    ];
  }
  if (snapshot.phase === "countdown") {
    return [
      isLocalBot(snapshot) ? `Против бота · ${botModeLabel(snapshot)}` : "Приготовьтесь",
      String(Math.max(1, Math.ceil(Number(snapshot.countdown) || 0))),
      isLocalBot(snapshot)
        ? "Вы слева. Двигайтесь клавишами W / S или ↑ / ↓."
        : "Ракетка движется клавишами W / S или ↑ / ↓.",
    ];
  }
  if (snapshot.phase === "playing" && snapshot.connection === "connected") return null;
  if (snapshot.connection === "connecting") {
    return ["Подключение", "Подключаемся", "Ждём ответ соперника…"];
  }
  if (snapshot.connection === "incomingChallenge") {
    return ["Вызов на матч", "Примите вызов", "Решите, хотите ли сыграть с этим соперником."];
  }
  if (snapshot.connection === "awaitingAcceptance") {
    return ["Вызов отправлен", "Ждём согласия", "Матч начнётся, когда соперник примет вызов."];
  }
  return ["Ожидание", "Ждём соперника", "Ваша игра доступна игрокам в локальной сети."];
}

function localDisplayName(snapshot: PongSnapshot): string {
  return snapshot.localNickname.trim() || "Вы";
}

function peerDisplayName(snapshot: PongSnapshot): string {
  if (isLocalBot(snapshot)) return `Бот ${botModeLabel(snapshot)}`;
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
  const active = inGame || snapshot.connection === "searching";
  if (active && snapshot.localNickname && ui.playerNickname.value !== snapshot.localNickname)
    ui.playerNickname.value = snapshot.localNickname;
  else if (!active && wasActive && preferredNickname) ui.playerNickname.value = preferredNickname;
  else if (!nicknameWasEdited && !ui.playerNickname.value && snapshot.localNickname)
    ui.playerNickname.value = snapshot.localNickname;
  wasActive = active;
  // Network snapshots arrive much more often than score, ping text, or controls change.
  const uiSignature = JSON.stringify([
    snapshot.role,
    snapshot.requestedBotId,
    snapshot.requestedBotName,
    snapshot.effectiveBotId,
    snapshot.effectiveBotName,
    snapshot.botFallbackReason,
    botCatalogRevision,
    snapshot.opponentMode,
    snapshot.opponentFallbackActive,
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
  const localBot = isLocalBot(snapshot);
  const botFallback = localBot && snapshot.opponentFallbackActive;
  const incomingChallenge =
    snapshot.opponentMode === "lan" &&
    snapshot.role === "host" &&
    snapshot.connection === "incomingChallenge";
  const status = connectionText(snapshot);

  ui.connectionPill.dataset.state = snapshot.connection;
  ui.connectionPill.dataset.mode = snapshot.opponentMode;
  ui.connectionPill.dataset.requestedBotId = snapshot.requestedBotId ?? "";
  ui.connectionPill.dataset.effectiveBotId = snapshot.effectiveBotId ?? "";
  writeText(ui.connectionLabel, status);
  ui.liveIndicator.dataset.state = snapshot.connection;
  writeText(ui.liveLabel, status);
  ui.roleBadge.dataset.role = snapshot.role;
  writeText(ui.roleBadge, roleText(snapshot));
  writeText(ui.roleDetail, roleText(snapshot));
  writeText(
    ui.arenaModeLabel,
    botFallback
      ? `${snapshot.requestedBotName} недоступен · играет ${snapshot.effectiveBotName}`
      : localBot
        ? `Против бота · ${botModeLabel(snapshot)}`
        : snapshot.opponentMode === "lan"
          ? "Сетевая партия"
          : "Выберите режим",
  );
  ui.networkHint.hidden = snapshot.opponentMode !== "lan";
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
    localBot
      ? peerDisplayName(snapshot)
      : snapshot.peerNickname ||
          (snapshot.peerAddress
            ? "Соперник"
            : snapshot.connection === "searching"
              ? "Ищем соперника"
              : inGame
                ? "Ожидаем подключения"
                : "—"),
  );
  ui.pingRow.hidden = !connected || localBot;
  const ping = snapshot.pingMs;
  writeText(
    ui.pingValue,
    ping !== null && ping !== undefined && Number.isFinite(Number(ping)) && Number(ping) >= 0
      ? `${Math.round(Number(ping))} мс`
      : "— мс",
  );
  writeText(
    ui.sessionMessage,
    localBot
      ? snapshot.phase === "gameover"
        ? "Матч с ботом завершён. Возьмите реванш или вернитесь к выбору игры."
        : `Вы управляете левой ракеткой. Справа играет бот ${botModeLabel(snapshot)}.`
      : snapshot.opponentMode === "none"
        ? "Выберите игру с ботом или другом."
        : snapshot.message || status + ".",
  );
  ui.opponentFallback.hidden = !botFallback;
  if (botFallback)
    writeText(
      ui.opponentFallback,
      `Бот «${snapshot.requestedBotName}» недоступен — сейчас играет «${snapshot.effectiveBotName}». ${snapshot.botFallbackReason ?? ""}`,
    );
  ui.challengeRequest.hidden = !incomingChallenge;
  if (incomingChallenge) writeText(ui.challengePeer, peerDisplayName(snapshot));
  ui.acceptButton.disabled = busy || !incomingChallenge;
  ui.declineButton.disabled = busy || !incomingChallenge;
  writeText(ui.leftScore, Math.max(0, Math.trunc(Number(snapshot.leftScore) || 0)));
  writeText(ui.rightScore, Math.max(0, Math.trunc(Number(snapshot.rightScore) || 0)));
  writeText(
    ui.arenaTitle,
    localBot
      ? snapshot.phase === "playing"
        ? "Игра против бота"
        : snapshot.phase === "gameover"
          ? "Матч с ботом окончен"
          : "Бот готовится"
      : snapshot.phase === "playing" && connected
        ? "Матч идёт"
        : snapshot.phase === "gameover"
          ? "Матч окончен"
          : snapshot.connection === "searching"
            ? "Подбираем соперника"
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

  ui.quickButton.disabled = busy || active;
  ui.botButton.disabled = busy || active || getSelectedBotId() === null;
  ui.botSelect.disabled =
    busy || active || botCatalog === null || !botCatalog.bots.some((bot) => bot.canPlay);
  ui.botCatalogRetry.disabled = busy || active;
  writeText(
    ui.quickButton,
    snapshot.connection === "searching"
      ? "Ищем соперника…"
      : busy && !active
        ? "Запускаем…"
        : "Быстрая игра",
  );
  ui.joinButton.disabled = busy || active;
  ui.discoverButton.disabled = busy || discovering || active;
  ui.joinPort.disabled = busy || active;
  ui.playerNickname.disabled = busy || active;
  ui.peerAddress.disabled = busy || active;
  ui.restartButton.disabled = busy || !connected || snapshot.phase !== "gameover";
  writeText(ui.restartButton, localBot ? "Реванш с ботом" : "Новый матч");
  ui.leaveButton.disabled = busy || !active;
  writeText(
    ui.leaveButton,
    snapshot.connection === "searching"
      ? "Отменить поиск"
      : snapshot.connection === "awaitingAcceptance" || snapshot.connection === "connecting"
        ? "Отменить вызов"
        : localBot
          ? "К выбору игры"
          : "Покинуть игру",
  );
  ui.shareBox.hidden =
    snapshot.opponentMode !== "lan" ||
    snapshot.role !== "host" ||
    snapshot.connection !== "waiting";
  writeText(ui.shareNickname, localDisplayName(snapshot));
  writeText(ui.sharePort, snapshot.udpPort > 0 ? snapshot.udpPort : "—");
  if (snapshot.opponentMode === "lan" && snapshot.role === "host") renderAddresses(snapshot);
}
