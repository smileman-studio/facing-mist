# -*- coding: utf-8 -*-
"""
Golden-реплика воксельного SDF острова (ADR-0002, D1/D6).

Зеркалирует C#-алгоритм из SmilemanStudio.Core.Voxels побитно:
- Murmur3 fmix32 (VoxelNoise.Hash)
- ValueNoise2D / Fbm2D (Q16 smoothstep)
- ValueNoise3D / Fbm3D (Q16 smoothstep)
- IslandSdf: ApexDepth, TopHeightMax, TopSurface, Sdf (int-пайплайн)
- IslandMaterialField.At (argmax 4x Fbm3D)

Python int — бесконечная точность: сдвиги вправо = floor-деление,
что совпадает с C# int-арифметикой. Умножения всегда в диапазоне 64 бит.

Запуск:  python tools/golden_sdf.py
"""

import struct

MASK32 = 0xFFFFFFFF
MASK16 = 0xFFFF
ONE = 65536  # Q16: 1.0

# --- Константы VoxelConstants ---
APEX_DEPTH_PERMILLE = 900
TOP_RELIEF_PERMILLE = 200
TOP_HEIGHT_HARD_CAP = 448
RELIEF_NOISE_PERIOD = 64
DENSITY_UNITS_PER_VOXEL = 256
RADIUS_MIN = 200
RADIUS_MAX = 2000

OCTAVE_COUNT = 5
NOISE_STEP = 0x9E3779B9


# ---------------------------------------------------------------------------
# VoxelNoise
# ---------------------------------------------------------------------------

def fmix32(x):
    x &= MASK32
    x ^= x >> 16
    x = (x * 0x85EBCA6B) & MASK32
    x ^= x >> 13
    x = (x * 0xC2B2AE35) & MASK32
    x ^= x >> 16
    return x


def node_value_2d(x, y, seed):
    h = ((x & MASK32) * 0x9E3779B9) & MASK32
    h ^= ((y & MASK32) * 0x85EBCA6B) & MASK32
    h ^= (seed * 0xC2B2AE35) & MASK32
    h &= MASK32
    return fmix32(h) & MASK16


def node_value_3d(x, y, z, seed):
    h = ((x & MASK32) * 0x9E3779B9) & MASK32
    h ^= ((y & MASK32) * 0x85EBCA6B) & MASK32
    h ^= ((z & MASK32) * 0xC2B2AE35) & MASK32
    h ^= (seed * 0x27D4EB2F) & MASK32
    h &= MASK32
    return fmix32(h) & MASK16


def smoothstep_q16(t):
    t &= MASK16
    t2 = (t * t) >> 16
    s = (t2 * (3 * ONE - 2 * t)) >> 16
    return s


def lerp_q16(a, b, t):
    # ВАЖНО: скобки обязательны — в Python `>>` ниже по приоритету, чем `+`,
    # поэтому без скобок это было бы (a + (b-a)*t) >> 16, а не a + ((b-a)*t >> 16).
    # C#-версия (VoxelNoise.Lerp) имеет явные скобки — повторяем её семантику.
    return a + (((b - a) * t) >> 16)


def value_noise_2d(x, y, seed):
    x0 = x >> 16
    y0 = y >> 16
    tx = x & MASK16
    ty = y & MASK16
    sx = smoothstep_q16(tx)
    sy = smoothstep_q16(ty)
    v0 = lerp_q16(node_value_2d(x0, y0, seed), node_value_2d(x0 + 1, y0, seed), sx)
    v1 = lerp_q16(node_value_2d(x0, y0 + 1, seed), node_value_2d(x0 + 1, y0 + 1, seed), sx)
    r = lerp_q16(v0, v1, sy)
    return min(max(r, 0), 65535)


def fbm_2d(x, y, seed):
    total = 0
    weight = 16
    fx, fy = x, y
    for o in range(OCTAVE_COUNT):
        total += value_noise_2d(fx, fy, (seed + o * NOISE_STEP) & MASK32) * weight
        weight >>= 1
        fx >>= 1
        fy >>= 1
    return total // 31


def value_noise_3d(x, y, z, seed):
    x0 = x >> 16
    y0 = y >> 16
    z0 = z >> 16
    sx = smoothstep_q16(x & MASK16)
    sy = smoothstep_q16(y & MASK16)
    sz = smoothstep_q16(z & MASK16)
    v00 = lerp_q16(node_value_3d(x0, y0, z0, seed), node_value_3d(x0 + 1, y0, z0, seed), sx)
    v10 = lerp_q16(node_value_3d(x0, y0 + 1, z0, seed), node_value_3d(x0 + 1, y0 + 1, z0, seed), sx)
    v01 = lerp_q16(node_value_3d(x0, y0, z0 + 1, seed), node_value_3d(x0 + 1, y0, z0 + 1, seed), sx)
    v11 = lerp_q16(node_value_3d(x0, y0 + 1, z0 + 1, seed), node_value_3d(x0 + 1, y0 + 1, z0 + 1, seed), sx)
    v0 = lerp_q16(v00, v10, sy)
    v1 = lerp_q16(v01, v11, sy)
    r = lerp_q16(v0, v1, sz)
    return min(max(r, 0), 65535)


