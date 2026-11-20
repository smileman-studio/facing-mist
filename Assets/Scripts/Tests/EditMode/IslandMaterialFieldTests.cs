using NUnit.Framework;
using SmilemanStudio.Core;
using SmilemanStudio.Core.Voxels;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты процедурного поля материала <see cref="IslandMaterialField"/> (ADR-0002, D7).
    /// </summary>
    /// <remarks>
    /// Golden-значения вычислены Python-репликой того же алгоритма
    /// (tools/golden_sdf.py). Целочисленная математика даёт идентичный
    /// результат на всех платформах.
    /// </remarks>
    public class IslandMaterialFieldTests
    {
        [Test]
        public void At_Origin_IsGoldenValue()
        {
            // (0,0,0) id=1: argmax четырёх Fbm3D → Coal (2).
            Assert.AreEqual(VoxelMaterial.Coal, IslandMaterialField.At(0, 0, 0, new IslandId(1)),
                "Материал в начале координат id=1 — golden значение.");
        }

        [Test]
        public void At_NearOrigin_IsGoldenValue()
        {
            Assert.AreEqual(VoxelMaterial.Coal, IslandMaterialField.At(10, 0, 0, new IslandId(1)));
            Assert.AreEqual(VoxelMaterial.Coal, IslandMaterialField.At(0, 10, 0, new IslandId(1)));
            Assert.AreEqual(VoxelMaterial.Coal, IslandMaterialField.At(0, 0, 10, new IslandId(1)));
        }

        [Test]
        public void At_DifferentIds_IsGoldenValue()
        {
            Assert.AreEqual(VoxelMaterial.Dirt, IslandMaterialField.At(0, 0, 0, new IslandId(2)));
            Assert.AreEqual(VoxelMaterial.Coal, IslandMaterialField.At(0, 0, 0, new IslandId(7)));
        }

        [Test]
        public void At_CellBoundary_IsGoldenValue()
        {
            // (32,32,32) id=1: на границе клетки материала → Quartz (4).
            Assert.AreEqual(VoxelMaterial.Quartz, IslandMaterialField.At(32, 32, 32, new IslandId(1)),
                "Материал на границе клетки id=1 — golden значение.");
        }

        [Test]
        public void At_NegativeCoords_IsGoldenValue()
        {
            // (−16,8,24) id=3: отрицательные координаты → Quartz (4).
            Assert.AreEqual(VoxelMaterial.Quartz, IslandMaterialField.At(-16, 8, 24, new IslandId(3)),
                "Материал с отрицательными координатами id=3 — golden значение.");
        }

        [Test]
        public void At_NeverReturnsNone()
        {
            // Материал всегда один из четырёх (Dirt..Quartz), никогда None.
            int[] coords = { -32, -16, 0, 16, 32, 64, 100 };
            int[] ids = { 1, 2, 7 };

            foreach (int x in coords)
            foreach (int y in coords)
            foreach (int z in coords)
            foreach (int id in ids)
            {
                VoxelMaterial m = IslandMaterialField.At(x, y, z, new IslandId(id));
                Assert.GreaterOrEqual((int)m, (int)VoxelMaterial.Dirt,
                    $"Материал >= Dirt (x={x}, y={y}, z={z}, id={id}).");
                Assert.LessOrEqual((int)m, (int)VoxelMaterial.Quartz,
                    $"Материал <= Quartz (x={x}, y={y}, z={z}, id={id}).");
            }
        }

        [Test]
        public void At_IsDeterministic()
        {
            var id = new IslandId(5);
            VoxelMaterial a = IslandMaterialField.At(42, -17, 99, id);
            VoxelMaterial b = IslandMaterialField.At(42, -17, 99, id);
            Assert.AreEqual(a, b, "Материал обязан быть детерминированным.");
        }

        [Test]
        public void At_DifferentIds_DifferentMaterial()
        {
            // Разные Id дают разные seed → материал различается хотя бы в одной точке.
            bool foundDifference = false;
            for (int x = -64; x <= 64 && !foundDifference; x += 8)
            {
                for (int y = -64; y <= 64 && !foundDifference; y += 8)
                {
                    for (int z = -64; z <= 64 && !foundDifference; z += 8)
                    {
                        VoxelMaterial m1 = IslandMaterialField.At(x, y, z, new IslandId(1));
                        VoxelMaterial m2 = IslandMaterialField.At(x, y, z, new IslandId(2));
                        if (m1 != m2)
                        {
                            foundDifference = true;
                        }
                    }
                }
            }
            Assert.IsTrue(foundDifference,
                "Разные Id обязаны давать разный материал хотя бы в одной точке.");
        }

        [Test]
        public void At_AllFourMaterialsAppear()
        {
            // На сетке точек все четыре материала должны встретиться.
            bool seenDirt = false, seenCoal = false, seenSand = false, seenQuartz = false;

            for (int x = -128; x <= 128 && !(seenDirt && seenCoal && seenSand && seenQuartz); x += 16)
            {
                for (int y = -128; y <= 128 && !(seenDirt && seenCoal && seenSand && seenQuartz); y += 16)
                {
                    for (int z = -128; z <= 128 && !(seenDirt && seenCoal && seenSand && seenQuartz); z += 16)
                    {
                        VoxelMaterial m = IslandMaterialField.At(x, y, z, new IslandId(1));
                        if (m == VoxelMaterial.Dirt) seenDirt = true;
                        if (m == VoxelMaterial.Coal) seenCoal = true;
                        if (m == VoxelMaterial.Sand) seenSand = true;
                        if (m == VoxelMaterial.Quartz) seenQuartz = true;
                    }
                }
            }

            Assert.IsTrue(seenDirt, "Материал Dirt должен встречаться на сетке точек.");
            Assert.IsTrue(seenCoal, "Материал Coal должен встречаться на сетке точек.");
            Assert.IsTrue(seenSand, "Материал Sand должен встречаться на сетке точек.");
            Assert.IsTrue(seenQuartz, "Материал Quartz должен встречаться на сетке точек.");
        }
    }
}
