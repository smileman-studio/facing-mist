using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Квантование и интерполяция плотности (ADR-0002, D4).
    /// </summary>
    /// <remarks>
    /// Плотность хранится как <c>short</c> в единицах <see cref="VoxelConstants.DensityUnitsPerVoxel"/>:
    /// один шаг сетки = 256 целых единиц, диапазон ±128 шагов.
    /// Квантование выполняется однократно в конце пайплайна: explicit floor, не math.round.
    /// Интерполяция t ∈ [0, 65536] — fixed-point Q16, шаг = 1/65536 от шага сетки.
    /// </remarks>
    public static class VoxelDensity
    {
        /// <summary>
        /// Квантование float SDF → <c>short</c>: <c>floor(d * DensityUnitsPerVoxel + 0,5)</c>,
        /// кламп в диапазон short.
        /// </summary>
        public static short Quantize(float sdf)
        {
            long quantized = (long)math.floor(sdf * VoxelConstants.DensityUnitsPerVoxel + 0.5f);
            return (short)math.clamp(quantized, short.MinValue, short.MaxValue);
        }

        /// <summary>
        /// Параметр интерполяции <c>t</c> в фиксированной точке (1/65536 шага сетки).
        /// </summary>
        /// <remarks>
        /// АDR: <c>t = (|d0| &lt;&lt; 16) / (|d0| + |d1|)</c>.
        /// Если сумма нулевая (обе точки на поверхности) — возвращается середина ребра (32768).
        /// Результат ∈ [0, 65536]: 0 = на точке d0, 65536 = на точке d1.
        /// </remarks>
        public static int InterpolateEdgeT(short d0, short d1)
        {
            int abs0 = math.abs(d0);
            int abs1 = math.abs(d1);
            int sum = abs0 + abs1;

            if (sum == 0)
            {
                return 32768;
            }

            return (abs0 << 16) / sum;
        }
    }
}
