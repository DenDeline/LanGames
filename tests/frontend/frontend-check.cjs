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
  let reducedMotion = false;
  const scrollCalls = [];
  const scoreFlashes = { left: 0, right: 0 };
  const localScoreMarks = { left: false, right: false };

  Object.defineProperty(globalThis, "performance", {
    configurable: true,
    value: { now: () => now },
  });
  globalThis.setTimeout = () => 0;
  globalThis.clearTimeout = () => {};
  globalThis.window = {
    devicePixelRatio: 2,
    matchMedia: () => ({ matches: reducedMotion }),
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

  const matchesSelector = (node, selector) => {
    const tag = selector.match(/^[a-z][a-z0-9]*/i)?.[0];
    if (tag && node.tagName !== tag.toUpperCase()) return false;
    const id = selector.match(/#([\w-]+)/)?.[1];
    if (id && node.id !== id) return false;
    for (const [, name] of selector.matchAll(/\.([\w-]+)/g))
      if (!node.classList.contains(name)) return false;
    for (const [, name, value] of selector.matchAll(/\[([\w-]+)(?:=['"]?([^'"\]]+)['"]?)?\]/g)) {
      const actual = name.startsWith("data-")
        ? (node.dataset[
            name.slice(5).replace(/-([a-z])/g, (_, character) => character.toUpperCase())
          ] ?? node.getAttribute(name))
        : (node[name] ?? node.getAttribute(name));
      if (
        value === undefined
          ? actual === null || actual === undefined || actual === false
          : String(actual) !== value
      )
        return false;
    }
    return !selector.includes(":checked") || node.checked;
  };

  class DomElement {
    constructor(tag = "div") {
      Object.assign(this, makeElement(tag));
    }
    set className(value) {
      this._className = value;
      this.classList.add(...value.split(/\s+/).filter(Boolean));
    }
    get className() {
      return this._className ?? "";
    }
  }
  class InputElement extends DomElement {}
  globalThis.Element = DomElement;
  globalThis.HTMLElement = DomElement;
  globalThis.HTMLInputElement = InputElement;

  function makeElement(tag = "div") {
    const listeners = new Map();
    const attributes = new Map();
    const classes = new Set();
    return {
      tagName: tag.toUpperCase(),
      id: "",
      type: "",
      name: "",
      checked: false,
      open: false,
      dataset: {},
      classList: {
        contains: (name) => classes.has(name),
        toggle(name, force = !classes.has(name)) {
          if (force) classes.add(name);
          else classes.delete(name);
          return force;
        },
        remove: (...names) => names.forEach((name) => classes.delete(name)),
        add: (...names) => names.forEach((name) => classes.add(name)),
      },
      textContent: "",
      hidden: false,
      value: "47777",
      clientWidth: 960,
      clientHeight: 540,
      width: 960,
      height: 540,
      disabled: false,
      validationMessages: [],
      setCustomValidity(message) {
        this.validationMessages.push(message);
      },
      reportValidity() {
        return false;
      },
      showModalCalls: 0,
      closeCalls: 0,
      showModal() {
        assert.equal(this.tagName, "DIALOG");
        this.open = true;
        this.showModalCalls++;
      },
      close() {
        if (!this.open) return;
        this.open = false;
        this.closeCalls++;
        if (this.contains(document.activeElement)) document.activeElement = null;
        // Native close dispatch is queued after close(), never synchronous.
        queueMicrotask(() => this.dispatch("close"));
      },
      setAttribute(name, value) {
        attributes.set(name, String(value));
      },
      getAttribute(name) {
        return attributes.get(name) ?? null;
      },
      removeAttribute(name) {
        attributes.delete(name);
      },
      children: [],
      parentElement: null,
      replacementCount: 0,
      contains(node) {
        return this === node || this.children.some((child) => child.contains(node));
      },
      replaceChildren(...children) {
        for (const child of this.children) {
          if (child.contains(document.activeElement)) document.activeElement = null;
          child.parentElement = null;
        }
        this.children = [...children];
        for (const child of children) child.parentElement = this;
        this.replacementCount++;
      },
      append(...children) {
        for (const child of children) child.parentElement = this;
        this.children.push(...children);
      },
      querySelectorAll(selector) {
        const found = [];
        for (const child of this.children) {
          if (matchesSelector(child, selector)) found.push(child);
          found.push(...child.querySelectorAll(selector));
        }
        return found;
      },
      querySelector(selector) {
        return this.querySelectorAll(selector)[0] ?? null;
      },
      closest(selector) {
        if (selector.split(",").some((part) => matchesSelector(this, part.trim()))) return this;
        return this.parentElement?.closest(selector) ?? null;
      },
      focusCalls: [],
      scrollIntoView(options) {
        scrollCalls.push({ element: this, options });
      },
      focus(options) {
        for (let node = this; node; node = node.parentElement)
          if (
            node.hidden ||
            (node.tagName === "DIALOG" && !node.open) ||
            (node.disabled && (node === this || node.tagName === "FIELDSET"))
          )
            return;
        this.focusCalls.push(options);
        document.activeElement = this;
        document.dispatch?.("focusin", { target: this });
      },
      addEventListener(type, handler) {
        const handlers = listeners.get(type) ?? [];
        handlers.push(handler);
        listeners.set(type, handlers);
      },
      dispatch(type, values = {}) {
        const event = {
          target: this,
          preventDefault() {
            this.defaultPrevented = true;
          },
          ...values,
        };
        this.dispatchEventToListeners(type, event);
        if (type === "cancel" && this.open && !event.defaultPrevented) this.close();
        return event;
      },
      dispatchEventToListeners(type, event) {
        for (const handler of listeners.get(type) ?? []) handler(event);
        this.parentElement?.dispatchEventToListeners(type, event);
      },
      getContext() {
        return drawingContext();
      },
    };
  }

  function element(tag = "div") {
    return tag === "input" ? new InputElement(tag) : new DomElement(tag);
  }

  const elements = new Map();
  globalThis.document = {
    activeElement: null,
    createElement: (tag) => element(tag),
    getElementById(id) {
      for (const root of elements.values()) {
        const found = root.querySelector(`#${id}`);
        if (found) return found;
      }
      if (!elements.has(id)) {
        const node = element();
        node.id = id;
        elements.set(id, node);
      }
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
        toggle(name, enabled) {
          if (name === "is-local") localScoreMarks[side] = enabled;
        },
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
    localSide: "left",
    requestedBotId: id,
    requestedBotName: name,
    effectiveBotId: effectiveId,
    effectiveBotName: effectiveName,
    peerNickname: null,
    peerAddress: null,
    pingMs: null,
    udpPort: 0,
  });
  let fixtureMatchCounter = 0;
  const nextMatchId = () => (++fixtureMatchCounter).toString(16).padStart(32, "0");
  const fixtureSourceId = "abcdefabcdefabcdefabcdefabcdefab";
  let fixtureCaptureSequence = 0;
  const captureSnapshot = (value) => ({
    ...value,
    sourceId: value.sourceId ?? fixtureSourceId,
    snapshotSequence: ++fixtureCaptureSequence,
  });
  const httpSnapshot = (changes = {}) =>
    captureSnapshot({
      ...defaultSnapshot,
      localNickname: "Лиса",
      ...changes,
    });
  const apply = (changes, source = "websocket") => {
    const role = changes.role ?? "host";
    const mode = changes.opponentMode ?? "lan";
    const assigned = (changes.connection ?? "connected") === "connected";
    const matchId = assigned
      ? (changes.matchId ??
        (session.snapshot.matchId !== null &&
        session.snapshot.role === role &&
        session.snapshot.opponentMode === mode &&
        session.snapshot.roundId === (changes.roundId ?? 1) &&
        session.snapshot.requestedBotId === (changes.requestedBotId ?? null)
          ? session.snapshot.matchId
          : nextMatchId()))
      : null;
    session.apply(
      {
        ...httpSnapshot(),
        opponentMode: "lan",
        role: "host",
        localSide: changes.localSide ?? (changes.role === "guest" ? "right" : "left"),
        connection: "connected",
        phase: "playing",
        canRematch: false,
        roundId: 1,
        tick: 100,
        ballX: 0.2,
        ballY: 0.5,
        ballVx: 0.6,
        ballVy: 0,
        leftY: 0.5,
        rightY: 0.4,
        ...changes,
        matchId,
        ...(changes.connection && changes.connection !== "connected" ? { localSide: null } : {}),
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
  apply({ tick: 151, phase: "countdown", canRematch: false, ballX: 0.5, ballVx: 0, leftScore: 1 });
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

  // Goal sounds follow physical ownership for either network role.
  const ownedSounds = [];
  const ownershipFeedback = new FeedbackController(leftScore, rightScore, {
    playChallengeSound() {},
    playFeedbackSound(...args) {
      ownedSounds.push(args);
    },
  });
  for (const role of ["host", "guest"]) {
    for (const localSide of ["left", "right"]) {
      const base = httpSnapshot({
        role,
        localSide,
        matchId: nextMatchId(),
        connection: "connected",
        opponentMode: "lan",
        phase: "playing",
        canRematch: false,
        roundId: 1,
      });
      ownershipFeedback.process(base, defaultSnapshot);
      const ownGoal = {
        id: `${role}-${localSide}-own`,
        kind: "goal",
        tick: 1,
        x: localSide === "left" ? 1 : 0,
        y: 0.5,
      };
      ownershipFeedback.process({ ...base, events: [ownGoal] }, base);
      assert.deepEqual(ownedSounds.at(-1), ["goal", true, false]);
      const peerGoal = {
        ...ownGoal,
        id: `${role}-${localSide}-peer`,
        x: localSide === "left" ? 0 : 1,
      };
      ownershipFeedback.process(
        { ...base, phase: "gameover", canRematch: true, events: [ownGoal, peerGoal] },
        base,
      );
      assert.deepEqual(ownedSounds.at(-1), ["goal", false, true]);
    }
  }

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
    apply(
      { role, connection, phase: "waiting", canRematch: false, roundId: 22, tick: 0, events: [] },
      source,
    );
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
    setGameMode,
    openBotPicker,
    closeBotPicker,
    isSetupLocked,
    setBotCatalog,
    getSelectedBotId,
    updateBotSelection,
    ui,
  } = await import("../../.artifacts/frontend-test/view.js");
  // Preserve the static ancestors so native hidden/closed/disabled focus behavior
  // and dialog-descendant input ownership are represented in this fixture.
  ui.gameMode.tagName = "SELECT";
  ui.botPicker.tagName = "DIALOG";
  for (const input of [ui.playerNickname, ui.peerAddress, ui.joinPort, ui.volumeRange])
    input.tagName = "INPUT";
  ui.botCatalog.tagName = "FIELDSET";
  ui.botCatalog.append(ui.botCatalogGroups);
  ui.botPicker.append(
    ui.botPickerTitle,
    ui.botPickerClose,
    ui.botCatalog,
    ui.botCatalogStatus,
    ui.botCatalogRetry,
    ui.botProfile,
    ui.botPickerDone,
  );
  ui.botProfile.append(
    ui.botProfileAvatar,
    ui.botProfileName,
    ui.botProfileDescription,
    ui.botProfileDifficulty,
    ui.botProfileStyle,
    ui.botProfileAvailability,
  );
  ui.botForm.append(
    ui.botSummaryAvatar,
    ui.botSummaryName,
    ui.botSummaryDifficulty,
    ui.botSummaryAvailability,
    ui.botSummaryStatus,
    ui.botPickerOpen,
    ui.botButton,
  );
  ui.botPanel.append(ui.botForm);
  ui.quickForm.append(ui.quickButton);
  ui.quickPanel.append(ui.quickForm);
  ui.selectedHost.append(ui.selectedHostName, ui.clearSelectedHost);
  ui.joinForm.append(
    ui.peerAddress,
    ui.joinPort,
    ui.joinButton,
    ui.discoverButton,
    ui.discoveryResults,
    ui.selectedHost,
  );
  ui.joinPanel.append(ui.joinForm);
  const radios = () => ui.botCatalog.querySelectorAll('input[name="botId"]');
  const radio = (id) => radios().find((choice) => choice.value === id);
  const checkedId = () => radios().find((choice) => choice.checked)?.value ?? null;
  const card = (id) => radio(id)?.closest(".bot-card");
  const cardText = (id, selector) => card(id)?.querySelector(selector)?.textContent;
  const checkBot = (id) => {
    const choice = radio(id);
    assert.ok(choice, `Missing configured radio: ${id}`);
    assert.equal(choice.disabled, false, `Unavailable radio must not be selected: ${id}`);
    for (const other of radios()) other.checked = other === choice;
    return choice;
  };
  const selectBot = (id) => {
    if (!ui.botPicker.open) openBotPicker();
    assert.equal(ui.botPicker.open, true, "Native catalog selection requires an open picker");
    assert.equal(
      ui.botCatalog.disabled,
      false,
      "Native selection requires an enabled catalog fieldset",
    );
    const choice = checkBot(id);
    choice.focus();
    return choice.dispatch("change");
  };
  const submitBot = () => {
    if (ui.botPicker.open) closeBotPicker();
    return ui.botForm.dispatch("submit");
  };
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
    version: 9,
    defaultBotId: "calm",
    bots: [
      catalogEntry("calm", "Тихий"),
      catalogEntry("predictive", "Прогноз", { order: 20, availability: "notChecked" }),
      catalogEntry("config-only-opponent", "Дополнительный бот из конфигурации", { order: 30 }),
    ],
  };
  setBotCatalog(browserCatalog);
  const selectionIdle = httpSnapshot({
    ...noBotIdentity,
    opponentMode: "none",
    role: "none",
    connection: "idle",
    phase: "waiting",
    canRematch: false,
  });
  renderView(selectionIdle, false, false);
  assert.equal(checkedId(), "calm");
  assert.equal(getSelectedBotId(), "calm");
  assert.equal(ui.botPicker.open, false);
  assert.equal(ui.botSummaryName.textContent, "Тихий");
  assert.equal(ui.botSummaryDifficulty.textContent, "Тренировка");
  assert.equal(ui.botSummaryStatus.hidden, true);
  checkBot("predictive");
  updateBotSelection();
  setBotCatalog(browserCatalog);
  assert.equal(
    checkedId(),
    "predictive",
    "An eligible remembered bot takes precedence over default",
  );
  setBotCatalog({
    ...browserCatalog,
    bots: browserCatalog.bots.map((bot) =>
      bot.id === "predictive"
        ? {
            ...bot,
            availability: "unavailable",
            availabilityReason: "Бот временно недоступен.",
            canPlay: false,
          }
        : bot,
    ),
  });
  assert.equal(checkedId(), "calm", "An ineligible remembered bot yields to eligible default");
  checkBot("config-only-opponent");
  updateBotSelection();
  setBotCatalog({
    ...browserCatalog,
    bots: browserCatalog.bots.map((bot) =>
      bot.id !== "predictive"
        ? {
            ...bot,
            availability: "unavailable",
            availabilityReason: "Бот временно недоступен.",
            canPlay: false,
          }
        : bot,
    ),
  });
  assert.equal(
    checkedId(),
    "predictive",
    "An ineligible remembered/default bot yields to first eligible entry",
  );

  const noPlayableCatalog = {
    ...browserCatalog,
    bots: browserCatalog.bots.map((bot) => ({
      ...bot,
      availability: "unavailable",
      availabilityReason: "Бот временно недоступен.",
      canPlay: false,
    })),
  };
  setBotCatalog(noPlayableCatalog);
  renderView(selectionIdle, false, false);
  assert.equal(checkedId(), null);
  assert.equal(getSelectedBotId(), null);
  assert.equal(ui.botProfile.hidden, true);
  assert.equal(ui.botButton.disabled, true);
  assert.equal(ui.botPickerOpen.disabled, false, "All unavailable entries remain inspectable");
  assert.equal(radios().length, 3, "Unavailable entries remain visible with their safe status");
  assert.ok(radios().every((choice) => choice.disabled));
  assert.match(ui.botCatalogStatus.textContent, /нет доступных|недоступн/i);
  assert.equal(ui.quickButton.disabled, false);
  assert.equal(ui.joinButton.disabled, false);
  setBotCatalog(null);
  renderView(selectionIdle, false, false);
  assert.equal(ui.botPickerOpen.disabled, false, "Loading still allows opening catalog status");
  assert.equal(ui.botProfile.hidden, true);
  assert.match(ui.botCatalogStatus.textContent, /Загружаем/i);
  assert.equal(ui.quickButton.disabled, false);
  assert.equal(ui.joinButton.disabled, false);

  const fallbackCatalog = {
    version: 9,
    defaultBotId: "primary",
    bots: [
      catalogEntry("primary", "Основной", {
        fallbackBotId: "middle",
        availability: "unavailable",
        availabilityReason: "Основной бот недоступен.",
      }),
      catalogEntry("middle", "Промежуточный", {
        order: 20,
        fallbackBotId: "rescue",
        availability: "unavailable",
        availabilityReason: "Резерв также недоступен.",
      }),
      catalogEntry("rescue", "Резервный", { order: 30 }),
    ],
  };
  setBotCatalog(fallbackCatalog);
  renderView(selectionIdle, false, false);
  assert.equal(checkedId(), "primary");
  assert.equal(ui.botProfileName.textContent, "Основной");
  assert.match(ui.botProfileAvailability.textContent, /Резервный/);
  assert.match(ui.botProfileAvailability.textContent, /недоступен/);
  assert.match(ui.botSummaryAvailability.textContent, /Основной бот недоступен.*Резервный/);
  assert.equal(
    ui.botButton.disabled,
    false,
    "An explicit playable fallback permits the selected primary ID",
  );
  assert.equal(radio("primary").disabled, false);
  setBotCatalog({
    ...fallbackCatalog,
    bots: fallbackCatalog.bots.map((bot) =>
      bot.id === "rescue" ? { ...bot, availability: "notChecked" } : bot,
    ),
  });
  assert.equal(checkedId(), "primary");
  assert.match(ui.botProfileAvailability.textContent, /попробуем.*Резервный.*не проверена/);
  assert.match(ui.botSummaryAvailability.textContent, /попробуем.*Резервный.*не проверена/);
  assert.doesNotMatch(ui.botProfileAvailability.textContent, /будет играть/);

  setBotCatalog(browserCatalog);
  checkBot("predictive");
  updateBotSelection();
  assert.match(ui.botProfileAvailability.textContent, /провер|выбор/i);
  assert.doesNotMatch(ui.botProfileAvailability.textContent, /проверена|проверен успешно/i);

  const htmlName = `<b>${"Х".repeat(57)}</b>`;
  const groupedCatalog = {
    version: 9,
    defaultBotId: "alfa",
    bots: [
      catalogEntry("alfa", "Альфа", { order: 5, category: "Практика", glyph: "✦" }),
      catalogEntry("charlie", "Илья Осторожный", { order: 10, category: "Испытание" }),
      catalogEntry("beta", htmlName, {
        order: 20,
        category: "Практика",
        difficulty: "Продвинутый",
        description: '<img src="x" onerror="alert(1)">',
        style: "<script>Настроенный стиль</script>",
      }),
    ],
  };
  setBotCatalog(groupedCatalog);
  renderView(selectionIdle, false, false);
  assert.deepEqual(
    ui.botCatalogGroups.children.map((group) => group.querySelector("h3").textContent),
    ["Практика", "Испытание"],
  );
  assert.deepEqual(
    radios().map((choice) => choice.value),
    ["alfa", "beta", "charlie"],
  );
  assert.equal(cardText("alfa", ".bot-avatar"), "✦");
  assert.equal(cardText("charlie", ".bot-avatar"), "ИО");
  assert.equal(cardText("beta", ".bot-card-name"), htmlName);
  assert.equal(cardText("beta", ".bot-card-difficulty"), "Продвинутый");
  assert.equal(cardText("beta", ".bot-card-style"), "<script>Настроенный стиль</script>");
  assert.equal(
    radio("alfa").getAttribute("aria-labelledby"),
    card("alfa").querySelector(".bot-card-name").id,
  );
  assert.deepEqual(
    radio("alfa").getAttribute("aria-describedby").split(" "),
    [".bot-card-difficulty", ".bot-card-style", ".bot-card-availability"].map(
      (selector) => card("alfa").querySelector(selector).id,
    ),
  );
  assert.equal(card("alfa").htmlFor, radio("alfa").id);
  checkBot("beta");
  updateBotSelection();
  assert.equal(ui.botProfileName.textContent, htmlName);
  assert.equal(ui.botProfileName.children.length, 0);
  assert.equal(ui.botProfileDescription.textContent, '<img src="x" onerror="alert(1)">');
  assert.equal(ui.botProfileStyle.textContent, "<script>Настроенный стиль</script>");
  assert.equal(ui.botProfileDifficulty.textContent, "Продвинутый");
  assert.equal(ui.botSummaryName.textContent, htmlName);
  assert.equal(ui.botSummaryName.children.length, 0);
  assert.equal(ui.botSummaryDifficulty.textContent, "Продвинутый");
  assert.match(ui.botButton.textContent, /<b>Х+<\/b>/);

  openBotPicker();
  checkBot("alfa").focus();
  updateBotSelection();
  const focusedRadio = radio("alfa");
  const stableCards = radios();
  const stableGroups = [...ui.botCatalogGroups.children];
  const groupReplacements = ui.botCatalogGroups.replacementCount;
  setBotCatalog({
    ...groupedCatalog,
    bots: groupedCatalog.bots.map((bot) => ({
      ...bot,
      description: `${bot.description} Обновлено.`,
    })),
  });
  assert.deepEqual(radios(), stableCards, "Metadata refresh reuses native radio nodes");
  assert.deepEqual(ui.botCatalogGroups.children, stableGroups);
  assert.equal(ui.botCatalogGroups.replacementCount, groupReplacements);
  assert.equal(document.activeElement, focusedRadio);
  renderView({ ...selectionIdle, tick: 1 }, false, false);
  renderView({ ...selectionIdle, tick: 2 }, false, false);
  assert.equal(ui.botCatalogGroups.replacementCount, groupReplacements);
  assert.equal(document.activeElement, focusedRadio, "Ordinary snapshots preserve catalog focus");
  setBotCatalog({
    ...groupedCatalog,
    defaultBotId: "charlie",
    bots: groupedCatalog.bots.filter((bot) => bot.id !== "alfa"),
  });
  assert.equal(checkedId(), "charlie");
  assert.equal(
    document.activeElement,
    radio("charlie"),
    "Removing a focused entry moves focus to eligible selection",
  );

  closeBotPicker();
  setBotCatalog(browserCatalog);
  checkBot("calm");
  updateBotSelection();
  setBotCatalog(null, "Не удалось загрузить ботов. Попробуйте ещё раз.");
  renderView(selectionIdle, false, false);
  openBotPicker();
  ui.botCatalogRetry.focus();
  renderView(
    {
      ...selectionIdle,
      ...botIdentity("calm", "Тихий"),
      role: "host",
      connection: "connected",
      phase: "playing",
      canRematch: false,
    },
    false,
    false,
  );
  assert.equal(ui.botCatalog.disabled, true);
  assert.equal(ui.botPicker.open, false, "An active snapshot closes the idle picker");
  const afterActiveFocus = document.activeElement;
  setBotCatalog(browserCatalog);
  radio("calm").focus();
  assert.equal(
    document.activeElement,
    afterActiveFocus,
    "Native focus ignores a closed and disabled dialog descendant",
  );
  renderView(selectionIdle, false, false);
  assert.equal(
    document.activeElement,
    afterActiveFocus,
    "A completed catalog refresh never queues focus into a later idle closed picker",
  );
  openBotPicker();
  assert.equal(
    document.activeElement,
    radio("calm"),
    "Reopening focuses current eligible selection",
  );
  closeBotPicker();

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
      localSide: null,
      matchId: null,
      udpPort: 59123,
      localNickname: "Лиса",
      peerNickname: "Кот",
    },
    false,
    false,
  );
  assert.equal(ui.leftPlayer.textContent, "Игрок 1");
  assert.equal(ui.rightPlayer.textContent, "Игрок 2");
  assert.equal(ui.shareNickname.textContent, "Лиса");
  assert.equal(ui.sharePort.textContent, "59123");
  assert.equal(ui.shareBox.hidden, false);
  assert.equal(ui.arenaModeLabel.textContent, "Сетевая партия");
  assert.equal(ui.networkHint.hidden, false);
  assert.equal(ui.playerNickname.value, "Лиса");
  assert.equal(ui.playerNickname.disabled, true);
  renderView(
    {
      ...lanSnapshot,
      role: "guest",
      localSide: "right",
      localNickname: "Кот",
      peerNickname: "Лиса",
    },
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
      localSide: null,
      matchId: null,
      peerAddress: "192.168.1.43:47777",
      localNickname: "Лиса",
      peerNickname: "Кот",
      phase: "waiting",
      canRematch: false,
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
      canRematch: true,
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
      canRematch: false,
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
      canRematch: true,
      leftScore: 5,
      rightScore: 3,
      localNickname: "Лиса",
      peerNickname: "",
    },
    false,
    false,
  );
  assert.equal(ui.overlayTitle.textContent, "Победа: Лиса");
  assert.equal(ui.restartButton.textContent, "Реванш с «Тихий»");
  assert.equal(ui.overlay.dataset.phase, "gameover");
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
      canRematch: false,
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
      canRematch: false,
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
      canRematch: true,
      localNickname: "Лиса",
      message: "Матч завершён",
    },
    false,
    false,
  );
  assert.equal(ui.opponentFallback.hidden, false);
  assert.equal(ui.restartButton.textContent, "Реванш с «Тихий»");
  renderView(
    {
      ...lanSnapshot,
      role: "guest",
      localSide: null,
      matchId: null,
      connection: "awaitingAcceptance",
      phase: "waiting",
      canRematch: false,
    },
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
  assert.equal(ui.sessionMessage.textContent, "Нажмите «Быстрая игра» или подключитесь к другу.");
  const terminalFailureMessage = "Бот не смог продолжить игру. Выберите другого соперника.";
  renderView({ ...selectionIdle, message: terminalFailureMessage }, false, false);
  assert.equal(ui.sessionMessage.textContent, terminalFailureMessage);
  renderView({ ...selectionIdle, message: "" }, false, false);
  assert.equal(ui.sessionMessage.textContent, "Выберите игру с ботом или другом.");
  setGameMode("join");
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
    const role = changes.role ?? "host";
    const mode = changes.opponentMode ?? "lan";
    const matchId =
      changes.matchId ??
      (visualSession.snapshot.matchId !== null &&
      visualSession.snapshot.role === role &&
      visualSession.snapshot.opponentMode === mode &&
      visualSession.snapshot.roundId === (changes.roundId ?? 44) &&
      visualSession.snapshot.requestedBotId === (changes.requestedBotId ?? null)
        ? visualSession.snapshot.matchId
        : nextMatchId());
    visualSession.apply(
      {
        ...httpSnapshot(),
        opponentMode: "lan",
        role: "host",
        localSide: changes.localSide ?? (changes.role === "guest" ? "right" : "left"),
        connection: "connected",
        phase: "playing",
        canRematch: false,
        roundId: 44,
        tick: 10,
        ballX: 0.3,
        ballY: 0.5,
        ballVx: 0.6,
        ballVy: 0,
        leftY: 0.5,
        rightY: 0.4,
        ...changes,
        matchId,
        ...(changes.connection && changes.connection !== "connected" ? { localSide: null } : {}),
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

  // Canvas local prediction overlays the selected physical paddle, independently of role.
  for (const role of ["host", "guest"]) {
    for (const localSide of ["left", "right"]) {
      const drawn = [];
      const ctx = drawingContext();
      ctx.roundRect = (...args) => drawn.push(args);
      const ownerCanvas = element("canvas");
      ownerCanvas.getContext = () => ctx;
      const ownerSnapshot = {
        ...httpSnapshot(),
        role,
        localSide,
        matchId: nextMatchId(),
        connection: "connected",
        opponentMode: "lan",
        phase: "playing",
        canRematch: false,
        roundId: 1,
      };
      const renderer = new ArenaRenderer(
        ownerCanvas,
        {
          displayedMotion() {
            return { ballX: 0.5, ballY: 0.5, leftY: 0.2, rightY: 0.8 };
          },
          displayedLocalPaddle() {
            return 0.6;
          },
        },
        () => ownerSnapshot,
        () => 1,
        () => null,
      );
      renderer.resize(1000, 500, window.devicePixelRatio);
      renderer.draw(now);
      close(drawn[0][1], (localSide === "left" ? 0.6 : 0.2) * 500 - 45);
      close(drawn[1][1], (localSide === "right" ? 0.6 : 0.8) * 500 - 45);
    }
  }

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

  // Names, completed winner and local score marks follow side, while sharing stays role-based.
  for (const role of ["host", "guest"]) {
    for (const localSide of ["left", "right"]) {
      const own = {
        ...httpSnapshot(),
        opponentMode: "lan",
        role,
        localSide,
        matchId: nextMatchId(),
        roundId: 1,
        connection: "connected",
        phase: "gameover",
        canRematch: true,
        localNickname: "Свой",
        peerNickname: "Друг",
        leftScore: 7,
        rightScore: 2,
      };
      renderView(own, false, false);
      assert.equal(ui.leftPlayer.textContent, localSide === "left" ? "Свой" : "Друг");
      assert.equal(ui.rightPlayer.textContent, localSide === "right" ? "Свой" : "Друг");
      assert.equal(localScoreMarks.left, localSide === "left");
      assert.equal(localScoreMarks.right, localSide === "right");
      assert.equal(
        ui.overlayTitle.textContent,
        `Победа: ${localSide === "left" ? "Свой" : "Друг"}`,
      );
      assert.equal(ui.roleBadge.dataset.role, role);
      assert.equal(ui.roleBadge.dataset.side, localSide);
      assert.equal(ui.roleDetail.textContent, localSide === "left" ? "Вы — слева" : "Вы — справа");
    }
  }
  renderView(
    {
      ...httpSnapshot(),
      ...botIdentity("calm", "Тихий"),
      role: "host",
      localSide: "right",
      matchId: nextMatchId(),
      roundId: 1,
      connection: "connected",
      phase: "countdown",
      canRematch: false,
    },
    false,
    false,
  );
  assert.equal(ui.leftPlayer.textContent, "Бот Тихий");
  assert.equal(ui.rightPlayer.textContent, "Лиса");
  assert.match(ui.overlayDescription.textContent, /Вы справа/);
  assert.match(ui.sessionMessage.textContent, /правой.*Слева.*Тихий/);

  // A compact selection form launches the chosen profile; the body-level
  // native dialog retains full profiles without nested forms or launch controls.
  const html = readFileSync(`${__dirname}/../../frontend/index.html`, "utf8");
  assert.match(html, /<form[^>]+id="bot-form"[\s\S]*?<button[^>]+id="bot-button"/);
  assert.doesNotMatch(html, /bot-select|hard-form|hard-button|Simple|Hard/);
  assert.match(html, /id="bot-profile"/);
  assert.match(html, /id="bot-profile-description"/);
  assert.match(html, /id="opponent-fallback"/);
  assert.match(html, /<select[^>]+id="game-mode"/);
  for (const mode of ["bot", "quick", "join"]) assert.match(html, new RegExp(`value="${mode}"`));
  assert.doesNotMatch(html, /id="tab-host"|id="tab-join"/);
  const pickerMarkup = html.match(/<dialog[\s\S]*?<\/dialog>/)?.[0];
  assert.match(pickerMarkup, /id="bot-picker"/);
  assert.match(pickerMarkup, /aria-labelledby="bot-picker-title"/);
  assert.match(pickerMarkup, /id="bot-catalog"/);
  assert.match(pickerMarkup, /id="bot-profile-description"/);
  assert.doesNotMatch(pickerMarkup, /<form|id="bot-button"/);
  for (const id of ["bot-picker-open", "bot-picker-close", "bot-picker-done"])
    assert.match(html, new RegExp(`<button[^>]+id="${id}"[^>]+type="button"`));
  assert.match(html, /<section[^>]+id="game-arena"[^>]+tabindex="-1"/);
  const bindEvents = (target) => {
    const listeners = new Map();
    target.addEventListener = (type, handler) => {
      const handlers = listeners.get(type) ?? [];
      handlers.push(handler);
      listeners.set(type, handlers);
    };
    target.dispatch = (type, values = {}) => {
      const event = {
        target: null,
        preventDefault() {
          this.defaultPrevented = true;
        },
        ...values,
      };
      for (const handler of listeners.get(type) ?? []) handler(event);
      return event;
    };
  };
  bindEvents(window);
  bindEvents(document);
  globalThis.setInterval = () => 0;
  globalThis.requestAnimationFrame = () => 0;
  globalThis.location = { protocol: "http:", host: "127.0.0.1:47777" };
  globalThis.WebSocket = class {
    static CONNECTING = 0;
    static OPEN = 1;
    static instances = [];
    readyState = 0;
    listeners = new Map();
    sent = [];
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
    send(payload) {
      this.sent.push(payload);
    }
  };
  const requests = [];
  const idleSnapshot = httpSnapshot({
    role: "none",
    opponentMode: "none",
    connection: "idle",
    phase: "waiting",
    canRematch: false,
    roundId: 60,
    tick: 0,
  });
  let catalogPayload = browserCatalog;
  let catalogError = false;
  let discoveredHosts = [];
  let holdNextDiscovery = false;
  let releaseDiscovery = null;
  let holdNextStatus = false;
  let releaseStatus = null;
  let guestRestartPending = false;
  let rejectRestartWithoutStopping = false;
  let pushLaterRoundDuringRestart = false;
  let supersedeRestartWithNewMatch = false;
  let holdNextAction = false;
  let releaseAction = null;
  let quickMatchesGuest = false;
  let pushIdleDuringQuick = false;
  let rejectQuickAfterGuestFrame = false;
  let quickGuestModeWhilePending = null;
  let rejectSelection = false;
  let rejectRestart = false;
  let invalidActionResponse = false;
  let holdNextCatalog = false;
  let pushNewerBotDuringStart = false;
  let pushIdleBeforeMatchingBotResponse = false;
  let statusAfterIdleFrame = null;
  let pushOldGameOverDuringRestart = false;
  let supersedingBotDuringStart = null;
  let releaseOldCatalog = null;
  let serverSnapshot = idleSnapshot;
  globalThis.fetch = async (path, options) => {
    requests.push({ path, options });
    if (path === "/api/status" && holdNextStatus) {
      holdNextStatus = false;
      const captured = captureSnapshot(serverSnapshot);
      await new Promise((resolve) => {
        releaseStatus = resolve;
      });
      return { ok: true, text: async () => JSON.stringify(captured) };
    }
    if (path === "/api/discover") {
      if (holdNextDiscovery) {
        holdNextDiscovery = false;
        await new Promise((resolve) => {
          releaseDiscovery = resolve;
        });
      }
      return { ok: true, text: async () => JSON.stringify({ hosts: discoveredHosts }) };
    }
    if (options?.method === "POST" && holdNextAction) {
      holdNextAction = false;
      await new Promise((resolve) => {
        releaseAction = resolve;
      });
    }
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
    if (path === "/api/restart" && rejectRestartWithoutStopping) {
      return {
        ok: false,
        status: 400,
        text: async () => JSON.stringify({ error: "Этот матч уже завершён иначе." }),
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
    if (path === "/api/quick" && rejectQuickAfterGuestFrame) {
      rejectQuickAfterGuestFrame = false;
      serverSnapshot = {
        ...idleSnapshot,
        opponentMode: "lan",
        role: "guest",
        connection: "awaitingAcceptance",
        localNickname: "Browser Tester",
        peerNickname: "Другая игра",
        peerAddress: "192.168.1.44:49126",
      };
      pushSnapshot(serverSnapshot);
      quickGuestModeWhilePending = ui.gameMode.value;
      return {
        ok: false,
        status: 400,
        text: async () => JSON.stringify({ error: "Быстрая игра уже недоступна." }),
      };
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
          canRematch: false,
          localNickname: "Browser Tester",
          roundId: 61,
          localSide: "left",
          matchId: nextMatchId(),
        }
      : path === "/api/restart"
        ? guestRestartPending
          ? { ...serverSnapshot }
          : {
              ...serverSnapshot,
              phase: "countdown",
              canRematch: false,
              roundId: serverSnapshot.roundId + 1,
              localSide: serverSnapshot.localSide === "left" ? "right" : "left",
              tick: 0,
              leftScore: 0,
              rightScore: 0,
            }
        : path === "/api/quick"
          ? {
              ...idleSnapshot,
              opponentMode: "lan",
              role: quickMatchesGuest ? "guest" : "host",
              connection: quickMatchesGuest ? "connected" : "waiting",
              localSide: quickMatchesGuest ? "left" : null,
              matchId: quickMatchesGuest ? nextMatchId() : null,
              roundId: quickMatchesGuest ? 61 : 60,
              phase: quickMatchesGuest ? "countdown" : "waiting",
              localNickname: JSON.parse(options.body).nickname,
              udpPort: 49123,
              localAddresses: ["127.0.0.1", "192.168.1.10"],
            }
          : path === "/api/join"
            ? {
                ...idleSnapshot,
                opponentMode: "lan",
                role: "guest",
                connection: "awaitingAcceptance",
                localNickname: JSON.parse(options.body).nickname,
                peerAddress: `${JSON.parse(options.body).address}:${JSON.parse(options.body).port}`,
              }
            : idleSnapshot;
    if (
      ["/api/local-opponent", "/api/leave", "/api/restart", "/api/quick", "/api/join"].includes(
        path,
      )
    )
      serverSnapshot = data;
    if (path === "/api/quick" && pushIdleDuringQuick) {
      pushIdleDuringQuick = false;
      pushSnapshot(idleSnapshot);
    }
    if (path === "/api/restart" && pushOldGameOverDuringRestart) {
      pushOldGameOverDuringRestart = false;
      pushSnapshot({
        ...data,
        phase: "gameover",
        canRematch: true,
        roundId: data.roundId - 1,
        localSide: data.localSide === "left" ? "right" : "left",
        tick: 100,
        leftScore: 5,
        rightScore: 3,
      });
    }
    if (path === "/api/restart" && pushLaterRoundDuringRestart) {
      pushLaterRoundDuringRestart = false;
      serverSnapshot = {
        ...data,
        roundId: data.roundId + 1,
        localSide: data.localSide === "left" ? "right" : "left",
        phase: "playing",
        canRematch: false,
      };
      pushSnapshot(serverSnapshot);
    }
    if (path === "/api/restart" && supersedeRestartWithNewMatch) {
      supersedeRestartWithNewMatch = false;
      serverSnapshot = { ...data, matchId: nextMatchId(), phase: "playing", canRematch: false };
      pushSnapshot(serverSnapshot);
    }
    if (selected && pushNewerBotDuringStart) {
      pushNewerBotDuringStart = false;
      serverSnapshot = { ...data, phase: "playing", canRematch: false, tick: 5 };
      pushSnapshot(serverSnapshot);
    }
    if (selected && pushIdleBeforeMatchingBotResponse) {
      pushIdleBeforeMatchingBotResponse = false;
      if (statusAfterIdleFrame === "left") serverSnapshot = idleSnapshot;
      if (statusAfterIdleFrame === "later-round")
        serverSnapshot = {
          ...data,
          phase: "playing",
          canRematch: false,
          roundId: data.roundId + 1,
          localSide: data.localSide === "left" ? "right" : "left",
          tick: 5,
        };
      statusAfterIdleFrame = null;
      pushSnapshot(idleSnapshot);
    }
    if (selected && supersedingBotDuringStart !== null) {
      const newer = browserCatalog.bots.find((bot) => bot.id === supersedingBotDuringStart);
      supersedingBotDuringStart = null;
      serverSnapshot = {
        ...data,
        ...botIdentity(newer.id, newer.name),
        phase: "playing",
        canRematch: false,
        tick: 5,
      };
      pushSnapshot(serverSnapshot);
    }
    return {
      ok: true,
      text: async () =>
        JSON.stringify(captureSnapshot(path === "/api/status" ? serverSnapshot : data)),
    };
  };
  const { encode, decode } = await import("@msgpack/msgpack");
  const pushSnapshot = (next, captured = false) => {
    if (!captured) next = captureSnapshot(next);
    const payload = [
      9,
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
      next.localSide === null ? null : next.localSide === "left" ? 1 : 2,
      next.matchId,
      next.sourceId,
      next.snapshotSequence,
      next.canRematch,
    ];
    WebSocket.instances
      .at(-1)
      .dispatch("message", { data: Uint8Array.from(encode(payload)).buffer });
  };
  const catalogRequests = () => requests.filter((request) => request.path === "/api/bots").length;
  const statusRequests = () => requests.filter((request) => request.path === "/api/status").length;
  const flush = () => new Promise((resolve) => setImmediate(resolve));
  const { startGame } = await import("../../.artifacts/frontend-test/game.js");
  renderView(idleSnapshot, false, false);
  setGameMode("bot");
  ui.playerNickname.value = "Browser Tester";
  startGame();
  await flush();
  const browserSocket = WebSocket.instances.at(-1);
  browserSocket.readyState = WebSocket.OPEN;
  const lastSentControl = () =>
    browserSocket.sent.length ? decode(new Uint8Array(browserSocket.sent.at(-1))) : [9, null, 0, 0];
  const lastSentAxis = () => lastSentControl()[3];
  assert.equal(checkedId(), "calm");
  assert.equal(radios().length, 3);
  assert.equal(ui.botButton.disabled, false);
  const postRequests = () =>
    requests.filter((request) => request.options?.method === "POST").length;
  const selectMode = (mode) => {
    ui.gameMode.focus();
    ui.gameMode.value = mode;
    ui.gameMode.dispatch("change");
  };

  // Setup choices only reveal controls; they retain shared fields and eligible
  // bot identity without starting or cancelling a session.
  const beforeModeChanges = requests.length;
  ui.playerNickname.value = "Browser Tester";
  ui.playerNickname.dispatch("input");
  ui.peerAddress.value = "friend.local";
  ui.joinPort.value = "49123";
  for (const mode of ["join", "quick", "bot"]) {
    selectMode(mode);
    assert.equal(ui.gameMode.value, mode);
    assert.equal(ui.botPanel.hidden, mode !== "bot");
    assert.equal(ui.quickPanel.hidden, mode !== "quick");
    assert.equal(ui.joinPanel.hidden, mode !== "join");
    assert.equal(ui.playerNickname.value, "Browser Tester");
    assert.equal(ui.peerAddress.value, "friend.local");
    assert.equal(ui.joinPort.value, "49123");
    assert.equal(getSelectedBotId(), "calm");
  }
  assert.equal(requests.length, beforeModeChanges);
  ui.gameMode.value = "unsupported";
  ui.gameMode.dispatch("change");
  assert.equal(ui.gameMode.value, "bot", "Unsupported mode values preserve the current setup");

  // Opening clears input, and full profiles remain on demand. Every user close
  // path keeps the native selection and synchronously returns to its opener.
  const beforePickerPosts = postRequests();
  window.dispatch("keydown", { key: "ArrowDown", target: ui.arenaPanel });
  assert.equal(browserSocket.sent.length, 0, "Idle input has no assigned round to control");
  ui.botPickerOpen.focus();
  ui.botPickerOpen.dispatch("click");
  assert.equal(ui.botPicker.open, true);
  assert.equal(document.activeElement, radio("calm"));
  assert.equal(lastSentAxis(), 0, "Opening the opponent picker clears held paddle input");
  for (const target of [
    ui.botPickerClose,
    ui.botPickerDone,
    ui.botPickerTitle,
    ui.botProfileName,
  ]) {
    for (const key of ["ArrowUp", "ArrowDown", "w", "s"]) {
      assert.equal(window.dispatch("keydown", { key, target }).defaultPrevented, undefined);
      assert.equal(lastSentAxis(), 0, "Non-editable picker content never drives the paddle");
    }
  }
  // Exercise the bound dialog handler through bubbling endpoint events. Only
  // endpoint Tab traversal is trapped; native traversal within the dialog stays free.
  for (const start of [ui.botPickerClose, ui.botPickerTitle]) {
    start.focus();
    const reverseTab = start.dispatch("keydown", { key: "Tab", shiftKey: true });
    assert.equal(reverseTab.defaultPrevented, true);
    assert.equal(document.activeElement, ui.botPickerDone);
    assert.equal(lastSentAxis(), 0, "Reverse modal endpoint traversal never moves the paddle");
  }
  ui.botPickerDone.focus();
  const forwardTab = ui.botPickerDone.dispatch("keydown", { key: "Tab", shiftKey: false });
  assert.equal(forwardTab.defaultPrevented, true);
  assert.equal(document.activeElement, ui.botPickerClose);
  assert.equal(lastSentAxis(), 0);
  for (const [start, shiftKey] of [
    [ui.botPickerClose, false],
    [ui.botPickerDone, true],
    [radio("calm"), false],
    [radio("calm"), true],
  ]) {
    start.focus();
    const nativeTab = start.dispatch("keydown", { key: "Tab", shiftKey });
    assert.equal(nativeTab.defaultPrevented, undefined, "Interior Tab traversal remains native");
    assert.equal(
      document.activeElement,
      start,
      "The endpoint handler does not override interior focus",
    );
    assert.equal(lastSentAxis(), 0);
  }
  selectBot("config-only-opponent");
  assert.equal(ui.botSummaryName.textContent, "Дополнительный бот из конфигурации");
  ui.botPickerDone.dispatch("click");
  assert.equal(ui.botPicker.open, false);
  assert.equal(document.activeElement, ui.botPickerOpen);
  for (const shiftKey of [false, true]) {
    const closedTab = ui.botPicker.dispatch("keydown", { key: "Tab", shiftKey });
    assert.equal(closedTab.defaultPrevented, undefined, "A closed picker does not trap Tab");
    assert.equal(document.activeElement, ui.botPickerOpen);
    assert.equal(lastSentAxis(), 0);
  }
  ui.botPickerOpen.dispatch("click");
  assert.equal(document.activeElement, radio("config-only-opponent"));
  ui.botPickerClose.dispatch("click");
  assert.equal(document.activeElement, ui.botPickerOpen);
  ui.botPickerOpen.dispatch("click");
  const cancelled = ui.botPicker.dispatch("cancel");
  assert.equal(cancelled.defaultPrevented, true);
  assert.equal(ui.botPicker.open, false);
  assert.equal(document.activeElement, ui.botPickerOpen);
  assert.equal(getSelectedBotId(), "config-only-opponent");
  assert.equal(
    postRequests(),
    beforePickerPosts,
    "Open, radio selection, Done, Close and Escape never Play",
  );
  selectBot("calm");
  closeBotPicker();

  // A fresh tab may observe an already-open LAN host while the local setup
  // still prefers bots. Sharing and challenge/session controls stay reachable.
  const sharingHost = {
    ...idleSnapshot,
    opponentMode: "lan",
    role: "host",
    connection: "waiting",
    udpPort: 49123,
    localNickname: "Browser Tester",
    localAddresses: ["192.168.1.10"],
  };
  pushSnapshot(sharingHost);
  assert.equal(ui.gameMode.value, "quick", "Fresh LAN host context reveals quick-game sharing");
  assert.equal(ui.quickPanel.hidden, false);
  assert.equal(ui.shareBox.hidden, false);
  assert.equal(ui.shareBox.closest("#quick-panel"), null);
  assert.equal(ui.shareNickname.textContent, "Browser Tester");
  assert.equal(ui.sharePort.textContent, "49123");
  const beforeLockedActions = requests.length;
  for (const snapshot of [
    sharingHost,
    { ...sharingHost, connection: "incomingChallenge", peerNickname: "Друг" },
    { ...sharingHost, role: "guest", connection: "awaitingAcceptance" },
    { ...sharingHost, connection: "disconnected" },
    {
      ...sharingHost,
      connection: "connected",
      localSide: "left",
      matchId: nextMatchId(),
      phase: "gameover",
      canRematch: true,
    },
    { ...sharingHost, role: "none", connection: "searching" },
  ]) {
    if (snapshot.connection === "disconnected") renderView(snapshot, false, false);
    else pushSnapshot(snapshot);
    assert.equal(isSetupLocked(), true);
    assert.equal(ui.gameMode.disabled, true);
    assert.equal(ui.botPickerOpen.disabled, true);
    const displayedMode = snapshot.role === "guest" ? "join" : "quick";
    assert.equal(
      ui.gameMode.value,
      displayedMode,
      "Active mode follows host, guest or search context",
    );
    selectMode(displayedMode === "join" ? "bot" : "join");
    assert.equal(
      ui.gameMode.value,
      displayedMode,
      "Active, challenge and searching state reject setup changes",
    );
    ui.botPickerOpen.dispatch("click");
    assert.equal(ui.botPicker.open, false);
    ui.quickForm.dispatch("submit");
    ui.botForm.dispatch("submit");
    ui.joinForm.dispatch("submit");
    ui.discoverButton.dispatch("click");
  }
  assert.equal(
    requests.length,
    beforeLockedActions,
    "Locked setup handlers cannot send another action",
  );
  pushSnapshot(idleSnapshot);
  assert.equal(ui.gameMode.value, "bot", "Returning idle restores the prior setup choice");
  selectMode("join");

  // Real discovery handlers filter malformed results, render nicknames as
  // text, and use the selected address/port while preserving manual fallback.
  discoveredHosts = [
    { address: "192.168.1.42", port: 49124, nickname: "<b>Друг</b>" },
    { address: "192.168.1.43", port: 0, nickname: "Invalid" },
    { address: "", port: 49124, nickname: "Invalid" },
    { address: "192.168.1.44", port: 49124, nickname: "" },
  ];
  ui.discoverButton.dispatch("click");
  await flush();
  const discoveryChoice = ui.discoveryResults.querySelector("button");
  assert.equal(ui.discoveryResults.querySelectorAll("button").length, 1);
  assert.equal(discoveryChoice.querySelector("strong").textContent, "<b>Друг</b>");
  assert.equal(discoveryChoice.querySelector("strong").children.length, 0);
  discoveryChoice.dispatch("click");
  assert.equal(ui.selectedHost.hidden, false);
  assert.equal(ui.selectedHostName.textContent, "<b>Друг</b>");
  assert.equal(ui.peerAddress.value, "");
  assert.equal(ui.joinPort.value, "49124");
  selectMode("quick");
  selectMode("join");
  assert.equal(ui.selectedHost.hidden, false, "Mode changes keep the discovered host choice");
  const beforeDiscoveredJoinScrolls = scrollCalls.length;
  ui.joinForm.dispatch("submit");
  await flush();
  assert.deepEqual(
    JSON.parse(requests.filter((request) => request.path === "/api/join").at(-1).options.body),
    {
      address: "192.168.1.42",
      port: 49124,
      nickname: "Browser Tester",
    },
  );
  assert.equal(ui.gameMode.disabled, true);
  assert.equal(discoveryChoice.disabled, true);
  assert.equal(ui.clearSelectedHost.disabled, true);
  const selectedName = ui.selectedHostName.textContent;
  ui.clearSelectedHost.dispatch("click");
  discoveryChoice.dispatch("click");
  assert.equal(ui.selectedHostName.textContent, selectedName);
  assert.equal(ui.selectedHost.hidden, false);
  assert.equal(scrollCalls.length, beforeDiscoveredJoinScrolls);
  ui.leaveButton.dispatch("click");
  await flush();
  ui.clearSelectedHost.dispatch("click");
  assert.equal(ui.selectedHost.hidden, true);
  assert.equal(document.activeElement, ui.peerAddress);
  discoveryChoice.dispatch("click");
  ui.peerAddress.value = "  friend.local  ";
  ui.peerAddress.dispatch("input");
  assert.equal(
    ui.selectedHost.hidden,
    true,
    "Manual address editing clears the discovery override",
  );
  ui.joinPort.value = "49125";
  ui.joinPort.dispatch("input");
  ui.joinForm.dispatch("submit");
  await flush();
  assert.deepEqual(
    JSON.parse(requests.filter((request) => request.path === "/api/join").at(-1).options.body),
    {
      address: "friend.local",
      port: 49125,
      nickname: "Browser Tester",
    },
  );
  ui.leaveButton.dispatch("click");
  await flush();

  // A discovery response arriving during an active match cannot re-enable its
  // new controls or change a locked host selection.
  holdNextDiscovery = true;
  ui.discoverButton.dispatch("click");
  await flush();
  assert.equal(ui.discoverButton.disabled, true);
  pushSnapshot(sharingHost);
  releaseDiscovery();
  await flush();
  const lateDiscoveryChoice = ui.discoveryResults.querySelector("button");
  assert.equal(lateDiscoveryChoice.disabled, true);
  lateDiscoveryChoice.dispatch("click");
  assert.equal(ui.selectedHost.hidden, true);
  assert.equal(ui.peerAddress.value, "  friend.local  ");
  pushSnapshot(idleSnapshot);
  selectMode("quick");

  // Busy locking begins before the HTTP reply, and successful quick LAN
  // creation uses the real handler without triggering bot arena navigation.
  const beforeQuickScrolls = scrollCalls.length;
  holdNextAction = true;
  ui.quickForm.dispatch("submit");
  await flush();
  assert.equal(isSetupLocked(), true);
  assert.equal(ui.gameMode.disabled, true);
  const beforeBusyRequests = postRequests();
  selectMode("join");
  assert.equal(ui.gameMode.value, "quick");
  ui.botForm.dispatch("submit");
  ui.joinForm.dispatch("submit");
  assert.equal(postRequests(), beforeBusyRequests);
  releaseAction();
  await flush();
  assert.deepEqual(
    JSON.parse(requests.filter((request) => request.path === "/api/quick").at(-1).options.body),
    {
      nickname: "Browser Tester",
    },
  );
  assert.equal(ui.shareBox.hidden, false);
  assert.equal(scrollCalls.length, beforeQuickScrolls);
  ui.leaveButton.dispatch("click");
  await flush();
  assert.equal(ui.gameMode.value, "quick");
  quickMatchesGuest = true;
  pushIdleDuringQuick = true;
  ui.quickForm.dispatch("submit");
  await flush();
  assert.equal(serverSnapshot.role, "guest");
  assert.equal(
    ui.connectionPill.dataset.state,
    "idle",
    "A newer pre-quick idle frame supersedes the successful HTTP snapshot",
  );
  assert.equal(ui.gameMode.value, "quick");
  pushSnapshot(serverSnapshot);
  assert.equal(
    ui.gameMode.value,
    "quick",
    "A quick-game initiator keeps quick context when matched as guest",
  );
  assert.equal(ui.quickPanel.hidden, false);
  assert.equal(ui.joinPanel.hidden, true);
  assert.equal(ui.shareBox.hidden, true);
  ui.leaveButton.dispatch("click");
  await flush();
  assert.equal(ui.gameMode.value, "quick", "Leave restores the remembered quick setup");
  quickMatchesGuest = false;
  selectMode("bot");

  const beforeInvalidNickname = postRequests();
  ui.playerNickname.value = "";
  submitBot();
  assert.equal(postRequests(), beforeInvalidNickname);
  assert.match(ui.playerNickname.validationMessages.join(" "), /Введите ник/);
  ui.playerNickname.value = "Browser Tester";
  const beforeFirstPlayScroll = scrollCalls.length;
  const beforeFirstPlayFocus = ui.arenaPanel.focusCalls.length;
  const beforeFirstPlayStatus = statusRequests();
  window.dispatch("keydown", { key: "ArrowDown" });
  const controlsBeforeStart = browserSocket.sent.length;
  assert.equal(lastSentAxis(), 0, "Idle held keys cannot send a roundless control");
  pushIdleBeforeMatchingBotResponse = true;
  submitBot();
  await flush();
  assert.equal(
    statusRequests(),
    beforeFirstPlayStatus + 1,
    "A superseded successful Play response reconciles current status once before revealing",
  );
  assert.ok(
    requests.filter((request) => request.path === "/api/status").at(-1).options.signal instanceof
      AbortSignal,
    "The reconciliation status request carries a bounded abort signal",
  );
  assert.equal(
    ui.rightPlayer.textContent,
    "Бот Тихий",
    "Current status confirms the requested bot and same action round",
  );
  assert.equal(
    scrollCalls.length,
    beforeFirstPlayScroll + 1,
    "A newer pre-start idle WebSocket frame does not suppress reveal for matching successful Play",
  );
  assert.equal(ui.arenaPanel.focusCalls.length, beforeFirstPlayFocus + 1);
  assert.equal(document.activeElement, ui.arenaPanel);
  assert.equal(
    lastSentAxis(),
    0,
    "Current status confirmation clears held input before the arena is revealed",
  );
  pushSnapshot(serverSnapshot);
  await flush();
  assert.equal(
    scrollCalls.length,
    beforeFirstPlayScroll + 1,
    "The subsequent active bot WebSocket frame does not reveal the arena a second time",
  );
  assert.equal(ui.arenaPanel.focusCalls.length, beforeFirstPlayFocus + 1);
  const botRequest = requests.find((request) => request.path === "/api/local-opponent");
  assert.ok(botRequest);
  assert.equal(botRequest.options.method, "POST");
  assert.deepEqual(JSON.parse(botRequest.options.body), {
    nickname: "Browser Tester",
    botId: "calm",
    side: "left",
  });
  assert.equal(ui.arenaModeLabel.textContent, "Против бота · Тихий");
  assert.equal(ui.rightPlayer.textContent, "Бот Тихий");
  assert.equal(ui.botCatalog.disabled, true);
  assert.equal(scrollCalls.length, beforeFirstPlayScroll + 1);
  assert.deepEqual(scrollCalls.at(-1), {
    element: ui.arenaPanel,
    options: { behavior: "instant", block: "start" },
  });
  assert.equal(ui.arenaPanel.focusCalls.length, beforeFirstPlayFocus + 1);
  assert.deepEqual(ui.arenaPanel.focusCalls.at(-1), { preventScroll: true });
  assert.equal(document.activeElement, ui.arenaPanel);
  assert.equal(lastSentAxis(), 0, "A successful bot launch clears held paddle input");
  ui.botPicker.dispatch("close");
  assert.equal(
    document.activeElement,
    ui.arenaPanel,
    "A delayed dialog close event cannot override accepted arena focus",
  );
  window.dispatch("keydown", { key: "ArrowUp", target: ui.arenaPanel });
  assert.equal(lastSentAxis(), -1, "Keyboard controls work after focus leaves the catalog");
  window.dispatch("keyup", { key: "ArrowUp", target: ui.arenaPanel });
  assert.equal(lastSentAxis(), 0);
  const normalSnapshotScrolls = scrollCalls.length;
  const normalSnapshotArenaFocus = ui.arenaPanel.focusCalls.length;
  pushSnapshot({ ...serverSnapshot, tick: 1 });
  pushSnapshot({ ...serverSnapshot, tick: 2 });
  await flush();
  assert.equal(
    scrollCalls.length,
    normalSnapshotScrolls,
    "Ordinary bot snapshots never move the page",
  );
  assert.equal(ui.arenaPanel.focusCalls.length, normalSnapshotArenaFocus);
  renderView(
    {
      ...lanSnapshot,
      ...noBotIdentity,
      opponentMode: "lan",
      role: "host",
      connection: "connected",
    },
    false,
    false,
  );
  renderView(
    {
      ...lanSnapshot,
      ...noBotIdentity,
      opponentMode: "lan",
      role: "host",
      connection: "connected",
      tick: 2,
    },
    false,
    false,
  );
  assert.equal(
    scrollCalls.length,
    normalSnapshotScrolls,
    "LAN snapshots never trigger deliberate bot launch navigation",
  );
  assert.equal(ui.arenaPanel.focusCalls.length, normalSnapshotArenaFocus);
  assert.ok(requests.filter((request) => request.path === "/api/bots").length >= 2);

  ui.leaveButton.dispatch("click");
  await flush();
  const beforeLanSnapshots = scrollCalls.length;
  const beforeLanSnapshotFocus = ui.arenaPanel.focusCalls.length;
  const liveLanSnapshot = {
    ...serverSnapshot,
    ...noBotIdentity,
    opponentMode: "lan",
    role: "host",
    connection: "connected",
    phase: "playing",
    canRematch: false,
    localSide: "left",
    matchId: nextMatchId(),
    udpPort: 47777,
    peerAddress: "192.168.1.42:47777",
    localNickname: "Browser Tester",
    peerNickname: "LAN Peer",
  };
  pushSnapshot(liveLanSnapshot);
  pushSnapshot({ ...liveLanSnapshot, tick: 2 });
  await flush();
  assert.equal(scrollCalls.length, beforeLanSnapshots);
  assert.equal(ui.arenaPanel.focusCalls.length, beforeLanSnapshotFocus);
  pushSnapshot(serverSnapshot);
  await flush();
  const beforeRadioBrowse = scrollCalls.length;
  radio("config-only-opponent").focus();
  selectBot("config-only-opponent");
  assert.equal(document.activeElement, radio("config-only-opponent"));
  assert.equal(scrollCalls.length, beforeRadioBrowse, "Radio browsing does not scroll the page");
  reducedMotion = true;
  pushNewerBotDuringStart = true;
  submitBot();
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
    side: "left",
  });
  assert.equal(ui.rightPlayer.textContent, "Бот Дополнительный бот из конфигурации");
  assert.equal(checkedId(), "config-only-opponent");
  assert.equal(scrollCalls.length, beforeRadioBrowse + 1);
  assert.equal(
    scrollCalls.at(-1).options.behavior,
    "instant",
    "Reduced-motion bot launch never animates scrolling",
  );
  assert.equal(document.activeElement, ui.arenaPanel);
  assert.equal(
    serverSnapshot.phase,
    "playing",
    "A newer accepted WebSocket snapshot can precede the matching Play response",
  );
  reducedMotion = false;
  ui.leaveButton.dispatch("click");
  await flush();
  assert.equal(checkedId(), "config-only-opponent");

  // A failed start keeps its card/reason visible and selects an eligible default for the next attempt.
  selectBot("predictive");
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
  radio("predictive").focus();
  const rejectedPlayScrolls = scrollCalls.length;
  const rejectedPlayArenaFocus = ui.arenaPanel.focusCalls.length;
  submitBot();
  await flush();
  assert.equal(checkedId(), "calm");
  assert.equal(ui.botButton.disabled, false);
  assert.match(cardText("predictive", ".bot-card-availability"), /Выбранный бот недоступен/);
  assert.match(ui.toast.textContent, /Выбранный бот недоступен/);
  assert.equal(radio("predictive").disabled, true);
  assert.equal(
    scrollCalls.length,
    rejectedPlayScrolls,
    "Rejected Play never scrolls toward a nonexistent match",
  );
  assert.equal(ui.arenaPanel.focusCalls.length, rejectedPlayArenaFocus);
  assert.notEqual(document.activeElement, ui.arenaPanel);

  // A terminal strategy failure refreshes once, preserving the failed entry's status. A late
  // catalog response started before that failure cannot restore stale playable status.
  rejectSelection = false;
  catalogPayload = browserCatalog;
  setBotCatalog(browserCatalog);
  selectBot("calm");
  holdNextCatalog = true;
  submitBot();
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
  serverSnapshot = { ...idleSnapshot, message: terminalFailureMessage };
  pushSnapshot(serverSnapshot);
  await flush();
  assert.equal(catalogRequests(), beforeTerminalFailure + 1);
  assert.equal(checkedId(), "predictive");
  assert.equal(ui.botButton.disabled, false);
  assert.match(cardText("calm", ".bot-card-availability"), /перестал отвечать/);
  assert.equal(
    ui.sessionMessage.textContent,
    terminalFailureMessage,
    "The backend's terminal failure survives the idle transition",
  );
  pushSnapshot({ ...serverSnapshot, tick: 1 });
  pushSnapshot({ ...serverSnapshot, tick: 2 });
  await flush();
  assert.equal(catalogRequests(), beforeTerminalFailure + 1);
  releaseOldCatalog();
  await flush();
  assert.equal(ui.botButton.disabled, false);
  assert.equal(radio("calm").disabled, true);

  // An unsolicited runtime fallback refreshes primary availability once and keeps both
  // names/reason visible. Later ticks preserve the selection without catalog fetches.
  catalogPayload = browserCatalog;
  setBotCatalog(browserCatalog);
  selectBot("predictive");
  submitBot();
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
    canRematch: false,
    tick: 10,
  };
  pushSnapshot(serverSnapshot);
  await flush();
  assert.equal(catalogRequests(), beforeFallback + 1);
  assert.equal(checkedId(), "predictive");
  assert.match(ui.botProfileAvailability.textContent, /перестал отвечать.*Тихий/);
  assert.match(ui.opponentFallback.textContent, /Прогноз.*Тихий.*перестал отвечать/);
  pushSnapshot({ ...serverSnapshot, tick: 11 });
  await flush();
  assert.equal(catalogRequests(), beforeFallback + 1);
  serverSnapshot = {
    ...serverSnapshot,
    phase: "gameover",
    canRematch: true,
    tick: 100,
    leftScore: 5,
    rightScore: 3,
  };
  pushSnapshot(serverSnapshot);
  assert.equal(ui.restartButton.textContent, "Реванш с «Тихий»");
  const fallbackRound = serverSnapshot.roundId;
  const beforeRematchScrolls = scrollCalls.length;
  const beforeRematchFocus = ui.arenaPanel.focusCalls.length;
  const beforeRematchStatus = statusRequests();
  window.dispatch("keyup", { key: "ArrowDown", target: ui.arenaPanel });
  window.dispatch("keydown", { key: "ArrowDown", target: ui.arenaPanel });
  assert.equal(lastSentAxis(), 1);
  pushOldGameOverDuringRestart = true;
  ui.restartButton.dispatch("click");
  await flush();
  assert.equal(
    statusRequests(),
    beforeRematchStatus + 1,
    "An old game-over WebSocket frame during rematch requires one current-status reconciliation",
  );
  assert.ok(
    requests.filter((request) => request.path === "/api/status").at(-1).options.signal instanceof
      AbortSignal,
  );
  assert.ok(requests.some((request) => request.path === "/api/restart"));
  assert.equal(serverSnapshot.roundId, fallbackRound + 1);
  assert.equal(
    ui.overlay.dataset.phase,
    "countdown",
    "Current status accepts the new rematch round",
  );
  assert.equal(ui.restartButton.disabled, true);
  assert.equal(serverSnapshot.requestedBotId, "predictive");
  assert.equal(serverSnapshot.effectiveBotId, "calm");
  assert.equal(ui.opponentFallback.hidden, false);
  assert.match(ui.opponentFallback.textContent, /Прогноз.*Тихий.*перестал отвечать/);
  assert.equal(ui.leftPlayer.textContent, "Бот Тихий");
  assert.equal(checkedId(), "predictive");
  assert.equal(scrollCalls.length, beforeRematchScrolls + 1);
  assert.equal(ui.arenaPanel.focusCalls.length, beforeRematchFocus + 1);
  assert.equal(document.activeElement, ui.arenaPanel);
  assert.equal(
    lastSentAxis(),
    0,
    "Successful rematch clears held input before arena control resumes",
  );
  pushSnapshot({ ...serverSnapshot, tick: 1 });
  await flush();
  assert.equal(statusRequests(), beforeRematchStatus + 1);
  assert.equal(scrollCalls.length, beforeRematchScrolls + 1);
  assert.equal(ui.arenaPanel.focusCalls.length, beforeRematchFocus + 1);
  ui.leaveButton.dispatch("click");
  await flush();

  // A failed rematch that stops during the POST refreshes once rather than relying on
  // a later rejected Play attempt; its snapshot transition shares the POST's refresh.
  catalogPayload = browserCatalog;
  setBotCatalog(browserCatalog);
  selectBot("calm");
  submitBot();
  await flush();
  serverSnapshot = { ...serverSnapshot, phase: "gameover", canRematch: true, tick: 100 };
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
  const failedRematchScrolls = scrollCalls.length;
  const failedRematchArenaFocus = ui.arenaPanel.focusCalls.length;
  ui.restartButton.dispatch("click");
  await flush();
  assert.equal(catalogRequests(), beforeRematchFailure + 1);
  assert.equal(checkedId(), "predictive");
  assert.equal(ui.botButton.disabled, false);
  assert.match(cardText("calm", ".bot-card-availability"), /не смог продолжить игру/);
  assert.equal(ui.leaveButton.disabled, true);
  assert.equal(scrollCalls.length, failedRematchScrolls);
  assert.equal(ui.arenaPanel.focusCalls.length, failedRematchArenaFocus);

  // A matching successful response must not steal focus from a newer different bot session.
  catalogPayload = browserCatalog;
  setBotCatalog(browserCatalog);
  renderView(idleSnapshot, false, false);
  selectBot("predictive");
  const beforeSupersedingScrolls = scrollCalls.length;
  const beforeSupersedingFocus = ui.arenaPanel.focusCalls.length;
  supersedingBotDuringStart = "calm";
  submitBot();
  await flush();
  assert.equal(ui.rightPlayer.textContent, "Бот Тихий");
  assert.equal(scrollCalls.length, beforeSupersedingScrolls);
  assert.equal(ui.arenaPanel.focusCalls.length, beforeSupersedingFocus);
  ui.leaveButton.dispatch("click");
  await flush();

  // An older successful response never reveals a match after a later leave or different round.
  for (const scenario of ["left", "later-round"]) {
    catalogPayload = browserCatalog;
    setBotCatalog(browserCatalog);
    renderView(idleSnapshot, false, false);
    selectBot("calm");
    const beforeScrolls = scrollCalls.length;
    const beforeFocus = ui.arenaPanel.focusCalls.length;
    const beforeStatus = statusRequests();
    pushIdleBeforeMatchingBotResponse = true;
    statusAfterIdleFrame = scenario;
    submitBot();
    await flush();
    assert.equal(
      statusRequests(),
      beforeStatus + 1,
      "Superseded Play uses one bounded current-status check",
    );
    assert.equal(
      scrollCalls.length,
      beforeScrolls,
      `Current status ${scenario} rejects stale arena reveal`,
    );
    assert.equal(ui.arenaPanel.focusCalls.length, beforeFocus);
    if (scenario === "left") {
      assert.equal(ui.arenaModeLabel.textContent, "Выберите режим");
      assert.equal(serverSnapshot.opponentMode, "none");
    } else {
      assert.equal(ui.leftPlayer.textContent, "Бот Тихий");
      assert.equal(serverSnapshot.roundId, 62);
      ui.leaveButton.dispatch("click");
      await flush();
    }
  }

  // A guest frame accepted during an unrelated quick request remains current
  // when HTTP rejects quick; rejected intent must not mislabel that guest session.
  selectMode("quick");
  const beforeQuickRejectionScrolls = scrollCalls.length;
  const beforeQuickRejectionFocus = ui.arenaPanel.focusCalls.length;
  rejectQuickAfterGuestFrame = true;
  ui.quickForm.dispatch("submit");
  await flush();
  assert.equal(
    quickGuestModeWhilePending,
    "quick",
    "Pending quick intent initially retains quick context",
  );
  assert.equal(
    ui.gameMode.value,
    "join",
    "Confirmed quick HTTP rejection clears intent for the already accepted guest frame",
  );
  assert.equal(ui.gameMode.disabled, true);
  assert.equal(ui.connectionPill.dataset.state, "awaitingAcceptance");
  assert.equal(ui.peerDetail.textContent, "Другая игра");
  assert.equal(serverSnapshot.role, "guest");
  assert.equal(serverSnapshot.peerAddress, "192.168.1.44:49126");
  assert.match(ui.toast.textContent, /Быстрая игра уже недоступна/);
  assert.equal(scrollCalls.length, beforeQuickRejectionScrolls);
  assert.equal(ui.arenaPanel.focusCalls.length, beforeQuickRejectionFocus);
  ui.leaveButton.dispatch("click");
  await flush();
  assert.equal(
    ui.gameMode.value,
    "quick",
    "Leave restores the idle choice after rejecting quick intent",
  );

  // Parser errors returned by a POST remain Russian in the visible toast.
  invalidActionResponse = true;
  selectMode("quick");
  const beforeRejectedLanAction = scrollCalls.length;
  const beforeRejectedLanFocus = ui.arenaPanel.focusCalls.length;
  ui.quickForm.dispatch("submit");
  await flush();
  assert.match(ui.toast.textContent, /некорректный ответ/);
  assert.doesNotMatch(ui.toast.textContent, /Unsupported|Invalid snapshot/);
  assert.equal(scrollCalls.length, beforeRejectedLanAction);
  assert.equal(ui.arenaPanel.focusCalls.length, beforeRejectedLanFocus);
  pushSnapshot({
    ...idleSnapshot,
    opponentMode: "lan",
    role: "guest",
    connection: "awaitingAcceptance",
  });
  assert.equal(
    ui.gameMode.value,
    "join",
    "A rejected quick action does not label a later unrelated guest session as quick",
  );
  pushSnapshot(idleSnapshot);
  assert.equal(ui.gameMode.value, "quick");

  // Fetch errors offer retry in Russian, and configured HTML-looking metadata remains text.
  selectMode("bot");
  ui.botPickerOpen.dispatch("click");
  const catalogNodesBeforeError = [...radios()];
  catalogError = true;
  ui.botCatalogRetry.dispatch("click");
  await flush();
  assert.equal(ui.botCatalogRetry.hidden, false);
  assert.match(ui.botCatalogStatus.textContent, /Не удалось загрузить ботов/);
  assert.doesNotMatch(ui.botCatalogStatus.textContent, /Network|sensitive/);
  assert.equal(ui.quickButton.disabled, false);
  assert.equal(ui.joinButton.disabled, false);
  assert.equal(ui.botPickerOpen.disabled, false);
  assert.equal(ui.botButton.disabled, true);
  assert.equal(ui.botCatalog.disabled, false, "Catalog errors do not lock the idle picker");
  ui.botCatalogRetry.focus();
  assert.equal(document.activeElement, ui.botCatalogRetry);
  catalogError = false;
  catalogPayload = {
    ...browserCatalog,
    bots: browserCatalog.bots.map((bot) =>
      bot.id === "config-only-opponent" ? { ...bot, name: "<b>Настроенный бот</b>" } : bot,
    ),
  };
  holdNextCatalog = true;
  ui.botCatalogRetry.dispatch("click");
  await flush();
  assert.equal(typeof releaseOldCatalog, "function");
  ui.botPickerDone.dispatch("click");
  assert.equal(document.activeElement, ui.botPickerOpen);
  selectMode("quick");
  const closedPickerFocus = document.activeElement;
  const closedRadioFocuses = catalogNodesBeforeError.map((choice) => choice.focusCalls.length);
  releaseOldCatalog();
  await flush();
  assert.equal(ui.botPicker.open, false);
  assert.equal(
    document.activeElement,
    closedPickerFocus,
    "Retry completing after close/mode change never steals focus",
  );
  assert.deepEqual(
    catalogNodesBeforeError.map((choice) => choice.focusCalls.length),
    closedRadioFocuses,
  );
  assert.equal(ui.botSummaryName.textContent, "Тихий");
  selectMode("bot");
  ui.botPickerOpen.dispatch("click");
  assert.equal(
    document.activeElement,
    radio("calm"),
    "Reopening resolves current eligible selection afresh",
  );
  setBotCatalog(null, "Не удалось загрузить ботов. Попробуйте снова.");
  renderView(idleSnapshot, false, false);
  ui.botCatalogRetry.focus();
  ui.botCatalogRetry.dispatch("click");
  await flush();
  assert.equal(ui.botCatalogRetry.hidden, true);
  assert.equal(ui.botCatalog.disabled, false);
  assert.equal(
    document.activeElement,
    radio(checkedId()),
    "Retry completing in the open unlocked dialog restores native radio focus",
  );
  assert.match(cardText("config-only-opponent", ".bot-card-name"), /<b>Настроенный бот<\/b>/);
  const replacements = ui.botCatalogGroups.replacementCount;
  renderView({ ...idleSnapshot, tick: 1 }, false, false);
  renderView({ ...idleSnapshot, tick: 2 }, false, false);
  assert.equal(ui.botCatalogGroups.replacementCount, replacements);
  assert.equal(checkedId(), "calm");
  setBotCatalog({ version: 9, defaultBotId: "calm", bots: [] });
  renderView(idleSnapshot, false, false);
  assert.equal(ui.botButton.disabled, true);
  assert.equal(ui.botPickerOpen.disabled, false);
  assert.match(ui.botCatalogStatus.textContent, /нет ботов/);
  assert.equal(checkedId(), null);
  assert.equal(ui.botProfile.hidden, true);
  assert.equal(ui.quickButton.disabled, false);
  assert.equal(ui.joinButton.disabled, false);
  closeBotPicker();
  ui.botPickerOpen.dispatch("click");
  assert.equal(ui.botPicker.open, true, "An empty catalog can still be inspected or retried");
  assert.equal(document.activeElement, ui.botCatalogRetry);
  ui.botPickerDone.dispatch("click");
  // A pre-rematch GET cannot overwrite an accepted HTTP transition without a WS revision.
  catalogPayload = browserCatalog;
  catalogError = false;
  rejectRestart = false;
  setBotCatalog(browserCatalog);
  renderView(idleSnapshot, false, false);
  selectMode("bot");
  selectBot("calm");
  submitBot();
  await flush();
  serverSnapshot = {
    ...serverSnapshot,
    phase: "gameover",
    canRematch: true,
    tick: 100,
    leftScore: 7,
    rightScore: 2,
  };
  pushSnapshot(serverSnapshot);
  const getFenceMatch = serverSnapshot.matchId;
  const getFenceRound = serverSnapshot.roundId;
  holdNextStatus = true;
  browserSocket.dispatch("open", {});
  await flush();
  assert.equal(typeof releaseStatus, "function");
  ui.restartButton.dispatch("click");
  await flush();
  assert.equal(ui.overlay.dataset.phase, "countdown");
  assert.equal(ui.rightPlayer.textContent, "Browser Tester");
  assert.deepEqual(
    JSON.parse(requests.filter((r) => r.path === "/api/restart").at(-1).options.body),
    { matchId: getFenceMatch, expectedRoundId: getFenceRound },
  );
  assert.deepEqual(lastSentControl(), [9, getFenceMatch, getFenceRound + 1, 0]);
  const afterAcceptedFocus = ui.arenaPanel.focusCalls.length;
  releaseStatus();
  await flush();
  assert.equal(ui.overlay.dataset.phase, "countdown", "Old GET cannot restore the finished round");
  assert.equal(ui.rightPlayer.textContent, "Browser Tester", "Old GET cannot restore old side");
  assert.equal(ui.arenaPanel.focusCalls.length, afterAcceptedFocus);
  ui.leaveButton.dispatch("click");
  await flush();

  // Guest acknowledgement remains finished; only authoritative new State owns the swap.
  serverSnapshot = {
    ...idleSnapshot,
    opponentMode: "lan",
    role: "guest",
    matchId: nextMatchId(),
    localSide: "left",
    connection: "connected",
    roundId: 1,
    phase: "gameover",
    canRematch: true,
    localNickname: "Browser Tester",
    peerNickname: "LAN Peer",
    leftScore: 7,
    rightScore: 2,
  };
  pushSnapshot(serverSnapshot);
  window.dispatch("keyup", { key: "ArrowDown" });
  window.dispatch("keydown", { key: "ArrowDown", target: ui.arenaPanel });
  assert.equal(lastSentAxis(), 1);
  const beforeGuestFocus = ui.arenaPanel.focusCalls.length;
  const beforeGuestScroll = scrollCalls.length;
  guestRestartPending = true;
  const pendingGuestRound = serverSnapshot.roundId;
  ui.restartButton.dispatch("click");
  await flush();
  assert.equal(ui.overlay.dataset.phase, "gameover");
  assert.equal(ui.leftPlayer.textContent, "Browser Tester");
  assert.equal(ui.leftScore.textContent, "7");
  assert.equal(lastSentAxis(), 1, "Pending acknowledgement has not changed ownership yet");
  assert.equal(ui.arenaPanel.focusCalls.length, beforeGuestFocus);
  guestRestartPending = false;
  serverSnapshot = {
    ...serverSnapshot,
    roundId: pendingGuestRound + 1,
    localSide: "right",
    phase: "countdown",
    canRematch: false,
    tick: 0,
    leftScore: 0,
    rightScore: 0,
  };
  pushSnapshot(serverSnapshot);
  assert.equal(lastSentAxis(), 0);
  assert.deepEqual(lastSentControl(), [9, serverSnapshot.matchId, serverSnapshot.roundId, 0]);
  assert.equal(ui.rightPlayer.textContent, "Browser Tester");
  assert.equal(
    ui.arenaPanel.focusCalls.length,
    beforeGuestFocus + 1,
    "An acknowledged local guest rematch reveals its accepted exact ownership",
  );
  assert.equal(scrollCalls.length, beforeGuestScroll + 1);
  ui.leaveButton.dispatch("click");
  await flush();

  // A corrected predicted finish cancels local intent before any later unrelated remote rematch.
  serverSnapshot = {
    ...idleSnapshot,
    opponentMode: "lan",
    role: "guest",
    matchId: nextMatchId(),
    localSide: "left",
    connection: "connected",
    roundId: 1,
    phase: "gameover",
    canRematch: true,
    tick: 200,
    localNickname: "Browser Tester",
    peerNickname: "LAN Peer",
    leftScore: 7,
    rightScore: 2,
  };
  pushSnapshot(serverSnapshot);
  const correctedFinishFocus = ui.arenaPanel.focusCalls.length;
  const correctedFinishScroll = scrollCalls.length;
  guestRestartPending = true;
  ui.restartButton.dispatch("click");
  await flush();
  assert.equal(ui.overlay.dataset.phase, "gameover");
  assert.equal(ui.arenaPanel.focusCalls.length, correctedFinishFocus);
  guestRestartPending = false;
  serverSnapshot = {
    ...serverSnapshot,
    phase: "playing",
    canRematch: false,
    tick: 201,
    leftScore: 6,
  };
  pushSnapshot(serverSnapshot);
  assert.equal(ui.overlay.hidden, true, "Accepted same-round correction resumes play");
  serverSnapshot = {
    ...serverSnapshot,
    phase: "gameover",
    canRematch: true,
    tick: 400,
    leftScore: 7,
  };
  pushSnapshot(serverSnapshot);
  assert.equal(ui.overlay.dataset.phase, "gameover");
  serverSnapshot = {
    ...serverSnapshot,
    roundId: 2,
    localSide: "right",
    phase: "countdown",
    canRematch: false,
    tick: 0,
    leftScore: 0,
    rightScore: 0,
  };
  pushSnapshot(serverSnapshot);
  assert.equal(ui.rightPlayer.textContent, "Browser Tester");
  assert.equal(
    ui.arenaPanel.focusCalls.length,
    correctedFinishFocus,
    "A later remote rematch cannot consume intent from the corrected predicted finish",
  );
  assert.equal(scrollCalls.length, correctedFinishScroll);
  ui.leaveButton.dispatch("click");
  await flush();

  // Authoritative eligibility can change while replay still displays the same finished score.
  serverSnapshot = {
    ...idleSnapshot,
    opponentMode: "lan",
    role: "guest",
    matchId: nextMatchId(),
    localSide: "left",
    connection: "connected",
    roundId: 1,
    phase: "gameover",
    canRematch: false,
    tick: 200,
    localNickname: "Browser Tester",
    peerNickname: "LAN Peer",
    leftScore: 7,
    rightScore: 2,
  };
  pushSnapshot(serverSnapshot);
  assert.equal(ui.overlay.dataset.phase, "gameover");
  assert.equal(ui.restartButton.disabled, true, "Predicted GameOver cannot request a rematch");
  const beforeUnconfirmedPosts = postRequests();
  ui.restartButton.dispatch("click");
  await flush();
  assert.equal(
    postRequests(),
    beforeUnconfirmedPosts,
    "The action guard rejects even a synthetic click on predicted GameOver",
  );
  serverSnapshot = { ...serverSnapshot, canRematch: true };
  pushSnapshot(serverSnapshot);
  assert.equal(
    ui.restartButton.disabled,
    false,
    "Eligibility alone changes the UI signature and enables a confirmed finish",
  );
  const eligibilityFocus = ui.arenaPanel.focusCalls.length;
  const eligibilityScroll = scrollCalls.length;
  guestRestartPending = true;
  ui.restartButton.dispatch("click");
  await flush();
  assert.equal(postRequests(), beforeUnconfirmedPosts + 1);
  assert.equal(ui.arenaPanel.focusCalls.length, eligibilityFocus);
  guestRestartPending = false;
  window.dispatch("keyup", { key: "ArrowDown" });
  window.dispatch("keydown", { key: "ArrowDown", target: ui.arenaPanel });
  assert.equal(lastSentAxis(), 1);
  serverSnapshot = { ...serverSnapshot, canRematch: false };
  pushSnapshot(serverSnapshot);
  assert.equal(
    ui.overlay.dataset.phase,
    "gameover",
    "Replay may still display the predicted finish",
  );
  assert.equal(ui.leftScore.textContent, "7");
  assert.equal(
    ui.restartButton.disabled,
    true,
    "Authoritative correction alone disables rematch without changing finished orientation",
  );
  assert.equal(lastSentAxis(), 1, "Eligibility correction does not clear same-round held input");
  serverSnapshot = { ...serverSnapshot, canRematch: true, tick: 400 };
  pushSnapshot(serverSnapshot);
  assert.equal(ui.restartButton.disabled, false);
  serverSnapshot = {
    ...serverSnapshot,
    roundId: 2,
    localSide: "right",
    phase: "countdown",
    canRematch: false,
    tick: 0,
    leftScore: 0,
    rightScore: 0,
  };
  pushSnapshot(serverSnapshot);
  assert.equal(lastSentAxis(), 0);
  assert.equal(ui.rightPlayer.textContent, "Browser Tester");
  assert.equal(
    ui.arenaPanel.focusCalls.length,
    eligibilityFocus,
    "A later remote rematch cannot reuse intent invalidated by CanRematch=false",
  );
  assert.equal(scrollCalls.length, eligibilityScroll);
  ui.leaveButton.dispatch("click");
  await flush();

  // A remote rematch without local intent never scrolls or steals focus.
  serverSnapshot = {
    ...idleSnapshot,
    opponentMode: "lan",
    role: "guest",
    matchId: nextMatchId(),
    localSide: "right",
    connection: "connected",
    roundId: 1,
    phase: "gameover",
    canRematch: true,
    localNickname: "Browser Tester",
    peerNickname: "LAN Peer",
    leftScore: 2,
    rightScore: 7,
  };
  pushSnapshot(serverSnapshot);
  const remoteFocus = ui.arenaPanel.focusCalls.length;
  const remoteScroll = scrollCalls.length;
  serverSnapshot = {
    ...serverSnapshot,
    roundId: 2,
    localSide: "left",
    phase: "countdown",
    canRematch: false,
    leftScore: 0,
    rightScore: 0,
  };
  pushSnapshot(serverSnapshot);
  assert.equal(ui.arenaPanel.focusCalls.length, remoteFocus);
  assert.equal(scrollCalls.length, remoteScroll);
  ui.leaveButton.dispatch("click");
  await flush();

  // A WS transition before the HTTP reply requires a successful matching acknowledgement to reveal.
  for (const acknowledgement of ["accepted", "rejected"]) {
    serverSnapshot = {
      ...idleSnapshot,
      opponentMode: "lan",
      role: "guest",
      matchId: nextMatchId(),
      localSide: "left",
      connection: "connected",
      roundId: 1,
      phase: "gameover",
      canRematch: true,
      localNickname: "Browser Tester",
      peerNickname: "LAN Peer",
      leftScore: 7,
      rightScore: 2,
    };
    pushSnapshot(serverSnapshot);
    const beforeAckFocus = ui.arenaPanel.focusCalls.length;
    const beforeAckScroll = scrollCalls.length;
    holdNextAction = true;
    guestRestartPending = true;
    ui.restartButton.dispatch("click");
    await flush();
    serverSnapshot = {
      ...serverSnapshot,
      roundId: 2,
      localSide: "right",
      phase: "countdown",
      canRematch: false,
      leftScore: 0,
      rightScore: 0,
    };
    pushSnapshot(serverSnapshot);
    assert.equal(
      ui.arenaPanel.focusCalls.length,
      beforeAckFocus,
      "Unacknowledged rematch intent never reveals a remote snapshot",
    );
    rejectRestartWithoutStopping = acknowledgement === "rejected";
    releaseAction();
    await flush();
    assert.equal(
      ui.arenaPanel.focusCalls.length,
      beforeAckFocus + (acknowledgement === "accepted" ? 1 : 0),
    );
    assert.equal(scrollCalls.length, beforeAckScroll + (acknowledgement === "accepted" ? 1 : 0));
    rejectRestartWithoutStopping = false;
    guestRestartPending = false;
    ui.leaveButton.dispatch("click");
    await flush();
  }

  // Stale successful rematch responses cannot reveal a later round or different match with colliding round IDs.
  for (const superseding of ["later-round", "new-match"]) {
    selectMode("bot");
    selectBot("calm");
    submitBot();
    await flush();
    serverSnapshot = {
      ...serverSnapshot,
      phase: "gameover",
      canRematch: true,
      leftScore: 7,
      rightScore: 2,
    };
    pushSnapshot(serverSnapshot);
    const before = ui.arenaPanel.focusCalls.length;
    const beforeScroll = scrollCalls.length;
    pushLaterRoundDuringRestart = superseding === "later-round";
    supersedeRestartWithNewMatch = superseding === "new-match";
    ui.restartButton.dispatch("click");
    await flush();
    assert.equal(
      ui.arenaPanel.focusCalls.length,
      before,
      `Stale rematch cannot focus ${superseding}`,
    );
    assert.equal(scrollCalls.length, beforeScroll);
    assert.equal(ui.overlay.hidden, true);
    ui.leaveButton.dispatch("click");
    await flush();
  }

  // Capture ordering fences even queued null or previously unseen contexts after HTTP admission.
  selectMode("bot");
  selectBot("calm");
  const queuedLobby = captureSnapshot(idleSnapshot);
  pushSnapshot(queuedLobby, true);
  submitBot();
  await flush();
  const admittedContext = serverSnapshot.matchId;
  const admittedFocus = ui.arenaPanel.focusCalls.length;
  pushSnapshot(queuedLobby, true);
  assert.equal(ui.rightPlayer.textContent, "Бот Тихий");
  pushSnapshot(
    {
      ...queuedLobby,
      ...serverSnapshot,
      matchId: nextMatchId(),
      sourceId: queuedLobby.sourceId,
      snapshotSequence: queuedLobby.snapshotSequence,
    },
    true,
  );
  assert.equal(ui.rightPlayer.textContent, "Бот Тихий");
  pushSnapshot(serverSnapshot);
  assert.equal(
    ui.rightPlayer.textContent,
    "Бот Тихий",
    "Rejected old null capture never retires the newly admitted match",
  );
  assert.equal(serverSnapshot.matchId, admittedContext);
  assert.equal(ui.arenaPanel.focusCalls.length, admittedFocus);

  // Reconnection clears held input and sends nothing until a fresh accepted ownership baseline.
  window.dispatch("keyup", { key: "ArrowDown" });
  window.dispatch("keydown", { key: "ArrowDown", target: ui.arenaPanel });
  assert.equal(lastSentAxis(), 1);
  browserSocket.readyState = 3;
  browserSocket.dispatch("close", {});
  const controlsBeforeReconnect = browserSocket.sent.length;
  holdNextStatus = true;
  browserSocket.readyState = WebSocket.OPEN;
  browserSocket.dispatch("open", {});
  await flush();
  assert.equal(
    window.dispatch("keydown", { key: "ArrowDown", repeat: true, target: ui.arenaPanel })
      .defaultPrevented,
    true,
  );
  assert.equal(
    browserSocket.sent.length,
    controlsBeforeReconnect,
    "Reconnect cannot send old held axis before the ownership snapshot arrives",
  );
  pushSnapshot(serverSnapshot);
  assert.deepEqual(lastSentControl(), [9, serverSnapshot.matchId, serverSnapshot.roundId, 0]);
  releaseStatus();
  await flush();
  ui.leaveButton.dispatch("click");
  await flush();

  console.log(
    "Frontend behavior checks passed: motion, events, native modes/dialog, input ownership, catalog/action races, and real LAN handlers.",
  );
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
