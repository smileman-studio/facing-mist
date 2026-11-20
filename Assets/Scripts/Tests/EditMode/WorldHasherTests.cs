using NUnit.Framework;
using SmilemanStudio.Core;
using SmilemanStudio.Core.Hashing;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты детерминированного хеша <see cref="WorldHasher"/> (инфраструктура
    /// golden-hash тестов, ADR 0001).
    /// </summary>
    public class WorldHasherTests
    {
        [Test]
        public void SameState_ProducesSameHash()
        {
            ulong firstHash = WorldHasher.ComputeHash(new IslandId(1), IslandTier.Cold, 7, 12500);
            ulong secondHash = WorldHasher.ComputeHash(new IslandId(1), IslandTier.Cold, 7, 12500);

            Assert.AreEqual(firstHash, secondHash,
                "Одинаковое состояние острова должно давать одинаковый хеш.");
        }

        [Test]
        public void DifferentId_ProducesDifferentHash()
        {
            ulong firstHash = WorldHasher.ComputeHash(new IslandId(1), IslandTier.Cold, 7, 12500);
            ulong secondHash = WorldHasher.ComputeHash(new IslandId(2), IslandTier.Cold, 7, 12500);

            Assert.AreNotEqual(firstHash, secondHash,
                "Разные IslandId должны давать разные хеши.");
        }

        [Test]
        public void DifferentTier_ProducesDifferentHash()
        {
            ulong coldHash = WorldHasher.ComputeHash(new IslandId(1), IslandTier.Cold, 7, 12500);
            ulong backgroundHash = WorldHasher.ComputeHash(new IslandId(1), IslandTier.Background, 7, 12500);

            Assert.AreNotEqual(coldHash, backgroundHash,
                "Разные тиры должны давать разные хеши.");
        }

        [Test]
        public void DifferentRevision_ProducesDifferentHash()
        {
            ulong firstHash = WorldHasher.ComputeHash(new IslandId(1), IslandTier.Cold, 7, 12500);
            ulong secondHash = WorldHasher.ComputeHash(new IslandId(1), IslandTier.Cold, 8, 12500);

            Assert.AreNotEqual(firstHash, secondHash,
                "Разные ревизии должны давать разные хеши.");
        }

        [Test]
        public void DifferentSimulationTime_ProducesDifferentHash()
        {
            ulong firstHash = WorldHasher.ComputeHash(new IslandId(1), IslandTier.Cold, 7, 12500);
            ulong secondHash = WorldHasher.ComputeHash(new IslandId(1), IslandTier.Cold, 7, 12501);

            Assert.AreNotEqual(firstHash, secondHash,
                "Разное время макро-симуляции должно давать разные хеши.");
        }
    }
}
