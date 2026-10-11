#!/usr/bin/env python3
"""Generate the pinned independent shortest-float text oracle fixture."""

from __future__ import annotations

from collections import Counter
from decimal import Decimal
import json
import math
from pathlib import Path
import random
import struct
import sys
import hashlib


PINNED_PYTHON_VERSION = (3, 12, 10)
FRACTION_MASK = (1 << 52) - 1
EXPONENT_MASK = 0x7FF0000000000000
SIGN_MASK = 1 << 63


def bits_of(value: float) -> int:
    return struct.unpack(">Q", struct.pack(">d", value))[0]


def value_of(bits: int) -> float:
    return struct.unpack(">d", struct.pack(">Q", bits))[0]


def canonical_text(value: float) -> str:
    """Render CPython's shortest decimal tuple using the AgentLang contract."""
    shortest = repr(value)
    decimal = Decimal(shortest)
    sign, raw_digits, exponent = decimal.as_tuple()
    digits = "".join(str(digit) for digit in raw_digits)

    if not any(raw_digits):
        return "-0.0" if sign else "0.0"

    digits = digits.lstrip("0") or "0"
    while len(digits) > 1 and digits[-1] == "0":
        digits = digits[:-1]
        exponent += 1

    adjusted_exponent = exponent + len(digits) - 1
    if -4 <= adjusted_exponent <= 16:
        point = adjusted_exponent + 1
        if point <= 0:
            body = "0." + "0" * (-point) + digits
        elif point >= len(digits):
            body = digits + "0" * (point - len(digits))
        else:
            body = digits[:point] + "." + digits[point:]
        if "." not in body and "E" not in body:
            body += ".0"
    else:
        mantissa = digits[0]
        if len(digits) > 1:
            mantissa += "." + digits[1:]
        exponent_sign = "+" if adjusted_exponent >= 0 else "-"
        body = f"{mantissa}E{exponent_sign}{abs(adjusted_exponent):02d}"

    return ("-" if sign else "") + body


