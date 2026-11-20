using NUnit.Framework;
using SmilemanStudio.Core;
using SmilemanStudio.Core.Voxels;
using Unity.Mathematics;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты целочисленного SDF острова <see cref="IslandSdf"/> (ADR-0002, D1/D6).
    /// </summary>
    /// <remarks>
    /// Golden-значения вычислены Python-репликой того же алгоритма
    /// (tools/golden_sdf.py). Целочисленная математика даёт идентичный
    /// результат на всех платформах (x86, ARM, WASM).
    /// </remarks>
    public class IslandSdfTests
    {
        // --- Параметры (инварианты) ---

        [Test]
        public void ApexDepth_AtRadiusMin_Is180()
        {
            Assert.AreEqual(180, IslandSdf.ApexDepth(200),
                "D = 200 * 900 / 1000 = 180 м.");
        }

        [Test]
        public void ApexDepth_AtReferenceRadius_Is450()
        {
            Assert.AreEqual(450, IslandSdf.ApexDepth(500),
                "D = 500 * 900 / 1000 = 450 м.");
        }

        [Test]
        public void ApexDepth_AtRadiusMax_Is1800()
        {
            Assert.AreEqual(1800, IslandSdf.ApexDepth(2000),
                "D = 2000 * 900 / 1000 = 1800 м.");
        }

        [Test]
        public void TopHeightMax_AtRadiusMin_Is40()
        {
            Assert.AreEqual(40, IslandSdf.TopHeightMax(200),
                "min(200 * 200 / 1000, 448) = 40 м.");
        }

        [Test]
        public void TopHeightMax_AtReferenceRadius_Is100()
        {
            Assert.AreEqual(100, IslandSdf.TopHeightMax(500),
                "min(500 * 200 / 1000, 448) = 100 м.");
        }

        [Test]
        public void TopHeightMax_AtRadiusMax_Is400()
        {
            Assert.AreEqual(400, IslandSdf.TopHeightMax(2000),
                "min(2000 * 200 / 1000, 448) = 400 м (hard cap не срабатывает).");
        }

        [Test]
        public void TopHeightMax_RespectsHardCap()
        {
            // R = 2240: 2240 * 200 / 1000 = 448 = hard cap.
            Assert.AreEqual(448, IslandSdf.TopHeightMax(2240),
                "На R = 2240 рельеф достигает hard cap 448 м.");
            // R = 2500: 2500 * 200 / 1000 = 500 > 448 → clamp.
            Assert.AreEqual(448, IslandSdf.TopHeightMax(2500),
                "На R = 2500 рельеф ограничен hard cap 448 м.");
        }

        // --- TopSurface golden ---

        [Test]
        public void TopSurface_GoldenValues()
        {
            Assert.AreEqual(15, IslandSdf.TopSurface(0, 0, new IslandId(1), 200));
            Assert.AreEqual(15, IslandSdf.TopSurface(10, 0, new IslandId(1), 200));
            Assert.AreEqual(15, IslandSdf.TopSurface(0, 10, new IslandId(1), 200));
            Assert.AreEqual(28, IslandSdf.TopSurface(0, 0, new IslandId(2), 200));
            Assert.AreEqual(38, IslandSdf.TopSurface(0, 0, new IslandId(1), 500));
            Assert.AreEqual(152, IslandSdf.TopSurface(0, 0, new IslandId(1), 2000));
            Assert.AreEqual(35, IslandSdf.TopSurface(50, 50, new IslandId(7), 500));
            Assert.AreEqual(21, IslandSdf.TopSurface(-30, 25, new IslandId(3), 200));
        }

        [Test]
        public void TopSurface_InRange()
        {
            int[] coords = { -100, -50, 0, 10, 50, 100, 200, 500 };
            int[] radii = { 200, 500, 2000 };
            int[] ids = { 1, 2, 7 };

            foreach (int radius in radii)
            {
                int thm = IslandSdf.TopHeightMax(radius);
                foreach (int x in coords)
                foreach (int z in coords)
                foreach (int id in ids)
                {
                    int v = IslandSdf.TopSurface(x, z, new IslandId(id), radius);
                    Assert.GreaterOrEqual(v, 0,
                        $"TopSurface >= 0 (x={x}, z={z}, id={id}, R={radius}).");
                    Assert.LessOrEqual(v, thm,
                        $"TopSurface <= TopHeightMax={thm} (x={x}, z={z}, id={id}, R={radius}).");
                }
            }
        }

        [Test]
        public void TopSurface_IsDeterministic()
        {
            int a = IslandSdf.TopSurface(42, -17, new IslandId(5), 500);
            int b = IslandSdf.TopSurface(42, -17, new IslandId(5), 500);
            Assert.AreEqual(a, b, "TopSurface обязан быть детерминированным.");
        }

        // --- Sdf golden (R = 200) ---

        [Test]
        public void Sdf_R200_GoldenValues()
        {
            var id = new IslandId(1);
            Assert.AreEqual(133, IslandSdf.Sdf(0, 0, 0, id, 200));
            Assert.AreEqual(141, IslandSdf.Sdf(0, 10, 0, id, 200));
            Assert.AreEqual(126, IslandSdf.Sdf(0, -10, 0, id, 200));
            Assert.AreEqual(59, IslandSdf.Sdf(0, -100, 0, id, 200));
            Assert.AreEqual(127, IslandSdf.Sdf(10, 0, 0, id, 200));
            Assert.AreEqual(127, IslandSdf.Sdf(-10, 0, 0, id, 200));
            Assert.AreEqual(127, IslandSdf.Sdf(0, 0, 10, id, 200));
            Assert.AreEqual(132, IslandSdf.Sdf(5, 5, 5, id, 200));
            Assert.AreEqual(66, IslandSdf.Sdf(100, 0, 0, id, 200));
            Assert.AreEqual(282, IslandSdf.Sdf(0, 200, 0, id, 200));
            Assert.AreEqual(-14, IslandSdf.Sdf(0, -200, 0, id, 200));
            Assert.AreEqual(-237, IslandSdf.Sdf(0, -500, 0, id, 200));
            Assert.AreEqual(-609, IslandSdf.Sdf(0, -1000, 0, id, 200));
            Assert.AreEqual(-1353, IslandSdf.Sdf(0, -2000, 0, id, 200));
            Assert.AreEqual(-12, IslandSdf.Sdf(300, 0, 0, id, 200));
            Assert.AreEqual(431, IslandSdf.Sdf(0, 400, 0, id, 200));
            Assert.AreEqual(-163, IslandSdf.Sdf(0, -400, 0, id, 200));
            Assert.AreEqual(-1204, IslandSdf.Sdf(0, -1800, 0, id, 200));
            Assert.AreEqual(-2096, IslandSdf.Sdf(0, -3000, 0, id, 200));
        }

        // --- Sdf golden (R = 500) ---

        [Test]
        public void Sdf_R500_GoldenValues()
        {
            var id = new IslandId(1);
            Assert.AreEqual(334, IslandSdf.Sdf(0, 0, 0, id, 500));
            Assert.AreEqual(342, IslandSdf.Sdf(0, 10, 0, id, 500));
            Assert.AreEqual(327, IslandSdf.Sdf(0, -10, 0, id, 500));
            Assert.AreEqual(260, IslandSdf.Sdf(0, -100, 0, id, 500));
            Assert.AreEqual(328, IslandSdf.Sdf(10, 0, 0, id, 500));
            Assert.AreEqual(328, IslandSdf.Sdf(-10, 0, 0, id, 500));
            Assert.AreEqual(328, IslandSdf.Sdf(0, 0, 10, id, 500));
            Assert.AreEqual(333, IslandSdf.Sdf(5, 5, 5, id, 500));
            Assert.AreEqual(267, IslandSdf.Sdf(100, 0, 0, id, 500));
            Assert.AreEqual(483, IslandSdf.Sdf(0, 200, 0, id, 500));
            Assert.AreEqual(186, IslandSdf.Sdf(0, -200, 0, id, 500));
            Assert.AreEqual(-37, IslandSdf.Sdf(0, -500, 0, id, 500));
            Assert.AreEqual(-409, IslandSdf.Sdf(0, -1000, 0, id, 500));
            Assert.AreEqual(-1153, IslandSdf.Sdf(0, -2000, 0, id, 500));
            Assert.AreEqual(133, IslandSdf.Sdf(300, 0, 0, id, 500));
            Assert.AreEqual(632, IslandSdf.Sdf(0, 400, 0, id, 500));
            Assert.AreEqual(37, IslandSdf.Sdf(0, -400, 0, id, 500));
            Assert.AreEqual(-1004, IslandSdf.Sdf(0, -1800, 0, id, 500));
            Assert.AreEqual(-1897, IslandSdf.Sdf(0, -3000, 0, id, 500));
        }

        // --- Sdf golden (R = 2000) ---

        [Test]
        public void Sdf_R2000_GoldenValues()
        {
            var id = new IslandId(1);
            Assert.AreEqual(1338, IslandSdf.Sdf(0, 0, 0, id, 2000));
            Assert.AreEqual(1345, IslandSdf.Sdf(0, 10, 0, id, 2000));
            Assert.AreEqual(1330, IslandSdf.Sdf(0, -10, 0, id, 2000));
            Assert.AreEqual(1263, IslandSdf.Sdf(0, -100, 0, id, 2000));
            Assert.AreEqual(1331, IslandSdf.Sdf(10, 0, 0, id, 2000));
            Assert.AreEqual(1331, IslandSdf.Sdf(-10, 0, 0, id, 2000));
            Assert.AreEqual(1331, IslandSdf.Sdf(0, 0, 10, id, 2000));
            Assert.AreEqual(1337, IslandSdf.Sdf(5, 5, 5, id, 2000));
            Assert.AreEqual(1271, IslandSdf.Sdf(100, 0, 0, id, 2000));
            Assert.AreEqual(1486, IslandSdf.Sdf(0, 200, 0, id, 2000));
            Assert.AreEqual(1189, IslandSdf.Sdf(0, -200, 0, id, 2000));
            Assert.AreEqual(966, IslandSdf.Sdf(0, -500, 0, id, 2000));
            Assert.AreEqual(594, IslandSdf.Sdf(0, -1000, 0, id, 2000));
            Assert.AreEqual(-148, IslandSdf.Sdf(0, -2000, 0, id, 2000));
            Assert.AreEqual(1137, IslandSdf.Sdf(300, 0, 0, id, 2000));
            Assert.AreEqual(1635, IslandSdf.Sdf(0, 400, 0, id, 2000));
            Assert.AreEqual(1040, IslandSdf.Sdf(0, -400, 0, id, 2000));
            Assert.AreEqual(0, IslandSdf.Sdf(0, -1800, 0, id, 2000));
            Assert.AreEqual(-892, IslandSdf.Sdf(0, -3000, 0, id, 2000));
        }

        // --- Инварианты Sdf ---

        [Test]
        public void Sdf_AtOrigin_IsPositive()
        {
            // В центре острова (0,0,0) всегда воздух: dCone > 0 и dTop > 0.
            int[] radii = { 200, 500, 2000 };
            int[] ids = { 1, 2, 7 };

            foreach (int radius in radii)
            foreach (int id in ids)
            {
                int d = IslandSdf.Sdf(0, 0, 0, new IslandId(id), radius);
                Assert.Greater(d, 0,
                    $"Sdf(0,0,0) > 0 (id={id}, R={radius}).");
            }
        }

        [Test]
        public void Sdf_BelowIsland_IsNegative()
        {
            // Ниже вершины конуса всегда грунт: d < 0.
            // Используем y = -D - 100: достаточно далеко, чтобы |numerator| > denominator
            // и truncation toward zero давал отрицательный результат.
            int[] radii = { 200, 500, 2000 };
            int[] ids = { 1, 2, 7 };

            foreach (int radius in radii)
            {
                int d = IslandSdf.ApexDepth(radius);
                foreach (int id in ids)
                {
                    int sdf = IslandSdf.Sdf(0, -d - 100, 0, new IslandId(id), radius);
                    Assert.Less(sdf, 0,
                        $"Sdf ниже вершины < 0 (id={id}, R={radius}, D={d}).");
                }
            }
        }

        [Test]
        public void Sdf_FarAbove_IsPositive()
        {
            // Высоко над островом всегда воздух: d > 0.
            int[] radii = { 200, 500, 2000 };
            int[] ids = { 1, 2, 7 };

            foreach (int radius in radii)
            foreach (int id in ids)
            {
                int d = IslandSdf.Sdf(0, radius * 2, 0, new IslandId(id), radius);
                Assert.Greater(d, 0,
                    $"Sdf высоко над островом > 0 (id={id}, R={radius}).");
            }
        }

        [Test]
        public void Sdf_FarBelow_IsNegative()
        {
            // Глубоко под островом всегда грунт: d < 0.
            int[] radii = { 200, 500, 2000 };
            int[] ids = { 1, 2, 7 };

            foreach (int radius in radii)
            foreach (int id in ids)
            {
                int d = IslandSdf.Sdf(0, -radius * 3, 0, new IslandId(id), radius);
                Assert.Less(d, 0,
                    $"Sdf глубоко под островом < 0 (id={id}, R={radius}).");
            }
        }

        [Test]
        public void Sdf_IsDeterministic()
        {
            var id = new IslandId(3);
            int a = IslandSdf.Sdf(42, -17, 99, id, 500);
            int b = IslandSdf.Sdf(42, -17, 99, id, 500);
            Assert.AreEqual(a, b, "Sdf обязан быть детерминированным.");
        }

        [Test]
        public void TopSurface_DifferentIds_DifferentRelief()
        {
            // Rельеф (TopSurface) — id-зависимая часть Sdf: разные Id дают
            // разные карты высот. Находим точку, где они различаются.
            int radius = 500;
            int id1 = 1, id2 = 2;

            bool foundDifference = false;
            for (int x = -100; x <= 100 && !foundDifference; x += 7)
            {
                for (int z = -100; z <= 100 && !foundDifference; z += 7)
                {
                    int ts1 = IslandSdf.TopSurface(x, z, new IslandId(id1), radius);
                    int ts2 = IslandSdf.TopSurface(x, z, new IslandId(id2), radius);
                    if (ts1 != ts2)
                    {
                        foundDifference = true;
                        Assert.AreNotEqual(ts1, ts2,
                            $"Разные Id должны давать разный рельеф в точке ({x}, {z}).");
                    }
                }
            }
            Assert.IsTrue(foundDifference,
                "Разные Id обязаны давать разный рельеф хотя бы в одной точке.");
        }

        // --- SdfVoxel golden (R = 200 / 500 / 2000) ---

        [Test]
        public void SdfVoxel_R200_GoldenValues()
        {
            var id = new IslandId(1);
            Assert.AreEqual(267, IslandSdf.SdfVoxel(new int3(0, 0, 0), id, 200));
            Assert.AreEqual(282, IslandSdf.SdfVoxel(new int3(0, 20, 0), id, 200));
            Assert.AreEqual(252, IslandSdf.SdfVoxel(new int3(0, -20, 0), id, 200));
            Assert.AreEqual(118, IslandSdf.SdfVoxel(new int3(0, -200, 0), id, 200));
            Assert.AreEqual(254, IslandSdf.SdfVoxel(new int3(20, 0, 0), id, 200));
            Assert.AreEqual(254, IslandSdf.SdfVoxel(new int3(-20, 0, 0), id, 200));
            Assert.AreEqual(254, IslandSdf.SdfVoxel(new int3(0, 0, -20), id, 200));
            Assert.AreEqual(267, IslandSdf.SdfVoxel(new int3(10, 10, -6), id, 200));
            Assert.AreEqual(133, IslandSdf.SdfVoxel(new int3(200, 0, 0), id, 200));
            Assert.AreEqual(565, IslandSdf.SdfVoxel(new int3(0, 400, 0), id, 200));
            Assert.AreEqual(862, IslandSdf.SdfVoxel(new int3(0, 800, 0), id, 200));
            Assert.AreEqual(266, IslandSdf.SdfVoxel(new int3(1, 0, 0), id, 200));
            Assert.AreEqual(235, IslandSdf.SdfVoxel(new int3(3, -39, 5), id, 200));
            Assert.AreEqual(217, IslandSdf.SdfVoxel(new int3(-57, 129, 211), id, 200));
        }

        [Test]
        public void SdfVoxel_R500_GoldenValues()
        {
            var id = new IslandId(1);
            Assert.AreEqual(669, IslandSdf.SdfVoxel(new int3(0, 0, 0), id, 500));
            Assert.AreEqual(684, IslandSdf.SdfVoxel(new int3(0, 20, 0), id, 500));
            Assert.AreEqual(654, IslandSdf.SdfVoxel(new int3(0, -20, 0), id, 500));
            Assert.AreEqual(520, IslandSdf.SdfVoxel(new int3(0, -200, 0), id, 500));
            Assert.AreEqual(656, IslandSdf.SdfVoxel(new int3(20, 0, 0), id, 500));
            Assert.AreEqual(656, IslandSdf.SdfVoxel(new int3(-20, 0, 0), id, 500));
            Assert.AreEqual(656, IslandSdf.SdfVoxel(new int3(0, 0, -20), id, 500));
            Assert.AreEqual(669, IslandSdf.SdfVoxel(new int3(10, 10, -6), id, 500));
            Assert.AreEqual(535, IslandSdf.SdfVoxel(new int3(200, 0, 0), id, 500));
            Assert.AreEqual(967, IslandSdf.SdfVoxel(new int3(0, 400, 0), id, 500));
            Assert.AreEqual(1264, IslandSdf.SdfVoxel(new int3(0, 800, 0), id, 500));
            Assert.AreEqual(668, IslandSdf.SdfVoxel(new int3(1, 0, 0), id, 500));
            Assert.AreEqual(637, IslandSdf.SdfVoxel(new int3(3, -39, 5), id, 500));
            Assert.AreEqual(619, IslandSdf.SdfVoxel(new int3(-57, 129, 211), id, 500));
        }

        [Test]
        public void SdfVoxel_R2000_GoldenValues()
        {
            var id = new IslandId(1);
            Assert.AreEqual(2676, IslandSdf.SdfVoxel(new int3(0, 0, 0), id, 2000));
            Assert.AreEqual(2691, IslandSdf.SdfVoxel(new int3(0, 20, 0), id, 2000));
            Assert.AreEqual(2661, IslandSdf.SdfVoxel(new int3(0, -20, 0), id, 2000));
            Assert.AreEqual(2527, IslandSdf.SdfVoxel(new int3(0, -200, 0), id, 2000));
            Assert.AreEqual(2663, IslandSdf.SdfVoxel(new int3(20, 0, 0), id, 2000));
            Assert.AreEqual(2663, IslandSdf.SdfVoxel(new int3(-20, 0, 0), id, 2000));
            Assert.AreEqual(2663, IslandSdf.SdfVoxel(new int3(0, 0, -20), id, 2000));
            Assert.AreEqual(2676, IslandSdf.SdfVoxel(new int3(10, 10, -6), id, 2000));
            Assert.AreEqual(2542, IslandSdf.SdfVoxel(new int3(200, 0, 0), id, 2000));
            Assert.AreEqual(2973, IslandSdf.SdfVoxel(new int3(0, 400, 0), id, 2000));
            Assert.AreEqual(3271, IslandSdf.SdfVoxel(new int3(0, 800, 0), id, 2000));
            Assert.AreEqual(2675, IslandSdf.SdfVoxel(new int3(1, 0, 0), id, 2000));
            Assert.AreEqual(2644, IslandSdf.SdfVoxel(new int3(3, -39, 5), id, 2000));
            Assert.AreEqual(2626, IslandSdf.SdfVoxel(new int3(-57, 129, 211), id, 2000));
        }

        // --- Инварианты SdfVoxel ---

        [Test]
        public void SdfVoxel_MatchesMeterSdf_WithinOneStep()
        {
            // Единицы шага LOD0 вдвое мельче метров, поэтому SdfVoxel(2p) / 2
            // обязан совпадать с Sdf(p) с точностью до одного шага.
            // Точного равенства нет: floor_sqrt от удвоенных координат не
            // масштабируется ровно — floor(2√n) != 2·floor(√n).
            int[] radii = { 200, 500, 2000 };
            int[] points =
            {
                0, 0, 0,
                0, 10, 0,
                0, -100, 0,
                10, 0, 0,
                50, 5, -30,
                100, 0, 0,
                0, -200, 0,
                -40, 60, 20,
            };

            for (int p = 0; p < points.Length; p += 3)
            {
                int x = points[p], y = points[p + 1], z = points[p + 2];
                foreach (int radius in radii)
                {
                    int meters = IslandSdf.Sdf(x, y, z, new IslandId(1), radius);
                    int voxel = IslandSdf.SdfVoxel(new int3(2 * x, 2 * y, 2 * z), new IslandId(1), radius);
                    double deviation = math.abs(voxel / 2.0 - meters);
                    Assert.LessOrEqual(deviation, 1.0,
                        $"SdfVoxel(2*({x},{y},{z}))/2={voxel / 2.0} vs Sdf={meters} (R={radius}).");
                }
            }
        }

        [Test]
        public void SdfVoxel_AtOrigin_IsPositive()
        {
            foreach (int radius in new[] { 200, 500, 2000 })
            foreach (int id in new[] { 1, 2, 7 })
            {
                int d = IslandSdf.SdfVoxel(int3.zero, new IslandId(id), radius);
                Assert.Greater(d, 0, $"SdfVoxel(0,0,0) > 0 (id={id}, R={radius}).");
            }
        }

        [Test]
        public void SdfVoxel_SignMatchesMeterSdf_AtEvenCoordinates()
        {
            // Знак занятости обязан совпадать: иначе поверхность окажется
            // в разных местах у разных пайплайнов.
            int[] radii = { 200, 500, 2000 };
            for (int y = -300; y <= 300; y += 37)
            {
                foreach (int radius in radii)
                {
                    int meters = IslandSdf.Sdf(20, y, -15, new IslandId(1), radius);
                    int voxel = IslandSdf.SdfVoxel(new int3(40, 2 * y, -30), new IslandId(1), radius);
                    if (meters > 1 || meters < -1)
                    {
                        Assert.AreEqual(math.sign(meters), math.sign(voxel),
                            $"Знак SdfVoxel обязан совпадать со знаком Sdf (y={y}, R={radius}).");
                    }
                }
            }
        }

        [Test]
        public void SdfVoxel_IsDeterministic()
        {
            int3 v = new int3(37, -91, 204);
            int a = IslandSdf.SdfVoxel(v, new IslandId(3), 500);
            int b = IslandSdf.SdfVoxel(v, new IslandId(3), 500);
            Assert.AreEqual(a, b, "SdfVoxel обязан быть детерминированным.");
        }

        [Test]
        public void SdfVoxel_NearSurface_FitsInShort()
        {
            // Вблизи поверхности плотность обязана влезать в short:
            // |SdfVoxel| <= 127 → плотность <= 32512.
            var id = new IslandId(1);
            int[] radii = { 200, 500, 2000 };

            foreach (int radius in radii)
            {
                for (int y = -128; y <= 128; y += 8)
                {
                    int d = IslandSdf.SdfVoxel(new int3(0, y, 0), id, radius);
                    if (d >= -127 && d <= 127)
                    {
                        long density = (long)d * VoxelConstants.DensityUnitsPerVoxel;
                        Assert.GreaterOrEqual(density, short.MinValue,
                            $"Плотность >= short.MinValue (y={y}, R={radius}, d={d}).");
                        Assert.LessOrEqual(density, short.MaxValue,
                            $"Плотность <= short.MaxValue (y={y}, R={radius}, d={d}).");
                    }
                }
            }
        }

        // --- FloorSqrt ---

        [Test]
        public void FloorSqrt_KnownValues()
        {
            Assert.AreEqual(0, IslandSdf.FloorSqrt(0));
            Assert.AreEqual(1, IslandSdf.FloorSqrt(1));
            Assert.AreEqual(1, IslandSdf.FloorSqrt(2));
            Assert.AreEqual(1, IslandSdf.FloorSqrt(3));
            Assert.AreEqual(2, IslandSdf.FloorSqrt(4));
            Assert.AreEqual(3, IslandSdf.FloorSqrt(9));
            Assert.AreEqual(3, IslandSdf.FloorSqrt(15));
            Assert.AreEqual(4, IslandSdf.FloorSqrt(16));
            Assert.AreEqual(4, IslandSdf.FloorSqrt(17));
            Assert.AreEqual(10, IslandSdf.FloorSqrt(100));
            Assert.AreEqual(10, IslandSdf.FloorSqrt(101));
            Assert.AreEqual(100, IslandSdf.FloorSqrt(10000));
            Assert.AreEqual(200, IslandSdf.FloorSqrt(40000));
            Assert.AreEqual(1000, IslandSdf.FloorSqrt(1000000));
            Assert.AreEqual(999, IslandSdf.FloorSqrt(999999));
            Assert.AreEqual(1000, IslandSdf.FloorSqrt(1000001));
        }

        [Test]
        public void FloorSqrt_IsFloor_NotCeil()
        {
            // 15 = 3.87... → floor = 3, не 4.
            Assert.AreEqual(3, IslandSdf.FloorSqrt(15),
                "FloorSqrt обязан округлять вниз, не вверх.");
            // 999999 = 999.999... → floor = 999, не 1000.
            Assert.AreEqual(999, IslandSdf.FloorSqrt(999999),
                "FloorSqrt обязан округлять вниз, не вверх.");
        }

        [Test]
        public void FloorSqrt_NegativeInput_ReturnsZero()
        {
            Assert.AreEqual(0, IslandSdf.FloorSqrt(-1),
                "Отрицательный вход → 0.");
            Assert.AreEqual(0, IslandSdf.FloorSqrt(-100),
                "Отрицательный вход → 0.");
        }

        [Test]
        public void FloorSqrt_LargeValues()
        {
            // 2^40 = (2^20)^2 → sqrt = 2^20 = 1048576.
            Assert.AreEqual(1048576, IslandSdf.FloorSqrt(1L << 40),
                "sqrt(2^40) = 2^20.");
            // 2^40 + 1 → floor = 2^20 (не 2^20 + 1).
            Assert.AreEqual(1048576, IslandSdf.FloorSqrt((1L << 40) + 1),
                "sqrt(2^40 + 1) floor = 2^20.");
        }

        // --- Связь с VoxelDensity ---

        [Test]
        public void Sdf_NearSurface_FitsInShort()
        {
            // Вблизи поверхности (|Sdf| <= 127 м) плотность помещается в short.
            // 127 м * 256 = 32512 < 32767 (short.MaxValue).
            // Точки далеко от поверхности могут иметь |Sdf| > 127 — это ожидаемо:
            // они не пересекают поверхность и не попадают в Marching Cubes.
            var id = new IslandId(1);
            int[] radii = { 200, 500, 2000 };
            int[] ys = { -3000, -2000, -1800, -1000, -500, -400, -200, -100, -64, 0, 10, 100, 200, 400, 448 };

            foreach (int radius in radii)
            {
                foreach (int y in ys)
                {
                    int d = IslandSdf.Sdf(0, y, 0, id, radius);
                    long density = (long)d * VoxelConstants.DensityUnitsPerVoxel;
                    // Если |Sdf| <= 127 м — плотность гарантированно в short.
                    if (d >= -127 && d <= 127)
                    {
                        Assert.GreaterOrEqual(density, short.MinValue,
                            $"Плотность >= short.MinValue (y={y}, R={radius}, d={d}).");
                        Assert.LessOrEqual(density, short.MaxValue,
                            $"Плотность <= short.MaxValue (y={y}, R={radius}, d={d}).");
                    }
                }
            }
        }
    }
}
