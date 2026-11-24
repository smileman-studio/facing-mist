using SmilemanStudio.Core;
using SmilemanStudio.Core.Voxels;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SmilemanStudio.Simulation.Voxels
{
    /// <summary>
    /// Меширование одного воксельного чанка regular cells Transvoxel (P4a/P5).
    /// </summary>
    /// <remarks>
    /// Тонкая обёртка над <see cref="TransvoxelMesher.MeshInto"/>. Вход: кэш плотности
    /// 19³ и <c>nodeMaterial</c> 17³. Метод — чистая функция входных буферов: одинаковый
    /// вход даёт побитово одинаковый меш на любой платформе.
    /// </remarks>
    [BurstCompile]
    public struct MeshVoxelChunkJob : IJob
    {
        /// <summary>
        /// Кэш плотности 19^3 <c>short</c>, XYZ-мажорный (длина
        /// <see cref="TransvoxelMesher.CacheVolume"/>). Только чтение.
        /// </summary>
        [ReadOnly] public NativeArray<short> Density;

        /// <summary>
        /// Материал узлов сетки: 17³ <c>byte</c>, XYZ-мажорный. Только чтение.
        /// </summary>
        [ReadOnly] public NativeArray<byte> NodeMaterial;

        /// <summary>Выход: позиции вершин в шагах сетки LOD0 (chunk-local). Пустой на входе.</summary>
        public NativeList<float3> Positions;

        /// <summary>Выход: нормали (единичные). Пустой на входе.</summary>
        public NativeList<float3> Normals;

        /// <summary>Выход: материал вершины (<see cref="VoxelMaterial"/> как byte). Пустой на входе.</summary>
        public NativeList<byte> Materials;

        /// <summary>Выход: индексы треугольников (3 на треугольник). Пустой на входе.</summary>
        public NativeList<uint> Indices;

        /// <summary>
        /// Seam-маска regular-слоёв (из <see cref="VoxelChunkRequest.SeamFaceMask"/>):
        /// 0 — мешировать весь чанк regular cells (LOD0 / без LOD-шва).
        /// </summary>
        public int SeamFaceMask;

        public void Execute()
        {
            TransvoxelMesher.MeshInto(Density, NodeMaterial, Positions, Normals, Materials, Indices, SeamFaceMask);
        }
    }
}
