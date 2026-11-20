using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Island-local воксельные координаты LOD0 (ADR-0002, D8).
    /// </summary>
    /// <remarks>
    /// Координата — <see cref="int3"/> в единицах шага LOD0 (0,5 м); начало отсчёта —
    /// <c>IslandManifest.Origin</c>, <c>Origin.Y</c> — референсный уровень нуля острова.
    /// Мировая позиция равна <c>Origin + VoxelCoord * 0.5f</c> и точна, потому что шаг —
    /// степень двойки. Здесь координаты переводятся только в целые миллиметры: float
    /// появляется позднее, в Presentation.
    /// Static class вместо struct-обёртки — прецедент <see cref="IslandFootprint"/>:
    /// данные (int3) уже есть в Unity.Mathematics, обёртка плодила бы новый тип
    /// без добавления инвариантов.
    /// </remarks>
    public static class VoxelCoord
    {
        /// <summary>
        /// Мировая позиция (миллиметры, island-local) воксельной координаты LOD0.
        /// Шаг 0,5 м = 500 мм, поэтому умножение точное и целочисленное.
        /// </summary>
        public static int3 ToMillimeters(int3 voxelCoord)
        {
            return voxelCoord * VoxelLod.VoxelStepMilli(0);
        }

        /// <summary>
        /// Воксельная координата LOD0 из island-local позиции в миллиметрах.
        /// Округление вниз (floor), чтобы отрицательные координаты попадали
        /// в тот же воксель, что и соседние положительные на границе.
        /// </summary>
        public static int3 FromMillimeters(int3 positionMilli)
        {
            int step = VoxelLod.VoxelStepMilli(0);
            return new int3(
                FloorDiv(positionMilli.x, step),
                FloorDiv(positionMilli.y, step),
                FloorDiv(positionMilli.z, step));
        }

        /// <summary>
        /// Воксельная координата LOD0 из island-local позиции в целых метрах.
        /// </summary>
        public static int3 FromMeters(int3 meters)
        {
            return FromMillimeters(meters * 1000);
        }

        /// <summary>
        /// Вокселей LOD0 в одном метре (2 при шаге 0,5 м).
        /// </summary>
        public static int VoxelsPerMeter()
        {
            return 1000 / VoxelLod.VoxelStepMilli(0);
        }

        /// <summary>
        /// Кламп воксельной Y в пределах воксельной полосы (кора плюс переходный слой):
        /// [<see cref="VoxelConstants.CrustStitchMinY"/>, <see cref="VoxelConstants.CrustMaxY"/>]
        /// включительно, в воксельных единицах LOD0.
        /// </summary>
        /// <remarks>
        /// Правки клампятся на <see cref="VoxelConstants.DigLimitY"/> геймплейно; этот
        /// кламп — техническая граница полосы, за которой воксельных данных не существует.
        /// </remarks>
        public static int ClampY(int voxelY)
        {
            int voxelsPerMeter = VoxelsPerMeter();
            int minY = VoxelConstants.CrustStitchMinY * voxelsPerMeter;
            int maxY = VoxelConstants.CrustMaxY * voxelsPerMeter;
            return math.clamp(voxelY, minY, maxY);
        }

        /// <summary>
        /// Целочисленное деление с округлением вниз (делитель положителен).
        /// </summary>
        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            int remainder = value % divisor;

            if (remainder != 0 && ((remainder ^ divisor) < 0))
            {
                quotient--;
            }

            return quotient;
        }
    }
}
