# Roadmap: воксельная генерация островов

Основание: ADR-0002. 

## P0. Фиксация решений — выполнено

ADR-0002 принят (2026-11-17).

## P1. Core-примитивы

`Assets/Scripts/Core/Voxels/` → `SmilemanStudio.Core.Voxels`

Сделано частично: `VoxelConstants` и `IslandFootprint` (нужны раскладке мира).

- `VoxelConstants` — все константы ADR-0002, включая `CrustStitchMinY`,
  `LodHardMax`, `LodSwitchFactor`, `SegmentCapacity`, `CoreMinStep`,
  `ApexDepthPermille`, `TopReliefPermille`. Единственный источник значений.
- `IslandFootprint` — `Extent(radius)`, `MinEdgeGap`, `MinCenterSpacing`,
  `MinCenterSpacingSquared`, `EdgeGap`, `IsValidRadius`. Единственный источник
  дистанций раскладки; bootstrap и тесты используют только его.
- `VoxelCoord` — обёртка `int3`, world↔voxel, island-local
- `VoxelChunkCoord` — упаковка в `long`: 12+12+12 бита координат, 4 бита LOD,
  бит валидности; `0 = invalid`, `TryUnpack`
- `VoxelLod` — `VoxelStep(k)`, `ChunkWorldSize(k)`, `LodMax(radius)` без float
- `VoxelDensity` — quantize/dequantize `float`↔`short`, кламп
- `VoxelNoise` — integer hash с avalanche, fixed-point value noise, fbm
- `VoxelMaterial` — enum, `None = 0`
- `VoxelBrushKind` — enum, `None = 0`, `Subtract`, `Add` (Paint в v1 нет)

Тесты EditMode: round-trip координат и ключей; валидный ключ никогда не ноль;
границы диапазонов при `RadiusMin` и `RadiusMax`; `LodMax` монотонен по радиусу
и даёт 6 при 200 и 9 при 2000; `IslandFootprint` не допускает пересечения
силуэтов ни для какой пары радиусов из диапазона и держит зазор между краями
равным радиусу меньшего на минимальной дистанции; детерминизм и кламп
квантования; детерминизм шума; отсутствие float-трансцендентных; `None = 0`
во всех enum.

Выход: golden-hash зафиксирован, `check_arch.py` = OK. Зависимостей нет.

## P2. SDF острова — выполнено

`Core/Voxels/IslandSdf`, `Core/Voxels/IslandMaterialField`

Все параметры из `Radius` и `Id`, значения — из `VoxelConstants`, без литералов
в коде. `ApexDepth = R * ApexDepthPermille / 1000`,
`TopHeightMax = min(R * TopReliefPermille / 1000, TopHeightHardCap)`. Форма
`max(dCone, dTop)`, `topSurface = clamp(fbm, 0, TopHeightMax)`. Материал —
чистая функция от глубины под поверхностью, высоты и 3D-шума; покраски нет.
Пещеры выносятся в P2b, не блокируют.

Реализовано:
- `IslandSdf` — `ApexDepth`, `TopHeightMax`, `TopSurface`, `Sdf`, `FloorSqrt`.
  Чисто int-пайплайн, `Unity.Mathematics` только `int3`/`clamp`.
- `IslandMaterialField.At` — argmax 4×`Fbm3D` (Dirt/Coal/Sand/Quartz),
  клетка 32 м (`MATERIAL_SCALE = 2048`).
- `VoxelNoise.ValueNoise3D` + `Fbm3D` (8 узлов, 3 smoothstep, Q16).
- Golden-значения посчитаны Python-репликой (`tools/golden_sdf.py`) и
  зафиксированы в C#-тестах.

Тесты: SDF-пробы в фиксированных точках → golden-hash на радиусах 200, 500, 2000;
вершина всегда не выше `TopHeightMax` и не ниже 0; толщина следует
из радиуса; знак SDF на границе радиуса; разные `Id` дают разный рельеф при
равном радиусе; один `Id` и `Radius` дают идентичный SDF независимо от порядка
вызовов.

Выход: одинаковый `Id` и `Radius` дают идентичный SDF на любой платформе.

