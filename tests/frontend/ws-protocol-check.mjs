import assert from "node:assert/strict";
import { encode, decode } from "@msgpack/msgpack";
import { defaultSnapshot, parseSnapshot } from "../../.artifacts/frontend-test/snapshot.js";
import { parseBotCatalog } from "../../.artifacts/frontend-test/botCatalog.js";
import { decodeWsSnapshot, encodeWsAxis } from "../../.artifacts/frontend-test/wsProtocol.js";

const sourceId = "abcdefabcdefabcdefabcdefabcdefab";
const matchId = "1234567890abcdef1234567890abcdef";
const snapshot = [
  9,
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
  "Лиса",
  "Кот",
  1,
  null,
  null,
  null,
  null,
  false,
  null,
  1,
  matchId,
  sourceId,
  100,
  false,
];
const frame = (value) => Uint8Array.from(encode(value)).buffer;
const expected = {
  ...defaultSnapshot,
  role: "host",
  localSide: "left",
  matchId,
  sourceId,
  snapshotSequence: 100,
  opponentMode: "lan",
  connection: "connected",
  message: "Игра началась!",
  udpPort: 47777,
  localAddresses: ["192.168.1.42"],
  peerAddress: "192.168.1.43:47777",
  localNickname: "Лиса",
  peerNickname: "Кот",
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
};
assert.equal(snapshot.length, 35);
for (const axis of [-1, 0, 1]) {
  assert.deepEqual(decode(new Uint8Array(encodeWsAxis(matchId, 7, axis))), [9, matchId, 7, axis]);
}
assert.throws(() => encodeWsAxis(matchId, 7, 2), RangeError);
assert.throws(() => encodeWsAxis(matchId, 7, 0.5), RangeError);
assert.throws(() => encodeWsAxis(matchId, 0, 1), RangeError);
assert.throws(() => encodeWsAxis(matchId, -1, 0), RangeError);
assert.throws(() => encodeWsAxis(matchId, 1.5, 0), RangeError);
assert.strictEqual(encodeWsAxis(matchId, 7, 1), encodeWsAxis(matchId, 7, 1));
assert.deepEqual(decodeWsSnapshot(frame(snapshot)), expected);
const humanRight = [...snapshot];
humanRight[30] = 2;
assert.deepEqual(decodeWsSnapshot(frame(humanRight)), { ...expected, localSide: "right" });
for (const invalidSource of [undefined, null, 0, "", sourceId.toUpperCase(), "0".repeat(32)])
  assert.throws(() => parseSnapshot({ ...expected, sourceId: invalidSource }), TypeError);
for (const canRematch of [undefined, null, 0, 1, "true"])
  assert.throws(() => parseSnapshot({ ...expected, canRematch }), TypeError);
assert.throws(() => parseSnapshot({ ...expected, canRematch: true }), TypeError);
assert.equal(parseSnapshot({ ...expected, phase: "gameover", canRematch: true }).canRematch, true);
assert.equal(
  parseSnapshot({ ...expected, phase: "gameover", canRematch: false }).canRematch,
  false,
);
for (const snapshotSequence of [undefined, null, 0, -1, 0.5, "100", Number.MAX_SAFE_INTEGER + 1])
  assert.throws(() => parseSnapshot({ ...expected, snapshotSequence }), TypeError);
for (const invalidMatch of [
  undefined,
  null,
  0,
  "",
  "random",
  matchId.toUpperCase(),
  "0".repeat(31),
  "0".repeat(32),
])
  assert.throws(() => parseSnapshot({ ...expected, matchId: invalidMatch }), TypeError);
assert.throws(() => encodeWsAxis("invalid", 7, 1), RangeError);
assert.throws(() => encodeWsAxis("0".repeat(32), 7, 1), RangeError);
for (const localSide of [undefined, null, 0, 1, 2, "random", "Left", "none"])
  assert.throws(() => parseSnapshot({ ...expected, localSide }), TypeError);
for (const localSide of ["left", "right"])
  assert.throws(
    () => parseSnapshot({ ...expected, connection: "incomingChallenge", localSide }),
    TypeError,
  );
assert.equal(parseSnapshot({ ...expected, connection: "disconnected" }).localSide, "left");
assert.deepEqual(
  parseSnapshot({ ...expected, events: undefined, recentEvents: expected.events }),
  expected,
);

