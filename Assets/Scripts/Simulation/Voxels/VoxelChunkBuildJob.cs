using SmilemanStudio.Core.Voxels;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace SmilemanStudio.Simulation.Voxels
{
    /// <summary>
    /// Полное меширование одного чанка движком в джобе: field + request → geometry
    /// (regular + transition), ADR-0002 D7.
    /// </summary>
    /// <remarks>
    /// Generic-параметры ограничены struct + интерфейс, поэтому Burst деvirtуализирует
    /// вызовы поля: путь к плотности/материалу неизвестен джобе — она видит только
    /// <see cref="IVoxelDensityField"/> / <see cref="IVoxelMaterialField"/>. Это и есть
    /// автономность движка в job-системе.
    ///
    /// Scratch-буферы и geometry принадлежат вызывающему и переиспользуются между чанками;
    /// внутри джобы — только append в geometry.
    /// </remarks>
    [BurstCompile]
    public struct VoxelChunkBuildJob<TD, TM> : IJob
        where TD : struct, IVoxelDensityField
        where TM : struct, IVoxelMaterialField
    {
        /// <summary>Поле плотности (остров, сфера, том — джобе безразлично).</summary>
        public TD DensityField;

        /// <summary>Поле материала.</summary>
        public TM MaterialField;

        /// <summary>Заявка чанка из <see cref="VoxelLodPlanner"/>.</summary>
        public VoxelChunkRequest Request;

        /// <summary>Scratch плотности 19³ (длина CacheVolume).</summary>
        [NativeDisableParallelForRestriction] public NativeArray<short> DensityScratch;

        /// <summary>Scratch материала узлов 17³ (длина NodeMaterialVolume).</summary>
        [NativeDisableParallelForRestriction] public NativeArray<byte> NodeMatScratch;

        /// <summary>Выход: объединённая геометрия (вызывающий обязан сбросить перед вызовом).</summary>
        public VoxelChunkGeometry Geometry;

        public void Execute()
        {
            VoxelChunkMesher.BuildChunk(
                DensityField, MaterialField, Request, ref Geometry,
                DensityScratch, NodeMatScratch);
        }
    }
}
