import re, sys

# Read the reference tables from transvoxel_core/TransvoxelTables.cs
with open('transvoxel_core/TransvoxelTables.cs', 'r', encoding='utf-8-sig') as f:
    text = f.read()

def block(name, txt):
    idx = txt.index(name)
    brace = txt.index("{", idx)
    depth = 0
    i = brace
    while i < len(txt):
        if txt[i] == "{":
            depth += 1
        elif txt[i] == "}":
            depth -= 1
            if depth == 0:
                break
        i += 1
    return txt[brace + 1:i]

def hexlist(s):
    out = []
    for x in s.split(","):
        x = x.strip()
        if x:
            out.append(int(x, 16 if x.lower().startswith("0x") else 10))
    return out

def parse_cell(body):
    """Parse 'new RegularCellData(0xNN, new byte[] {...})' entries."""
    ent = re.findall(
        r"new\s+RegularCellData\(\s*(0x[0-9A-Fa-f]+)\s*,\s*new\s+byte\[\]\s*\{([^}]*)\}",
        body)
    return [(int(c, 16), hexlist(ix)) for c, ix in ent]

def parse_rows(body):
    return [hexlist(r) for r in re.findall(r"new\s+ushort\[\]\s*\{([^}]*)\}", body)]

trc_data = parse_cell(block("TransitionRegularCellData", text))
tvt_data = parse_rows(block("TransitionVertexData", text))
tc_vals = [int(x.strip(), 16) for x in re.findall(r"0x[0-9A-Fa-f]+", block("TransitionCellClass", text))]
tcd_vals = [int(x.strip(), 16) for x in re.findall(r"0x[0-9A-Fa-f]+", block("TransitionCornerData", text))]
# TransitionCornerOffset uses Vector3Int format: new Vector3Int(x,y,z)
tco_body = block("TransitionCornerOffset", text)
tco_vals = []
for m in re.finditer(r"new\s+Vector3Int\(\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*\)", tco_body):
    tco_vals.extend([int(m.group(1)), int(m.group(2)), int(m.group(3))])

print("=== Reference Data Summary ===")
print(f"TransitionCornerOffset: {len(tco_vals)} ints ({len(tco_vals)//3} corners)")
print(f"TransitionCellClass: {len(tc_vals)} bytes")
print(f"TransitionRegularCellData: {len(trc_data)} entries")
print(f"TransitionCornerData: {len(tcd_vals)} bytes")
print(f"TransitionVertexData: {len(tvt_data)} rows")

max_trc_idx = max(len(ix) for _, ix in trc_data)
max_tvt_row = max(len(r) for r in tvt_data)
print(f"Max transition cell triangle indices: {max_trc_idx}")
print(f"Max transition vertex data per case: {max_tvt_row}")

# FNV-1a 64-bit (little-endian bytes)
def fnv1a_64(data_bytes):
    h = 14695981039346656037  # FNV offset basis
    for b in data_bytes:
        h ^= b
        h *= 14695981039346656037
        h &= 0xFFFFFFFFFFFFFFFF
    return h

def flatten_tco():
    """TransitionCornerOffset: 39 int values (13 corners * 3)"""
    return tco_vals  # int[] → each int contributes 4 bytes (little-endian)

def flatten_tcc():
    """TransitionCellClass: 512 byte values"""
    return tc_vals  # byte[] → 1 byte each

def flatten_tcgc():
    """TransitionCellGeometryCounts: 56 byte values"""
    return [c for c, _ in trc_data]  # byte[] → 1 byte each

def flatten_tcti():
    """TransitionCellTriangleIndices: 56 classes * max_trc_idx bytes each, padded"""
    flat = []
    for _, ix in trc_data:
        flat.extend(ix + [0] * (max_trc_idx - len(ix)))
    return flat  # byte[] → 1 byte each

def flatten_tcd():
    """TransitionCornerData: 13 byte values"""
    return tcd_vals  # byte[] → 1 byte each

def flatten_tvt():
    """TransitionVertexDataFlat: 512 cases * max_tvt_row ushort each, padded"""
    flat = []
    for r in tvt_data:
        flat.extend(r + [0] * (max_tvt_row - len(r)))
    return flat  # ushort[] → each ushort contributes 2 bytes (little-endian)

