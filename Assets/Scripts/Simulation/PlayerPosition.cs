using Unity.Entities;
using Unity.Mathematics;

namespace SmilemanStudio.Simulation
{
    /// <summary>
    /// ECS-синглтон: текущая позиция игрока в плавных координатах.
    /// </summary>
    public struct PlayerPosition : IComponentData
    {
        /// <summary>Позиция игрока (мировые координаты, метры).</summary>
        public float3 Position;
    }
}
