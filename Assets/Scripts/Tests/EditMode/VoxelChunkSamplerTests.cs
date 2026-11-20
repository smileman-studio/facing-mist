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
    /// Тесты геометрии сэмплера чанка <see cref="VoxelChunkSampler"/> (P3.1, ADR-0002 D3/D8).
    /// </summary>
    /// <remarks>
    /// Покрывают: размер кэша и интерьер; отображение индекс → воксель LOD0
    /// на LOD 0/1/2/6/9 (шаг 2^k); origin из ключа; границы apron;
    /// покрытие центральной разности с полу базой в один шаг сетки;
    /// согласованность границ соседних чанков.
    /// </remarks>
    public class VoxelChunkSamplerTests
    {
        // --- Константы ---

        [Test]
        public void CacheSize_Is19()
        {
            Assert.AreEqual(19, VoxelChunkSampler.CacheSize, "17 сэмплов + apron по 1 (ADR-0002, D3).");
        }

        [Test]
        public void InteriorSamples_Is17()
        {
            Assert.AreEqual(17, VoxelChunkSampler.InteriorSamples,
                "16 ячеек чанка дают 17 сэмплов-узлов.");
        }

        [Test]
        public void Interior_CoversExactly17Indices()
        {
            int interiorCount = 0;
            for (int i = 0; i < VoxelChunkSampler.CacheSize; i++)
            {
                if (VoxelChunkSampler.IsInterior(i))
                {
                    interiorCount++;
                }
            }
            Assert.AreEqual(VoxelChunkSampler.InteriorSamples, interiorCount);
        }

        // --- IsInterior / IsApron ---

        [Test]
        public void IsInterior_OnlyIndices1To17()
        {
            for (int i = 0; i < 19; i++)
            {
                Assert.AreEqual(i >= 1 && i <= 17, VoxelChunkSampler.IsInterior(i),
                    $"IsInterior({i})");
            }
            Assert.IsFalse(VoxelChunkSampler.IsInterior(-1));
            Assert.IsFalse(VoxelChunkSampler.IsInterior(19));
        }

        [Test]
        public void IsApron_OnlyIndices0And18()
        {
            for (int i = 0; i < 19; i++)
            {
                Assert.AreEqual(i == 0 || i == 18, VoxelChunkSampler.IsApron(i),
                    $"IsApron({i})");
            }
            Assert.IsFalse(VoxelChunkSampler.IsApron(-1));
            Assert.IsFalse(VoxelChunkSampler.IsApron(19));
        }

        [Test]
        public void IsInterior_IsApron_PartitionCache()
        {
            // Интерьер и apron не пересекаются и покрывают весь кэш.
            for (int i = 0; i < 19; i++)
            {
                Assert.AreNotEqual(VoxelChunkSampler.IsInterior(i), VoxelChunkSampler.IsApron(i),
                    $"Index {i} обязан быть либо интерьером, либо apron.");
            }
        }

        // --- ChunkOrigin0: mapping chunkCoord → origin в вокселях LOD0 ---

        [Test]
        public void ChunkOrigin0_LOD0_IsCoordTimes16()
        {
            int3 coord = new int3(5, -3, 10);
            int3 origin = VoxelChunkSampler.ChunkOrigin0(coord, 0);
            Assert.AreEqual(coord << 4, origin, "LOD0: origin = chunkCoord << 4.");
        }

        [Test]
        public void ChunkOrigin0_LOD1_IsCoordTimes32()
        {
            int3 coord = new int3(1, 0, -2);
            Assert.AreEqual(coord << 5, VoxelChunkSampler.ChunkOrigin0(coord, 1));
        }

        [Test]
        public void ChunkOrigin0_LOD2_IsCoordTimes64()
        {
            int3 coord = new int3(-4, 2, 7);
            Assert.AreEqual(coord << 6, VoxelChunkSampler.ChunkOrigin0(coord, 2));
        }

        [Test]
        public void ChunkOrigin0_LOD6_IsCoordTimes1024()
        {
            int3 coord = new int3(1, -1, 0);
            Assert.AreEqual(coord << 10, VoxelChunkSampler.ChunkOrigin0(coord, 6));
        }

        [Test]
        public void ChunkOrigin0_LodMax_R2000_IsCoordTimes8192()
        {
            // R = 2000 → LodMax = 9: origin = coord << 13.
            int3 coord = new int3(0, 1, -3);
            Assert.AreEqual(VoxelLod.LodMax(2000), 9, "LodMax(2000) = 9 (ADR-0002).");
            Assert.AreEqual(coord << 13, VoxelChunkSampler.ChunkOrigin0(coord, 9));
        }

        [Test]
        public void ChunkOrigin0_MatchesChunkWorldSize()
        {
            // Шаг между origin соседних чанков = 16 * 2^k вокселей = 8 * 2^k метров.
            for (int lod = 0; lod <= VoxelConstants.LodHardMax; lod++)
            {
                int3 a = VoxelChunkSampler.ChunkOrigin0(new int3(1, 0, 0), lod);
                int3 b = VoxelChunkSampler.ChunkOrigin0(new int3(2, 0, 0), lod);
                int stepVoxels = b.x - a.x;
                Assert.AreEqual(16 << lod, stepVoxels,
                    $"LOD{lod}: шаг между origin в вокселях LOD0 обязан быть 16 * 2^lod.");
                Assert.AreEqual(VoxelLod.ChunkWorldSize(lod) * VoxelCoord.VoxelsPerMeter(), stepVoxels,
                    $"LOD{lod}: шаг обязан совпадать с ChunkWorldSize(lod) в вокселях LOD0.");
            }
        }

        [Test]
        public void ChunkOrigin0_ZeroCoord_IsZero()
        {
            for (int lod = 0; lod <= 10; lod++)
            {
                Assert.AreEqual(int3.zero, VoxelChunkSampler.ChunkOrigin0(int3.zero, lod));
            }
        }

        // --- Origin из ключа ---

        [Test]
        public void ChunkOrigin0_FromKey_MatchesCoordForm()
        {
            int[] lods = { 0, 1, 3, 6, 9 };
            int3[] coords =
            {
                new int3(0, 0, 0),
                new int3(5, -3, 10),
                new int3(-7, 4, -12),
            };
            foreach (int lod in lods)
            {
                foreach (int3 coord in coords)
                {
                    long key = VoxelChunkCoord.Pack(coord.x, coord.y, coord.z, lod);
                    Assert.AreEqual(
                        VoxelChunkSampler.ChunkOrigin0(coord, lod),
                        VoxelChunkSampler.ChunkOrigin0(key),
                        $"Origin из ключа не совпал для {coord} LOD{lod}.");
                }
            }
        }

        [Test]
        public void CacheIndexToVoxel_FromKey_MatchesOriginForm()
        {
            long key = VoxelChunkCoord.Pack(3, -2, 8, 2);
            int3 coord = new int3(3, -2, 8);
            int3 origin = VoxelChunkSampler.ChunkOrigin0(coord, 2);

            for (int i = 0; i < VoxelChunkSampler.CacheSize; i++)
            for (int j = 0; j < VoxelChunkSampler.CacheSize; j++)
            for (int k = 0; k < VoxelChunkSampler.CacheSize; k += 6)
            {
                Assert.AreEqual(
                    VoxelChunkSampler.VoxelFromIndices(new int3(i, j, k), origin, 2),
                    VoxelChunkSampler.VoxelFromIndices(key, new int3(i, j, k)),
                    $"Сэмпл ({i},{j},{k}) из ключа не совпал.");
            }
        }

        // --- VoxelFromIndices / AxisToVoxel: последовательность сэмплов ---

        [Test]
        public void CacheIndexToVoxel_LOD0_17InteriorSamples_Step1()
        {
            int3 origin = new int3(160, -32, 48); // coord (10, -2, 3) LOD0
            int prev = int.MinValue;
            for (int i = 0; i <= VoxelChunkSampler.CacheSize - 1; i++)
            {
                int v = VoxelChunkSampler.AxisToVoxel(i, 0, origin, 0);
                if (i > 0)
                {
                    Assert.AreEqual(prev + 1, v, $"LOD0: сэмпл {i} обязан идти через шаг 1.");
                }
                prev = v;
            }
        }

        [Test]
        public void CacheIndexToVoxel_LOD1_StepIsTwoVoxels()
        {
            int3 origin = VoxelChunkSampler.ChunkOrigin0(new int3(2, 0, -1), 1);
            for (int i = 1; i < VoxelChunkSampler.CacheSize - 1; i++)
            {
                int a = VoxelChunkSampler.AxisToVoxel(i, 0, origin, 1);
                int b = VoxelChunkSampler.AxisToVoxel(i + 1, 0, origin, 1);
                Assert.AreEqual(a + 2, b,
                    $"LOD1: соседние сэмплы обязаны идти через 2 воксела LOD0 (индекс {i}).");
            }
        }

        [Test]
        public void CacheIndexToVoxel_LOD2_StepIsFourVoxels()
        {
            int3 origin = VoxelChunkSampler.ChunkOrigin0(new int3(-1, 3, 0), 2);
            for (int i = 1; i < VoxelChunkSampler.CacheSize - 1; i++)
            {
                int a = VoxelChunkSampler.AxisToVoxel(i, 1, origin, 2);
                int b = VoxelChunkSampler.AxisToVoxel(i + 1, 1, origin, 2);
                Assert.AreEqual(a + 4, b,
                    $"LOD2: соседние сэмплы обязаны идти через 4 воксела LOD0 (индекс {i}).");
            }
        }

        [Test]
        public void CacheIndexToVoxel_AllLods_StepEqualsTwoPowLod()
        {
            for (int lod = 0; lod <= VoxelConstants.LodHardMax; lod++)
            {
                int3 origin = VoxelChunkSampler.ChunkOrigin0(new int3(1, 0, 0), lod);
                int a = VoxelChunkSampler.AxisToVoxel(1, 2, origin, lod);
                int b = VoxelChunkSampler.AxisToVoxel(2, 2, origin, lod);
                Assert.AreEqual(a + (1 << lod), b,
                    $"LOD{lod}: шаг сэмпла обязан быть 2^lod вокселей LOD0.");
            }
        }

        [Test]
        public void CacheIndexToVoxel_FirstInterior_IsOrigin()
        {
            int3[] origins = { int3.zero, new int3(100, -50, 200), new int3(-4096, 8192, -128) };
            int[] lods = { 0, 1, 3, 6, 9, 10 };
            foreach (int3 origin in origins)
            {
                foreach (int lod in lods)
                {
                    Assert.AreEqual(origin,
                        VoxelChunkSampler.VoxelFromIndices(new int3(1, 1, 1), origin, lod),
                        $"Первый интерьерный сэмпл обязан равняться origin (LOD{lod}).");
                }
            }
        }

        [Test]
        public void CacheIndexToVoxel_LastInterior_IsOriginPlus16TimesStep()
        {
            int[] lods = { 0, 1, 2, 6, 9 };
            foreach (int lod in lods)
            {
                int3 origin = VoxelChunkSampler.ChunkOrigin0(new int3(2, -1, 3), lod);
                int step16 = 16 << lod;
                Assert.AreEqual(origin + step16,
                    VoxelChunkSampler.VoxelFromIndices(
                        new int3(VoxelChunkSampler.InteriorSamples, VoxelChunkSampler.InteriorSamples, VoxelChunkSampler.InteriorSamples),
                        origin, lod),
                    $"Последний интерьерный сэмпл обязан быть origin + 16 * 2^lod (LOD{lod}).");
            }
        }

        [Test]
        public void CacheIndexToVoxel_ApronLow_IsOriginMinusStep()
        {
            int[] lods = { 0, 1, 2, 6, 9 };
            foreach (int lod in lods)
            {
                int3 origin = VoxelChunkSampler.ChunkOrigin0(new int3(1, 1, 1), lod);
                int3 expected = origin - (1 << lod);
                Assert.AreEqual(expected,
                    VoxelChunkSampler.VoxelFromIndices(int3.zero, origin, lod),
                    $"Apron low обязан быть origin - 2^lod (LOD{lod}).");
            }
        }

        [Test]
        public void CacheIndexToVoxel_ApronHigh_IsOriginPlus17TimesStep()
        {
            int[] lods = { 0, 1, 2, 6, 9 };
            foreach (int lod in lods)
            {
                int3 origin = VoxelChunkSampler.ChunkOrigin0(new int3(-1, 0, 2), lod);
                int3 expected = origin + (17 << lod);
                int3 high = new int3(
                    VoxelChunkSampler.CacheSize - 1,
                    VoxelChunkSampler.CacheSize - 1,
                    VoxelChunkSampler.CacheSize - 1);
                Assert.AreEqual(expected,
                    VoxelChunkSampler.VoxelFromIndices(high, origin, lod),
                    $"Apron high обязан быть origin + 17 * 2^lod (LOD{lod}).");
            }
        }

        [Test]
        public void CacheIndexToVoxel_LinearSequence_19Points()
        {
            // 19 сэмплов вдоль оси: от origin - 2^k до origin + 17 * 2^k, шаг 2^k.
            // Ось передаётся явно — скалярный индекс сам по себе не задаёт
            // направление смещения в 3D-кэше.
            for (int lod = 0; lod <= 3; lod++)
            {
                int3 origin = VoxelChunkSampler.ChunkOrigin0(int3.zero, lod);
                int step = 1 << lod;
                for (int axis = 0; axis < 3; axis++)
                {
                    int originComponent = axis == 0 ? origin.x : (axis == 1 ? origin.y : origin.z);
                    for (int i = 0; i < VoxelChunkSampler.CacheSize; i++)
                    {
                        int v = VoxelChunkSampler.AxisToVoxel(i, axis, origin, lod);
                        Assert.AreEqual(originComponent + (i - 1) * step, v,
                            $"LOD{lod}, ось {axis}, i={i}: неверное смещение.");
                    }
                }
            }
        }

        [Test]
        public void VoxelFromIndices_MovesOnlyAlongEachAxis()
        {
            // Регрессия: скалярный вариант транслировал сдвиг во все три оси
            // (int3 + int = broadcast), и сэмпл уезжал по диагонали.
            int3 origin = new int3(100, -50, 200);
            int3 moved = VoxelChunkSampler.VoxelFromIndices(new int3(5, 1, 1), origin, 2);

            Assert.AreEqual(origin.x + (4 << 2), moved.x, "Сдвиг обязан быть только по X.");
            Assert.AreEqual(origin.y, moved.y, "Y меняться не должен.");
            Assert.AreEqual(origin.z, moved.z, "Z меняться не должен.");
        }

        // --- Границы чанка и согласованность с соседями ---

        [Test]
        public void BoundarySample_LoD0_SharedWithNeighbor()
        {
            // Последний интерьерный сэмпл чанка C (i=17) — тот же узел,
            // что первый интерьерный сэмпл соседа C+1 (i=1).
            int3 coord = new int3(7, -4, 12);
            int3 originC = VoxelChunkSampler.ChunkOrigin0(coord, 0);
            int3 originNext = VoxelChunkSampler.ChunkOrigin0(coord + new int3(1, 0, 0), 0);

            int lastX = VoxelChunkSampler.AxisToVoxel(17, 0, originC, 0);
            int nextX = VoxelChunkSampler.AxisToVoxel(1, 0, originNext, 0);

            Assert.AreEqual(lastX, nextX, "Граничный узел обязан совпадать у обоих соседей.");
        }

        [Test]
        public void BoundarySample_AllLods_SharedWithNeighbor()
        {
            int[] lods = { 0, 1, 2, 5, 9 };
            foreach (int lod in lods)
            {
                int3 coord = new int3(-3, 5, 2);
                int3 originC = VoxelChunkSampler.ChunkOrigin0(coord, lod);
                int3 originNextX = VoxelChunkSampler.ChunkOrigin0(coord + new int3(1, 0, 0), lod);
                int3 originNextY = VoxelChunkSampler.ChunkOrigin0(coord + new int3(0, 1, 0), lod);
                int3 originNextZ = VoxelChunkSampler.ChunkOrigin0(coord + new int3(0, 0, 1), lod);

                for (int axis = 0; axis < 3; axis++)
                {
                    int3 neighborOrigin = axis == 0 ? originNextX : (axis == 1 ? originNextY : originNextZ);
                    Assert.AreEqual(
                        VoxelChunkSampler.AxisToVoxel(17, axis, originC, lod),
                        VoxelChunkSampler.AxisToVoxel(1, axis, neighborOrigin, lod),
                        $"LOD{lod}: граница по оси {axis} не совпала.");
                }
            }
        }

        [Test]
        public void Apron_LowNeighbor_CoversBoundaryDifference()
        {
            // Нормаль граничного сэмпла i=17 чанка C требует соседей
            // i=16 (интерьер C) и i=18 (apron high C): оба внутри кэша C.
            for (int lod = 0; lod <= 3; lod++)
            {
                int3 origin = VoxelChunkSampler.ChunkOrigin0(new int3(2, 0, 1), lod);
                int step = 1 << lod;

                for (int axis = 0; axis < 3; axis++)
                {
                    int center = VoxelChunkSampler.AxisToVoxel(17, axis, origin, lod);
                    int low = VoxelChunkSampler.AxisToVoxel(16, axis, origin, lod);
                    int high = VoxelChunkSampler.AxisToVoxel(18, axis, origin, lod);

                    Assert.AreEqual(center - step, low, $"LOD{lod}, ось {axis}: нижний сосед i=16.");
                    Assert.AreEqual(center + step, high, $"LOD{lod}, ось {axis}: верхний сосед i=18 (apron).");
                }
            }
        }

        [Test]
        public void Apron_HighNeighbor_CoversBoundaryDifference()
        {
            // Симметрично: для i=1 соседи i=0 (apron low) и i=2.
            for (int lod = 0; lod <= 3; lod++)
            {
                int3 origin = VoxelChunkSampler.ChunkOrigin0(new int3(-1, 3, 0), lod);
                int step = 1 << lod;

                for (int axis = 0; axis < 3; axis++)
                {
                    int center = VoxelChunkSampler.AxisToVoxel(1, axis, origin, lod);
                    int low = VoxelChunkSampler.AxisToVoxel(0, axis, origin, lod);
                    int high = VoxelChunkSampler.AxisToVoxel(2, axis, origin, lod);

                    Assert.AreEqual(center - step, low, $"LOD{lod}, ось {axis}: нижний сосед i=0 (apron).");
                    Assert.AreEqual(center + step, high, $"LOD{lod}, ось {axis}: верхний сосед i=2.");
                }
            }
        }

        [Test]
        public void Apron_CoversCentralDifference_AllInteriorSamples()
        {
            // Каждый интерьерный сэмпл i ∈ [1, 17] обязан иметь соседей
            // i-1 и i+1 внутри кэша [0, 18] (полу база в один шаг сетки).
            for (int i = 1; i <= VoxelChunkSampler.InteriorSamples; i++)
            {
                Assert.GreaterOrEqual(i - 1, 0, $"i={i}: нижний сосед вне кэша.");
                Assert.LessOrEqual(i - 1, VoxelChunkSampler.CacheSize - 1);
                Assert.GreaterOrEqual(i + 1, 0, $"i={i}: верхний сосед вне кэша.");
                Assert.LessOrEqual(i + 1, VoxelChunkSampler.CacheSize - 1);
            }
        }

        [Test]
        public void Apron_CentralDifference_IsCoveredOnEveryAxis()
        {
            // Центральная разность по любому направлению требует 6 соседей.
            // Проверяем, что для каждого интерьерного узла (i,j,k) все шесть
            // смещений лежат внутри кэша — иначе нормаль на грани получит
            // одностороннюю разность (ADR-0002, D4).
            int max = VoxelChunkSampler.CacheSize - 1;

            for (int i = 1; i <= VoxelChunkSampler.InteriorSamples; i++)
            for (int j = 1; j <= VoxelChunkSampler.InteriorSamples; j++)
            for (int k = 1; k <= VoxelChunkSampler.InteriorSamples; k++)
            {
                Assert.IsTrue(i - 1 >= 0 && i + 1 <= max, $"Узел ({i},{j},{k}): соседи по X вне кэша.");
                Assert.IsTrue(j - 1 >= 0 && j + 1 <= max, $"Узел ({i},{j},{k}): соседи по Y вне кэша.");
                Assert.IsTrue(k - 1 >= 0 && k + 1 <= max, $"Узел ({i},{j},{k}): соседи по Z вне кэша.");
            }
        }

        [Test]
        public void Apron_ExtraLayer_IsExactlyOneOnEachSide()
        {
            // Apron должен быть ровно по одному слою: 19 = 17 + 2. Если бы слоёв
            // было больше, кэш раздулся бы без пользы для полушаговой разности.
            Assert.AreEqual(
                VoxelChunkSampler.InteriorSamples + 2,
                VoxelChunkSampler.CacheSize,
                "Кэш обязан быть интерьером плюс два слоя apron.");

            // Вне диапазона [1,17] лежат ровно два индекса — 0 и 18.
            int apronCount = 0;
            for (int i = 0; i < VoxelChunkSampler.CacheSize; i++)
            {
                if (VoxelChunkSampler.IsApron(i)) apronCount++;
            }

            Assert.AreEqual(2, apronCount, "Apron обязан состоять ровно из двух индексов.");
        }

        [Test]
        public void Apron_Sample_CoversAllInteriorCentralDifferences()
        {
            // Практическая проверка на реальном буфере: для каждого интерьерного
            // узла все шесть соседей существуют в кэше и имеют валидную плотность.
            long key = VoxelChunkCoord.Pack(1, 0, -2, 0);
            var id = new IslandId(1);

            using NativeArray<short> density =
                VoxelChunkSampler.Sample(key, id, 500, Allocator.Temp);

            int n = VoxelChunkSampler.CacheSize;
            int interior = VoxelChunkSampler.InteriorSamples;

            for (int i = 1; i <= interior; i++)
            for (int j = 1; j <= interior; j++)
            for (int k = 1; k <= interior; k++)
            {
                int center = (i * n + j) * n + k;

                int[] neighbors =
                {
                    ((i - 1) * n + j) * n + k,
                    ((i + 1) * n + j) * n + k,
                    (i * n + (j - 1)) * n + k,
                    (i * n + (j + 1)) * n + k,
                    (i * n + j) * n + (k - 1),
                    (i * n + j) * n + (k + 1),
                };

                foreach (int index in neighbors)
                {
                    Assert.GreaterOrEqual(index, 0, $"Узел ({i},{j},{k}): сосед вне буфера.");
                    Assert.Less(index, density.Length, $"Узел ({i},{j},{k}): сосед вне буфера.");
                }

                Assert.GreaterOrEqual(center, 0);
                Assert.Less(center, density.Length);
            }
        }

        // --- Замер аллокаций ---

        [Test]
        public void Sample_IntoExistingBuffer_DoesNotAllocate()
        {
            // Заполнение готового буфера не должно аллоцировать: буфер создаётся
            // один раз на чанк, дальше только перезаписывается.
            long key = VoxelChunkCoord.Pack(0, 1, -1, 0);
            var id = new IslandId(1);

            using NativeArray<short> density =
                new NativeArray<short>(VoxelChunkSampler.CacheVolume, Allocator.Persistent);
            VoxelChunkSampler.Sample(key, id, 200, density);

            long bytesBefore = System.GC.GetAllocatedBytesForCurrentThread();
            for (int repeat = 0; repeat < 8; repeat++)
            {
                VoxelChunkSampler.Sample(key, id, 200, density);
            }
            long bytesAfter = System.GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(bytesBefore, bytesAfter,
                $"Заполнение готового буфера не должно выделять managed-память (было {bytesAfter - bytesBefore} байт).");
        }

        [Test]
        public void Sample_VolumeBytes_MatchShortTimes19Cubed()
        {
            // Бюджет кэша: 19³ * 2 байта = 13718 байт на чанк.
            long expectedBytes = (long)VoxelChunkSampler.CacheVolume * sizeof(short);

            using NativeArray<short> density =
                VoxelChunkSampler.Sample(
                    VoxelChunkCoord.Pack(0, 0, 0, 0),
                    new IslandId(1),
                    200,
                    Allocator.Temp);

            Assert.AreEqual(expectedBytes, density.Length * (long)sizeof(short));
            Assert.AreEqual(13718, expectedBytes, "19³ = 6859 сэмплов по 2 байта.");
        }

        // --- Отрицательные координаты (острова в -X, -Y, -Z) ---

        [Test]
        public void NegativeCoordinates_MappingStaysConsistent()
        {
            // Чанк в (-2, -1, -3) LOD1: shift = ChunkSizeLog2 + 1 = 5,
            // шаг ячейки 2 вокселя, чанк = 32 вокселя LOD0,
            // origin = (-2,-1,-3) << 5 = (-64, -32, -96).
            int3 coord = new int3(-2, -1, -3);
            int3 origin = VoxelChunkSampler.ChunkOrigin0(coord, 1);
            Assert.AreEqual(new int3(-64, -32, -96), origin);

            int3 first = VoxelChunkSampler.VoxelFromIndices(new int3(1, 1, 1), origin, 1);
            int3 last = VoxelChunkSampler.VoxelFromIndices(
                new int3(17, 17, 17), origin, 1);
            int3 apronLow = VoxelChunkSampler.VoxelFromIndices(int3.zero, origin, 1);
            int3 apronHigh = VoxelChunkSampler.VoxelFromIndices(
                new int3(18, 18, 18), origin, 1);

            Assert.AreEqual(origin, first);
            Assert.AreEqual(origin + 32, last);
            Assert.AreEqual(origin - 2, apronLow);
            Assert.AreEqual(origin + 34, apronHigh);
        }

        // --- Roundtrip: воксель → чанк ---

        [Test]
        public void InteriorSample_RoundTrip_ReturnsOwnChunk()
        {
            // Сэмплы i ∈ [1, 16] floor-делением возвращаются в свой чанк.
            // Граничный узел i = 17 — общий с соседом: floor-деление относит
            // его к следующему чанку (coord + 1), что и есть согласованность
            // границы (ADR-0002, D3).
            int[] lods = { 0, 1, 2, 6, 9 };
            foreach (int lod in lods)
            {
                int3 coord = new int3(4, -2, 9);
                int3 origin = VoxelChunkSampler.ChunkOrigin0(coord, lod);
                int shift = VoxelConstants.ChunkSizeLog2 + lod;

                foreach (int i in new[] { 1, 8, 16 })
                {
                    int3 voxel = VoxelChunkSampler.VoxelFromIndices(new int3(i, i, i), origin, lod);
                    int3 back = voxel >> shift;
                    Assert.AreEqual(coord, back,
                        $"LOD{lod} i={i}: воксель {voxel} обязан вернуться в чанк {coord}.");
                }

                int3 lastVoxel = VoxelChunkSampler.VoxelFromIndices(
                    new int3(VoxelChunkSampler.InteriorSamples, VoxelChunkSampler.InteriorSamples, VoxelChunkSampler.InteriorSamples),
                    origin, lod);
                Assert.AreEqual(coord + 1, lastVoxel >> shift,
                    $"LOD{lod}: граничный узел i=17 обязан принадлежать соседу {coord + 1}.");
            }
        }

        [Test]
        public void ApronSamples_BelongToAdjacentChunk()
        {
            // Apron low обязан принадлежать предыдущему чанку (при origin > 0),
            // apron high — следующему. Это проверяет, что apron — ровно по одному.
            int[] lods = { 0, 2, 5 };
            foreach (int lod in lods)
            {
                int3 coord = new int3(3, 1, 2);
                int3 origin = VoxelChunkSampler.ChunkOrigin0(coord, lod);
                int shift = VoxelConstants.ChunkSizeLog2 + lod;

                int3 backLow = VoxelChunkSampler.VoxelFromIndices(int3.zero, origin, lod) >> shift;
                int3 backHigh = VoxelChunkSampler.VoxelFromIndices(
                    new int3(18, 18, 18), origin, lod) >> shift;

                Assert.AreEqual(coord - 1, backLow,
                    $"LOD{lod}: apron low обязан принадлежать чанку {coord - 1}.");
                Assert.AreEqual(coord + 1, backHigh,
                    $"LOD{lod}: apron high обязан принадлежать чанку {coord + 1}.");
            }
        }

        [Test]
        public void ApronAtNegativeCoordinate_FloorsToPreviousChunk()
        {
            // Чанк (0, 0, 0) LOD0: apron low = воксель -1.
            // C#-сдвиг -1 >> 4 = -1 (floor-деление для степеней двойки) —
            // apron попадает в чанк (-1, -1, -1), как и положено.
            int3 origin = VoxelChunkSampler.ChunkOrigin0(int3.zero, 0);
            int3 backLow = VoxelChunkSampler.VoxelFromIndices(int3.zero, origin, 0) >> 4;
            Assert.AreEqual(new int3(-1, -1, -1), backLow,
                "Apron low чанка в нуле обязан принадлежать чанку (-1, -1, -1).");
        }

        // --- Sample: плотность 19³ ---

        [Test]
        public void Sample_ProducesCacheVolumeSamples()
        {
            long key = VoxelChunkCoord.Pack(0, 0, 0, 0);
            using NativeArray<short> density =
                VoxelChunkSampler.Sample(key, new IslandId(1), 200, Allocator.Temp);

            Assert.AreEqual(VoxelChunkSampler.CacheVolume, density.Length);
            Assert.AreEqual(19 * 19 * 19, density.Length);
        }

        [Test]
        public void Sample_MatchesSampleOne_AtAllCorners()
        {
            // Крайние индексы кэша — точки, где легче всего ошибиться в сдвигах.
            long key = VoxelChunkCoord.Pack(2, -1, 3, 0);
            var id = new IslandId(1);

            using NativeArray<short> density =
                VoxelChunkSampler.Sample(key, id, 200, Allocator.Temp);

            int[] corners = { 0, 18 };
            foreach (int i in corners)
            foreach (int j in corners)
            foreach (int k in corners)
            {
                int index = (i * 19 + j) * 19 + k;
                Assert.AreEqual(
                    VoxelChunkSampler.SampleOne(key, i, j, k, id, 200),
                    density[index],
                    $"Сэмпл ({i},{j},{k}) обязан совпасть с точечной выборкой.");
            }
        }

        [Test]
        public void Sample_MatchesSampleOne_AtCenterAndInterior()
        {
            long key = VoxelChunkCoord.Pack(-4, 2, -7, 0);
            var id = new IslandId(5);

            using NativeArray<short> density =
                VoxelChunkSampler.Sample(key, id, 500, Allocator.Temp);

            for (int i = 1; i <= VoxelChunkSampler.InteriorSamples; i += 8)
            for (int j = 1; j <= VoxelChunkSampler.InteriorSamples; j += 8)
            for (int k = 1; k <= VoxelChunkSampler.InteriorSamples; k += 8)
            {
                int index = (i * 19 + j) * 19 + k;
                Assert.AreEqual(
                    VoxelChunkSampler.SampleOne(key, i, j, k, id, 500),
                    density[index],
                    $"Интерьерный сэмпл ({i},{j},{k}) обязан совпасть.");
            }
        }

        [Test]
        public void Sample_IndexOrder_IsXyzMajor()
        {
            // Порядок XYZ-мажорный: соседние по z индексы различаются на 1,
            // по y — на CacheSize, по x — на CacheSize².
            long key = VoxelChunkCoord.Pack(1, 1, 1, 0);
            var id = new IslandId(1);

            using NativeArray<short> density =
                VoxelChunkSampler.Sample(key, id, 200, Allocator.Temp);

            int i = 5, j = 6, k = 7;
            int baseIndex = (i * 19 + j) * 19 + k;

            Assert.AreEqual(VoxelChunkSampler.SampleOne(key, i, j, k + 1, id, 200),
                density[baseIndex + 1], "Сосед по z — смещение 1.");
            Assert.AreEqual(VoxelChunkSampler.SampleOne(key, i, j + 1, k, id, 200),
                density[baseIndex + 19], "Сосед по y — смещение CacheSize.");
            Assert.AreEqual(VoxelChunkSampler.SampleOne(key, i + 1, j, k, id, 200),
                density[baseIndex + 19 * 19], "Сосед по x — смещение CacheSize².");
        }

        [Test]
        public void Sample_GradientAcrossDensity_ChangesSmoothly()
        {
            // Вдоль вертикали внутри коры плотность обязана меняться плавно:
            // шаг SDF между соседними сэмплами ограничен (наклон конуса < 1).
            long key = VoxelChunkCoord.Pack(0, 0, 0, 0);
            var id = new IslandId(1);

            using NativeArray<short> density =
                VoxelChunkSampler.Sample(key, id, 200, Allocator.Temp);

            int i = 9, k = 9;
            for (int j = 1; j < VoxelChunkSampler.CacheSize; j++)
            {
                int a = density[(i * 19 + (j - 1)) * 19 + k];
                int b = density[(i * 19 + j) * 19 + k];
                Assert.LessOrEqual(math.abs(b - a), 4 * VoxelConstants.DensityUnitsPerVoxel,
                    $"Скачок плотности между слоями j={j - 1} и j={j} слишком велик.");
            }
        }

        [Test]
        public void Sample_WrongBufferLength_Throws()
        {
            long key = VoxelChunkCoord.Pack(0, 0, 0, 0);
            using NativeArray<short> wrong = new NativeArray<short>(10, Allocator.Temp);

            Assert.Throws<System.ArgumentException>(() =>
                VoxelChunkSampler.Sample(key, new IslandId(1), 200, wrong));
        }

        [Test]
        public void Sample_AllLods_Succeeds()
        {
            // Сдвиги LOD не должны ломать выборку ни на одном уровне.
            var id = new IslandId(1);
            foreach (int lod in new[] { 0, 1, 2, 5, 9 })
            {
                long key = VoxelChunkCoord.Pack(1, 0, -1, lod);
                using NativeArray<short> density =
                    VoxelChunkSampler.Sample(key, id, 2000, Allocator.Temp);

                Assert.AreEqual(VoxelChunkSampler.CacheVolume, density.Length,
                    $"LOD{lod}: объём кэша обязан быть 19³.");
                Assert.AreEqual(
                    VoxelChunkSampler.SampleOne(key, 1, 1, 1, id, 2000),
                    density[(19 + 1) * 19 + 1],
                    $"LOD{lod}: первый интерьерный сэмпл обязан совпасть.");
            }
        }

        // --- SampleVoxelChunkJob: Burst-джоба ---

        [Test]
        public void Job_MatchesMainThreadSample_Bitwise()
        {
            // Джоба обязана дать побитно тот же буфер, что главный поток:
            // она вызывает тот же SampleCore, но прогоняется через Burst.
            long key = VoxelChunkCoord.Pack(-3, 2, 5, 0);
            var id = new IslandId(4);

            using NativeArray<short> expected =
                VoxelChunkSampler.Sample(key, id, 500, Allocator.Temp);
            using NativeArray<short> actual =
                new NativeArray<short>(VoxelChunkSampler.CacheVolume, Allocator.TempJob);

            new SampleVoxelChunkJob
            {
                ChunkKey = key,
                IslandId = id,
                Radius = 500,
                Density = actual
            }.Run();

            for (int n = 0; n < VoxelChunkSampler.CacheVolume; n++)
            {
                Assert.AreEqual(expected[n], actual[n], $"Расхождение джобы и Sample в индексе {n}.");
            }
        }

        [Test]
        public void Job_IsDeterministic_AcrossRuns()
        {
            long key = VoxelChunkCoord.Pack(6, -2, 1, 1);
            var id = new IslandId(9);

            using NativeArray<short> first =
                new NativeArray<short>(VoxelChunkSampler.CacheVolume, Allocator.TempJob);
            using NativeArray<short> second =
                new NativeArray<short>(VoxelChunkSampler.CacheVolume, Allocator.TempJob);

            for (int run = 0; run < 2; run++)
            {
                new SampleVoxelChunkJob
                {
                    ChunkKey = key,
                    IslandId = id,
                    Radius = 2000,
                    Density = run == 0 ? first : second
                }.Run();
            }

            for (int n = 0; n < VoxelChunkSampler.CacheVolume; n++)
            {
                Assert.AreEqual(first[n], second[n], $"Недетерминизм джобы в индексе {n}.");
            }
        }

        [Test]
        public void Job_AllLods_MatchesMainThread()
        {
            var id = new IslandId(2);
            foreach (int lod in new[] { 0, 1, 3, 6 })
            {
                long key = VoxelChunkCoord.Pack(2, 0, -3, lod);

                using NativeArray<short> expected =
                    VoxelChunkSampler.Sample(key, id, 2000, Allocator.Temp);
                using NativeArray<short> actual =
                    new NativeArray<short>(VoxelChunkSampler.CacheVolume, Allocator.TempJob);

                new SampleVoxelChunkJob
                {
                    ChunkKey = key,
                    IslandId = id,
                    Radius = 2000,
                    Density = actual
                }.Run();

                for (int n = 0; n < VoxelChunkSampler.CacheVolume; n++)
                {
                    Assert.AreEqual(expected[n], actual[n], $"LOD{lod}: расхождение в индексе {n}.");
                }
            }
        }

        [Test]
        public void Job_SchedulePath_CompletesWithSameResult()
        {
            // Проверяем именно параллельный путь (Schedule + Complete), а не Run:
            // именно так джоба будет использоваться в стриминге.
            long key = VoxelChunkCoord.Pack(0, 0, 0, 0);
            var id = new IslandId(1);

            using NativeArray<short> expected =
                VoxelChunkSampler.Sample(key, id, 200, Allocator.Temp);
            using NativeArray<short> actual =
                new NativeArray<short>(VoxelChunkSampler.CacheVolume, Allocator.TempJob);

            JobHandle handle = new SampleVoxelChunkJob
            {
                ChunkKey = key,
                IslandId = id,
                Radius = 200,
                Density = actual
            }.Schedule();
            handle.Complete();

            for (int n = 0; n < VoxelChunkSampler.CacheVolume; n++)
            {
                Assert.AreEqual(expected[n], actual[n], $"Schedule: расхождение в индексе {n}.");
            }
        }

        [Test]
        public void Job_IntoExistingBuffer_DoesNotAllocateOnRepeat()
        {
            // Повторный прогон джобы в тот же буфер не должен выделять
            // managed-память: буфер для стриминга переиспользуется.
            long key = VoxelChunkCoord.Pack(-1, 2, 3, 0);
            var id = new IslandId(1);

            using NativeArray<short> density =
                new NativeArray<short>(VoxelChunkSampler.CacheVolume, Allocator.Persistent);

            var job = new SampleVoxelChunkJob
            {
                ChunkKey = key,
                IslandId = id,
                Radius = 500,
                Density = density
            };

            job.Run();

            long bytesBefore = System.GC.GetAllocatedBytesForCurrentThread();
            for (int repeat = 0; repeat < 8; repeat++)
            {
                job.Run();
            }
            long bytesAfter = System.GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(bytesBefore, bytesAfter,
                $"Повторный прогон джобы не должен выделять managed-память (было {bytesAfter - bytesBefore} байт).");
        }

        // --- P3.5: согласованность границ apron соседа A с интерьером B ---
        [Test]
        public void ApronHigh_MatchesNeighborInterior_AlongX()
        {
            // Apron high чанка A (i=18) — это узел, следующий за последним
            // интерьерным. У соседа B по +X тот же узел лежит внутри интерьера
            // под индексом i=2 (первый узел после своего origin).
            AssertApronHighMatchesNeighborInterior(
                new int3(1, 0, 0), /* neighborOffset */ new int3(1, 0, 0), 0);
        }

        [Test]
        public void ApronHigh_MatchesNeighborInterior_AllAxes()
        {
            int3[] neighborOffsets =
            {
                new int3(1, 0, 0),
                new int3(0, 1, 0),
                new int3(0, 0, 1),
            };

            foreach (int3 offset in neighborOffsets)
            {
                AssertApronHighMatchesNeighborInterior(new int3(1, 0, 0), offset, 0);
            }
        }

        [Test]
        public void ApronHigh_MatchesNeighborInterior_AllLods()
        {
            foreach (int lod in new[] { 0, 1, 2, 5, 9 })
            {
                int3[] neighborOffsets =
                {
                    new int3(1, 0, 0),
                    new int3(0, 1, 0),
                    new int3(0, 0, 1),
                };

                foreach (int3 offset in neighborOffsets)
                {
                    AssertApronHighMatchesNeighborInterior(new int3(2, -1, 3), offset, lod);
                }
            }
        }

        [Test]
        public void ApronLow_MatchesNeighborInterior_AllAxes()
        {
            // Зеркально: apron low A (i=0) — узел перед первым интерьерным.
            // У соседа B по −X тот же узел лежит внутри интерьера под i=16.
            foreach (int lod in new[] { 0, 1, 3 })
            {
                int3[] neighborOffsets =
                {
                    new int3(-1, 0, 0),
                    new int3(0, -1, 0),
                    new int3(0, 0, -1),
                };

                foreach (int3 offset in neighborOffsets)
                {
                    AssertApronLowMatchesNeighborInterior(new int3(2, -1, 3), offset, lod);
                }
            }
        }

        [Test]
        public void Apron_SharedSurface_IsBitwiseIdentical()
        {
            // Соседи обязаны вычислить один и тот же SDF в общей точке:
            // функция чистая, от чанка не зависит. Проверяем побитно на всех
            // 17 общих узлах грани X при LOD0.
            int3 coord = new int3(-2, 1, 4);
            int radius = 500;
            var id = new IslandId(7);

            long keyA = VoxelChunkCoord.Pack(coord.x, coord.y, coord.z, 0);
            long keyB = VoxelChunkCoord.Pack(coord.x + 1, coord.y, coord.z, 0);

            using NativeArray<short> densityA =
                VoxelChunkSampler.Sample(keyA, id, radius, Allocator.Temp);
            using NativeArray<short> densityB =
                VoxelChunkSampler.Sample(keyB, id, radius, Allocator.Temp);

            // Грань x: у A это i = 17, у B это i = 1.
            for (int j = 0; j < VoxelChunkSampler.CacheSize; j++)
            for (int k = 0; k < VoxelChunkSampler.CacheSize; k++)
            {
                short a = densityA[(17 * 19 + j) * 19 + k];
                short b = densityB[(1 * 19 + j) * 19 + k];
                Assert.AreEqual(a, b, $"Грань X, узел (j={j}, k={k}): значения обязаны совпасть.");
            }
        }

        private static void AssertApronHighMatchesNeighborInterior(
            int3 baseCoord,
            int3 neighborOffset,
            int lod)
        {
            int radius = 500;
            var id = new IslandId(3);

            int3 coordA = baseCoord;
            int3 coordB = baseCoord + neighborOffset;

            long keyA = VoxelChunkCoord.Pack(coordA.x, coordA.y, coordA.z, lod);
            long keyB = VoxelChunkCoord.Pack(coordB.x, coordB.y, coordB.z, lod);

            int axis = neighborOffset.x != 0 ? 0 : (neighborOffset.y != 0 ? 1 : 2);
            int sign = neighborOffset.x + neighborOffset.y + neighborOffset.z;

            for (int t = 0; t < VoxelChunkSampler.InteriorSamples; t += 4)
            {
                // У A apron high лежит по той же оси; две другие координаты
                // берём как интерьерные индексы, чтобы точки совпали.
                int aHigh = VoxelChunkSampler.CacheSize - 1;
                int bInterior = 2;
                int other1 = t + 1;
                int other2 = VoxelChunkSampler.InteriorSamples - t;

                int3 ia = IndexOnAxis(axis, aHigh, other1, other2);
                int3 ib = IndexOnAxis(axis, bInterior, other1, other2);

                int3 voxelA = IndexToVoxel(keyA, ia, lod);
                int3 voxelB = IndexToVoxel(keyB, ib, lod);

                Assert.AreEqual(voxelA, voxelB,
                    $"LOD{lod}, ось {axis}, sign {sign}: apron high A обязан совпасть с интерьером B.");

                Assert.AreEqual(
                    VoxelChunkSampler.SampleOne(keyA, ia.x, ia.y, ia.z, id, radius),
                    VoxelChunkSampler.SampleOne(keyB, ib.x, ib.y, ib.z, id, radius),
                    $"LOD{lod}, ось {axis}: плотность в общей точке обязана совпасть.");
            }
        }

        private static void AssertApronLowMatchesNeighborInterior(
            int3 baseCoord,
            int3 neighborOffset,
            int lod)
        {
            int radius = 500;
            var id = new IslandId(3);

            int3 coordA = baseCoord;
            int3 coordB = baseCoord + neighborOffset;

            long keyA = VoxelChunkCoord.Pack(coordA.x, coordA.y, coordA.z, lod);
            long keyB = VoxelChunkCoord.Pack(coordB.x, coordB.y, coordB.z, lod);

            int axis = neighborOffset.x != 0 ? 0 : (neighborOffset.y != 0 ? 1 : 2);

            for (int t = 0; t < VoxelChunkSampler.InteriorSamples; t += 4)
            {
                int aLow = 0;
                int bInterior = VoxelChunkSampler.InteriorSamples - 1;
                int other1 = t + 1;
                int other2 = VoxelChunkSampler.InteriorSamples - t;

                int3 ia = IndexOnAxis(axis, aLow, other1, other2);
                int3 ib = IndexOnAxis(axis, bInterior, other1, other2);

                int3 voxelA = IndexToVoxel(keyA, ia, lod);
                int3 voxelB = IndexToVoxel(keyB, ib, lod);

                Assert.AreEqual(voxelA, voxelB,
                    $"LOD{lod}, ось {axis}: apron low A обязан совпасть с интерьером B.");

                Assert.AreEqual(
                    VoxelChunkSampler.SampleOne(keyA, ia.x, ia.y, ia.z, id, radius),
                    VoxelChunkSampler.SampleOne(keyB, ib.x, ib.y, ib.z, id, radius),
                    $"LOD{lod}, ось {axis}: плотность в общей точке обязана совпасть.");
            }
        }

        private static int3 IndexOnAxis(int axis, int axisIndex, int other1, int other2)
        {
            switch (axis)
            {
                case 0: return new int3(axisIndex, other1, other2);
                case 1: return new int3(other1, axisIndex, other2);
                default: return new int3(other1, other2, axisIndex);
            }
        }

        private static int3 IndexToVoxel(long chunkKey, int3 cacheIndex, int lod)
        {
            return VoxelChunkSampler.VoxelFromIndices(chunkKey, cacheIndex);
        }
    }
}
