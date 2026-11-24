using Unity.Collections;
using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Планировщик LOD-каскада воксельного движка (ADR-0002, D3/D7).
    /// </summary>
    /// <remarks>
    /// Единственный модуль, который решает, КАКИЕ чанки мешировать, на КАКОМ LOD и
    /// ГДЕ ставить transition-шов. Вызывающий поставляет только: границы фигуры
    /// (voxel LOD0 AABB), позицию игрока (voxel LOD0) и число уровней. Всё остальное
    /// считается здесь и вызывается в джобе (Burst-safe, без managed-аллокаций).
    ///
    /// Схема — geometry-clipmap: для каждого уровня k вложенный регион R_k — куб из
    /// (2·RegionHalfChunks)³ чанков уровня k, центр снапан на сетку уровня k+1. За счёт
    /// снапа finer-регион R_{k-1} покрывает coarse-чанк уровня k либо ЦЕЛИКОМ, либо не
    /// покрывает вовсе: частичного перекрытия нет по построению. Чанк уровня k мешируется,
    /// если он не покрыт R_{k-1} и пересекает границы фигуры.
    ///
    /// Шов (корень исправленного бага). Transition на грани coarse-чанка ставится ТОГДА
    /// И ТОЛЬКО КОГДА соседний coarse-чанк через эту грань покрыт finer-регионом (то есть
    /// реально будет меширован на уровне k-1). Старый код ставил шов по совпадению грани с
    /// границей региона, не проверяя, что сосед finer присутствует, и на угловых чанках
    /// возникала transition-полоса без finer-соседа — дыра/двойная стенка. Здесь шов всегда
    /// согласован с фактическим наличием finer-геометрии.
    ///
    /// Прыжок через уровень (k → k-2) невозможен: регион R_{k-2} вложен в R_{k-1} с
    /// зазором не меньше одного coarse-чанка, поэтому граничный слой R_{k-1} всегда
    /// мешируется на k-1, а не на k-2.
    /// </remarks>
    public static class VoxelLodPlanner
    {
        /// <summary>Половина региона в чанках своего уровня (чётное — для выравнивания границ).</summary>
        public const int RegionHalfChunks = 4;

        /// <summary>
        /// Собрать список заявок на меширование в NativeList (Burst-safe, append-only).
        /// </summary>
        /// <param name="boundsMinVox">Минимум AABB фигуры в вокселях LOD0 (включительно).</param>
        /// <param name="boundsMaxVox">Максимум AABB фигуры в вокселях LOD0 (включительно).</param>
        /// <param name="playerVox">Позиция игрока в вокселях LOD0.</param>
        /// <param name="lodMax">Максимальный уровень (обычно LodMax(radius)).</param>
        /// <param name="requests">Выход: заявки (не очищается — append).</param>
        public static void Plan(
            int3 boundsMinVox,
            int3 boundsMaxVox,
            int3 playerVox,
            int lodMax,
            NativeList<VoxelChunkRequest> requests)
        {
            lodMax = math.min(lodMax, VoxelConstants.LodHardMax);

            // Для каждого уровня: регион R_k, центр снапан на сетку k+1.
            for (int k = 0; k <= lodMax; k++)
            {
                int chunkVox = 16 << k;
                int half = chunkVox * RegionHalfChunks;
                int snapStep = k < lodMax ? chunkVox << 1 : chunkVox;
                int3 center = SnapToGrid(playerVox, snapStep);

                int3 rMin = center - half;
                int3 rMax = center + half;

                // Диапазон координат чанков уровня k, покрывающий регион.
                int3 cMin = FloorDiv(rMin, chunkVox);
                int3 cMax = FloorDiv(rMax - 1, chunkVox);

                for (int cx = cMin.x; cx <= cMax.x; cx++)
                for (int cy = cMin.y; cy <= cMax.y; cy++)
                for (int cz = cMin.z; cz <= cMax.z; cz++)
                {
                    int3 coord = new int3(cx, cy, cz);
                    int3 origin0 = coord * chunkVox;
                    int3 cellMax = origin0 + chunkVox; // exclusive

                    // Чанк покрыт finer-регионом R_{k-1} → мешируется уровнем k-1.
                    if (k > 0 && ChunkCoveredByFiner(origin0, chunkVox, playerVox, k - 1))
                        continue;

                    // Чанк вне AABB фигуры → пусто.
                    if (!AabbOverlap(origin0, cellMax, boundsMinVox, boundsMaxVox))
                        continue;

                    // SeamFaceMask: сосед через грань покрыт R_{k-1} (и реально мешируется).
                    int seamMask = 0;
                    if (k >= 1)
                        seamMask = ComputeSeamMask(coord, chunkVox, playerVox, k - 1, lodMax, boundsMinVox, boundsMaxVox);

                    requests.Add(new VoxelChunkRequest
                    {
                        ChunkCoord = coord,
                        Lod = (byte)k,
                        Origin0 = origin0,
                        SeamFaceMask = seamMask,
                    });
                }
            }
        }

        // --- helpers (все чистые, Burst-safe) ---

        /// <summary>Ближайший узел сетки шага step (округление центра региона).</summary>
        public static int3 SnapToGrid(int3 v, int step) => new int3(
            RoundDiv(v.x, step) * step,
            RoundDiv(v.y, step) * step,
            RoundDiv(v.z, step) * step);

        private static int RoundDiv(int a, int b) => (int)math.round(a / (float)b);

        private static int3 FloorDiv(int3 a, int b) => new int3(
            FloorDiv(a.x, b), FloorDiv(a.y, b), FloorDiv(a.z, b));

        private static int FloorDiv(int a, int b)
        {
            int q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }

        /// <summary>Чанк [origin, origin+size) пересекает AABB [bMin, bMax] (включая границы).</summary>
        private static bool AabbOverlap(int3 originIncl, int3 originExcl, int3 bMin, int3 bMax)
        {
            return originIncl.x <= bMax.x && originExcl.x > bMin.x &&
                   originIncl.y <= bMax.y && originExcl.y > bMin.y &&
                   originIncl.z <= bMax.z && originExcl.z > bMin.z;
        }

        /// <summary>
        /// Регион finer-уровня k в воксельных границах [rMin, rMax) (half = chunkVox_k * RegionHalfChunks).
        /// </summary>
        private static void FinerRegionBounds(int k, int3 playerVox, out int3 rMin, out int3 rMax)
        {
            int chunkVox = 16 << k;
            int half = chunkVox * RegionHalfChunks;
            int3 center = SnapToGrid(playerVox, chunkVox << 1);
            rMin = center - half;
            rMax = center + half;
        }

        /// <summary>Чанк уровня k+1 [origin, origin+size) ЦЕЛИКОМ внутри региона уровня k.</summary>
        private static bool ChunkCoveredByFiner(int3 origin, int size, int3 playerVox, int finerK)
        {
            FinerRegionBounds(finerK, playerVox, out int3 rMin, out int3 rMax);
            return origin.x >= rMin.x && origin.x + size <= rMax.x &&
                   origin.y >= rMin.y && origin.y + size <= rMax.y &&
                   origin.z >= rMin.z && origin.z + size <= rMax.z;
        }

        /// <summary>
        /// Маска seam-граней coarse-чанка: бит dir = 1, если сосед через dir покрыт
        /// finer-регионом И сам сосед пересекает AABB фигуры (значит, будет меширован).
        /// </summary>
        private static int ComputeSeamMask(
            int3 coord, int chunkVox, int3 playerVox, int finerK, int lodMax,
            int3 bMin, int3 bMax)
        {
            int mask = 0;
            // 6 направлений: смещения координаты чанка на сетке k.
            mask |= SeamBit(coord + new int3(-1, 0, 0), chunkVox, playerVox, finerK, bMin, bMax) ? TransvoxelMesher.SkipXMin : 0;
            mask |= SeamBit(coord + new int3(1, 0, 0), chunkVox, playerVox, finerK, bMin, bMax) ? TransvoxelMesher.SkipXMax : 0;
            mask |= SeamBit(coord + new int3(0, -1, 0), chunkVox, playerVox, finerK, bMin, bMax) ? TransvoxelMesher.SkipYMin : 0;
            mask |= SeamBit(coord + new int3(0, 1, 0), chunkVox, playerVox, finerK, bMin, bMax) ? TransvoxelMesher.SkipYMax : 0;
            mask |= SeamBit(coord + new int3(0, 0, -1), chunkVox, playerVox, finerK, bMin, bMax) ? TransvoxelMesher.SkipZMin : 0;
            mask |= SeamBit(coord + new int3(0, 0, 1), chunkVox, playerVox, finerK, bMin, bMax) ? TransvoxelMesher.SkipZMax : 0;
            return mask;
        }

        // Сосед через грань coarse-чанка: покрыт finer-регионом и пересекает фигуру.
        private static bool SeamBit(int3 neighborCoord, int chunkVox, int3 playerVox, int finerK, int3 bMin, int3 bMax)
        {
            int3 nOrigin = neighborCoord * chunkVox;
            if (!ChunkCoveredByFiner(nOrigin, chunkVox, playerVox, finerK))
                return false;
            return AabbOverlap(nOrigin, nOrigin + chunkVox, bMin, bMax);
        }
    }
}