const confirmedFinish = [...snapshot];
confirmedFinish[15] = 3;
confirmedFinish[34] = true;
assert.equal(decodeWsSnapshot(frame(confirmedFinish))?.canRematch, true);
assert.deepEqual(
  parseSnapshot(decodeWsSnapshot(frame(confirmedFinish))),
  decodeWsSnapshot(frame(confirmedFinish)),
);
const predictedFinish = [...confirmedFinish];
predictedFinish[34] = false;
assert.equal(decodeWsSnapshot(frame(predictedFinish))?.canRematch, false);
const ipv6 = [...snapshot];
ipv6[5] = ["::1", "fe80::1234%3"];
ipv6[6] = "[::1]:47777";
assert.deepEqual(decodeWsSnapshot(frame(ipv6))?.localAddresses, ipv6[5]);
assert.equal(decodeWsSnapshot(frame(ipv6))?.peerAddress, ipv6[6]);

const idle = [...snapshot];
idle[1] = idle[2] = idle[4] = idle[15] = idle[23] = 0;
idle[6] = idle[19] = idle[22] = idle[30] = idle[31] = null;
assert.equal(decodeWsSnapshot(frame(idle))?.opponentMode, "none");
assert.equal(decodeWsSnapshot(frame(idle))?.requestedBotId, null);

const local = [...snapshot];
local[4] = 0;
local[6] = local[19] = local[22] = null;
local[23] = 2;
local[24] = local[26] = "config-only-opponent";
local[25] = local[27] = "Бот с именем длиннее человеческого ника — ".padEnd(64, "я");
assert.equal(local[25].length, 64);
const decodedLocal = decodeWsSnapshot(frame(local));
assert.equal(decodedLocal?.opponentMode, "bot");
assert.equal(decodedLocal?.peerNickname, null);
assert.equal(decodedLocal?.requestedBotId, "config-only-opponent");
assert.equal(decodedLocal?.effectiveBotName.length, 64);
assert.deepEqual(parseSnapshot(decodedLocal), decodedLocal);
const fallback = [...local];
fallback[26] = "configured-rescue";
fallback[27] = "Другой настроенный соперник";
fallback[28] = true;
fallback[29] = "Выбранный бот перестал отвечать.";
const decodedFallback = decodeWsSnapshot(frame(fallback));
assert.equal(decodedFallback?.requestedBotId, "config-only-opponent");
assert.equal(decodedFallback?.effectiveBotId, "configured-rescue");
assert.equal(decodedFallback?.opponentFallbackActive, true);
assert.deepEqual(parseSnapshot(decodedFallback), decodedFallback);

for (const version of [undefined, 7, 6, 8, "9"]) {
  assert.throws(() => parseSnapshot({ ...expected, version }), RangeError);
}
for (const value of ["simple", "hard", "unknown", 2, null]) {
  assert.throws(() => parseSnapshot({ ...expected, opponentMode: value }), TypeError);
}
assert.throws(() => parseSnapshot({ version: 9 }), TypeError);
for (const [ordinal, name] of [
  [4, "incomingChallenge"],
  [5, "awaitingAcceptance"],
  [6, "searching"],
]) {
  const pending = [...snapshot];
  pending[2] = ordinal;
  pending[15] = 0;
  pending[30] = pending[31] = null;
  assert.equal(decodeWsSnapshot(frame(pending))?.connection, name);
}
function reject(index, value, source = snapshot) {
  const changed = [...source];
  changed[index] = value;
  assert.equal(decodeWsSnapshot(frame(changed)), null, `Unexpected acceptance of field ${index}`);
}
for (const version of [7, 6, 8, "9"]) reject(0, version);
for (const value of [undefined, null, 0, 1, "true", true]) reject(34, value);
reject(1, 3);
reject(2, "connected");
reject(2, 7);
reject(4, -1);
reject(5, ["127.0.0.1", 5]);
reject(7, "0.5");
reject(17, -1);
reject(20, null);
reject(21, "");
reject(21, "x".repeat(25));
reject(21, "bad\u0000nickname");
reject(22, "x".repeat(25));
reject(22, 42);
reject(23, 3);
reject(23, "bot");
reject(24, "outside-bot");
reject(28, 1);
reject(29, "outside-bot");
for (const value of [undefined, null, 0, 3, "left", "random"]) reject(30, value);
for (const value of [undefined, null, 0, "invalid", matchId.toUpperCase()]) reject(31, value);
for (const value of [undefined, null, 0, "invalid", sourceId.toUpperCase()]) reject(32, value);
for (const value of [undefined, null, 0, -1, 1.5, Number.MAX_SAFE_INTEGER + 1]) reject(33, value);
reject(24, "UPPERCASE", local);
reject(24, "bad--id", local);
reject(24, "valid-looking\n", local);
reject(24, "a".repeat(65), local);
reject(25, "x".repeat(65), local);
reject(26, null, local);
reject(27, "", local);
reject(22, "Компьютер", local);
reject(4, 47777, local);
reject(6, "127.0.0.1:47777", local);
reject(19, 8, local);
reject(29, "Unexpected reason", local);
reject(28, true, local);
reject(29, null, fallback);
reject(26, local[24], fallback);
reject(
  20,
  Array.from({ length: 13 }, (_, i) => [`event-${i}`, 1, 1, 0.5, 0.5]),
);
for (const event of [
  "goal",
  ["id", 4, 123, 1],
  ["", 4, 123, 1, 0.5],
  ["x".repeat(81), 4, 123, 1, 0.5],
  ["id", 0, 123, 1, 0.5],
  ["id", 6, 123, 1, 0.5],
  ["id", 4.5, 123, 1, 0.5],
  ["id", 4, -1, 1, 0.5],
  ["id", 4, 1.5, 1, 0.5],
  ["id", 4, 123, -0.01, 0.5],
  ["id", 4, 123, 1.01, 0.5],
  ["id", 4, 123, 1, Number.NaN],
])
  reject(20, [event]);
