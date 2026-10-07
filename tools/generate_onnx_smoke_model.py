#!/usr/bin/env python3
"""Generate the tiny, deterministic ONNX model used by the Native AOT smoke test.

The model has one float32 input named ``input`` with shape [1] and one float32
output named ``output`` with shape [1]. Its only node is Add(input, one), where
``one`` is a float32 initializer containing 1.0. Thus [2.0] produces [3.0].

This writes the small subset of Protocol Buffers needed here directly, so
regenerating the fixture requires only Python's standard library. Field numbers
come from https://github.com/onnx/onnx/blob/main/onnx/onnx.proto3. The model
uses ONNX IR 7 and the standard-domain operator set 13.
"""

from __future__ import annotations

import argparse
import hashlib
import struct
from pathlib import Path


DEFAULT_OUTPUT = Path(__file__).resolve().parents[1] / "src/LanPong/Models/aot-smoke.onnx"


def varint(value: int) -> bytes:
    if value < 0:
        raise ValueError("this generator only encodes non-negative integers")
    encoded = bytearray()
    while value > 0x7F:
        encoded.append((value & 0x7F) | 0x80)
        value >>= 7
    encoded.append(value)
    return bytes(encoded)


def integer(field_number: int, value: int) -> bytes:
    return varint(field_number << 3) + varint(value)


def data(field_number: int, value: bytes | str) -> bytes:
    if isinstance(value, str):
        value = value.encode("utf-8")
    return varint((field_number << 3) | 2) + varint(len(value)) + value


def value_info(name: str) -> bytes:
    # ValueInfoProto.type.tensor_type: float32 tensor with one dimension of 1.
    dimension = integer(1, 1)  # TensorShapeProto.Dimension.dim_value
    shape = data(1, dimension)  # TensorShapeProto.dim
    tensor_type = integer(1, 1) + data(2, shape)  # elem_type=FLOAT, shape
    type_proto = data(1, tensor_type)  # TypeProto.tensor_type
    return data(1, name) + data(2, type_proto)


def model_bytes() -> bytes:
    # NodeProto: Add(input, one) -> output.
    node = data(1, "input") + data(1, "one") + data(2, "output") + data(4, "Add")

    # TensorProto: shape [1], type FLOAT, named "one", value 1.0 in little-endian raw_data.
    one = integer(1, 1) + integer(2, 1) + data(8, "one") + data(9, struct.pack("<f", 1.0))

    # GraphProto: node, name, initializer, input, output.
    graph = (
        data(1, node)
        + data(2, "aot_smoke")
        + data(5, one)
        + data(11, value_info("input"))
        + data(12, value_info("output"))
    )

    # ModelProto: ir_version, producer_name, graph, opset_import(version=13).
    return integer(1, 7) + data(2, "LanPong") + data(7, graph) + data(8, integer(2, 13))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--check", action="store_true", help="verify the existing fixture without writing")
    args = parser.parse_args()

    expected = model_bytes()
    if args.check:
        if not args.output.exists() or args.output.read_bytes() != expected:
            parser.error(f"{args.output} is missing or differs from the generated model")
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_bytes(expected)

    print(f"{args.output}: {len(expected)} bytes, sha256 {hashlib.sha256(expected).hexdigest()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