def fbm_3d(x, y, z, seed):
    total = 0
    weight = 16
    fx, fy, fz = x, y, z
    for o in range(OCTAVE_COUNT):
        total += value_noise_3d(fx, fy, fz, (seed + o * NOISE_STEP) & MASK32) * weight
        weight >>= 1
        fx >>= 1
        fy >>= 1
        fz >>= 1
    return total // 31


# ---------------------------------------------------------------------------
# IslandSdf (int-пайплайн)
# ---------------------------------------------------------------------------

def island_seed(island_id):
    return fmix32(island_id & MASK32)


def apex_depth(radius):
    return radius * APEX_DEPTH_PERMILLE // 1000


def top_height_max(radius):
    return min(radius * TOP_RELIEF_PERMILLE // 1000, TOP_HEIGHT_HARD_CAP)


def relief_noise_scale():
    return (ONE // RELIEF_NOISE_PERIOD)  # 1024


def top_surface(x, z, island_id, radius):
    thm = top_height_max(radius)
    s = island_seed(island_id)
    v = fbm_2d(x * relief_noise_scale(), z * relief_noise_scale(), s)
    return v * thm // 65535


def _isqrt(n):
    if n < 0:
        raise ValueError("isqrt: negative input")
    if n == 0:
        return 0
    x = 1 << ((n.bit_length() + 1) // 2)
    while True:
        y = (x + n // x) // 2
        if y >= x:
            return x
        x = y


def _floor_sqrt(n):
    r = _isqrt(n)
    if r * r > n:
        r -= 1
    return r


def _csharp_div(num, den):
    """C# int/int: truncation toward zero (НЕ floor)."""
    q = abs(num) // abs(den)
    return q if (num >= 0) == (den >= 0) else -q


def d_cone(x, y, z, radius):
    """Целочисленный SDF конуса: apex (0, -D, 0), радиус R на y = 0.

    d = (R * (y + D) - D * rho) / L,  L = sqrt(R^2 + D^2).
    d <= 0 — внутри конуса (грунт). sqrt только для масштаба (знак — int).
    """
    d = apex_depth(radius)
    r = radius
    # y + D: y в метрах, D в метрах — оба int.
    t = y + d
    # rho = sqrt(x^2 + z^2), целочисленный floor-sqrt.
    rho = _floor_sqrt(x * x + z * z)
    num = r * t - d * rho
    # L = sqrt(R^2 + D^2), floor — масштаб знаменателя.
    den = _floor_sqrt(r * r + d * d)
    # Деление C#-стилем: truncation toward zero (num может быть отрицательным).
    q = _csharp_div(num, den)
    return q


def sdf(x, y, z, island_id, radius):
    d_cone_v = d_cone(x, y, z, radius)
    d_top = y - top_surface(x, z, island_id, radius)
    return max(d_cone_v, d_top)


# ---------------------------------------------------------------------------
# IslandSdf.SdfVoxel (единицы шага LOD0 = 0,5 м)
# ---------------------------------------------------------------------------

def top_surface_voxel(x, z, island_id, radius):
    """Та же, что top_surface, но сетка шума вдвое плотнее (вход * 512 вместо 1024).

    x, z — воксельные LOD0-координаты. Возвращает метры (как и top_surface):
    d_top в полуметрах получается умножением на 2 без второго округления.
    """
    thm = top_height_max(radius)
    s = island_seed(island_id)
    scale = ONE // (RELIEF_NOISE_PERIOD * 2)  # 512
    v = fbm_2d(x * scale, z * scale, s)
    return v * thm // 65535


def d_cone_voxel(x, y, z, radius):
    """SDF конуса в единицах шага LOD0 (полуметрах).

    d = (R * (y + 2D) - D * rho) / L. Координаты — в шагах LOD0,
    R и D — в метрах, поэтому D удваивается в сумме с y.
    """
    d = apex_depth(radius)
    r = radius
    t = y + 2 * d
    rho = _floor_sqrt(x * x + z * z)
    num = r * t - d * rho
    den = _floor_sqrt(r * r + d * d)
    return _csharp_div(num, den)


def sdf_voxel(x, y, z, island_id, radius):
    """SDF острова в воксельной координате LOD0 (шаг 0,5 м), в шагах сетки."""
    d_cone_v = d_cone_voxel(x, y, z, radius)
    d_top = y - 2 * top_surface_voxel(x, z, island_id, radius)
    return max(d_cone_v, d_top)


# ---------------------------------------------------------------------------
# IslandMaterialField
# ---------------------------------------------------------------------------

MATERIAL_SCALE = ONE // 32  # 2048: клетка материала = 32 м


def material_at(x, y, z, island_id):
    s = island_seed(island_id)
    mx = x * MATERIAL_SCALE
    my = y * MATERIAL_SCALE
    mz = z * MATERIAL_SCALE
    # Порядок enum: Dirt=1, Coal=2, Sand=3, Quartz=4.
    best = 1
    best_v = fbm_3d(mx, my, mz, (s ^ 0x12345678) & MASK32)
    for i in range(2, 5):
        v = fbm_3d(mx, my, mz, (s ^ (0x12345678 + i * NOISE_STEP)) & MASK32)
        if v > best_v:
            best_v = v
            best = i
    return best


# ---------------------------------------------------------------------------
# Golden-hash: FNV-1a 64 по little-endian байтам int (по 4 байта)
# ---------------------------------------------------------------------------

FNV_OFFSET = 14695981039346656037
FNV_PRIME = 1099511628211


def fnv1a_64(h, value32):
    for shift in (0, 8, 16, 24):
        h ^= (value32 >> shift) & 0xFF
        h = (h * FNV_PRIME) & 0xFFFFFFFFFFFFFFFF
    return h


def golden_hash_ints(values):
    h = FNV_OFFSET
    for v in values:
        v &= 0xFFFFFFFF
        h = fnv1a_64(h, v & 0xFFFFFFFF)
        h = fnv1a_64(h, (v >> 32) & 0xFFFFFFFF)
    return h


def golden_hash_ints_signed(values):
    """FNV-1a по little-endian байтам int32 (знаковые)."""
    h = FNV_OFFSET
    for v in values:
        for shift in (0, 8, 16, 24):
            b = (v >> shift) & 0xFF
            h ^= b
            h = (h * FNV_PRIME) & 0xFFFFFFFFFFFFFFFF
    return h


# ---------------------------------------------------------------------------
# Генерация golden-значений
# ---------------------------------------------------------------------------

def main():
    print("=" * 72)
    print("GOLDEN VALUES: IslandSdf / VoxelNoise3D / IslandMaterialField")
    print("=" * 72)

    # --- Параметры (инварианты) ---
    print("\n[IslandSdf parameters]")
    for r in (200, 500, 2000):
        print(f"  R={r}: ApexDepth={apex_depth(r)}  TopHeightMax={top_height_max(r)}")

    # --- TopSurface golden ---
    print("\n[TopSurface]  (x, z, id, R) -> int meters")
    top_surface_cases = [
        (0, 0, 1, 200),
        (10, 0, 1, 200),
        (0, 10, 1, 200),
        (0, 0, 2, 200),
        (0, 0, 1, 500),
        (0, 0, 1, 2000),
        (50, 50, 7, 500),
        (-30, 25, 3, 200),
    ]
    ts_values = []
    for (x, z, i, r) in top_surface_cases:
        v = top_surface(x, z, i, r)
        ts_values.append(v)
        print(f"  TopSurface({x}, {z}, id={i}, R={r}) = {v}")
    print(f"  GOLDEN_HASH(TopSurface) = {golden_hash_ints_signed(ts_values):016X}")

    # --- Sdf golden (R = 200 / 500 / 2000) ---
    sdf_points = [
        (0, 0, 0),
        (0, 10, 0),
        (0, -10, 0),
        (0, -100, 0),
        (10, 0, 0),
        (-10, 0, 0),
        (0, 0, 10),
        (5, 5, 5),
        (100, 0, 0),
        (0, 200, 0),
        (0, -200, 0),
        (0, -500, 0),
        (0, -1000, 0),
        (0, -2000, 0),
        (300, 0, 0),
        (0, 400, 0),
        (0, -400, 0),
        (0, -1800, 0),
        (0, -3000, 0),
    ]
    print("\n[Sdf]  (x, y, z, id=1) -> int meters")
    for r in (200, 500, 2000):
        vals = []
        print(f"  R={r}:")
        for (x, y, z) in sdf_points:
            v = sdf(x, y, z, 1, r)
            vals.append(v)
            print(f"    Sdf({x}, {y}, {z}, id=1, R={r}) = {v}")
        print(f"    GOLDEN_HASH(Sdf R={r}) = {golden_hash_ints_signed(vals):016X}")

    # --- SdfVoxel golden (R = 200 / 500 / 2000) ---
    print("\n[SdfVoxel]  (x, y, z, id=1) -> int LOD0 steps (0.5 m)")
    voxel_points = [
        (0, 0, 0),
        (0, 20, 0),
        (0, -20, 0),
        (0, -200, 0),
        (20, 0, 0),
        (-20, 0, 0),
        (0, 0, -20),
        (10, 10, -6),
        (200, 0, 0),
        (0, 400, 0),
        (0, 800, 0),
        (1, 0, 0),
        (3, -39, 5),
        (-57, 129, 211),
    ]
    for r in (200, 500, 2000):
        vals = []
        print(f"  R={r}:")
        for (x, y, z) in voxel_points:
            v = sdf_voxel(x, y, z, 1, r)
            vals.append(v)
        print(f"    values = {vals}")
        print(f"    GOLDEN_HASH(SdfVoxel R={r}) = {golden_hash_ints_signed(vals):016X}")

    # --- Согласованность SdfVoxel(2p) с Sdf(p) ---
    print("\n[SdfVoxel consistency]  |SdfVoxel(2p)/2 - Sdf(p)| <= 1")
    consistency_points = [
        (0, 0, 0), (0, 10, 0), (0, -100, 0), (10, 0, 0),
        (50, 5, -30), (100, 0, 0), (0, -200, 0), (-40, 60, 20),
    ]
    worst = 0
    for r in (200, 500, 2000):
        for (x, y, z) in consistency_points:
            m = sdf(x, y, z, 1, r)
            vv = sdf_voxel(2 * x, 2 * y, 2 * z, 1, r)
            diff = abs(vv / 2.0 - m)
            worst = max(worst, diff)
            if diff > 1:
                print(f"    VIOLATION R={r} p=({x},{y},{z}): {vv}/2={vv/2.0} vs {m}")
    print(f"    worst deviation = {worst}")

    print("\n[Sdf, разные Id, R=500]")
    for i in (1, 2, 3):
        print(f"  Sdf(0, 0, 0, id={i}, R=500) = {sdf(0, 0, 0, i, 500)}")

    # --- VoxelNoise3D golden ---
    print("\n[VoxelNoise3D]")
    vn3d_cases = [
        (0, 0, 0, 42),
        (65536, 0, 0, 42),
        (0, 65536, 0, 42),
        (0, 0, 65536, 42),
        (32768, 0, 0, 42),
        (0, 32768, 0, 42),
        (0, 0, 32768, 42),
        (32768, 32768, 32768, 42),
        (131072, 65536, 65536, 123),
    ]
    vn3d_values = []
    for (x, y, z, s) in vn3d_cases:
        v = value_noise_3d(x, y, z, s)
        vn3d_values.append(v)
        print(f"  ValueNoise3D({x}, {y}, {z}, seed={s}) = {v}")
    print(f"  GOLDEN_HASH(ValueNoise3D) = {golden_hash_ints_signed(vn3d_values):016X}")

    print("\n[Fbm3D]")
    fbm3d_cases = [
        (0, 0, 0, 42),
        (65536, 0, 0, 42),
        (0, 65536, 0, 42),
        (0, 0, 65536, 42),
        (131072, 65536, 65536, 123),
    ]
    fbm3d_values = []
    for (x, y, z, s) in fbm3d_cases:
        v = fbm_3d(x, y, z, s)
        fbm3d_values.append(v)
        print(f"  Fbm3D({x}, {y}, {z}, seed={s}) = {v}")
    print(f"  GOLDEN_HASH(Fbm3D) = {golden_hash_ints_signed(fbm3d_values):016X}")

    # --- Material golden ---
    print("\n[IslandMaterialField.At]")
    mat_cases = [
        (0, 0, 0, 1),
        (10, 0, 0, 1),
        (0, 10, 0, 1),
        (0, 0, 10, 1),
        (0, 0, 0, 2),
        (0, 0, 0, 7),
        (32, 32, 32, 1),
        (-16, 8, 24, 3),
    ]
    mat_values = []
    for (x, y, z, i) in mat_cases:
        v = material_at(x, y, z, i)
        mat_values.append(v)
        print(f"  MaterialAt({x}, {y}, {z}, id={i}) = {v}")
    print(f"  GOLDEN_HASH(Material) = {golden_hash_ints_signed(mat_values):016X}")

    # --- Связка с VoxelDensity.Quantize ---
    print("\n[VoxelDensity.Quantize связь]")
    for (x, y, z, i, r) in [(0, 0, 0, 1, 200), (0, -100, 0, 1, 500), (100, 0, 0, 1, 2000)]:
        s = sdf(x, y, z, i, r)
        q = s * DENSITY_UNITS_PER_VOXEL
        print(f"  Sdf({x},{y},{z},id={i},R={r}) = {s}  ->  Quantize = {q} (short)")

    print("\n" + "=" * 72)
    print("DONE")
    print("=" * 72)


if __name__ == "__main__":
    main()
