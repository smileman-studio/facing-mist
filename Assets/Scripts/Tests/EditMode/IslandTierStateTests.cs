using System;
using NUnit.Framework;
using SmilemanStudio.Core;
using SmilemanStudio.Simulation;
using Unity.Entities;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты динамического состояния острова <see cref="IslandTierState"/> (ADR 0001).
    /// </summary>
    public class IslandTierStateTests
    {
        [Test]
        public void IslandTierState_IsEcsComponentData()
        {
            Assert.IsTrue(typeof(IComponentData).IsAssignableFrom(typeof(IslandTierState)),
                "IslandTierState должен быть ECS-компонентом (IComponentData).");
        }

        [Test]
        public void DefaultState_ThrowsException_OnValidate()
        {
            IslandTierState state = default(IslandTierState);
            Assert.Throws<ArgumentException>(() => state.Validate());
        }

        [Test]
        public void BackgroundTier_DoesNotThrow_OnValidate()
        {
            IslandTierState state = new IslandTierState { Tier = IslandTier.Background };
            Assert.DoesNotThrow(() => state.Validate());
        }

        [Test]
        public void HotTier_IsValid_ForAnchorState()
        {
            // Якорь острова существует всегда, поэтому Hot — валидное состояние
            // (в отличие от старого IslandMacro, который описывал только Cold/Background).
            IslandTierState state = new IslandTierState { Tier = IslandTier.Hot };
            Assert.DoesNotThrow(() => state.Validate());
        }
    }
}
