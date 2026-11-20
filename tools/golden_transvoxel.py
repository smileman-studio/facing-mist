# -*- coding: utf-8 -*-
"""Golden-hash (FNV-1a 64) для таблиц Transvoxel (P4a).

Читает Assets/Scripts/Core/Voxels/TransvoxelTables.cs, извлекает плоские
массивы регулярных таблиц и считает FNV-1a 64-бит по little-endian байтам:
  - byte[]  -> байты как есть
  - int[]   -> 4 байта LE на элемент
  - ushort[]-> 2 байта LE на элемент

Значения фиксируются в TransvoxelMesherTests (таблицы — данные, не логика,
поэтому хеш считается по содержимому, порядок полей фиксирован).
"""
import re
import sys

SRC = r"Assets\Scripts\Core\Voxels\TransvoxelTables.cs"

FNV_OFFSET = 0xCBF29CE484222325
FNV_PRIME = 0x100000001B3
MASK = 0xFFFFFFFFFFFFFFFF


def fnv1a(data: bytes) -> int:
    h = FNV_OFFSET
    for b in data:
        h ^= b
        h = (h * FNV_PRIME) & MASK
    return h


def extract(name: str, text: str) -> str:
    m = re.search(r"public static readonly \w+\[\] " + name + r"\s*=\s*\{(.*?)\};", text, re.S)
    if not m:
        raise SystemExit("not found: " + name)
    return m.group(1)


def parse_ints(body: str):
    out = []
    for tok in body.split(","):
        tok = tok.strip()
        if tok:
            out.append(int(tok, 0))
    return out


def to_bytes(values, width):
    return b"".join(v.to_bytes(width, "little") for v in values)


def main():
    with open(SRC, "r", encoding="utf-8-sig") as f:
        text = f.read()

    # Только класс TransvoxelTables (regular), до TransvoxelTransitionTables.
    end = text.index("public static class TransvoxelTransitionTables")
    regular = text[:end]

    tables = [
        ("RegularCornerOffset", 4),
        ("RegularCellClass", 1),
        ("RegularCellGeometryCounts", 1),
        ("RegularCellTriangleIndices", 1),
        ("RegularVertexDataFlat", 2),
    ]

    for name, width in tables:
        values = parse_ints(extract(name, regular))
        h = fnv1a(to_bytes(values, width))
        print("%s len=%d fnv1a64=0x%016X" % (name, len(values), h))


if __name__ == "__main__":
    main()
