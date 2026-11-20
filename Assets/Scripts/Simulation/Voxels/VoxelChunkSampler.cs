using SmilemanStudio.Core;
using SmilemanStudio.Core.Voxels;
using Unity.Collections;
using Unity.Mathematics;

namespace SmilemanStudio.Simulation.Voxels
{
    /// <summary>
    /// Геометрия сэмплера чанка (ADR-0002, D3; P3.1).
    /// </summary>
    /// <remarks>
    /// Отвечает только за отображение координат — без SDF, плотности и Burst:
    ///   индекс кэша 0..18 → island-local воксель LOD0 (int3)
    ///   origin чанка в вокселях LOD0.
    ///
    /// Размер кэша = 19 = 17 + apron по одному с каждой стороны (ADR-0002, D3):
    /// 16 ячеек чанка дают 17 сэмплов-углов, apron добавляет по одному сэмплу
    /// с каждой стороны — ровно для нормалей с полу базой в один шаг сетки.
    /// Индекс i ∈ [0, 18]:
    ///   интерьер i ∈ [1, 17] — 17 сэмплов чанка.
    ///   i = 0 и i = 18 — apron.
    ///
    /// Мировые координаты не считаются здесь: float появляется в Presentation.
    /// Перевод воксель → метры — <see cref="VoxelCoord.ToMillimeters"/> / *0.5f.
    ///
    /// Формулы (k = LOD, chunkCoord — на сетке уровня k):
    ///   origin0  = chunkCoord &lt;&lt; (ChunkSizeLog2 + k)        // 16 ячеек * шаг 2^k
    ///   voxel0(i) = origin0 + (i - 1) &lt;&lt; k
    ///     i = 0  → origin0 - 2^k   (apron слева)
    ///     i = 1  → origin0         (первый сэмпл чанка)
    ///     i = 17 → origin0 + 16*2^k (последний сэмпл чанка)
    ///     i = 18 → origin0 + 17*2^k (apron справа)
    ///
    /// Apron — чистая функция того же острова: соседние чанки независимо
    /// вычисляют одну и ту же точку (ADR-0002, D3), границы согласованы.
    /// </remarks>
    public static class VoxelChunkSampler
    {
        /// <summary>Размер кэша плотности: 17 сэмплов + apron по 1 с каждой стороны.</summary>
        public const int CacheSize = VoxelConstants.CacheSize;

        /// <summary>Число сэмплов самого чанка: 16 ячеек дают 17 узлов сетки.</summary>
        public const int InteriorSamples = VoxelConstants.CacheSize - 2;

        /// <summary>
        /// Воксель LOD0 для индекса кэша вдоль одной оси.
        /// </summary>
        /// <param name="axisIndex">Индекс в кэше 0..18 по этой оси.</param>
        /// <param name="axis">Ось: 0 = X, 1 = Y, 2 = Z.</param>
        /// <param name="chunkOrigin0">Origin чанка в вокселях LOD0.</param>
        /// <param name="lod">Уровень LOD (0..LodHardMax).</param>
        /// <returns>Компонента вокселя LOD0 вдоль оси <paramref name="axis"/>.</returns>
        /// <remarks>
        /// Скалярный вариант нужен для тестов и одномерных проверок. Полная
        /// трёхмерная координата — <see cref="VoxelFromIndices(int3,int3,int)"/>.
        /// Ось передаётся явно: без неё сдвиг пришлось бы применить ко всем трём
        /// компонентам, и сэмпл уехал бы по диагонали.
        /// </remarks>
        public static int AxisToVoxel(int axisIndex, int axis, int3 chunkOrigin0, int lod)
        {
            int origin = axis == 0 ? chunkOrigin0.x : (axis == 1 ? chunkOrigin0.y : chunkOrigin0.z);
            return origin + ((axisIndex - 1) << lod);
        }

