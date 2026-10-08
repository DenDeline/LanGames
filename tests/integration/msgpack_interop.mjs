import { readFileSync } from "node:fs";
import { decode, encode } from "@msgpack/msgpack";

switch (process.argv[2]) {
  case "decode": {
    const bytes = Buffer.from(readFileSync(0, "utf8").trim(), "base64");
    process.stdout.write(JSON.stringify(decode(bytes), (_key, value) =>
      value?.type === "Buffer" && Array.isArray(value.data) ? value.data : value));
    break;
  }
  case "encode": {
    const value = JSON.parse(Buffer.from(readFileSync(0, "utf8").trim(), "base64").toString("utf8"));
    process.stdout.write(JSON.stringify(Buffer.from(encode(value)).toString("base64")));
    break;
  }
  case "encode-controls": {
    const { roundId, matchId } = JSON.parse(Buffer.from(readFileSync(0, "utf8").trim(), "base64").toString("utf8"));
    if (!Number.isSafeInteger(roundId) || roundId <= 0) throw new Error("Invalid round");
    if (typeof matchId !== "string" || !/^[0-9a-f]{32}$/.test(matchId) || /^0+$/.test(matchId))
      throw new Error("Invalid match");
    const controls = Object.fromEntries(
      [-1, 0, 1].map((axis) => [axis, Buffer.from(encode([9, matchId, roundId, axis])).toString("base64")]),
    );
    process.stdout.write(JSON.stringify(controls));
    break;
  }
  default:
    throw new Error("Expected decode or encode-controls");
}
