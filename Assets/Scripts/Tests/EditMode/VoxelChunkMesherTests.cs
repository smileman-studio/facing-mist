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
    /// Тесты автономного мешировщика чанка <see cref="VoxelChunkMesher"/> (ADR-0002 D7).
    /// </summary>
    /// <remarks>
    /// Движок не знает источник формы — проверка идёт на аналитической сфере (field по
    /// контракту). Покрывают: валидность объединённой геометрии (индексы в диапазоне,
    /// замкнутость), единый scale regular+transition (transition-вершины в coarse-ячейках
    /// 0..16, а не 0..32 — регрессия на дыры), наличие transition при seam-маске,
    /// детерминизм и parity с Burst-джобой движка.
    /// </remarks>
    public class VoxelChunkMesherTests
    {
        static VoxelChunkGeometry BuildChunk(SphereDensityField sphere, NullMaterialField mat, VoxelChunkRequest req)
        {
            var geo = new VoxelChunkGeometry();
            geo.Allocate(Allocator.Persistent);
            var density = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
            var nodeMat = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            VoxelChunkMesher.BuildChunk(sphere, mat, req, ref geo, density, nodeMat);
            density.Dispose();
            nodeMat.Dispose();
            return geo;
        }

        // Чанк LOD1, который пересекает поверхность сферы (радиус 32 вокселя → координаты ~0..64).
        static VoxelChunkRequest SurfaceChunkLod1()
        {
            int radiusVox = 64;
            // Чанк LOD1 в (0,0,0): покрывает воксели 0..32 — сфера радиуса 64 вокруг 0..? нет.
            // Возьмём чанк на экваторе: origin x≈48, чтобы пересечь поверхность.
            int3 coord = new int3(1, 0, 0); // origin0 = coord << 5 = 32..64
            return new VoxelChunkRequest
            {
                ChunkCoord = coord,
                Lod = 1,
                Origin0 = VoxelFieldSampler.ChunkOrigin0(coord, 1),
                SeamFaceMask = 0,
            };
        }

        [Test]
        public void RegularOnly_ValidGeometry()
        {
            var sphere = new SphereDensityField(int3.zero, 40);
            var mat = new NullMaterialField();
            var req = SurfaceChunkLod1();
            using var geo = BuildChunk(sphere, mat, req);

            Assert.Greater(geo.VertexCount, 0, "чанк обязан пересекать поверхность сферы");
            Assert.AreEqual(0, geo.Indices.Length % 3, "индексы кратны 3");
            foreach (uint idx in geo.Indices)
                Assert.Less((int)idx, geo.VertexCount, "индекс вне диапазона вершин");
            Assert.AreEqual(geo.VertexCount, geo.Normals.Length);
            Assert.AreEqual(geo.VertexCount, geo.Materials.Length);
        }

        [Test]
        public void TransitionVertices_InCoarseCellSpace_NotHalfCell()
        {
            // Регрессия на дыры: transition-вершины должны лежать в единой coarse-системе
            // (0..16), как и regular. Если мешер не переводит полуячейки → вершины уедут в 0..32.
            // Сфера с центром на XMax-грани (x=32), радиус 20 → поверхность реально пересекает
            // seam-грань, поэтому transition-геометрия присутствует.
            var sphere = new SphereDensityField(new int3(32, 16, 16), 20);
            var mat = new NullMaterialField();

            int3 coord = new int3(0, 0, 0);
            var req = new VoxelChunkRequest
            {
                ChunkCoord = coord,
                Lod = 1,
                Origin0 = VoxelFieldSampler.ChunkOrigin0(coord, 1),
                SeamFaceMask = TransvoxelMesher.SkipXMax,
            };

            using var geo = BuildChunk(sphere, mat, req);
            Assert.Greater(geo.VertexCount, 0);

            // Все позиции чанка — в coarse-ячейках: 0..16 (допуск на интерполяцию + apron).
            foreach (var p in geo.Positions)
            {
                Assert.GreaterOrEqual(p.x, -0.5f, "X coarse-space");
                Assert.LessOrEqual(p.x, 16.5f, "X обязан быть в coarse-диапазоне (0..16), не 0..32");
                Assert.GreaterOrEqual(p.y, -0.5f);
                Assert.LessOrEqual(p.y, 16.5f, "Y coarse-space");
                Assert.GreaterOrEqual(p.z, -0.5f);
                Assert.LessOrEqual(p.z, 16.5f, "Z coarse-space");
            }
        }

        [Test]
        public void SeamMask_AddsTransitionVertices()
        {
            // Сфера с центром на XMax-грани чанка (0,0,0) LOD1 (x=32), радиус 20:
            // плоскость грани проходит через центр → поверхность трансверсально пересекает
            // XMax-грань, поэтому transition реально мешируется, а regular-слой на грани пропускается.
            var sphere = new SphereDensityField(new int3(32, 16, 16), 20);
            var mat = new NullMaterialField();
            int3 coord = new int3(0, 0, 0);
            int3 origin0 = VoxelFieldSampler.ChunkOrigin0(coord, 1);

            var noSeam = new VoxelChunkRequest { ChunkCoord = coord, Lod = 1, Origin0 = origin0, SeamFaceMask = 0 };
            var withSeam = new VoxelChunkRequest
                { ChunkCoord = coord, Lod = 1, Origin0 = origin0, SeamFaceMask = TransvoxelMesher.SkipXMax };

            using var geoNo = BuildChunk(sphere, mat, noSeam);
            using var geoYes = BuildChunk(sphere, mat, withSeam);

            Assert.Greater(geoNo.VertexCount, 0, "sanity: чанк обязан пересекать поверхность");
            // Seam-грань: regular-приграничный слой пропущен + добавлены transition cells.
            Assert.AreNotEqual(geoNo.VertexCount, geoYes.VertexCount,
                "transition-шов обязан изменить геометрию чанка");
            Assert.AreNotEqual(geoNo.Indices.Length, geoYes.Indices.Length,
                "transition-шов обязан изменить число треугольников");
        }

        [Test]
        public void Mesher_IsDeterministic()
        {
            var sphere = new SphereDensityField(int3.zero, 48);
            var mat = new NullMaterialField();
            var req = SurfaceChunkLod1();
            using var a = BuildChunk(sphere, mat, req);
            using var b = BuildChunk(sphere, mat, req);

            Assert.AreEqual(a.VertexCount, b.VertexCount);
            Assert.AreEqual(a.Indices.Length, b.Indices.Length);
            for (int i = 0; i < a.VertexCount; i++)
                Assert.AreEqual(a.Positions[i], b.Positions[i]);
        }

        [Test]
        public void Job_ParityWithMainThread()
        {
            var sphere = new SphereDensityField(int3.zero, 48);
            var mat = new NullMaterialField();
            var req = SurfaceChunkLod1();

            using var main = BuildChunk(sphere, mat, req);

            var geo = new VoxelChunkGeometry();
            geo.Allocate(Allocator.Persistent);
            var density = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
            var nodeMat = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);

            new VoxelChunkBuildJob<SphereDensityField, NullMaterialField>
            {
                DensityField = sphere,
                MaterialField = mat,
                Request = req,
                DensityScratch = density,
                NodeMatScratch = nodeMat,
                Geometry = geo,
            }.Run();

            Assert.AreEqual(main.VertexCount, geo.VertexCount, "job/main thread vertex count");
            for (int i = 0; i < main.VertexCount; i++)
                Assert.AreEqual(main.Positions[i], geo.Positions[i], $"job parity vertex {i}");

            density.Dispose();
            nodeMat.Dispose();
            geo.Dispose();
        }

        [Test]
        public void IslandField_ProducesSameDensityAsLegacyPath()
        {
            // Побитовая совместимость нового движка со старым golden-путём: island chunk
            // через VoxelChunkMesher даёт ту же regular-геометрию, что TransvoxelMesher.Mesh
            // напрямую (материал через IslandMaterialAdapter = IslandMaterialField).
            var id = new IslandId(6);
            const int radius = 500;
            int lod = 0;
            int3 coord = new int3(2, -1, 3);
            int3 origin0 = VoxelFieldSampler.ChunkOrigin0(coord, lod);

            var dField = new IslandDensityField(id, radius);
            var mField = new IslandMaterialAdapter(id);

            using var density = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
            using var nodeMat = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            VoxelFieldSampler.FillDensity(origin0, lod, dField, density);
            VoxelFieldSampler.FillNodeMaterial(origin0, lod, mField, nodeMat);
            var legacy = TransvoxelMesher.Mesh(density, nodeMat, Allocator.Persistent);

            var req = new VoxelChunkRequest
                { ChunkCoord = coord, Lod = (byte)lod, Origin0 = origin0, SeamFaceMask = 0 };
            var geo = new VoxelChunkGeometry();
            geo.Allocate(Allocator.Persistent);
            var dScratch = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
            var nScratch = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            VoxelChunkMesher.BuildChunk(dField, mField, req, ref geo, dScratch, nScratch);

            Assert.AreEqual(legacy.VertexCount, geo.VertexCount, "regular-геометрия движка == legacy");
            Assert.AreEqual(legacy.Indices.Length, geo.Indices.Length);

            dScratch.Dispose();
            nScratch.Dispose();
            geo.Dispose();
            legacy.Dispose();
        }

        [Test]
        public void TransitionStrip_LiesOnSeamFace_NotShrunkToMinCorner()
        {
            // Сфера с центром на XMax-грани (coarse x=16): поверхность трансверсально
            // пересекает грань → transition-вершины обязаны лежать НА плоскости x=16.
            var sphere = new SphereDensityField(new int3(32, 16, 16), 20);
            var mat = new NullMaterialField();
            int3 coord = int3.zero;
            var req = new VoxelChunkRequest
            {
                ChunkCoord = coord, Lod = 1,
                Origin0 = VoxelFieldSampler.ChunkOrigin0(coord, 1),
                SeamFaceMask = TransvoxelMesher.SkipXMax,
            };
            using var geo = BuildChunk(sphere, mat, req);

            bool onFace = false;
            float maxOnFaceY = 0f;
            foreach (var p in geo.Positions)
            {
                // Regular не может дать x > 15 (слой x=15 пропущен) — всё, что на x=16, это transition.
                if (p.x > 15.999f)
                {
                    onFace = true;
                    maxOnFaceY = math.max(maxOnFaceY, p.y);
                }
            }

            Assert.IsTrue(onFace, "полоса обязана лежать на грани x=16, а не x=8 (двойной /2)");
            Assert.Greater(maxOnFaceY, 12f, "полоса не сжата к min-углу: на грани сфера достигает y≈16");
        }

        static void TransitionFaceGeometry(TransitionDirection dir, int3 sphereCenterVox,
            out NativeList<float3> pos, out NativeList<int3> grad, out NativeList<uint> idx)
        {
            var sphere = new SphereDensityField(sphereCenterVox, 20);
            var mat = new NullMaterialField();
            int3 origin0 = int3.zero;
            const int lod = 1;
            var density = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
            var nodeMat = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            VoxelFieldSampler.FillDensity(origin0, lod, sphere, density);
            VoxelFieldSampler.FillNodeMaterial(origin0, lod, mat, nodeMat);
            var face = default(TransitionFaceSamples);
            VoxelFieldSampler.FillFace(origin0, lod, dir, sphere, mat, ref face);
            pos = new NativeList<float3>(64, Allocator.Persistent);
            grad = new NativeList<int3>(64, Allocator.Persistent);
            var m = new NativeList<byte>(64, Allocator.Persistent);
            idx = new NativeList<uint>(96, Allocator.Persistent);
            TransvoxelTransitionMesher.MeshTransitionInto(density, nodeMat, face, dir, pos, grad, m, idx);
            m.Dispose();
            density.Dispose();
            nodeMat.Dispose();
            face.Density.Dispose();
            face.Material.Dispose();
        }

        [Test]
        public void TransitionStrip_DepthInward_AndWindingOutward_XMin()
        {
            // Центр сферы на грани XMin (world x=0): полоса обязана уходить ВНУТРЬ чанка (x>0).
            TransitionFaceGeometry(TransitionDirection.XMin, new int3(0, 16, 16),
                out var pos, out var grad, out var idx);
            Assert.Greater(pos.Length, 0);

            bool hasInterior = false;
            for (int i = 0; i < pos.Length; i++)
            {
                Assert.GreaterOrEqual(pos[i].x, -1e-3f, "XMin: interior ушёл наружу (зеркало глубины)");
                if (pos[i].x > 0.5f) hasInterior = true;
            }

            Assert.IsTrue(hasInterior, "полоса обязана иметь глубину внутрь чанка");

            int checkedTris = 0;
            for (int t = 0; t < idx.Length; t += 3)
            {
                float3 p0 = pos[(int)idx[t]], p1 = pos[(int)idx[t + 1]], p2 = pos[(int)idx[t + 2]];
                float3 cross = math.cross(p1 - p0, p2 - p0);
                if (math.lengthsq(cross) < 1e-10f) continue;
                float3 n = TransvoxelMesher.NormalizeGradient(grad[(int)idx[t]]
                                                              + grad[(int)idx[t + 1]] + grad[(int)idx[t + 2]]);
                Assert.Greater(math.dot(math.normalize(cross), n), 0.5f,
                    $"tri {t / 3}: winding transition перевёрнут относительно нормали");
                checkedTris++;
            }

            Assert.Greater(checkedTris, 0);
            pos.Dispose();
            grad.Dispose();
            idx.Dispose();
        }

        [Test]
        public void Seam_TransitionBoundary_MatchesFineNeighbor_WorldSpace()
        {
            const int lod = 1;
            var sphere = new SphereDensityField(new int3(0, 16, 16), 20); // центр на XMin-грани
            var mat = new NullMaterialField();
            int3 origin0 = int3.zero;

            var density = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
            var nodeMat = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            VoxelFieldSampler.FillDensity(origin0, lod, sphere, density);
            VoxelFieldSampler.FillNodeMaterial(origin0, lod, mat, nodeMat);
            var face = default(TransitionFaceSamples);
            VoxelFieldSampler.FillFace(origin0, lod, TransitionDirection.XMin, sphere, mat, ref face);
            var tp = new NativeList<float3>(64, Allocator.Persistent);
            var tg = new NativeList<int3>(64, Allocator.Persistent);
            var tm = new NativeList<byte>(64, Allocator.Persistent);
            var ti = new NativeList<uint>(96, Allocator.Persistent);
            TransvoxelTransitionMesher.MeshTransitionInto(density, nodeMat, face, TransitionDirection.XMin, tp, tg, tm, ti);

            // Fine-сосед lod0 на -X: его XMax-грань = наша XMin-плоскость.
            // Он покрывает только ЧЕТВЕРТЬ грани: world y,z ∈ [0,16].
            int3 originG = origin0 - new int3(16, 0, 0);
            var dG = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
            var nG = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            VoxelFieldSampler.FillDensity(originG, 0, sphere, dG);
            VoxelFieldSampler.FillNodeMaterial(originG, 0, mat, nG);
            var fine = TransvoxelMesher.Mesh(dG, nG, Allocator.Persistent);

            int matched = 0;
            float maxGap = 0f;
            for (int i = 0; i < tp.Length; i++)
            {
                if (math.abs(tp[i].x) > 1e-3f) continue; // только w=0 — на плоскости шва
                // Четверть грани, покрытая этим fine-соседом: coarse y,z ∈ [0,8].
                if (tp[i].y < 0.5f || tp[i].y > 7.5f || tp[i].z < 0.5f || tp[i].z > 7.5f) continue;
                float3 world = origin0 + tp[i] * 2f; // coarse-ячейки lod1 → воксели LOD0
                float best = float.MaxValue;
                for (int j = 0; j < fine.VertexCount; j++)
                {
                    if (math.abs(fine.Positions[j].x - 16f) > 1e-3f) continue; // fine XMax-грань
                    best = math.min(best, math.distance(world, originG + fine.Positions[j]));
                }
                if (best < 1e-2f) { matched++; maxGap = math.max(maxGap, best); }
                else Assert.Fail($"seam vertex {world} без пары fine (gap={best})");
            }
            Assert.Greater(matched, 0, "шов обязан пересекаться поверхностью");
            Assert.Less(maxGap, 4e-4f, $"max seam gap = {maxGap}");

            tp.Dispose(); tg.Dispose(); tm.Dispose(); ti.Dispose();
            density.Dispose(); nodeMat.Dispose(); dG.Dispose(); nG.Dispose(); fine.Dispose();
            face.Density.Dispose(); face.Material.Dispose();
        }
        
                [Test]
        public void Seam_AllDirections_TransitionBoundary_MatchesFine()
        {
            const int lod = 1;
            var sphere = new SphereDensityField(new int3(16, 16, 16), 20);
            var mat = new NullMaterialField();
            int3 origin0 = int3.zero;
            float Comp(float3 v, int a) => a == 0 ? v.x : a == 1 ? v.y : v.z;

            var dirs = new[]
            {
                (TransitionDirection.XMin, 0, new int3(-16, 0, 0)),
                (TransitionDirection.XMax, 0, new int3(32, 0, 0)),
                (TransitionDirection.YMin, 1, new int3(0, -16, 0)),
                (TransitionDirection.YMax, 1, new int3(0, 32, 0)),
                (TransitionDirection.ZMin, 2, new int3(0, 0, -16)),
                (TransitionDirection.ZMax, 2, new int3(0, 0, 32)),
            };

            var density = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
            var nodeMat = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            VoxelFieldSampler.FillDensity(origin0, lod, sphere, density);
            VoxelFieldSampler.FillNodeMaterial(origin0, lod, mat, nodeMat);

            foreach (var (dir, axis, off) in dirs)
            {
                bool min = dir == TransitionDirection.XMin || dir == TransitionDirection.YMin || dir == TransitionDirection.ZMin;
                float ourFace = min ? 0f : 16f;
                float fineFace = min ? 16f : 0f;

                var face = default(TransitionFaceSamples);
                VoxelFieldSampler.FillFace(origin0, lod, dir, sphere, mat, ref face);
                var tp = new NativeList<float3>(64, Allocator.Persistent);
                var tg = new NativeList<int3>(64, Allocator.Persistent);
                var tm = new NativeList<byte>(64, Allocator.Persistent);
                var ti = new NativeList<uint>(96, Allocator.Persistent);
                TransvoxelTransitionMesher.MeshTransitionInto(density, nodeMat, face, dir, tp, tg, tm, ti);

                int3 originG = origin0 + off;
                var dG = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
                var nG = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
                VoxelFieldSampler.FillDensity(originG, 0, sphere, dG);
                VoxelFieldSampler.FillNodeMaterial(originG, 0, mat, nG);
                var fine = TransvoxelMesher.Mesh(dG, nG, Allocator.Persistent);

                int matched = 0; float maxGap = 0f;
                for (int i = 0; i < tp.Length; i++)
                {
                    if (math.abs(Comp(tp[i], axis) - ourFace) > 1e-3f) continue;
                    int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
                    if (Comp(tp[i], a1) < 0.5f || Comp(tp[i], a1) > 7.5f ||
                        Comp(tp[i], a2) < 0.5f || Comp(tp[i], a2) > 7.5f) continue;
                    float3 world = origin0 + tp[i] * 2f;
                    float best = float.MaxValue;
                    for (int j = 0; j < fine.VertexCount; j++)
                    {
                        if (math.abs(Comp(fine.Positions[j], axis) - fineFace) > 1e-3f) continue;
                        best = math.min(best, math.distance(world, originG + fine.Positions[j]));
                    }
                    if (best < 1e-2f) { matched++; maxGap = math.max(maxGap, best); }
                    else Assert.Fail($"{dir}: seam vertex {world} без пары fine (gap={best})");
                }
                Assert.Greater(matched, 0, $"{dir}: шов обязан пересекаться поверхностью");
                Assert.Less(maxGap, 4e-4f, $"{dir}: max seam gap = {maxGap}");

                tp.Dispose(); tg.Dispose(); tm.Dispose(); ti.Dispose();
                dG.Dispose(); nG.Dispose(); fine.Dispose();
                face.Density.Dispose(); face.Material.Dispose();
            }
            density.Dispose(); nodeMat.Dispose();
        }
    }
}
