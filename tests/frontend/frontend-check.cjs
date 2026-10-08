const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");

async function main() {
  const [
    { ArenaRenderer },
    { FeedbackController },
    { MotionModel, RIGHT_CONTACT_X },
    { GameSession },
    { parseSnapshot, defaultSnapshot },
    { SoundController },
  ] = await Promise.all([
    import("../../.artifacts/frontend-test/arena.js"),
    import("../../.artifacts/frontend-test/feedback.js"),
    import("../../.artifacts/frontend-test/motion.js"),
    import("../../.artifacts/frontend-test/session.js"),
    import("../../.artifacts/frontend-test/snapshot.js"),
    import("../../.artifacts/frontend-test/sound.js"),
  ]);

  let now = 1000;
  let gradients = 0;
  let imageDraws = 0;
  let playedTones = 0;
  let renderCount = 0;
  let trailResets = 0;
  let axis = 0;
  const scoreFlashes = { left: 0, right: 0 };

  Object.defineProperty(globalThis, "performance", {
    configurable: true,
    value: { now: () => now },
  });
  globalThis.setTimeout = () => 0;
  globalThis.clearTimeout = () => {};
  globalThis.window = {
    devicePixelRatio: 2,
    matchMedia: () => ({ matches: false }),
    localStorage: { getItem: () => null, setItem: () => {} },
  };

  function drawingContext() {
    return {
      setTransform() {},
      clearRect() {},
      fillRect() {},
      beginPath() {},
      moveTo() {},
      lineTo() {},
      stroke() {},
      setLineDash() {},
      arc() {},
      save() {},
      restore() {},
      roundRect() {},
      fill() {},
      createLinearGradient() {
        gradients++;
        return { addColorStop() {} };
      },
      createRadialGradient() {
        gradients++;
        return { addColorStop() {} };
      },
      drawImage() {
        imageDraws++;
      },
    };
  }

  function element() {
    const listeners = new Map();
    return {
      dataset: {},
      classList: { toggle() {}, remove() {}, add() {} },
      textContent: "",
      hidden: false,
      value: "47777",
      clientWidth: 960,
      clientHeight: 540,
      width: 960,
      height: 540,
      disabled: false,
      setAttribute() {},
      children: [],
      replacementCount: 0,
      replaceChildren(...children) {
        this.children = [...children];
        this.replacementCount++;
      },
      append(...children) {
        this.children.push(...children);
      },
      addEventListener(type, handler) {
        const handlers = listeners.get(type) ?? [];
        handlers.push(handler);
        listeners.set(type, handlers);
      },
      dispatch(type) {
        for (const handler of listeners.get(type) ?? []) handler({ preventDefault() {} });
      },
      getContext() {
        return drawingContext();
      },
    };
  }

  const elements = new Map();
  globalThis.document = {
    createElement: () => element(),
    getElementById(id) {
      if (!elements.has(id)) elements.set(id, element());
      return elements.get(id);
    },
  };
  const canvas = element();
  const soundToggle = element();
  const volumeRange = element();
  const leftScore = element();
  const rightScore = element();
  elements.set("left-score", leftScore);
  elements.set("right-score", rightScore);
  for (const [side, score] of [
    ["left", leftScore],
    ["right", rightScore],
  ]) {
    score.parentElement = {
      classList: {
        toggle() {},
        remove() {},
        add(name) {
          if (name === "is-scored") scoreFlashes[side]++;
        },
      },
      offsetWidth: 100,
    };
  }

  const sound = new SoundController(soundToggle, volumeRange);
  sound.loadSettings();
  sound.bindControls();
  const feedback = new FeedbackController(leftScore, rightScore, sound);
  const motion = new MotionModel();
  let session;
  const arena = new ArenaRenderer(
    canvas,
    motion,
    () => session.snapshot,
    () => axis,
    () => feedback.pulse,
  );
  session = new GameSession(
    motion,
    feedback,
    () => {
      arena.resetTrail();
      trailResets++;
    },
    () => renderCount++,
  );

  const close = (actual, expected) =>
    assert.ok(Math.abs(actual - expected) < 1e-6, String(actual) + " != " + String(expected));
  const displayedMotion = (at) => motion.displayedMotion(at, session.snapshot);
  const displayedLocalPaddle = (at) => motion.displayedLocalPaddle(at, session.snapshot, axis);
  const noBotIdentity = {
    requestedBotId: null,
    requestedBotName: null,
    effectiveBotId: null,
    effectiveBotName: null,
    opponentFallbackActive: false,
    botFallbackReason: null,
  };
  const botIdentity = (id, name, effectiveId = id, effectiveName = name) => ({
    ...noBotIdentity,
    opponentMode: "bot",
    requestedBotId: id,
    requestedBotName: name,
    effectiveBotId: effectiveId,
    effectiveBotName: effectiveName,
    peerNickname: null,
    peerAddress: null,
    pingMs: null,
    udpPort: 0,
  });
  const httpSnapshot = (changes = {}) => ({
    ...defaultSnapshot,
    localNickname: "Лиса",
    ...changes,
  });
  const apply = (changes, source = "websocket") => {
    session.apply(
      {
        ...httpSnapshot(),
        opponentMode: "lan",
        role: "host",
        connection: "connected",
        phase: "playing",
        roundId: 1,
        tick: 100,
        ballX: 0.2,
        ballY: 0.5,
        ballVx: 0.6,
        ballVy: 0,
        leftY: 0.5,
        rightY: 0.4,
        ...changes,
      },
      source,
    );
  };

  apply({});
  close(displayedMotion(1000).ballX, 0.2);
  close(displayedMotion(1000 + 1000 / 120).ballX, 0.205);
  close(displayedMotion(1040).ballX, 0.22);
  now += 1000 / 60;
  apply({ tick: 101, ballX: 0.21, rightY: 0.4 + 0.85 / 60 });
  close(displayedMotion(now).ballX, 0.21);
  close(displayedMotion(now + 1000 / 120).rightY, 0.4 + (1.5 * 0.85) / 60);
  now += 54;
  apply({ tick: 102, ballX: 0.22, rightY: 0.4 + (2 * 0.85) / 60 });
  close(displayedMotion(now).ballX, 0.22);
  assert.equal(motion.sampleCount, 2);
  now += 20;
  apply({ tick: 102, ballX: 0.22, rightY: 0.4 + (2 * 0.85) / 60 });
  assert.equal(motion.sampleCount, 2);
  close(displayedMotion(now).ballX, 0.232);

  // Same-tick rollback corrections ease in; trajectory changes take effect immediately.
  now += 1000 / 60;
  apply({ tick: 103, ballX: 0.23, rightY: 0.4 + (3 * 0.85) / 60 });
  apply({ tick: 103, ballX: 0.225, rightY: 0.4 + (3 * 0.85) / 60 });
  assert.equal(motion.sampleCount, 2);
  assert.notEqual(motion.correction, null);
  close(displayedMotion(now).ballX, 0.23);
  assert.ok(displayedMotion(now + 15).ballX > 0.23);
  close(displayedMotion(now + 30).ballX, 0.243);
  now += 100;
  apply({ tick: 110, ballX: RIGHT_CONTACT_X - 0.002, ballVx: 0.6 });
  assert.equal(motion.sampleCount, 1);
  assert.equal(motion.correction, null);
  close(displayedMotion(now + 20).ballX, RIGHT_CONTACT_X);
  now += 1000 / 60;
  apply({ tick: 111, ballX: RIGHT_CONTACT_X - 0.004, ballVx: -0.6 });
  assert.equal(motion.correction, null);
  close(displayedMotion(now).ballX, RIGHT_CONTACT_X - 0.004);

  // HTTP responses cannot replace a newer tick; WebSocket rebases clear motion history.
  const beforeStaleHttp = renderCount;
  now += 1000 / 60;
  apply({ tick: 110, ballX: 0.1, ballVx: 0.6 }, "http");
  assert.equal(session.snapshot.tick, 111);
  assert.equal(renderCount, beforeStaleHttp);
  now += 1000 / 60;
  apply({ tick: 300, ballX: 0.3 });
  now += 1000 / 60;
  apply({ tick: 291, ballX: 0.27 });
  assert.equal(session.snapshot.tick, 291);
  assert.equal(motion.sampleCount, 1);
  assert.equal(motion.correction, null);
  now += 1000 / 60;
  apply({ tick: 290, ballX: 0.26 }, "http");
  assert.equal(session.snapshot.tick, 291);
  now += 1000 / 60;
  apply({ tick: 150, ballX: 0.2 });
  assert.equal(session.snapshot.tick, 150);
  assert.equal(motion.sampleCount, 1);
  assert.equal(motion.correction, null);
  assert.ok(trailResets > 0);
  now += 1000 / 60;
  apply({ tick: 151, phase: "countdown", ballX: 0.5, ballVx: 0, leftScore: 1 });
  assert.equal(motion.sampleCount, 1);
  assert.equal(motion.correction, null);

  motion.reset();
  axis = 1;
  const startY = displayedLocalPaddle(1000);
  const heldY = displayedLocalPaddle(1016);
  assert.ok(heldY > startY);
  const heldAgainY = displayedLocalPaddle(1032);
  assert.ok(heldAgainY > heldY);
  let cappedY = heldAgainY;
  for (let frame = 1048; frame <= 1288; frame += 16) {
    const nextY = displayedLocalPaddle(frame);
    assert.ok(nextY >= cappedY);
    cappedY = nextY;
  }
  assert.ok(cappedY <= 0.5 + (2 * 0.85) / 60 + 0.000001);
  axis = 0;
  close(displayedLocalPaddle(1304), cappedY);
  assert.ok(displayedLocalPaddle(1600) < cappedY);

  now += 200;
  apply({ tick: 120, ballX: 0.4, rightY: 0.5 });
  assert.equal(motion.sampleCount, 1);
  arena.resize(960, 540, 2);
  const initialGradients = gradients;
  arena.draw(1400);
  arena.draw(1416);
  arena.resize(960, 540, 2);
  assert.equal(gradients, initialGradients);
  assert.equal(imageDraws, 4);
  arena.resize(1200, 675, 2);
  assert.equal(gradients, initialGradients + 2);

  // HTTP snapshots preserve valid event enums and reject malformed event history.
  const parsedEvents = parseSnapshot(
    httpSnapshot({
      recentEvents: [
        { id: "valid-goal", kind: "goal", tick: 120, x: 0, y: 0.4 },
        { id: "valid-paddle", kind: 2, tick: 121, x: 0.05, y: 0.5 },
      ],
    }),
  ).events;
  assert.deepEqual(parsedEvents, [
    { id: "valid-goal", kind: "goal", tick: 120, x: 0, y: 0.4 },
    { id: "valid-paddle", kind: "paddle", tick: 121, x: 0.05, y: 0.5 },
  ]);
  assert.throws(
    () =>
      parseSnapshot(
        httpSnapshot({
          recentEvents: [{ id: "invalid-position", kind: "wall", tick: 122, x: -0.01, y: 0.5 }],
        }),
      ),
    TypeError,
  );

  // Entering a live match baselines history. Repeats and rollback do not replay it.
  apply({ role: "none", connection: "idle", events: [] });
  const historicPaddle = { id: "20:4:2", kind: "paddle", tick: 1, x: 0.05, y: 0.5 };
  const newGoal = { id: "20:5:4", kind: "goal", tick: 2, x: 1, y: 0.4 };
  apply({ roundId: 20, tick: 1, events: [historicPaddle] });
  assert.equal(feedback.pulse, null);
  assert.equal(scoreFlashes.left, 0);
  assert.equal(feedback.hasSeenEvent(historicPaddle.id), true);
  now += 10;
  apply({ roundId: 20, tick: 2, leftScore: 1, events: [historicPaddle, newGoal] });
  assert.equal(feedback.pulse.kind, "goal");
  assert.equal(feedback.pulse.scorer, "left");
  assert.equal(scoreFlashes.left, 1);
  const pulseAt = feedback.pulse.startedAt;
  now += 10;
  apply({ roundId: 20, tick: 3, leftScore: 1, events: [historicPaddle, newGoal] });
  assert.equal(feedback.pulse.startedAt, pulseAt);
  assert.equal(scoreFlashes.left, 1);
  now += 10;
  apply({ roundId: 20, tick: 3, leftScore: 1, ballX: 0.205, events: [historicPaddle, newGoal] });
  assert.equal(feedback.pulse.startedAt, pulseAt);
  assert.equal(scoreFlashes.left, 1);
  now += 10;
  apply({ roundId: 20, tick: 2, leftScore: 1, events: [historicPaddle, newGoal] });
  assert.equal(feedback.pulse, null);
  assert.equal(scoreFlashes.left, 1);
  now += 10;
  apply({ roundId: 20, tick: 3, leftScore: 1, events: [historicPaddle, newGoal] });
  assert.equal(feedback.pulse, null);
  assert.equal(scoreFlashes.left, 1);

  const laterWall = { id: "20:6:3", kind: "wall", tick: 4, x: 0.4, y: 0.012 };
  apply(
    { roundId: 20, tick: 2, leftScore: 1, events: [historicPaddle, newGoal, laterWall] },
    "http",
  );
  assert.equal(feedback.pulse, null);
  assert.equal(feedback.hasSeenEvent(laterWall.id), false);
  apply({ roundId: 20, tick: 4, leftScore: 1, events: [historicPaddle, newGoal, laterWall] });
  assert.equal(feedback.pulse.kind, "wall");
  assert.equal(scoreFlashes.left, 1);

  const reconnectEvent = { id: "20:7:2", kind: "paddle", tick: 5, x: 0.05, y: 0.4 };
  const beforeReconnectPulse = feedback.pulse.startedAt;
  feedback.markReconnect();
  now += 10;
  apply({ roundId: 20, tick: 5, leftScore: 1, events: [reconnectEvent] });
  assert.equal(feedback.pulse.startedAt, beforeReconnectPulse);
  assert.equal(feedback.hasSeenEvent(reconnectEvent.id), true);
  const afterReconnectEvent = { id: "20:8:3", kind: "wall", tick: 6, x: 0.4, y: 0.012 };
  now += 10;
  apply({ roundId: 20, tick: 6, leftScore: 1, events: [reconnectEvent, afterReconnectEvent] });
  assert.equal(feedback.pulse.kind, "wall");
  assert.equal(feedback.pulse.startedAt, now);

  // The guest scores on the right when the goal coordinate is at the left edge.
  apply({ role: "none", connection: "idle", events: [] });
  apply({ role: "guest", roundId: 21, tick: 1, events: [historicPaddle] });
  const guestGoal = { id: "21:5:4", kind: "goal", tick: 2, x: 0, y: 0.6 };
  apply({ role: "guest", roundId: 21, tick: 2, rightScore: 1, events: [guestGoal] });
  assert.equal(feedback.pulse.scorer, "right");
  assert.equal(scoreFlashes.right, 1);

  // Web Audio is optional. Muting prevents tones without hiding visual feedback.
  class AudioContextStub {
    state = "running";
    currentTime = 0;
    destination = {};
    createGain() {
      return {
        gain: {
          value: 0,
          setTargetAtTime() {},
          setValueAtTime() {},
          exponentialRampToValueAtTime() {},
        },
        connect() {},
        disconnect() {},
      };
    }
    createOscillator() {
      return {
        frequency: { setValueAtTime() {}, exponentialRampToValueAtTime() {} },
        connect() {},
        disconnect() {},
        start() {
          playedTones++;
        },
        stop() {},
      };
    }
  }
  window.AudioContext = AudioContextStub;
  const guestPaddle = { id: "21:6:2", kind: "paddle", tick: 3, x: 0.95, y: 0.6 };
  apply({ role: "guest", roundId: 21, tick: 3, rightScore: 1, events: [guestGoal, guestPaddle] });
  assert.equal(playedTones, 1);
  soundToggle.dispatch("click");
  const mutedWall = { id: "21:7:3", kind: "wall", tick: 4, x: 0.5, y: 0.012 };
  apply({ role: "guest", roundId: 21, tick: 4, rightScore: 1, events: [mutedWall] });
  assert.equal(playedTones, 1);
  assert.equal(feedback.pulse.kind, "wall");
  soundToggle.dispatch("click");
  for (const [kind, tick, tones] of [
    ["serve", 5, 2],
    ["wall", 6, 3],
    ["match", 7, 5],
  ]) {
    apply({
      role: "guest",
      roundId: 21,
      tick,
      rightScore: 1,
      events: [{ id: "21:" + tick + ":" + kind, kind, tick, x: 0.5, y: 0.5 }],
    });
    assert.equal(feedback.pulse.kind, kind);
    assert.equal(playedTones, tones);
  }

  // The host hears one alert per incoming challenge, not on every snapshot.
  const applyChallenge = (connection, role = "host", source = "websocket") =>
    apply({ role, connection, phase: "waiting", roundId: 22, tick: 0, events: [] }, source);
  applyChallenge("waiting");
  applyChallenge("incomingChallenge");
  assert.equal(playedTones, 7);
  applyChallenge("incomingChallenge", "host", "http");
  applyChallenge("incomingChallenge");
  assert.equal(playedTones, 7);
  applyChallenge("waiting");
  applyChallenge("incomingChallenge", "guest");
  assert.equal(playedTones, 7);
  applyChallenge("waiting");
  soundToggle.dispatch("click");
  applyChallenge("incomingChallenge");
  assert.equal(playedTones, 7);
  soundToggle.dispatch("click");
  applyChallenge("waiting");
  applyChallenge("incomingChallenge");
  assert.equal(playedTones, 9);

  // A configured fallback is an opponent change inside the same session.
  // A new game event on that frame must still play, rather than be baselined.
  apply({ role: "none", connection: "idle", events: [] });
  const botServe = { id: "23:1:1", kind: "serve", tick: 1, x: 0.5, y: 0.5 };
  const fallbackGoal = { id: "23:2:4", kind: "goal", tick: 2, x: 1, y: 0.4 };
  apply({
    role: "host",
    connection: "connected",
    ...botIdentity("predictive", "Прогноз"),
    opponentMode: "bot",
    roundId: 23,
    tick: 1,
    events: [botServe],
  });
  now += 10;
  apply({
    role: "host",
    connection: "connected",
    ...botIdentity("predictive", "Прогноз"),
    opponentMode: "bot",
    opponentFallbackActive: true,
    effectiveBotId: "calm",
    effectiveBotName: "Тихий",
    botFallbackReason: "Выбранный бот перестал отвечать.",
    roundId: 23,
    tick: 2,
    leftScore: 1,
    events: [botServe, fallbackGoal],
  });
  assert.equal(feedback.pulse.kind, "goal");
  assert.equal(feedback.hasSeenEvent(fallbackGoal.id), true);

  // The view presents the local player on the side selected by the snapshot.
  const {
    render: renderView,
    saveNickname,
    setTab,
    setBotCatalog,
    updateBotSelection,
    ui,
  } = await import("../../.artifacts/frontend-test/view.js");
  const catalogEntry = (id, name, extra = {}) => ({
    id,
    name,
    description: `Описание ${name}`,
    style: "Стиль",
    difficulty: "Тренировка",
    category: "Боты",
    order: 10,
    glyph: null,
    enabled: true,
    fallbackBotId: null,
    availability: "ready",
    availabilityReason: null,
    canPlay: true,
    ...extra,
  });
  const browserCatalog = {
    version: 8,
    defaultBotId: "calm",
    bots: [
      catalogEntry("calm", "Тихий"),
      catalogEntry("predictive", "Прогноз", { order: 20, availability: "notChecked" }),
      catalogEntry("config-only-opponent", "Дополнительный бот из конфигурации", { order: 30 }),
    ],
  };
  setBotCatalog(browserCatalog);
  const lanSnapshot = {
    ...session.snapshot,
    ...noBotIdentity,
    opponentMode: "lan",
    opponentFallbackActive: false,
  };
  ui.playerNickname.value = "Мой ник";
  saveNickname();
  renderView(
    {
      ...lanSnapshot,
      role: "host",
      connection: "waiting",
      udpPort: 59123,
      localNickname: "Лиса",
      peerNickname: "Кот",
    },
    false,
    false,
  );
  assert.equal(ui.leftPlayer.textContent, "Лиса");
  assert.equal(ui.rightPlayer.textContent, "Кот");
  assert.equal(ui.shareNickname.textContent, "Лиса");
  assert.equal(ui.sharePort.textContent, "59123");
  assert.equal(ui.shareBox.hidden, false);
  assert.equal(ui.arenaModeLabel.textContent, "Сетевая партия");
  assert.equal(ui.networkHint.hidden, false);
  assert.equal(ui.playerNickname.value, "Лиса");
  assert.equal(ui.playerNickname.disabled, true);
  renderView(
    { ...lanSnapshot, role: "guest", localNickname: "Кот", peerNickname: "Лиса" },
    false,
    false,
  );
  assert.equal(ui.leftPlayer.textContent, "Лиса");
  assert.equal(ui.rightPlayer.textContent, "Кот");
  assert.equal(ui.playerNickname.value, "Кот");
  renderView(
    {
      ...lanSnapshot,
      role: "host",
      connection: "incomingChallenge",
      peerAddress: "192.168.1.43:47777",
      localNickname: "Лиса",
      peerNickname: "Кот",
      phase: "waiting",
    },
    false,
    false,
  );
  assert.equal(ui.challengeRequest.hidden, false);
  assert.equal(ui.challengePeer.textContent, "Кот");
  assert.equal(ui.peerDetail.textContent, "Кот");
  assert.equal(ui.acceptButton.disabled, false);
  assert.equal(ui.declineButton.disabled, false);
  assert.equal(ui.overlayTitle.textContent, "Примите вызов");
  renderView(
    {
      ...lanSnapshot,
      role: "host",
      connection: "connected",
      phase: "gameover",
      leftScore: 5,
      rightScore: 3,
      localNickname: "Лиса",
      peerNickname: "Кот",
    },
    false,
    false,
  );
  assert.equal(ui.overlayTitle.textContent, "Победа: Лиса");
  assert.equal(ui.shareBox.hidden, true);
  renderView(
    {
      ...session.snapshot,
      ...botIdentity("calm", "Тихий"),
      opponentMode: "bot",
      opponentFallbackActive: false,
      role: "host",
      connection: "connected",
      phase: "playing",
      localNickname: "Лиса",
      peerNickname: "",
      peerAddress: null,
      pingMs: 8,
      udpPort: 59123,
    },
    false,
    false,
  );
  assert.equal(ui.connectionLabel.textContent, "Игра против бота");
  assert.equal(ui.connectionPill.dataset.mode, "bot");
  assert.equal(ui.arenaModeLabel.textContent, "Против бота · Тихий");
  assert.equal(ui.leftPlayer.textContent, "Лиса");
  assert.equal(ui.rightPlayer.textContent, "Бот Тихий");
  assert.equal(ui.roleDetail.textContent, "Вы — слева");
  assert.equal(ui.peerDetail.textContent, "Бот Тихий");
  assert.equal(ui.pingRow.hidden, true);
  assert.equal(ui.networkHint.hidden, true);
  assert.equal(ui.shareBox.hidden, true);
  assert.equal(ui.challengeRequest.hidden, true);
  assert.equal(ui.botButton.disabled, true);
  assert.equal(ui.opponentFallback.hidden, true);
  assert.equal(ui.leaveButton.textContent, "К выбору игры");
  assert.equal(ui.restartButton.disabled, true);
  assert.equal(ui.overlay.hidden, true);
  renderView(
    {
      ...session.snapshot,
      ...botIdentity("calm", "Тихий"),
      opponentMode: "bot",
      opponentFallbackActive: false,
      role: "host",
      connection: "connected",
      phase: "gameover",
      leftScore: 5,
      rightScore: 3,
      localNickname: "Лиса",
      peerNickname: "",
    },
    false,
    false,
  );
  assert.equal(ui.overlayTitle.textContent, "Победа: Лиса");
  assert.equal(ui.restartButton.textContent, "Реванш с ботом");
  assert.equal(ui.restartButton.disabled, false);
  assert.equal(ui.shareBox.hidden, true);
  renderView(
    {
      ...session.snapshot,
      ...botIdentity("predictive", "Прогноз"),
      opponentMode: "bot",
      role: "host",
      connection: "connected",
      phase: "playing",
      localNickname: "Лиса",
      message: "Игра против Hard.",
    },
    false,
    false,
  );
  assert.equal(ui.arenaModeLabel.textContent, "Против бота · Прогноз");
  assert.equal(ui.rightPlayer.textContent, "Бот Прогноз");
  assert.equal(ui.peerDetail.textContent, "Бот Прогноз");
  assert.equal(ui.connectionPill.dataset.requestedBotId, "predictive");
  assert.equal(ui.opponentFallback.hidden, true);
  renderView(
    {
      ...session.snapshot,
      ...botIdentity("predictive", "Прогноз"),
      opponentMode: "bot",
      opponentFallbackActive: true,
      effectiveBotId: "calm",
      effectiveBotName: "Тихий",
      botFallbackReason: "Выбранный бот перестал отвечать.",
      role: "host",
      connection: "connected",
      phase: "playing",
      localNickname: "Лиса",
      message: "Сообщение игрового цикла",
    },
    false,
    false,
  );
  assert.equal(ui.arenaModeLabel.textContent, "Прогноз недоступен · играет Тихий");
  assert.equal(ui.rightPlayer.textContent, "Бот Тихий");
  assert.equal(ui.opponentFallback.hidden, false);
  assert.match(ui.opponentFallback.textContent, /Прогноз.*недоступен.*Тихий/);
  renderView(
    {
      ...session.snapshot,
      ...botIdentity("predictive", "Прогноз"),
      opponentMode: "bot",
      opponentFallbackActive: true,
      effectiveBotId: "calm",
      effectiveBotName: "Тихий",
      botFallbackReason: "Выбранный бот перестал отвечать.",
      role: "host",
      connection: "connected",
      phase: "gameover",
      localNickname: "Лиса",
      message: "Матч завершён",
    },
    false,
    false,
  );
  assert.equal(ui.opponentFallback.hidden, false);
  assert.equal(ui.restartButton.textContent, "Реванш с ботом");
  renderView(
    { ...lanSnapshot, role: "guest", connection: "awaitingAcceptance", phase: "waiting" },
    false,
    false,
  );
  assert.equal(ui.challengeRequest.hidden, true);
  assert.equal(ui.leaveButton.textContent, "Отменить вызов");
  assert.equal(ui.overlayTitle.textContent, "Ждём согласия");
  assert.equal(
    parseSnapshot(httpSnapshot({ connection: "incomingChallenge" })).connection,
    "incomingChallenge",
  );
  assert.equal(
    parseSnapshot(httpSnapshot({ localNickname: "Лиса", peerNickname: "Кот" })).peerNickname,
    "Кот",
  );
  assert.equal(
    parseSnapshot(httpSnapshot({ connection: "awaitingAcceptance" })).connection,
    "awaitingAcceptance",
  );
  assert.equal(parseSnapshot(httpSnapshot({ connection: "searching" })).connection, "searching");
  renderView(
    { ...lanSnapshot, role: "none", connection: "searching", message: "", udpPort: 0 },
    false,
    false,
  );
  assert.equal(ui.overlayTitle.textContent, "Ищем соперника");
  assert.equal(ui.quickButton.disabled, true);
  assert.equal(ui.joinButton.disabled, true);
  assert.equal(ui.leaveButton.disabled, false);
  assert.equal(ui.leaveButton.textContent, "Отменить поиск");
  renderView(
    {
      ...session.snapshot,
      role: "none",
      ...noBotIdentity,
      opponentMode: "none",
      opponentFallbackActive: false,
      connection: "idle",
      message: "Нажмите «Быстрая игра» или подключитесь к другу.",
    },
    false,
    false,
  );
  assert.equal(ui.playerNickname.value, "Мой ник");
  assert.equal(ui.playerNickname.disabled, false);
  assert.equal(ui.quickButton.disabled, false);
  assert.equal(ui.botButton.disabled, false);
  assert.equal(ui.opponentFallback.hidden, true);
  assert.equal(ui.networkHint.hidden, true);
  assert.equal(ui.arenaModeLabel.textContent, "Выберите режим");
  assert.equal(ui.sessionMessage.textContent, "Выберите игру с ботом или другом.");
  setTab("join");
  assert.equal(ui.quickPanel.hidden, true);
  assert.equal(ui.joinPanel.hidden, false);

  // Paddle rollback corrections are visual only and keep both sides continuous.
  const visualMotion = new MotionModel();
  let visualResets = 0;
  const visualSession = new GameSession(
    visualMotion,
    { clearPulse() {}, process() {} },
    () => visualResets++,
    () => {},
  );
  const visualApply = (changes, at, source = "websocket") => {
    now = at;
    visualSession.apply(
      {
        ...httpSnapshot(),
        opponentMode: "lan",
        role: "host",
        connection: "connected",
        phase: "playing",
        roundId: 44,
        tick: 10,
        ballX: 0.3,
        ballY: 0.5,
        ballVx: 0.6,
        ballVy: 0,
        leftY: 0.5,
        rightY: 0.4,
        ...changes,
      },
      source,
    );
  };
  const visualRemote = (at) => visualMotion.displayedMotion(at, visualSession.snapshot).rightY;
  const visualLeft = (at) => visualMotion.displayedMotion(at, visualSession.snapshot).leftY;
  const visualLocal = (at, direction) =>
    visualMotion.displayedLocalPaddle(at, visualSession.snapshot, direction);

  visualApply({}, 2000);
  visualLocal(2000, 0);
  visualApply({ rightY: 0.6 }, 2000);
  close(visualSession.snapshot.rightY, 0.6);
  close(visualRemote(2000), 0.4);
  close(visualRemote(2040), 0.5);
  close(visualRemote(2080), 0.6);

  visualApply({ tick: 11, rightY: 0.6 }, 2100);
  visualApply({ tick: 12, rightY: 0.75 }, 2116);
  close(visualRemote(2116), 0.6);
  const beforeSecondCorrection = visualRemote(2136);
  visualApply({ tick: 12, rightY: 0.8 }, 2136);
  close(visualRemote(2136), beforeSecondCorrection);
  const beforeOrdinaryUpdate = visualRemote(2152);
  visualApply({ tick: 13, rightY: 0.8 + 0.85 / 60 }, 2152);
  close(visualRemote(2152), beforeOrdinaryUpdate);
  assert.ok(visualRemote(2176) > beforeSecondCorrection);
  close(visualRemote(2216), visualSession.snapshot.rightY + (2 * 0.85) / 60);
  visualApply({ tick: 14, rightY: 0.8 + (2 * 0.85) / 60 }, 2240);
  close(visualRemote(2240), visualSession.snapshot.rightY);

  // A guest clock rebase clears ball history but preserves the visible paddles.
  visualApply({ role: "guest", tick: 100, leftY: 0.7, rightY: 0.7 }, 2300);
  const remoteBeforeRebase = visualLeft(2316);
  const localBeforeRebase = visualLocal(2316, 0);
  const resetsBeforeRebase = visualResets;
  visualApply({ role: "guest", tick: 90, leftY: 0.25, rightY: 0.2 }, 2316);
  assert.equal(visualResets, resetsBeforeRebase + 1);
  assert.equal(visualMotion.sampleCount, 1);
  assert.equal(visualMotion.correction, null);
  close(visualLeft(2316), remoteBeforeRebase);
  close(visualLocal(2316, 0), localBeforeRebase);
  close(visualLeft(2396), 0.25);

  // Starting a new round resets the presentation instead of easing across it.
  visualApply({ roundId: 45, tick: 1, leftY: 0.5, rightY: 0.5 }, 2400);
  close(visualRemote(2400), 0.5);
  close(visualLocal(2400, 0), 0.5);

  // The local paddle remains responsive while a large correction is displayed.
  visualApply({ roundId: 46, tick: 1 }, 2500);
  close(visualLocal(2500, 1), 0.5);
  const beforeLocalCorrection = visualLocal(2516, 1);
  assert.ok(beforeLocalCorrection > 0.5);
  visualApply({ roundId: 46, tick: 1, leftY: 0.75 }, 2516);
  close(visualSession.snapshot.leftY, 0.75);
  close(visualLocal(2516, 1), beforeLocalCorrection);
  const beforeLocalUpdate = visualLocal(2532, 1);
  visualApply({ roundId: 46, tick: 2, leftY: 0.75 + 0.85 / 60 }, 2532);
  close(visualLocal(2532, 1), beforeLocalUpdate);
  const duringLocalCorrection = visualLocal(2556, 1);
  assert.ok(duringLocalCorrection > beforeLocalCorrection);
  assert.ok(duringLocalCorrection < 0.75);
  close(visualLocal(2596, 1), visualSession.snapshot.leftY);
  assert.ok(visualLocal(2612, 1) > visualSession.snapshot.leftY);

  // A direction reversal and a gap in snapshots must not teleport the remote paddle.
  visualApply({ roundId: 47, tick: 10, rightY: 0.5 + 0.85 / 60 }, 2700);
  visualApply({ roundId: 47, tick: 11, rightY: 0.5 }, 2716);
  const beforeReversal = visualRemote(2732);
  visualApply({ roundId: 47, tick: 12, rightY: 0.5 + 0.85 / 60 }, 2732);
  close(visualRemote(2732), beforeReversal);
  visualApply({ roundId: 48, tick: 10, rightY: 0.2 }, 2800);
  visualApply({ roundId: 48, tick: 20, rightY: 0.3 }, 2950);
  close(visualRemote(2950), 0.2);
  close(visualRemote(2990), 0.25);
  close(visualRemote(3030), 0.3);

  visualApply({ roundId: 49, tick: 10, leftY: 0.5 }, 3100);
  visualLocal(3100, -1);
  const beforeOppositeCorrection = visualLocal(3116, -1);
  visualApply({ roundId: 49, tick: 10, leftY: 0.75 }, 3116);
  close(visualLocal(3116, -1), beforeOppositeCorrection);
  assert.ok(visualLocal(3132, -1) < beforeOppositeCorrection + 0.05);
  assert.ok(visualLocal(3196, -1) > 0.7);

  // A mode switch resets prediction even when the round and tick are unchanged.
  visualApply({ ...noBotIdentity, opponentMode: "lan", roundId: 50, tick: 10 }, 3200);
  visualApply({ ...noBotIdentity, opponentMode: "lan", roundId: 50, tick: 11 }, 3216);
  assert.equal(visualMotion.sampleCount, 2);
  const resetsBeforeModeSwitch = visualResets;
  visualApply(
    { ...botIdentity("calm", "Тихий"), opponentMode: "bot", roundId: 50, tick: 11 },
    3232,
  );
  assert.equal(visualSession.snapshot.opponentMode, "bot");
  assert.equal(visualResets, resetsBeforeModeSwitch + 1);
  assert.equal(visualMotion.sampleCount, 1);

  // Mid-rally configured fallback keeps the requested session identity and motion history.
  visualApply(
    { ...botIdentity("predictive", "Прогноз"), opponentMode: "bot", roundId: 51, tick: 10 },
    3250,
  );
  visualApply(
    { ...botIdentity("predictive", "Прогноз"), opponentMode: "bot", roundId: 51, tick: 11 },
    3266,
  );
  const resetsBeforeFallback = visualResets;
  visualApply(
    {
      ...botIdentity("predictive", "Прогноз"),
      opponentMode: "bot",
      opponentFallbackActive: true,
      effectiveBotId: "calm",
      effectiveBotName: "Тихий",
      botFallbackReason: "Выбранный бот перестал отвечать.",
      roundId: 51,
      tick: 12,
    },
    3282,
  );
  assert.equal(visualResets, resetsBeforeFallback);
  assert.equal(visualMotion.sampleCount, 2);

  // One selection form submits configured IDs, including an extra config-only entry.
  const html = readFileSync(`${__dirname}/../../frontend/index.html`, "utf8");
  assert.match(
    html,
    /<form[^>]+id="bot-form"[\s\S]*?<select[^>]+id="bot-select"[\s\S]*?<button[^>]+id="bot-button"/,
  );
  assert.doesNotMatch(html, /hard-form|hard-button|Simple|Hard/);
  assert.match(html, /id="opponent-fallback"/);
  assert.match(html, /id="tab-host"/);
  assert.match(html, /id="tab-join"/);
  window.addEventListener = () => {};
  document.addEventListener = () => {};
  globalThis.setInterval = () => 0;
  globalThis.requestAnimationFrame = () => 0;
  globalThis.location = { protocol: "http:", host: "127.0.0.1:47777" };
  globalThis.WebSocket = class {
    static CONNECTING = 0;
    static OPEN = 1;
    static instances = [];
    readyState = 0;
    listeners = new Map();
    constructor() {
      WebSocket.instances.push(this);
    }
    addEventListener(type, listener) {
      this.listeners.set(type, listener);
    }
    dispatch(type, event) {
      this.listeners.get(type)?.(event);
    }
    close() {
      this.readyState = 3;
    }
  };
  const requests = [];
  const idleSnapshot = httpSnapshot({
    role: "none",
    opponentMode: "none",
    connection: "idle",
    phase: "waiting",
    roundId: 60,
    tick: 0,
  });
  let catalogPayload = browserCatalog;
  let catalogError = false;
  let rejectSelection = false;
  let rejectRestart = false;
  let invalidActionResponse = false;
  let holdNextCatalog = false;
  let releaseOldCatalog = null;
  let serverSnapshot = idleSnapshot;
  globalThis.fetch = async (path, options) => {
    requests.push({ path, options });
    if (path === "/api/bots") {
      if (catalogError) throw new Error("Network failure with sensitive detail");
      const payload = catalogPayload;
      if (holdNextCatalog) {
        holdNextCatalog = false;
        await new Promise((resolve) => {
          releaseOldCatalog = resolve;
        });
      }
      return { ok: true, text: async () => JSON.stringify(payload) };
    }
    if (path === "/api/local-opponent" && rejectSelection) {
      return {
        ok: false,
        status: 400,
        text: async () => JSON.stringify({ error: "Выбранный бот недоступен." }),
      };
    }
    if (path === "/api/restart" && rejectRestart) {
      serverSnapshot = idleSnapshot;
      pushSnapshot(serverSnapshot); // Backend stops before the failed rematch response arrives.
      return {
        ok: false,
        status: 400,
        text: async () => JSON.stringify({ error: "Бот не смог продолжить игру." }),
      };
    }
    if (path === "/api/quick" && invalidActionResponse) {
      return { ok: true, text: async () => JSON.stringify({ version: 7, role: "host" }) };
    }
    const selected =
      path === "/api/local-opponent"
        ? browserCatalog.bots.find((bot) => bot.id === JSON.parse(options.body).botId)
        : null;
    const data = selected
      ? {
          ...idleSnapshot,
          ...botIdentity(selected.id, selected.name),
          role: "host",
          connection: "connected",
          phase: "countdown",
          localNickname: "Browser Tester",
          roundId: 61,
        }
      : idleSnapshot;
    if (path === "/api/local-opponent" || path === "/api/leave") serverSnapshot = data;
    return {
      ok: true,
      text: async () => JSON.stringify(path === "/api/status" ? serverSnapshot : data),
    };
  };
  const { encode } = await import("@msgpack/msgpack");
  const pushSnapshot = (next) => {
    const payload = [
      8,
      ["none", "host", "guest"].indexOf(next.role),
      [
        "idle",
        "waiting",
        "connecting",
        "connected",
        "incomingChallenge",
        "awaitingAcceptance",
        "searching",
      ].indexOf(next.connection),
      next.message,
      next.udpPort,
      next.localAddresses,
      next.peerAddress,
      next.leftY,
      next.rightY,
      next.ballX,
      next.ballY,
      next.ballVx,
      next.ballVy,
      next.leftScore,
      next.rightScore,
      ["waiting", "countdown", "playing", "gameover"].indexOf(next.phase),
      next.countdown,
      next.tick,
      next.roundId,
      next.pingMs,
      [],
      next.localNickname,
      next.peerNickname,
      ["none", "lan", "bot"].indexOf(next.opponentMode),
      next.requestedBotId,
      next.requestedBotName,
      next.effectiveBotId,
      next.effectiveBotName,
      next.opponentFallbackActive,
      next.botFallbackReason,
    ];
    WebSocket.instances
      .at(-1)
      .dispatch("message", { data: Uint8Array.from(encode(payload)).buffer });
  };
  const catalogRequests = () => requests.filter((request) => request.path === "/api/bots").length;
  const flush = () => new Promise((resolve) => setImmediate(resolve));
  const { startGame } = await import("../../.artifacts/frontend-test/game.js");
  ui.playerNickname.value = "Browser Tester";
  startGame();
  await flush();
  assert.equal(ui.botSelect.value, "calm");
  assert.equal(ui.botSelect.children.length, 3);
  assert.equal(ui.botButton.disabled, false);
  ui.botForm.dispatch("submit");
  await flush();
  const botRequest = requests.find((request) => request.path === "/api/local-opponent");
  assert.ok(botRequest);
  assert.equal(botRequest.options.method, "POST");
  assert.deepEqual(JSON.parse(botRequest.options.body), {
    nickname: "Browser Tester",
    botId: "calm",
  });
  assert.equal(ui.arenaModeLabel.textContent, "Против бота · Тихий");
  assert.equal(ui.rightPlayer.textContent, "Бот Тихий");
  assert.equal(ui.botSelect.disabled, true);
  assert.ok(requests.filter((request) => request.path === "/api/bots").length >= 2);

  ui.leaveButton.dispatch("click");
  await flush();
  ui.botSelect.value = "config-only-opponent";
  ui.botSelect.dispatch("change");
  ui.botForm.dispatch("submit");
  await flush();
  const extraRequest = requests.find(
    (request) =>
      request.path === "/api/local-opponent" &&
      JSON.parse(request.options.body).botId === "config-only-opponent",
  );
  assert.ok(extraRequest);
  assert.deepEqual(JSON.parse(extraRequest.options.body), {
    nickname: "Browser Tester",
    botId: "config-only-opponent",
  });
  assert.equal(ui.rightPlayer.textContent, "Бот Дополнительный бот из конфигурации");
  assert.equal(ui.botSelect.value, "config-only-opponent");
  ui.leaveButton.dispatch("click");
  await flush();
  assert.equal(ui.botSelect.value, "config-only-opponent");

  // A failed start refreshes truthful availability and keeps the requested selection visible.
  ui.botSelect.value = "predictive";
  ui.botSelect.dispatch("change");
  catalogPayload = {
    ...browserCatalog,
    bots: browserCatalog.bots.map((bot) =>
      bot.id === "predictive"
        ? {
            ...bot,
            availability: "unavailable",
            availabilityReason: "Выбранный бот недоступен.",
            canPlay: false,
          }
        : bot,
    ),
  };
  rejectSelection = true;
  ui.botForm.dispatch("submit");
  await flush();
  assert.equal(ui.botSelect.value, "predictive");
  assert.equal(ui.botButton.disabled, true);
  assert.match(ui.botCatalogStatus.textContent, /Выбранный бот недоступен/);
  assert.equal(
    ui.botSelect.children.find((option) => option.value === "predictive").disabled,
    true,
  );

  // A terminal strategy failure refreshes once, preserving the failed selection. A late
  // catalog response started before that failure cannot restore stale playable status.
  rejectSelection = false;
  catalogPayload = browserCatalog;
  setBotCatalog(browserCatalog);
  ui.botSelect.value = "calm";
  ui.botSelect.dispatch("change");
  holdNextCatalog = true;
  ui.botForm.dispatch("submit");
  await flush();
  assert.equal(typeof releaseOldCatalog, "function");
  const beforeTerminalFailure = catalogRequests();
  catalogPayload = {
    ...browserCatalog,
    bots: browserCatalog.bots.map((bot) =>
      bot.id === "calm"
        ? {
            ...bot,
            availability: "unavailable",
            availabilityReason: "Выбранный бот перестал отвечать.",
            canPlay: false,
          }
        : bot,
    ),
  };
  serverSnapshot = idleSnapshot;
  pushSnapshot(serverSnapshot);
  await flush();
  assert.equal(catalogRequests(), beforeTerminalFailure + 1);
  assert.equal(ui.botSelect.value, "calm");
  assert.equal(ui.botButton.disabled, true);
  assert.match(ui.botCatalogStatus.textContent, /перестал отвечать/);
  pushSnapshot({ ...idleSnapshot, tick: 1 });
  pushSnapshot({ ...idleSnapshot, tick: 2 });
  await flush();
  assert.equal(catalogRequests(), beforeTerminalFailure + 1);
  releaseOldCatalog();
  await flush();
  assert.equal(ui.botButton.disabled, true);
  assert.equal(ui.botSelect.children.find((option) => option.value === "calm").disabled, true);

  // An unsolicited runtime fallback refreshes primary availability once and keeps both
  // names/reason visible. Later ticks preserve the selection without catalog fetches.
  catalogPayload = browserCatalog;
  setBotCatalog(browserCatalog);
  ui.botSelect.value = "predictive";
  ui.botSelect.dispatch("change");
  ui.botForm.dispatch("submit");
  await flush();
  const beforeFallback = catalogRequests();
  catalogPayload = {
    ...browserCatalog,
    bots: browserCatalog.bots.map((bot) =>
      bot.id === "predictive"
        ? {
            ...bot,
            availability: "unavailable",
            availabilityReason: "Выбранный бот перестал отвечать.",
            fallbackBotId: "calm",
            canPlay: true,
          }
        : bot,
    ),
  };
  serverSnapshot = {
    ...serverSnapshot,
    ...botIdentity("predictive", "Прогноз", "calm", "Тихий"),
    opponentFallbackActive: true,
    botFallbackReason: "Выбранный бот перестал отвечать.",
    phase: "playing",
    tick: 10,
  };
  pushSnapshot(serverSnapshot);
  await flush();
  assert.equal(catalogRequests(), beforeFallback + 1);
  assert.equal(ui.botSelect.value, "predictive");
  assert.match(ui.botCatalogStatus.textContent, /перестал отвечать/);
  assert.match(ui.opponentFallback.textContent, /Прогноз.*Тихий.*перестал отвечать/);
  pushSnapshot({ ...serverSnapshot, tick: 11 });
  await flush();
  assert.equal(catalogRequests(), beforeFallback + 1);
  ui.leaveButton.dispatch("click");
  await flush();

  // A failed rematch that stops during the POST refreshes once rather than relying on
  // a later rejected Play attempt; its snapshot transition shares the POST's refresh.
  catalogPayload = browserCatalog;
  setBotCatalog(browserCatalog);
  ui.botSelect.value = "calm";
  ui.botSelect.dispatch("change");
  ui.botForm.dispatch("submit");
  await flush();
  serverSnapshot = { ...serverSnapshot, phase: "gameover", tick: 100 };
  pushSnapshot(serverSnapshot);
  const beforeRematchFailure = catalogRequests();
  catalogPayload = {
    ...browserCatalog,
    bots: browserCatalog.bots.map((bot) =>
      bot.id === "calm"
        ? {
            ...bot,
            availability: "unavailable",
            availabilityReason: "Бот не смог продолжить игру.",
            canPlay: false,
          }
        : bot,
    ),
  };
  rejectRestart = true;
  ui.restartButton.dispatch("click");
  await flush();
  assert.equal(catalogRequests(), beforeRematchFailure + 1);
  assert.equal(ui.botSelect.value, "calm");
  assert.equal(ui.botButton.disabled, true);
  assert.match(ui.botCatalogStatus.textContent, /не смог продолжить игру/);
  assert.equal(ui.leaveButton.disabled, true);

  // Parser errors returned by a POST remain Russian in the visible toast.
  invalidActionResponse = true;
  ui.quickForm.dispatch("submit");
  await flush();
  assert.match(ui.toast.textContent, /некорректный ответ/);
  assert.doesNotMatch(ui.toast.textContent, /Unsupported|Invalid snapshot/);

  // Fetch errors offer retry in Russian, and configured HTML-looking metadata remains text.
  catalogError = true;
  ui.botCatalogRetry.dispatch("click");
  await flush();
  assert.equal(ui.botCatalogRetry.hidden, false);
  assert.match(ui.botCatalogStatus.textContent, /Не удалось загрузить ботов/);
  assert.doesNotMatch(ui.botCatalogStatus.textContent, /Network|sensitive/);
  catalogError = false;
  catalogPayload = {
    ...browserCatalog,
    bots: browserCatalog.bots.map((bot) =>
      bot.id === "config-only-opponent" ? { ...bot, name: "<b>Настроенный бот</b>" } : bot,
    ),
  };
  ui.botCatalogRetry.dispatch("click");
  await flush();
  assert.equal(ui.botCatalogRetry.hidden, true);
  assert.match(
    ui.botSelect.children.find((option) => option.value === "config-only-opponent").textContent,
    /<b>Настроенный бот<\/b>/,
  );
  const replacements = ui.botSelect.replacementCount;
  renderView({ ...idleSnapshot, tick: 1 }, false, false);
  renderView({ ...idleSnapshot, tick: 2 }, false, false);
  assert.equal(ui.botSelect.replacementCount, replacements);
  assert.equal(ui.botSelect.value, "calm");
  setBotCatalog({ version: 8, defaultBotId: "calm", bots: [] });
  renderView(idleSnapshot, false, false);
  assert.equal(ui.botButton.disabled, true);
  assert.match(ui.botCatalogStatus.textContent, /нет ботов/);
  assert.equal(ui.quickButton.disabled, false);
  console.log(
    "Frontend behavior checks passed: motion, prediction, events, bot identities/fallback, LAN, catalog refresh, and generic selection.",
  );
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