for (const changes of [
  { requestedBotId: "wrong--id" },
  { requestedBotName: "x".repeat(65) },
  { effectiveBotId: null },
  { peerNickname: "Компьютер" },
  { udpPort: 47777 },
  { opponentFallbackActive: true },
])
  assert.throws(() => parseSnapshot({ ...decodedLocal, ...changes }), TypeError);
assert.equal(decodeWsSnapshot(frame(snapshot.slice(0, -1))), null);
assert.equal(decodeWsSnapshot(frame({ role: "host" })), null);
assert.equal(decodeWsSnapshot(new ArrayBuffer(0)), null);
const valid = new Uint8Array(frame(snapshot));
const trailing = new Uint8Array(valid.length + 1);
trailing.set(valid);
assert.equal(decodeWsSnapshot(trailing.buffer), null);
assert.equal(decodeWsSnapshot(frame(snapshot))?.tick, 123456);

const catalogBot = {
  id: "config-only-opponent",
  name: local[25],
  description: "Настроенный соперник",
  style: "Стиль",
  difficulty: "Сложность",
  category: "Категория",
  order: 10,
  glyph: "◇",
  enabled: true,
  fallbackBotId: null,
  availability: "notChecked",
  availabilityReason: "Проверим при выборе.",
  canPlay: true,
};
const catalog = { version: 9, defaultBotId: catalogBot.id, bots: [catalogBot] };
assert.deepEqual(parseBotCatalog(catalog), catalog);
assert.deepEqual(parseBotCatalog({ ...catalog, bots: [] }).bots, []);
for (const version of [undefined, 7, 8])
  assert.throws(() => parseBotCatalog({ ...catalog, version }), RangeError);
for (const changes of [
  { id: "Upper" },
  { id: "valid-looking\n" },
  { name: "x".repeat(65) },
  { description: "x".repeat(513) },
  { style: "x".repeat(129) },
  { difficulty: "" },
  { category: "x".repeat(65) },
  { glyph: "x".repeat(17) },
  { order: -1 },
  { enabled: "true" },
  { availability: "unknown" },
  { canPlay: 1 },
  { fallbackBotId: "bad--id" },
  { enabled: false },
  { availabilityReason: 10 },
])
  assert.throws(
    () => parseBotCatalog({ ...catalog, bots: [{ ...catalogBot, ...changes }] }),
    TypeError,
  );
assert.throws(() => parseBotCatalog({ ...catalog, bots: [catalogBot, catalogBot] }), TypeError);
console.log(
  "Frontend v9 protocol checks passed: strict HTTP/MessagePack parity, bot identities, catalog, and invalid frames.",
);
