import { readFileSync } from "node:fs";
import { decode, encode } from "@msgpack/msgpack";

switch (process.argv[2]) {
  case "decode": {
    const bytes = Buffer.from(readFileSync(0, "utf8").trim(), "base64");
    process.stdout.write(JSON.stringify(decode(bytes)));
    break;
  }
  case "encode-controls": {
    const controls = Object.fromEntries(
      [-1, 0, 1].map((axis) => [axis, Buffer.from(encode([1, axis])).toString("base64")]),
    );
    process.stdout.write(JSON.stringify(controls));
    break;
  }
  default:
    throw new Error("Expected decode or encode-controls");
}
