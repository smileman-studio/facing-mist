using SmilemanStudio.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace SmilemanStudio.Simulation.Voxels
{
    /// <summary>
    /// Заполнение кэша плотности одного чанка (P3.4, ADR-0002 D3/D4).
    /// </summary>
    /// <remarks>
    /// Тонкая обёртка над <see cref="VoxelChunkSampler.SampleCore"/>: вся математика
    /// живёт в Core-примитивах, Burst здесь нужен только чтобы вынести 19³ сэмплов
    /// с главного потока. Джоба не аллоцирует и не читает состояние мира — это чистая
    /// функция от <see cref="ChunkKey"/>, <see cref="IslandId"/> и <see cref="Radius"/>,
    /// поэтому одинаковый вход даёт побитно одинаковый буфер на любой платформе.
    ///
    /// <see cref="Density"/> обязан быть длиной
    /// <see cref="VoxelChunkSampler.CacheVolume"/>: контракт проверяется снаружи,
    /// внутри Burst <c>throw</c> недоступен.
    /// </remarks>
    [BurstCompile]
    public struct SampleVoxelChunkJob : IJob
    {
        /// <summary>Ключ чанка (<see cref="SmilemanStudio.Core.Voxels.VoxelChunkCoord.Pack"/>).</summary>
        public long ChunkKey;

        /// <summary>Идентификатор острова.</summary>
        public IslandId IslandId;

        /// <summary>Радиус острова, м.</summary>
        public int Radius;

        /// <summary>Выходной буфер плотности: 19³ <c>short</c>, XYZ-мажорный порядок.</summary>
        public NativeArray<short> Density;

        public void Execute()
        {
            VoxelChunkSampler.SampleCore(ChunkKey, IslandId, Radius, Density);
        }
    }
}
