namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Константы воксельного terrain островов (ADR-0002).
    /// </summary>
    /// <remarks>
    /// Единственный источник истины для значений из раздела «Константы» ADR-0002:
    /// код и тесты обязаны ссылаться на эти поля, а не дублировать числа литералами.
    /// Доли радиуса заданы в промилле, а не во float: генерация и раскладка мира
    /// должны быть детерминированными и не зависеть от платформенной float-арифметики.
    /// </remarks>
    public static class VoxelConstants
    {
        /// <summary>Логарифм шага вокселя LOD0 по основанию 2: −1 означает 0,5 м.</summary>
        public const int VoxelSizeLog2 = -1;

        /// <summary>Логарифм числа ячеек в чанке по основанию 2: 4 означает 16.</summary>
        public const int ChunkSizeLog2 = 4;

        /// <summary>Размер кэша плотности: 17 сэмплов плюс apron по одному с каждой стороны.</summary>
        public const int CacheSize = 19;

        /// <summary>Выравнивание сетки LOD и границ полосы, м.</summary>
        public const int LodAlignment = 64;

        /// <summary>Радиус переключения LOD в размерах чанка.</summary>
        public const int LodSwitchFactor = 4;

        /// <summary>Guard на число уровней LOD: дальше воксельный меш заменяется impostor'ом.</summary>
        public const int LodHardMax = 10;

        /// <summary>Верхняя граница мутабельной коры, м.</summary>
        public const int CrustMaxY = 448;

        /// <summary>Нижняя граница мутабельной коры, м.</summary>
        public const int CrustMinY = -64;

        /// <summary>Граница между переходным слоем и иммутабельным ядром, м.</summary>
        public const int CrustStitchMinY = -128;

        /// <summary>Полная толщина воксельной полосы (кора плюс переходный слой), м.</summary>
        public const int CrustThickness = CrustMaxY - CrustStitchMinY;

        /// <summary>Геймплейный предел копания, м; тюнится независимо от сетки.</summary>
        public const int DigLimitY = -50;

        /// <summary>Guard на предел рельефа при расширении <see cref="RadiusMax"/>, м.</summary>
        public const int TopHeightHardCap = 448;

        /// <summary>Глубина вершины конуса в промилле радиуса: 900 означает 0,9 R.</summary>
        public const int ApexDepthPermille = 900;

        /// <summary>Предел рельефа в промилле радиуса: 200 означает 0,2 R.</summary>
        public const int TopReliefPermille = 200;

        /// <summary>Период одной клетки рельефного шума, м (ADR-0002, D1/D6).</summary>
        /// <remarks>
        /// Частота рельефа не задана ADR — фиксируем её здесь, единственным
        /// источником. Одна клетка value-noise = 64 м, fbm с lacunarity 2 даёт
        /// детали вплоть до 64 / 2^4 = 4 м при 5 октавах. Вход в шум — целочисленный
        /// Q16: coord * (1 &lt;&lt; 16) / ReliefNoisePeriod.
        /// </remarks>
        public const int ReliefNoisePeriod = 64;

        /// <summary>Единиц плотности на шаг сетки; плотность хранится как short.</summary>
        public const int DensityUnitsPerVoxel = 256;

        /// <summary>Порог запекания чанка в производный кэш по числу пересекающих операций.</summary>
        public const int CompactThreshold = 16;

        /// <summary>Ёмкость сегмента журнала правок, операций.</summary>
        public const int SegmentCapacity = 512;

        /// <summary>Минимальный шаг меша иммутабельного ядра, м.</summary>
        public const int CoreMinStep = 8;

        /// <summary>Минимальный радиус острова, м.</summary>
        public const int RadiusMin = 200;

        /// <summary>Максимальный радиус острова, м.</summary>
        public const int RadiusMax = 2000;
    }
}
