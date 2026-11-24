using SmilemanStudio.Core;
using SmilemanStudio.Core.Voxels;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SmilemanStudio.Simulation.Voxels
{
    /// <summary>
    /// Заполнение кэша плотности и материала узлов одного чанка острова (ADR-0002 D3/D4).
    /// </summary>
    /// <remarks>
    /// Джоба вызывает generic-сэмплер движка <see cref="VoxelFieldSampler"/> с островным
    /// полем <see cref="IslandDensityField"/> / <see cref="IslandMaterialAdapter"/>. Чистая
    /// функция от <see cref="ChunkKey"/>, <see cref="IslandId"/> и <see cref="Radius"/>.
    /// Побитово совпадает со старым путём через VoxelChunkSampler (golden-hash мешей не меняется).
    /// </remarks>
    [BurstCompile]
    public struct SampleVoxelChunkJob : IJob
    {
        /// <summary>Ключ чанка (<see cref="VoxelChunkCoord.Pack"/>).</summary>
        public long ChunkKey;

        /// <summary>Идентификатор острова.</summary>
        public IslandId IslandId;

        /// <summary>Радиус острова, м.</summary>
        public int Radius;

        /// <summary>Выходной буфер плотности: 19³ <c>short</c>, XYZ-мажорный порядок.</summary>
        public NativeArray<short> Density;

        /// <summary>Выходной буфер материала узлов: 17³ <c>byte</c>, XYZ-мажорный порядок.</summary>
        public NativeArray<byte> NodeMaterial;

        public void Execute()
        {
            int3 origin0 = VoxelFieldSampler.ChunkOrigin0(ChunkKey);
            int lod = VoxelChunkCoord.UnpackLod(ChunkKey);
            var densityField = new IslandDensityField(IslandId, Radius);
            var materialField = new IslandMaterialAdapter(IslandId);

            VoxelFieldSampler.FillDensity(origin0, lod, densityField, Density);
            VoxelFieldSampler.FillNodeMaterial(origin0, lod, materialField, NodeMaterial);
        }
    }
}