## P3. Сэмплер чанка — выполнено

`Simulation/Voxels/` → `SmilemanStudio.Simulation.Voxels`

Burst-джоб заполняет `NativeArray<short>` на 19³ из `Allocator.TempJob`.
Apron из того же SDF. Плотность вне полосы клампится по стороне.
Никаких lookup на воксель.

Что сделано:

- `IslandSdf.SdfVoxel(int3, IslandId, int)` — SDF в единицах шага LOD0 (0,5 м).
  Метровый `Sdf` не тронут. Согласованность: `|SdfVoxel(2p)/2 − Sdf(p)| ≤ 1`
  (фактически ≤ 0,5), golden-hash на R = 200/500/2000.
- `VoxelChunkSampler` — геометрия кэша: `ChunkOrigin0 = coord << (4 + lod)`,
  `AxisToVoxel`/`VoxelFromIndices` = `origin0 + (index − 1) << lod`, 19 = 17 + 2 apron.
- `Sample`/`SampleCore` — заполнение 19³ `short`, XYZ-мажорный порядок.
- `SampleVoxelChunkJob` — `[BurstCompile] IJob`, побитно совпадает с main-thread.

Тесты: плотность на границах чанка совпадает с соседним; apron покрывает
центральную разность с полу базой 1; детерминизм job против main-thread;
отсутствие аллокаций после прогрева.

Найденный при верификации дефект: `int3 + int` в Unity.Mathematics — broadcast,
поэтому скалярный `CacheIndexToVoxel` смещал сэмпл сразу по трём осям.
API заменён на `AxisToVoxel(axisIndex, axis, …)` и `VoxelFromIndices(int3, …)`.

## P4. Transvoxel и меши

`Core/Voxels/TransvoxelTables.cs`, `Core/Voxels/TransvoxelMesher.cs`,
`Simulation/Voxels/MeshVoxelChunkJob.cs`, `Presentation/Voxels/`

Transvoxel (Laine–Krahenbuhl, GDC 2006). P4a: regular cells (LOD0) — **выполнено**,
P4b: transition cells (LOD > 0) — **выполнено**. Таблицы — стандартные Laine–Krahenbuhl,
целочисленные, детерминизм не нарушают. Интерполяция `t` в Q16
(`VoxelDensity.InterpolateEdgeT`). Нормали — центральная разность по кэшу
19³ с полу базой 1, односторонняя на краях apron, guard от `normalize(0)`.
Материал только для эмитируемых вершин, процедурно. Бесшовный LOD:
vertex caching на границах чанков, skirt'ы не нужны.

### P4a — выполнено

Реализовано:
- `TransvoxelTables.cs` — flat static readonly массивы (Burst-safe): `RegularCornerOffset` (24 int),
  `RegularCellClass` (256 byte), `RegularCellGeometryCounts` (16 byte), `RegularCellTriangleIndices`
  (16×15 byte), `RegularVertexDataFlat` (256×12 ushort). Golden hashes (FNV-1a 64):
  `0x229B64048EA9BCC5`, `0xEFFBF2112A9922D7`, `0x132487C81C138B5D`, `0xD656F9622A6EECE0`, `0x25A2E854318F80CD`.
- `TransvoxelMesher.Mesh` / `MeshInto` — полный алгоритм regular cells: case code → lookup →
  vertex cache (2 dock-а 16×16×4, swap по y) → Q16 интерполяция → нормали (центральная разность)
  → материалы (`IslandMaterialField.At`). Совпадает с эталоном `transvoxel_core/TransvoxelMesher.cs`.
- `MeshVoxelChunkJob` — `[BurstCompile] IJob` (NativeList-поля, без IJob<T>).
- `VoxelChunkMeshBuilder` / `VoxelChunkView` — Presentation-мост, `UnityEngine.Mesh`,
  vertex color (`Sprites/Default`).

Тесты (251 EditMode, 4 PlayMode): таблицы (golden-hash, замкнутость, диапазон);
алгоритм (детерминизм, burst = main-thread); нормали единичные/без NaN;
сфера (замкнутость, поток=0, объём); плоскость `x=26/3` (вершина точно на
плоскости в пределах Q16-кванта, grid = cache − 1); реальный остров (Burst job vs main-thread);
PlayMode: MeshBuilder + RenderTexture (камера видит треугольники).

