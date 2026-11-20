using Unity.Mathematics;

namespace SmilemanStudio.Core
{
    /// <summary>
    /// Целочисленная математика мира (координаты в метрах).
    /// </summary>
    public static class WorldMath
    {
        /// <summary>
        /// Квадрат евклидова расстояния между двумя точками int3.
        /// Приведение к long перед умножением защищает от переполнения int.
        /// </summary>
        public static long DistanceSquared(int3 a, int3 b)
        {
            long dx = (long)a.x - b.x;
            long dy = (long)a.y - b.y;
            long dz = (long)a.z - b.z;
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
