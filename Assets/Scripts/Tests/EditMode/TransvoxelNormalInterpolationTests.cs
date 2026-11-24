using NUnit.Framework;
using SmilemanStudio.Core;
using SmilemanStudio.Core.Voxels;
using SmilemanStudio.Simulation.Voxels;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// D4a: трилинейная интерполяция узловых градиентов.
    /// Стыки чанков одного LOD и LOD-граница — побитовое совпадение нормалей;
    /// отсутствие per-cell площадок; детерминизм и parity с Burst-джобой.
    /// </summary>
    public class TransvoxelNormalInterpolationTests
    {
        const int N = VoxelConstants.CacheSize;

        // Истинный SDF сферы в шагах сетки, квантованный как в проде (256 ед./шаг).
        static NativeArray<short> SphereCache(int3 originVoxels, int stepVoxels, float3 center, float radiusVoxels)
        {
            var d = new NativeArray<short>(TransvoxelMesher.CacheVolume, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            for (int k = 0; k < N; k++)
            {
                int3 v = originVoxels + new int3(i - 1, j - 1, k - 1) * stepVoxels;
                float sdf = math.length(new float3(v.x, v.y, v.z) - center) - radiusVoxels;
                d[(i * N + j) * N + k] = VoxelDensity.Quantize(sdf);
            }
            return d;
        }

        // --- 1. Стык двух regular-чанков одного LOD: нормали побитово равны ---
        [Test]
        public void Seam_RegularAdjacentChunks_NormalsBitwiseEqual()
        {
            float3 center = new float3(16f, 8.5f, 8.5f);
            const float r = 6f;
            using var dA = SphereCache(int3.zero, 1, center, r);
            using var dB = SphereCache(new int3(16, 0, 0), 1, center, r);
            var nm = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            var a = TransvoxelMesher.Mesh(dA, nm, Allocator.Persistent);
            var b = TransvoxelMesher.Mesh(dB, nm, Allocator.Persistent);
            nm.Dispose();

            int matched = 0;
            for (int i = 0; i < a.VertexCount; i++)
            {
                if (math.abs(a.Positions[i].x - 16f) > 1e-4f) continue;
                for (int j = 0; j < b.VertexCount; j++)
                {
                    if (math.abs(b.Positions[j].x) > 1e-4f) continue;
                    if (math.abs(a.Positions[i].y - b.Positions[j].y) > 1e-4f) continue;
                    if (math.abs(a.Positions[i].z - b.Positions[j].z) > 1e-4f) continue;
                    Assert.AreEqual(a.Normals[i], b.Normals[j], $"seam vertex {i}/{j}: normals must be bitwise equal");
                    matched++;
                }
            }
            Assert.Greater(matched, 0, "sphere must cross the shared face");
            a.Dispose(); b.Dispose();
        }

        // --- 2. Нет per-cell площадок: нормаль близка к аналитической нормали сферы ---
        [Test]
        public void Normals_MatchAnalyticSphere_NoCellPatching()
        {
            float3 center = new float3(8.5f, 8.5f, 8.5f);
            const float r = 6f;
            using var d = SphereCache(int3.zero, 1, center, r);
            var nm = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            var o = TransvoxelMesher.Mesh(d, nm, Allocator.Persistent);
            nm.Dispose();
            Assert.Greater(o.VertexCount, 0);
            for (int i = 0; i < o.VertexCount; i++)
            {
                float3 analytic = math.normalizesafe(o.Positions[i] - center);
                float dot = math.dot(o.Normals[i], analytic);
                // Старый floor-снэп давал ошибку до ~step/r радиан (dot ~0.9987 при r=6);
                // трилинейная интерполяция — на порядок точнее.
                Assert.Greater(dot, 0.999f, $"vertex {i}: dot={dot} (cell patching?)");
            }
            o.Dispose();
        }

        // --- 3. LOD-граница: transition-грань vs мелкий regular-сосед, побитово ---
        // P5: transition mesh использует face-буфер; узлы плоскости шва — те же LOD0-воксели,
        // что у мелкого соседа, поэтому контракт D4a (побитовое совпадение нормалей) сохранён.
        [Test]
        public void Seam_TransitionFace_MatchesFineRegular_NormalsBitwise()
        {
            var id = new IslandId(1);
            const int radius = 200;
            const int lod = 1;
            int shift = 4 + lod;

            // Точка поверхности: смена знака вдоль -Y.
            bool found = false;
            int3 surf = int3.zero;
            int[] probes = { 8, 24, 40, -16, -32 };
            foreach (int px in probes)
            foreach (int pz in probes)
            {
                int prev = IslandSdf.SdfVoxel(new int3(px, 128, pz), id, radius);
                for (int y = 128; y >= -600; y -= 4)
                {
                    int cur = IslandSdf.SdfVoxel(new int3(px, y, pz), id, radius);
                    if ((cur < 0) != (prev < 0))
                    {
                        surf = new int3(px, y, pz);
                        found = true;
                        break;
                    }
                    prev = cur;
                }
                if (found) break;
            }
            Assert.IsTrue(found, "surface point not found");

            int3 base0 = new int3((surf.x >> shift) << shift, (surf.y >> shift) << shift, (surf.z >> shift) << shift);
            int3[] offsets =
            {
                new int3(0, 0, 0),   new int3(0, -32, 0), new int3(0, 0, -32), new int3(0, -32, -32),
                new int3(32, 0, 0),  new int3(-32, 0, 0), new int3(0, 32, 0),  new int3(0, 0, 32),
            };

            int matched = 0;
            foreach (int3 off in offsets)
            {
                int3 origin0 = base0 + off;
                long keyC = VoxelChunkCoord.Pack(origin0.x >> shift, origin0.y >> shift, origin0.z >> shift, lod);
                using var dCoarse = IslandDensity(keyC, id, radius, Allocator.Persistent);
                var nmC = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
                IslandNodeMat(keyC, id, radius, nmC);

                int3 originG = origin0 - new int3(16, 0, 0);
                long keyG = VoxelChunkCoord.Pack(originG.x >> 4, originG.y >> 4, originG.z >> 4, 0);
                using var dFine = IslandDensity(keyG, id, radius, Allocator.Persistent);
                var nmG = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
                IslandNodeMat(keyG, id, radius, nmG);
                var fine = TransvoxelMesher.Mesh(dFine, nmG, Allocator.Persistent);
                nmG.Dispose();

                var pos = new NativeList<float3>(64, Allocator.Persistent);
                var grad = new NativeList<int3>(64, Allocator.Persistent);
                var mat = new NativeList<byte>(64, Allocator.Persistent);
                var idx = new NativeList<uint>(96, Allocator.Persistent);
                var face = default(TransitionFaceSamples);
                IslandFace(keyC, id, radius, TransitionDirection.XMin, lod, ref face);
                TransvoxelTransitionMesher.MeshTransitionInto(dCoarse, nmC, face, TransitionDirection.XMin, pos, grad, mat, idx);
                if (face.Density.IsCreated) face.Density.Dispose();
                if (face.Material.IsCreated) face.Material.Dispose();
                nmC.Dispose();

                for (int i = 0; i < pos.Length; i++)
                {
                    float3 world = origin0 + pos[i] * 2f;
                    if (math.abs(world.x - origin0.x) > 1e-3f) continue;
                    for (int j = 0; j < fine.VertexCount; j++)
                    {
                        float3 wf = originG + fine.Positions[j];
                        if (math.distance(world, wf) > 1e-3f) continue;
                        float3 nT = TransvoxelMesher.NormalizeGradient(grad[i]);
                        Assert.AreEqual(nT, fine.Normals[j], $"LOD seam vertex {i}/{j} at {world}: normals must be bitwise equal");
                        matched++;
                    }
                }
                pos.Dispose(); grad.Dispose(); mat.Dispose(); idx.Dispose(); fine.Dispose();
                if (matched > 0) break;
            }
            Assert.Greater(matched, 0, "surface must cross the LOD seam face");
        }

        // --- 4. Low-res грань полосы vs own regular coarse-чанка, побитово ---
        [Test]
        public void Seam_TransitionLowResFace_MatchesOwnRegular_NormalsBitwise()
        {
            var id = new IslandId(1);
            float3 center = new float3(8f, 8f, 2f);
            const float r = 7f;
            int3 origin0 = int3.zero;
            using var dCoarse = SphereCache(origin0, 2, center, r);
            var nm = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            var regular = TransvoxelMesher.Mesh(dCoarse, nm, Allocator.Persistent);

            // Синтетический face-буфер (нулевой плотности → нет secondary-точек
            // с пересечением; qw>0 ветвь читает density напрямую).
            var face = default(TransitionFaceSamples);
            face.Density = new NativeArray<short>(VoxelFieldSampler.FaceSamplesVolume, Allocator.Temp);
            face.Material = new NativeArray<byte>(VoxelFieldSampler.FaceSamplesVolume, Allocator.Temp);
            face.Origin = new int3(-1, -1, -1);
            face.Size = new int3(35, 35, 3);
            face.Direction = TransitionDirection.ZMin;

            var pos = new NativeList<float3>(64, Allocator.Persistent);
            var grad = new NativeList<int3>(64, Allocator.Persistent);
            var mat = new NativeList<byte>(64, Allocator.Persistent);
            var idx = new NativeList<uint>(96, Allocator.Persistent);
            TransvoxelTransitionMesher.MeshTransitionInto(dCoarse, nm, face, TransitionDirection.ZMin, pos, grad, mat, idx);
            nm.Dispose();

            int matched = 0;
            for (int i = 0; i < pos.Length; i++)
            {
                if (math.abs(pos[i].z - 1f) > 1e-4f) continue; // low-res грань = 1 coarse-ячейка
                for (int j = 0; j < regular.VertexCount; j++)
                {
                    if (math.distance(pos[i], regular.Positions[j]) > 1e-3f) continue;
                    float3 nT = TransvoxelMesher.NormalizeGradient(grad[i]);
                    Assert.AreEqual(nT, regular.Normals[j], $"low-res seam vertex {i}/{j}");
                    matched++;
                }
            }
            Assert.Greater(matched, 0, "surface must cross the low-res face");
            pos.Dispose(); grad.Dispose(); mat.Dispose(); idx.Dispose(); regular.Dispose();
            face.Density.Dispose(); face.Material.Dispose();
        }

        // --- 5. Оракул: свёртка x→y→z независимой реализацией, побитово ---
        [Test]
        public void TrilinearGradient_MatchesIndependentReference_Bitwise()
        {
            var rnd = new Unity.Mathematics.Random(12345);
            for (int t = 0; t < 64; t++)
            {
                int3[] g = new int3[8];
                for (int i = 0; i < 8; i++) g[i] = rnd.NextInt3(-65534, 65535);
                int fx = rnd.NextInt(0, 65537), fy = rnd.NextInt(0, 65537), fz = rnd.NextInt(0, 65537);

                int3 got = TransvoxelMesher.TrilinearGradient(g[0], g[1], g[2], g[3], g[4], g[5], g[6], g[7], fx, fy, fz);

                //.Reference: явные веса, тот же порядок свёртки и truncation.
                int3[] ax = new int3[4];
                for (int p = 0; p < 4; p++)
                {
                    int3 a = g[p * 2], b = g[p * 2 + 1];
                    long inv = 65536 - fx;
                    ax[p] = new int3(
                        (int)(((long)a.x * inv + (long)b.x * fx) >> 16),
                        (int)(((long)a.y * inv + (long)b.y * fx) >> 16),
                        (int)(((long)a.z * inv + (long)b.z * fx) >> 16));
                }
                int3[] by = new int3[2];
                for (int p = 0; p < 2; p++)
                {
                    int3 a = ax[p * 2], b = ax[p * 2 + 1];
                    long inv = 65536 - fy;
                    by[p] = new int3(
                        (int)(((long)a.x * inv + (long)b.x * fy) >> 16),
                        (int)(((long)a.y * inv + (long)b.y * fy) >> 16),
                        (int)(((long)a.z * inv + (long)b.z * fy) >> 16));
                }
                long invz = 65536 - fz;
                int3 want = new int3(
                    (int)(((long)by[0].x * invz + (long)by[1].x * fz) >> 16),
                    (int)(((long)by[0].y * invz + (long)by[1].y * fz) >> 16),
                    (int)(((long)by[0].z * invz + (long)by[1].z * fz) >> 16));
                Assert.AreEqual(want, got, $"probe {t}");
            }
        }

        // --- 6. Детерминизм + parity с Burst-джобой ---
        [Test]
        public void Normals_Deterministic_AndJobMatchesMainThread()
        {
            float3 center = new float3(8.5f, 8.5f, 8.5f);
            using var d = SphereCache(int3.zero, 1, center, 6f);
            var nm = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            var a = TransvoxelMesher.Mesh(d, nm, Allocator.Persistent);
            var b = TransvoxelMesher.Mesh(d, nm, Allocator.Persistent);
            Assert.AreEqual(a.VertexCount, b.VertexCount);
            for (int i = 0; i < a.VertexCount; i++) Assert.AreEqual(a.Normals[i], b.Normals[i]);

            var pos = new NativeList<float3>(64, Allocator.Persistent);
            var nor = new NativeList<float3>(64, Allocator.Persistent);
            var mat = new NativeList<byte>(64, Allocator.Persistent);
            var idx = new NativeList<uint>(96, Allocator.Persistent);
            new MeshVoxelChunkJob { Density = d, NodeMaterial = nm, Positions = pos, Normals = nor, Materials = mat, Indices = idx }.Run();
            Assert.AreEqual(a.VertexCount, pos.Length);
            for (int i = 0; i < a.VertexCount; i++) Assert.AreEqual(a.Normals[i], nor[i], $"job parity vertex {i}");

            a.Dispose(); b.Dispose(); pos.Dispose(); nor.Dispose(); mat.Dispose(); idx.Dispose(); nm.Dispose();
        }

        // --- helpers: выборка острова через generic-движок (VoxelFieldSampler + Island поля) ---

        static NativeArray<short> IslandDensity(long key, IslandId id, int radius, Allocator alloc)
        {
            var d = new NativeArray<short>(VoxelFieldSampler.CacheVolume, alloc, NativeArrayOptions.UninitializedMemory);
            var field = new IslandDensityField(id, radius);
            VoxelFieldSampler.FillDensity(VoxelFieldSampler.ChunkOrigin0(key), VoxelChunkCoord.UnpackLod(key), field, d);
            return d;
        }

        static void IslandNodeMat(long key, IslandId id, int radius, NativeArray<byte> nm)
        {
            var field = new IslandMaterialAdapter(id);
            VoxelFieldSampler.FillNodeMaterial(VoxelFieldSampler.ChunkOrigin0(key), VoxelChunkCoord.UnpackLod(key), field, nm);
        }

        static void IslandFace(long key, IslandId id, int radius, TransitionDirection dir, int lod, ref TransitionFaceSamples face)
        {
            var dField = new IslandDensityField(id, radius);
            var mField = new IslandMaterialAdapter(id);
            VoxelFieldSampler.FillFace(VoxelFieldSampler.ChunkOrigin0(key), lod, dir, dField, mField, ref face);
        }
    }
}