using Unity.Collections;
using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Результат меширования одного чанка regular cells (P4a, ADR-0002 D7).
    /// </summary>
    /// <remarks>
    /// Позиции и нормали — в шагах сетки LOD0 (полуметрах) как <c>float3</c>.
    /// Индексы — <c>uint</c> (3 на треугольник). Обход треугольников (CCW в
    /// левой системе координат Unity) согласован с нормалями вершин: нормаль,
    /// вычисленная как <c>cross(p1-p0, p2-p0)</c>, смотрит в ту же сторону,
    /// что и вершинные нормали (наружу, от грунта к воздуху). Порядок вершин
    /// совпадает с порядком добавления в <see cref="Positions"/>.
    /// </remarks>
    public struct TransvoxelMeshOutput
    {
        public NativeArray<float3> Positions;
        public NativeArray<float3> Normals;
        public NativeArray<uint> Indices;
        public NativeArray<byte> Materials;

        /// <summary>Число вершин (== Positions.Length == Normals.Length == Materials.Length).</summary>
        public int VertexCount => Positions.Length;

        /// <summary>Число треугольников (== Indices.Length / 3).</summary>
        public int TriangleCount => Indices.Length / 3;

        /// <summary>Освободить все буферы (идемпотентно).</summary>
        public void Dispose()
        {
            if (Positions.IsCreated) Positions.Dispose();
            if (Normals.IsCreated) Normals.Dispose();
            if (Indices.IsCreated) Indices.Dispose();
            if (Materials.IsCreated) Materials.Dispose();
        }
    }

    /// <summary>
    /// Меширование regular cells Transvoxel (Laine–Krahenbuhl, GDC 2006), LOD 0.
    /// </summary>
    /// <remarks>
    /// Вход — кэш плотности 19³ <c>short</c> (XYZ-мажорный: индекс
    /// <c>(x * CacheSize + y) * CacheSize + z</c>), <see cref="IslandId"/>.
    /// Выход — <see cref="TransvoxelMeshOutput"/>: позиции, нормали, индексы, материалы.
    ///
    /// Алгоритм (P4a, regular cells, LOD 0):
    ///   1. Для каждой из 16³ ячеек чанка читаем 8 углов из кэша.
    ///   2. Case code — 8 бит: бит i = 1, если угол i в грунте (d &lt; 0).
    ///   3. <see cref="TransvoxelTables.RegularCellClass"/> -> класс ячейки,
    ///      <see cref="TransvoxelTables.RegularCellGeometryCounts"/> +
    ///      <see cref="TransvoxelTables.RegularCellTriangleIndices"/> -> геометрия.
    ///   4. Для каждой вершины (ребра) — edge code из
    ///      <see cref="TransvoxelTables.RegularVertexDataFlat"/>: два угла + параметры кэша.
    ///   5. Позиция — интерполяция вдоль ребра: t = (|d0| &lt;&lt; 16) / (|d0| + |d1|),
    ///      фиксированная точка Q16 (1/65536 шага сетки).
    ///   6. Нормаль — центральная разность плотности по трём осям (полу база = 1 шаг),
    ///      направлена от грунта к воздуху (наружу).
    ///   7. Материал — <see cref="IslandMaterialField.At"/> в целочисленной позиции вершины.
    ///
    /// Vertex cache (как в оригинале): две плоскости 16×16×4, по одной на слой y.
    /// Кэшируемая вершина (ребро на грани ячейки) пишется в dock, выбранный битом 2
    /// edge code: бит 2 = 0 → current, бит 2 = 1 → previous. При обходе y-слоя текущий
    /// слой уже обработан, поэтому вершины с битом 2 = 0 читаются/пишутся в current,
    /// а с битом 2 = 1 — в previous (слой y−1). После каждого слоя dock-и меняются местами.
    ///
    /// Детерминизм: чистая функция от (кэш, IslandId). Все вычисления
    /// целочисленные или float-линейные (без трансцендентных), поэтому результат
    /// побитово одинаков на всех платформах и в Burst.
    /// </remarks>
    public static class TransvoxelMesher
    {
        /// <summary>Число ячеек чанка по каждой оси (16, ADR-0002 D2).</summary>
        public const int CellsPerAxis = 1 << VoxelConstants.ChunkSizeLog2;

        /// <summary>Число углов regular cell.</summary>
        public const int CornerCount = 8;

        /// <summary>Объём кэша плотности чанка: CacheSize³ = 19³ = 6859.</summary>
        public const int CacheVolume =
            VoxelConstants.CacheSize * VoxelConstants.CacheSize * VoxelConstants.CacheSize;

        /// <summary>Число слоёв vertex cache (по z внутри слоя).</summary>
        private const int CacheSlots = 4;

        /// <summary>
        /// Сэмпл плотности из кэша 19³ (XYZ-мажорный).
        /// </summary>
        /// <remarks>
        /// Индекс = (x * 19 + y) * 19 + z. Контракт — координаты в диапазоне [0, 18].
        /// </remarks>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static short GetDensity(NativeArray<short> density, int x, int y, int z)
        {
            return density[(x * VoxelConstants.CacheSize + y) * VoxelConstants.CacheSize + z];
        }

        /// <summary>
        /// Сэмпл плотности из кэша 19³ по <see cref="Unity.Mathematics.int3"/>.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static short GetDensity(NativeArray<short> density, int3 pos)
        {
            return GetDensity(density, pos.x, pos.y, pos.z);
        }

        /// <summary>
        /// Интерполяция t ∈ [0, 65536] в Q16: (|d0| &lt;&lt; 16) / (|d0| + |d1|).
        /// </summary>
        /// <remarks>
        /// 0 = на точке d0, 65536 = на точке d1. Если оба нуля — середина (32768).
        /// Точное совпадение с <see cref="VoxelDensity.InterpolateEdgeT"/>.
        /// </remarks>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int EdgeT(short d0, short d1)
        {
            int abs0 = math.abs(d0);
            int abs1 = math.abs(d1);
            int sum = abs0 + abs1;
            if (sum == 0) return 32768;
            return (abs0 << 16) / sum;
        }
        
                /// <summary>
        /// Узловой градиент: центральная разность плотности по кэшу (полу база = 1 шаг).
        /// На краях apron (индекс 0 / CacheSize-1) — односторонняя разность (ADR-0002 D4).
        /// Знак: от грунта (d&lt;0) к воздуху (d&gt;0).
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int3 NodeGradient(NativeArray<short> density, int3 c)
        {
            int last = VoxelConstants.CacheSize - 1;
            int x0 = c.x > 0 ? c.x - 1 : c.x;
            int x1 = c.x < last ? c.x + 1 : c.x;
            int y0 = c.y > 0 ? c.y - 1 : c.y;
            int y1 = c.y < last ? c.y + 1 : c.y;
            int z0 = c.z > 0 ? c.z - 1 : c.z;
            int z1 = c.z < last ? c.z + 1 : c.z;
            return new int3(
                GetDensity(density, x1, c.y, c.z) - GetDensity(density, x0, c.y, c.z),
                GetDensity(density, c.x, y1, c.z) - GetDensity(density, c.x, y0, c.z),
                GetDensity(density, c.x, c.y, z1) - GetDensity(density, c.x, c.y, z0));
        }

        /// <summary>Линейная интерполяция int3 в Q16 (f ∈ [0, 65536]), long-арифметика, truncation.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int3 LerpQ16(int3 a, int3 b, int f)
        {
            long inv = 65536 - f;
            return new int3(
                (int)(((long)a.x * inv + (long)b.x * f) >> 16),
                (int)(((long)a.y * inv + (long)b.y * f) >> 16),
                (int)(((long)a.z * inv + (long)b.z * f) >> 16));
        }

        /// <summary>
        /// Трилинейная интерполяция 8 узловых градиентов весами Q16 (D4a).
        /// Порядок свёртки фиксирован: x → y → z. Узлы — общие данные соседних ячеек
        /// и чанков, поэтому поле непрерывно через границы ячеек и побитово совпадает
        /// на стыках чанков одного LOD при одинаковых мировых узлах и весах.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int3 TrilinearGradient(
            int3 g000, int3 g100, int3 g010, int3 g110,
            int3 g001, int3 g101, int3 g011, int3 g111,
            int fx, int fy, int fz)
        {
            int3 a00 = LerpQ16(g000, g100, fx);
            int3 a10 = LerpQ16(g010, g110, fx);
            int3 a01 = LerpQ16(g001, g101, fx);
            int3 a11 = LerpQ16(g011, g111, fx);
            int3 b0 = LerpQ16(a00, a10, fy);
            int3 b1 = LerpQ16(a01, a11, fy);
            return LerpQ16(b0, b1, fz);
        }

        /// <summary>
        /// Нормализация градиента для Presentation/тестов. Guard от normalize(0):
        /// в регионах клампа градиент нулевой — нормаль вверх (ADR-0002 D4).
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static float3 NormalizeGradient(int3 g)
        {
            float3 n = new float3(g.x, g.y, g.z);
            float lenSq = n.x * n.x + n.y * n.y + n.z * n.z;
            if (lenSq > 0.0f)
            {
                return n / math.sqrt(lenSq);
            }
            return new float3(0f, 1f, 0f);
        }

        /// <summary>
        /// Меширование одного чанка regular cells (LOD 0).
        /// </summary>
        /// <param name="density">
        /// Кэш плотности 19³ <c>short</c>, XYZ-мажорный. Длина ровно
        /// <see cref="CacheVolume"/>.
        /// </param>
        /// <param name="islandId">Идентификатор острова (для материала).</param>
        /// <param name="allocator">Аллокатор выходных буферов.</param>
        /// <returns>
        /// Заполненный <see cref="TransvoxelMeshOutput"/>. Вызывающий обязан
        /// вызвать <see cref="TransvoxelMeshOutput.Dispose"/>.
        /// </returns>
        public static TransvoxelMeshOutput Mesh(
            NativeArray<short> density,
            IslandId islandId,
            Allocator allocator)
        {
            var positions = new NativeList<float3>(64, allocator);
            var normals = new NativeList<float3>(64, allocator);
            var materials = new NativeList<byte>(64, allocator);
            var indices = new NativeList<uint>(96, allocator);

            MeshInto(density, islandId, positions, normals, materials, indices);

            // Финальные буферы ровно по фактическим длинам (NativeList.ToArray копирует).
            var output = new TransvoxelMeshOutput
            {
                Positions = positions.ToArray(allocator),
                Normals = normals.ToArray(allocator),
                Indices = indices.ToArray(allocator),
                Materials = materials.ToArray(allocator),
            };

            positions.Dispose();
            normals.Dispose();
            materials.Dispose();
            indices.Dispose();
            return output;
        }

        /// <summary>
        /// Меширование regular cells в готовые growable-выходы (P4a, ADR-0002 D7).
        /// </summary>
        /// <remarks>
        /// Списки на входе обязаны быть пустыми; заполняются append-ом, их буферы —
        /// ответственность вызывающего. Служебные scratch-буферы живут на Allocator.Temp
        /// (освобождаются автоматически). Метод — чистая функция: без throw, без
        /// managed-аллокаций, только прямое обращение к static readonly таблицам,
        /// поэтому пригоден для вызова из Burst-джобы (паттерн NativeList-полей).
        /// Контракт: density.Length == CacheVolume (проверка длины — снаружи: Burst
        /// компилирует весь граф вызовов job-ы, throw внутри Burst недоступен).
        /// </remarks>
        public static void MeshInto(
            NativeArray<short> density,
            IslandId islandId,
            NativeList<float3> positions,
            NativeList<float3> normals,
            NativeList<byte> materials,
            NativeList<uint> indices)
        {
            // Vertex cache: два dock-а по 16*16*4 int, инициализированы -1.
            int cacheSize = CellsPerAxis * CellsPerAxis * CacheSlots;
            var currentCache = new NativeArray<int>(cacheSize, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var previousCache = new NativeArray<int>(cacheSize, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < cacheSize; i++)
            {
                currentCache[i] = -1;
                previousCache[i] = -1;
            }

            // Scratch для значений углов текущей ячейки.
            var cellValues = new NativeArray<short>(CornerCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var vertexIndices = new NativeArray<int>(16, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            for (int y = 0; y < CellsPerAxis; y++)
            {
                for (int z = 0; z < CellsPerAxis; z++)
                {
                    for (int x = 0; x < CellsPerAxis; x++)
                    {
                        // 1. Читаем 8 углов ячейки: ячейка (x,y,z) занимает
                        //    кэш-индексы [x+1, x+2] × [y+1, y+2] × [z+1, z+2].
                        for (int c = 0; c < CornerCount; c++)
                        {
                            int ox = TransvoxelTables.RegularCornerOffset[c * 3 + 0];
                            int oy = TransvoxelTables.RegularCornerOffset[c * 3 + 1];
                            int oz = TransvoxelTables.RegularCornerOffset[c * 3 + 2];
                            cellValues[c] = GetDensity(density, x + 1 + ox, y + 1 + oy, z + 1 + oz);
                        }

                        // 2. Case code: бит c = 1, если угол c в грунте (d < 0).
                        int caseCode = 0;
                        for (int c = 0; c < CornerCount; c++)
                        {
                            if (cellValues[c] < 0)
                            {
                                caseCode |= (1 << c);
                            }
                        }

                        // Сброс слота 0 кэша текущей ячейки (x,z) — ДО любой
                        // проверки/continue (эталон transvoxel_core строка 75/124):
                        // _currentCache[x * ChunkSize * 4 + z * 4] = -1.
                        // Слот 0 хранит угловые вершины; слоты 1-3 — интерполированные
                        // (пишутся при cornerIdx1 == 7, см. ниже). Сбрасываем только
                        // слот 0, как в эталоне: слоты 1-3 пишутся только ячейкой
                        // (x, y, z) при cacheDir бит 0 = 0 (x не смещён), и следующая
                        // ячейка (x+1, y, z) читает их при cacheDir бит 0 = 1.
                        currentCache[x * CellsPerAxis * 4 + z * 4] = -1;

                        // Пустая / полностью заполненная ячейка — треугольников нет.
                        if (caseCode == 0 || caseCode == 0xFF)
                        {
                            continue;
                        }

                        // 3. Lookup: класс -> геометрия, case code -> edge codes.
                        //    Прямое обращение к плоским static readonly массивам:
                        //    Burst не позволяет сохранять managed-массив в локаль или
                        //    передавать его в метод, поэтому индексация инлайн.
                        int cellClass = TransvoxelTables.RegularCellClass[caseCode];
                        int geoCounts = TransvoxelTables.RegularCellGeometryCounts[cellClass];
                        int vertexCount = (geoCounts >> 4) & 0x0F;
                        int triCount = geoCounts & 0x0F;
                        int vertexBase = caseCode * TransvoxelTables.MaxVertexDataPerCase;
                        int triBase = cellClass * TransvoxelTables.MaxCellTriangleIndices;

                        // Кэшируема ли вершина по каждой оси: ячейка не на границе чанка.
                        int cacheValidator =
                            (x != 0 ? 0x01 : 0) |
                            (z != 0 ? 0x02 : 0) |
                            (y != 0 ? 0x04 : 0);

                        // 4-7. Вершины.
                        for (int i = 0; i < vertexCount; i++)
                        {
                            ushort edgeCode = TransvoxelTables.RegularVertexDataFlat[vertexBase + i];
                            int cornerIdx0 = (edgeCode >> 4) & 0x0F;
                            int cornerIdx1 = edgeCode & 0x0F;

                            short d0 = cellValues[cornerIdx0];
                            short d1 = cellValues[cornerIdx1];

                            byte cacheIdx = (byte)((edgeCode >> 8) & 0x0F);
                            byte cacheDir = (byte)(edgeCode >> 12);

                            // Угол на поверхности (d == 0): вершина в целочисленном углу.
                            if (d1 == 0)
                            {
                                cacheDir = (byte)(cornerIdx1 ^ 7);
                                cacheIdx = 0;
                            }
                            else if (d0 == 0)
                            {
                                cacheDir = (byte)(cornerIdx0 ^ 7);
                                cacheIdx = 0;
                            }

                            bool cacheable = (cacheDir & cacheValidator) == cacheDir;
                            int cachePosX = x - (cacheDir & 1);
                            int cachePosZ = z - ((cacheDir >> 1) & 1);

                            // Dock: бит 2 = 0 → current, бит 2 = 1 → previous.
                            var dock = ((cacheDir >> 2) & 1) == 1 ? previousCache : currentCache;

                            int vertexIndex = -1;
                            if (cacheable)
                            {
                                vertexIndex = dock[cachePosX * CellsPerAxis * 4 + cachePosZ * 4 + cacheIdx];
                            }

                            if (!cacheable || vertexIndex == -1)
                            {
                                vertexIndex = positions.Length;

                                // 5. Позиция: интерполяция вдоль ребра в Q16.
                                int3 pos0 = new int3(
                                    x + TransvoxelTables.RegularCornerOffset[cornerIdx0 * 3 + 0],
                                    y + TransvoxelTables.RegularCornerOffset[cornerIdx0 * 3 + 1],
                                    z + TransvoxelTables.RegularCornerOffset[cornerIdx0 * 3 + 2]);
                                int3 pos1 = new int3(
                                    x + TransvoxelTables.RegularCornerOffset[cornerIdx1 * 3 + 0],
                                    y + TransvoxelTables.RegularCornerOffset[cornerIdx1 * 3 + 1],
                                    z + TransvoxelTables.RegularCornerOffset[cornerIdx1 * 3 + 2]);

                                int t = EdgeT(d0, d1);
                                // Позиция вершины в Q16 fixed-point (шаги сетки * 65536):
                                // pos = pos0 + (pos1 - pos0) * t. Всё целочисленно -> детерминировано.
                                long qfx = (long)pos0.x * 65536 + (long)(pos1.x - pos0.x) * t;
                                long qfy = (long)pos0.y * 65536 + (long)(pos1.y - pos0.y) * t;
                                long qfz = (long)pos0.z * 65536 + (long)(pos1.z - pos0.z) * t;

                                // Суб-воксельная позиция в шагах сетки (float — только для Presentation).
                                const float Inv65536 = 1f / 65536f;
                                float3 vertexPos = new float3(qfx * Inv65536, qfy * Inv65536, qfz * Inv65536);

                                // 6. Нормаль (D4a): трилинейная интерполяция узловых градиентов
                                //    в суб-воксельной позиции вершины. Floor-снэп по ячейке удалён:
                                //    он давал piecewise-constant нормали (площадки 0.5 м на терминаторе).
                                //    Узловые градиенты общие у соседних ячеек/чанков (apron из того же
                                //    SDF) -> непрерывность через границы и побитовые стыки чанков.
                                int gx0 = (int)(qfx >> 16);
                                int gy0 = (int)(qfy >> 16);
                                int gz0 = (int)(qfz >> 16);
                                int fx = (int)(qfx & 0xFFFF);
                                int fy = (int)(qfy & 0xFFFF);
                                int fz = (int)(qfz & 0xFFFF);
                                int3 c000 = new int3(gx0, gy0, gz0) + 1;
                                int3 gI = TrilinearGradient(
                                    NodeGradient(density, c000),
                                    NodeGradient(density, c000 + new int3(1, 0, 0)),
                                    NodeGradient(density, c000 + new int3(0, 1, 0)),
                                    NodeGradient(density, c000 + new int3(1, 1, 0)),
                                    NodeGradient(density, c000 + new int3(0, 0, 1)),
                                    NodeGradient(density, c000 + new int3(1, 0, 1)),
                                    NodeGradient(density, c000 + new int3(0, 1, 1)),
                                    NodeGradient(density, c000 + new int3(1, 1, 1)),
                                    fx, fy, fz);
                                float3 normal = NormalizeGradient(gI);

                                // 7. Материал: в grid-ячейке вершины (chunk-local, без
                                //    apron-оффсета). Материал ищется в поле по крупным
                                //    ячейкам (32 м = 64 шага), суб-воксельный сдвиг
                                //    пренебрежим; сквозная island-local привязка — P5.
                                byte mat = (byte)IslandMaterialField.At(
                                    (int)(qfx >> 16), (int)(qfy >> 16), (int)(qfz >> 16), islandId);

                                positions.Add(vertexPos);
                                normals.Add(normal);
                                materials.Add(mat);

                                if (cacheIdx == 0)
                                {
                                    // Угловая вершина: запись в выбранный dock
                                    // (current или previous по bit 2 cacheDir).
                                    if (cacheable)
                                    {
                                        dock[cachePosX * CellsPerAxis * 4 + cachePosZ * 4 + cacheIdx] = vertexIndex;
                                    }
                                }
                                else if (cornerIdx1 == 7)
                                {
                                    // Интерполированная вершина: запись в currentCache
                                    // на позиции текущей ячейки (x,z), как в эталоне
                                    // transvoxel_core строки 239-242. Соседняя ячейка
                                    // (x+1, y, z) прочитает её при cacheDir bit 0 = 1
                                    // (cachePosX = x). Это обеспечивает shared vertices
                                    // и сохраняет manifold.
                                    currentCache[x * CellsPerAxis * 4 + z * 4 + cacheIdx] = vertexIndex;
                                }
                            }

                            vertexIndices[i] = vertexIndex;
                        }

                    // 8. Треугольники: triCount * 3 индекса из плоского массива.
                    // Обход развёрнут (ia, ic, ib) относительно таблиц Laine–Krahenbuhl:
                    // таблицы дают обход, согласованный с inward-нормалями эталона
                    // transvoxel_core; P4a считает нормали наружу (градиент от грунта
                    // к воздуху), поэтому winding переворачивается здесь.
                    // Transition-мешер (P4b) разворачивает аналогично:
                    // flipWinding=false → (i+2, i+1, i).
                    int triVertCount = triCount * 3;
                    for (int i = 0; i < triVertCount; i += 3)
                    {
                        int ia = vertexIndices[TransvoxelTables.RegularCellTriangleIndices[triBase + i + 0]];
                        int ib = vertexIndices[TransvoxelTables.RegularCellTriangleIndices[triBase + i + 1]];
                        int ic = vertexIndices[TransvoxelTables.RegularCellTriangleIndices[triBase + i + 2]];
                        indices.Add((uint)ia);
                        indices.Add((uint)ic);
                        indices.Add((uint)ib);
                    }
                    }
                }

                // Swap dock-ов между слоями y.
                var tmp = currentCache;
                currentCache = previousCache;
                previousCache = tmp;
            }

            // Освобождаем только служебные scratch-буферы; выходные списки
            // принадлежат вызывающему.
            currentCache.Dispose();
            previousCache.Dispose();
            cellValues.Dispose();
            vertexIndices.Dispose();
        }
    }
}
