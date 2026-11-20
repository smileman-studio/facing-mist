using SmilemanStudio.Core;
using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Целочисленный SDF острова (ADR-0002, D1/D6).
    /// </summary>
    /// <remarks>
    /// Форма острова — конус с вершиной (0, −D, 0) и радиусом R на y = 0,
    /// плюс волнистая карта высот TopSurface(x, z) сверху.
    /// Результат — целое число метров: d &lt;= 0 — грунт, d &gt; 0 — воздух.
    ///
    /// Целочисленный пайплайн (ADR D6): знак занятости целочисленно точен
    /// на всех платформах. sqrt используется только для масштаба знаменателя
    /// (L = sqrt(R²+D²)) и радиуса (rho = sqrt(x²+z²)) — оба floor-sqrt.
    /// Никаких float-трансцендентных: sin, cos, exp запрещены.
    ///
    /// Связь с плотностью (ADR-0002, D4): один шаг сетки = DensityUnitsPerVoxel
    /// единиц плотности. Для воксельного пайплайна используется
    /// <see cref="SdfVoxel"/> (единицы шага LOD0), где
    /// SdfVoxel * DensityUnitsPerVoxel = short плотности.
    /// </remarks>
    public static class IslandSdf
    {
        /// <summary>Глубина вершины конуса, м: D = R * ApexDepthPermille / 1000.</summary>
        public static int ApexDepth(int radius)
        {
            return radius * VoxelConstants.ApexDepthPermille / 1000;
        }

        /// <summary>Максимальная высота рельефа, м: min(R * TopReliefPermille / 1000, TopHeightHardCap).</summary>
        public static int TopHeightMax(int radius)
        {
            int relief = radius * VoxelConstants.TopReliefPermille / 1000;
            return relief < VoxelConstants.TopHeightHardCap ? relief : VoxelConstants.TopHeightHardCap;
        }

        /// <summary>
        /// Высота поверхности рельефа в точке (x, z), м.
        /// </summary>
        /// <remarks>
        /// TopSurface = Fbm2D(x * scale, z * scale, seed) * TopHeightMax / 65535.
        /// Функция чистая: зависит только от позиции, ID острова и радиуса.
        /// Результат ∈ [0, TopHeightMax].
        /// </remarks>
        public static int TopSurface(int x, int z, IslandId id, int radius)
        {
            int thm = TopHeightMax(radius);
            uint seed = VoxelNoise.Hash((uint)id.Value);
            int scale = (1 << 16) / VoxelConstants.ReliefNoisePeriod;
            int v = VoxelNoise.Fbm2D(x * scale, z * scale, seed);
            return v * thm / 65535;
        }

        /// <summary>
        /// SDF острова в точке (x, y, z), м.
        /// </summary>
        /// <remarks>
        /// d = max(dCone, dTop), где:
        /// dCone = (R * (y + D) − D * rho) / L,  L = floor_sqrt(R² + D²), rho = floor_sqrt(x² + z²).
        /// dTop = y − TopSurface(x, z, id, radius).
        /// d &lt;= 0 — внутри острова (грунт), d &gt; 0 — воздух.
        /// </remarks>
        public static int Sdf(int x, int y, int z, IslandId id, int radius)
        {
            int dCone = DCone(x, y, z, radius);
            int dTop = y - TopSurface(x, z, id, radius);
            return dCone > dTop ? dCone : dTop;
        }

        /// <summary>
        /// SDF острова в воксельной координате LOD0 (шаг 0,5 м), в единицах шага сетки.
        /// </summary>
        /// <param name="voxelCoord">Island-local воксель LOD0 (<see cref="VoxelCoord"/>).</param>
        /// <param name="id">Идентификатор острова.</param>
        /// <param name="radius">Радиус острова, м.</param>
        /// <returns>
        /// Целое число шагов сетки LOD0: d &lt;= 0 — грунт, d &gt; 0 — воздух.
        /// </returns>
        /// <remarks>
        /// Тот же SDF, что <see cref="Sdf(int,int,int,IslandId,int)"/>, но вычисленный
        /// в единицах шага LOD0 (0,5 м). Метрового разрешения для воксельного меша мало:
        /// поверхность встала бы только на чётные сэмплы.
        ///
        /// Все длины внутри — в шагах LOD0 (полуметрах), поэтому радиусы и глубины
        /// умножаются на 2, а координаты берутся как есть:
        ///   dCone = (R * (y + 2D) − D * rho) / L,  rho = floor_sqrt(x² + z²), L = floor_sqrt(R² + D²)
        ///   dTop  = y − 2 * TopSurface(x·½, z·½, id, radius)
        /// Размерности сходятся: числитель — м·шаг, знаменатель — м, результат — шаг.
        ///
        /// Согласованность с метровым SDF приближённая, а не точная: floor_sqrt от
        /// удвоенных координат не масштабируется ровно (floor(2√n) != 2·floor(√n)).
        /// Для чётной воксельной координаты v = 2p: |SdfVoxel(v) / 2 − Sdf(p)| &lt;= 1 шаг,
        /// по dTop — точное равенство.
        ///
        /// Связь с плотностью (ADR-0002, D4): <c>SdfVoxel * DensityUnitsPerVoxel</c> =
        /// <c>short</c> плотности; |SdfVoxel| &lt;= 127 гарантирует влезание в short.
        /// </remarks>
        public static int SdfVoxel(int3 voxelCoord, IslandId id, int radius)
        {
            int dCone = DConeVoxel(voxelCoord, radius);
            int dTop = voxelCoord.y - 2 * TopSurfaceVoxel(voxelCoord.x, voxelCoord.z, id, radius);
            return dCone > dTop ? dCone : dTop;
        }

        /// <summary>
        /// Высота рельефа в полуметровых координатах, в метрах.
        /// </summary>
        /// <remarks>
        /// Та же функция, что <see cref="TopSurface(int,int,IslandId,int)"/>, но сетка
        /// вдвое плотнее: вход в шум <c>coord * ((1 &lt;&lt; 16) / (2 * ReliefNoisePeriod))</c>.
        /// Масштаб половинится без потери точности (1024 → 512), поэтому на чётных
        /// координатах значение совпадает с метровым TopSurface.
        /// Возвращает метры: dTop в полуметрах получается умножением на 2 точно,
        /// без второго округления целочисленным делением.
        /// </remarks>
        private static int TopSurfaceVoxel(int x, int z, IslandId id, int radius)
        {
            int thm = TopHeightMax(radius);
            uint seed = VoxelNoise.Hash((uint)id.Value);
            int scale = (1 << 16) / (VoxelConstants.ReliefNoisePeriod * 2);
            int v = VoxelNoise.Fbm2D(x * scale, z * scale, seed);
            return v * thm / 65535;
        }

        /// <summary>
        /// Целочисленный SDF конуса в единицах шага LOD0 (полуметрах).
        /// </summary>
        /// <remarks>
        /// d = (R * (y + 2D) − D * rho) / L. Радиус R и глубина D — в метрах,
        /// координаты — в шагах LOD0, поэтому D удваивается только в сумме с y.
        /// L = floor_sqrt(R² + D²) — тот же знаменатель, что в метровом пайплайне.
        /// Деление — C#-стилем (truncation toward zero).
        /// </remarks>
        private static int DConeVoxel(int3 v, int radius)
        {
            int d = ApexDepth(radius);
            int r = radius;
            int t = v.y + 2 * d;
            int rho = FloorSqrt((long)v.x * v.x + (long)v.z * v.z);
            long num = (long)r * t - (long)d * rho;
            int den = FloorSqrt((long)r * r + (long)d * d);
            return (int)(num / den);
        }

        /// <summary>
        /// Целочисленный SDF конуса: вершина (0, −D, 0), радиус R на y = 0.
        /// </summary>
        /// <remarks>
        /// d = (R * (y + D) − D * rho) / L.
        /// L = floor_sqrt(R² + D²) — масштаб знаменателя (ADR D6: sqrt только для масштаба).
        /// rho = floor_sqrt(x² + z²) — радиальная координата.
        /// Деление — C#-стилем (truncation toward zero), не floor.
        /// </remarks>
        private static int DCone(int x, int y, int z, int radius)
        {
            int d = ApexDepth(radius);
            int r = radius;
            int t = y + d;
            int rho = FloorSqrt((long)x * x + (long)z * z);
            long num = (long)r * t - (long)d * rho;
            int den = FloorSqrt((long)r * r + (long)d * d);
            return (int)(num / den);
        }

        /// <summary>
        /// Целочисленный floor(sqrt(n)) для n &gt;= 0.
        /// </summary>
        /// <remarks>
        /// Алгоритм bit-by-bit: гарантированно floor (не ceil, не nearest).
        /// Работает с long-аргументами до 2^62.
        /// </remarks>
        public static int FloorSqrt(long n)
        {
            if (n <= 0) return 0;

            long result = 0;
            long bit = 1L << 62;
            while (bit > n) bit >>= 2;

            while (bit != 0)
            {
                long candidate = result + bit;
                if (n >= candidate)
                {
                    n -= candidate;
                    result = (result >> 1) + bit;
                }
                else
                {
                    result >>= 1;
                }
                bit >>= 2;
            }
            return (int)result;
        }
    }
}
