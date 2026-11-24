using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Аналитическая сфера как источник плотности движка (ADR-0002, D7).
    /// </summary>
    /// <remarks>
    /// Демонстрирует автономность движка: форма задаётся одним <see cref="IVoxelDensityField"/>,
    /// движку безразлично, остров это или сфера. Плотность — signed distance сферы радиуса
    /// <c>RadiusVox</c> вокселей LOD0 вокруг <c>Center</c>, квантованная как у острова
    /// (256 единиц на воксель LOD0). Метр не появляется: scale делает Presentation.
    /// </remarks>
    public struct SphereDensityField : IVoxelDensityField
    {
        /// <summary>Центр сферы в вокселях LOD0.</summary>
        public int3 Center;

        /// <summary>Радиус сферы в вокселях LOD0.</summary>
        public int RadiusVox;

        public SphereDensityField(int3 center, int radiusVox)
        {
            Center = center;
            RadiusVox = radiusVox;
        }

        public short SampleDensity(int3 voxel)
        {
            float sdf = math.length((float3)(voxel - Center)) - RadiusVox;
            return VoxelDensity.Quantize(sdf);
        }
    }

    /// <summary>
    /// Пустое поле материала: всегда <see cref="VoxelMaterial.None"/>.
    /// </summary>
    /// <remarks>
    /// Для форм без материала (сфера). Раскраску вершин задаёт вызывающий, а не движок.
    /// </remarks>
    public struct NullMaterialField : IVoxelMaterialField
    {
        public byte SampleMaterial(int3 voxel) => (byte)VoxelMaterial.None;
    }
}
