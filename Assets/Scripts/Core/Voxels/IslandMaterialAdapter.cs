using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Поле материала острова: адаптер <see cref="IslandMaterialField"/> к контракту
    /// <see cref="IVoxelMaterialField"/> (ADR-0002, D7).
    /// </summary>
    /// <remarks>
    /// Процедурный материал (покраска отложена). Побитово совпадает со старым
    /// <c>VoxelChunkSampler.SampleNodeMaterial</c> (тот же <c>IslandMaterialField.At</c>),
    /// поэтому golden-hash не меняется.
    /// </remarks>
    public struct IslandMaterialAdapter : IVoxelMaterialField
    {
        /// <summary>Идентификатор острова (seed материала).</summary>
        public IslandId Id;

        public IslandMaterialAdapter(IslandId id)
        {
            Id = id;
        }

        /// <summary>Материал (VoxelMaterial в byte) в воксельной точке.</summary>
        public byte SampleMaterial(int3 voxel)
        {
            return (byte)IslandMaterialField.At(voxel.x, voxel.y, voxel.z, Id);
        }
    }
}
