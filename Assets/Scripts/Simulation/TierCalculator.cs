using SmilemanStudio.Core;

namespace SmilemanStudio.Simulation
{
    /// <summary>
    /// Определяет тир симуляции острова (ADR 0001) по квадрату расстояния
    /// от игрока до центра острова и радиусу самого острова.
    /// </summary>
    /// <remarks>
    /// Чистая детерминированная функция: не использует UnityEngine.Random,
    /// UnityEngine.Time и MonoBehaviour, поэтому подходит для симуляции.
    /// </remarks>
    public static partial class TierCalculator
    {
        /// <summary>Квадрат границы Warm-тира: 5000 м.</summary>
        private const long WarmRadiusSquared = 5000L * 5000L;

        /// <summary>Квадрат границы Cold-тира: 10 000 м.</summary>
        private const long ColdRadiusSquared = 10000L * 10000L;

        /// <summary>
        /// Вычисляет тир острова (ADR 0001).
        /// Hot включается, когда игрок находится в пределах диаметра острова
        /// (летает вокруг и видит все детали).
        /// </summary>
        /// <param name="distanceSquaredMeters">Квадрат расстояния от игрока до центра острова, м².</param>
        /// <param name="islandRadiusMeters">Радиус острова в метрах.</param>
        /// <returns>
        /// IslandTier.Hot - игрок в пределах диаметра (distance² &lt;= diameter²);
        /// IslandTier.Warm - игрок в пределах 5000 м;
        /// IslandTier.Cold - игрок в пределах 10000 м;
        /// IslandTier.Background - иначе.
        /// </returns>
        public static IslandTier CalculateTier(long distanceSquaredMeters, int islandRadiusMeters)
        {
            long diameterMeters = 2L * islandRadiusMeters;
            long diameterSq = diameterMeters * diameterMeters;

            if (distanceSquaredMeters <= diameterSq)
            {
                return IslandTier.Hot;
            }

            if (distanceSquaredMeters <= WarmRadiusSquared)
            {
                return IslandTier.Warm;
            }

            if (distanceSquaredMeters <= ColdRadiusSquared)
            {
                return IslandTier.Cold;
            }

            return IslandTier.Background;
        }
    }
}
