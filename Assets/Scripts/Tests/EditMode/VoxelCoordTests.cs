using NUnit.Framework;
using SmilemanStudio.Core.Voxels;
using Unity.Mathematics;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты island-local воксельных координат <see cref="VoxelCoord"/> (ADR-0002, D8).
    /// </summary>
    public class VoxelCoordTests
    {
        [Test]
        public void ToMillimeters_AtOrigin_IsZero()
        {
            Assert.AreEqual(new int3(0, 0, 0), VoxelCoord.ToMillimeters(new int3(0, 0, 0)));
        }

        [Test]
        public void ToMillimeters_OneVoxel_Is500mm()
        {
            Assert.AreEqual(new int3(500, 500, 500), VoxelCoord.ToMillimeters(new int3(1, 1, 1)),
                "Шаг LOD0 = 0,5 м = 500 мм.");
        }

        [Test]
        public void ToMillimeters_NegativeVoxel_IsNegativeMillimeters()
        {
            Assert.AreEqual(new int3(-1500, -500, -2500),
                VoxelCoord.ToMillimeters(new int3(-3, -1, -5)));
        }

        [Test]
        public void FromMillimeters_ExactBoundary_RoundDown()
        {
            // 1000 мм = 2 воксела ровно.
            Assert.AreEqual(new int3(2, 2, 2), VoxelCoord.FromMillimeters(new int3(1000, 1000, 1000)));
        }

        [Test]
        public void FromMillimeters_NegativeValue_FloorsCorrectly()
        {
            // -500 мм: floor(-500/500) = -1, не 0 (С# truncates toward zero).
            Assert.AreEqual(new int3(-1, -1, -1), VoxelCoord.FromMillimeters(new int3(-500, -500, -500)));

            // -600 мм: floor(-600/500) = floor(-1.2) = -2.
            Assert.AreEqual(new int3(-2, -2, -2), VoxelCoord.FromMillimeters(new int3(-600, -600, -600)));
        }

        [Test]
        public void RoundTrip_VoxelToMillimetersToVoxel_IsIdentity()
        {
            int3[] coords =
            {
                new int3(0, 0, 0),
                new int3(1, -1, 2),
                new int3(-256, 896, 512),
                new int3(int.MaxValue / 1000, int.MinValue / 1000, 0),
            };

            foreach (int3 coord in coords)
            {
                int3 roundTrip = VoxelCoord.FromMillimeters(VoxelCoord.ToMillimeters(coord));
                Assert.AreEqual(coord, roundTrip,
                    "Roundtrip не совпал для " + coord);
            }
        }

        [Test]
        public void FromMeters_OneMeter_IsTwoVoxels()
        {
            Assert.AreEqual(new int3(2, 2, 2), VoxelCoord.FromMeters(new int3(1, 1, 1)),
                "1 м = 2 воксела LOD0 (шаг 0,5 м).");
        }

        [Test]
        public void FromMeters_NegativeOneMeter_IsMinusTwoVoxels()
        {
            Assert.AreEqual(new int3(-2, -2, -2), VoxelCoord.FromMeters(new int3(-1, -1, -1)));
        }

        [Test]
        public void ClampY_BelowBand_ClampsToMin()
        {
            // CrustStitchMinY = -128 м * 2 воксела/м = -256.
            int minY = VoxelConstants.CrustStitchMinY * VoxelCoord.VoxelsPerMeter();
            Assert.AreEqual(minY, VoxelCoord.ClampY(minY - 100),
                "Y ниже полосы должен клампиться на нижнюю границу.");
        }

        [Test]
        public void ClampY_AboveBand_ClampsToMax()
        {
            // CrustMaxY = 448 м * 2 воксела/м = 896.
            int maxY = VoxelConstants.CrustMaxY * VoxelCoord.VoxelsPerMeter();
            Assert.AreEqual(maxY, VoxelCoord.ClampY(maxY + 100),
                "Y выше полосы должен клампиться на верхнюю границу.");
        }

        [Test]
        public void ClampY_WithinBand_IsUnchanged()
        {
            Assert.AreEqual(0, VoxelCoord.ClampY(0), "Y = 0 внутри полосы, не меняется.");
            Assert.AreEqual(500, VoxelCoord.ClampY(500), "Y = 500 внутри полосы, не меняется.");
        }

        [Test]
        public void VoxelsPerMeter_Is2()
        {
            Assert.AreEqual(2, VoxelCoord.VoxelsPerMeter(),
                "При шаге 0,5 м в одном метре 2 воксела.");
        }
    }
}
