using SmilemanStudio.Core;
using SmilemanStudio.Core.Voxels;
using SmilemanStudio.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SmilemanStudio.Bootstrap
{
    /// <summary>
    /// Композиция мира при старте (composition root): один раз создаёт сущность
    /// игрока и 100 островов-якорей с уникальными идентификаторами (ADR 0001).
    /// Работает в <see cref="InitializationSystemGroup"/> — до первой игровой итерации.
    /// </summary>
    /// <remarks>
    /// Детерминирован: позиции и радиусы островов генерируются
    /// <see cref="Unity.Mathematics.Random"/> с фиксированным сидом, поэтому
    /// одинаковый мир создаётся при каждом запуске (требование детерминизма, ADR 0001).
    /// Повторный вызов безопасен: если синглтон <see cref="PlayerPosition"/> уже есть,
    /// система ничего не делает.
    /// Область генерации — квадрат с центром в начале координат:
    /// X и Z в диапазоне [-<see cref="AreaHalfExtent"/>, <see cref="AreaHalfExtent"/>] м.
    /// Радиусы островов берутся из диапазона ADR-0002, дистанции раскладки
    /// считаются через <see cref="IslandFootprint"/>: расстояние между краями
    /// любых двух островов не меньше радиуса меньшего из них.
    /// Архетип острова создаётся один раз и переиспользуется для всех 100 сущностей,
    /// поэтому спавн не порождает новых архетипов.
    /// Выполняет структурные изменения через <see cref="EntityManager"/>, поэтому
    /// намеренно НЕ помечена <c>[BurstCompile]</c> — этот API управляемый.
    /// </remarks>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct WorldBootstrapSystem : ISystem
    {
        /// <summary>Количество островов, создаваемых при заселении мира.</summary>
        private const int IslandCount = 100;

        /// <summary>Фиксированный сид генератора (детерминизм, ADR 0001).</summary>
        private const uint RandomSeed = 12345;

        /// <summary>Полуразмер области генерации по X и Z, м: [-50000, 50000].</summary>
        private const int AreaHalfExtent = 50000;

        /// <summary>Максимальное число попыток размещения одного острова.</summary>
        private const int MaxPlacementAttempts = 4000;

        public void OnUpdate(ref SystemState state)
        {
            // Игрок уже существует (мир уже заселён) — ничего не делаем.
            if (SystemAPI.TryGetSingleton<PlayerPosition>(out _))
            {
                return;
            }

            EntityManager entityManager = state.EntityManager;

            // Сущность игрока: синглтон PlayerPosition в нулевой точке мира.
            Entity playerEntity = entityManager.CreateEntity();
            entityManager.AddComponentData(playerEntity, new PlayerPosition
            {
                Position = new float3(0.0f, 0.0f, 0.0f)
            });

            // Архетип якоря острова создаётся один раз (ADR 0001):
            // статический IslandManifest + динамический IslandTierState.
            EntityArchetype islandArchetype = entityManager.CreateArchetype(
                ComponentType.ReadWrite<IslandManifest>(),
                ComponentType.ReadWrite<IslandTierState>());

            // Детерминированная генерация островов: Unity.Mathematics.Random
            // с фиксированным сидом (UnityEngine.Random в симуляции запрещён).
            Random rng = new Random(RandomSeed);

            // Уже размещённые центры и радиусы островов — для проверки дистанций.
            NativeArray<int3> placedOrigins = new NativeArray<int3>(IslandCount, Allocator.Temp);
            NativeArray<int> placedRadii = new NativeArray<int>(IslandCount, Allocator.Temp);
            try
            {
                for (int i = 1; i <= IslandCount; i++)
                {
                    // Радиус нужен до выбора позиции: минимальная дистанция
                    // зависит от радиусов обоих островов.
                    int radius = rng.NextInt(VoxelConstants.RadiusMin, VoxelConstants.RadiusMax + 1);

                    int3 origin;

                    if (i == 1)
                    {
                        // Первый остров — в начале координат: игрок стартует в его центре (Hot).
                        origin = int3.zero;
                    }
                    else
                    {
                        origin = PlaceNextIsland(ref rng, placedOrigins, placedRadii, i - 1, radius);
                    }

                    placedOrigins[i - 1] = origin;
                    placedRadii[i - 1] = radius;

                    Entity islandEntity = entityManager.CreateEntity(islandArchetype);
                    entityManager.SetComponentData(islandEntity, new IslandManifest
                    {
                        Id = new IslandId(i),
                        Origin = origin,
                        Radius = radius
                    });
                    entityManager.SetComponentData(islandEntity, new IslandTierState
                    {
                        // До первого пересчёта тиров остров считается фоновым.
                        Tier = IslandTier.Background,
                        Revision = 0,
                        LastSimulationTime = 0
                    });
                }
            }
            finally
            {
                placedOrigins.Dispose();
                placedRadii.Dispose();
            }
        }

        /// <summary>
        /// Подбирает позицию следующего острова rejection sampling: расстояние
        /// между краями кандидата и любого уже размещённого острова не меньше
        /// радиуса меньшего из них (ADR-0002, D1).
        /// </summary>
        /// <param name="rng">Генератор; передаётся по ссылке, чтобы состояние сохранялось между вызовами.</param>
        /// <param name="placedOrigins">Уже занятые центры островов.</param>
        /// <param name="placedRadii">Радиусы размещённых островов, параллельно <paramref name="placedOrigins"/>.</param>
        /// <param name="placedCount">Количество занятых центров в <paramref name="placedOrigins"/>.</param>
        /// <param name="radiusMeters">Радиус размещаемого острова, м.</param>
        private static int3 PlaceNextIsland(
            ref Random rng,
            NativeArray<int3> placedOrigins,
            NativeArray<int> placedRadii,
            int placedCount,
            int radiusMeters)
        {
            for (int attempt = 0; attempt < MaxPlacementAttempts; attempt++)
            {
                // NextInt(min, max): max исключён, поэтому +1 для включаемого диапазона.
                int3 candidate = new int3(
                    rng.NextInt(-AreaHalfExtent, AreaHalfExtent + 1),
                    0,
                    rng.NextInt(-AreaHalfExtent, AreaHalfExtent + 1));

                bool tooClose = false;

                for (int j = 0; j < placedCount; j++)
                {
                    long distanceSquared = WorldMath.DistanceSquared(candidate, placedOrigins[j]);

                    if (distanceSquared < IslandFootprint.MinCenterSpacingSquared(radiusMeters, placedRadii[j]))
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                {
                    return candidate;
                }
            }

            // Страховка: попытки исчерпаны. При текущей плотности мира это
            // недостижимо, а нарушение дистанции поймает тест раскладки.
            return new int3(
                rng.NextInt(-AreaHalfExtent, AreaHalfExtent + 1),
                0,
                rng.NextInt(-AreaHalfExtent, AreaHalfExtent + 1));
        }
    }
}
