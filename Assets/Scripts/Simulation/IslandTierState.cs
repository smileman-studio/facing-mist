using System;
using SmilemanStudio.Core;
using Unity.Entities;

namespace SmilemanStudio.Simulation
{
    /// <summary>
    /// ECS-компонент: динамическое состояние острова (ADR 0001).
    /// Хранит тир симуляции и макро-данные, которые живут вместе с якорем острова
    /// независимо от наличия внутренних сущностей.
    /// </summary>
    /// <remarks>
    /// Входит в фиксированный набор компонентов якоря: тир меняется значением поля,
    /// а не добавлением/удалением компонентов (иначе каждый переход создавал бы
    /// новый архетип и фрагментировал чанки).
    /// </remarks>
    public struct IslandTierState : IComponentData
    {
        /// <summary>Текущий тир симуляции острова.</summary>
        public IslandTier Tier;

        /// <summary>
        /// Версия данных. Увеличивается при каждом пересчёте, чтобы понимать,
        /// когда состояние острова устарело относительно текущего мира.
        /// </summary>
        public uint Revision;

        /// <summary>
        /// Сколько времени остров симулировался в макро-тире, в тиках (1 тик = 1/1000 c).
        /// Целочисленный тип гарантирует точное накопление без потери битов и
        /// независимость от порядка сложений (детерминизм, ADR 0001).
        /// </summary>
        public long LastSimulationTime;

        /// <summary>
        /// Проверяет, что состояние валидно: <see cref="Tier"/> должен быть назначен
        /// (не <see cref="IslandTier.None"/>).
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Бросается, если <see cref="Tier"/> равен <see cref="IslandTier.None"/>:
        /// это значение по умолчанию и оно не описывает ни один реальный тир.
        /// </exception>
        public void Validate()
        {
            if (Tier == IslandTier.None)
            {
                throw new ArgumentException(
                    "IslandTierState.Tier must be assigned (not IslandTier.None).",
                    nameof(Tier));
            }
        }
    }
}
