using System;

namespace SmilemanStudio.Core.Hashing
{
    /// <summary>
    /// Детерминированный хеш-суммаризатор состояния острова для golden-hash тестов
    /// (ADR 0001: код Hydrate/Dehydrate должен быть строго детерминированным).
    /// </summary>
    /// <remarks>
    /// Хеш вычисляется алгоритмом FNV-1a (64-бит) строго по целочисленным битам полей,
    /// поэтому результат одинаков на всех поддерживаемых платформах. Порядок байтов
    /// фиксируется явно (little-endian), математика — только целочисленная (ulong),
    /// без float-арифметики.
    /// <para>
    /// Хеш принимает примитивы, а не компоненты: <c>Core</c> не зависит от
    /// <c>Unity.Entities</c> и не знает про ECS. Вызывающая сторона передаёт поля
    /// <c>IslandManifest</c> и <c>IslandTierState</c>.
    /// </para>
    /// </remarks>
    public static class WorldHasher
    {
        // Константы FNV-1a 64-bit.
        private const ulong FnvOffsetBasis = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>
        /// Вычисляет детерминированный 64-битный хеш состояния острова
        /// по идентификатору, тиру, ревизии и накопленному времени симуляции.
        /// </summary>
        /// <param name="id">Идентификатор острова.</param>
        /// <param name="tier">Текущий тир симуляции.</param>
        /// <param name="revision">Версия состояния острова.</param>
        /// <param name="lastSimulationTime">Время макро-симуляции в тиках.</param>
        /// <returns>Детерминированный хеш; одинаковый для одинаковых состояний на любой платформе.</returns>
        public static ulong ComputeHash(
            IslandId id,
            IslandTier tier,
            uint revision,
            long lastSimulationTime)
        {
            ulong hash = FnvOffsetBasis;

            hash = Mix(hash, (uint)id.Value);
            hash = Mix(hash, (uint)tier);
            hash = Mix(hash, revision);
            // long — 8 байт: проходим сначала младшие 32 бита, затем старшие
            // (little-endian, как и для остальных полей).
            hash = Mix(hash, (uint)lastSimulationTime);
            hash = Mix(hash, (uint)(lastSimulationTime >> 32));

            return hash;
        }

        /// <summary>
        /// Пропускает 4 байта значения (little-endian) через FNV-1a.
        /// </summary>
        private static ulong Mix(ulong hash, uint value)
        {
            hash ^= (value & 0xFFUL);
            hash *= FnvPrime;
            hash ^= ((value >> 8) & 0xFFUL);
            hash *= FnvPrime;
            hash ^= ((value >> 16) & 0xFFUL);
            hash *= FnvPrime;
            hash ^= ((value >> 24) & 0xFFUL);
            hash *= FnvPrime;
            return hash;
        }
    }
}
