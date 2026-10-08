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
  const [{ InputController }, { MotionModel }, { defaultSnapshot }] = await Promise.all([
    import("../../.artifacts/frontend-test/input.js"),
    import("../../.artifacts/frontend-test/motion.js"),
    import("../../.artifacts/frontend-test/snapshot.js"),
  ]);

  globalThis.Element = class {
    constructor(editable = false) {
      this.editable = editable;
    }
    closest(selector) {
      const tag =
        typeof this.editable === "string" ? this.editable : this.editable ? "input" : null;
      return tag &&
        selector
          .split(",")
          .map((part) => part.trim())
          .includes(tag)
        ? this
        : null;
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

  console.log(
    "Frontend input checks passed: keyboard and touch drive the left paddle against a configured bot.",
  );
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
