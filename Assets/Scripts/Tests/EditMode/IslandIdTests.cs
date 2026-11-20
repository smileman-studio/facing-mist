using NUnit.Framework;
using SmilemanStudio.Core;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты структур <see cref="IslandId"/> и <see cref="IslandTier"/> (ADR 0001).
    /// </summary>
    public class IslandIdTests
    {
        [Test]
        public void NewIslandId_IsValid_ReturnsTrue()
        {
            IslandId id = new IslandId(1);
            Assert.IsTrue(id.IsValid);
        }

        [Test]
        public void DefaultIslandId_IsNotValid_ReturnsTrue()
        {
            IslandId id = default(IslandId);
            Assert.IsFalse(id.IsValid);
        }

        [Test]
        public void SameIslandIds_AreEqual_ReturnsTrue()
        {
            IslandId a = new IslandId(5);
            IslandId b = new IslandId(5);
            Assert.AreEqual(a, b);
        }

        [Test]
        public void DefaultIslandTier_IsNone_ReturnsTrue()
        {
            Assert.AreEqual(IslandTier.None, default(IslandTier));
        }
    }
}
