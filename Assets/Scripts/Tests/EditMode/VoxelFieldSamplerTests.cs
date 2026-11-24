using NUnit.Framework;
using SmilemanStudio.Core;
using SmilemanStudio.Core.Voxels;
using Unity.Collections;
using Unity.Mathematics;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты generic-сэмплера движка <see cref="VoxelFieldSampler"/> (ADR-0002 D3/D7).
    /// </summary>
    /// <remarks>
    /// Покрывают: геометрию кэша (размер, интерьер/apron, origin из ключа, шаг 2^lod);
    /// согласованность границ соседей; generic-заполнение density/nodeMat/face против
    /// независимой выборки IslandSdf/IslandMaterialField (побитово). Движок отвязан от
    /// острова — поле приходит через контракт, поэтому проверка идёт через IslandDensityField.
    /// </remarks>
    public class VoxelFieldSamplerTests
    {
        [Test]
        public void CacheSize_Is19_Interior17()
        {
            Assert.AreEqual(19, VoxelFieldSampler.CacheSize);
            Assert.AreEqual(17, VoxelFieldSampler.InteriorSamples);
            Assert.AreEqual(19 * 19 * 19, VoxelFieldSampler.CacheVolume);
            Assert.AreEqual(17 * 17 * 17, VoxelFieldSampler.NodeMaterialVolume);
            Assert.AreEqual(35 * 35 * 3, VoxelFieldSampler.FaceSamplesVolume);
        }

        [Test]
        public void IsInterior_Apron_Partition()
        {
            for (int i = 0; i < 19; i++)
            {
                Assert.AreEqual(i >= 1 && i <= 17, VoxelFieldSampler.IsInterior(i), $"i={i} interior");
                Assert.AreEqual(i == 0 || i == 18, VoxelFieldSampler.IsApron(i), $"i={i} apron");
                Assert.AreNotEqual(VoxelFieldSampler.IsInterior(i), VoxelFieldSampler.IsApron(i));
            }
        }

        [Test]
        public void ChunkOrigin0_MatchesShift_ForAllLods()
        {
            for (int lod = 0; lod <= VoxelConstants.LodHardMax; lod++)
            {
                int3 coord = new int3(3, -2, 5);
                Assert.AreEqual(coord << (4 + lod), VoxelFieldSampler.ChunkOrigin0(coord, lod),
                    $"LOD{lod}: origin = coord << (ChunkSizeLog2+lod)");
            }
        }

        [Test]
        public void ChunkOrigin0_FromKey_MatchesCoordForm()
        {
            int[] lods = { 0, 1, 3, 6, 9 };
            foreach (int lod in lods)
            {
                int3 coord = new int3(-7, 4, 12);
                long key = VoxelChunkCoord.Pack(coord.x, coord.y, coord.z, lod);
                Assert.AreEqual(VoxelFieldSampler.ChunkOrigin0(coord, lod), VoxelFieldSampler.ChunkOrigin0(key));
            }
        }

        [Test]
        public void VoxelFromIndices_StepEqualsTwoPowLod()
        {
            for (int lod = 0; lod <= 3; lod++)
            {
                int3 origin = VoxelFieldSampler.ChunkOrigin0(int3.zero, lod);
                int a = VoxelFieldSampler.VoxelFromIndices(new int3(1, 1, 1), origin, lod).x;
                int b = VoxelFieldSampler.VoxelFromIndices(new int3(2, 1, 1), origin, lod).x;
                Assert.AreEqual(a + (1 << lod), b, $"LOD{lod}: шаг сэмпла = 2^lod");
            }
        }

        [Test]
        public void BoundarySample_SharedWithNeighbor()
        {
            int3 coord = new int3(6, -3, 9);
            int3 originC = VoxelFieldSampler.ChunkOrigin0(coord, 0);
            int3 originN = VoxelFieldSampler.ChunkOrigin0(coord + new int3(1, 0, 0), 0);
            Assert.AreEqual(
                VoxelFieldSampler.VoxelFromIndices(new int3(17, 9, 9), originC, 0),
                VoxelFieldSampler.VoxelFromIndices(new int3(1, 9, 9), originN, 0),
                "Граничный узел обязан совпадать у соседей.");
        }

        [Test]
        public void FillDensity_MatchesIslandSdfQuantize_Bitwise()
        {
            var id = new IslandId(4);
            const int radius = 500;
            int lod = 1;
            int3 origin = VoxelFieldSampler.ChunkOrigin0(new int3(3, -1, 2), lod);
            var field = new IslandDensityField(id, radius);

            using var density = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
            VoxelFieldSampler.FillDensity(origin, lod, field, density);

            for (int i = 0; i < VoxelFieldSampler.CacheSize; i++)
            for (int j = 0; j < VoxelFieldSampler.CacheSize; j += 5)
            for (int k = 0; k < VoxelFieldSampler.CacheSize; k += 7)
            {
                int3 voxel = VoxelFieldSampler.VoxelFromIndices(new int3(i, j, k), origin, lod);
                short expected = VoxelDensity.Quantize(IslandSdf.SdfVoxel(voxel, id, radius));
                Assert.AreEqual(expected, density[(i * 19 + j) * 19 + k],
                    $"Сэмпл ({i},{j},{k}) обязан побитово совпасть с IslandSdf.");
            }
        }

        [Test]
        public void FillDensity_IndexOrder_IsXyzMajor()
        {
            var field = new IslandDensityField(new IslandId(1), 200);
            int3 origin = VoxelFieldSampler.ChunkOrigin0(int3.zero, 0);
            using var density = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
            VoxelFieldSampler.FillDensity(origin, 0, field, density);

            int i = 5, j = 6, k = 7;
            int baseIndex = (i * 19 + j) * 19 + k;
            int3 v = origin + new int3(i, j, k) - new int3(1, 1, 1);
            Assert.AreEqual(
                field.SampleDensity(v + new int3(0, 0, 1)), density[baseIndex + 1], "Сосед по z: +1");
            Assert.AreEqual(
                field.SampleDensity(v + new int3(0, 1, 0)), density[baseIndex + 19], "Сосед по y: +19");
            Assert.AreEqual(
                field.SampleDensity(v + new int3(1, 0, 0)), density[baseIndex + 19 * 19], "Сосед по x: +361");
        }

        [Test]
        public void FillDensity_WrongBufferLength_Throws()
        {
            var field = new IslandDensityField(new IslandId(1), 200);
            using var wrong = new NativeArray<short>(10, Allocator.Persistent);
            Assert.Throws<System.ArgumentException>(() =>
                VoxelFieldSampler.FillDensityChecked(int3.zero, 0, field, wrong));
        }

        [Test]
        public void FillNodeMaterial_MatchesIslandMaterialField()
        {
            var id = new IslandId(9);
            int lod = 0;
            int3 origin = VoxelFieldSampler.ChunkOrigin0(new int3(2, 0, -4), lod);
            var field = new IslandMaterialAdapter(id);

            using var nm = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            VoxelFieldSampler.FillNodeMaterial(origin, lod, field, nm);

            for (int x = 0; x < 17; x += 4)
            for (int y = 0; y < 17; y += 5)
            for (int z = 0; z < 17; z += 6)
            {
                int3 voxel = origin + new int3(x << lod, y << lod, z << lod);
                byte expected = (byte)IslandMaterialField.At(voxel.x, voxel.y, voxel.z, id);
                Assert.AreEqual(expected, nm[(x * 17 + y) * 17 + z], $"Узел ({x},{y},{z})");
            }
        }

        [Test]
        public void FillFace_MatchesDensityField()
        {
            var id = new IslandId(3);
            const int radius = 500;
            int lod = 1;
            int3 origin = VoxelFieldSampler.ChunkOrigin0(new int3(1, 0, -2), lod);
            var dField = new IslandDensityField(id, radius);
            var mField = new IslandMaterialAdapter(id);

            var face = default(TransitionFaceSamples);
            VoxelFieldSampler.FillFace(origin, lod, TransitionDirection.XMax, dField, mField, ref face);

            Assert.AreEqual(VoxelFieldSampler.FaceSamplesVolume, face.Density.Length);
            int halfStep = 1 << (lod - 1);
            int3 minCorner = VoxelFieldSampler.FaceMinCorner(TransitionDirection.XMax);

            // Проверка одной точки: u=16,v=16,w=0.
            int3 local = TransvoxelTransitionMesher.FaceToLocalSpace(TransitionDirection.XMax, 16, 16, 0);
            int3 rel = local - minCorner;
            int3 bufSize = VoxelFieldSampler.FaceBufferSize(TransitionDirection.XMax);
            int idx = (rel.x * bufSize.y + rel.y) * bufSize.z + rel.z;
            int3 voxel = origin + local * halfStep;
            Assert.AreEqual(dField.SampleDensity(voxel), face.Density[idx], "Face density обязан совпасть.");

            face.Density.Dispose();
            face.Material.Dispose();
        }

        [Test]
        public void FillDensity_NegativeOrigin_Consistent()
        {
            var field = new IslandDensityField(new IslandId(2), 800);
            int lod = 2;
            int3 origin = VoxelFieldSampler.ChunkOrigin0(new int3(-5, -3, -9), lod);
            using var density = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
            VoxelFieldSampler.FillDensity(origin, lod, field, density);

            // Первый интерьерный сэмпл = origin.
            int3 firstVoxel = VoxelFieldSampler.VoxelFromIndices(new int3(1, 1, 1), origin, lod);
            Assert.AreEqual(origin, firstVoxel);
            Assert.AreEqual(field.SampleDensity(origin), density[(19 + 1) * 19 + 1]);
        }
    }
}
