const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

// pnpm test:frontend emits this JavaScript with the TypeScript 7 compiler first.
const source = fs.readFileSync(
  path.resolve(__dirname, "../../.artifacts/frontend-test/game.js"),
  "utf8",
);
const startup = source.indexOf("export function startGame()");
assert.ok(startup > 0, "startGame must mark the browser-only initialization");
// The protocol module is exercised separately; the VM only needs game logic.
const core = source.slice(0, startup).replace(/^import .*;\r?\n/gm, "");
let now = 1000;
let gradients = 0;
let imageDraws = 0;
const drawingContext = () => ({
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
});
const elements = new Map();
function element(id = "") {
  if (elements.has(id)) return elements.get(id);
  const item = {
    dataset: {},
    classList: { toggle() {} },
    textContent: "",
    hidden: false,
    value: "47777",
    clientWidth: 960,
    clientHeight: 540,
    width: 960,
    height: 540,
    setAttribute() {},
    replaceChildren() {},
    append() {},
    addEventListener() {},
    getContext() {
      return drawingContext();
    },
  };
  elements.set(id, item);
  return item;
}
const context = vm.createContext({
  document: {
    getElementById: element,
    createElement: () => ({ ...element(`new-${Math.random()}`) }),
  },
  window: { devicePixelRatio: 2 },
  performance: { now: () => now },
  setTimeout() {},
  clearTimeout() {},
  console,
  Math,
  Number,
  String,
  Set,
  JSON,
  Array,
});
vm.runInContext(core, context);
const run = (expression) => vm.runInContext(expression, context);
const close = (actual, expected) =>
  assert.ok(Math.abs(actual - expected) < 1e-6, `${actual} != ${expected}`);
const apply = (changes, source = "websocket") => {
  const state = {
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
  };
  run(`applySnapshot(${JSON.stringify(state)}, ${JSON.stringify(source)})`);
};

apply({});
close(run("displayedMotion(1000).ballX"), 0.2);
close(run("displayedMotion(1000 + 1000 / 120).ballX"), 0.205);
close(run("displayedMotion(1040).ballX"), 0.22);
now += 1000 / 60;
apply({ tick: 101, ballX: 0.21, rightY: 0.4 + 0.85 / 60 });
close(run(`displayedMotion(${now}).ballX`), 0.21);
close(run(`displayedMotion(${now + 1000 / 120}).rightY`), 0.4 + (1.5 * 0.85) / 60);
// A delayed delivery still shows the newest simulated tick immediately.
now += 54;
apply({ tick: 102, ballX: 0.22, rightY: 0.4 + (2 * 0.85) / 60 });
close(run(`displayedMotion(${now}).ballX`), 0.22);
assert.equal(run("motionSamples.length"), 2);
const lastArrival = run("motionSamples.at(-1).arrivedAt");
now += 20;
apply({ tick: 102, ballX: 0.22, rightY: 0.4 + (2 * 0.85) / 60 });
close(run("motionSamples.at(-1).arrivedAt"), lastArrival);
close(run(`displayedMotion(${now}).ballX`), 0.232);

// A same-tick rollback correction replaces the sample. Small changes ease in.
now += 1000 / 60;
apply({ tick: 103, ballX: 0.23, rightY: 0.4 + (3 * 0.85) / 60 });
apply({ tick: 103, ballX: 0.225, rightY: 0.4 + (3 * 0.85) / 60 });
assert.equal(run("motionSamples.length"), 2);
assert.ok(run("motionCorrection !== null"));
close(run(`displayedMotion(${now}).ballX`), 0.23);
assert.ok(run(`displayedMotion(${now + 15}).ballX`) > 0.23);
close(run(`displayedMotion(${now + 30}).ballX`), 0.243);

// A trajectory change must show the new bounce without crossing the paddle.
const rightContact = run("RIGHT_CONTACT_X");
now += 100;
apply({ tick: 110, ballX: rightContact - 0.002, ballVx: 0.6 });
assert.equal(run("motionSamples.length"), 1);
assert.equal(run("motionCorrection"), null);
close(run(`displayedMotion(${now + 20}).ballX`), rightContact);
now += 1000 / 60;
apply({ tick: 111, ballX: rightContact - 0.004, ballVx: -0.6 });
assert.equal(run("motionCorrection"), null);
close(run(`displayedMotion(${now}).ballX`), rightContact - 0.004);
now += 1000 / 60;
apply({ tick: 110, ballX: 0.1, ballVx: 0.6 }, "http");
close(run("snapshot.tick"), 111);
now += 1000 / 60;
apply({ tick: 300, ballX: 0.3 });
now += 1000 / 60;
apply({ tick: 291, ballX: 0.27 });
close(run("snapshot.tick"), 291);
assert.equal(run("motionSamples.length"), 1);
assert.equal(run("motionCorrection"), null);
now += 1000 / 60;
apply({ tick: 290, ballX: 0.26 }, "http");
close(run("snapshot.tick"), 291);
now += 1000 / 60;
apply({ tick: 150, ballX: 0.2 });
close(run("snapshot.tick"), 150);
assert.equal(run("motionSamples.length"), 1);
assert.equal(run("motionCorrection"), null);

// A score or phase transition discards any motion correction.
now += 1000 / 60;
apply({ tick: 151, phase: "countdown", ballX: 0.5, ballVx: 0, leftScore: 1 });
assert.equal(run("motionSamples.length"), 1);
assert.equal(run("motionCorrection"), null);

run("resetMotionHistory(); pressedKeys.add('s')");
const startY = run("displayedLocalPaddle(1000)");
const heldY = run("displayedLocalPaddle(1016)");
assert.ok(heldY > startY);
const heldAgainY = run("displayedLocalPaddle(1032)");
assert.ok(heldAgainY > heldY);
let cappedY = heldAgainY;
for (let frame = 1048; frame <= 1288; frame += 16) {
  const nextY = run(`displayedLocalPaddle(${frame})`);
  assert.ok(nextY >= cappedY);
  cappedY = nextY;
}
assert.ok(cappedY <= 0.5 + (2 * 0.85) / 60 + 0.000001);
run("pressedKeys.delete('s')");
close(run("displayedLocalPaddle(1304)"), cappedY);
assert.ok(run("displayedLocalPaddle(1600)") < cappedY);

now += 200;
apply({ tick: 120, ballX: 0.4, rightY: 0.5 });
assert.equal(run("motionSamples.length"), 1);

run("resizeArenaCache(960, 540, 2)");
const initialGradients = gradients;
run("drawArena(1400); drawArena(1416); resizeArenaCache(960, 540, 2)");
assert.equal(gradients, initialGradients);
assert.equal(imageDraws, 4);
run("resizeArenaCache(1200, 675, 2)");
assert.equal(gradients, initialGradients + 2);

console.log(
  "Frontend behavior checks passed: present-time rendering, correction, collision stop, prediction, gap reset, canvas caching.",
);
