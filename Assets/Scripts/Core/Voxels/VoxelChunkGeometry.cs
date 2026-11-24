using Unity.Collections;
using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Объединённая геометрия одного чанка: regular cells + transition cells
    /// (ADR-0002, D7). Выход <see cref="VoxelChunkMesher"/>.
    /// </summary>
    /// <remarks>
    /// Позиции — chunk-local в coarse-ячейках уровня <c>Lod</c> (0..16), scale 2^lod
    /// вокселей LOD0; Presentation переводит в метры. Нормали единичные (градиент
    /// transition нормализуется при сборке). Материал — <see cref="VoxelMaterial"/> в byte.
    /// Индексы абсолютные (3 на треугольник) относительно Positions.Length.
    ///
    /// Списки принадлежат вызывающему: переиспользуются между чанками через
    /// <see cref="Reset"/> (clear без dealloc), что убирает аллокации из горячего пути.
    /// </remarks>
    public struct VoxelChunkGeometry : System.IDisposable
    {
        public NativeList<float3> Positions;
        public NativeList<float3> Normals;
        public NativeList<byte> Materials;
        public NativeList<uint> Indices;

        /// <summary>Число вершин (== Positions.Length).</summary>
        public readonly int VertexCount => Positions.Length;

        /// <summary>Число треугольников (== Indices.Length / 3).</summary>
        public readonly int TriangleCount => Indices.Length / 3;

        /// <summary>Инициализировать списки (вызывать один раз на пул).</summary>
        public void Allocate(Allocator allocator, int capacity = 64)
        {
            Positions = new NativeList<float3>(capacity, allocator);
            Normals = new NativeList<float3>(capacity, allocator);
            Materials = new NativeList<byte>(capacity, allocator);
            Indices = new NativeList<uint>(capacity * 3, allocator);
        }

        /// <summary>Очистить содержимое, сохранив ёмкость (для переиспользования).</summary>
        public void Reset()
        {
            Positions.Clear();
            Normals.Clear();
            Materials.Clear();
            Indices.Clear();
        }

        /// <summary>Освободить списки (идемпотентно).</summary>
        public void Dispose()
        {
            if (Positions.IsCreated) Positions.Dispose();
            if (Normals.IsCreated) Normals.Dispose();
            if (Materials.IsCreated) Materials.Dispose();
            if (Indices.IsCreated) Indices.Dispose();
        }
    }
}
