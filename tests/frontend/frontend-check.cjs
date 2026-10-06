const assert = require("node:assert/strict");

async function main() {
  const [
    { ArenaRenderer },
    { FeedbackController },
    { MotionModel, RIGHT_CONTACT_X },
    { GameSession },
    { parseSnapshot },
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
      replaceChildren() {},
      append() {},
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
  const apply = (changes, source = "websocket") => {
    session.apply(
      {
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

  // HTTP snapshots retain only valid event records, including numeric enum kinds.
  const parsedEvents = parseSnapshot({
    recentEvents: [
      { id: "valid-goal", kind: "goal", tick: 120, x: 0, y: 0.4 },
      { id: "valid-paddle", kind: 2, tick: 121, x: 0.05, y: 0.5 },
      { id: "invalid-kind", kind: 6, tick: 122, x: 0.5, y: 0.5 },
      { id: "invalid-position", kind: "wall", tick: 122, x: -0.01, y: 0.5 },
    ],
  }).events;
  assert.deepEqual(parsedEvents, [
    { id: "valid-goal", kind: "goal", tick: 120, x: 0, y: 0.4 },
    { id: "valid-paddle", kind: "paddle", tick: 121, x: 0.05, y: 0.5 },
  ]);

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

  // The view presents the local player on the side selected by the snapshot.
  const {
    render: renderView,
    saveNickname,
    setTab,
    ui,
  } = await import("../../.artifacts/frontend-test/view.js");
  ui.playerNickname.value = "Мой ник";
  saveNickname();
  renderView(
    { ...session.snapshot, role: "host", localNickname: "Лиса", peerNickname: "Кот" },
    false,
    false,
  );
  assert.equal(ui.leftPlayer.textContent, "Лиса");
  assert.equal(ui.rightPlayer.textContent, "Кот");
  assert.equal(ui.shareNickname.textContent, "Лиса");
  assert.equal(ui.playerNickname.value, "Лиса");
  assert.equal(ui.playerNickname.disabled, true);
  renderView(
    { ...session.snapshot, role: "guest", localNickname: "Кот", peerNickname: "Лиса" },
    false,
    false,
  );
  assert.equal(ui.leftPlayer.textContent, "Лиса");
  assert.equal(ui.rightPlayer.textContent, "Кот");
  assert.equal(ui.playerNickname.value, "Кот");
  renderView(
    {
      ...session.snapshot,
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
      ...session.snapshot,
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
  renderView(
    { ...session.snapshot, role: "guest", connection: "awaitingAcceptance", phase: "waiting" },
    false,
    false,
  );
  assert.equal(ui.challengeRequest.hidden, true);
  assert.equal(ui.leaveButton.textContent, "Отменить вызов");
  assert.equal(ui.overlayTitle.textContent, "Ждём согласия");
  assert.equal(parseSnapshot({ connection: "incomingChallenge" }).connection, "incomingChallenge");
  assert.equal(parseSnapshot({ localNickname: "Лиса", peerNickname: "Кот" }).peerNickname, "Кот");
  assert.equal(
    parseSnapshot({ connection: "awaitingAcceptance" }).connection,
    "awaitingAcceptance",
  );
  renderView({ ...session.snapshot, role: "none", connection: "idle" }, false, false);
  assert.equal(ui.playerNickname.value, "Мой ник");
  assert.equal(ui.playerNickname.disabled, false);
  setTab("join");
  assert.equal(ui.hostPanel.hidden, true);
  assert.equal(ui.joinPanel.hidden, false);

  console.log(
    "Frontend behavior checks passed: motion, prediction, canvas caching, event parsing, score feedback, and replay deduplication.",
  );
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