### P4b — выполнено

Реализовано:
- `TransvoxelTransitionTables.cs` — flat static readonly массивы (Burst-safe):
  `TransitionCornerOffset` (13 углов × 3), `TransitionCellClass` (512 byte, case 0..511 → класс 0..55),
  `TransitionCellGeometryCounts` (56 byte), `TransitionCellTriangleIndices` (56×36 byte),
  `TransitionCornerData` (56×13 byte), `TransitionVertexDataFlat` (512×12 ushort).
  Golden hashes (FNV-1a 64): `0x147BBBD3785D8EC5`, `0x7AB69356F98DB6BF`, `0x2BA3A2147DAD229B`,
  `0xCA3019016258D2BF`, `0x094C32965AB74A93`, `0xC31123ABB3D0B4A5`.
- `TransvoxelTransitionMesher.MeshTransitionInto` — transition cells одной грани чанка (lod ≥ 1):
  13 углов ячейки (4 угла coarse-сетки из кэша 19³, 5 вторичных точек — середины coarse-рёбер
  и центр — из `IslandSdf.SdfVoxel` точно в полушаге, 4 алиаса углов); 9-битный case code →
  класс → lookup геометрии; Q16-интерполяция через общий `TransvoxelMesher.EdgeT`;
  vertex cache — 2 дока по 16×10 int (инициализация −1, swap по строкам); нормали —
  целочисленный градиент генератора (центральная разность, шаг 1 воксель LOD0) в опорном
  вокселе (концевая точка ребра с меньшим |density|); материал — `IslandMaterialField.At`
  в grid-ячейке опорного вокселя. Выход — Q16-полуячейки × 2⁻¹⁷ = ячейки lod-1.
- `TransvoxelMesher` — `GetDensity` / `EdgeT` вынесены в общий доступ (используются и regular,
  и transition мешированием).

Отличия от эталона transvoxel_core (принятые решения P4b):
- **D1:** бисекция рёбер удалена — все пересечения считаются единой Q16-интерполяцией
  `EdgeT` по целым плотностям концов. Позиция вершины становится чистой функцией пары
  (концы, плотности) и не зависит от lod чанка: шов LOD-границы герметичен по построению.
- **D2:** secondary vertices удалены вместе с бисекцией (они компенсировали lod-зависимый
  сдвиг бисекции).
- **D3:** единая система координат выхода — Q16 в полуячейках, финальная конвертация
  умножением на 2⁻¹⁷ (шаг = ячейка lod-1). Для грубого ребра (delta = 2 полуячейки)
  значение равно 2 × qCell regular-мешера того же чанка; для мелкого (delta = 1) —
  побитово равно qCell мелкого соседа.
- **D4:** нормали — целочисленный градиент генератора в опорном вокселе вершины;
  градиент отдаётся как есть (int3), нормализация — ответственность Presentation.

Тесты (266 EditMode, 4 PlayMode): таблицы (golden-hash, замкнутость, диапазоны,
индексы ссылаются на валидные вершины); guard lod = 0 (пусто); all-air / all-solid
(пусто); реальный остров — грани [0, 16] coarse-cell units, индексы валидны,
количества вершин/материалов/градиентов согласованы; детерминизм (два вызова
идентичны); все 6 граней дают валидные индексы; **шов с fine-соседом**: позиции
вершин transition-клетки совпадают с позициями вершин мелкого чанка (lod-1)
в пределах Q16-кванта — герметичность LOD-границы.

## P5. Ядро и переходный слой

`Simulation/Voxels/BakeIslandCoreSystem`

Переходный слой `[-128, -64)` — обычные чанки коры: тот же пайплайн и те же
LOD, но правки туда не попадают из-за клампа на `DigLimitY`.

Ядро ниже `-128` — меш на шаге `max(2 * VoxelStep(k), CoreMinStep)`,
однократно. Статический коллайдер. Кэш по `IslandId`, допустима
детерминированная регенерация вместо хранения.

