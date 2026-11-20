namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Горизонтальная геометрия острова и правила раскладки островов в мире
    /// (ADR-0002, D1). Все размеры и расстояния — в метрах.
    /// </summary>
    /// <remarks>
    /// Единственный источник истины для дистанций раскладки: bootstrap и тесты
    /// обязаны вызывать эти методы, а не держать собственные константы.
    /// Расчёты целочисленные, чтобы раскладка была детерминированной на любой
    /// платформе и не зависела от float-арифметики.
    /// </remarks>
    public static class IslandFootprint
    {
        /// <summary>
        /// Радиус силуэта острова на высоте максимального рельефа, м.
        /// </summary>
        /// <remarks>
        /// Конус расширяется вверх, поэтому силуэт шире радиуса на опорном уровне
        /// (<c>IslandManifest.Radius</c>). На высоте <c>TopHeightMax</c> добавка равна
        /// <c>R * TopReliefPermille / ApexDepthPermille</c>, то есть примерно 0,222 R.
        /// Округление вверх: два острова не должны соприкасаться даже на границе.
        /// </remarks>
        /// <param name="radiusMeters">Радиус острова на опорном уровне, м.</param>
        public static int Extent(int radiusMeters)
        {
            long relief = (long)radiusMeters * VoxelConstants.TopReliefPermille;
            long flare = (relief + VoxelConstants.ApexDepthPermille - 1) / VoxelConstants.ApexDepthPermille;
            return radiusMeters + (int)flare;
        }

        /// <summary>
        /// Минимальный зазор между краями двух островов, м: радиус меньшего из них.
        /// </summary>
        public static int MinEdgeGap(int radiusMeters, int otherRadiusMeters)
        {
            return radiusMeters < otherRadiusMeters ? radiusMeters : otherRadiusMeters;
        }

        /// <summary>
        /// Минимальное расстояние между центрами двух островов, м.
        /// Складывается из двух силуэтов и минимального зазора между краями.
        /// </summary>
        public static long MinCenterSpacing(int radiusMeters, int otherRadiusMeters)
        {
            return (long)Extent(radiusMeters)
                   + Extent(otherRadiusMeters)
                   + MinEdgeGap(radiusMeters, otherRadiusMeters);
        }

        /// <summary>
        /// Квадрат <see cref="MinCenterSpacing"/> — для сравнения с квадратом
        /// расстояния без извлечения корня.
        /// </summary>
        public static long MinCenterSpacingSquared(int radiusMeters, int otherRadiusMeters)
        {
            long spacing = MinCenterSpacing(radiusMeters, otherRadiusMeters);
            return spacing * spacing;
        }

        /// <summary>
        /// Расстояние между краями двух островов по расстоянию между их центрами, м.
        /// Отрицательное значение означает пересечение силуэтов.
        /// </summary>
        public static long EdgeGap(long centerDistanceMeters, int radiusMeters, int otherRadiusMeters)
        {
            return centerDistanceMeters - Extent(radiusMeters) - Extent(otherRadiusMeters);
        }

        /// <summary>
        /// Проверяет, что радиус лежит в диапазоне, заданном ADR-0002.
        /// </summary>
        public static bool IsValidRadius(int radiusMeters)
        {
            return radiusMeters >= VoxelConstants.RadiusMin && radiusMeters <= VoxelConstants.RadiusMax;
        }
    }
}
