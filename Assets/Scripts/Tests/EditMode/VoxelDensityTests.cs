using NUnit.Framework;
using SmilemanStudio.Core.Voxels;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты квантования и интерполяции плотности <see cref="VoxelDensity"/> (ADR-0002, D4).
    /// </summary>
    public class VoxelDensityTests
    {
        [Test]
        public void Quantize_AtZero_IsZero()
        {
            Assert.AreEqual((short)0, VoxelDensity.Quantize(0.0f),
                "Нулевая плотность = ноль.");
        }

        [Test]
        public void Quantize_AtHalfVoxel_Is128()
        {
            // 0,5 шага = 0,5 * 256 = 128.
            Assert.AreEqual((short)128, VoxelDensity.Quantize(0.5f),
                "Полшага = 128 единиц.");
        }

        [Test]
        public void Quantize_AtOneStep_Is256()
        {
            Assert.AreEqual((short)256, VoxelDensity.Quantize(1.0f),
                "Один шаг = 256 единиц.");
        }

        [Test]
        public void Quantize_AtNegativeHalf_IsMinus128()
        {
            // -0,5 * 256 + 0,5 = -128 + 0,5 = -127,5 → floor = -128.
            Assert.AreEqual((short)(-128), VoxelDensity.Quantize(-0.5f),
                "Минус полшага = -128 единиц.");
        }

        [Test]
        public void Quantize_ClampsToShortMax()
        {
            // 128 шагов = 32768 единиц, что = short.MaxValue + 1 → кламп на short.MaxValue.
            Assert.AreEqual(short.MaxValue, VoxelDensity.Quantize(128.0f),
                "Значение выше short.MaxValue должно клампиться.");
        }

        [Test]
        public void Quantize_ClampsToShortMin()
        {
            Assert.AreEqual(short.MinValue, VoxelDensity.Quantize(-128.0f),
                "Значение ниже short.MinValue должно клампиться.");
        }

        [Test]
        public void Quantize_RoundTrip_WithinShortRange()
        {
            // Значения внутри [-128, 127] шагов → roundtrip: Quantize(d) / 256.0f ≈ d.
            for (float d = -127.0f; d <= 127.0f; d += 1.0f)
            {
                short q = VoxelDensity.Quantize(d);
                float recovered = (float)q / VoxelConstants.DensityUnitsPerVoxel;
                Assert.AreEqual(d, recovered, 0.004f,
                    "Roundtrip для d = " + d);
            }
        }

        [Test]
        public void InterpolateEdgeT_BothZero_ReturnsHalf()
        {
            // sum == 0 → пересечения нет, возвращаем середину ребра (32768).
            Assert.AreEqual(32768, VoxelDensity.InterpolateEdgeT(0, 0),
                "Обе нулевые — середина ребра.");
        }

        [Test]
        public void InterpolateEdgeT_D0ZeroD1Positive_ReturnsZero()
        {
            // |d0|=0, sum=|d1| → t=0: пересечение на точке d0.
            Assert.AreEqual(0, VoxelDensity.InterpolateEdgeT(0, 100),
                "d0=0 → t=0 (пересечение на d0).");
        }

        [Test]
        public void InterpolateEdgeT_D0NegativeD1Zero_ReturnsOne()
        {
            // |d0|=50, sum=50 → t=(50<<16)/50 = 65536: пересечение на d1.
            Assert.AreEqual(65536, VoxelDensity.InterpolateEdgeT(-50, 0),
                "d1=0 → t=65536 (пересечение на d1).");
        }

        [Test]
        public void InterpolateEdgeT_EqualMagnitudes_ReturnsHalf()
        {
            // |d0|=|d1|=100, sum=200 → t=(100<<16)/200 = 32768.
            Assert.AreEqual(32768, VoxelDensity.InterpolateEdgeT(-100, 100),
                "Равные модули → середина ребра.");
        }

        [Test]
        public void InterpolateEdgeT_Q1AtQ3_ReturnsQuarter()
        {
            // |d0|=1, |d1|=3, sum=4 → t=(1<<16)/4 = 16384 = 0,25 шага.
            Assert.AreEqual(16384, VoxelDensity.InterpolateEdgeT(-1, 3),
                "d0 в 4 раза меньше d1 → t = 1/4.");
        }

        [Test]
        public void InterpolateEdgeT_IsSymmetricForSigns()
        {
            // Знак d0 и d1 не влияет на модули в формуле.
            int t1 = VoxelDensity.InterpolateEdgeT(-100, 100);
            int t2 = VoxelDensity.InterpolateEdgeT(100, -100);
            Assert.AreEqual(t1, t2,
                "Формула симметрична по модулям.");
        }

        [Test]
        public void InterpolateEdgeT_ResultInRange()
        {
            // Для любых nonzero d0, d1: t ∈ [0, 65536].
            short[] vals = { -256, -1, 0, 1, 256 };

            foreach (short d0 in vals)
            foreach (short d1 in vals)
            {
                int t = VoxelDensity.InterpolateEdgeT(d0, d1);
                Assert.GreaterOrEqual(t, 0);
                Assert.LessOrEqual(t, 65536);
            }
        }
    }
}
