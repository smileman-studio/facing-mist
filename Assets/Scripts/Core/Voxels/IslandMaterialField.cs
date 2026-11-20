using SmilemanStudio.Core;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Процедурное поле материала острова (ADR-0002, D7).
    /// </summary>
    /// <remarks>
    /// Материал в v1 полностью процедурный: чистая функция от island-local
    /// позиции и seed острова. В кэше и запечённом чанке материал не хранится —
    /// вычисляется на лету.
    ///
    /// Алгоритм: argmax четырёх независимых Fbm3D-полей (Dirt, Coal, Sand, Quartz).
    /// Каждое поле имеет собственный seed (основной seed острова ^ смещение),
    /// поэтому четыре поля статистически независимы.
    ///
    /// Клетка материала = 32 м (MATERIAL_SCALE = 65536 / 32 = 2048).
    /// Целочисленный пайплайн (ADR D6): только int-арифметика, без float.
    /// </remarks>
    public static class IslandMaterialField
    {
        /// <summary>Масштаб координат для 3D-шума: одна клетка = 32 м.</summary>
        private const int MaterialScale = (1 << 16) / 32;

        /// <summary>Базовое смещение seed для материала Dirt.</summary>
        private const uint DirtSeedOffset = 0x12345678;

        /// <summary>Шаг между seed-смещениями соседних материалов.</summary>
        private const uint MaterialSeedStep = 0x9E3779B9;

        /// <summary>
        /// Материал в точке (x, y, z) острова.
        /// </summary>
        /// <remarks>
        /// Возвращает аргмакс четырёх Fbm3D-полей.
        /// Порядок enum: Dirt=1, Coal=2, Sand=3, Quartz=4.
        /// При равенстве значений побеждает материал с меньшим индексом (Dirt).
        /// </remarks>
        public static VoxelMaterial At(int x, int y, int z, IslandId id)
        {
            uint seed = VoxelNoise.Hash((uint)id.Value);
            int mx = x * MaterialScale;
            int my = y * MaterialScale;
            int mz = z * MaterialScale;

            int best = (int)VoxelMaterial.Dirt;
            int bestValue = VoxelNoise.Fbm3D(mx, my, mz, seed ^ DirtSeedOffset);

            for (int i = 2; i <= 4; i++)
            {
                uint matSeed = seed ^ (DirtSeedOffset + (uint)i * MaterialSeedStep);
                int v = VoxelNoise.Fbm3D(mx, my, mz, matSeed);
                if (v > bestValue)
                {
                    bestValue = v;
                    best = i;
                }
            }

            return (VoxelMaterial)best;
        }
    }
}
