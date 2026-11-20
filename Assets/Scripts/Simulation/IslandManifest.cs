using SmilemanStudio.Core;
using Unity.Entities;
using Unity.Mathematics;

namespace SmilemanStudio.Simulation
{
    /// <summary>
    /// ECS-компонент: статический манифест острова (ADR 0001).
    /// Хранит идентификатор, координаты центра и радиус. Не меняется за время
    /// жизни мира, поэтому входит в фиксированный набор компонентов якоря острова.
    /// Динамическое состояние (тир, ревизия) живёт в <see cref="IslandTierState"/>.
    /// </summary>
    public struct IslandManifest : IComponentData
    {
        /// <summary>Идентификатор острова.</summary>
        public IslandId Id;

        /// <summary>Координаты центра острова в метрах.</summary>
        public int3 Origin;

        /// <summary>Радиус острова в метрах.</summary>
        public int Radius;
    }
}