def main() -> None:
    if sys.implementation.name != "cpython" or sys.version_info[:3] != PINNED_PYTHON_VERSION:
        wanted = ".".join(map(str, PINNED_PYTHON_VERSION))
        raise SystemExit(f"fixture generation requires CPython {wanted}; got {sys.version}")

    samples: dict[int, tuple[str, int]] = {}
    categories: Counter[str] = Counter()

    def add(bits: int, category: str) -> None:
        if (bits & EXPONENT_MASK) == EXPONENT_MASK:
            return
        categories[category] += 1
        if bits in samples:
            return
        value = value_of(bits)
        text = canonical_text(value)
        roundtrip_bits = bits_of(float(text))
        if roundtrip_bits != bits:
            raise AssertionError(f"CPython text failed bit round-trip: {bits:016X} -> {text} -> {roundtrip_bits:016X}")
        samples[bits] = (text, roundtrip_bits)

    contract_vectors = [
        0x0000000000000000,
        0x8000000000000000,
        0x0000000000000001,
        0x0000000000000002,
        0x000FFFFFFFFFFFFF,
        0x0010000000000000,
        0x0010000000000001,
        0x3FF0000000000000,
        0x3FF0000000000001,
        0x3FF3C0CA428C59FB,
        0x3FB999999999999A,
        0x3FD3333333333334,
        0x3F1A36E2EB1C432D,
        0x3EE4F8B588E368F1,
        0x4341C37937E08000,
        0x4376345785D8A000,
        0x4415AF1D78B58C40,
        0x040FFFFFFFFFFFFF,
        0x0410000000000000,
        0x0410000000000001,
        0x7FEFFFFFFFFFFFFF,
    ]
    for magnitude in contract_vectors:
        add(magnitude, "contract-vector")
        add(magnitude | SIGN_MASK, "contract-vector")

    mantissa_cases = (0, 1, 2, 1 << 51, FRACTION_MASK - 1, FRACTION_MASK)
    for exponent in range(2047):
        exponent_bits = exponent << 52
        for mantissa in mantissa_cases:
            if exponent == 0 and mantissa == 0:
                category = "signed-zero"
            elif exponent == 0:
                category = "subnormal"
            elif mantissa == 0:
                category = "power-of-two"
            else:
                category = "exponent-band"
            add(exponent_bits | mantissa, category)
            add(SIGN_MASK | exponent_bits | mantissa, category)

    rng = random.Random(0xF10A7)
    accepted = 0
    while accepted < 1024:
        magnitude = rng.getrandbits(63)
        if (magnitude & EXPONENT_MASK) == EXPONENT_MASK:
            continue
        accepted += 1
        add(magnitude, "seeded-random")
        add(SIGN_MASK | magnitude, "seeded-random")

    for decimal_exponent in range(-323, 309):
        power = math.pow(10.0, decimal_exponent)
        if math.isfinite(power) and power > 0.0:
            for neighbor in (math.nextafter(power, -math.inf), power, math.nextafter(power, math.inf)):
                bits = bits_of(neighbor)
                add(bits, "decimal-power-neighbor")
                add(bits | SIGN_MASK, "decimal-power-neighbor")

    notation_boundaries = (1e-5, 1e-4, 1e15, 1e16, 1e17, 9.999999999999998e16)
    for threshold in notation_boundaries:
        for neighbor in (math.nextafter(threshold, -math.inf), threshold, math.nextafter(threshold, math.inf)):
            bits = bits_of(neighbor)
            add(bits, "notation-boundary")
            add(bits | SIGN_MASK, "notation-boundary")

    rows = []
    for bits, (text, roundtrip_bits) in samples.items():
        rows.append([f"{bits:016X}", text, f"{roundtrip_bits:016X}"])

    row_bytes = b"".join(
        (json.dumps(row, separators=(",", ":"), ensure_ascii=True) + "\n").encode("ascii")
        for row in rows
    )
    script_path = Path(__file__).resolve()
    manifest = {
        "schemaVersion": 1,
        "oracle": {
            "implementation": "CPython",
            "pythonVersion": ".".join(map(str, PINNED_PYTHON_VERSION)),
            "pythonBuild": sys.version,
            "shortestSource": "repr(float), CPython shortest-roundtrip conversion",
            "canonicalRenderer": "Decimal tuple; adjusted exponent -4..16 plain, otherwise uppercase E with sign and two exponent digits; append .0 to plain integers and signed zero",
            "generator": script_path.name,
            "generatorSha256": hashlib.sha256(script_path.read_bytes()).hexdigest().upper(),
            "sampleCount": len(rows),
            "categoryAttemptCounts": dict(sorted(categories.items())),
            "rowsSha256": hashlib.sha256(row_bytes).hexdigest().upper(),
            "rowEncoding": "Each row is [16-digit uppercase IEEE-754 bits, canonical text, parsed round-trip bits].",
        },
        "samples": rows,
    }

    output_path = Path(__file__).with_name("float-shortest-oracle.json")
    sample_lines = [
        "    " + json.dumps(row, separators=(",", ":"), ensure_ascii=True)
        for row in rows
    ]
    document_text = (
        "{\n  \"schemaVersion\": 1,\n  \"oracle\": "
        + json.dumps(manifest["oracle"], indent=2, ensure_ascii=True)
        + ",\n  \"samples\": [\n"
        + ",\n".join(sample_lines)
        + "\n  ]\n}\n"
    )
    output_path.write_text(document_text, encoding="utf-8", newline="\n")
    print(f"wrote {len(rows)} samples to {output_path}")
    print(f"generator sha256: {manifest['oracle']['generatorSha256']}")
    print(f"rows sha256: {manifest['oracle']['rowsSha256']}")


if __name__ == "__main__":
    main()
