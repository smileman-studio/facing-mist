using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Грань чанка, на которой строятся transition cells (P4b, ADR-0002 D7).
    /// </summary>
    public enum TransitionDirection
    {
        /// <summary>Нет грани (невалидное значение по умолчанию).</summary>
        None = 0,
        /// <summary>Грань x = 0.</summary>
        XMin = 1,
        /// <summary>Грань y = 0.</summary>
        YMin = 2,
        /// <summary>Грань z = 0.</summary>
        ZMin = 3,
        /// <summary>Грань x = 16.</summary>
        XMax = 4,
        /// <summary>Грань y = 16.</summary>
        YMax = 5,
        /// <summary>Грань z = 16.</summary>
        ZMax = 6,
    }

    /// <summary>
    /// Буфер сэмпов у переходной грани чанка (P5).
    /// </summary>
    /// <remarks>
    /// Раскладка chunk-local полуячейки: в плоскости грани [-1, 33] × [-1, 33]
    /// (35×35 = 1225), по нормали [-1, 0, +1] (3 слоя). Итого 3675 сэмплов.
    /// Заполняется вызывающим через <see cref="VoxelFieldSampler"/>; мешер читает
    /// только его — ни <see cref="IslandSdf"/>, ни <see cref="IslandMaterialField"/>.
    /// <para/>
    /// <c>Origin</c> — минимум буфера в chunk-local полуячейках.
    /// <c>Size</c> — размер по осям (перестановка 35,35,3).
    /// </remarks>
    public struct TransitionFaceSamples
    {
        /// <summary>Плотность грани: 35×35×3 = 3675 <c>short</c>.</summary>
        public NativeArray<short> Density;

        /// <summary>Материал грани: 35×35×3 = 3675 <c>byte</c>.</summary>
        public NativeArray<byte> Material;

        /// <summary>Chunk-local полуячейка: минимум буфера (перестановка -1/-1/-1 по осям).</summary>
        public int3 Origin;

        /// <summary>Размер буфера по осям (перестановка 35,35,3).</summary>
        public int3 Size;

        /// <summary>Направление грани.</summary>
        public TransitionDirection Direction;

        /// <summary>Плотность по face-local полуячейкам (u,v,w), u,v∈[-1,33], w∈[-1,1].</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public short GetDensityFace(int u, int v, int w)
        {
            return Density[ToIndex(u, v, w)];
        }

        /// <summary>Материал по face-local полуячейкам.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetMaterialFace(int u, int v, int w)
        {
            return Material[ToIndex(u, v, w)];
        }

        /// <summary>
        /// Узловой градиент в мировых осях по face-local узлу (u,v,w).
        /// Центральная разность плотности по буферу ±1 полуячейка.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int3 NodeGradientWorld(int u, int v, int w)
        {
            int3 local = TransvoxelTransitionMesher.FaceToLocalSpace(Direction, u, v, w);
            return new int3(
                GetDensityChunkLocal(local.x + 1, local.y, local.z) -
                GetDensityChunkLocal(local.x - 1, local.y, local.z),
                GetDensityChunkLocal(local.x, local.y + 1, local.z) -
                GetDensityChunkLocal(local.x, local.y - 1, local.z),
                GetDensityChunkLocal(local.x, local.y, local.z + 1) -
                GetDensityChunkLocal(local.x, local.y, local.z - 1));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ToIndex(int u, int v, int w)
        {
            int3 local = TransvoxelTransitionMesher.FaceToLocalSpace(Direction, u, v, w);
            int3 rel = local - Origin;
            return (rel.x * Size.y + rel.y) * Size.z + rel.z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private short GetDensityChunkLocal(int cx, int cy, int cz)
        {
            int3 rel = new int3(cx, cy, cz) - Origin;
            return Density[(rel.x * Size.y + rel.y) * Size.z + rel.z];
        }
    }

    /// <summary>
    /// Меширование transition cells Transvoxel (Laine-Krahenbuhl, GDC 2006), LOD &gt;= 1 (P4b).
    /// </summary>
    /// <remarks>
    /// Transition cell — квадратная сетка 2x2 на грани чанка уровня lod, примыкающей
    /// к чанку уровня lod-1. Углы ячейки — 13 точек в полуячейках:
    ///   - 4 угла coarse-сетки (0, 2, 6, 8) и 4 алиаса (9..12) — из кэша плотности 19^3;
    ///   - 5 вторичных точек (1, 3, 4, 5, 7) — из <see cref="TransitionFaceSamples"/>.
    ///
    /// Мешер — чистая функция входных буферов: <c>density</c> (19³),
    /// <c>nodeMaterial</c> (17³) и <c>face</c> (3675 сэмплов). Кто и чем их наполнил,
    /// ему знать не надо.
    ///
    /// Отличия от эталона transvoxel_core (решения P4b/P5):
    ///   - D1: бисекция удалена, единая Q16-интерполяция → шов герметичен.
    ///   - D2: secondary vertices удалены.
    ///   - D3: единая система координат выхода — Q16 в полуячейках × 2⁻¹⁷.
    ///   - D4: нормаль = трилинейная интерполяция узловых градиентов из буфера.
    ///   - P5: мешер не зависит от <see cref="IslandSdf"/> и <see cref="IslandMaterialField"/>.
    ///     Материал вершин берётся из <c>nodeMaterial</c> (regular) и <c>face.Material</c>
    ///     (transition seam). Координаты материалов — chunk-local, едины для regular и transition.
    ///
    /// Детерминизм: только int/long-арифметика и точные конвертации float.
    /// </remarks>
    public static class TransvoxelTransitionMesher
    {
        /// <summary>Число coarse-ячеек грани чанка по каждой оси (16).</summary>
        public const int CellsPerAxis = 1 << VoxelConstants.ChunkSizeLog2;

        /// <summary>Размер грани в полуячейках (32 = 16 * 2).</summary>
        public const int HalfCellsPerAxis = CellsPerAxis * 2;

        /// <summary>Число углов transition cell (13 = 3x3 грань + 4 алиаса).</summary>
        public const int CornerCount = 13;

        /// <summary>Число слотов кэша вершин transition (cacheIdx 0..9).</summary>
        private const int CacheSlots = 10;

        /// <summary>Конвертация Q16-полуячеек в ячейки lod-1: 2^-17 (точная).</summary>
        private const float InvQ16HalfCell = 1f / 131072f;

        /// <summary>
        /// Перестановка грань-локальных координат в локальные координаты чанка
        /// (в полуячейках, грань 0..32). Ось z грань-пространства — нормаль грани.
        /// </summary>
        /// <remarks>
        /// Та же перестановка, что в эталоне transvoxel_core FaceToLocalSpace, но
        /// chunkSize фиксирован = <see cref="HalfCellsPerAxis"/> и только целочисленная
        /// версия (float-перегрузка не нужна после удаления бисекции, D1).
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int3 FaceToLocalSpace(TransitionDirection direction, int x, int y, int z)
        {
            switch (direction)
            {
                case TransitionDirection.XMin: return new int3(z, x, y);
                case TransitionDirection.XMax: return new int3(HalfCellsPerAxis - z, y, x);
                case TransitionDirection.YMin: return new int3(y, z, x);
                case TransitionDirection.YMax: return new int3(x, HalfCellsPerAxis - z, y);
                case TransitionDirection.ZMin: return new int3(x, y, z);
                case TransitionDirection.ZMax: return new int3(y, x, HalfCellsPerAxis - z);
                default: return new int3(x, y, z);
            }
        }

        /// <summary>
        /// Воксель LOD0 точки грани по её грань-локальным полуячейкам (x, y на грани, z = 0).
        /// </summary>
        /// <remarks>
        /// Точная целочисленная привязка: шаг полуячейки = 2^(lod-1) вокселей LOD0,
        /// поэтому любое (x, y, 0) из диапазона 0..32 отображается в валидную int3-точку
        /// без округления (решение D1: fractional-входов в генератор не существует).
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int3 FaceVoxel0(TransitionDirection direction, int3 chunkOrigin0, int halfStep, int x, int y)
        {
            return chunkOrigin0 + FaceToLocalSpace(direction, x, y, 0) * halfStep;
        }

        

        /// <summary>
        /// Меширование transition cells одной грани чанка в готовые growable-выходы (P4b/P5).
        /// </summary>
        /// <param name="density">
        /// Кэш плотности чанка уровня lod: 19^3 <c>short</c>, XYZ-мажорный.
        /// </param>
        /// <param name="nodeMaterial">
        /// Материал узловой сетки чанка: 17^3 <c>byte</c>, XYZ-мажорный.
        /// Плоский фартук материал из него не читает (берёт всё с плоскости шва
        /// из <paramref name="face"/>); параметр сохранён ради единого контракта
        /// буферов regular/transition.
        /// </param>
        /// <param name="face">
        /// Буфер сэмпов у грани: <see cref="TransitionFaceSamples"/>.
        /// </param>
        /// <param name="direction">Грань чанка, на которой строятся transition cells.</param>
        /// <param name="positions">Выход: в coarse-ячейках текущего lod (0..16), Q16-полуячейки × 2⁻¹⁷.</param>
        /// <param name="gradients">Выход: целочисленные градиенты (нормализует Presentation).</param>
        /// <param name="materials">Выход: материалы вершин (<see cref="VoxelMaterial"/> в byte).</param>
        /// <param name="indices">Выход: индексы треугольников (3 на треугольник).</param>
        /// <remarks>
        /// Мешер — чистая функция входных буферов. Guard <c>lod &gt;= 1</c> — обязанность вызывающего.
        /// </remarks>
        public static void MeshTransitionInto(
            NativeArray<short> density,
            NativeArray<byte> nodeMaterial,
            in TransitionFaceSamples face,
            TransitionDirection direction,
            NativeList<float3> positions,
            NativeList<int3> gradients,
            NativeList<byte> materials,
            NativeList<uint> indices)
        {
            int cacheVolume = CellsPerAxis * CacheSlots;

            // Кэш вершин грани: два дока по 16 * 10 int, инициализированы -1 (D5).
            var currentCache = new NativeArray<int>(cacheVolume, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var previousCache = new NativeArray<int>(cacheVolume, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < cacheVolume; i++)
            {
                currentCache[i] = -1;
                previousCache[i] = -1;
            }

            var cellValues = new NativeArray<short>(CornerCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var vertexIndices = new NativeArray<int>(TransvoxelTransitionTables.MaxVertexDataPerCase, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            for (int y = 0; y < CellsPerAxis; y++)
            {
                for (int x = 0; x < CellsPerAxis; x++)
                {
                    // 1. Углы ячейки (полуячейки: x, y в 0..32).
                    //    4 угла coarse-сетки — из кэша 19³; индекс = local + apron(1).
                    cellValues[0] = TransvoxelMesher.GetDensity(density, FaceToLocalSpace(direction, 2 * x, 2 * y, 0) / 2 + 1);
                    cellValues[2] = TransvoxelMesher.GetDensity(density, FaceToLocalSpace(direction, 2 * x + 2, 2 * y, 0) / 2 + 1);
                    cellValues[6] = TransvoxelMesher.GetDensity(density, FaceToLocalSpace(direction, 2 * x, 2 * y + 2, 0) / 2 + 1);
                    cellValues[8] = TransvoxelMesher.GetDensity(density, FaceToLocalSpace(direction, 2 * x + 2, 2 * y + 2, 0) / 2 + 1);

                    //    5 вторичных точек (середины coarse-рёбер и центр) — из face.
                    cellValues[1] = face.GetDensityFace(2 * x + 1, 2 * y, 0);
                    cellValues[3] = face.GetDensityFace(2 * x, 2 * y + 1, 0);
                    cellValues[4] = face.GetDensityFace(2 * x + 1, 2 * y + 1, 0);
                    cellValues[5] = face.GetDensityFace(2 * x + 2, 2 * y + 1, 0);
                    cellValues[7] = face.GetDensityFace(2 * x + 1, 2 * y + 2, 0);

                    //    4 угла low-res грани (глубина 2 полуячейки = 1 coarse-ячейка) —
                    //    узлы кэша 19³ на глубине 1, как в эталоне transvoxel_core.
                    // Эталон transvoxel_core: low-res углы дублируют углы грани.
                    // Полоса — плоский фартук на плоскости шва; low-res кривая задана
                    // coarse-узлами НА ЭТОЙ ЖЕ плоскости.
                    cellValues[9]  = cellValues[0];
                    cellValues[10] = cellValues[2];
                    cellValues[11] = cellValues[6];
                    cellValues[12] = cellValues[8];

                    // 2. Case code: 9 бит, порядок обхода углов 0,1,2,5,8,7,6,3,4.
                    int caseCode = ((cellValues[0] < 0 ? 1 : 0)
                                  | (cellValues[1] < 0 ? 2 : 0)
                                  | (cellValues[2] < 0 ? 4 : 0)
                                  | (cellValues[5] < 0 ? 8 : 0)
                                  | (cellValues[8] < 0 ? 16 : 0)
                                  | (cellValues[7] < 0 ? 32 : 0)
                                  | (cellValues[6] < 0 ? 64 : 0)
                                  | (cellValues[3] < 0 ? 128 : 0)
                                  | (cellValues[4] < 0 ? 256 : 0));

                    // Сброс слотов 0,1,2,7 кэша текущей ячейки — ДО continue.
                    currentCache[0 * CellsPerAxis + x] = -1;
                    currentCache[1 * CellsPerAxis + x] = -1;
                    currentCache[2 * CellsPerAxis + x] = -1;
                    currentCache[7 * CellsPerAxis + x] = -1;

                    if (caseCode == 0 || caseCode == 511)
                    {
                        continue;
                    }

                    // Кэшируема ли вершина по осям грани (2 бита).
                    int cacheValidator = (x != 0 ? 0b01 : 0) | (y != 0 ? 0b10 : 0);

                    // 3. Lookup: класс -> геометрия, case code -> edge codes.
                    int cellClass = TransvoxelTransitionTables.TransitionCellClass[caseCode];
                    int classIdx = cellClass & 0x7F;
                    int geoCounts = TransvoxelTransitionTables.TransitionCellGeometryCounts[classIdx];
                    int vertexCount = (geoCounts >> 4) & 0x0F;
                    int triCount = geoCounts & 0x0F;
                    int vertexBase = caseCode * TransvoxelTransitionTables.MaxVertexDataPerCase;
                    int triBase = classIdx * TransvoxelTransitionTables.MaxCellTriangleIndices;
                    bool flipWinding = (cellClass & 0x80) != 0;

                    // 4. Вершины (рёбра).
                    for (int i = 0; i < vertexCount; i++)
                    {
                        ushort edgeCode = TransvoxelTransitionTables.TransitionVertexDataFlat[vertexBase + i];
                        int c0 = (edgeCode >> 4) & 0x0F;
                        int c1 = edgeCode & 0x0F;

                        short d0 = cellValues[c0];
                        short d1 = cellValues[c1];

                        int cacheIdx = (edgeCode >> 8) & 0x0F;
                        int cacheDir = edgeCode >> 12;

                        // Угол точно на поверхности (d == 0).
                        if (d1 == 0)
                        {
                            byte cornerData = TransvoxelTransitionTables.TransitionCornerData[c1];
                            cacheDir = (cornerData >> 4) & 0x0F;
                            cacheIdx = cornerData & 0x0F;
                        }
                        else if (d0 == 0)
                        {
                            byte cornerData = TransvoxelTransitionTables.TransitionCornerData[c0];
                            cacheDir = (cornerData >> 4) & 0x0F;
                            cacheIdx = cornerData & 0x0F;
                        }

                        bool cacheable = (cacheDir & cacheValidator) == cacheDir;
                        int cachePosX = x - (cacheDir & 1);
                        var dock = (cacheDir & 2) != 0 ? previousCache : currentCache;

                        int vertexIndex = -1;
                        if (cacheable)
                        {
                            vertexIndex = dock[cacheIdx * CellsPerAxis + cachePosX];
                        }

                        if (!cacheable || vertexIndex == -1)
                        {
                            vertexIndex = positions.Length;

                            // 5. Позиция: единая Q16-интерполяция (D1).
                            int3 h0 = FaceToLocalSpace(direction,
                                2 * x + TransvoxelTransitionTables.TransitionCornerOffset[c0 * 3],
                                2 * y + TransvoxelTransitionTables.TransitionCornerOffset[c0 * 3 + 1],
                                0);
                            int3 h1 = FaceToLocalSpace(direction,
                                2 * x + TransvoxelTransitionTables.TransitionCornerOffset[c1 * 3],
                                2 * y + TransvoxelTransitionTables.TransitionCornerOffset[c1 * 3 + 1],
                                0);

                            int t = TransvoxelMesher.EdgeT(d0, d1);
                            long qx = (long)h0.x * 65536 + (long)(h1.x - h0.x) * t;
                            long qy = (long)h0.y * 65536 + (long)(h1.y - h0.y) * t;
                            long qz = (long)h0.z * 65536 + (long)(h1.z - h0.z) * t;

                            // 6. Опорный воксель вершины (D4).
                            int abs0 = math.abs((int)d0);
                            int abs1 = math.abs((int)d1);
                            int anchor = abs0 < abs1 ? c0 : (abs1 < abs0 ? c1 : (c0 < c1 ? c0 : c1));

                            // 7. Градиент (D4a): трилинейная интерполяция узловых градиентов.
                            //    qw == 0: узлы МЕЛКОЙ сетки из face-буфера.
                            //    qw > 0:  узлы coarse-кэша из density.
                            long qu = (long)(2 * x + TransvoxelTransitionTables.TransitionCornerOffset[c0 * 3]) * 65536
                                    + (long)(TransvoxelTransitionTables.TransitionCornerOffset[c1 * 3]
                                           - TransvoxelTransitionTables.TransitionCornerOffset[c0 * 3]) * t;
                            long qv = (long)(2 * y + TransvoxelTransitionTables.TransitionCornerOffset[c0 * 3 + 1]) * 65536
                                    + (long)(TransvoxelTransitionTables.TransitionCornerOffset[c1 * 3 + 1]
                                           - TransvoxelTransitionTables.TransitionCornerOffset[c0 * 3 + 1]) * t;
                            // Фартук плоский, но нормали двойные, как в эталоне: вершины
                            // low-res стороны фартука (cacheIdx > 6) получают нормаль из
                            // coarse-кэша (побитово = own-regular на плоскости шва),
                            // остальные — из face-буфера (побитово = fine-сосед).
                            bool lowResFace = cacheIdx > 6;

                            int3 gI;
                            if (!lowResFace)
                            {
                                int fnx = (int)(qu >> 16) - 2 * x;
                                int fny = (int)(qv >> 16) - 2 * y;
                                int ffx = (int)(qu & 0xFFFF);
                                int ffy = (int)(qv & 0xFFFF);
                                int fnx1 = ffx == 0 ? fnx : fnx + 1;
                                int fny1 = ffy == 0 ? fny : fny + 1;
                                // Узловые градиенты из face-буфера (центральная разность ±1 полуячейка).
                                int3 g00 = face.NodeGradientWorld(2 * x + fnx,  2 * y + fny,  0);
                                int3 g10 = face.NodeGradientWorld(2 * x + fnx1, 2 * y + fny,  0);
                                int3 g01 = face.NodeGradientWorld(2 * x + fnx,  2 * y + fny1, 0);
                                int3 g11 = face.NodeGradientWorld(2 * x + fnx1, 2 * y + fny1, 0);
                                // Локальные оси грань-направлений u и v: свёртка in-plane
                                // в порядке возрастания локальной оси (как у fine-соседа).
                                int3 eU = FaceToLocalSpace(direction, 1, 0, 0);
                                int3 eV = FaceToLocalSpace(direction, 0, 1, 0);
                                int au = eU.x != 0 ? 0 : (eU.y != 0 ? 1 : 2);
                                int av = eV.x != 0 ? 0 : (eV.y != 0 ? 1 : 2);
                                gI = au < av
                                    ? TransvoxelMesher.TrilinearGradient(g00, g10, g01, g11, g00, g10, g01, g11, ffx, ffy, 0)
                                    : TransvoxelMesher.TrilinearGradient(g00, g01, g10, g11, g00, g01, g10, g11, ffy, ffx, 0);
                            }
                            else
                            {
                                // Q16 coarse-ячеек в ЛОКАЛЬНЫХ координатах.
                                long cx = qx >> 1, cy = qy >> 1, cz = qz >> 1;
                                int3 c000 = new int3((int)(cx >> 16), (int)(cy >> 16), (int)(cz >> 16)) + 1;
                                gI = TransvoxelMesher.TrilinearGradient(
                                    TransvoxelMesher.NodeGradient(density, c000),
                                    TransvoxelMesher.NodeGradient(density, c000 + new int3(1, 0, 0)),
                                    TransvoxelMesher.NodeGradient(density, c000 + new int3(0, 1, 0)),
                                    TransvoxelMesher.NodeGradient(density, c000 + new int3(1, 1, 0)),
                                    TransvoxelMesher.NodeGradient(density, c000 + new int3(0, 0, 1)),
                                    TransvoxelMesher.NodeGradient(density, c000 + new int3(1, 0, 1)),
                                    TransvoxelMesher.NodeGradient(density, c000 + new int3(0, 1, 1)),
                                    TransvoxelMesher.NodeGradient(density, c000 + new int3(1, 1, 1)),
                                    (int)(cx & 0xFFFF), (int)(cy & 0xFFFF), (int)(cz & 0xFFFF));
                            }

                            // 8. Материал (P5): фартук плоский, поэтому материал берётся
                            //    С ПЛОСКОСТИ ШВА — в грани-локальных полуячейках опорного угла.
                            //    Для low-res вершины это узел coarse-сетки на плоскости
                            //    (побитово = own-regular), для fine-вершины — узел мелкой сетки
                            //    (побитово = fine-сосед). Прежнее чтение на одну coarse-ячейку
                            //    вглубь чанка давало цветовой шов по границе LOD.
                            int aU = 2 * x + TransvoxelTransitionTables.TransitionCornerOffset[anchor * 3];
                            int aV = 2 * y + TransvoxelTransitionTables.TransitionCornerOffset[anchor * 3 + 1];
                            byte mat = face.GetMaterialFace(aU, aV, 0);

                            positions.Add(new float3((float)qx, (float)qy, (float)qz) * InvQ16HalfCell);
                            gradients.Add(gI);
                            materials.Add(mat);

                            // 9. Запись в кэш вершин.
                            if (cacheDir == 8)
                            {
                                currentCache[cacheIdx * CellsPerAxis + x] = vertexIndex;
                            }
                            else if (cacheable)
                            {
                                dock[cacheIdx * CellsPerAxis + cachePosX] = vertexIndex;
                            }
                        }

                        vertexIndices[i] = vertexIndex;
                    }
                    
                    // 10. Треугольники. Transition-таблицы хранят базовый обход НАРУЖУ
                    // (в отличие от regular — там внутрь), поэтому !flipWinding → прямой порядок,
                    // flipWinding (complement) → реверс. Инверсия обеих веток = полоса наизнанку.
                    int triVertCount = triCount * 3;
                    if (!flipWinding)
                    {
                        for (int i = 0; i < triVertCount; i += 3)
                        {
                            indices.Add((uint)vertexIndices[TransvoxelTransitionTables.TransitionCellTriangleIndices[triBase + i + 0]]);
                            indices.Add((uint)vertexIndices[TransvoxelTransitionTables.TransitionCellTriangleIndices[triBase + i + 1]]);
                            indices.Add((uint)vertexIndices[TransvoxelTransitionTables.TransitionCellTriangleIndices[triBase + i + 2]]);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < triVertCount; i += 3)
                        {
                            indices.Add((uint)vertexIndices[TransvoxelTransitionTables.TransitionCellTriangleIndices[triBase + i + 2]]);
                            indices.Add((uint)vertexIndices[TransvoxelTransitionTables.TransitionCellTriangleIndices[triBase + i + 1]]);
                            indices.Add((uint)vertexIndices[TransvoxelTransitionTables.TransitionCellTriangleIndices[triBase + i + 0]]);
                        }
                    }
                }

                // Swap dock-ов между строками y.
                var tmp = currentCache;
                currentCache = previousCache;
                previousCache = tmp;
            }

            // Освобождаем служебные scratch-буферы; выходные списки — вызывающего.
            currentCache.Dispose();
            previousCache.Dispose();
            cellValues.Dispose();
            vertexIndices.Dispose();
        }
    }
}
