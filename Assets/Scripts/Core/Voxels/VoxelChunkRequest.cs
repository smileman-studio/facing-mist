using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Заявка на меширование одного чанка, которую выдаёт <see cref="VoxelLodPlanner"/>.
    /// </summary>
    /// <remarks>
    /// Полностью описывает чанк для <see cref="VoxelChunkMesher"/>: где он (origin0),
    /// на каком уровне (lod) и на каких гранях нужен transition-шов (seamFaceMask).
    /// Вызывающий не вычисляет ни LOD, ни швы — только передаёт поля, границы и
    /// позицию игрока в планировщик.
    /// </remarks>
    public struct VoxelChunkRequest
    {
        /// <summary>Координата чанка на сетке его уровня <see cref="Lod"/>.</summary>
        public int3 ChunkCoord;

        /// <summary>Уровень LOD (0..LodHardMax).</summary>
        public byte Lod;

        /// <summary>Origin чанка в вокселях LOD0 (для выборки поля и позиционирования).</summary>
        public int3 Origin0;

        /// <summary>
        /// Маска граней LOD-шва: бит <see cref="TransvoxelMesher.SkipFaceBit"/>(dir)
        /// установлен, если сосед чанка через грань dir мешируется на уровне lod-1.
        /// На таких гранях regular-слой заменяется transition cells.
        /// </summary>
        public int SeamFaceMask;

        /// <summary>
        /// Собрать упакованный ключ чанка (<see cref="VoxelChunkCoord.Pack"/>).
        /// </summary>
        public readonly long ChunkKey =>
            VoxelChunkCoord.Pack(ChunkCoord.x, ChunkCoord.y, ChunkCoord.z, Lod);
    }
}
