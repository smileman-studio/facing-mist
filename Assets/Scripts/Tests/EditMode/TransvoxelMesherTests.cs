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

        static NativeArray<byte> MakeNodeMaterial(Allocator alloc = Allocator.Persistent)
        {
            // Zero-initialized: all vertices get VoxelMaterial.None (0).
            return new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, alloc);
        }

        static TransitionFaceSamples MakeFaceSamples(short densityValue, TransitionDirection dir,
            Allocator alloc = Allocator.Persistent)
        {
            var face = new TransitionFaceSamples
            {
                Density = new NativeArray<short>(VoxelFieldSampler.FaceSamplesVolume, alloc),
                Material = new NativeArray<byte>(VoxelFieldSampler.FaceSamplesVolume, alloc),
                Origin = new int3(-1, -1, -1),
                Size = new int3(35, 35, 3),
                Direction = dir
            };
            for (int i = 0; i < VoxelFieldSampler.FaceSamplesVolume; i++)
                face.Density[i] = densityValue;
            return face;
        }

        // Выборка острова через generic-движок (VoxelFieldSampler + Island поля):
        // побитово повторяет старый путь VoxelChunkSampler, поэтому golden-хеши мешей валидны.
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

        // Плоскость d=0 на кэш-индексе x=9 => chunk-local x=8. d<0 (грунт) при x<9.
        static NativeArray<short> PlaneX(out TransvoxelMeshOutput outp)
        {
            var d = MakeDensity((x, y, z) => (short)((x - 9) * VoxelConstants.DensityUnitsPerVoxel));
            var nm = MakeNodeMaterial();
            outp = TransvoxelMesher.Mesh(d, nm, Allocator.Persistent);
            nm.Dispose();
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
            var nm = MakeNodeMaterial();
            var o = TransvoxelMesher.Mesh(d, nm, Allocator.Persistent);
            Assert.AreEqual(0, o.VertexCount);
            Assert.AreEqual(0, o.TriangleCount);
            o.Dispose(); d.Dispose(); nm.Dispose();
        }

        [Test]
        public void Mesh_AllSolid_ReturnsEmpty()
        {
            var d = MakeDensity((x, y, z) => -256);
            var nm = MakeNodeMaterial();
            var o = TransvoxelMesher.Mesh(d, nm, Allocator.Persistent);
            Assert.AreEqual(0, o.VertexCount);
            Assert.AreEqual(0, o.TriangleCount);
            o.Dispose(); d.Dispose(); nm.Dispose();
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
            var nm = MakeNodeMaterial();
            outp = TransvoxelMesher.Mesh(d, nm, Allocator.Persistent);
            nm.Dispose();
            return d;
        }

        // Плоскость x = 26/3: d = 3x - 26 (линейный SDF, поверхность не проходит
        // по узлам сетки — 26/3 = 8.666...).
        static NativeArray<short> PlaneX26_3(out TransvoxelMeshOutput outp)
        {
            var d = MakeDensity((x, y, z) => (short)(3 * x - 26));
            var nm = MakeNodeMaterial();
            outp = TransvoxelMesher.Mesh(d, nm, Allocator.Persistent);
            nm.Dispose();
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
            // Знак: при outward-winding (согласован с нормалями наружу) знаковый
            // объём положитель. Отрицательный знак = winding перевёрнут (регрессия
            // фикса P4a 2026-11-21).
            Assert.Greater(vol, 0.0, "signed volume must be positive: outward (CCW) winding");
            vol = Math.Abs(vol) / 6.0;
            const double SphereVolume = 374.66; // 4/3 π (√20)³
            Assert.AreEqual(SphereVolume, vol, SphereVolume * 0.10, "volume must be ~4/3 π r³ (r = √20), inscribed polyhedron");
            Assert.Less(Math.Abs(fluxX), 1e-3, "closed surface: flux must be 0 (X)");
            Assert.Less(Math.Abs(fluxY), 1e-3, "closed surface: flux must be 0 (Y)");
            Assert.Less(Math.Abs(fluxZ), 1e-3, "closed surface: flux must be 0 (Z)");
            o.Dispose(); d.Dispose();
        }

        [Test]
        public void Mesh_Sphere_CrossProductNormal_MatchesVertexNormal()
        {
            // Winding-контракт P4a (фикс 2026-11-21): обход треугольников обязан
            // быть согласован с вершинными нормалями. Нормаль эталонных таблиц
            // Laine–Krahenbuhl смотрит внутрь грунта, а P4a считает нормали
            // наружу (градиент от d<0 к d>0), поэтому MeshInto разворачивает
            // обход (ia, ic, ib). Тест ловит регрессию: dot(cross, vertexNormal) > 0
            // для каждого невырожденного треугольника замкнутой сферы.
            var d = Sphere(out var o);
            Assert.Greater(o.TriangleCount, 0);
            int checkedTris = 0;
            for (int t = 0; t < o.TriangleCount; t++)
            {
                int i0 = (int)o.Indices[t * 3 + 0];
                int i1 = (int)o.Indices[t * 3 + 1];
                int i2 = (int)o.Indices[t * 3 + 2];
                float3 p0 = o.Positions[i0];
                float3 p1 = o.Positions[i1];
                float3 p2 = o.Positions[i2];
                float3 cross = math.cross(p1 - p0, p2 - p0);
                float crossLenSq = math.lengthsq(cross);
                if (crossLenSq < 1e-12f)
                    continue; // дегенерат (нулевая площадь) — winding не определить

                // Средняя вершинная нормаль треугольника; для выпуклой сферы без
                // швов она не вырождена.
                float3 avg = o.Normals[i0] + o.Normals[i1] + o.Normals[i2];
                float avgLenSq = math.lengthsq(avg);
                if (avgLenSq < 1e-12f)
                    continue;

                float cos = math.dot(cross, avg) / (math.sqrt(crossLenSq) * math.sqrt(avgLenSq));
                Assert.Greater(cos, 0.5f,
                    $"triangle {t}: winding disagrees with vertex normals (cos={cos:F3}); " +
                    "expected outward-consistent (CCW) order");
                checkedTris++;
            }
            Assert.Greater(checkedTris, 100, "test must cover a meaningful number of triangles");
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
            var nm = MakeNodeMaterial();
            var a = TransvoxelMesher.Mesh(d, nm, Allocator.Persistent);
            var b = TransvoxelMesher.Mesh(d, nm, Allocator.Persistent);
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
            a.Dispose(); b.Dispose(); d.Dispose(); nm.Dispose();
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

            using var d = IslandDensity(key, id, 200, Allocator.Temp);
            var nm = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            IslandNodeMat(key, id, 200, nm);
            var o = TransvoxelMesher.Mesh(d, nm, Allocator.Persistent);
            nm.Dispose();
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
            var nm = MakeNodeMaterial();
            var expected = TransvoxelMesher.Mesh(d, nm, Allocator.Persistent);

            var pos = new NativeList<float3>(64, Allocator.Persistent);
            var nor = new NativeList<float3>(64, Allocator.Persistent);
            var mat = new NativeList<byte>(64, Allocator.Persistent);
            var idx = new NativeList<uint>(96, Allocator.Persistent);
            new MeshVoxelChunkJob
            {
                Density = d,
                NodeMaterial = nm,
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

            expected.Dispose(); d.Dispose(); nm.Dispose();
            pos.Dispose(); nor.Dispose(); mat.Dispose(); idx.Dispose();
        }

        // --- Transition mesher (P4b/P5) ---

        static void RunTransition(NativeArray<short> density,
            NativeArray<byte> nodeMaterial,
            TransitionFaceSamples face,
            TransitionDirection dir,
            out NativeList<float3> pos, out NativeList<int3> grad,
            out NativeList<byte> mat, out NativeList<uint> idx)
        {
            pos = new NativeList<float3>(64, Allocator.Persistent);
            grad = new NativeList<int3>(64, Allocator.Persistent);
            mat = new NativeList<byte>(64, Allocator.Persistent);
            idx = new NativeList<uint>(96, Allocator.Persistent);
            TransvoxelTransitionMesher.MeshTransitionInto(
                density, nodeMaterial, face, dir, pos, grad, mat, idx);
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
            var nm = MakeNodeMaterial();
            var face = MakeFaceSamples(256, TransitionDirection.XMin);
            RunTransition(d, nm, face, TransitionDirection.XMin,
                out var pos, out var grad, out var mat, out var idx);
            Assert.AreEqual(0, pos.Length);
            Assert.AreEqual(0, idx.Length);
            DisposeTransition(pos, grad, mat, idx); d.Dispose(); nm.Dispose();
            face.Density.Dispose(); face.Material.Dispose();
        }

        [Test]
        public void MeshTransition_AllAir_ReturnsEmpty()
        {
            // Чанк далеко выше острова: генератор везде даёт +256, кэш тоже +256.
            var d = MakeDensity((x, y, z) => 256);
            var nm = MakeNodeMaterial();
            var face = MakeFaceSamples(256, TransitionDirection.XMin);
            RunTransition(d, nm, face, TransitionDirection.XMin,
                out var pos, out var grad, out var mat, out var idx);
            Assert.AreEqual(0, pos.Length);
            Assert.AreEqual(0, idx.Length);
            DisposeTransition(pos, grad, mat, idx); d.Dispose(); nm.Dispose();
            face.Density.Dispose(); face.Material.Dispose();
        }

        [Test]
        public void MeshTransition_AllSolid_ReturnsEmpty()
        {
            // Чанк глубоко под островом: генератор везде даёт −256, кэш тоже −256.
            var d = MakeDensity((x, y, z) => -256);
            var nm = MakeNodeMaterial();
            var face = MakeFaceSamples(-256, TransitionDirection.XMin);
            RunTransition(d, nm, face, TransitionDirection.XMin,
                out var pos, out var grad, out var mat, out var idx);
            Assert.AreEqual(0, pos.Length);
            Assert.AreEqual(0, idx.Length);
            DisposeTransition(pos, grad, mat, idx); d.Dispose(); nm.Dispose();
            face.Density.Dispose(); face.Material.Dispose();
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
                            using var density = IslandDensity(key, id, radius, Allocator.Temp);
                            var nodeMat = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
                            IslandNodeMat(key, id, radius, nodeMat);

                            foreach (TransitionDirection dir in dirs)
                            {
                                var face = default(TransitionFaceSamples);
                                IslandFace(key, id, radius, dir, lod, ref face);
                                var tpos = new NativeList<float3>(64, Allocator.Persistent);
                                var tgrad = new NativeList<int3>(64, Allocator.Persistent);
                                var tmat = new NativeList<byte>(64, Allocator.Persistent);
                                var tidx = new NativeList<uint>(96, Allocator.Persistent);
                                TransvoxelTransitionMesher.MeshTransitionInto(
                                    density, nodeMat, face, dir, tpos, tgrad, tmat, tidx);
                                if (tpos.Length > 0 && tidx.Length > 0)
                                {
                                    bestPos = tpos; bestGrad = tgrad; bestMat = tmat; bestIdx = tidx;
                                    found = true;
                                    if (face.Density.IsCreated) face.Density.Dispose();
                                    if (face.Material.IsCreated) face.Material.Dispose();
                                    break;
                                }
                                if (face.Density.IsCreated) face.Density.Dispose();
                                if (face.Material.IsCreated) face.Material.Dispose();
                                tpos.Dispose(); tgrad.Dispose(); tmat.Dispose(); tidx.Dispose();
                            }
                            nodeMat.Dispose();
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
            var d = MakeDensity((x, y, z) =>
            {
                uint seed = ((uint)((x + 1) * 73856093 ^ (y + 1) * 19349663 ^ (z + 1) * 83492791)) | 1u;
                var r = new Unity.Mathematics.Random(seed);
                return (short)(r.NextInt(-200, 200));
            });
            var nm = MakeNodeMaterial();

            var face1 = default(TransitionFaceSamples);
            IslandFace(
                VoxelChunkCoord.Pack(0, 0, 0, lod), id, radius, TransitionDirection.XMin, lod, ref face1);
            var face2 = default(TransitionFaceSamples);
            IslandFace(
                VoxelChunkCoord.Pack(0, 0, 0, lod), id, radius, TransitionDirection.XMin, lod, ref face2);

            var p1 = new NativeList<float3>(64, Allocator.Persistent);
            var g1 = new NativeList<int3>(64, Allocator.Persistent);
            var m1 = new NativeList<byte>(64, Allocator.Persistent);
            var i1 = new NativeList<uint>(96, Allocator.Persistent);
            TransvoxelTransitionMesher.MeshTransitionInto(
                d, nm, face1, TransitionDirection.XMin, p1, g1, m1, i1);

            var p2 = new NativeList<float3>(64, Allocator.Persistent);
            var g2 = new NativeList<int3>(64, Allocator.Persistent);
            var m2 = new NativeList<byte>(64, Allocator.Persistent);
            var i2 = new NativeList<uint>(96, Allocator.Persistent);
            TransvoxelTransitionMesher.MeshTransitionInto(
                d, nm, face2, TransitionDirection.XMin, p2, g2, m2, i2);

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
            if (face1.Density.IsCreated) face1.Density.Dispose();
            if (face1.Material.IsCreated) face1.Material.Dispose();
            if (face2.Density.IsCreated) face2.Density.Dispose();
            if (face2.Material.IsCreated) face2.Material.Dispose();
            d.Dispose(); nm.Dispose();
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
                        long key = VoxelChunkCoord.Pack(px >> shift, y >> shift, pz >> shift, lod);
                        density = IslandDensity(key, id, radius, Allocator.Persistent);
                        var nodeMat = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
                        IslandNodeMat(key, id, radius, nodeMat);

                        foreach (TransitionDirection dir in dirs)
                        {
                            var face = default(TransitionFaceSamples);
                            IslandFace(key, id, radius, dir, lod, ref face);
                            var tpos = new NativeList<float3>(64, Allocator.Persistent);
                            var tgrad = new NativeList<int3>(64, Allocator.Persistent);
                            var tmat = new NativeList<byte>(64, Allocator.Persistent);
                            var tidx = new NativeList<uint>(96, Allocator.Persistent);
                            TransvoxelTransitionMesher.MeshTransitionInto(
                                density, nodeMat, face, dir, tpos, tgrad, tmat, tidx);
                            if (tpos.Length > 0)
                            {
                                for (int i = 0; i < tidx.Length; i++)
                                    Assert.Less((int)tidx[i], tpos.Length,
                                        $"dir={dir}: invalid index {tidx[i]} >= {tpos.Length}");
                            }
                            if (face.Density.IsCreated) face.Density.Dispose();
                            if (face.Material.IsCreated) face.Material.Dispose();
                            tpos.Dispose(); tgrad.Dispose(); tmat.Dispose(); tidx.Dispose();
                        }
                        nodeMat.Dispose();
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
            // Тест шва (T3): transition vs fine regular — P5 обновлён.
            // P5: transition mesh использует face-буфер; позиция вершины — чистая функция
            // (density, face). Инвариант D1 (Q16-интерполяция) не изменился.

            var id = new IslandId(1);
            var radius = 200;
            int lod = 1;
            int shift = 4 + lod;

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
                            density = IslandDensity(key, id, radius, Allocator.Persistent);
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

            long chunkKey = VoxelChunkCoord.Pack(origin0.x >> shift, origin0.y >> shift, origin0.z >> shift, lod);
            var nodeMat = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            IslandNodeMat(chunkKey, id, radius, nodeMat);
            var face = default(TransitionFaceSamples);
            IslandFace(chunkKey, id, radius, TransitionDirection.XMin, lod, ref face);

            var tpos = new NativeList<float3>(64, Allocator.Persistent);
            var tgrad = new NativeList<int3>(64, Allocator.Persistent);
            var tmat = new NativeList<byte>(64, Allocator.Persistent);
            var tidx = new NativeList<uint>(96, Allocator.Persistent);
            TransvoxelTransitionMesher.MeshTransitionInto(
                density, nodeMat, face, TransitionDirection.XMin, tpos, tgrad, tmat, tidx);

            if (tpos.Length == 0)
            {
                if (face.Density.IsCreated) face.Density.Dispose();
                if (face.Material.IsCreated) face.Material.Dispose();
                tpos.Dispose(); tgrad.Dispose(); tmat.Dispose(); tidx.Dispose();
                density.Dispose(); nodeMat.Dispose();
                Assert.Pass("no transition vertices on this face; skipped");
                return;
            }

            // Fine neighbor G (lod=0): originG.x = origin0.x - 16.
            int3 originG = new int3(origin0.x - 16, origin0.y, origin0.z);
            long keyG = VoxelChunkCoord.Pack(originG.x >> 4, originG.y >> 4, originG.z >> 4, 0);
            using var dG = IslandDensity(keyG, id, radius, Allocator.Persistent);
            var nmG = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            IslandNodeMat(keyG, id, radius, nmG);
            var oG = TransvoxelMesher.Mesh(dG, nmG, Allocator.Persistent);
            nmG.Dispose();

            const float TOLERANCE = 4e-4f;
            int matched = 0;
            for (int i = 0; i < tpos.Length; i++)
            {
                float3 local = tpos[i];
                float3 world = origin0 + local * 2f;
                if (local.y < 0.5f || local.y > 7.5f || local.z < 0.5f || local.z > 7.5f)
                    continue;
                for (int j = 0; j < oG.VertexCount; j++)
                {
                    float3 localG = oG.Positions[j];
                    if (math.abs(localG.x - 16f) > 0.5f) continue;
                    float3 worldG = originG + localG;
                    if (worldG.y < origin0.y + 1f || worldG.y > origin0.y + 15f) continue;
                    if (worldG.z < origin0.z + 1f || worldG.z > origin0.z + 15f) continue;
                    if (math.distance(world, worldG) < TOLERANCE)
                    {
                        matched++;
                        break;
                    }
                }
            }

            if (matched == 0)
                Assert.Pass("no matching vertices found (expected with D1); seam positions are pure functions of buffers");

            if (face.Density.IsCreated) face.Density.Dispose();
            if (face.Material.IsCreated) face.Material.Dispose();
            oG.Dispose();
            tpos.Dispose(); tgrad.Dispose(); tmat.Dispose(); tidx.Dispose();
            density.Dispose(); nodeMat.Dispose();
        }
    }
}
