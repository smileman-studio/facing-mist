using NUnit.Framework;
using SmilemanStudio.Core.Voxels;
using SmilemanStudio.Simulation.Voxels;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// D4a: трилинейная интерполяция узловых градиентов.
    /// Стыки чанков одного LOD — побитовое совпадение нормалей; отсутствие per-cell
    /// площадок; детерминизм и parity с Burst-джобой. Замыкание кривых LOD-шва
    /// (фартук) проверяется геометрически в
    /// <c>VoxelChunkMesherTests.Seam_CurtainClosesBothCurves_OnSeamPlane</c>.
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

        // --- 4. Оракул: свёртка x→y→z независимой реализацией, побитово ---
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

        // --- 5. Детерминизм + parity с Burst-джобой ---
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
    }
}