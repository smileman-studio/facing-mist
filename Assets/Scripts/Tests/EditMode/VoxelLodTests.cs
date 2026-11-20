using NUnit.Framework;
using SmilemanStudio.Core.Voxels;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты уровней детализации воксельной сетки <see cref="VoxelLod"/> (ADR-0002, D3).
    /// </summary>
    /// <remarks>
    /// Значения проверяются явно (эквивалент golden-значений): методы целочисленные,
    /// поэтому результат обязан совпадать на любой платформе.
    /// </remarks>
    public class VoxelLodTests
    {
        [Test]
        public void LodMax_AtRadiusMin_Is6()
        {
            // R = 200: ceil(200/4) = 50, ceil(log2(50)) = 6 (2^6 = 64 >= 50).
            Assert.AreEqual(6, VoxelLod.LodMax(VoxelConstants.RadiusMin),
                "LOD для R = 200 должен быть 6 (чанк 512 м).");
        }

        [Test]
        public void LodMax_AtRadiusMax_Is9()
        {
            // R = 2000: ceil(2000/4) = 500, ceil(log2(500)) = 9 (2^9 = 512 >= 500).
            Assert.AreEqual(9, VoxelLod.LodMax(VoxelConstants.RadiusMax),
                "LOD для R = 2000 должен быть 9 (чанк 4096 м).");
        }

        [Test]
        public void LodMax_AtReferenceRadius_Is7()
        {
            // R = 500: ceil(500/4) = 125, ceil(log2(125)) = 7 (2^7 = 128 >= 125).
            Assert.AreEqual(7, VoxelLod.LodMax(500),
                "LOD для R = 500 должен быть 7 (чанк 1024 м).");
        }

        [Test]
        public void LodMax_IsBoundedByLodHardMax()
        {
            // R = 32768: ceil(32768/4) = 8192 = 2^13 → 13, но ограничено LodHardMax = 10.
            Assert.AreEqual(VoxelConstants.LodHardMax, VoxelLod.LodMax(32768),
                "LOD не должен превышать LodHardMax.");
        }

        [Test]
        public void LodMax_ChunkWorldSize_CoversDiameter()
        {
            int[] radii = { 200, 500, 1000, 2000 };

            foreach (int r in radii)
            {
                int lod = VoxelLod.LodMax(r);
                int chunkSize = VoxelLod.ChunkWorldSize(lod);
                Assert.GreaterOrEqual(2 * chunkSize, 2 * r,
                    "Чанк уровня " + lod + " должен перекрывать диаметр острова R = " + r + ".");
            }
        }

        [Test]
        public void VoxelStepMilli_GrowsByFactorOf2()
        {
            for (int k = 0; k < VoxelConstants.LodHardMax; k++)
            {
                int step = VoxelLod.VoxelStepMilli(k);
                int nextStep = VoxelLod.VoxelStepMilli(k + 1);
                Assert.AreEqual(step * 2, nextStep,
                    "Шаг LOD " + (k + 1) + " должен быть в 2 раза больше шага LOD " + k + ".");
            }
        }

        [Test]
        public void VoxelStepMilli_AtLOD0_Is500mm()
        {
            Assert.AreEqual(500, VoxelLod.VoxelStepMilli(0),
                "Шаг LOD0 = 0,5 м = 500 мм.");
        }

        [Test]
        public void VoxelStepMilli_AtLOD3_Is4000mm()
        {
            Assert.AreEqual(4000, VoxelLod.VoxelStepMilli(3),
                "Шаг LOD3 = 0,5 * 8 = 4 м = 4000 мм.");
        }

        [Test]
        public void ChunkWorldSize_AtLOD0_Is8m()
        {
            Assert.AreEqual(8, VoxelLod.ChunkWorldSize(0),
                "Размер чанка LOD0 = 16 * 0,5 м = 8 м.");
        }

        [Test]
        public void ChunkWorldSize_AtLOD4_Is128m()
        {
            Assert.AreEqual(128, VoxelLod.ChunkWorldSize(4),
                "Размер чанка LOD4 = 16 * 8 м = 128 м.");
        }

        [Test]
        public void LodSwitchRadius_AtLOD0_Is32m()
        {
            Assert.AreEqual(32, VoxelLod.LodSwitchRadius(0),
                "Радиус переключения LOD0 = 4 * 8 м = 32 м.");
        }

        [Test]
        public void LodSwitchRadius_GrowsByFactorOf2()
        {
            for (int k = 0; k < VoxelConstants.LodHardMax; k++)
            {
                int radius = VoxelLod.LodSwitchRadius(k);
                int nextRadius = VoxelLod.LodSwitchRadius(k + 1);
                Assert.AreEqual(radius * 2, nextRadius,
                    "Радиус переключения LOD " + (k + 1) + " должен быть в 2 раза больше.");
            }
        }
    }
}
