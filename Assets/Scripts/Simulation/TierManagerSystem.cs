using SmilemanStudio.Core;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace SmilemanStudio.Simulation
{
    /// <summary>
    /// Пересчитывает тир каждого острова (ADR 0001) раз в секунду на основе
    /// целочисленного квадрата расстояния от игрока до Origin острова.
    /// </summary>
    /// <remarks>
    /// Тир записывается в <see cref="IslandTierState"/> — компонент якоря острова.
    /// Набор компонентов сущности при этом не меняется, поэтому пересчёт тиров
    /// не порождает структурных изменений и новых архетипов.
    /// </remarks>
    [BurstCompile]
    public partial struct TierManagerSystem : ISystem
    {
        /// <summary>Интервал пересчёта тиров, секунд.</summary>
        private const double UpdateIntervalSeconds = 1.0;

        /// <summary>Время последнего пересчёта (ElapsedTime).</summary>
        private double lastUpdateTime;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            lastUpdateTime = double.NegativeInfinity;
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            double now = SystemAPI.Time.ElapsedTime;
            if (now - lastUpdateTime < UpdateIntervalSeconds)
            {
                return;
            }

            lastUpdateTime = now;

            if (!SystemAPI.TryGetSingleton<PlayerPosition>(out var playerPos))
            {
                return;
            }

            int3 playerPosInt = (int3)math.round(playerPos.Position);

            foreach (var (manifest, tierState) in
                     SystemAPI.Query<RefRO<IslandManifest>, RefRW<IslandTierState>>())
            {
                long distanceSquared = WorldMath.DistanceSquared(playerPosInt, manifest.ValueRO.Origin);
                IslandTier newTier = TierCalculator.CalculateTier(distanceSquared, manifest.ValueRO.Radius);

                if (tierState.ValueRO.Tier == newTier)
                {
                    continue;
                }

                // Меняем только значения полей: архетип сущности остаётся прежним.
                tierState.ValueRW.Tier = newTier;
                tierState.ValueRW.Revision++;
            }
        }
    }
}
