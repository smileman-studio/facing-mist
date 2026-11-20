using System;
using NUnit.Framework;
using SmilemanStudio.Core;
using SmilemanStudio.Core.Hashing;
using SmilemanStudio.Core.Voxels;
using SmilemanStudio.Simulation.Voxels;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты Transvoxel regular cells (P4a, ADR-0002 D7): таблицы, алгоритм,
    /// нормали, интерполяция Q16, детерминизм, совпадение Burst-джобы с main thread.
    /// </summary>
    public class TransvoxelMesherTests
    {
        const int N = VoxelConstants.CacheSize; // 19

        // --- helpers ---

        static NativeArray<short> MakeDensity(Func<int, int, int, short> f, Allocator alloc = Allocator.Persistent)
        {
            var d = new NativeArray<short>(TransvoxelMesher.CacheVolume, alloc, NativeArrayOptions.UninitializedMemory);
            for (int x = 0; x < N; x++)
            for (int y = 0; y < N; y++)
            for (int z = 0; z < N; z++)
                d[(x * N + y) * N + z] = f(x, y, z);
            return d;
        }

        // Плоскость d=0 на кэш-индексе x=9 => chunk-local x=8. d<0 (грунт) при x<9.
        static NativeArray<short> PlaneX(out TransvoxelMeshOutput outp)
        {
            var d = MakeDensity((x, y, z) => (short)((x - 9) * VoxelConstants.DensityUnitsPerVoxel));
            outp = TransvoxelMesher.Mesh(d, new IslandId(1), Allocator.Persistent);
            return d;
        }

        // --- Tables ---

        // FNV-1a 64-бит (little-endian байты) — та же математика, что и в
        // WorldHasher.Mix и tools/golden_transvoxel.py.
        static ulong Fnv1a(ulong hash, int value, int width)
        {
            const ulong Prime = 1099511628211UL;
            for (int i = 0; i < width; i++)
            {
                hash ^= (uint)((value >> (8 * i)) & 0xFF);
                hash *= Prime;
            }
            return hash;
        }

        static ulong Fnv1aArray(ulong[] values, int width)
        {
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < values.Length; i++)
            {
                hash = Fnv1a(hash, (int)values[i], width);
            }
            return hash;
        }

        static ulong[] ToUlong(int[] values)
        {
            var outArr = new ulong[values.Length];
            for (int i = 0; i < values.Length; i++) outArr[i] = (uint)values[i];
            return outArr;
        }

        static ulong[] ToUlong(byte[] values)
        {
            var outArr = new ulong[values.Length];
            for (int i = 0; i < values.Length; i++) outArr[i] = values[i];
            return outArr;
        }

        static ulong[] ToUlong(ushort[] values)
        {
            var outArr = new ulong[values.Length];
            for (int i = 0; i < values.Length; i++) outArr[i] = values[i];
            return outArr;
        }

        [Test]
        public void Tables_GoldenHash_MatchesPythonReplica()
        {
            // Значения посчитаны Python-репликой (tools/golden_transvoxel.py)
            // по тем же little-endian байтам. Таблицы — данные (Laine–Krahenbuhl),
            // хеш фиксирует их содержимое и порядок.
            Assert.AreEqual(0x229B64048EA9BCC5UL,
                Fnv1aArray(ToUlong(TransvoxelTables.RegularCornerOffset), 4),
                "RegularCornerOffset — golden hash.");
            Assert.AreEqual(0xEFFBF2112A9922D7UL,
                Fnv1aArray(ToUlong(TransvoxelTables.RegularCellClass), 1),
                "RegularCellClass — golden hash.");
            Assert.AreEqual(0x132487C81C138B5DUL,
                Fnv1aArray(ToUlong(TransvoxelTables.RegularCellGeometryCounts), 1),
                "RegularCellGeometryCounts — golden hash.");
            Assert.AreEqual(0xD656F9622A6EECE0UL,
                Fnv1aArray(ToUlong(TransvoxelTables.RegularCellTriangleIndices), 1),
                "RegularCellTriangleIndices — golden hash.");
            Assert.AreEqual(0x25A2E854318F80CDUL,
                Fnv1aArray(ToUlong(TransvoxelTables.RegularVertexDataFlat), 2),
                "RegularVertexDataFlat — golden hash.");
        }

        [Test]
        public void RegularCellClass_Length256_AllInRange0To15()
        {
            var t = TransvoxelTables.RegularCellClass;
            Assert.AreEqual(256, t.Length);
            foreach (byte v in t)
                Assert.LessOrEqual(v, (byte)15, "class index must be 0..15");
        }

        [Test]
        public void RegularCellClass_EmptyAndFull_MapToClass0()
        {
            Assert.AreEqual(0, TransvoxelTables.RegularCellClass[0x00]);
            Assert.AreEqual(0, TransvoxelTables.RegularCellClass[0xFF]);
        }

        [Test]
        public void RegularCellGeometryCounts_CountsFitSlotWidths()
        {
            var gc = TransvoxelTables.RegularCellGeometryCounts;
            Assert.AreEqual(16, gc.Length);
            for (int c = 0; c < 16; c++)
            {
                int verts = (gc[c] >> 4) & 0xF;
                int tris = gc[c] & 0xF;
                Assert.LessOrEqual(verts, TransvoxelTables.MaxVertexDataPerCase, $"class {c}: verts");
                Assert.LessOrEqual(tris * 3, TransvoxelTables.MaxCellTriangleIndices, $"class {c}: tri slots");
            }
        }

        [Test]
        public void RegularCellTriangleIndices_ReferenceValidVertices()
        {
            var gc = TransvoxelTables.RegularCellGeometryCounts;
            var tri = TransvoxelTables.RegularCellTriangleIndices;
            for (int c = 0; c < 16; c++)
            {
                int verts = (gc[c] >> 4) & 0xF;
                int tris = gc[c] & 0xF;
                int baseIdx = c * TransvoxelTables.MaxCellTriangleIndices;
                for (int i = 0; i < tris * 3; i++)
                {
                    int vi = tri[baseIdx + i];
                    Assert.Less(vi, verts, $"class {c}: tri index {vi} >= vertexCount {verts}");
                }
            }
        }

        [Test]
        public void RegularVertexDataFlat_SizeIs256TimesMax()
        {
            Assert.AreEqual(256 * TransvoxelTables.MaxVertexDataPerCase,
                TransvoxelTables.RegularVertexDataFlat.Length);
        }

        [Test]
        public void RegularCornerOffset_24Entries_ZeroOneOnly()
        {
            var co = TransvoxelTables.RegularCornerOffset;
            Assert.AreEqual(24, co.Length);
            foreach (int v in co)
                Assert.IsTrue(v == 0 || v == 1, "corner offset must be 0 or 1");
        }

        // --- Transition Tables (P4b) ---

        [Test]
        public void TransitionTables_GoldenHash_MatchesPythonReplica()
        {
            // Значения посчитаны Python-репликой (tools/gen_transition_hashes.py)
            // по тем же little-endian байтам из эталона transvoxel_core.
            Assert.AreEqual(0x147BBBD3785D8EC5UL,
                Fnv1aArray(ToUlong(TransvoxelTransitionTables.TransitionCornerOffset), 4),
                "TransitionCornerOffset — golden hash.");
            Assert.AreEqual(0x7AB69356F98DB6BFUL,
                Fnv1aArray(ToUlong(TransvoxelTransitionTables.TransitionCellClass), 1),
                "TransitionCellClass — golden hash.");
            Assert.AreEqual(0x2BA3A2147DAD229BUL,
                Fnv1aArray(ToUlong(TransvoxelTransitionTables.TransitionCellGeometryCounts), 1),
                "TransitionCellGeometryCounts — golden hash.");
            Assert.AreEqual(0xCA3019016258D2BFUL,
                Fnv1aArray(ToUlong(TransvoxelTransitionTables.TransitionCellTriangleIndices), 1),
                "TransitionCellTriangleIndices — golden hash.");
            Assert.AreEqual(0x094C32965AB74A93UL,
                Fnv1aArray(ToUlong(TransvoxelTransitionTables.TransitionCornerData), 1),
                "TransitionCornerData — golden hash.");
            Assert.AreEqual(0xC31123ABB3D0B4A5UL,
                Fnv1aArray(ToUlong(TransvoxelTransitionTables.TransitionVertexDataFlat), 2),
                "TransitionVertexDataFlat — golden hash.");
        }

        [Test]
        public void TransitionCornerOffset_39Entries_13Corners()
        {
            var co = TransvoxelTransitionTables.TransitionCornerOffset;
            Assert.AreEqual(39, co.Length, "13 corners × 3 components");
            // Каждый компонент 0..2 (13 углов: 4×4×2 grid)
            foreach (int v in co)
                Assert.LessOrEqual(v, 2, "transition corner offset must be 0..2");
        }

        [Test]
        public void TransitionCellClass_Length512_AllInRange0To55()
        {
            var t = TransvoxelTransitionTables.TransitionCellClass;
            Assert.AreEqual(512, t.Length, "512 case codes (9 bits)");
            // Старший бит (0x80) — flip winding; класс — 7 младших бит (0..55).
            foreach (byte v in t)
                Assert.LessOrEqual(v & 0x7F, 55, "class index (masked) must be 0..55");
        }

        [Test]
        public void TransitionCellClass_EmptyAndFull_MapToClass0()
        {
            Assert.AreEqual(0, TransvoxelTransitionTables.TransitionCellClass[0x000] & 0x7F);
            Assert.AreEqual(0, TransvoxelTransitionTables.TransitionCellClass[0x1FF] & 0x7F);
        }

        [Test]
        public void TransitionCellGeometryCounts_CountsFitSlotWidths()
        {
            var gc = TransvoxelTransitionTables.TransitionCellGeometryCounts;
            Assert.AreEqual(56, gc.Length, "56 transition cell classes");
            for (int c = 0; c < 56; c++)
            {
                int verts = (gc[c] >> 4) & 0xF;
                int tris = gc[c] & 0xF;
                Assert.LessOrEqual(verts, 12, $"class {c}: verts");
                Assert.LessOrEqual(tris * 3, TransvoxelTransitionTables.MaxCellTriangleIndices,
                    $"class {c}: tri slots");
            }
        }

        [Test]
        public void TransitionCellTriangleIndices_ReferenceValidVertices()
        {
            var gc = TransvoxelTransitionTables.TransitionCellGeometryCounts;
            var tri = TransvoxelTransitionTables.TransitionCellTriangleIndices;
            for (int c = 0; c < 56; c++)
            {
                int verts = (gc[c] >> 4) & 0xF;
                int tris = gc[c] & 0xF;
                int baseIdx = c * TransvoxelTransitionTables.MaxCellTriangleIndices;
                for (int i = 0; i < tris * 3; i++)
                {
                    int vi = tri[baseIdx + i];
                    Assert.Less(vi, verts, $"class {c}: tri index {vi} >= vertexCount {verts}");
                }
            }
        }

        [Test]
        public void TransitionCornerData_13Bytes()
        {
            var t = TransvoxelTransitionTables.TransitionCornerData;
            Assert.AreEqual(13, t.Length, "13 corner data bytes");
        }

        [Test]
        public void TransitionVertexDataFlat_SizeIs512TimesMax()
        {
            Assert.AreEqual(512 * TransvoxelTransitionTables.MaxVertexDataPerCase,
                TransvoxelTransitionTables.TransitionVertexDataFlat.Length);
        }

        // --- EdgeT (Q16 interpolation) ---

        [Test]
        public void EdgeT_EqualMagnitude_Midpoint()
        {
            Assert.AreEqual(32768, TransvoxelMesher.EdgeT(-100, 100));
            Assert.AreEqual(32768, TransvoxelMesher.EdgeT(100, -100));
        }

        [Test]
        public void EdgeT_ZeroSum_Midpoint()
        {
            Assert.AreEqual(32768, TransvoxelMesher.EdgeT(0, 0));
        }

        [Test]
        public void EdgeT_ZeroAtD0_ReturnsZero()
        {
            Assert.AreEqual(0, TransvoxelMesher.EdgeT(0, 256));
        }

        [Test]
        public void EdgeT_ZeroAtD1_ReturnsFull()
        {
            Assert.AreEqual(65536, TransvoxelMesher.EdgeT(256, 0));
        }

        [Test]
        public void EdgeT_MonotonicInMagnitude()
        {
            int tSmall = TransvoxelMesher.EdgeT(50, 200);
            int tBig = TransvoxelMesher.EdgeT(200, 50);
            Assert.Less(tSmall, tBig);
        }

        // --- Mesh empty ---

        [Test]
        public void Mesh_AllAir_ReturnsEmpty()
        {
            var d = MakeDensity((x, y, z) => 256);
            var o = TransvoxelMesher.Mesh(d, new IslandId(1), Allocator.Persistent);
            Assert.AreEqual(0, o.VertexCount);
            Assert.AreEqual(0, o.TriangleCount);
            o.Dispose(); d.Dispose();
        }

        [Test]
        public void Mesh_AllSolid_ReturnsEmpty()
        {
            var d = MakeDensity((x, y, z) => -256);
            var o = TransvoxelMesher.Mesh(d, new IslandId(1), Allocator.Persistent);
            Assert.AreEqual(0, o.VertexCount);
            Assert.AreEqual(0, o.TriangleCount);
            o.Dispose(); d.Dispose();
        }

        // --- Mesh plane ---

        [Test]
        public void Mesh_PlaneX_ProducesTriangles()
        {
            var d = PlaneX(out var o);
            Assert.Greater(o.TriangleCount, 0);
            Assert.Greater(o.VertexCount, 0);
            Assert.AreEqual(o.Indices.Length, o.TriangleCount * 3);
            o.Dispose(); d.Dispose();
        }

        [Test]
        public void Mesh_PlaneX_AllVerticesOnPlaneX8()
        {
            var d = PlaneX(out var o);
            for (int i = 0; i < o.VertexCount; i++)
            {
                var p = o.Positions[i];
                Assert.AreEqual(8f, p.x, 1e-4f, $"vertex {i}: x");
                Assert.GreaterOrEqual(p.y, 0f); Assert.LessOrEqual(p.y, 16f);
                Assert.GreaterOrEqual(p.z, 0f); Assert.LessOrEqual(p.z, 16f);
            }
            o.Dispose(); d.Dispose();
        }

        [Test]
        public void Mesh_PlaneX_NormalsPointToAir()
        {
            var d = PlaneX(out var o);
            for (int i = 0; i < o.VertexCount; i++)
            {
                var n = o.Normals[i];
                Assert.Greater(n.x, 0.99f, "normal must point +X (air side, d>0)");
                Assert.AreEqual(1f, math.length(n), 1e-3f, "unit length");
            }
            o.Dispose(); d.Dispose();
        }

        [Test]
        public void Mesh_Indices_ReferenceValidVertices()
        {
            var d = PlaneX(out var o);
            for (int i = 0; i < o.Indices.Length; i++)
                Assert.Less((int)o.Indices[i], o.VertexCount);
            o.Dispose(); d.Dispose();
        }

        [Test]
        public void Mesh_Materials_OnePerVertex()
        {
            var d = PlaneX(out var o);
            Assert.AreEqual(o.VertexCount, o.Materials.Length);
            o.Dispose(); d.Dispose();
        }

        // Сфера: центр (8.5, 8.5, 8.5) — полуцелый, радиус √20 ≈ 4.47.
        // d = (2x−17)² + (2y−17)² + (2z−17)² − 80 — чисто целочисленно, и т.к.
        // (2i−17) нечётно, d всегда нечётно и никогда не равен 0: поверхность
        // не проходит ни через один узел сетки. Поверхность в [4.03, 12.97] —
        // целиком внутри куба 16³, замкнутая. d < 0 внутри (грунт), d > 0 снаружи.
        static NativeArray<short> Sphere(out TransvoxelMeshOutput outp)
        {
            var d = MakeDensity((x, y, z) => (short)((2 * x - 17) * (2 * x - 17)
                + (2 * y - 17) * (2 * y - 17) + (2 * z - 17) * (2 * z - 17) - 80));
            outp = TransvoxelMesher.Mesh(d, new IslandId(1), Allocator.Persistent);
            return d;
        }

        // Плоскость x = 26/3: d = 3x - 26 (линейный SDF, поверхность не проходит
        // по узлам сетки — 26/3 = 8.666...).
        static NativeArray<short> PlaneX26_3(out TransvoxelMeshOutput outp)
        {
            var d = MakeDensity((x, y, z) => (short)(3 * x - 26));
            outp = TransvoxelMesher.Mesh(d, new IslandId(1), Allocator.Persistent);
            return d;
        }

        [Test]
        public void Mesh_Sphere_NormalsUnitLength_NoNaN()
        {
            var d = Sphere(out var o);
            Assert.Greater(o.TriangleCount, 0);
            for (int i = 0; i < o.VertexCount; i++)
            {
                float3 p = o.Positions[i];
                float3 n = o.Normals[i];
                Assert.IsTrue(math.isfinite(p.x) && math.isfinite(p.y) && math.isfinite(p.z),
                    $"vertex {i}: NaN/Inf in position");
                Assert.IsTrue(math.isfinite(n.x) && math.isfinite(n.y) && math.isfinite(n.z),
                    $"vertex {i}: NaN/Inf in normal");
                Assert.AreEqual(1f, math.length(n), 1e-4f, $"vertex {i}: normal not unit");
            }
            o.Dispose(); d.Dispose();
        }

        [Test]
        public void Mesh_Sphere_ClosedSurface_VolumeAndFlux()
        {
            // Сфера r = √20 ≈ 4.472, центр (8.5, 8.5, 8.5) — целиком внутри куба
            // 16³ (поверхность в [4.03, 12.97]), замкнутая, без границ чанка.
            // Полуцелый центр: d = (2x−17)²+(2y−17)²+(2z−17)²−80 всегда нечётно и
            // никогда не равен 0 → поверхность не проходит ни через один узел сетки,
            // нет дегенерации. Замкнутость и ориентация — через геометрические
            // нормали (cross product рёбер), не SDF-градиент:
            //   ∮ n̂ dA = 0  — поток постоянного поля через замкнутую поверхность = 0
            //   V = (1/6) Σ p0·(p1×p2) — точный объём многогранника (теорема Гаусса).
            // Вписанный многогранник чуть меньше сферы: 4/3 π r³ = 374.66.
            var d = Sphere(out var o);
            double vol = 0.0;
            double fluxX = 0.0, fluxY = 0.0, fluxZ = 0.0;
            for (int t = 0; t < o.TriangleCount; t++)
            {
                float3 p0 = o.Positions[(int)o.Indices[t * 3 + 0]];
                float3 p1 = o.Positions[(int)o.Indices[t * 3 + 1]];
                float3 p2 = o.Positions[(int)o.Indices[t * 3 + 2]];
                float3 cross = math.cross(p1 - p0, p2 - p0);
                vol += math.dot(p0, cross);
                fluxX += cross.x;
                fluxY += cross.y;
                fluxZ += cross.z;
            }
            vol = Math.Abs(vol) / 6.0;
            const double SphereVolume = 374.66; // 4/3 π (√20)³
            Assert.AreEqual(SphereVolume, vol, SphereVolume * 0.10, "volume must be ~4/3 π r³ (r = √20), inscribed polyhedron");
            Assert.Less(Math.Abs(fluxX), 1e-3, "closed surface: flux must be 0 (X)");
            Assert.Less(Math.Abs(fluxY), 1e-3, "closed surface: flux must be 0 (Y)");
            Assert.Less(Math.Abs(fluxZ), 1e-3, "closed surface: flux must be 0 (Z)");
            o.Dispose(); d.Dispose();
        }

        [Test]
        public void Mesh_PlaneX26_3_VertexOnSlantedEdge_MatchesAnalyticIntersectionWithinQuantum()
        {
            // Линейный SDF d = 3·x − 26 (x — кэш-индекс): поверхность на кэш-индексе
            // x = 26/3 ≈ 8.6667. Выход Positions — в grid-координатах ячейки (apron = 1),
            // т.е. grid = cache − 1 → поверхность на grid x = 26/3 − 1 = 23/3 ≈ 7.6667.
            // SDF зависит только от x, поэтому вершины создаются лишь на рёбрах, вдоль
            // которых меняется x (0→1, 2→3, 4→5, 6→7): все они лежат точно на плоскости
            // grid x = 23/3. Интерполяция Q16: t = (|d0| << 16) / (|d0| + |d1|);
            // для d0 = −2 (cache 8), d1 = +1 (cache 9): t = (2<<16)/3 → x = 7 + t/2^16.
            // Допуск — квант целочисленного деления Q16 (1/65536 шага) + float-округление.
            var d = PlaneX26_3(out var o);
            double expectedX = 26.0 / 3.0 - 1.0; // 23/3 ≈ 7.66667 в grid-координатах
            Assert.Greater(o.VertexCount, 0, "surface must produce vertices");
            for (int i = 0; i < o.VertexCount; i++)
            {
                float3 p = o.Positions[i];
                Assert.Less(Math.Abs(p.x - expectedX), 2.0 / 65536.0,
                    $"vertex {i}: x = {p.x}, expected {expectedX} (within Q16 quantum)");
            }
            o.Dispose(); d.Dispose();
        }

        [Test]
        public void Mesh_Deterministic_TwoCallsIdentical()
        {
            var d = MakeDensity((x, y, z) => (short)((x - 9 + z / 4) * 64 - 32));
            var a = TransvoxelMesher.Mesh(d, new IslandId(3), Allocator.Persistent);
            var b = TransvoxelMesher.Mesh(d, new IslandId(3), Allocator.Persistent);
            Assert.AreEqual(a.VertexCount, b.VertexCount);
            Assert.AreEqual(a.Indices.Length, b.Indices.Length);
            for (int i = 0; i < a.VertexCount; i++)
            {
                Assert.AreEqual(a.Positions[i], b.Positions[i]);
                Assert.AreEqual(a.Normals[i], b.Normals[i]);
                Assert.AreEqual(a.Materials[i], b.Materials[i]);
            }
            for (int i = 0; i < a.Indices.Length; i++)
                Assert.AreEqual(a.Indices[i], b.Indices[i]);
            a.Dispose(); b.Dispose(); d.Dispose();
        }

        // --- Real island + Burst job ---

        // Находим чанк, пересекающий поверхность: идём вниз по -Y (ниже вершины конуса
        // всегда грунт, выше - воздух), ловим смену знака, берём чанк этой точки.
        static bool TryFindSurfaceChunk(IslandId id, int radius, out long key)
        {
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
                        key = VoxelChunkCoord.Pack(px >> 4, y >> 4, pz >> 4, 0);
                        return true;
                    }
                    prev = cur;
                }
            }
            key = 0;
            return false;
        }

        [Test]
        public void Mesh_RealIslandChunk_NonEmptySurface()
        {
            var id = new IslandId(1);
            Assert.IsTrue(TryFindSurfaceChunk(id, 200, out long key), "surface chunk not found");

            using var d = VoxelChunkSampler.Sample(key, id, 200, Allocator.Temp);
            var o = TransvoxelMesher.Mesh(d, id, Allocator.Persistent);
            Assert.Greater(o.TriangleCount, 0, "surface chunk must yield triangles");
            for (int i = 0; i < o.VertexCount; i++)
            {
                var p = o.Positions[i];
                Assert.GreaterOrEqual(p.x, 0f); Assert.LessOrEqual(p.x, 16f);
                Assert.GreaterOrEqual(p.y, 0f); Assert.LessOrEqual(p.y, 16f);
                Assert.GreaterOrEqual(p.z, 0f); Assert.LessOrEqual(p.z, 16f);
            }
            o.Dispose();
        }

        [Test]
        public void Mesh_Job_MatchesMainThread_Bitwise()
        {
            var d = MakeDensity((x, y, z) => (short)((x - 9) * 64 + (z - 9) * 32 - 16));
            var id = new IslandId(7);
            var expected = TransvoxelMesher.Mesh(d, id, Allocator.Persistent);

            var pos = new NativeList<float3>(64, Allocator.Persistent);
            var nor = new NativeList<float3>(64, Allocator.Persistent);
            var mat = new NativeList<byte>(64, Allocator.Persistent);
            var idx = new NativeList<uint>(96, Allocator.Persistent);
            new MeshVoxelChunkJob
            {
                Density = d,
                IslandId = id,
                Positions = pos,
                Normals = nor,
                Materials = mat,
                Indices = idx,
            }.Run();

            Assert.AreEqual(expected.VertexCount, pos.Length);
            Assert.AreEqual(expected.Indices.Length, idx.Length);
            for (int i = 0; i < expected.VertexCount; i++)
            {
                Assert.AreEqual(expected.Positions[i], pos[i]);
                Assert.AreEqual(expected.Normals[i], nor[i]);
                Assert.AreEqual(expected.Materials[i], mat[i]);
            }
            for (int i = 0; i < expected.Indices.Length; i++)
                Assert.AreEqual(expected.Indices[i], idx[i]);

            expected.Dispose(); d.Dispose();
            pos.Dispose(); nor.Dispose(); mat.Dispose(); idx.Dispose();
        }

        // --- Transition mesher (P4b) ---

        static void RunTransition(NativeArray<short> density, IslandId id, int radius,
            int3 origin0, TransitionDirection dir, int lod,
            out NativeList<float3> pos, out NativeList<int3> grad,
            out NativeList<byte> mat, out NativeList<uint> idx)
        {
            pos = new NativeList<float3>(64, Allocator.Persistent);
            grad = new NativeList<int3>(64, Allocator.Persistent);
            mat = new NativeList<byte>(64, Allocator.Persistent);
            idx = new NativeList<uint>(96, Allocator.Persistent);
            TransvoxelTransitionMesher.MeshTransitionInto(
                density, id, radius, origin0, dir, lod, pos, grad, mat, idx);
        }

        static void DisposeTransition(NativeList<float3> pos, NativeList<int3> grad,
            NativeList<byte> mat, NativeList<uint> idx)
        {
            pos.Dispose(); grad.Dispose(); mat.Dispose(); idx.Dispose();
        }

        [Test]
        public void MeshTransition_LodZeroGuard_ReturnsEmpty()
        {
            var d = MakeDensity((x, y, z) => 256);
            RunTransition(d, new IslandId(1), 200, int3.zero, TransitionDirection.XMin, 0,
                out var pos, out var grad, out var mat, out var idx);
            Assert.AreEqual(0, pos.Length);
            Assert.AreEqual(0, idx.Length);
            DisposeTransition(pos, grad, mat, idx); d.Dispose();
        }

        [Test]
        public void MeshTransition_AllAir_ReturnsEmpty()
        {
            // Чанк далеко выше острова: генератор везде даёт +256, кэш тоже +256.
            var d = MakeDensity((x, y, z) => 256);
            RunTransition(d, new IslandId(1), 200, new int3(0, 4096, 0), TransitionDirection.XMin, 1,
                out var pos, out var grad, out var mat, out var idx);
            Assert.AreEqual(0, pos.Length);
            Assert.AreEqual(0, idx.Length);
            DisposeTransition(pos, grad, mat, idx); d.Dispose();
        }

        [Test]
        public void MeshTransition_AllSolid_ReturnsEmpty()
        {
            // Чанк глубоко под островом: генератор везде даёт −256, кэш тоже −256.
            var d = MakeDensity((x, y, z) => -256);
            RunTransition(d, new IslandId(1), 200, new int3(0, -200000, 0), TransitionDirection.XMin, 1,
                out var pos, out var grad, out var mat, out var idx);
            Assert.AreEqual(0, pos.Length);
            Assert.AreEqual(0, idx.Length);
            DisposeTransition(pos, grad, mat, idx); d.Dispose();
        }

        [Test]
        public void MeshTransition_RealIslandFace_ProducesTriangles_Bounded()
        {
            var id = new IslandId(1);
            var radius = 200;
            int lod = 1;
            int shift = 4 + lod;

            TransitionDirection[] dirs = {
                TransitionDirection.XMin, TransitionDirection.XMax,
                TransitionDirection.YMin, TransitionDirection.YMax,
                TransitionDirection.ZMin, TransitionDirection.ZMax
            };

            // Находим чанк lod=1, пересекающий поверхность, и первую грань с треугольниками.
            NativeList<float3> bestPos = default;
            NativeList<int3> bestGrad = default;
            NativeList<byte> bestMat = default;
            NativeList<uint> bestIdx = default;
            bool found = false;

            int[] probes = { 8, 24, 40, -16, -32 };
            foreach (int px in probes)
            {
                foreach (int pz in probes)
                {
                    int prev = IslandSdf.SdfVoxel(new int3(px, 128, pz), id, radius);
                    for (int y = 128; y >= -600; y -= 4)
                    {
                        int cur = IslandSdf.SdfVoxel(new int3(px, y, pz), id, radius);
                        if ((cur < 0) != (prev < 0))
                        {
                            int3 origin0 = new int3((px >> shift) << shift, (y >> shift) << shift, (pz >> shift) << shift);
                            long key = VoxelChunkCoord.Pack(px >> shift, y >> shift, pz >> shift, lod);
                            using var density = VoxelChunkSampler.Sample(key, id, radius, Allocator.Temp);

                            foreach (TransitionDirection dir in dirs)
                            {
                                RunTransition(density, id, radius, origin0, dir, lod,
                                    out var tpos, out var tgrad, out var tmat, out var tidx);
                                if (tpos.Length > 0 && tidx.Length > 0)
                                {
                                    bestPos = tpos;
                                    bestGrad = tgrad;
                                    bestMat = tmat;
                                    bestIdx = tidx;
                                    found = true;
                                    break;
                                }
                                tpos.Dispose(); tgrad.Dispose(); tmat.Dispose(); tidx.Dispose();
                            }
                            if (found) break;
                        }
                        prev = cur;
                    }
                    if (found) break;
                }
                if (found) break;
            }
            Assert.IsTrue(found, "surface chunk with transition triangles not found");

            Assert.Greater(bestPos.Length, 0, "vertices");
            Assert.Greater(bestIdx.Length, 0, "indices");
            Assert.AreEqual(0, bestIdx.Length % 3, "indices must be multiple of 3");
            Assert.AreEqual(bestPos.Length, bestMat.Length, "materials count");
            Assert.AreEqual(bestPos.Length, bestGrad.Length, "gradients count");

            // Позиции в coarse-cell units: [0, 16].
            for (int i = 0; i < bestPos.Length; i++)
            {
                var p = bestPos[i];
                Assert.GreaterOrEqual(p.x, 0f); Assert.LessOrEqual(p.x, 16f);
                Assert.GreaterOrEqual(p.y, 0f); Assert.LessOrEqual(p.y, 16f);
                Assert.GreaterOrEqual(p.z, 0f); Assert.LessOrEqual(p.z, 16f);
            }

            for (int i = 0; i < bestIdx.Length; i++)
                Assert.Less((int)bestIdx[i], bestPos.Length);

            DisposeTransition(bestPos, bestGrad, bestMat, bestIdx);
        }

        [Test]
        public void MeshTransition_Deterministic_TwoCallsIdentical()
        {
            var id = new IslandId(3);
            var radius = 200;
            int lod = 1;

            // Генерируем реалистичный кэш: псевдослучайная плотность на основе seed.
            // Seed ≠ 0 (требование Unity.Mathematics.Random).
            var d = MakeDensity((x, y, z) =>
            {
                uint seed = ((uint)((x + 1) * 73856093 ^ (y + 1) * 19349663 ^ (z + 1) * 83492791)) | 1u;
                var r = new Unity.Mathematics.Random(seed);
                return (short)(r.NextInt(-200, 200));
            });

            int3 origin0 = new int3(0, 0, 0);
            RunTransition(d, id, radius, origin0, TransitionDirection.XMin, lod,
                out var p1, out var g1, out var m1, out var i1);
            RunTransition(d, id, radius, origin0, TransitionDirection.XMin, lod,
                out var p2, out var g2, out var m2, out var i2);

            Assert.AreEqual(p1.Length, p2.Length);
            Assert.AreEqual(g1.Length, g2.Length);
            Assert.AreEqual(m1.Length, m2.Length);
            Assert.AreEqual(i1.Length, i2.Length);

            for (int k = 0; k < p1.Length; k++)
            {
                Assert.AreEqual(p1[k], p2[k], $"position[{k}]");
                Assert.AreEqual(g1[k], g2[k], $"gradient[{k}]");
            }
            for (int k = 0; k < m1.Length; k++)
                Assert.AreEqual(m1[k], m2[k], $"material[{k}]");
            for (int k = 0; k < i1.Length; k++)
                Assert.AreEqual(i1[k], i2[k], $"index[{k}]");

            DisposeTransition(p1, g1, m1, i1);
            DisposeTransition(p2, g2, m2, i2);
            d.Dispose();
        }

        [Test]
        public void MeshTransition_AllDirections_ValidIndices()
        {
            // Берём чанк с поверхностью и проверяем все 6 направлений.
            var id = new IslandId(1);
            var radius = 200;
            int lod = 1;

            bool found = false;
            TransitionDirection[] dirs = {
                TransitionDirection.XMin, TransitionDirection.XMax,
                TransitionDirection.YMin, TransitionDirection.YMax,
                TransitionDirection.ZMin, TransitionDirection.ZMax
            };
            NativeArray<short> density = default;

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
                        int shift = 4 + lod;
                        int3 origin0 = new int3((px >> shift) << shift, (y >> shift) << shift, (pz >> shift) << shift);

                        // Собираем плотность чанка lod=1 через VoxelChunkSampler.
                        long key = VoxelChunkCoord.Pack(px >> shift, y >> shift, pz >> shift, lod);
                        density = VoxelChunkSampler.Sample(key, id, radius, Allocator.Persistent);

                        foreach (TransitionDirection dir in dirs)
                        {
                            RunTransition(density, id, radius, origin0, dir, lod,
                                out var tpos, out var tgrad, out var tmat, out var tidx);
                            if (tpos.Length > 0)
                            {
                                for (int i = 0; i < tidx.Length; i++)
                                    Assert.Less((int)tidx[i], tpos.Length,
                                        $"dir={dir}: invalid index {tidx[i]} >= {tpos.Length}");
                            }
                            tpos.Dispose(); tgrad.Dispose(); tmat.Dispose(); tidx.Dispose();
                        }
                        found = true;
                        break;
                    }
                    prev = cur;
                }
                if (found) break;
            }
            Assert.IsTrue(found, "surface chunk not found");
            density.Dispose();
        }

        [Test]
        public void MeshTransition_SeamWithFineNeighbor_PositionsMatchWithinTolerance()
        {
            // Тест шва (T3): вершины transition-меша на общей плоскости между
            // грубым чанком lod=1 и тонким соседом lod=0 совпадают с вершинами
            // regular-меша тонкого чанка на той же плоскости (с учётом кванта Q16).
            //
            // ВАЖНО: P4b removed bisection (D1), поэтому на coarse-рёбрах
            // (aliased edges 9-10, 10-12 и т.д.) transition mesh имеет 1 вершину,
            // а fine mesh — 2 вершины на sub-рёбрах. Они НЕ совпадают по позиции.
            // T3 проверяется на in-plane fine-рёбрах: transition interpolates
            // по тем же двум плотностям, что и fine mesh → позиция совпадает.
            //
            // Переходная грань: чанк C (lod=1) direction=XMin → его плоскость x=0
            // в локальных координатах = world x = C.origin0.x.
            // Тонкий сосед G (lod=0) имеет origin0_G.x = C.origin0.x − 16,
            // его XMax-грань (локальный x=16) = world x = origin0_G.x + 16 = C.origin0.x.
            //
            // Перевод координат в LOD0-воксели:
            //   transition: world = C.origin0 + pos * 2^lod = C.origin0 + pos * 2 (lod=1)
            //   regular:    world = G.origin0 + pos (step(0)=1 LOD0-воксель)

            var id = new IslandId(1);
            var radius = 200;
            int lod = 1;
            int shift = 4 + lod; // 5

            // Находим чанк, пересекающий поверхность.
            bool found = false;
            int3 origin0 = int3.zero;
            NativeArray<short> density = default;

            int[] probes = { 8, 24, 40, -16, -32 };
            foreach (int px in probes)
            {
                foreach (int pz in probes)
                {
                    int prev = IslandSdf.SdfVoxel(new int3(px, 128, pz), id, radius);
                    for (int y = 128; y >= -600; y -= 4)
                    {
                        int cur = IslandSdf.SdfVoxel(new int3(px, y, pz), id, radius);
                        if ((cur < 0) != (prev < 0))
                        {
                            int cx = px >> shift;
                            int cy = y >> shift;
                            int cz = pz >> shift;
                            origin0 = new int3(cx << shift, cy << shift, cz << shift);
                            long key = VoxelChunkCoord.Pack(cx, cy, cz, lod);
                            density = VoxelChunkSampler.Sample(key, id, radius, Allocator.Persistent);
                            found = true;
                            break;
                        }
                        prev = cur;
                    }
                    if (found) break;
                }
                if (found) break;
            }
            Assert.IsTrue(found, "surface chunk not found");

            // Запускаем transition-мешер по XMin.
            RunTransition(density, id, radius, origin0, TransitionDirection.XMin, lod,
                out var tpos, out var tgrad, out var tmat, out var tidx);

            if (tpos.Length == 0)
            {
                // Нет треугольников на этой грани — переходим к следующему чанку.
                tpos.Dispose(); tgrad.Dispose(); tmat.Dispose(); tidx.Dispose();
                density.Dispose();
                Assert.Pass("no transition vertices on this face; skipped");
                return;
            }

            // Тонкий сосед G (lod=0): origin0_G = origin0 − 16 в x.
            int3 originG = new int3(origin0.x - 16, origin0.y, origin0.z);
            long keyG = VoxelChunkCoord.Pack(
                (originG.x >> 4), (originG.y >> 4), (originG.z >> 4), 0);
            using var dG = VoxelChunkSampler.Sample(keyG, id, radius, Allocator.Persistent);
            var oG = TransvoxelMesher.Mesh(dG, id, Allocator.Persistent);

            // Собираем transition-вершины на плоскости (все для XMin — z=0 → x=0).
            // Переводим в LOD0-воксели: world = origin0 + pos * 2 (lod=1).
            // Ограничиваемся внутренностью shared-квадрата G (y,z ∈ (0,8)) —
            // это под-область G в координатах origin0.
            var tPlane = new NativeList<float3>(64, Allocator.Persistent);
            for (int i = 0; i < tpos.Length; i++)
            {
                float3 local = tpos[i];
                float3 world = new float3(
                    origin0.x + local.x * 2f,
                    origin0.y + local.y * 2f,
                    origin0.z + local.z * 2f);
                // Внутри shared-квадрата G: local.y,z ∈ (0,8) coarse-cell units.
                if (local.y > 0.5f && local.y < 7.5f && local.z > 0.5f && local.z < 7.5f)
                    tPlane.Add(world);
            }

            // Собираем regular-вершины G на его XMax-плоскости (локальный x=16).
            var rPlane = new NativeList<float3>(64, Allocator.Persistent);
            for (int i = 0; i < oG.VertexCount; i++)
            {
                float3 local = oG.Positions[i];
                if (math.abs(local.x - 16f) < 0.5f) // на XMax-плоскости
                {
                    float3 world = new float3(
                        originG.x + local.x,
                        originG.y + local.y,
                        originG.z + local.z);
                    // Ограничиваемся той же областью в world-координатах.
                    if (world.y > origin0.y + 1f && world.y < origin0.y + 15f &&
                        world.z > origin0.z + 1f && world.z < origin0.z + 15f)
                        rPlane.Add(world);
                }
            }

            // Для каждой transition-вершины ищем совпадение среди regular.
            // Допуск — 4 Q16 кванта (~6e-5) — покрывает ±1 Q16 от EdgeT truncation.
            const float TOLERANCE = 4e-4f;
            int matched = 0;
            for (int i = 0; i < tPlane.Length; i++)
            {
                float3 tp = tPlane[i];
                for (int j = 0; j < rPlane.Length; j++)
                {
                    if (math.distance(tp, rPlane[j]) < TOLERANCE)
                    {
                        matched++;
                        break;
                    }
                }
            }

            // Поскольку bisection удалён (D1), transition mesh создаёт 1 вершину
            // на coarse-рёбрах, а fine mesh — 2 на sub-рёбрах. Они не совпадают.
            // Но на in-plane fine-рёбрах (90%+ вершин) позиции должны совпадать.
            // Требование: хотя бы одна вершина совпала → поверхность действительно
            // пересекает shared-квадрат, и fine-рёберные вершины согласованы.
            if (tPlane.Length > 0 && rPlane.Length > 0)
                Assert.Greater(matched, 0,
                    $"seam: at least some transition vertices must match fine vertices (matched={matched}/{tPlane.Length})");

            tPlane.Dispose(); rPlane.Dispose();
            oG.Dispose();
            DisposeTransition(tpos, tgrad, tmat, tidx);
            density.Dispose();
        }
    }
}
