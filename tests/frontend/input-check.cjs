const assert = require("node:assert/strict");

function eventTarget() {
  const listeners = new Map();
  return {
    addEventListener(type, handler) {
      const handlers = listeners.get(type) ?? [];
      handlers.push(handler);
      listeners.set(type, handlers);
    },
    dispatch(type, values = {}) {
      const event = {
        target: null,
        preventDefault() {
          event.prevented = true;
        },
        ...values,
      };
      for (const handler of listeners.get(type) ?? []) handler(event);
      return event;
    },
  };
}

async function main() {
  const [{ InputController }, { MotionModel }, { defaultSnapshot }, { GameSession }] =
    await Promise.all([
      import("../../.artifacts/frontend-test/input.js"),
      import("../../.artifacts/frontend-test/motion.js"),
      import("../../.artifacts/frontend-test/snapshot.js"),
      import("../../.artifacts/frontend-test/session.js"),
    ]);

  globalThis.Element = class {
    constructor(editable = false, parentElement = null) {
      this.editable = editable;
      this.parentElement = parentElement;
      this.open = false;
    }
    closest(selector) {
      const tag =
        typeof this.editable === "string" ? this.editable : this.editable ? "input" : null;
      const matches = selector.split(",").some((part) => {
        const candidate = part.trim();
        return candidate === tag || (candidate === "dialog[open]" && tag === "dialog" && this.open);
      });
      return matches ? this : (this.parentElement?.closest(selector) ?? null);
    }
  };
  globalThis.window = eventTarget();
  globalThis.document = Object.assign(eventTarget(), { hidden: false });
  const upButton = Object.assign(eventTarget(), {
    capturedPointer: null,
    setPointerCapture(pointerId) {
      this.capturedPointer = pointerId;
    },
  });
  const downButton = Object.assign(eventTarget(), {
    capturedPointer: null,
    setPointerCapture(pointerId) {
      this.capturedPointer = pointerId;
    },
  });
  const changes = [];
  let input;
  input = new InputController(upButton, downButton, () => changes.push(input.axis));
  input.bind();
  input.bind();

  const motion = new MotionModel();
  const snapshot = {
    ...defaultSnapshot,
    opponentMode: "bot",
    role: "host",
    localSide: "left",
    connection: "connected",
    phase: "playing",
    leftY: 0.5,
    rightY: 0.7,
  };
  const startY = motion.displayedLocalPaddle(1000, snapshot, input.axis);
  assert.equal(startY, snapshot.leftY);
  assert.notEqual(startY, snapshot.rightY);

  assert.equal(window.dispatch("keydown", { key: "w" }).prevented, true);
  assert.equal(input.axis, -1);
  const keyUpY = motion.displayedLocalPaddle(1016, snapshot, input.axis);
  assert.ok(keyUpY < startY);
  window.dispatch("keyup", { key: "w" });
  assert.equal(input.axis, 0);
  const botRadio = new Element("input");
  botRadio.type = "radio";
  botRadio.name = "botId";
  for (const key of ["ArrowUp", "ArrowDown", "w", "s"]) {
    assert.equal(
      window.dispatch("keydown", { key, target: botRadio }).prevented,
      undefined,
      "Native catalog radio keys stay available to browser selection",
    );
    assert.equal(input.axis, 0, "Catalog navigation must not move the paddle");
  }
  window.dispatch("keydown", { key: "ArrowDown" });
  assert.equal(input.axis, 1);
  document.dispatch("focusin", { target: botRadio });
  assert.equal(input.axis, 0, "Entering the catalog clears a held paddle input");

  window.dispatch("keydown", { key: "ArrowDown" });
  assert.equal(input.axis, 1);
  const keyDownY = motion.displayedLocalPaddle(1032, snapshot, input.axis);
  assert.ok(keyDownY > keyUpY);
  window.dispatch("keyup", { key: "ArrowDown" });
  assert.equal(input.axis, 0);
  assert.equal(
    window.dispatch("keydown", { key: "s", target: new Element(true) }).prevented,
    undefined,
  );
  assert.equal(input.axis, 0);
  assert.equal(
    window.dispatch("keydown", { key: "ArrowDown", target: new Element("select") }).prevented,
    undefined,
  );
  assert.equal(input.axis, 0);

  const picker = new Element("dialog");
  picker.open = true;
  const pickerButton = new Element("button", picker);
  const pickerHeading = new Element("h2", picker);
  const pickerLabel = new Element("label", picker);
  const pickerDetails = new Element("span", new Element("p", picker));
  for (const target of [picker, pickerButton, pickerHeading, pickerLabel, pickerDetails]) {
    for (const key of ["ArrowUp", "ArrowDown", "w", "s"]) {
      assert.equal(
        window.dispatch("keydown", { key, target }).prevented,
        undefined,
        "Every open-dialog descendant keeps native keyboard handling",
      );
      assert.equal(input.axis, 0, "Dialog navigation must not move the paddle");
    }
  }
  window.dispatch("keydown", { key: "ArrowDown" });
  assert.equal(input.axis, 1);
  document.dispatch("focusin", { target: pickerButton });
  assert.equal(input.axis, 0, "Entering a dialog button clears held keyboard input");
  upButton.dispatch("pointerdown", { pointerId: 41 });
  assert.equal(input.axis, -1);
  document.dispatch("focusin", { target: pickerHeading });
  assert.equal(input.axis, 0, "Entering dialog content clears held touch input too");
  picker.open = false;
  assert.equal(
    window.dispatch("keydown", { key: "w", target: pickerButton }).prevented,
    true,
    "A closed dialog does not globally suppress normal game keyboard handling",
  );
  assert.equal(input.axis, -1);
  window.dispatch("keyup", { key: "w", target: pickerButton });
  assert.equal(input.axis, 0);

  assert.equal(upButton.dispatch("pointerdown", { pointerId: 42 }).prevented, true);
  assert.equal(upButton.capturedPointer, 42);
  assert.equal(input.axis, -1);
  const touchUpY = motion.displayedLocalPaddle(1048, snapshot, input.axis);
  assert.ok(touchUpY < keyDownY);
  upButton.dispatch("pointerup", { pointerId: 42 });
  assert.equal(input.axis, 0);
  downButton.dispatch("pointerdown", { pointerId: 43 });
  assert.equal(downButton.capturedPointer, 43);
  assert.equal(input.axis, 1);
  const touchDownY = motion.displayedLocalPaddle(1064, snapshot, input.axis);
  assert.ok(touchDownY > touchUpY);
  downButton.dispatch("pointercancel", { pointerId: 43 });
  assert.equal(input.axis, 0);

  window.dispatch("keydown", { key: "ArrowUp" });
  assert.equal(input.axis, -1);
  window.dispatch("blur");
  assert.equal(input.axis, 0);
  downButton.dispatch("pointerdown", { pointerId: 44 });
  document.hidden = true;
  document.dispatch("visibilitychange");
  assert.equal(input.axis, 0);
  document.hidden = false;
  window.dispatch("keydown", { key: "s" });
  document.dispatch("focusin", { target: new Element(true) });
  assert.equal(input.axis, 0);
  assert.deepEqual(changes.slice(0, 3), [-1, 0, 1]);

  // Network authority and physical ownership are independent, with vertical sign unchanged.
  for (const role of ["host", "guest"]) {
    for (const localSide of ["left", "right"]) {
      motion.reset();
      const owned = { ...snapshot, role, localSide };
      const y = motion.displayedLocalPaddle(2000, owned, input.axis);
      assert.equal(y, localSide === "left" ? owned.leftY : owned.rightY);
      window.dispatch("keydown", { key: "w" });
      assert.ok(motion.displayedLocalPaddle(2016, owned, input.axis) < y);
      input.clear();
      downButton.dispatch("pointerdown", { pointerId: 51 });
      assert.equal(input.axis, 1);
      const before = motion.displayedLocalPaddle(2032, owned, input.axis);
      assert.ok(motion.displayedLocalPaddle(2048, owned, input.axis) > before);
      input.clear();
    }
  }
  assert.equal(
    motion.displayedLocalPaddle(2100, { ...snapshot, localSide: null }, 1),
    null,
    "An unresolved lobby has no locally predicted paddle",
  );

  // An accepted ownership transition clears input after the new fence is visible.
  const controls = [];
  let ownershipSession;
  ownershipSession = new GameSession(
    motion,
    { clearPulse() {}, process() {} },
    () => {},
    () => {},
    () => {
      input.clear(true);
      controls.push({
        matchId: ownershipSession.snapshot.matchId,
        roundId: ownershipSession.snapshot.roundId,
        side: ownershipSession.snapshot.localSide,
        axis: input.axis,
      });
    },
  );
  const firstMatchId = "11111111111111111111111111111111";
  const secondMatchId = "22222222222222222222222222222222";
  let captureSequence = 0;
  const capture = (value) => ({
    ...value,
    sourceId: "abcdefabcdefabcdefabcdefabcdefab",
    snapshotSequence: ++captureSequence,
  });
  const active = {
    ...defaultSnapshot,
    opponentMode: "lan",
    role: "host",
    localNickname: "Лиса",
    peerNickname: "Кот",
    connection: "connected",
    phase: "playing",
    roundId: 10,
    matchId: firstMatchId,
    localSide: "left",
    leftY: 0.35,
    rightY: 0.65,
    tick: 100,
  };
  ownershipSession.apply(capture(active), "websocket");
  window.dispatch("keyup", { key: "w" });
  window.dispatch("keydown", { key: "w" });
  assert.equal(input.axis, -1);
  const beforeGoalClears = controls.length;
  ownershipSession.apply(
    capture({ ...active, phase: "countdown", leftScore: 1, tick: 101 }),
    "websocket",
  );
  assert.equal(input.axis, -1, "A goal/countdown in the same round keeps held input");
  assert.equal(controls.length, beforeGoalClears);
  ownershipSession.apply(
    capture({ ...active, phase: "gameover", leftScore: 7, tick: 102 }),
    "websocket",
  );
  assert.equal(
    input.axis,
    -1,
    "Finished score orientation and input fence remain until acceptance",
  );
  const swapped = { ...active, roundId: 11, localSide: "right", phase: "countdown", tick: 0 };
  ownershipSession.apply(capture(swapped), "websocket");
  assert.equal(input.axis, 0);
  assert.deepEqual(controls.at(-1), { matchId: firstMatchId, roundId: 11, side: "right", axis: 0 });
  document.dispatch("focusin", { target: new Element(true) });
  assert.equal(
    window.dispatch("keydown", { key: "w", target: new Element(true), repeat: true }).prevented,
    undefined,
    "Blocked game keys preserve native editable handling",
  );
  assert.equal(window.dispatch("keydown", { key: "w", repeat: true }).prevented, true);
  assert.equal(input.axis, 0, "A still-held key repeat cannot control the newly owned paddle");
  window.dispatch("keyup", { key: "w" });
  window.dispatch("keydown", { key: "w" });
  assert.equal(input.axis, -1, "A fresh press after release controls the new ownership");
  input.clear();
  window.dispatch("keyup", { key: "w" });
  downButton.dispatch("pointerdown", { pointerId: 81 });
  assert.equal(input.axis, 1);
  ownershipSession.apply(
    capture({ ...swapped, roundId: 12, localSide: "left", tick: 0 }),
    "websocket",
  );
  assert.equal(input.axis, 0, "A held touch clears on remote accepted rematch too");
  downButton.dispatch("pointerup", { pointerId: 81 });
  const accepted = ownershipSession.snapshot;
  const acceptedClears = controls.length;
  for (const stale of [
    swapped,
    { ...accepted, localSide: "right" },
    { ...accepted, roundId: 13 },
    { ...accepted, role: "guest" },
  ]) {
    ownershipSession.apply(capture(stale), "websocket");
    assert.strictEqual(ownershipSession.snapshot, accepted);
    assert.equal(controls.length, acceptedClears, "Rejected snapshots never clear current input");
  }
  // Losing browser focus can lose a keyup, so it releases the repeat suppression too.
  window.dispatch("keydown", { key: "ArrowUp" });
  input.clear(true);
  assert.equal(input.axis, 0);
  window.dispatch("blur");
  assert.equal(window.dispatch("keydown", { key: "ArrowUp", repeat: true }).prevented, true);
  assert.equal(input.axis, 0, "Repeat after lost focus cannot revive old held input");
  window.dispatch("keydown", { key: "ArrowUp", repeat: false });
  assert.equal(input.axis, -1);
  window.dispatch("keyup", { key: "ArrowUp" });

  // A new host context may begin at a lower round than an unrelated old match.
  ownershipSession.apply(
    capture({
      ...active,
      role: "guest",
      localSide: "left",
      matchId: secondMatchId,
      roundId: 1,
      tick: 1,
    }),
    "websocket",
  );
  assert.equal(ownershipSession.snapshot.matchId, secondMatchId);
  assert.equal(ownershipSession.snapshot.roundId, 1);
  ownershipSession.apply(capture({ ...active, roundId: 100 }), "websocket");
  assert.equal(
    ownershipSession.snapshot.matchId,
    secondMatchId,
    "A retired context never resumes via a delayed old frame",
  );

  // Capture ordering is independent of world ticks and ownership context.
  const ordered = new GameSession(
    new MotionModel(),
    { clearPulse() {}, process() {} },
    () => {},
    () => {},
  );
  const bootA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
  const bootB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
  const lobby = { ...defaultSnapshot, localNickname: "Лиса", sourceId: bootA, snapshotSequence: 1 };
  ordered.apply(lobby, "websocket");
  const admitted = { ...active, role: "guest", sourceId: bootA, snapshotSequence: 3 };
  ordered.apply(admitted, "http");
  ordered.apply({ ...lobby, snapshotSequence: 2 }, "websocket");
  assert.equal(ordered.snapshot.matchId, admitted.matchId);
  ordered.apply({ ...admitted, snapshotSequence: 4, tick: 90 }, "websocket");
  assert.equal(
    ordered.snapshot.tick,
    90,
    "A newer capture preserves a legitimate guest tick rebase",
  );
  ordered.apply({ ...admitted, matchId: secondMatchId, snapshotSequence: 2 }, "websocket");
  assert.equal(ordered.snapshot.matchId, admitted.matchId);
  ordered.apply({ ...lobby, sourceId: bootB, snapshotSequence: 1 }, "websocket");
  assert.equal(
    ordered.snapshot.sourceId,
    bootB,
    "A new server boot can restart its capture counter",
  );
  ordered.apply({ ...admitted, sourceId: bootA, snapshotSequence: 1000 }, "websocket");
  assert.equal(
    ordered.snapshot.sourceId,
    bootB,
    "Retired server captures never return after restart",
  );
  window.dispatch("keydown", { key: "w" });
  input.clear(true);
  document.hidden = true;
  document.dispatch("visibilitychange");
  document.hidden = false;
  assert.equal(window.dispatch("keydown", { key: "w", repeat: true }).prevented, true);
  assert.equal(input.axis, 0, "Repeat after hidden-page reset cannot revive held input");
  window.dispatch("keydown", { key: "w", repeat: false });
  assert.equal(input.axis, -1, "Fresh keys are not stranded after visibility loses a keyup");
  window.dispatch("keyup", { key: "w" });

  console.log(
    "Frontend input checks passed: keyboard and touch drive either physical paddle against a configured bot.",
  );
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
