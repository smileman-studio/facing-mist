namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Вид CSG-операции правки грунта (ADR-0002, D5, D6).
    /// </summary>
    /// <remarks>
    /// Смешивание <see cref="Add"/> (<c>min</c>) и <see cref="Subtract"/> (<c>max</c>)
    /// не коммутативно: <c>max(min(d, a), c) ≠ min(max(d, c), a)</c>.
    /// Поэтому детерминизм журнала обеспечивается порядком <c>Seq</c>, а не алгеброй.
    /// <c>None = 0</c> — обязательное значение по умолчанию (правило AGENTS.md).
    /// </remarks>
    public enum VoxelBrushKind : byte
    {
        /// <summary>Нет операции (пустое значение по умолчанию).</summary>
        None = 0,

        /// <summary>Добавить грунт: d = min(d, sdfBrush).</summary>
        Add = 1,

        /// <summary>Выкопать: d = max(d, -sdfBrush).</summary>
        Subtract = 2,
    }
}
