using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Упаковка island-local координат чанка и LOD в 64-битный ключ (ADR-0002, D8).
    /// </summary>
    /// <remarks>
    /// Формат ключа: биты 0…11 — <c>x + 2048</c>, биты 12…23 — <c>y + 2048</c>,
    /// биты 24…35 — <c>z + 2048</c>, биты 36…39 — LOD (0…15), бит 63 — флаг валидности.
    /// Флаг в старшем бите гарантирует, что валидный ключ никогда не равен нулю
    /// (правило «0 означает невалидно»). Четыре бита на LOD обязательны: число уровней
    /// зависит от радиуса и доходит до <c>LodMax(RadiusMax) = 9</c>.
    /// Упаковка целочисленная, без float и без Morton через float.
    /// </remarks>
    public static class VoxelChunkCoord
    {
        /// <summary>Невалидный ключ: все нули, флаг валидности сброшен.</summary>
        public const long InvalidKey = 0;

        private const int CoordinateBits = 12;
        private const int CoordinateBias = 1 << (CoordinateBits - 1);  // 2048: x + 2048 ∈ [0, 4095]
        private const long CoordinateMask = (1L << CoordinateBits) - 1;  // 0xFFF
        private const int LodShift = 36;
        private const int LodBits = 4;
        private const long LodMask = (1L << LodBits) - 1;
        private const long ValidFlag = 1L << 63;

        /// <summary>
        /// Упаковать координаты чанка (−2048…2047 на каждом axis) и LOD (0…15) в ключ.
        /// </summary>
        /// <remarks>
        /// Координаты вне диапазона маскируются младшими битами — поведение на переполнении
        /// не определено, валидность входных координат обязана проверяться вызывающим
        /// кодом (для островов диапазона ADR запас ±2048 чанков LOD0 покрывает радиус 16 км).
        /// </remarks>
        public static long Pack(int x, int y, int z, int lod)
        {
            long packedX = (x + CoordinateBias) & CoordinateMask;
            long packedY = (y + CoordinateBias) & CoordinateMask;
            long packedZ = (z + CoordinateBias) & CoordinateMask;
            long packedLod = lod & LodMask;

            return ValidFlag | (packedLod << LodShift) | (packedZ << 24) | (packedY << 12) | packedX;
        }

        /// <summary>Извлечь X (−2048…2047) из ключа.</summary>
        public static int UnpackX(long key)
        {
            return (int)(key & CoordinateMask) - CoordinateBias;
        }

        /// <summary>Извлечь Y (−2048…2047) из ключа.</summary>
        public static int UnpackY(long key)
        {
            return (int)((key >> 12) & CoordinateMask) - CoordinateBias;
        }

        /// <summary>Извлечь Z (−2048…2047) из ключа.</summary>
        public static int UnpackZ(long key)
        {
            return (int)((key >> 24) & CoordinateMask) - CoordinateBias;
        }

        /// <summary>Извлечь LOD (0…15) из ключа.</summary>
        public static int UnpackLod(long key)
        {
            return (int)((key >> LodShift) & LodMask);
        }

        /// <summary>
        /// Проверить флаг валидности (бит 63). <see cref="InvalidKey"/> невалиден всегда.
        /// </summary>
        public static bool IsValid(long key)
        {
            return (key & ValidFlag) != 0;
        }

        /// <summary>
        /// Ключ чанка уровня lod, содержащего воксельную координату LOD0.
        /// </summary>
        /// <remarks>
        /// На уровне lod чанк содержит 16 ячеек с шагом 0,5 * 2^lod, поэтому из LOD0-координат
        /// координата чанка получается арифметическим сдвигом на ChunkSizeLog2 + lod.
        /// </remarks>
        public static long PackFromVoxel(int3 voxelCoord, int lod)
        {
            int shift = VoxelConstants.ChunkSizeLog2 + lod;
            return Pack(voxelCoord.x >> shift, voxelCoord.y >> shift, voxelCoord.z >> shift, lod);
        }
    }
}
