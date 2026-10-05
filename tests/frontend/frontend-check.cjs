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
const scoreFlashes = { left: 0, right: 0 };
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
  if (id === "left-score" || id === "right-score") {
    const side = id === "left-score" ? "left" : "right";
    item.parentElement = {
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

// HTTP snapshots retain only valid event records, including numeric enum kinds.
const parsedEvents = JSON.parse(
  run(
    `JSON.stringify(parseSnapshot({recentEvents:[
      {id:"valid-goal",kind:"goal",tick:120,x:0,y:0.4},
      {id:"valid-paddle",kind:2,tick:121,x:0.05,y:0.5},
      {id:"invalid-kind",kind:6,tick:122,x:0.5,y:0.5},
      {id:"invalid-position",kind:"wall",tick:122,x:-0.01,y:0.5}
    ]}).events)`,
  ),
);
assert.deepEqual(parsedEvents, [
  { id: "valid-goal", kind: "goal", tick: 120, x: 0, y: 0.4 },
  { id: "valid-paddle", kind: "paddle", tick: 121, x: 0.05, y: 0.5 },
]);

// Reconnecting into a live match must not replay its recent event history.
apply({ role: "none", connection: "idle", events: [] });
const historicPaddle = { id: "20:4:2", kind: "paddle", tick: 1, x: 0.05, y: 0.5 };
const newGoal = { id: "20:5:4", kind: "goal", tick: 2, x: 1, y: 0.4 };
apply({ roundId: 20, tick: 1, events: [historicPaddle] });
assert.equal(run("feedbackPulse"), null);
assert.equal(scoreFlashes.left, 0);
assert.equal(element("left-player").textContent, "Вы");
assert.equal(element("right-player").textContent, "Соперник");

// A new event gives one local score flash. Repeated snapshots and a same-tick
// correction must leave the event feedback unchanged.
now += 10;
apply({ roundId: 20, tick: 2, leftScore: 1, events: [historicPaddle, newGoal] });
assert.equal(run("feedbackPulse.kind"), "goal");
assert.equal(run("feedbackPulse.scorer"), "left");
assert.equal(scoreFlashes.left, 1);
const pulseAt = run("feedbackPulse.startedAt");
now += 10;
apply({ roundId: 20, tick: 3, leftScore: 1, events: [historicPaddle, newGoal] });
assert.equal(run("feedbackPulse.startedAt"), pulseAt);
assert.equal(scoreFlashes.left, 1);
now += 10;
apply({ roundId: 20, tick: 3, leftScore: 1, ballX: 0.205, events: [historicPaddle, newGoal] });
assert.equal(run("feedbackPulse.startedAt"), pulseAt);
assert.equal(scoreFlashes.left, 1);

// A lower authoritative WebSocket tick resets visual motion, but an already
// heard or flashed event is still remembered across the rollback.
now += 10;
apply({ roundId: 20, tick: 2, leftScore: 1, events: [historicPaddle, newGoal] });
assert.equal(run("feedbackPulse"), null);
assert.equal(scoreFlashes.left, 1);
now += 10;
apply({ roundId: 20, tick: 3, leftScore: 1, events: [historicPaddle, newGoal] });
assert.equal(run("feedbackPulse"), null);
assert.equal(scoreFlashes.left, 1);

const laterWall = { id: "20:6:3", kind: "wall", tick: 4, x: 0.4, y: 0.012 };
apply({ roundId: 20, tick: 2, leftScore: 1, events: [historicPaddle, newGoal, laterWall] }, "http");
assert.equal(run("feedbackPulse"), null);
assert.equal(run("seenEventIds.has('20:6:3')"), false);
apply({ roundId: 20, tick: 4, leftScore: 1, events: [historicPaddle, newGoal, laterWall] });
assert.equal(run("feedbackPulse.kind"), "wall");
assert.equal(scoreFlashes.left, 1);

// A browser socket reconnect baselines its history instead of playing every
// event that happened while the browser was offline.
const reconnectEvent = { id: "20:7:2", kind: "paddle", tick: 5, x: 0.05, y: 0.4 };
const beforeReconnectPulse = run("feedbackPulse.startedAt");
run("resyncFeedbackOnNextSnapshot = true");
now += 10;
apply({ roundId: 20, tick: 5, leftScore: 1, events: [reconnectEvent] });
assert.equal(run("feedbackPulse.startedAt"), beforeReconnectPulse);
assert.equal(run("seenEventIds.has('20:7:2')"), true);
const afterReconnectEvent = { id: "20:8:3", kind: "wall", tick: 6, x: 0.4, y: 0.012 };
now += 10;
apply({ roundId: 20, tick: 6, leftScore: 1, events: [reconnectEvent, afterReconnectEvent] });
assert.equal(run("feedbackPulse.kind"), "wall");
assert.equal(run("feedbackPulse.startedAt"), now);

// The guest's local score sits on the right; the goal coordinate identifies
// the side that conceded, independent of snapshot score transitions.
apply({ role: "none", connection: "idle", events: [] });
apply({ role: "guest", roundId: 21, tick: 1, events: [historicPaddle] });
assert.equal(element("left-player").textContent, "Соперник");
assert.equal(element("right-player").textContent, "Вы");
const guestGoal = { id: "21:5:4", kind: "goal", tick: 2, x: 0, y: 0.6 };
apply({ role: "guest", roundId: 21, tick: 2, rightScore: 1, events: [guestGoal] });
assert.equal(run("feedbackPulse.scorer"), "right");
assert.equal(scoreFlashes.right, 1);

// Feedback is safe without Web Audio (all events above), and audible when the
// browser provides it. Muting prevents new tones without hiding visual feedback.
let playedTones = 0;
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
    };
  }
  createOscillator() {
    return {
      frequency: { setValueAtTime() {}, exponentialRampToValueAtTime() {} },
      connect() {},
      start() {
        playedTones++;
      },
      stop() {},
    };
  }
}
context.window.AudioContext = AudioContextStub;
const guestPaddle = { id: "21:6:2", kind: "paddle", tick: 3, x: 0.95, y: 0.6 };
apply({ role: "guest", roundId: 21, tick: 3, rightScore: 1, events: [guestGoal, guestPaddle] });
assert.equal(playedTones, 1);
run("soundEnabled = false");
const mutedWall = { id: "21:7:3", kind: "wall", tick: 4, x: 0.5, y: 0.012 };
apply({ role: "guest", roundId: 21, tick: 4, rightScore: 1, events: [mutedWall] });
assert.equal(playedTones, 1);
assert.equal(run("feedbackPulse.kind"), "wall");
run("soundEnabled = true");
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
    events: [{ id: `21:${tick}:${kind}`, kind, tick, x: 0.5, y: 0.5 }],
  });
  assert.equal(run("feedbackPulse.kind"), kind);
  assert.equal(playedTones, tones);
}

console.log(
  "Frontend behavior checks passed: motion, prediction, canvas caching, event parsing, score feedback, and replay deduplication.",
);
