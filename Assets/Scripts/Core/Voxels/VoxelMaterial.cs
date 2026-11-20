namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Тип грунта вокселя (ADR-0002, D7).
    /// </summary>
    /// <remarks>
    /// В v1 материал полностью процедурный: чистая функция от island-local позиции
    /// и seed. В кэше и запечённом чанке материал не хранится.
    /// <c>None = 0</c> — обязательное значение по умолчанию (правило AGENTS.md).
    /// </remarks>
    public enum VoxelMaterial : byte
    {
        /// <summary>Нет материала (пусто / воздух / не инициализировано).</summary>
        None = 0,

        /// <summary>Земля — основной грунт острова.</summary>
        Dirt = 1,

        /// <summary>Уголь — полезная жила.</summary>
        Coal = 2,

        /// <summary>Песок.</summary>
        Sand = 3,

        /// <summary>Кварц — полезная жила.</summary>
        Quartz = 4,
    }
}
