using NUnit.Framework;
using SmilemanStudio.Core.Voxels;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты горизонтальной геометрии острова и правил раскладки
    /// <see cref="IslandFootprint"/> (ADR-0002, D1).
    /// </summary>
    /// <remarks>
    /// Значения проверяются явно (эквивалент golden-значений): методы целочисленные,
    /// поэтому результат обязан совпадать на любой платформе.
    /// </remarks>
    public class IslandFootprintTests
    {
        [Test]
        public void Extent_AtRadiusMin_MatchesConeFormula()
        {
            // R = 200: добавка ceil(200 * 200 / 900) = 45 -> 245 м.
            Assert.AreEqual(245, IslandFootprint.Extent(VoxelConstants.RadiusMin),
                "Силуэт острова минимального радиуса должен совпадать с формулой конуса.");
        }

        [Test]
        public void Extent_AtReferenceRadius_MatchesConeFormula()
        {
            // R = 500: добавка ceil(500 * 200 / 900) = 112 -> 612 м.
            Assert.AreEqual(612, IslandFootprint.Extent(500),
                "Силуэт острова R = 500 должен совпадать с формулой конуса.");
        }

        [Test]
        public void Extent_AtRadiusMax_MatchesConeFormula()
        {
            // R = 2000: добавка ceil(2000 * 200 / 900) = 445 -> 2445 м.
            Assert.AreEqual(2445, IslandFootprint.Extent(VoxelConstants.RadiusMax),
                "Силуэт острова максимального радиуса должен совпадать с формулой конуса.");
        }

        [Test]
        public void Extent_IsNeverSmallerThanRadius()
        {
            for (int radius = VoxelConstants.RadiusMin; radius <= VoxelConstants.RadiusMax; radius++)
            {
                Assert.GreaterOrEqual(IslandFootprint.Extent(radius), radius,
                    "Силуэт не может быть уже радиуса на опорном уровне (R = " + radius + ").");
            }
        }

        [Test]
        public void Extent_IsMonotonicByRadius()
        {
            int previous = IslandFootprint.Extent(VoxelConstants.RadiusMin);

            for (int radius = VoxelConstants.RadiusMin + 1; radius <= VoxelConstants.RadiusMax; radius++)
            {
                int current = IslandFootprint.Extent(radius);

                Assert.GreaterOrEqual(current, previous,
                    "Силуэт должен расти вместе с радиусом (R = " + radius + ").");
                previous = current;
            }
        }

        [Test]
        public void MinEdgeGap_EqualsSmallerRadius()
        {
            Assert.AreEqual(200, IslandFootprint.MinEdgeGap(200, 2000),
                "Минимальный зазор между краями равен радиусу меньшего острова.");
            Assert.AreEqual(200, IslandFootprint.MinEdgeGap(2000, 200),
                "Зазор не зависит от порядка аргументов.");
            Assert.AreEqual(500, IslandFootprint.MinEdgeGap(500, 500),
                "Для равных радиусов зазор равен радиусу.");
        }

        [Test]
        public void MinCenterSpacing_LeavesEdgeGapEqualToSmallerRadius()
        {
            int[] radii = { 200, 350, 500, 1100, 2000 };

            for (int i = 0; i < radii.Length; i++)
            {
                for (int j = 0; j < radii.Length; j++)
                {
                    long spacing = IslandFootprint.MinCenterSpacing(radii[i], radii[j]);
                    long edgeGap = IslandFootprint.EdgeGap(spacing, radii[i], radii[j]);

                    Assert.AreEqual(IslandFootprint.MinEdgeGap(radii[i], radii[j]), edgeGap,
                        "На минимальной дистанции между центрами зазор между краями должен быть " +
                        "ровно равен радиусу меньшего острова (" + radii[i] + ", " + radii[j] + ").");
                }
            }
        }

        [Test]
        public void MinCenterSpacing_AtRadiusMaxPair_Is6890()
        {
            // 2445 + 2445 + 2000 = 6890 м.
            Assert.AreEqual(6890L, IslandFootprint.MinCenterSpacing(2000, 2000),
                "Дистанция для пары островов максимального радиуса должна быть 6890 м.");
        }

        [Test]
        public void MinCenterSpacing_AtRadiusMinPair_Is690()
        {
            // 245 + 245 + 200 = 690 м.
            Assert.AreEqual(690L, IslandFootprint.MinCenterSpacing(200, 200),
                "Дистанция для пары островов минимального радиуса должна быть 690 м.");
        }

        [Test]
        public void MinCenterSpacing_IsSymmetric()
        {
            for (int radius = VoxelConstants.RadiusMin; radius <= VoxelConstants.RadiusMax; radius += 97)
            {
                for (int other = VoxelConstants.RadiusMin; other <= VoxelConstants.RadiusMax; other += 89)
                {
                    Assert.AreEqual(
                        IslandFootprint.MinCenterSpacing(radius, other),
                        IslandFootprint.MinCenterSpacing(other, radius),
                        "Дистанция не должна зависеть от порядка аргументов.");
                }
            }
        }

        [Test]
        public void MinCenterSpacingSquared_MatchesSpacingSquared()
        {
            for (int radius = VoxelConstants.RadiusMin; radius <= VoxelConstants.RadiusMax; radius += 97)
            {
                long spacing = IslandFootprint.MinCenterSpacing(radius, VoxelConstants.RadiusMax);

                Assert.AreEqual(spacing * spacing,
                    IslandFootprint.MinCenterSpacingSquared(radius, VoxelConstants.RadiusMax),
                    "Квадрат дистанции должен совпадать с произведением для R = " + radius + ".");
            }
        }

        [Test]
        public void EdgeGap_IsNegative_WhenSilhouettesOverlap()
        {
            long edgeGap = IslandFootprint.EdgeGap(100, 2000, 2000);

            Assert.Less(edgeGap, 0,
                "Пересечение силуэтов должно давать отрицательный зазор между краями.");
        }

        [Test]
        public void IsValidRadius_RespectsAdrRange()
        {
            Assert.IsFalse(IslandFootprint.IsValidRadius(VoxelConstants.RadiusMin - 1),
                "Радиус ниже RadiusMin недопустим.");
            Assert.IsTrue(IslandFootprint.IsValidRadius(VoxelConstants.RadiusMin),
                "RadiusMin входит в диапазон.");
            Assert.IsTrue(IslandFootprint.IsValidRadius(VoxelConstants.RadiusMax),
                "RadiusMax входит в диапазон.");
            Assert.IsFalse(IslandFootprint.IsValidRadius(VoxelConstants.RadiusMax + 1),
                "Радиус выше RadiusMax недопустим.");
        }
    }
}
