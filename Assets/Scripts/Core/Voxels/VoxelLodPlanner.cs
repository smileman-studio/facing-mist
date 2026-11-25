using Unity.Collections;
using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Планировщик LOD-каскада воксельного движка (ADR-0002, D3/D7).
    /// </summary>
    /// <remarks>
    /// Регионы концентрические: ОДИН общий центр <see cref="CommonCenter"/> для всех
    /// уровней. Это гарантирует ламинат: R_{k-2} касается, но не заходит в соседнюю
    /// ячейку шва на границе R_{k-1}, поэтому грань шва всегда стыкуется с однородным
    /// уровнем k-1 (без прыжка через уровень и без щелей).
    /// Цена: центр прыгает шагом chunkVox(lodMax) при движении игрока.
    ///
    /// Чанк уровня k мешируется, если он не покрыт регионом R_{k-1} и пересекает AABB
    /// фигуры. Seam-бит на грани ставится тогда и только тогда, когда сосед через эту
    /// грань целиком покрыт R_{k-1} и пересекает AABB.
    /// </remarks>
    public static class VoxelLodPlanner
    {
        /// <summary>Половина региона в чанках своего уровня.</summary>
        public const int RegionHalfChunks = 4;

        /// <summary>
        /// Собрать список заявок на меширование в NativeList (Burst-safe, append-only).
        /// </summary>
        public static void Plan(
            int3 boundsMinVox,
            int3 boundsMaxVox,
            int3 playerVox,
            int lodMax,
            NativeList<VoxelChunkRequest> requests)
        {
            lodMax = math.min(lodMax, VoxelConstants.LodHardMax);
            int3 center = CommonCenter(playerVox, lodMax);

            for (int k = 0; k <= lodMax; k++)
            {
                int chunkVox = 16 << k;
                int half = chunkVox * RegionHalfChunks;

                int3 rMin = center - half;
                int3 rMax = center + half;

                int3 cMin = FloorDiv(rMin, chunkVox);
                int3 cMax = FloorDiv(rMax - 1, chunkVox);

                for (int cx = cMin.x; cx <= cMax.x; cx++)
                for (int cy = cMin.y; cy <= cMax.y; cy++)
                for (int cz = cMin.z; cz <= cMax.z; cz++)
                {
                    int3 coord = new int3(cx, cy, cz);
                    int3 origin0 = coord * chunkVox;
                    int3 cellMax = origin0 + chunkVox;

                    // Чанк покрыт finer-регионом R_{k-1} → мешируется уровнем k-1.
                    if (k > 0 && ChunkCoveredByFiner(origin0, chunkVox, playerVox, k - 1, lodMax))
                        continue;

                    // Чанк вне AABB фигуры → пусто.
                    if (!AabbOverlap(origin0, cellMax, boundsMinVox, boundsMaxVox))
                        continue;

                    // SeamFaceMask: сосед через грань покрыт R_{k-1} и пересекает AABB.
                    int seamMask = 0;
                    if (k >= 1)
                        seamMask = ComputeSeamMask(coord, chunkVox, playerVox, k - 1, lodMax,
                            boundsMinVox, boundsMaxVox);

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

        /// <summary>Общий центр всех регионов, снапнутый к сетке грубого уровня.</summary>
        public static int3 CommonCenter(int3 playerVox, int lodMax) =>
            SnapToGrid(playerVox, 16 << lodMax);

        /// <summary>Ближайший узел сетки шага step.</summary>
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

        /// <summary>Чанк [originIncl, originExcl) пересекает AABB [bMin, bMax].</summary>
        private static bool AabbOverlap(int3 originIncl, int3 originExcl, int3 bMin, int3 bMax)
        {
            return originIncl.x <= bMax.x && originExcl.x > bMin.x &&
                   originIncl.y <= bMax.y && originExcl.y > bMin.y &&
                   originIncl.z <= bMax.z && originExcl.z > bMin.z;
        }

        /// <summary>Границы региона уровня k: [center - half, center + half).</summary>
        private static void RegionBounds(int k, int3 playerVox, int lodMax, out int3 rMin, out int3 rMax)
        {
            int chunkVox = 16 << k;
            int half = chunkVox * RegionHalfChunks;
            int3 center = CommonCenter(playerVox, lodMax);
            rMin = center - half;
            rMax = center + half;
        }

        /// <summary>Чанк [origin, origin+size) ЦЕЛИКОМ внутри региона уровня finerK.</summary>
        private static bool ChunkCoveredByFiner(int3 origin, int size, int3 playerVox, int finerK, int lodMax)
        {
            RegionBounds(finerK, playerVox, lodMax, out int3 rMin, out int3 rMax);
            return origin.x >= rMin.x && origin.x + size <= rMax.x &&
                   origin.y >= rMin.y && origin.y + size <= rMax.y &&
                   origin.z >= rMin.z && origin.z + size <= rMax.z;
        }

        /// <summary>Маска seam-граней coarse-чанка по правилу планировщика.</summary>
        private static int ComputeSeamMask(
            int3 coord, int chunkVox, int3 playerVox, int finerK, int lodMax,
            int3 bMin, int3 bMax)
        {
            int mask = 0;
            mask |= SeamBit(coord + new int3(-1, 0, 0), chunkVox, playerVox, finerK, lodMax, bMin, bMax) ? TransvoxelMesher.SkipXMin : 0;
            mask |= SeamBit(coord + new int3(1, 0, 0), chunkVox, playerVox, finerK, lodMax, bMin, bMax) ? TransvoxelMesher.SkipXMax : 0;
            mask |= SeamBit(coord + new int3(0, -1, 0), chunkVox, playerVox, finerK, lodMax, bMin, bMax) ? TransvoxelMesher.SkipYMin : 0;
            mask |= SeamBit(coord + new int3(0, 1, 0), chunkVox, playerVox, finerK, lodMax, bMin, bMax) ? TransvoxelMesher.SkipYMax : 0;
            mask |= SeamBit(coord + new int3(0, 0, -1), chunkVox, playerVox, finerK, lodMax, bMin, bMax) ? TransvoxelMesher.SkipZMin : 0;
            mask |= SeamBit(coord + new int3(0, 0, 1), chunkVox, playerVox, finerK, lodMax, bMin, bMax) ? TransvoxelMesher.SkipZMax : 0;
            return mask;
        }

        /// <summary>Сосед через грань: покрыт finer-регионом и пересекает фигуру.</summary>
        private static bool SeamBit(int3 neighborCoord, int chunkVox, int3 playerVox, int finerK, int lodMax, int3 bMin, int3 bMax)
        {
            int3 nOrigin = neighborCoord * chunkVox;
            if (!ChunkCoveredByFiner(nOrigin, chunkVox, playerVox, finerK, lodMax))
                return false;
            return AabbOverlap(nOrigin, nOrigin + chunkVox, bMin, bMax);
        }
    }
}