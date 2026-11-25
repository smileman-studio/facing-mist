using System.Collections.Generic;
using NUnit.Framework;
using SmilemanStudio.Core.Voxels;
using Unity.Collections;
using Unity.Mathematics;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты планировщика LOD-каскада <see cref="VoxelLodPlanner"/> (ADR-0002 D3/D7).
    /// </summary>
    /// <remarks>
    /// Главное свойство (корень исправленного бага с дырами): transition-шов на грани
    /// coarse-чанка ставится ТОЛЬКО если сосед через эту грань реально мешируется finer-
    /// уровнем. Проверки: инвариант seam↔finer-сосед, полнота покрытия (нет двойного
    /// покрытия и нет дыр), детерминизм, корректность lodMax=0.
    /// </remarks>
    public class VoxelLodPlannerTests
    {
        // Границы: крупный «остров/сфера», чтобы кэскад был непустой.
        static readonly int3 BoundsMin = new int3(-400, -400, -400);
        static readonly int3 BoundsMax = new int3(400, 400, 400);
        static readonly int3 Player = new int3(440, 0, 0);

        static NativeList<VoxelChunkRequest> Plan(int lodMax)
        {
            var list = new NativeList<VoxelChunkRequest>(Allocator.Persistent);
            VoxelLodPlanner.Plan(BoundsMin, BoundsMax, Player, lodMax, list);
            return list;
        }

        static int3 DirOffset(TransitionDirection dir)
        {
            switch (dir)
            {
                case TransitionDirection.XMin: return new int3(-1, 0, 0);
                case TransitionDirection.XMax: return new int3(1, 0, 0);
                case TransitionDirection.YMin: return new int3(0, -1, 0);
                case TransitionDirection.YMax: return new int3(0, 1, 0);
                case TransitionDirection.ZMin: return new int3(0, 0, -1);
                case TransitionDirection.ZMax: return new int3(0, 0, 1);
                default: return int3.zero;
            }
        }

        [Test]
        public void LodMax0_AllChunksLod0_NoSeams()
        {
            using var reqs = Plan(0);
            Assert.Greater(reqs.Length, 0);
            foreach (var r in reqs)
            {
                Assert.AreEqual(0, r.Lod, "lodMax=0 → все чанки LOD0");
                Assert.AreEqual(0, r.SeamFaceMask, "LOD0 не имеет finer-соседей → швов нет");
            }
        }

        [Test]
        public void Origin_MatchesCoordAndLod()
        {
            using var reqs = Plan(3);
            foreach (var r in reqs)
            {
                int3 expected = VoxelFieldSampler.ChunkOrigin0(r.ChunkCoord, r.Lod);
                Assert.AreEqual(expected, r.Origin0, $"origin чанка {r.ChunkCoord} LOD{r.Lod}");
            }
        }

        [Test]
        public void EveryChunk_IntersectsBounds()
        {
            using var reqs = Plan(3);
            foreach (var r in reqs)
            {
                int size = 16 << r.Lod;
                int3 max = r.Origin0 + size;
                bool overlaps = r.Origin0.x <= BoundsMax.x && max.x > BoundsMin.x &&
                                r.Origin0.y <= BoundsMax.y && max.y > BoundsMin.y &&
                                r.Origin0.z <= BoundsMax.z && max.z > BoundsMin.z;
                Assert.IsTrue(overlaps, $"чанк LOD{r.Lod} {r.ChunkCoord} обязан пересекать границы");
            }
        }

        [Test]
        public void SeamFace_HasFinerNeighborPresent()
        {
            // Ключевое: если у coarse-чанка стоит seam-бит по грани dir, то сосед через dir
            // мешируется finer-уровнем (lod-1) — иначе швар не к чему приварить → дыра.
            using var reqs = Plan(4);

            // Множество finer-чанков: origin → lod.
            var origins = new NativeList<VoxelChunkRequest>(Allocator.Persistent);
            foreach (var r in reqs) origins.Add(r);

            int checkedSeams = 0;
            foreach (var r in reqs)
            {
                if (r.Lod == 0 || r.SeamFaceMask == 0) continue;

                int chunkVox = 16 << r.Lod;
                for (int d = 1; d <= 6; d++)
                {
                    var dir = (TransitionDirection)d;
                    if ((r.SeamFaceMask & TransvoxelMesher.SkipFaceBit(dir)) == 0) continue;

                    int3 neighborOrigin = r.Origin0 + DirOffset(dir) * chunkVox;
                    int finerVox = 16 << (r.Lod - 1);

                    // Ищем finer-чанок уровня lod-1 внутри соседнего coarse-чанка.
                    bool found = false;
                    foreach (var q in origins)
                    {
                        if (q.Lod != r.Lod - 1) continue;
                        bool inside = q.Origin0.x >= neighborOrigin.x && q.Origin0.x < neighborOrigin.x + chunkVox &&
                                      q.Origin0.y >= neighborOrigin.y && q.Origin0.y < neighborOrigin.y + chunkVox &&
                                      q.Origin0.z >= neighborOrigin.z && q.Origin0.z < neighborOrigin.z + chunkVox;
                        if (inside) { found = true; break; }
                    }
                    Assert.IsTrue(found,
                        $"LOD{r.Lod} чанк {r.ChunkCoord}: seam по {dir}, но finer-соседа (lod{r.Lod - 1}) в {neighborOrigin} нет → дыра");
                    checkedSeams++;
                }
            }
            Assert.Greater(checkedSeams, 0, "каскад lodMax=4 обязан иметь LOD-швы");
            origins.Dispose();
        }

        [Test]
        public void NoChunk_CoveredByFiner_IsEmittedAtCoarse()
        {
            // Чанк, покрытый finer-регионом, не мешируется coarse-уровнем (полное покрытие
            // finer-чанками того же объёма). Проверяем: не существует двух чанков, где один
            // строго внутри другого finer-области разного LOD с перекрытием origin.
            using var reqs = Plan(4);
            foreach (var a in reqs)
            {
                foreach (var b in reqs)
                {
                    if (a.Lod >= b.Lod) continue;
                    // b finer: origin b не должен лежать строго внутри a (a coarse).
                    int sizeA = 16 << a.Lod;
                    bool bInsideA = b.Origin0.x >= a.Origin0.x && b.Origin0.x < a.Origin0.x + sizeA &&
                                    b.Origin0.y >= a.Origin0.y && b.Origin0.y < a.Origin0.y + sizeA &&
                                    b.Origin0.z >= a.Origin0.z && b.Origin0.z < a.Origin0.z + sizeA;
                    Assert.IsFalse(bInsideA,
                        $"coarse-чанк {a.ChunkCoord} LOD{a.Lod} перекрывается finer {b.ChunkCoord} LOD{b.Lod} → двойное покрытие");
                }
            }
        }

        [Test]
        public void Plan_IsDeterministic()
        {
            using var first = Plan(4);
            using var second = Plan(4);
            Assert.AreEqual(first.Length, second.Length);
            for (int i = 0; i < first.Length; i++)
            {
                Assert.AreEqual(first[i].ChunkCoord, second[i].ChunkCoord);
                Assert.AreEqual(first[i].Lod, second[i].Lod);
                Assert.AreEqual(first[i].SeamFaceMask, second[i].SeamFaceMask);
            }
        }

        [Test]
        public void ChunkKey_RoundTrips()
        {
            using var reqs = Plan(2);
            foreach (var r in reqs)
            {
                long key = r.ChunkKey;
                Assert.IsTrue(VoxelChunkCoord.IsValid(key));
                Assert.AreEqual(r.ChunkCoord.x, VoxelChunkCoord.UnpackX(key));
                Assert.AreEqual(r.ChunkCoord.y, VoxelChunkCoord.UnpackY(key));
                Assert.AreEqual(r.ChunkCoord.z, VoxelChunkCoord.UnpackZ(key));
                Assert.AreEqual(r.Lod, VoxelChunkCoord.UnpackLod(key));
            }
        }
        
        [Test]
        public void Plan_SeamMask_MatchesActualFinerNeighbors()
        {
            int radiusVox = 400;
            int3 boundsMin = -radiusVox, boundsMax = radiusVox;
            int3 player = new int3(radiusVox * 11 / 10, 0, 0);
            var requests = new NativeList<VoxelChunkRequest>(Allocator.Persistent);
            VoxelLodPlanner.Plan(boundsMin, boundsMax, player, 5, requests);

            // Множество присутствующих чанков (coord,lod).
            var present = new HashSet<long>();
            foreach (var r in requests)
                present.Add(VoxelChunkCoord.Pack(r.ChunkCoord.x, r.ChunkCoord.y, r.ChunkCoord.z, r.Lod));

            int[] dx = { -1, 1, 0, 0, 0, 0 };
            int[] dy = { 0, 0, -1, 1, 0, 0 };
            int[] dz = { 0, 0, 0, 0, -1, 1 };

            foreach (var r in requests)
            {
                if (r.Lod < 1) continue;
                for (int d = 0; d < 6; d++)
                {
                    int3 nb = r.ChunkCoord + new int3(dx[d], dy[d], dz[d]);
                    int nSize = 16 << r.Lod;
                    int3 nOrigin = nb * nSize;
                    // Независимое воспроизведение правила: шов <=> сосед целиком внутри R_{lod-1}
                    // И пересекает AABB фигуры.
                    int3 center = VoxelLodPlanner.CommonCenter(player, 5);
                    int half = (16 << (r.Lod - 1)) * VoxelLodPlanner.RegionHalfChunks;
                    bool covered = nOrigin.x >= center.x - half && nOrigin.x + nSize <= center.x + half
                                                                && nOrigin.y >= center.y - half && nOrigin.y + nSize <= center.y + half
                                                                && nOrigin.z >= center.z - half && nOrigin.z + nSize <= center.z + half;
                    bool aabb = nOrigin.x <= boundsMax.x && nOrigin.x + nSize > boundsMin.x
                                                         && nOrigin.y <= boundsMax.y && nOrigin.y + nSize > boundsMin.y
                                                         && nOrigin.z <= boundsMax.z && nOrigin.z + nSize > boundsMin.z;
                    int bit = d == 0 ? TransvoxelMesher.SkipXMin : d == 1 ? TransvoxelMesher.SkipXMax
                        : d == 2 ? TransvoxelMesher.SkipYMin : d == 3 ? TransvoxelMesher.SkipYMax
                        : d == 4 ? TransvoxelMesher.SkipZMin : TransvoxelMesher.SkipZMax;
                    bool seamSet = (r.SeamFaceMask & bit) != 0;
                    Assert.AreEqual(covered && aabb, seamSet,
                        $"lod{r.Lod} {r.ChunkCoord} dir{d}: seam={seamSet}, expected={covered && aabb}");
                }
            }
            requests.Dispose();
        }
    }
}
