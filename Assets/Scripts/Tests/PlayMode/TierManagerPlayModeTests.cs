using NUnit.Framework;
using SmilemanStudio.Core;
using SmilemanStudio.Simulation;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;

namespace SmilemanStudio.Tests.PlayMode
{
    /// <summary>
    /// PlayMode-тест динамики <see cref="TierManagerSystem"/> (ADR 0001):
    /// по мере удаления игрока от острова тир меняется Hot -> Warm -> Cold.
    /// </summary>
    /// <remarks>
    /// Время мира задаётся детерминированно через <see cref="World.SetTime"/>:
    /// TierManagerSystem пересчитывает тир раз в секунду по SystemAPI.Time.ElapsedTime.
    /// В синхронном [Test] все world.Update() выполняются в одном кадре, поэтому
    /// Time.deltaTime постоянен и разница последовательных SetTime-значений (2 с)
    /// гарантированно превышает интервал пересчёта (1 с).
    /// </remarks>
    public class TierManagerPlayModeTests
    {
        [Test]
        public void PlayerMovesAway_TierTransitionsHotWarmCold()
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
                // 1. Создаём тестовый мир со всеми системами по умолчанию.
                world = DefaultWorldInitialization.Initialize("Test World", false);

                // 2. Остров-якорь: Id=1, Origin=(0,0,0), Radius=1000 м.
                // Границы тиров (TierCalculator):
                //   Hot  - dist <= 2*radius = 2000 м (dist^2 <= 4 000 000);
                //   Warm - dist <= 5000 м (dist^2 <= 25 000 000);
                //   Cold - dist <= 10000 м (dist^2 <= 100 000 000).
                Entity islandEntity = world.EntityManager.CreateEntity();
                world.EntityManager.AddComponentData(islandEntity, new IslandManifest
                {
                    Id = new IslandId(1),
                    Origin = new int3(0, 0, 0),
                    Radius = 1000
                });
                world.EntityManager.AddComponentData(islandEntity, new IslandTierState
                {
                    Tier = IslandTier.Background,
                    Revision = 0,
                    LastSimulationTime = 0
                });

                // 3. Игрок в нулевой точке — в центре острова.
                Entity playerEntity = world.EntityManager.CreateEntity();
                world.EntityManager.AddComponentData(playerEntity, new PlayerPosition
                {
                    Position = new float3(0.0f, 0.0f, 0.0f)
                });

                // 4. Игрок в центре (0 м <= 2000 м) -> Hot.
                // Первый апдейт: lastUpdateTime = -inf, интервал гарантированно пройден.
                world.SetTime(new TimeData(0.0, 0.0f));
                world.Update();
                Assert.AreEqual(IslandTier.Hot, GetTier(world, islandEntity),
                    "Игрок в центре острова (0 м <= 2000 м) должен быть Hot.");

                // 5. Игрок на 3000 м: 2000 м < dist <= 5000 м -> Warm.
                world.EntityManager.SetComponentData(playerEntity, new PlayerPosition
                {
                    Position = new float3(3000.0f, 0.0f, 0.0f)
                });
                world.SetTime(new TimeData(2.0, 0.0f));
                world.Update();
                Assert.AreEqual(IslandTier.Warm, GetTier(world, islandEntity),
                    "Игрок на 3000 м (2000 м < dist <= 5000 м) должен быть Warm.");

                // 6. Игрок на 7000 м: 5000 м < dist <= 10000 м -> Cold.
                world.EntityManager.SetComponentData(playerEntity, new PlayerPosition
                {
                    Position = new float3(7000.0f, 0.0f, 0.0f)
                });
                world.SetTime(new TimeData(4.0, 0.0f));
                world.Update();
                Assert.AreEqual(IslandTier.Cold, GetTier(world, islandEntity),
                    "Игрок на 7000 м (5000 м < dist <= 10000 м) должен быть Cold.");
            }
            finally
            {
                // 7. Уничтожаем тестовый мир и восстанавливаем исходный default world.
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

        private static IslandTier GetTier(World world, Entity islandEntity)
        {
            return world.EntityManager.GetComponentData<IslandTierState>(islandEntity).Tier;
        }
    }
}
