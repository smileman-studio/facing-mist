using NUnit.Framework;
using SmilemanStudio.Core.Voxels;
using Unity.Mathematics;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты упаковки ключей чанков <see cref="VoxelChunkCoord"/> (ADR-0002, D8).
    /// </summary>
    public class VoxelChunkCoordTests
    {
        [Test]
        public void InvalidKey_IsZero()
        {
            Assert.AreEqual(0L, VoxelChunkCoord.InvalidKey,
                "Невалидный ключ обязан быть нулём (правило «0 означает невалидно»).");
        }

        [Test]
        public void IsValid_ForInvalidKey_ReturnsFalse()
        {
            Assert.IsFalse(VoxelChunkCoord.IsValid(VoxelChunkCoord.InvalidKey),
                "Нулевой ключ всегда невалиден.");
        }

        [Test]
        public void IsValid_ForPackedKey_ReturnsTrue()
        {
            long key = VoxelChunkCoord.Pack(0, 0, 0, 0);
            Assert.IsTrue(VoxelChunkCoord.IsValid(key),
                "Упакованный ключ обязан быть валидным.");
        }

        [Test]
        public void Pack_AtOrigin_IsKnownGoldenValue()
        {
            // Pack(0,0,0,0): x=y=z=2048, lod=0, valid=1.
            // 0x8000_0000_8080_0800 = -9223372002486646784.
            long key = VoxelChunkCoord.Pack(0, 0, 0, 0);
            Assert.AreEqual(-9223372002486646784L, key,
                "Золотое значение ключа (0,0,0,LOD0).");
        }

        [Test]
        public void Pack_AtUnit_IsKnownGoldenValue()
        {
            // Pack(1,1,1,0): x=y=z=2049, lod=0, valid=1.
            long key = VoxelChunkCoord.Pack(1, 1, 1, 0);
            Assert.AreEqual(-9223372002469865471L, key,
                "Золотое значение ключа (1,1,1,LOD0).");
        }

        [Test]
        public void Pack_AtNegativeOne_IsKnownGoldenValue()
        {
            // Pack(-1,-1,-1,0): x=y=z=2047, lod=0, valid=1.
            long key = VoxelChunkCoord.Pack(-1, -1, -1, 0);
            Assert.AreEqual(-9223372002503428097L, key,
                "Золотое значение ключа (-1,-1,-1,LOD0).");
        }

        [Test]
        public void Pack_WithLod5_IsKnownGoldenValue()
        {
            // Pack(0,0,0,5): lod=5 сдвигается на бит 36.
            long key = VoxelChunkCoord.Pack(0, 0, 0, 5);
            Assert.AreEqual(-9223371658889263104L, key,
                "Золотое значение ключа (0,0,0,LOD5).");
        }

        [Test]
        public void RoundTrip_AllAxes_IsIdentity()
        {
            int[] coords = { 0, 1, -1, 100, -100, 2047, -2048 };

            foreach (int x in coords)
            foreach (int y in coords)
            foreach (int z in coords)
            {
                long key = VoxelChunkCoord.Pack(x, y, z, 0);
                Assert.AreEqual(x, VoxelChunkCoord.UnpackX(key),
                    "X не совпал для (" + x + "," + y + "," + z + ")");
                Assert.AreEqual(y, VoxelChunkCoord.UnpackY(key),
                    "Y не совпал для (" + x + "," + y + "," + z + ")");
                Assert.AreEqual(z, VoxelChunkCoord.UnpackZ(key),
                    "Z не совпал для (" + x + "," + y + "," + z + ")");
            }
        }

        [Test]
        public void RoundTrip_Lod_IsIdentity()
        {
            for (int lod = 0; lod <= 15; lod++)
            {
                long key = VoxelChunkCoord.Pack(0, 0, 0, lod);
                Assert.AreEqual(lod, VoxelChunkCoord.UnpackLod(key),
                    "LOD не совпал для " + lod);
            }
        }

        [Test]
        public void Pack_DifferentLods_ProduceDifferentKeys()
        {
            long key0 = VoxelChunkCoord.Pack(5, 5, 5, 0);
            long key1 = VoxelChunkCoord.Pack(5, 5, 5, 1);
            long key9 = VoxelChunkCoord.Pack(5, 5, 5, 9);

            Assert.AreNotEqual(key0, key1, "LOD0 и LOD1 должны давать разные ключи.");
            Assert.AreNotEqual(key0, key9, "LOD0 и LOD9 должны давать разные ключи.");
        }

        [Test]
        public void PackFromVoxel_AtChunkOrigin_MatchesPack()
        {
            // Voxel (0,0,0) LOD0 -> chunk (0,0,0) LOD0.
            long expected = VoxelChunkCoord.Pack(0, 0, 0, 0);
            long actual = VoxelChunkCoord.PackFromVoxel(new int3(0, 0, 0), 0);
            Assert.AreEqual(expected, actual);
        }

        [Test]
        public void PackFromVoxel_AtChunkBoundary_SameChunk()
        {
            // LOD0: чанк содержит 16 вокселей; воксель 15 — последний в чанке 0,
            // воксель 16 — первый в чанке 1.
            long chunk0 = VoxelChunkCoord.PackFromVoxel(new int3(15, 0, 0), 0);
            long chunk1 = VoxelChunkCoord.PackFromVoxel(new int3(16, 0, 0), 0);

            Assert.AreEqual(0, VoxelChunkCoord.UnpackX(chunk0));
            Assert.AreEqual(1, VoxelChunkCoord.UnpackX(chunk1));
        }

        [Test]
        public void PackFromVoxel_NegativeCoord_FloorsCorrectly()
        {
            // Воксель -1 на LOD0: -1 >> 4 = -1 (арифметический сдвиг).
            long key = VoxelChunkCoord.PackFromVoxel(new int3(-1, -1, -1), 0);
            Assert.AreEqual(-1, VoxelChunkCoord.UnpackX(key));
            Assert.AreEqual(-1, VoxelChunkCoord.UnpackY(key));
            Assert.AreEqual(-1, VoxelChunkCoord.UnpackZ(key));
        }

        [Test]
        public void PackFromVoxel_Lod1_ChunkSizeDoubles()
        {
            // LOD1: шаг 1 м, чанк 16 м = 32 вокселя LOD0.
            // Воксель 31 LOD0 -> чанк 0 LOD1; воксель 32 -> чанк 1 LOD1.
            long chunk0 = VoxelChunkCoord.PackFromVoxel(new int3(31, 0, 0), 1);
            long chunk1 = VoxelChunkCoord.PackFromVoxel(new int3(32, 0, 0), 1);

            Assert.AreEqual(0, VoxelChunkCoord.UnpackX(chunk0));
            Assert.AreEqual(1, VoxelChunkCoord.UnpackX(chunk1));
        }

        [Test]
        public void Pack_IsDeterministic()
        {
            long key1 = VoxelChunkCoord.Pack(42, -7, 123, 3);
            long key2 = VoxelChunkCoord.Pack(42, -7, 123, 3);
            Assert.AreEqual(key1, key2, "Упаковка должна быть детерминированной.");
        }
    }
}
