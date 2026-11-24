using Unity.Collections;
using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Generic-заполнитель входных буферов мешера из произвольного поля плотности/
    /// материала (ADR-0002, D3/D7).
    /// </summary>
    /// <remarks>
    /// Единственный путь от <see cref="IVoxelDensityField"/>/<see cref="IVoxelMaterialField"/>
    /// к трём буферам мешера: кэш плотности 19³, узловой материал 17³ и transition-face
    /// 35x35x3. Класс не знает ни про острова, ни про сферы — только контракт поля и
    /// геометрию чанка. Это переносит «чистую» геометрию старого
    /// <c>VoxelChunkSampler</c> в движок и отвязывает её от <see cref="IslandSdf"/>.
    ///
    /// Все методы — чистые функции (origin0, lod, field) и Burst-safe: generic-параметр
    /// ограничен struct + interface, вызов идёт через constrained virtual (деvirtualize
    /// в Burst). Никаких managed-аллокаций и throw в Fill*-ядре.
    ///
    /// Единицы поля — воксели LOD0 (ADR-0002 D8): при любом LOD сэмпл кэша отображается
    /// в воксель LOD0 <c>origin0 + (i-1) &lt;&lt; lod</c> и передаётся в поле как есть.
    /// Плотность и материал — чистые функции этой точки, поэтому границы соседних чанков
    /// согласованы автоматически (apron из того же поля).
    /// </remarks>
    public static class VoxelFieldSampler
    {
        /// <summary>Размер кэша плотности: 17 сэмплов + apron по 1 с каждой стороны.</summary>
        public const int CacheSize = VoxelConstants.CacheSize;

        /// <summary>Число сэмплов самого чанка: 16 ячеек дают 17 узлов сетки.</summary>
        public const int InteriorSamples = VoxelConstants.CacheSize - 2;

        /// <summary>Число сэмплов в кэше плотности: 19³.</summary>
        public const int CacheVolume = CacheSize * CacheSize * CacheSize;

        /// <summary>Объём массива материала узлов: 17³ = 4913.</summary>
        public const int NodeMaterialVolume = InteriorSamples * InteriorSamples * InteriorSamples;

        /// <summary>Размер transition face-буфера: 35x35x3 = 3675.</summary>
        public const int FaceSamplesVolume = 35 * 35 * 3;

        // --- Геометрия чанка (перенос из VoxelChunkSampler) ---

        /// <summary>Origin чанка в вокселях LOD0: chunkCoord &lt;&lt; (ChunkSizeLog2 + lod).</summary>
        public static int3 ChunkOrigin0(int3 chunkCoord, int lod)
        {
            int shift = VoxelConstants.ChunkSizeLog2 + lod;
            return chunkCoord << shift;
        }

        /// <summary>Origin чанка в вокселях LOD0 из 64-битного ключа.</summary>
        public static int3 ChunkOrigin0(long chunkKey)
        {
            int3 coord = new int3(
                VoxelChunkCoord.UnpackX(chunkKey),
                VoxelChunkCoord.UnpackY(chunkKey),
                VoxelChunkCoord.UnpackZ(chunkKey));
            return ChunkOrigin0(coord, VoxelChunkCoord.UnpackLod(chunkKey));
        }

        /// <summary>Воксель LOD0 по полному индексу кэша (i, j, k).</summary>
        public static int3 VoxelFromIndices(int3 cacheIndex, int3 chunkOrigin0, int lod)
        {
            return new int3(
                chunkOrigin0.x + ((cacheIndex.x - 1) << lod),
                chunkOrigin0.y + ((cacheIndex.y - 1) << lod),
                chunkOrigin0.z + ((cacheIndex.z - 1) << lod));
        }

        /// <summary>Воксель LOD0 вдоль одной оси по индексу кэша 0..18.</summary>
        public static int AxisToVoxel(int axisIndex, int axis, int3 chunkOrigin0, int lod)
        {
            int origin = axis == 0 ? chunkOrigin0.x : (axis == 1 ? chunkOrigin0.y : chunkOrigin0.z);
            return origin + ((axisIndex - 1) << lod);
        }

        /// <summary>Проверить, что индекс кэша — интерьер чанка (17 сэмплов).</summary>
        public static bool IsInterior(int cacheIndex)
        {
            return cacheIndex >= 1 && cacheIndex <= InteriorSamples;
        }

        /// <summary>Проверить, что индекс кэша — apron (крайние слои 0 и 18).</summary>
        public static bool IsApron(int cacheIndex)
        {
            return cacheIndex == 0 || cacheIndex == CacheSize - 1;
        }

        // --- Generic-заполнение буферов из поля ---

        /// <summary>
        /// Заполнить кэш плотности 19³ из поля (XYZ-мажорный индекс (x*19+y)*19+z).
        /// </summary>
        /// <remarks>
        /// Буфер обязан иметь длину ровно <see cref="CacheVolume"/>; проверка длины с
        /// throw вынесена в <see cref="FillDensityChecked"/>, здесь — Burst-ядро.
        /// </remarks>
        public static void FillDensity<TD>(int3 origin0, int lod, in TD field, NativeArray<short> density)
            where TD : struct, IVoxelDensityField
        {
            for (int i = 0; i < CacheSize; i++)
            for (int j = 0; j < CacheSize; j++)
            {
                int rowBase = (i * CacheSize + j) * CacheSize;
                for (int k = 0; k < CacheSize; k++)
                {
                    int3 voxel = VoxelFromIndices(new int3(i, j, k), origin0, lod);
                    density[rowBase + k] = field.SampleDensity(voxel);
                }
            }
        }

        /// <summary>Заполнить кэш плотности с проверкой длины буфера (главный поток).</summary>
        public static void FillDensityChecked<TD>(int3 origin0, int lod, in TD field, NativeArray<short> density)
            where TD : struct, IVoxelDensityField
        {
            if (density.Length != CacheVolume)
            {
                throw new System.ArgumentException(
                    $"Буфер плотности обязан иметь длину {CacheVolume}, получено {density.Length}.",
                    nameof(density));
            }
            FillDensity(origin0, lod, field, density);
        }

        /// <summary>
        /// Заполнить узловой материал 17³ из поля. Grid узел (x,y,z) в [0,16]³ →
        /// воксель LOD0 <c>origin0 + (x,y,z) &lt;&lt; lod</c>.
        /// </summary>
        public static void FillNodeMaterial<TM>(int3 origin0, int lod, in TM field, NativeArray<byte> nodeMaterial)
            where TM : struct, IVoxelMaterialField
        {
            for (int x = 0; x < InteriorSamples; x++)
            for (int y = 0; y < InteriorSamples; y++)
            for (int z = 0; z < InteriorSamples; z++)
            {
                int3 voxel = origin0 + new int3(x << lod, y << lod, z << lod);
                nodeMaterial[(x * InteriorSamples + y) * InteriorSamples + z] = field.SampleMaterial(voxel);
            }
        }

        /// <summary>
        /// Заполнить <see cref="TransitionFaceSamples"/> грани чанка из полей.
        /// </summary>
        /// <remarks>
        /// Требует lod &gt;= 1 (transition cells существуют только на LOD-шве).
        /// Буферы Density/Material создаются, если ещё не созданы (Allocator.Temp),
        /// как в старом <c>VoxelChunkSampler.SampleFace</c>.
        /// </remarks>
        public static void FillFace<TD, TM>(
            int3 origin0, int lod, TransitionDirection dir,
            in TD densityField, in TM materialField, ref TransitionFaceSamples face)
            where TD : struct, IVoxelDensityField
            where TM : struct, IVoxelMaterialField
        {
            if (!face.Density.IsCreated)
                face.Density = new NativeArray<short>(FaceSamplesVolume, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            if (!face.Material.IsCreated)
                face.Material = new NativeArray<byte>(FaceSamplesVolume, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            int halfStep = 1 << (lod - 1);
            int3 minCorner = FaceMinCorner(dir);
            int3 bufSize = FaceBufferSize(dir);

            face.Origin = minCorner;
            face.Size = bufSize;
            face.Direction = dir;

            for (int w = -1; w <= 1; w++)
            for (int v = -1; v <= 33; v++)
            for (int u = -1; u <= 33; u++)
            {
                int3 local = TransvoxelTransitionMesher.FaceToLocalSpace(dir, u, v, w);
                int3 rel = local - minCorner;
                int idx = (rel.x * bufSize.y + rel.y) * bufSize.z + rel.z;

                int3 voxel = origin0 + local * halfStep;
                face.Density[idx] = densityField.SampleDensity(voxel);
                face.Material[idx] = materialField.SampleMaterial(voxel);
            }
        }

        /// <summary>Минимум face-буфера в chunk-local полуячейках для дирекции.</summary>
        public static int3 FaceMinCorner(TransitionDirection dir)
        {
            switch (dir)
            {
                case TransitionDirection.XMin: return new int3(-1, -1, -1);
                case TransitionDirection.XMax: return new int3(31, -1, -1);
                case TransitionDirection.YMin: return new int3(-1, -1, -1);
                case TransitionDirection.YMax: return new int3(-1, 31, -1);
                case TransitionDirection.ZMin: return new int3(-1, -1, -1);
                case TransitionDirection.ZMax: return new int3(-1, -1, 31);
                default: return new int3(-1, -1, -1);
            }
        }

        /// <summary>Размер face-буфера по осям (перестановка 35,35,3).</summary>
        public static int3 FaceBufferSize(TransitionDirection dir)
        {
            switch (dir)
            {
                case TransitionDirection.XMin: return new int3(3, 35, 35);
                case TransitionDirection.XMax: return new int3(3, 35, 35);
                case TransitionDirection.YMin: return new int3(35, 3, 35);
                case TransitionDirection.YMax: return new int3(35, 3, 35);
                case TransitionDirection.ZMin: return new int3(35, 35, 3);
                case TransitionDirection.ZMax: return new int3(35, 35, 3);
                default: return new int3(35, 35, 3);
            }
        }
    }
}