def bytes_from_int_list(values):
    """Convert int list to little-endian bytes"""
    result = bytearray()
    for v in values:
        v = v & 0xFFFFFFFF  # ensure non-negative 32-bit
        for i in range(4):
            result.append((v >> (8 * i)) & 0xFF)
    return bytes(result)

def bytes_from_ushort_list(values):
    """Convert ushort list to little-endian bytes (2 bytes per value)"""
    result = bytearray()
    for v in values:
        v = v & 0xFFFF
        for i in range(2):
            result.append((v >> (8 * i)) & 0xFF)
    return bytes(result)

print("\n=== Golden Hashes (FNV-1a 64, little-endian bytes) ===")

# TransitionCornerOffset (int[], 4 bytes per element)
tco_bytes = bytes_from_int_list(flatten_tco())
h_tco = fnv1a_64(tco_bytes)
print(f"TransitionCornerOffset: 0x{h_tco:016X}  ({len(tco_bytes)} bytes)")

# TransitionCellClass (byte[], 1 byte per element)
h_tcc = fnv1a_64(bytes(tc_vals))
print(f"TransitionCellClass:     0x{h_tcc:016X}  ({len(tc_vals)} bytes)")

# TransitionCellGeometryCounts (byte[], 1 byte per element)
h_tcgc = fnv1a_64(bytes(flatten_tcgc()))
print(f"TransitionCellGeometryCounts: 0x{h_tcgc:016X}  ({len(tc_vals)} bytes)")

# TransitionCellTriangleIndices (byte[], 1 byte per element)
h_tcti = fnv1a_64(bytes(flatten_tcti()))
print(f"TransitionCellTriangleIndices: 0x{h_tcti:016X}  ({len(flatten_tcti())} bytes)")

# TransitionCornerData (byte[], 1 byte per element)
h_tcd = fnv1a_64(bytes(tcd_vals))
print(f"TransitionCornerData:    0x{h_tcd:016X}  ({len(tcd_vals)} bytes)")

# TransitionVertexDataFlat (ushort[], 2 bytes per element)
flat_tvt = flatten_tvt()
h_tvt = fnv1a_64(bytes_from_ushort_list(flat_tvt))
print(f"TransitionVertexDataFlat: 0x{h_tvt:016X}  ({len(flat_tvt)} ushort = {len(flat_tvt)*2} bytes)")

print("\n=== C# test assertions ===")
print(f"        Assert.AreEqual(0x{h_tco:016X}UL,")
print(f"            Fnv1aArray(ToUlong(TransvoxelTransitionTables.TransitionCornerOffset), 4),")
print(f'            "TransitionCornerOffset — golden hash.");')
print(f"        Assert.AreEqual(0x{h_tcc:016X}UL,")
print(f"            Fnv1aArray(ToUlong(TransvoxelTransitionTables.TransitionCellClass), 1),")
print(f'            "TransitionCellClass — golden hash.");')
print(f"        Assert.AreEqual(0x{h_tcgc:016X}UL,")
print(f"            Fnv1aArray(ToUlong(TransvoxelTransitionTables.TransitionCellGeometryCounts), 1),")
print(f'            "TransitionCellGeometryCounts — golden hash.");')
print(f"        Assert.AreEqual(0x{h_tcti:016X}UL,")
print(f"            Fnv1aArray(ToUlong(TransvoxelTransitionTables.TransitionCellTriangleIndices), 1),")
print(f'            "TransitionCellTriangleIndices — golden hash.");')
print(f"        Assert.AreEqual(0x{h_tcd:016X}UL,")
print(f"            Fnv1aArray(ToUlong(TransvoxelTransitionTables.TransitionCornerData), 1),")
print(f'            "TransitionCornerData — golden hash.");')
print(f"        Assert.AreEqual(0x{h_tvt:016X}UL,")
print(f"            Fnv1aArray(ToUlong(TransvoxelTransitionTables.TransitionVertexDataFlat), 2),")
print(f'            "TransitionVertexDataFlat — golden hash.");')
