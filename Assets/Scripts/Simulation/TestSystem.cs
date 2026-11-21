using Unity.Entities;
using UnityEngine;

namespace SmilemanStudio.Simulation
{
    // Простая тестовая система: логирует текущее время симуляции.
    // ISystem требует unmanaged-реализатор (struct), поэтому `partial struct`.
    // Намеренно без [BurstCompile]: Debug.Log и интерполяция строк
    // несовместимы с Burst (привели бы к ошибке компиляции).
    public partial struct TestSystem : ISystem
    {
        public void OnUpdate(ref SystemState systemState)
        {
            // Используем время симуляции (SystemAPI.Time), а не UnityEngine.Time —
            // согласно правилам Game.Sim.* (SystemState.Time устарело).
            double elapsedTime = SystemAPI.Time.ElapsedTime;
            // Debug.Log($"[TestSystem] ElapsedTime: {elapsedTime:F4}s");
        }
    }
}
