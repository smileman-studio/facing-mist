using NUnit.Framework;
using SmilemanStudio.Core;
using SmilemanStudio.Core.Voxels;
using SmilemanStudio.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SmilemanStudio.Tests.PlayMode
{
    /// <summary>
    /// PlayMode-тест <see cref="SmilemanStudio.Bootstrap.WorldBootstrapSystem"/>:
    /// первый апдейт заселяет мир игроком и 100 островами-якорями в области 100 x 100 км
    /// с соблюдением дистанций между островами (ADR 0001, ADR 0002).
    /// </summary>
    public class WorldBootstrapPlayModeTests
    {
        /// <summary>Ожидаемое количество островов.</summary>
        private const int ExpectedIslandCount = 100;

        /// <summary>Полуразмер области генерации по X и Z, м.</summary>
        private const int AreaHalfExtent = 50000;

        /// <summary>
        /// Допустимое число инфраструктурных сущностей ECS помимо игрока и островов
        /// (синглтон WorldTime, ECB-системы). Внутренние сущности островов сюда не входят.
        /// </summary>
        private const int MaxInfrastructureEntityCount = 8;

        [Test]
        public void FirstUpdate_PopulatesPlayerAndIslandsWithinAreaAndSpacing()
        {
            // В PlayMode default world создаётся автоматически (AutomaticWorldBootstrap).
            // Сохраняем его и убираем из player loop, чтобы DefaultWorldInitialization
            // создал именно тестовый мир (эталонный паттерн из тестов Unity.Entities).
            World previousWorld = World.DefaultGameObjectInjectionWorld;
            bool previousWasInPlayerLoop = false;
            if (previousWorld != null)
            {
                previousWasInPlayerLoop = ScriptBehaviourUpdateOrder.IsWorldInCurrentPlayerLoop(previousWorld);
                if (previousWasInPlayerLoop)
                {
                    ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(previousWorld);
                }
            }

            World.DefaultGameObjectInjectionWorld = null;

            World world = null;
            try
            {
                world = DefaultWorldInitialization.Initialize("Bootstrap Test World", false);

                // WorldBootstrapSystem живёт в InitializationSystemGroup:
                // первый Update заселяет мир игроком и островами.
                world.Update();

                IslandManifest[] manifests = ReadComponentArray<IslandManifest>(world);
                IslandTierState[] tierStates = ReadComponentArray<IslandTierState>(world);
                PlayerPosition[] players = ReadComponentArray<PlayerPosition>(world);

                // Игрок: ровно один, в начале координат.
                Assert.AreEqual(1, players.Length, "Должна быть создана ровно одна сущность игрока.");
                Assert.AreEqual(float3.zero, players[0].Position, "Игрок должен стартовать в начале координат.");

                // Острова: ровно 100 якорей, у каждого есть и манифест, и состояние тира.
                Assert.AreEqual(ExpectedIslandCount, manifests.Length,
                    "Должно быть создано ровно " + ExpectedIslandCount + " островов.");
                Assert.AreEqual(ExpectedIslandCount, tierStates.Length,
                    "У каждого острова должен быть IslandTierState (1:1 с IslandManifest).");

                // Внутренних сущностей островов нет. Помимо 100 якорей и игрока в мире
                // живут только инфраструктурные синглтоны ECS (WorldTime, ECB-системы),
                // поэтому допускаем небольшой постоянный запас: рост означает,
                // что для островов начали спавнить внутренние сущности.
                int totalEntityCount = world.EntityManager.UniversalQuery.CalculateEntityCount();
                int nonIslandEntityCount = totalEntityCount - ExpectedIslandCount - 1;
                Assert.LessOrEqual(nonIslandEntityCount, MaxInfrastructureEntityCount,
                    "Обнаружены лишние сущности: якоря островов не должны порождать внутренние сущности.");

                // Все острова лежат в области генерации [-50000, 50000] по X и Z.
                for (int i = 0; i < manifests.Length; i++)
                {
                    int3 origin = manifests[i].Origin;
                    int id = manifests[i].Id.Value;

                    Assert.AreEqual(0, origin.y, "Origin острова " + id + " должен лежать в плоскости y = 0.");
                    Assert.IsTrue(origin.x >= -AreaHalfExtent && origin.x <= AreaHalfExtent,
                        "Origin.x острова " + id + " (" + origin.x + ") вне области генерации.");
                    Assert.IsTrue(origin.z >= -AreaHalfExtent && origin.z <= AreaHalfExtent,
                        "Origin.z острова " + id + " (" + origin.z + ") вне области генерации.");

                    // Радиусы берутся из диапазона ADR-0002, а не из локальных констант.
                    Assert.IsTrue(IslandFootprint.IsValidRadius(manifests[i].Radius),
                        "Радиус острова " + id + " (" + manifests[i].Radius + ") вне диапазона ADR-0002.");
                }

                // Идентификаторы уникальны и лежат в диапазоне 1..100.
                bool[] seenIds = new bool[ExpectedIslandCount + 1];
                for (int i = 0; i < manifests.Length; i++)
                {
                    int id = manifests[i].Id.Value;

                    Assert.IsTrue(id >= 1 && id <= ExpectedIslandCount, "Недопустимый IslandId: " + id + ".");
                    Assert.IsFalse(seenIds[id], "Дублирующийся IslandId: " + id + ".");
                    seenIds[id] = true;
                }

                // Дистанции: расстояние между краями любых двух островов не меньше
                // радиуса меньшего из них (ADR-0002, D1). Сравнение — по квадратам,
                // минимальная дистанция считается через IslandFootprint, без дублей констант.
                for (int i = 0; i < manifests.Length; i++)
                {
                    for (int j = i + 1; j < manifests.Length; j++)
                    {
                        long distanceSquared = WorldMath.DistanceSquared(manifests[i].Origin, manifests[j].Origin);
                        long minSpacingSquared = IslandFootprint.MinCenterSpacingSquared(
                            manifests[i].Radius,
                            manifests[j].Radius);

                        Assert.GreaterOrEqual(distanceSquared, minSpacingSquared,
                            "Острова " + manifests[i].Id.Value + " и " + manifests[j].Id.Value +
                            " ближе минимальной дистанции между краями (радиус меньшего).");
                    }
                }

                // Система тиров отработала: ни один остров не остался в неинициализированном тире.
                for (int i = 0; i < tierStates.Length; i++)
                {
                    Assert.AreNotEqual(IslandTier.None, tierStates[i].Tier,
                        "Тир острова не должен оставаться IslandTier.None после первого апдейта.");
                }

                // Игрок стоит в центре острова с Origin = (0,0,0) -> этот остров Hot.
                Assert.IsTrue(HasHotIslandAtOrigin(world),
                    "Остров под игроком (Origin = 0) должен быть Hot.");
            }
            finally
            {
                if (world != null && world.IsCreated)
                {
                    world.Dispose();
                }

                World.DefaultGameObjectInjectionWorld = previousWorld;
                if (previousWorld != null && previousWasInPlayerLoop)
                {
                    ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(previousWorld);
                }
            }
        }

        /// <summary>
        /// Проверяет, что остров с центром в начале координат находится в Hot-тире.
        /// </summary>
        private static bool HasHotIslandAtOrigin(World world)
        {
            using (EntityQuery query = world.EntityManager.CreateEntityQuery(
                       ComponentType.ReadOnly<IslandManifest>(),
                       ComponentType.ReadOnly<IslandTierState>()))
            using (NativeArray<IslandManifest> manifests = query.ToComponentDataArray<IslandManifest>(Allocator.Temp))
            using (NativeArray<IslandTierState> tierStates = query.ToComponentDataArray<IslandTierState>(Allocator.Temp))
            {
                for (int i = 0; i < manifests.Length; i++)
                {
                    if (manifests[i].Origin.Equals(int3.zero) && tierStates[i].Tier == IslandTier.Hot)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Копирует данные всех сущностей с компонентом <typeparamref name="T"/> в managed-массив.
        /// </summary>
        private static T[] ReadComponentArray<T>(World world) where T : unmanaged, IComponentData
        {
            using (EntityQuery query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<T>()))
            using (NativeArray<T> data = query.ToComponentDataArray<T>(Allocator.Temp))
            {
                return data.ToArray();
            }
        }
    }
}
