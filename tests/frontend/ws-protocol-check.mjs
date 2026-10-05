import assert from "node:assert/strict";
import { encode } from "@msgpack/msgpack";
import { decodeWsSnapshot, encodeWsAxis } from "../../.artifacts/frontend-test/wsProtocol.js";

const snapshot = [
  2,
  1,
  3,
  "Игра началась!",
  47777,
  ["192.168.1.42"],
  "192.168.1.43:47777",
  0.425,
  0.563,
  0.712375,
  0.2218,
  0.4321,
  -0.348,
  3,
  4,
  2,
  0,
  123456,
  7,
  8.42,
  [
    ["123451:0:1", 1, 123451, 0.5, 0.5],
    ["123452:0:2", 2, 123452, 0.05, 0.5],
    ["123453:0:3", 3, 123453, 0.4, 0.012],
    ["123454:0:4", 4, 123454, 1, 0.45],
    ["123455:0:5", 5, 123455, 0.5, 0.5],
  ],
];

function frame(value) {
  return Uint8Array.from(encode(value)).buffer;
}

assert.equal(snapshot.length, 21);
assert.deepEqual(Array.from(new Uint8Array(encodeWsAxis(-1))), [0x92, 2, 0xff]);
assert.deepEqual(Array.from(new Uint8Array(encodeWsAxis(0))), [0x92, 2, 0]);
assert.deepEqual(Array.from(new Uint8Array(encodeWsAxis(1))), [0x92, 2, 1]);
assert.throws(() => encodeWsAxis(2), RangeError);
assert.throws(() => encodeWsAxis(0.5), RangeError);

const decoded = decodeWsSnapshot(frame(snapshot));
assert.deepEqual(decoded, {
  role: "host",
  connection: "connected",
  message: "Игра началась!",
  udpPort: 47777,
  localAddresses: ["192.168.1.42"],
  peerAddress: "192.168.1.43:47777",
  leftY: 0.425,
  rightY: 0.563,
  ballX: 0.712375,
  ballY: 0.2218,
  ballVx: 0.4321,
  ballVy: -0.348,
  leftScore: 3,
  rightScore: 4,
  phase: "playing",
  countdown: 0,
  tick: 123456,
  roundId: 7,
  pingMs: 8.42,
  events: [
    { id: "123451:0:1", kind: "serve", tick: 123451, x: 0.5, y: 0.5 },
    { id: "123452:0:2", kind: "paddle", tick: 123452, x: 0.05, y: 0.5 },
    { id: "123453:0:3", kind: "wall", tick: 123453, x: 0.4, y: 0.012 },
    { id: "123454:0:4", kind: "goal", tick: 123454, x: 1, y: 0.45 },
    { id: "123455:0:5", kind: "match", tick: 123455, x: 0.5, y: 0.5 },
  ],
});

const idle = [...snapshot];
idle[1] = 0;
idle[2] = 0;
idle[6] = null;
idle[15] = 0;
idle[19] = null;
assert.equal(decodeWsSnapshot(frame(idle))?.role, "none");
assert.equal(decodeWsSnapshot(frame(idle))?.peerAddress, null);
assert.equal(decodeWsSnapshot(frame(idle))?.pingMs, null);

function reject(index, value) {
  const changed = [...snapshot];
  changed[index] = value;
  assert.equal(decodeWsSnapshot(frame(changed)), null);
}

reject(0, 1); // Unsupported protocol version.
reject(1, 3); // Unknown role enum.
reject(2, "connected"); // JSON enum is not valid on the binary socket.
reject(4, -1); // Invalid UDP port.
reject(5, ["127.0.0.1", 5]); // Invalid address element.
reject(7, "0.5"); // Invalid coordinate.
reject(17, -1); // Invalid tick.
reject(20, null); // Events must be an array.
reject(
  20,
  Array.from({ length: 13 }, (_, index) => [`event-${index}`, 1, 1, 0.5, 0.5]),
);

function rejectEvent(event) {
  reject(20, [event]);
}

rejectEvent("goal");
rejectEvent(["id", 4, 123, 1]);
rejectEvent(["", 4, 123, 1, 0.5]);
rejectEvent(["x".repeat(81), 4, 123, 1, 0.5]);
rejectEvent(["id", 0, 123, 1, 0.5]);
rejectEvent(["id", 6, 123, 1, 0.5]);
rejectEvent(["id", 4.5, 123, 1, 0.5]);
rejectEvent(["id", 4, -1, 1, 0.5]);
rejectEvent(["id", 4, 1.5, 1, 0.5]);
rejectEvent(["id", 4, 123, -0.01, 0.5]);
rejectEvent(["id", 4, 123, 1.01, 0.5]);
rejectEvent(["id", 4, 123, 1, Number.NaN]);
assert.equal(decodeWsSnapshot(frame(snapshot.slice(0, -1))), null);
assert.equal(decodeWsSnapshot(frame({ role: "host" })), null);
assert.equal(decodeWsSnapshot(new ArrayBuffer(0)), null);

const valid = new Uint8Array(frame(snapshot));
const trailing = new Uint8Array(valid.length + 1);
trailing.set(valid);
assert.equal(decodeWsSnapshot(trailing.buffer), null);
assert.equal(decodeWsSnapshot(frame(snapshot))?.tick, 123456);

console.log("Frontend MessagePack checks passed: controls, snapshots, and invalid frames.");
