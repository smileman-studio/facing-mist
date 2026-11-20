using SmilemanStudio.Core;
using SmilemanStudio.Core.Voxels;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SmilemanStudio.Simulation.Voxels
{
    /// <summary>
    /// Меширование одного воксельного чанка regular cells Transvoxel (P4a, ADR-0002 D7).
    /// </summary>
    /// <remarks>
    /// Тонкая обёртка над <see cref="TransvoxelMesher.MeshInto"/>: вся геометрия
    /// считается в Core-примитивах, Burst здесь нужен, чтобы вынести 16^3 ячеек с
    /// главного потока. Вход — уже заполненный кэш плотности 19^3 (см.
    /// <see cref="SampleVoxelChunkJob"/>), поэтому джоба — чистая функция от
    /// (кэш, IslandId): одинаковый вход даёт побитово одинаковый меш на любой платформе.
    ///
    /// Выход — четыре NativeList-поля, которые создаёт вызывающий (пустыми) и сам же
    /// освобождает. Выбран паттерн NativeList-полей, а не IJob&lt;TResult&gt;, потому что
    /// длина вершин/индексов неизвестна заранее, а generic IJob&lt;T&gt; в целевой сборке
    /// недоступен. Джоба только дополняет списки (append), не пересоздаёт их.
    /// </remarks>
    [BurstCompile]
    public struct MeshVoxelChunkJob : IJob
    {
        /// <summary>
        /// Кэш плотности 19^3 <c>short</c>, XYZ-мажорный (длина
        /// <see cref="TransvoxelMesher.CacheVolume"/>). Только чтение.
        /// </summary>
        [ReadOnly] public NativeArray<short> Density;

        /// <summary>Идентификатор острова (для выбора материала вершин).</summary>
        public IslandId IslandId;

        /// <summary>Выход: позиции вершин в шагах сетки LOD0 (chunk-local). Пустой на входе.</summary>
        public NativeList<float3> Positions;

        /// <summary>Выход: нормали (единичные). Пустой на входе.</summary>
        public NativeList<float3> Normals;

        /// <summary>Выход: материал вершины (<see cref="VoxelMaterial"/> как byte). Пустой на входе.</summary>
        public NativeList<byte> Materials;

        /// <summary>Выход: индексы треугольников (3 на треугольник). Пустой на входе.</summary>
        public NativeList<uint> Indices;

        public void Execute()
        {
            TransvoxelMesher.MeshInto(Density, IslandId, Positions, Normals, Materials, Indices);
        }
    }
}