        /// <summary>
        /// Воксель LOD0 по полному индексу кэша (i, j, k).
        /// </summary>
        /// <param name="cacheIndex">Индексы кэша по X, Y, Z, каждый 0..18.</param>
        /// <param name="chunkOrigin0">Origin чанка в вокселях LOD0.</param>
        /// <param name="lod">Уровень LOD (0..LodHardMax).</param>
        public static int3 VoxelFromIndices(int3 cacheIndex, int3 chunkOrigin0, int lod)
        {
            return new int3(
                chunkOrigin0.x + ((cacheIndex.x - 1) << lod),
                chunkOrigin0.y + ((cacheIndex.y - 1) << lod),
                chunkOrigin0.z + ((cacheIndex.z - 1) << lod));
        }

        /// <summary>
        /// Вычислить origin чанка в единицах вокселей LOD0.
        /// </summary>
        /// <param name="chunkCoord">Координата чанка на уровне lod.</param>
        /// <param name="lod">Уровень LOD (0..LodHardMax).</param>
        /// <returns>Origin чанка в вокселях LOD0.</returns>
        /// <remarks>
        /// На уровне lod шаг ячейки = 2^lod вокселей LOD0, поэтому
        /// origin0 = chunkCoord * (16 * 2^lod) = chunkCoord &lt;&lt; (4 + lod).
        /// </remarks>
        public static int3 ChunkOrigin0(int3 chunkCoord, int lod)
        {
            int shift = VoxelConstants.ChunkSizeLog2 + lod;
            return chunkCoord << shift;
        }

        /// <summary>
        /// Origin чанка в вокселях LOD0 из его 64-битного ключа.
        /// </summary>
        /// <param name="chunkKey">Ключ чанка (<see cref="VoxelChunkCoord.Pack"/>, валиден).</param>
        public static int3 ChunkOrigin0(long chunkKey)
        {
            int3 coord = new int3(
                VoxelChunkCoord.UnpackX(chunkKey),
                VoxelChunkCoord.UnpackY(chunkKey),
                VoxelChunkCoord.UnpackZ(chunkKey));
            return ChunkOrigin0(coord, VoxelChunkCoord.UnpackLod(chunkKey));
        }

        /// <summary>
        /// Воксель LOD0 сэмпла кэша из ключа чанка и индекса кэша вдоль одной оси.
        /// </summary>
        /// <param name="chunkKey">Ключ чанка (<see cref="VoxelChunkCoord.Pack"/>, валиден).</param>
        /// <param name="axisIndex">Индекс в кэше 0..18 по этой оси.</param>
        /// <param name="axis">Ось: 0 = X, 1 = Y, 2 = Z.</param>
        public static int AxisToVoxel(long chunkKey, int axisIndex, int axis)
        {
            return AxisToVoxel(axisIndex, axis, ChunkOrigin0(chunkKey), VoxelChunkCoord.UnpackLod(chunkKey));
        }

        /// <summary>
        /// Воксель LOD0 сэмпла кэша из ключа чанка и полного индекса кэша.
        /// </summary>
        /// <param name="chunkKey">Ключ чанка (<see cref="VoxelChunkCoord.Pack"/>, валиден).</param>
        /// <param name="cacheIndex">Индексы кэша по X, Y, Z, каждый 0..18.</param>
        public static int3 VoxelFromIndices(long chunkKey, int3 cacheIndex)
        {
            return VoxelFromIndices(
                cacheIndex,
                ChunkOrigin0(chunkKey),
                VoxelChunkCoord.UnpackLod(chunkKey));
        }

        /// <summary>
        /// Проверить, что индекс кэша — интерьер чанка (17 сэмплов).
        /// </summary>
        public static bool IsInterior(int cacheIndex)
        {
            return cacheIndex >= 1 && cacheIndex <= InteriorSamples;
        }

