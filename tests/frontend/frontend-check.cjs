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
const core = source.slice(0, startup);
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

run(`applySnapshot({ role: "host", connection: "connected", phase: "playing", roundId: 1,
  tick: 100, ballX: 0.2, ballY: 0.5, ballVx: 0.6, rightY: 0.4, leftY: 0.5 })`);
now += 1000 / 60;
run(`applySnapshot({ role: "host", connection: "connected", phase: "playing", roundId: 1,
  tick: 101, ballX: 0.21, ballY: 0.5, ballVx: 0.6, rightY: 0.42, leftY: 0.5 })`);
const stableDelay = run("interpolationDelayMs");
now += 54;
run(`applySnapshot({ role: "host", connection: "connected", phase: "playing", roundId: 1,
  tick: 102, ballX: 0.22, ballY: 0.5, ballVx: 0.6, rightY: 0.44, leftY: 0.5 })`);
const jitterDelay = run("interpolationDelayMs");
assert.ok(jitterDelay > stableDelay + 20 && jitterDelay <= 110);
run(`applySnapshot({ role: "host", connection: "connected", phase: "playing", roundId: 1,
  tick: 102, ballX: 0.22, ballY: 0.5, ballVx: 0.6, rightY: 0.44, leftY: 0.5 })`);
close(run("interpolationDelayMs"), jitterDelay);
assert.equal(run("motionSamples.length"), 3);

run(`motionSamples.length = 0;
  motionSamples.push({ tick: 100, arrivedAt: 1000, x: 0.2, y: 0.5, vx: 0.6, vy: 0, leftY: 0.5, rightY: 0.4 });
  motionSamples.push({ tick: 101, arrivedAt: 1016, x: 0.3, y: 0.5, vx: 0.6, vy: 0, leftY: 0.5, rightY: 0.6 });
  renderTick = 100.5; lastFrameTime = 1000;`);
const interpolated = run("displayedMotion(1000)");
close(interpolated.ballX, 0.25);
close(interpolated.rightY, 0.5);

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
assert.ok(cappedY <= 0.590001);
run("pressedKeys.delete('s')");
close(run("displayedLocalPaddle(1304)"), cappedY);
assert.ok(run("displayedLocalPaddle(1600)") < cappedY);

now += 200;
run(`applySnapshot({ role: "host", connection: "connected", phase: "playing", roundId: 1,
  tick: 120, ballX: 0.4, ballY: 0.5, ballVx: 0.6, rightY: 0.5, leftY: 0.5 })`);
assert.equal(run("motionSamples.length"), 1);

run("resizeArenaCache(960, 540, 2)");
const initialGradients = gradients;
run("drawArena(1400); drawArena(1416); resizeArenaCache(960, 540, 2)");
assert.equal(gradients, initialGradients);
assert.equal(imageDraws, 4);
run("resizeArenaCache(1200, 675, 2)");
assert.equal(gradients, initialGradients + 2);

console.log(
  "Frontend behavior checks passed: adaptive delay, duplicate handling, interpolation, prediction, gap reset, canvas caching.",
);
