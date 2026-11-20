using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Детерминированный целочисленный шум (ADR-0002, D6).
    /// </summary>
    /// <remarks>
    /// Только целочисленная математика: avalanche-финализатор Murmur3 fmix32
    /// для хеша, интерполяция в фиксированной точке Q16 (1/65536).
    /// Запрещены sin, cos, exp, sqrt и любые float-трансцендентные —
    /// они расходятся между x86 и ARM. Результат одинаков на всех платформах.
    /// </remarks>
    public static class VoxelNoise
    {
        /// <summary>Число октав fbm.</summary>
        public const int OctaveCount = 5;

        /// <summary>Единиц в фиксированной точке Q16 (1.0 = 65536).</summary>
        private const int One = 65536;

        /// <summary>
        /// Avalanche-финализатор Murmur3 fmix32: 32 → 32 бит.
        /// </summary>
        public static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x85EBCA6BU;
            x ^= x >> 13;
            x *= 0xC2B2AE35U;
            x ^= x >> 16;
            return x;
        }

        /// <summary>
        /// Value noise 2D: результат ∈ [0, 65535] (unsigned Q16).
        /// </summary>
        /// <remarks>
        /// Входные координаты x, y — int Q16.16: целая часть задаёт узел сетки,
        /// дробная — позицию интерполяции. Интерполяция — smoothstep
        /// (3t² − 2t³) в фиксированной точке Q16, билинейная комбинация узлов.
        /// Значение узла — Hash от смешанных координат и seed, младшие 16 бит.
        /// </remarks>
        public static int ValueNoise2D(int x, int y, uint seed)
        {
            int x0 = x >> 16;
            int y0 = y >> 16;
            uint tx = (uint)(x & 0xFFFF);
            uint ty = (uint)(y & 0xFFFF);

            int n00 = (int)NodeValue(x0, y0, seed);
            int n10 = (int)NodeValue(x0 + 1, y0, seed);
            int n01 = (int)NodeValue(x0, y0 + 1, seed);
            int n11 = (int)NodeValue(x0 + 1, y0 + 1, seed);

            // smoothstep: s = t² * (3 - 2t), где 3 = 3*One, 2t = 2*tx
            // t² в Q16: (tx*tx) >> 16; умножение на (3*One - 2*tx): >> 16.
            // Итого: s ∈ [0, One].
            int sx = SmoothStep(tx);
            int sy = SmoothStep(ty);

            // Билинейная интерполяция (lerp с фиксированной точкой).
            int v0 = n00 + (int)(((long)(n10 - n00) * sx) >> 16);
            int v1 = n01 + (int)(((long)(n11 - n01) * sx) >> 16);
            int result = v0 + (int)(((long)(v1 - v0) * sy) >> 16);

            return math.clamp(result, 0, 65535);
        }

        /// <summary>
        /// FBM 2D: <see cref="OctaveCount"/> октав, gain = 1/2, lacunarity = 2.
        /// Выход ∈ [0, 65535].
        /// </summary>
        /// <remarks>
        /// Каждая октава удваивает частоту (сдвиг координат вправо) и делит вес пополам.
        /// Нормировка: сумма весов = 31, итог = sum / 31.
        /// </remarks>
        public static int Fbm2D(int x, int y, uint seed)
        {
            long sum = 0;
            int weight = 16;
            int fx = x;
            int fy = y;

            for (int o = 0; o < OctaveCount; o++)
            {
                sum += (long)ValueNoise2D(fx, fy, seed + (uint)o * 0x9E3779B9U) * weight;
                weight >>= 1;
                fx >>= 1;
                fy >>= 1;
            }

            return (int)(sum / 31);
        }

        /// <summary>
        /// Value noise 3D: результат ∈ [0, 65535] (unsigned Q16).
        /// </summary>
        /// <remarks>
        /// Входные координаты x, y, z — int Q16.16: целая часть задаёт узел сетки,
        /// дробная — позицию интерполяции. Интерполяция — smoothstep
        /// (3t² − 2t³) в фиксированной точке Q16, трилинейная комбинация восьми
        /// узлов. Значение узла — Hash от смешанных координат и seed, младшие 16 бит.
        /// Нужен для процедурного материала (ADR-0002, D7): чистая функция от
        /// island-local позиции и seed, без float-трансцендентных.
        /// </remarks>
        public static int ValueNoise3D(int x, int y, int z, uint seed)
        {
            int x0 = x >> 16;
            int y0 = y >> 16;
            int z0 = z >> 16;
            uint tx = (uint)(x & 0xFFFF);
            uint ty = (uint)(y & 0xFFFF);
            uint tz = (uint)(z & 0xFFFF);

            int sx = SmoothStep(tx);
            int sy = SmoothStep(ty);
            int sz = SmoothStep(tz);

            // Трилинейная интерполяция: lerp по x, затем по y, затем по z.
            int v00 = Lerp((int)NodeValue3D(x0, y0, z0, seed), (int)NodeValue3D(x0 + 1, y0, z0, seed), sx);
            int v10 = Lerp((int)NodeValue3D(x0, y0 + 1, z0, seed), (int)NodeValue3D(x0 + 1, y0 + 1, z0, seed), sx);
            int v01 = Lerp((int)NodeValue3D(x0, y0, z0 + 1, seed), (int)NodeValue3D(x0 + 1, y0, z0 + 1, seed), sx);
            int v11 = Lerp((int)NodeValue3D(x0, y0 + 1, z0 + 1, seed), (int)NodeValue3D(x0 + 1, y0 + 1, z0 + 1, seed), sx);

            int v0 = Lerp(v00, v10, sy);
            int v1 = Lerp(v01, v11, sy);

            int result = Lerp(v0, v1, sz);
            return math.clamp(result, 0, 65535);
        }

        /// <summary>
        /// FBM 3D: <see cref="OctaveCount"/> октав, gain = 1/2, lacunarity = 2.
        /// Выход ∈ [0, 65535].
        /// </summary>
        /// <remarks>
        /// Каждая октава удваивает частоту (сдвиг координат вправо) и делит вес пополам.
        /// Нормировка: сумма весов = 31, итог = sum / 31.
        /// </remarks>
        public static int Fbm3D(int x, int y, int z, uint seed)
        {
            long sum = 0;
            int weight = 16;
            int fx = x;
            int fy = y;
            int fz = z;

            for (int o = 0; o < OctaveCount; o++)
            {
                sum += (long)ValueNoise3D(fx, fy, fz, seed + (uint)o * 0x9E3779B9U) * weight;
                weight >>= 1;
                fx >>= 1;
                fy >>= 1;
                fz >>= 1;
            }

            return (int)(sum / 31);
        }

        /// <summary>Линейная интерполяция в Q16: a + (b − a) * t / 65536.</summary>
        private static int Lerp(int a, int b, int t)
        {
            return a + (int)(((long)(b - a) * t) >> 16);
        }

        /// <summary>Значение узла сетки 2D: младшие 16 бит хеша от координат и seed.</summary>
        private static uint NodeValue(int x, int y, uint seed)
        {
            uint h = (uint)x * 0x9E3779B9U;
            h ^= (uint)y * 0x85EBCA6BU;
            h ^= seed * 0xC2B2AE35U;
            return Hash(h) & 0xFFFF;
        }

        /// <summary>Значение узла сетки 3D: младшие 16 бит хеша от координат и seed.</summary>
        private static uint NodeValue3D(int x, int y, int z, uint seed)
        {
            uint h = (uint)x * 0x9E3779B9U;
            h ^= (uint)y * 0x85EBCA6BU;
            h ^= (uint)z * 0xC2B2AE35U;
            h ^= seed * 0x27D4EB2FU;
            return Hash(h) & 0xFFFF;
        }

        /// <summary>Smoothstep 3t² − 2t³ в Q16: t ∈ [0, 65535] → s ∈ [0, 65536].</summary>
        private static int SmoothStep(uint t)
        {
            // t² в Q16: (t * t) >> 16
            long t2 = ((long)t * t) >> 16;
            // s = t² * (3 - 2t) = t² * (3*One - 2*t), результат Q16: >> 16
            long s = (t2 * (3 * (long)One - 2 * t)) >> 16;
            return (int)s;
        }
    }
}