        /// <summary>
        /// Проверить, что индекс кэша — apron (крайние слои 0 и 18).
        /// </summary>
        public static bool IsApron(int cacheIndex)
        {
            return cacheIndex == 0 || cacheIndex == CacheSize - 1;
        }

        /// <summary>Число сэмплов в кэше плотности: 19³.</summary>
        public const int CacheVolume = CacheSize * CacheSize * CacheSize;

        /// <summary>Плотность чанка целиком: 19³ <c>short</c>, XYZ-мажорный порядок.</summary>
        public static NativeArray<short> Sample(
            long chunkKey,
            IslandId islandId,
            int radius,
            Allocator allocator)
        {
            var density = new NativeArray<short>(CacheVolume, allocator, NativeArrayOptions.UninitializedMemory);
            Sample(chunkKey, islandId, radius, density);
            return density;
        }

        /// <summary>
        /// Заполнить готовый буфер плотности чанка (19³ <c>short</c>).
        /// </summary>
        /// <remarks>
        /// Порядок — XYZ-мажорный: индекс = (x * 19 + y) * 19 + z.
        /// Значения вне short клампятся <see cref="VoxelDensity.Quantize"/>.
        /// Буфер обязан иметь длину ровно <see cref="CacheVolume"/>.
        /// </remarks>
        public static void Sample(
            long chunkKey,
            IslandId islandId,
            int radius,
            NativeArray<short> density)
        {
            if (density.Length != CacheVolume)
            {
                throw new System.ArgumentException(
                    $"Буфер плотности обязан иметь длину {CacheVolume}, получено {density.Length}.",
                    nameof(density));
            }

            SampleCore(chunkKey, islandId, radius, density);
        }

        /// <summary>
        /// Заполнить буфер плотности без проверок — вызов пригоден для Burst.
        /// </summary>
        /// <param name="chunkKey">Ключ чанка (<see cref="VoxelChunkCoord.Pack"/>).</param>
        /// <param name="islandId">Идентификатор острова.</param>
        /// <param name="radius">Радиус острова, м.</param>
        /// <param name="density">
        /// Буфер ровно на <see cref="CacheVolume"/> элементов. Длина не проверяется:
        /// контракт — обязанность вызывающего, иначе индекс выйдет за границы.
        /// </param>
        /// <remarks>
        /// Тело вынесено отдельно, потому что проверка длины с <c>throw</c> не
        /// компилируется в Burst. Порядок — XYZ-мажорный: (x * 19 + y) * 19 + z.
        /// </remarks>
        public static void SampleCore(
            long chunkKey,
            IslandId islandId,
            int radius,
            NativeArray<short> density)
        {
            int lod = VoxelChunkCoord.UnpackLod(chunkKey);
            int3 origin0 = ChunkOrigin0(chunkKey);

            for (int i = 0; i < CacheSize; i++)
            {
                for (int j = 0; j < CacheSize; j++)
                {
                    int rowBase = (i * CacheSize + j) * CacheSize;

                    for (int k = 0; k < CacheSize; k++)
                    {
                        int3 voxel = VoxelFromIndices(new int3(i, j, k), origin0, lod);
                        int sdf = IslandSdf.SdfVoxel(voxel, islandId, radius);
                        density[rowBase + k] = VoxelDensity.Quantize(sdf);
                    }
                }
            }
        }

        /// <summary>
        /// Плотность сэмпла кэша по его индексам и ключу чанка.
        /// </summary>
        /// <remarks>
        /// Эквивалент выборки из <see cref="Sample(long,IslandId,int,NativeArray{short})"/>
        /// без построения буфера — для точечных проверок и отладки.
        /// </remarks>
        public static short SampleOne(long chunkKey, int i, int j, int k, IslandId islandId, int radius)
        {
            int3 voxel = VoxelFromIndices(chunkKey, new int3(i, j, k));
            return VoxelDensity.Quantize(IslandSdf.SdfVoxel(voxel, islandId, radius));
        }
    }
}
