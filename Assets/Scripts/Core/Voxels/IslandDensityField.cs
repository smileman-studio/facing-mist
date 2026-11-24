using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Поле плотности острова: адаптер <see cref="IslandSdf"/> к контракту движка
    /// <see cref="IVoxelDensityField"/> (ADR-0002, D1/D4).
    /// </summary>
    /// <remarks>
    /// Остров перестаёт быть особым случком мешинга: для движка это один из источников
    /// плотности, как сфера или том. Класс хранит Id и Radius и возвращает квантованную
    /// плотность в вокселе LOD0. Побитово совпадает со старым <c>VoxelChunkSampler.SampleCore</c>
    /// (тот же <c>VoxelDensity.Quantize(IslandSdf.SdfVoxel(...))</c>), поэтому golden-hash
    /// мешей не меняется.
    /// </remarks>
    public struct IslandDensityField : IVoxelDensityField
    {
        /// <summary>Идентификатор острова (seed формы).</summary>
        public IslandId Id;

        /// <summary>Радиус острова, м.</summary>
        public int Radius;

        public IslandDensityField(IslandId id, int radius)
        {
            Id = id;
            Radius = radius;
        }

        /// <summary>short плотности (256 единиц на воксель LOD0) в воксельной точке.</summary>
        public short SampleDensity(int3 voxel)
        {
            return VoxelDensity.Quantize(IslandSdf.SdfVoxel(voxel, Id, Radius));
        }
    }
}
