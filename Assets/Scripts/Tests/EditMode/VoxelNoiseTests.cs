using NUnit.Framework;
using SmilemanStudio.Core.Voxels;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты целочисленного шума <see cref="VoxelNoise"/> (ADR-0002, D6).
    /// </summary>
    /// <remarks>
    /// Golden-значения проверены на Python-реализации того же алгоритма;
    /// целочисленная математика даёт идентичный результат на всех платформах.
    /// </remarks>
    public class VoxelNoiseTests
    {
        [Test]
        public void Hash_AtZero_IsZero()
        {
            // Murmur3 fmix32(0) = 0 (все сдвиги и умножения дают ноль).
            Assert.AreEqual(0u, VoxelNoise.Hash(0u));
        }

        [Test]
        public void Hash_AtOne_IsGoldenValue()
        {
            Assert.AreEqual(1364076727u, VoxelNoise.Hash(1u),
                "fmix32(1) — фиксированное значение avalanche-хеша.");
        }

        [Test]
        public void Hash_At42_IsGoldenValue()
        {
            Assert.AreEqual(142593372u, VoxelNoise.Hash(42u),
                "fmix32(42) — фиксированное значение avalanche-хеша.");
        }

        [Test]
        public void Hash_IsDeterministic()
        {
            Assert.AreEqual(VoxelNoise.Hash(12345u), VoxelNoise.Hash(12345u),
                "Хеш обязан быть детерминированным.");
        }

        [Test]
        public void Hash_DifferentInputs_DifferentOutputs()
        {
            Assert.AreNotEqual(VoxelNoise.Hash(1u), VoxelNoise.Hash(2u),
                "Avalanche-хеш обязан рассеивать соседние входы.");
        }

        [Test]
        public void ValueNoise2D_AtNode_IsGoldenValue()
        {
            // (0,0,seed=42): x0=0, y0=0, tx=0, ty=0 → результат = NodeValue(0,0,42).
            Assert.AreEqual(15152, VoxelNoise.ValueNoise2D(0, 0, 42u),
                "Node (0,0) seed=42 — golden значение.");
        }

        [Test]
        public void ValueNoise2D_AtNextNode_IsGoldenValue()
        {
            // (65536, 0, seed=42): x0=1, tx=0 → NodeValue(1,0,42).
            Assert.AreEqual(13955, VoxelNoise.ValueNoise2D(65536, 0, 42u),
                "Node (1,0) seed=42 — golden значение.");
        }

        [Test]
        public void ValueNoise2D_AtHalfway_IsGoldenValue()
        {
            // (32768, 0, seed=42): x0=0, tx=32768 → smoothstep=32768 (0,5).
            // v0 = n00 + (n10 - n00) * 32768 >> 16 = (15152 + 13955) / 2 = 14553.
            Assert.AreEqual(14553, VoxelNoise.ValueNoise2D(32768, 0, 42u),
                "Середина ребра между нодами — golden значение.");
        }

        [Test]
        public void ValueNoise2D_YNode_IsGoldenValue()
        {
            Assert.AreEqual(35650, VoxelNoise.ValueNoise2D(0, 65536, 42u),
                "Node (0,1) seed=42 — golden значение.");
        }

        [Test]
        public void ValueNoise2D_ResultInRange()
        {
            int[] testCoords = { 0, 65536, 32768, -32768, 131072 };

            foreach (int x in testCoords)
            foreach (int y in testCoords)
            {
                int v = VoxelNoise.ValueNoise2D(x, y, 0u);
                Assert.GreaterOrEqual(v, 0, "Noise должен быть неотрицательным.");
                Assert.LessOrEqual(v, 65535, "Noise должен быть <= 65535.");
            }
        }

        [Test]
        public void ValueNoise2D_IsDeterministic()
        {
            int a = VoxelNoise.ValueNoise2D(123456, -789012, 99u);
            int b = VoxelNoise.ValueNoise2D(123456, -789012, 99u);
            Assert.AreEqual(a, b, "Шум обязан быть детерминированным.");
        }

        [Test]
        public void ValueNoise2D_DifferentSeeds_DifferentResults()
        {
            int v1 = VoxelNoise.ValueNoise2D(0, 0, 1u);
            int v2 = VoxelNoise.ValueNoise2D(0, 0, 2u);
            Assert.AreNotEqual(v1, v2, "Разные seed должны давать разные значения.");
        }

        [Test]
        public void Fbm2D_AtOrigin_IsGoldenValue()
        {
            Assert.AreEqual(20824, VoxelNoise.Fbm2D(0, 0, 42u),
                "FBM (0,0) seed=42 — golden значение.");
        }

        [Test]
        public void Fbm2D_AtNodeX_IsGoldenValue()
        {
            Assert.AreEqual(22076, VoxelNoise.Fbm2D(65536, 0, 42u),
                "FBM (1,0) seed=42 — golden значение.");
        }

        [Test]
        public void Fbm2D_AtNodeY_IsGoldenValue()
        {
            Assert.AreEqual(35648, VoxelNoise.Fbm2D(0, 65536, 42u),
                "FBM (0,1) seed=42 — golden значение.");
        }

        [Test]
        public void Fbm2D_OtherPosition_IsGoldenValue()
        {
            Assert.AreEqual(53055, VoxelNoise.Fbm2D(131072, 65536, 123u),
                "FBM (2,1) seed=123 — golden значение.");
        }

        [Test]
        public void Fbm2D_ResultInRange()
        {
            int[] coords = { 0, 65536, 131072, -65536, 262144 };

            foreach (int x in coords)
            foreach (int y in coords)
            {
                int v = VoxelNoise.Fbm2D(x, y, 7u);
                Assert.GreaterOrEqual(v, 0, "FBM должен быть неотрицательным.");
                Assert.LessOrEqual(v, 65535, "FBM должен быть <= 65535.");
            }
        }

        [Test]
        public void Fbm2D_IsDeterministic()
        {
            int a = VoxelNoise.Fbm2D(999999, -888888, 123u);
            int b = VoxelNoise.Fbm2D(999999, -888888, 123u);
            Assert.AreEqual(a, b, "FBM обязан быть детерминированным.");
        }

        // --- ValueNoise3D (P2: SDF острова, ADR-0002 D6/D7) ---

        [Test]
        public void ValueNoise3D_AtOrigin_IsGoldenValue()
        {
            Assert.AreEqual(23937, VoxelNoise.ValueNoise3D(0, 0, 0, 42u),
                "Node (0,0,0) seed=42 — golden значение.");
        }

        [Test]
        public void ValueNoise3D_AtNextNode_IsGoldenValue()
        {
            Assert.AreEqual(961, VoxelNoise.ValueNoise3D(65536, 0, 0, 42u),
                "Node (1,0,0) seed=42 — golden значение.");
            Assert.AreEqual(46240, VoxelNoise.ValueNoise3D(0, 65536, 0, 42u),
                "Node (0,1,0) seed=42 — golden значение.");
            Assert.AreEqual(44730, VoxelNoise.ValueNoise3D(0, 0, 65536, 42u),
                "Node (0,0,1) seed=42 — golden значение.");
        }

        [Test]
        public void ValueNoise3D_AtHalfway_IsGoldenValue()
        {
            Assert.AreEqual(12449, VoxelNoise.ValueNoise3D(32768, 0, 0, 42u),
                "Середина ребра по X seed=42 — golden значение.");
            Assert.AreEqual(35088, VoxelNoise.ValueNoise3D(0, 32768, 0, 42u),
                "Середина ребра по Y seed=42 — golden значение.");
            Assert.AreEqual(34333, VoxelNoise.ValueNoise3D(0, 0, 32768, 42u),
                "Середина ребра по Z seed=42 — golden значение.");
        }

        [Test]
        public void ValueNoise3D_AtCellCenter_IsGoldenValue()
        {
            Assert.AreEqual(28038, VoxelNoise.ValueNoise3D(32768, 32768, 32768, 42u),
                "Центр клетки seed=42 — golden значение.");
        }

        [Test]
        public void ValueNoise3D_OtherPosition_IsGoldenValue()
        {
            Assert.AreEqual(29138, VoxelNoise.ValueNoise3D(131072, 65536, 65536, 123u),
                "Node (2,1,1) seed=123 — golden значение.");
        }

        [Test]
        public void ValueNoise3D_ResultInRange()
        {
            int[] coords = { 0, 65536, 32768, -32768, 131072 };

            foreach (int x in coords)
            foreach (int y in coords)
            foreach (int z in coords)
            {
                int v = VoxelNoise.ValueNoise3D(x, y, z, 0u);
                Assert.GreaterOrEqual(v, 0, "Noise3D должен быть неотрицательным.");
                Assert.LessOrEqual(v, 65535, "Noise3D должен быть <= 65535.");
            }
        }

        [Test]
        public void ValueNoise3D_IsDeterministic()
        {
            int a = VoxelNoise.ValueNoise3D(123456, -789012, 456789, 99u);
            int b = VoxelNoise.ValueNoise3D(123456, -789012, 456789, 99u);
            Assert.AreEqual(a, b, "Noise3D обязан быть детерминированным.");
        }

        [Test]
        public void ValueNoise3D_DifferentSeeds_DifferentResults()
        {
            int v1 = VoxelNoise.ValueNoise3D(100000, 200000, 300000, 1u);
            int v2 = VoxelNoise.ValueNoise3D(100000, 200000, 300000, 2u);
            Assert.AreNotEqual(v1, v2, "Разные seed должны давать разные значения.");
        }

        // --- Fbm3D ---

        [Test]
        public void Fbm3D_AtOrigin_IsGoldenValue()
        {
            Assert.AreEqual(25821, VoxelNoise.Fbm3D(0, 0, 0, 42u),
                "FBM3D (0,0,0) seed=42 — golden значение.");
        }

        [Test]
        public void Fbm3D_AtNodes_IsGoldenValue()
        {
            Assert.AreEqual(18163, VoxelNoise.Fbm3D(65536, 0, 0, 42u),
                "FBM3D (1,0,0) seed=42 — golden значение.");
            Assert.AreEqual(38576, VoxelNoise.Fbm3D(0, 65536, 0, 42u),
                "FBM3D (0,1,0) seed=42 — golden значение.");
            Assert.AreEqual(36736, VoxelNoise.Fbm3D(0, 0, 65536, 42u),
                "FBM3D (0,0,1) seed=42 — golden значение.");
        }

        [Test]
        public void Fbm3D_OtherPosition_IsGoldenValue()
        {
            Assert.AreEqual(35624, VoxelNoise.Fbm3D(131072, 65536, 65536, 123u),
                "FBM3D (2,1,1) seed=123 — golden значение.");
        }

        [Test]
        public void Fbm3D_ResultInRange()
        {
            int[] coords = { 0, 65536, 131072, -65536, 262144 };

            foreach (int x in coords)
            foreach (int y in coords)
            foreach (int z in coords)
            {
                int v = VoxelNoise.Fbm3D(x, y, z, 7u);
                Assert.GreaterOrEqual(v, 0, "FBM3D должен быть неотрицательным.");
                Assert.LessOrEqual(v, 65535, "FBM3D должен быть <= 65535.");
            }
        }

        [Test]
        public void Fbm3D_IsDeterministic()
        {
            int a = VoxelNoise.Fbm3D(999999, -888888, 777777, 123u);
            int b = VoxelNoise.Fbm3D(999999, -888888, 777777, 123u);
            Assert.AreEqual(a, b, "FBM3D обязан быть детерминированным.");
        }
    }
}
