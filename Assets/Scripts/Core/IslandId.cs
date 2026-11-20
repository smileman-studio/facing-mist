using System;

namespace SmilemanStudio.Core
{
    /// <summary>
    /// Уникальный идентификатор острова. Остров — атомарная единица симуляции (ADR 0001),
    /// поэтому <see cref="IslandId"/> используется как ключ в хеш-таблицах
    /// (например, <c>NativeParallelHashMap</c>) и для сопоставления островов.
    /// </summary>
    /// <remarks>
    /// Значение <see cref="Value"/> равно <c>0</c> означает «нет острова» (невалидный ID).
    /// </remarks>
    [Serializable]
    public readonly struct IslandId : IEquatable<IslandId>, IComparable<IslandId>
    {
        /// <summary>Числовой идентификатор острова. Значение <c>0</c> означает «нет острова».</summary>
        public readonly int Value;

        /// <summary>
        /// <c>true</c>, если идентификатор валиден (отличен от <c>0</c>).
        /// </summary>
        public bool IsValid => Value > 0;

        public IslandId(int value)
        {
            Value = value;
        }

        public bool Equals(IslandId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is IslandId other && Equals(other);

        public int CompareTo(IslandId other) => Value.CompareTo(other.Value);

        public override int GetHashCode()
        {
            // Детерминированный хеш-микс (Thomas Wang / boost-style):
            // последовательные ID (0, 1, 2, ...) не дают кластеризации в хеш-таблицах.
            int hash = Value;
            hash = (hash ^ (hash >> 16)) * 0x45d9f3b;
            hash = (hash ^ (hash >> 16)) * 0x45d9f3b;
            hash ^= hash >> 16;
            return hash;
        }

        public static bool operator ==(IslandId left, IslandId right) => left.Equals(right);

        public static bool operator !=(IslandId left, IslandId right) => !left.Equals(right);

        public override string ToString() => $"IslandId({Value})";
    }
}
