using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Контракт поля материала воксельного движка (ADR-0002, D7).
    /// </summary>
    /// <remarks>
    /// Аналог <see cref="IVoxelDensityField"/> для цвета/типа грунта. Движок не знает,
    /// как материал получается: процедурно от позиции и seed (остров), константой
    /// (debug-сфера) или из карты. Реализация — чистая функция точки.
    ///
    /// Единицы: вход — воксель LOD0 (шаг 0,5 м). Выход — <see cref="VoxelMaterial"/>
    /// как <c>byte</c>. Материал влияет только на vertex color Presentation, поэтому
    /// требования детерминизма к нему мягче, чем к занятости: суб-миллиметровое
    /// расхождение допустимо.
    /// </remarks>
    public interface IVoxelMaterialField
    {
        /// <summary>
        /// Материал в вокселе LOD0.
        /// </summary>
        /// <param name="voxel">Island-local/world воксель LOD0 (шаг 0,5 м).</param>
        /// <returns><see cref="VoxelMaterial"/> как <c>byte</c>.</returns>
        byte SampleMaterial(int3 voxel);
    }
}
