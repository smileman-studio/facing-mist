using NUnit.Framework;
using SmilemanStudio.Core;
using SmilemanStudio.Simulation;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты определения тира острова TierCalculator (ADR 0001).
    /// Hot включается при расстоянии до центра меньше диаметра острова.
    /// </summary>
    public class TierCalculatorTests
    {
        // Типичный радиус острова: диаметр = 2000 м.
        private const int IslandRadius = 1000;

        [Test]
        public void InsideIslandDiameter_ReturnsHot()
        {
            // Расстояние 1500 м < диаметр 2000 м -> Hot.
            IslandTier tier = TierCalculator.CalculateTier(1500L * 1500L, IslandRadius);

            Assert.AreEqual(IslandTier.Hot, tier);
        }

        [Test]
        public void ExactlyAtIslandDiameter_ReturnsHot()
        {
            // distance² == diameter² (2000²) -> включительно, Hot.
            long diameter = 2L * IslandRadius;
            IslandTier tier = TierCalculator.CalculateTier(diameter * diameter, IslandRadius);

            Assert.AreEqual(IslandTier.Hot, tier);
        }

        [Test]
        public void JustOutsideIslandDiameter_ReturnsWarm()
        {
            // Расстояние чуть больше диаметра (2001 м), но <= 5000 -> Warm.
            long justOutside = 2L * IslandRadius + 1;
            IslandTier tier = TierCalculator.CalculateTier(justOutside * justOutside, IslandRadius);

            Assert.AreEqual(IslandTier.Warm, tier);
        }

        [Test]
        public void AtWarmBoundary_ReturnsWarm()
        {
            // distance² == 5000² -> включительно, Warm.
            IslandTier tier = TierCalculator.CalculateTier(5000L * 5000L, IslandRadius);

            Assert.AreEqual(IslandTier.Warm, tier);
        }

        [Test]
        public void JustPastWarmBoundary_ReturnsCold()
        {
            // distance² = 5000² + 1 -> Warm исключён, Cold.
            IslandTier tier = TierCalculator.CalculateTier(5000L * 5000L + 1, IslandRadius);

            Assert.AreEqual(IslandTier.Cold, tier);
        }

        [Test]
        public void AtColdBoundary_ReturnsCold()
        {
            // distance² == 10000² -> включительно, Cold.
            IslandTier tier = TierCalculator.CalculateTier(10000L * 10000L, IslandRadius);

            Assert.AreEqual(IslandTier.Cold, tier);
        }

        [Test]
        public void JustPastColdBoundary_ReturnsBackground()
        {
            // distance² = 10000² + 1 -> Background.
            IslandTier tier = TierCalculator.CalculateTier(10000L * 10000L + 1, IslandRadius);

            Assert.AreEqual(IslandTier.Background, tier);
        }

        [Test]
        public void FarDistance_ReturnsBackground()
        {
            // distance² = 20000² -> Background.
            IslandTier tier = TierCalculator.CalculateTier(20000L * 20000L, IslandRadius);

            Assert.AreEqual(IslandTier.Background, tier);
        }

        [Test]
        public void LargeIsland_ExpandsHotZone()
        {
            // Остров с радиусом 3000 м: диаметр = 6000 м.
            // distance = 5000 < diameter 6000 -> Hot (внутри диаметра).
            IslandTier tier = TierCalculator.CalculateTier(5000L * 5000L, 3000);

            Assert.AreEqual(IslandTier.Hot, tier);
        }

        [Test]
        public void ZeroRadius_Island_HotOnlyAtOrigin()
        {
            // Радиус 0: диаметр = 0, только distance² == 0 даёт Hot.
            Assert.AreEqual(IslandTier.Hot, TierCalculator.CalculateTier(0L, 0));
            Assert.AreEqual(IslandTier.Warm, TierCalculator.CalculateTier(1L, 0));
        }

        [Test]
        public void SmallIsland_DiameterLessThanWarm()
        {
            // Остров радиус 100 м: диаметр = 200 м.
            // distance = 201 > diameter 200 -> Warm (в пределах 5000).
            IslandTier tier = TierCalculator.CalculateTier(201L * 201L, 100);

            Assert.AreEqual(IslandTier.Warm, tier);
        }
    }
}
