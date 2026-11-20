namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Уровни детализации (LOD) воксельной сетки (ADR-0002, D3).
    /// </summary>
    /// <remarks>
    /// Все расчёты целочисленные: шаг и размер чанка — степени двойки от констант
    /// <see cref="VoxelConstants"/>, поэтому значения точны и одинаковы на любой
    /// платформе. Число уровней зависит от радиуса острова, а не от константы:
    /// уровень нужен, пока чанк меньше острова.
    /// </remarks>
    public static class VoxelLod
    {
        /// <summary>Миллиметров в метре — единица перевода шага сетки в целые.</summary>
        private const int MillimetersPerMeter = 1000;

        /// <summary>
        /// Шаг воксельной сетки уровня lod, миллиметров: 0,5 м * 2^lod.
        /// </summary>
        /// <remarks>
        /// Выведен из <see cref="VoxelConstants.VoxelSizeLog2"/>, а не из литерала 500:
        /// единственный источник значения шага — константа ADR.
        /// </remarks>
        public static int VoxelStepMilli(int lod)
        {
            int exponent = lod + VoxelConstants.VoxelSizeLog2;
            return exponent >= 0
                ? MillimetersPerMeter << exponent
                : MillimetersPerMeter >> -exponent;
        }

        /// <summary>
        /// Мироразмер чанка уровня lod, метры: 16 ячеек * шаг.
        /// </summary>
        public static int ChunkWorldSize(int lod)
        {
            int cells = 1 << VoxelConstants.ChunkSizeLog2;
            return (int)((long)cells * VoxelStepMilli(lod) / MillimetersPerMeter);
        }

        /// <summary>
        /// Максимальный уровень LOD острова заданного радиуса: минимальный k,
        /// при котором чанк уровня k перекрывает диаметр острова (2R).
        /// Ограничено <see cref="VoxelConstants.LodHardMax"/>.
        /// </summary>
        /// <remarks>
        /// Целочисленный эквивалент k = ceil(log2(ceil(R / 4))) без float-логарифма:
        /// ChunkWorldSize(k) = 8 * 2^k &gt;= 2R ⇔ 2^k &gt;= R/4.
        /// R = 200 даёт 6 (чанк 512 м), R = 2000 — 9 (чанк 4096 м).
        /// </remarks>
        public static int LodMax(int radiusMeters)
        {
            // Условие ChunkWorldSize(k) = 8 * 2^k >= 2R ⇔ 2^k >= R / 4,
            // где 4 = ChunkWorldSize(0) / 2. Деление с округлением вверх,
            // чтобы остров гарантированно влезал в чанк уровня k.
            long chunkDivisor = ChunkWorldSize(0) / 2;
            long chunks = (radiusMeters + chunkDivisor - 1) / chunkDivisor;
            int lod = 0;
            long power = 1;

            while (power < chunks && lod < VoxelConstants.LodHardMax)
            {
                power <<= 1;
                lod++;
            }

            return lod;
        }

        /// <summary>
        /// Радиус переключения на уровень lod: LodSwitchFactor * ChunkWorldSize(lod).
        /// </summary>
        public static int LodSwitchRadius(int lod)
        {
            return VoxelConstants.LodSwitchFactor * ChunkWorldSize(lod);
        }
    }
}
