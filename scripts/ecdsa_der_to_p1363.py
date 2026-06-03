#!/usr/bin/env python3
"""Convert a DER-encoded ECDSA P-256 signature to raw P1363 r||s hex."""

from __future__ import annotations

import sys
from pathlib import Path


P256_SCALAR_BYTES = 32


def read_der_length(blob: bytes, offset: int) -> tuple[int, int]:
    first = blob[offset]
    offset += 1
    if first < 0x80:
        return first, offset

    count = first & 0x7F
    if count == 0 or count > 2:
        raise ValueError("unsupported DER length")

    length = int.from_bytes(blob[offset : offset + count], "big")
    return length, offset + count


def read_der_integer(blob: bytes, offset: int) -> tuple[bytes, int]:
    if offset >= len(blob) or blob[offset] != 0x02:
        raise ValueError("expected DER integer")

    length, offset = read_der_length(blob, offset + 1)
    value = blob[offset : offset + length].lstrip(b"\x00")
    if len(value) > P256_SCALAR_BYTES:
        raise ValueError("ECDSA scalar is larger than P-256")

    return value.rjust(P256_SCALAR_BYTES, b"\x00"), offset + length


def der_to_p1363_hex(der: bytes) -> str:
    if len(der) < 8 or der[0] != 0x30:
        raise ValueError("expected DER sequence")

    sequence_len, offset = read_der_length(der, 1)
    end = offset + sequence_len
    if end != len(der):
        raise ValueError("DER sequence length mismatch")

    r, offset = read_der_integer(der, offset)
    s, offset = read_der_integer(der, offset)
    if offset != end:
        raise ValueError("unexpected trailing DER data")

    return (r + s).hex()


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: ecdsa_der_to_p1363.py <signature.der> <signature.hex>", file=sys.stderr)
        return 2

    sig_hex = der_to_p1363_hex(Path(sys.argv[1]).read_bytes())
    if len(sig_hex) != P256_SCALAR_BYTES * 2 * 2:
        raise ValueError("invalid P1363 signature length")

    Path(sys.argv[2]).write_text(sig_hex, encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
