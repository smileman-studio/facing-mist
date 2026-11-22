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
    /// Меширование transition cells Transvoxel (Laine-Krahenbuhl, GDC 2006), LOD &gt;= 1 (P4b).
    /// </summary>
    /// <remarks>
    /// Transition cell — квадратная сетка 2x2 на грани чанка уровня lod, примыкающей
    /// к чанку уровня lod-1. Углы ячейки — 13 точек в полуячейках (offset 1 = половина
    /// coarse-ячейки = 1 ячейка мелкой сетки уровня lod-1):
    ///   - 4 угла сетки (индексы 0, 2, 6, 8) берутся из кэша плотности 19^3 — это узлы
    ///     coarse-сетки на грани, они же узлы мелкой сетки соседа;
    ///   - 5 вторичных точек (1, 3, 4, 5, 7) вычисляются генератором IslandSdf.SdfVoxel
    ///     точно в полушаге coarse-сетки (int3-координата, округления нет);
    ///   - 4 алиаса (9..12) — углы low-res грани на глубине 2 полуячейки (узлы кэша на
    ///     глубине 1); edge-коды таблиц адресуют ими кэш вершин (D4a, толщина полосы).
    ///
    /// Отличия от эталона transvoxel_core (принятые решения P4b):
    ///   - D1: бисекция ребер удалена. Все пересечения — единая Q16-интерполяция
    ///     <see cref="TransvoxelMesher.EdgeT"/> по целым плотностям концов ребра.
    ///     Позиция вершины становится чистой функцией пары (концы, плотности) и не
    ///     зависит от lod чанка — шов LOD-границы герметичен по построению.
    ///   - D2: secondary vertices удалены вместе с бисекцией (они компенсировали
    ///     lod-зависимый сдвиг бисекции).
    ///   - D3: единая система координат выхода — Q16 в полуячейках, финальная
    ///     конвертация умножением на 2^-17 (step = ячейка lod-1). Для грубого ребра
    ///     (delta = 2 полуячейки) значение равно 2 * qCell regular-мешера того же
    ///     чанка, для мелкого (delta = 1) — побитово равно qCell мелкого соседа.
    ///   - D4: нормаль — целочисленный градиент генератора (центральная разность,
    ///     шаг 1 воксель LOD0) в опорном вокселе вершины; градиент отдается как есть
    ///     (int3), нормализация — ответственность Presentation.
    ///
    /// Детерминизм: только int/long-арифметика и точные конвертации float ( stepped на
    /// степени двойки, мантисса вершины <= 2^21). Результат не зависит от порядка
    /// вызовов, истории lod и включенного Burst.
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
        /// Плотность граничной точки из генератора (тот же квант, что у кэша).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static short SampleGenerator(int3 voxel0, IslandId islandId, int radius)
        {
            return VoxelDensity.Quantize(IslandSdf.SdfVoxel(voxel0, islandId, radius));
        }
        
        /// <summary>
        /// Узловой градиент МЕЛКОЙ сетки на грани стыка (D4a): центральная разность
        /// генератора с шагом halfStep вокселей LOD0. Memo 3x3 на ячейку (9 узлов
        /// плоскости шва: ветвь qw == 0 использует только nz = 0), ленивое заполнение;
        /// sentinel = int.MinValue. Общие узлы соседних ячеек переиспользуются через
        /// memo только внутри ячейки — значения при этом побитово идентичны
        /// (чистая функция мировых координат).
        /// </summary>
        private static int3 FaceNodeGradient(
            NativeArray<int3> memo, TransitionDirection direction, int3 chunkOrigin0,
            int halfStep, int cellX, int cellY, int nx, int ny,
            IslandId islandId, int radius)
        {
            int slot = ny * 3 + nx;
            int3 cached = memo[slot];
            if (cached.x != int.MinValue) return cached;
            int3 v = chunkOrigin0 + FaceToLocalSpace(direction, 2 * cellX + nx, 2 * cellY + ny, 0) * halfStep;
            int3 g = new int3(
                SampleGenerator(v + new int3(halfStep, 0, 0), islandId, radius) - SampleGenerator(v - new int3(halfStep, 0, 0), islandId, radius),
                SampleGenerator(v + new int3(0, halfStep, 0), islandId, radius) - SampleGenerator(v - new int3(0, halfStep, 0), islandId, radius),
                SampleGenerator(v + new int3(0, 0, halfStep), islandId, radius) - SampleGenerator(v - new int3(0, 0, halfStep), islandId, radius));
            memo[slot] = g;
            return g;
        }

        /// <summary>
        /// Меширование transition cells одной грани чанка в готовые growable-выходы (P4b).
        /// </summary>
        /// <param name="density">
        /// Кэш плотности чанка уровня lod: 19^3 <c>short</c>, XYZ-мажорный
        /// (тот же буфер, что для regular-меширования этого чанка).
        /// </param>
        /// <param name="islandId">Идентификатор острова.</param>
        /// <param name="radius">Радиус острова, м.</param>
        /// <param name="chunkOrigin0">Origin чанка в вокселях LOD0 (<c>chunkCoord << (4 + lod)</c>).</param>
        /// <param name="direction">Грань чанка, на которой строятся transition cells.</param>
        /// <param name="lod">Уровень чанка (lod &gt;= 1; при lod = 0 transition cells нет).</param>
        /// <param name="positions">Выход: позиции в ячейках lod-1 (float, из Q16 * 2^-17).</param>
        /// <param name="gradients">Выход: целочисленные градиенты (нормализует Presentation).</param>
        /// <param name="materials">Выход: материалы вершин (<see cref="VoxelMaterial"/> в byte).</param>
        /// <param name="indices">Выход: индексы треугольников (3 на треугольник).</param>
        /// <remarks>
        /// Контракты:
        ///   - density.Length == <see cref="TransvoxelMesher.CacheVolume"/> (проверка снаружи: throw
        ///     недоступен в Burst);
        ///   - списки на входе пусты или содержат предыдущий результат (append-режим,
        ///     индексы продолжатся с positions.Length);
        ///   - метод — чистая функция входов: без throw, без managed-аллокаций, только
        ///     прямое обращение к static readonly таблицам (Burst-safe).
        /// </remarks>
        public static void MeshTransitionInto(
            NativeArray<short> density,
            IslandId islandId,
            int radius,
            int3 chunkOrigin0,
            TransitionDirection direction,
            int lod,
            NativeList<float3> positions,
            NativeList<int3> gradients,
            NativeList<byte> materials,
            NativeList<uint> indices)
        {
            // Guard D7: transition cells существуют только при lod >= 1
            // (halfStep = 1 << (lod - 1) определён именно для них).
            if (lod < 1)
            {
                return;
            }

            int halfStep = 1 << (lod - 1);
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
            var faceNodes = new NativeArray<int3>(9, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            for (int y = 0; y < CellsPerAxis; y++)
            {
                for (int x = 0; x < CellsPerAxis; x++)
                {
                    // 1. Углы ячейки (полуячейки: x, y в 0..32).
                    //    4 угла coarse-сетки — из кэша; индекс кэша = local + apron(1),
                    //    coarse-координаты = полуячейки / 2 (всегда точное деление).
                    cellValues[0] = TransvoxelMesher.GetDensity(density, FaceToLocalSpace(direction, 2 * x, 2 * y, 0) / 2 + 1);
                    cellValues[2] = TransvoxelMesher.GetDensity(density, FaceToLocalSpace(direction, 2 * x + 2, 2 * y, 0) / 2 + 1);
                    cellValues[6] = TransvoxelMesher.GetDensity(density, FaceToLocalSpace(direction, 2 * x, 2 * y + 2, 0) / 2 + 1);
                    cellValues[8] = TransvoxelMesher.GetDensity(density, FaceToLocalSpace(direction, 2 * x + 2, 2 * y + 2, 0) / 2 + 1);

                    //    5 вторичных точек (середины coarse-рёбер и центр) — из генератора,
                    //    точно в полушаге: voxel0 = origin + local * 2^(lod-1) (D1, без округления).
                    cellValues[1] = SampleGenerator(FaceVoxel0(direction, chunkOrigin0, halfStep, 2 * x + 1, 2 * y), islandId, radius);
                    cellValues[3] = SampleGenerator(FaceVoxel0(direction, chunkOrigin0, halfStep, 2 * x, 2 * y + 1), islandId, radius);
                    cellValues[4] = SampleGenerator(FaceVoxel0(direction, chunkOrigin0, halfStep, 2 * x + 1, 2 * y + 1), islandId, radius);
                    cellValues[5] = SampleGenerator(FaceVoxel0(direction, chunkOrigin0, halfStep, 2 * x + 2, 2 * y + 1), islandId, radius);
                    cellValues[7] = SampleGenerator(FaceVoxel0(direction, chunkOrigin0, halfStep, 2 * x + 1, 2 * y + 2), islandId, radius);

                    //    4 угла low-res грани (глубина 2 полуячейки = 1 coarse-ячейка) —
                    //    узлы кэша на глубине 1, как в эталоне transvoxel_core:
                    //    полоса получает толщину, qw > 0 существует (D4a).
                    cellValues[9]  = TransvoxelMesher.GetDensity(density, FaceToLocalSpace(direction, 2 * x,     2 * y,     2) / 2 + 1);
                    cellValues[10] = TransvoxelMesher.GetDensity(density, FaceToLocalSpace(direction, 2 * x + 2, 2 * y,     2) / 2 + 1);
                    cellValues[11] = TransvoxelMesher.GetDensity(density, FaceToLocalSpace(direction, 2 * x,     2 * y + 2, 2) / 2 + 1);
                    cellValues[12] = TransvoxelMesher.GetDensity(density, FaceToLocalSpace(direction, 2 * x + 2, 2 * y + 2, 2) / 2 + 1);

                    // 2. Case code: 9 бит, порядок обхода углов 0,1,2,5,8,7,6,3,4 (п.4).
                    int caseCode = ((cellValues[0] < 0 ? 1 : 0)
                                  | (cellValues[1] < 0 ? 2 : 0)
                                  | (cellValues[2] < 0 ? 4 : 0)
                                  | (cellValues[5] < 0 ? 8 : 0)
                                  | (cellValues[8] < 0 ? 16 : 0)
                                  | (cellValues[7] < 0 ? 32 : 0)
                                  | (cellValues[6] < 0 ? 64 : 0)
                                  | (cellValues[3] < 0 ? 128 : 0)
                                  | (cellValues[4] < 0 ? 256 : 0));

                    // Сброс слотов 0,1,2,7 кэша текущей ячейки — ДО continue (эталон
                    // строки 140-143): эти слоты читаются соседями по x и y, их должно
                    // затронуть даже пропущенная ячейка.
                    currentCache[0 * CellsPerAxis + x] = -1;
                    currentCache[1 * CellsPerAxis + x] = -1;
                    currentCache[2 * CellsPerAxis + x] = -1;
                    currentCache[7 * CellsPerAxis + x] = -1;
                    
                    for (int i = 0; i < 9; i++) faceNodes[i] = new int3(int.MinValue);

                    if (caseCode == 0 || caseCode == 511)
                    {
                        continue;
                    }

                    // Кэшируема ли вершина по осям грани: ячейка не на границе грани (2 бита, D5).
                    int cacheValidator = (x != 0 ? 0b01 : 0) | (y != 0 ? 0b10 : 0);

                    // 3. Lookup: класс -> геометрия, case code -> edge codes (п.4).
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

                        // Угол точно на поверхности (d == 0): слот кэша берётся из
                        // TransitionCornerData (углы алиасов 9..12 адресуются им же).
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

                            // 5. Позиция: единая Q16-интерполяция по целым плотностям
                            //    концов (D1). Corner-случай (d == 0) даёт t = 0 или 65536,
                            //    т.е. точную позицию угла — отдельная ветка не нужна.
                            //    z угла берётся из таблицы: 0 — плоскость шва, 2 — low-res
                            //    грань полосы (толщина 1 coarse-ячейка, D4a).
                            int3 h0 = FaceToLocalSpace(direction,
                                2 * x + TransvoxelTransitionTables.TransitionCornerOffset[c0 * 3],
                                2 * y + TransvoxelTransitionTables.TransitionCornerOffset[c0 * 3 + 1],
                                TransvoxelTransitionTables.TransitionCornerOffset[c0 * 3 + 2]);
                            int3 h1 = FaceToLocalSpace(direction,
                                2 * x + TransvoxelTransitionTables.TransitionCornerOffset[c1 * 3],
                                2 * y + TransvoxelTransitionTables.TransitionCornerOffset[c1 * 3 + 1],
                                TransvoxelTransitionTables.TransitionCornerOffset[c1 * 3 + 2]);

                            int t = TransvoxelMesher.EdgeT(d0, d1);
                            long qx = (long)h0.x * 65536 + (long)(h1.x - h0.x) * t;
                            long qy = (long)h0.y * 65536 + (long)(h1.y - h0.y) * t;
                            long qz = (long)h0.z * 65536 + (long)(h1.z - h0.z) * t;

                            // 6. Опорный воксель вершины (D4/D6): конец ребра с меньшим
                            //    |density|; при равенстве — конец с меньшим индексом угла.
                            int abs0 = math.abs((int)d0);
                            int abs1 = math.abs((int)d1);
                            int anchor = abs0 < abs1 ? c0 : (abs1 < abs0 ? c1 : (c0 < c1 ? c0 : c1));
                            int3 anchorVoxel0 = chunkOrigin0 + FaceToLocalSpace(direction,
                                2 * x + TransvoxelTransitionTables.TransitionCornerOffset[anchor * 3],
                                2 * y + TransvoxelTransitionTables.TransitionCornerOffset[anchor * 3 + 1],
                                TransvoxelTransitionTables.TransitionCornerOffset[anchor * 3 + 2]) * halfStep;

                            // 7. Градиент (D4a): трилинейная интерполяция узловых градиентов.
                            //    Q16 вершины считается в ГРАНЬ-пространстве (qu, qv, qw) —
                            //    не зависит от перестановки осей FaceToLocalSpace.
                            //    qw == 0 (грань стыка): узлы МЕЛКОЙ сетки (шаг halfStep вокселей
                            //    LOD0) из генератора; свёртка в порядке ЛОКАЛЬНЫХ осей (нулевой
                            //    вес вдоль нормали прозрачен) — побитово совпадает с нормалью
                            //    мелкого regular-соседа на всех шести гранях.
                            //    qw > 0 (внутри полосы и на low-res грани): узлы coarse-кэша в
                            //    локальных координатах, свёртка x→y→z как в MeshInto — побитово
                            //    совпадает с own regular. Выход — int3 (D4), нормализация через
                            //    TransvoxelMesher.NormalizeGradient на стороне Presentation.
                            long qu = (long)(2 * x + TransvoxelTransitionTables.TransitionCornerOffset[c0 * 3]) * 65536
                                    + (long)(TransvoxelTransitionTables.TransitionCornerOffset[c1 * 3]
                                           - TransvoxelTransitionTables.TransitionCornerOffset[c0 * 3]) * t;
                            long qv = (long)(2 * y + TransvoxelTransitionTables.TransitionCornerOffset[c0 * 3 + 1]) * 65536
                                    + (long)(TransvoxelTransitionTables.TransitionCornerOffset[c1 * 3 + 1]
                                           - TransvoxelTransitionTables.TransitionCornerOffset[c0 * 3 + 1]) * t;
                            long qw = (long)TransvoxelTransitionTables.TransitionCornerOffset[c0 * 3 + 2] * 65536
                                    + (long)(TransvoxelTransitionTables.TransitionCornerOffset[c1 * 3 + 2]
                                           - TransvoxelTransitionTables.TransitionCornerOffset[c0 * 3 + 2]) * t;

                            int3 gI;
                            if (qw == 0)
                            {
                                int fnx = (int)(qu >> 16) - 2 * x;
                                int fny = (int)(qv >> 16) - 2 * y;
                                int ffx = (int)(qu & 0xFFFF);
                                int ffy = (int)(qv & 0xFFFF);
                                // При нулевой доле соседний узел не нужен (вес 0) — не выходим за memo 3x3.
                                int fnx1 = ffx == 0 ? fnx : fnx + 1;
                                int fny1 = ffy == 0 ? fny : fny + 1;
                                int3 g00 = FaceNodeGradient(faceNodes, direction, chunkOrigin0, halfStep, x, y, fnx,  fny,  islandId, radius);
                                int3 g10 = FaceNodeGradient(faceNodes, direction, chunkOrigin0, halfStep, x, y, fnx1, fny,  islandId, radius);
                                int3 g01 = FaceNodeGradient(faceNodes, direction, chunkOrigin0, halfStep, x, y, fnx,  fny1, islandId, radius);
                                int3 g11 = FaceNodeGradient(faceNodes, direction, chunkOrigin0, halfStep, x, y, fnx1, fny1, islandId, radius);
                                // Локальные оси грань-направлений u и v: свёртку in-plane ведём
                                // в порядке возрастания локальной оси (как у fine-соседа).
                                int3 eU = FaceToLocalSpace(direction, 1, 0, 0);
                                int3 eV = FaceToLocalSpace(direction, 0, 1, 0);
                                int au = eU.x != 0 ? 0 : (eU.y != 0 ? 1 : 2);
                                int av = eV.x != 0 ? 0 : (eV.y != 0 ? 1 : 2);
                                // Третья (нормальная) свёртка с весом 0 прозрачна: слои nz = 1
                                // дублируются, узлы плоскости шва используются дважды.
                                gI = au < av
                                    ? TransvoxelMesher.TrilinearGradient(g00, g10, g01, g11, g00, g10, g01, g11, ffx, ffy, 0)
                                    : TransvoxelMesher.TrilinearGradient(g00, g01, g10, g11, g00, g01, g10, g11, ffy, ffx, 0);
                            }
                            else
                            {
                                // Q16 coarse-ячеек в ЛОКАЛЬНЫХ координатах: coarse = 2 полуячейки (q / 2).
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

                            // 8. Материал: поле в опорном вокселе вершины (D6).
                            byte mat = (byte)IslandMaterialField.At(
                                anchorVoxel0.x, anchorVoxel0.y, anchorVoxel0.z, islandId);

                            positions.Add(new float3((float)qx, (float)qy, (float)qz) * InvQ16HalfCell);
                            gradients.Add(gI);
                            materials.Add(mat);

                            // 9. Запись в кэш вершин (асимметрия эталона сохранена:
                            //    corner-ветка пишет при cacheable, edge-ветка — при
                            //    cacheable && cacheDir != 4; под 2-битным валидатором
                            //    cacheDir == 4 никогда не cacheable, поэтому ветки
                            //    объединены без изменения поведения).
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

                    // 10. Треугольники; flip winding по старшему биту класса (п.4).
                    int triVertCount = triCount * 3;
                    if (!flipWinding)
                    {
                        for (int i = 0; i < triVertCount; i += 3)
                        {
                            indices.Add((uint)vertexIndices[TransvoxelTransitionTables.TransitionCellTriangleIndices[triBase + i + 2]]);
                            indices.Add((uint)vertexIndices[TransvoxelTransitionTables.TransitionCellTriangleIndices[triBase + i + 1]]);
                            indices.Add((uint)vertexIndices[TransvoxelTransitionTables.TransitionCellTriangleIndices[triBase + i + 0]]);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < triVertCount; i += 3)
                        {
                            indices.Add((uint)vertexIndices[TransvoxelTransitionTables.TransitionCellTriangleIndices[triBase + i + 0]]);
                            indices.Add((uint)vertexIndices[TransvoxelTransitionTables.TransitionCellTriangleIndices[triBase + i + 1]]);
                            indices.Add((uint)vertexIndices[TransvoxelTransitionTables.TransitionCellTriangleIndices[triBase + i + 2]]);
                        }
                    }
                }

                // Swap dock-ов между строками y (после строки, не после ячейки — D5).
                var tmp = currentCache;
                currentCache = previousCache;
                previousCache = tmp;
            }

            // Освобождаем служебные scratch-буферы; выходные списки — вызывающего.
            currentCache.Dispose();
            previousCache.Dispose();
            cellValues.Dispose();
            vertexIndices.Dispose();
            faceNodes.Dispose();
        }
    }
}