Тесты: rim ядра совпадает с сеткой переходного слоя бит-в-бит; ядро не
содержит данных выше `CrustStitchMinY`; шаг ядра не меньше `CoreMinStep`;
повторная генерация даёт идентичный меш; бюджет треугольников ядра
на `R = 2000` не превышен.

## P6. LOD и стриминг

Компоненты `VoxelChunk` (coord и LOD), `VoxelChunkDirty` как
`IEnableableComponent`, `VoxelChunkRequest` как `DynamicBuffer`. Один archetype
на все LOD. Резидентность по позиции игрока с округлением до `int3`.
Число уровней — `LodMax(R) + 1`, не константа. Бюджет построений на кадр,
очередь по расстоянию. Спавн и деспавн только ECB, батчем раз в кадр.
Коллизия только LOD0. За `LodMax(R)` остров воксельно не мешируется —
работает impostor.

Тесты PlayMode: число резидентных чанков в бюджете; нет структурных изменений
вне ECB; переходы LOD не создают и не удаляют компоненты; `LodMax` даёт 7
уровней при `R = 200` и 10 при `R = 2000`; нет утечек мешей при ста циклах
загрузки и выгрузки.

Выход: генерация завершена, остров проходим, бюджет кадра соблюдён.

## P7. Правки

`Core/Voxels/VoxelEditOp` — kind, центр `int3` в единицах 0,5 м, радиус, `Seq`.
Кламп по `DigLimitY` и полосе на входе. `VoxelBrushKind` без Paint:
`None`, `Subtract`, `Add`.

`Core/Voxels/VoxelEditSegment` — immutable blob на `SegmentCapacity` операций
с собственным пространственным индексом по чанкам.

`Simulation/Voxels/IslandVoxelEdits` — компонент на якоре: `LastSeq`,
`DynamicBuffer<BlobAssetReference<VoxelEditSegment>> Segments`,
`DynamicBuffer<VoxelEditOp> Tail`. Третий в фиксированном наборе, создаётся
в bootstrap.

`Simulation/Voxels/ApplyVoxelEditSystem` — append в `Tail`, seal при заполнении,
`VoxelChunkDirty` на затронутых чанках, наложение на payload на месте при
`BakedThroughSeq >= op.Seq - 1`.

Тесты: replay строго по `Seq` даёт golden-результат; перестановка операций
меняет результат (смешанные add/subtract не коммутативны); кламп не пускает
ниже `DigLimitY`; размер операции фиксирован; число dirty-чанков на правку
в ожидаемом диапазоне; журнал переживает Dehydrate; seal не меняет итоговую
плотность; сегменты освобождаются при уничтожении мира.

## P8. Компакция и персистентность

`Simulation/Voxels/CompactVoxelEditSystem` — при превышении `CompactThreshold`
чанк запекается в payload с палитрой, бит-упаковкой и `BakedThroughSeq`.
Сейв содержит только журнал (сегменты и хвост) и `LastSeq`. Запечённые чанки —
производный кэш, выбрасываются и пересобираются. Репликация операций клиентам
по `Seq > AppliedSeq`; `IslandTierState.Revision` к этому не привлекается.

Тесты: Dehydrate и Hydrate дают бит-идентичный terrain; остров без внутренних
сущностей помнит правки; компакция не меняет итоговую плотность; сброс
производного кэша и пересборка из журнала дают тот же результат; размер сейва
пропорционален числу операций; `AppliedSeq` догоняет `LastSeq` после ресинка.

## Зависимости

P1 → P2 → P3 → P4 → P5 → P6 → P7 → P8.

- P1 блокирует всё: ключи, константы, `LodMax`, `MinCenterSpacing`.
- P2 зависит только от P1.
- P3 и P4 последовательны, оба нужны до P5.
- P5 требует P4: переходный слой использует тот же меш-пайплайн.
- P6 требует P5 и `LodMax` из P1.
- P7 требует P6 (dirty-чанки) и формат сегмента из P1.
- P8 требует P7.

Impostor (отдельный этап) разблокирует дальние острова и снимает зависимость
`LodHardMax` от видимости 10 км.

