import type { PongSnapshot } from "./snapshot.js";
import type { BotCatalogResponse } from "./botCatalog.js";
import { BotCatalogView } from "./botCatalogView.js";

function element<T extends HTMLElement>(id: string): T {
  const found = document.getElementById(id);
  if (!found) throw new Error(`Missing required element: #${id}`);
  return found as T;
}

export const ui = {
  arenaPanel: element("game-arena"),
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
  rematchSideHint: element("rematch-side-hint"),
  opponentFallback: element("opponent-fallback"),
  challengeRequest: element("challenge-request"),
  challengePeer: element("challenge-peer"),
  acceptButton: element<HTMLButtonElement>("accept-button"),
  declineButton: element<HTMLButtonElement>("decline-button"),
  liveIndicator: element("live-indicator"),
  liveLabel: element("live-label"),
  gameMode: element<HTMLSelectElement>("game-mode"),
  playerSetup: element("player-setup"),
  botSideField: element("bot-side-field"),
  botSide: element<HTMLSelectElement>("bot-side"),
  lanSideHint: element("lan-side-hint"),
  botPanel: element("bot-panel"),
  botPicker: element<HTMLDialogElement>("bot-picker"),
  botPickerTitle: element("bot-picker-title"),
  botPickerOpen: element<HTMLButtonElement>("bot-picker-open"),
  botPickerClose: element<HTMLButtonElement>("bot-picker-close"),
  botPickerDone: element<HTMLButtonElement>("bot-picker-done"),
  botSummaryAvatar: element("bot-summary-avatar"),
  botSummaryName: element("bot-summary-name"),
  botSummaryDifficulty: element("bot-summary-difficulty"),
  botSummaryAvailability: element("bot-summary-availability"),
  botSummaryStatus: element("bot-summary-status"),
  quickPanel: element("quick-panel"),
  joinPanel: element("join-panel"),
  quickForm: element<HTMLFormElement>("quick-form"),
  botForm: element<HTMLFormElement>("bot-form"),
  botCatalog: element<HTMLFieldSetElement>("bot-catalog"),
  botCatalogGroups: element("bot-catalog-groups"),
  botProfile: element("bot-profile"),
  botProfileAvatar: element("bot-profile-avatar"),
  botProfileName: element("bot-profile-name"),
  botProfileDescription: element("bot-profile-description"),
  botProfileDifficulty: element("bot-profile-difficulty"),
  botProfileStyle: element("bot-profile-style"),
  botProfileAvailability: element("bot-profile-availability"),
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
let botCatalogRevision = 0;
type GameMode = "bot" | "quick" | "join";
let idleMode: GameMode = "bot";
let displayedMode: GameMode = "bot";
let quickGameRequested = false;
let quickGameAccepted = false;
let quickGameObserved = false;
let setupLocked = false;
const catalogView = new BotCatalogView({
  dialog: ui.botPicker,
  opener: ui.botPickerOpen,
  title: ui.botPickerTitle,
  summaryAvatar: ui.botSummaryAvatar,
  summaryName: ui.botSummaryName,
  summaryDifficulty: ui.botSummaryDifficulty,
  summaryAvailability: ui.botSummaryAvailability,
  summaryStatus: ui.botSummaryStatus,
  fieldset: ui.botCatalog,
  groups: ui.botCatalogGroups,
  status: ui.botCatalogStatus,
  retry: ui.botCatalogRetry,
  profile: ui.botProfile,
  avatar: ui.botProfileAvatar,
  name: ui.botProfileName,
  description: ui.botProfileDescription,
  difficulty: ui.botProfileDifficulty,
  style: ui.botProfileStyle,
  availability: ui.botProfileAvailability,
  play: ui.botButton,
});

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

export function getInitialSidePreference(): "left" | "right" | "random" {
  const preference = ui.botSide.value;
  return preference === "right" || preference === "random" ? preference : "left";
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

export function isSetupLocked(): boolean {
  return setupLocked;
}

export function setGameMode(mode: string): void {
  if (!setupLocked && (mode === "bot" || mode === "quick" || mode === "join")) {
    idleMode = mode;
    catalogView.closePicker(false);
  }
  presentMode(setupLocked ? displayedMode : idleMode);
}

function presentMode(mode: GameMode): void {
  displayedMode = mode;
  ui.gameMode.value = mode;
  ui.botPanel.hidden = mode !== "bot";
  ui.botSideField.hidden = mode !== "bot";
  ui.lanSideHint.hidden = mode === "bot";
  ui.playerSetup.dataset.mode = mode === "bot" ? "bot" : "lan";
  ui.quickPanel.hidden = mode !== "quick";
  ui.joinPanel.hidden = mode !== "join";
}

export function noteQuickGameStart(): void {
  quickGameRequested = true;
  quickGameAccepted = false;
  quickGameObserved = false;
}

export function acceptQuickGameStart(): void {
  quickGameAccepted = true;
}

export function clearQuickGameIntent(): void {
  quickGameRequested = false;
  quickGameAccepted = false;
  quickGameObserved = false;
}

export function openBotPicker(): void {
  if (displayedMode === "bot" && !setupLocked) catalogView.openPicker();
}

export function closeBotPicker(restoreFocus = true): void {
  catalogView.closePicker(restoreFocus);
}

export function setBotCatalog(
  catalog: BotCatalogResponse | null,
  error: string | null = null,
): void {
  catalogView.setCatalog(catalog, error);
  botCatalogRevision++;
}

export function getSelectedBotId(): string | null {
  return catalogView.getSelectedBotId();
}

export function updateBotSelection(): void {
  catalogView.updateSelection();
  botCatalogRevision++;
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
  if (snapshot.localSide !== null)
    return snapshot.localSide === "left" ? "Вы — слева" : "Вы — справа";
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

function rematchSideText(snapshot: PongSnapshot): string {
  return snapshot.localSide === "left" ? "справа" : "слева";
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
    if (!snapshot.canRematch) {
      return ["Результат матча", "Проверяем результат", "Ждём подтверждения от соперника."];
    }
    const leftName =
      snapshot.localSide === "left" ? localDisplayName(snapshot) : peerDisplayName(snapshot);
    const rightName =
      snapshot.localSide === "right" ? localDisplayName(snapshot) : peerDisplayName(snapshot);
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
        ? `Реванш с «${botModeLabel(snapshot)}»: вы будете ${rematchSideText(snapshot)}.`
        : `Новый матч: вы будете ${rematchSideText(snapshot)}.`,
    ];
  }
  if (snapshot.phase === "countdown") {
    return [
      isLocalBot(snapshot) ? `Против бота · ${botModeLabel(snapshot)}` : "Приготовьтесь",
      String(Math.max(1, Math.ceil(Number(snapshot.countdown) || 0))),
      isLocalBot(snapshot)
        ? `Вы ${snapshot.localSide === "left" ? "слева" : "справа"}. Двигайтесь клавишами W / S или ↑ / ↓.`
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
  setupLocked = busy || active;
  catalogView.setDisabled(setupLocked);
  if (quickGameRequested && active && snapshot.opponentMode !== "bot") quickGameObserved = true;
  // An accepted quick reply may follow a newer pre-action idle frame. Keep its
  // origin until the resulting session is observed, without applying stale data.
  if (!active && !busy && (quickGameObserved || !quickGameAccepted)) clearQuickGameIntent();
  presentMode(
    !active
      ? idleMode
      : snapshot.opponentMode === "bot"
        ? "bot"
        : snapshot.role === "guest" && !quickGameRequested
          ? "join"
          : "quick",
  );
  if (active && snapshot.localNickname && ui.playerNickname.value !== snapshot.localNickname)
    ui.playerNickname.value = snapshot.localNickname;
  else if (!active && wasActive && preferredNickname) ui.playerNickname.value = preferredNickname;
  else if (!nicknameWasEdited && !ui.playerNickname.value && snapshot.localNickname)
    ui.playerNickname.value = snapshot.localNickname;
  wasActive = active;
  // Network snapshots arrive much more often than score, ping text, or controls change.
  const uiSignature = JSON.stringify([
    snapshot.role,
    snapshot.localSide,
    snapshot.canRematch,
    snapshot.requestedBotId,
    snapshot.requestedBotName,
    snapshot.effectiveBotId,
    snapshot.effectiveBotName,
    snapshot.botFallbackReason,
    botCatalogRevision,
    displayedMode,
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
  ui.roleBadge.dataset.side = snapshot.localSide ?? "";
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
    snapshot.localSide === "left"
      ? localDisplayName(snapshot)
      : snapshot.localSide === "right"
        ? peerDisplayName(snapshot)
        : "Игрок 1",
  );
  writeText(
    ui.rightPlayer,
    snapshot.localSide === "right"
      ? localDisplayName(snapshot)
      : snapshot.localSide === "left"
        ? peerDisplayName(snapshot)
        : "Игрок 2",
  );
  ui.leftScore.parentElement?.classList.toggle("is-local", snapshot.localSide === "left");
  ui.rightScore.parentElement?.classList.toggle("is-local", snapshot.localSide === "right");
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
    snapshot.phase === "gameover" && connected && !snapshot.canRematch
      ? "Ждём подтверждения результата от соперника."
      : localBot
        ? snapshot.phase === "gameover"
          ? `Матч с «${botModeLabel(snapshot)}» завершён. Возьмите реванш или вернитесь к выбору игры.`
          : `Вы управляете ${snapshot.localSide === "left" ? "левой" : "правой"} ракеткой. ${snapshot.localSide === "left" ? "Справа" : "Слева"} играет бот ${botModeLabel(snapshot)}.`
        : snapshot.opponentMode === "none"
          ? snapshot.message || "Выберите игру с ботом или другом."
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
          ? snapshot.canRematch
            ? "Матч окончен"
            : "Проверяем результат"
          : snapshot.connection === "searching"
            ? "Подбираем соперника"
            : inGame
              ? "Ожидание игры"
              : "Пора сыграть",
  );

  const overlay = overlayContent(snapshot);
  ui.overlay.dataset.phase = snapshot.phase;
  ui.overlay.hidden = overlay === null;
  if (overlay !== null) {
    writeText(ui.overlayKicker, overlay[0]);
    writeText(ui.overlayTitle, overlay[1]);
    writeText(ui.overlayDescription, overlay[2]);
  }

  ui.quickButton.disabled = busy || active;
  ui.botButton.disabled = busy || active || getSelectedBotId() === null;
  ui.botCatalogRetry.disabled = busy || active;
  ui.gameMode.disabled = setupLocked;
  ui.botSide.disabled = setupLocked;
  ui.clearSelectedHost.disabled = setupLocked;
  for (const button of ui.discoveryResults.querySelectorAll<HTMLButtonElement>("button"))
    button.disabled = setupLocked;
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
  ui.restartButton.disabled = busy || !connected || !snapshot.canRematch;
  writeText(ui.restartButton, localBot ? `Реванш с «${botModeLabel(snapshot)}»` : "Новый матч");
  ui.rematchSideHint.hidden = !connected || snapshot.phase !== "gameover" || !snapshot.canRematch;
  if (!ui.rematchSideHint.hidden)
    writeText(ui.rematchSideHint, `В новом матче вы будете ${rematchSideText(snapshot)}.`);
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
