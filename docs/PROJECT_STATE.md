# Роль и контекст
Ты — ведущий системный архитектор и senior-разработчик Unity DOTS. Мы разрабатываем большой инди-проект с нуля. Твоя задача — помогать мне проектировать архитектуру, писать чистый код и строго следовать установленным правилам. Мы работаем итеративно: я ставлю задачу, ты предлагаешь решение, пишешь код и проверяешь его тестами.

# Обзор проекта
- **Жанр:** Игра про автоматизацию в открытом 3D-мире (Factorio-like, Satisfactory-like), но с упором на выживание, полеты и оборону баз.
- **Движок:** Unity 6.6 (URP), стек DOTS (Entities 6.6, Burst, Collections, Physics).
- **Мир:** Ограниченный, 100x100 км. Состоит из двух слоев:
  1. Поверхность (агрессивная среда, ядовитый туман). Строить можно, но мало. Из тумана торчат шпили и деревья (точки интереса).
  2. Облачный слой (основной геймплей): ~100 летающих воксельных островов
     радиусом 200…2000 м (`IslandManifest.Radius`, целое в метрах). Толщина
     и рельеф выводятся из радиуса, абсолютных размеров в генерации нет.
     Острова сгруппированы в архипелаги-биомы, расстояние между ними 2-5 км.
     Видимость ограничена туманом (~10 км). См. ADR-0002.
- **Масштаб:** Целевое количество машин — 100k на весь мир. Игрок будет активно развиваться на 10-20 островах (в среднем ~1000 машин на активный остров).
- **Мультиплеер:** Кооп 2-8 игроков, Client-Server (listen host), авторитет сервера. Host migration в v1 не нужен.

# Ключевые архитектурные решения (ADR)
1. **Остров — атомарная единица симуляции.** Каждый остров всегда существует как сущность-якорь с фиксированным набором компонентов: `IslandManifest` + `IslandTierState` + `IslandVoxelEdits`. Острова имеют 4 тира симуляции:
    - `Hot` (игрок на острове): полная ECS-симуляция.
    - `Warm` (игрок рядом): упрощённая ECS-симуляция.
    - `Cold` (игрок далеко): остров не существует в ECS с внутренними сущностями, только макро-состояние в `IslandTierState` (Revision, LastSimulationTime).
    - `Background` (игрок никогда не был): базовая симуляция жизни фракций через `IslandTierState`.
    - Переходы между тирами: `Hydrate` (создать внутренние сущности острова) и `Dehydrate` (свернуть внутренние сущности в макро-состояние). Обе операции меняют только внутренние сущности и `IslandTierState`.
2. **Трубы (автоматизация).** Трубы заменяют конвейеры. В трубах нет давления и предметов-сущностей. Это очереди ограниченной длины с мгновенным перемещением. Перемещение предметов считается алгоритмом O(перемещённых предметов). Фильтры — это данные (правила вида "ждать комплект", "пропорция 3 к 1").
3. **Разрушение и обрушение.** Воксельная только земля (SDF + Transvoxel).
   Terrain не хранится — хранится сегментированный журнал CSG-операций правок
   (брашей) фиксированного размера, append-only. Запечённые чанки — производный
   кэш для ограничения стоимости replay, не сейв-данные. Воксельный шаг 0,5 м,
   мутабельная кора Y ∈ [−64, 448], лимит копания Y = −50. Ниже: иммутабельный
   переходный слой Y ∈ [−128, −64), который мешируется общим пайплайном и
   убирает видимый шов, и иммутабельное запечённое ядро ниже −128, которое
   не меняет ничто. Постройки игрока модульные (как в Rust), имеют HP;
   при разрушении детали может смениться префаб на «пробитый». Обрушение
   работает по графу стабильности. См. ADR-0002.
4. **Макро-симуляция.** Фракции живут своей жизнью на всех островах. Если игрок отсутствует, макро-бой может повредить его базу (HP и структура сохраняются в `IslandTierState` — поля `Revision` и `LastSimulationTime`).
5. **Воксельный terrain островов** (`0002-voxel-island-terrain.md`): параметрическая форма из `Radius` и `Id`;
   кора, иммутабельный переходный слой и иммутабельное ядро; 16³ чанки
   с выравниванием по 64 м, шаг 0,5·2^k, число LOD — `LodMax(R) + 1`
   (6 при R = 200, 9 при R = 2000), а не константа; плотность `short`
   по 256 единиц на воксель; сегментированный журнал CSG-операций append-only
   плюс производный кэш запечённых чанков с `BakedThroughSeq`; целочисленный
   шум и запрет трансцендентных; Transvoxel (бесшовный LOD); материал
   процедурный (покраска отложена); `IslandVoxelEdits` третьим компонентом
   якоря.

# Технологический стек и работа с агентом (Koda)
- Мы работаем в Rider с плагином Koda (модель Qwen 27B и DeepSeek Flash и др. через API).
- **Строгие правила для симуляции:**
  - В модуле `SmilemanStudio.Simulation` запрещено использовать `UnityEngine.Random`, `UnityEngine.Time`, `MonoBehaviour`.
  - Координаты островов — строго целочисленные `int3` в метрах (для детерминизма). Координаты игрока — `float3`, но для расчетов округляются до `int3`.
  - Все системы: `public partial struct : ISystem`, по умолчанию с `[BurstCompile]`.
  - Неймспейсы строго соответствуют папкам (Корень: `SmilemanStudio`).
- **Проверка кода (Verification Commands):**
  Каждая задача завершается запуском проверок через терминал:
  1. `python tools/check_arch.py` (проверка архитектурных зависимостей).
  2. Очистка старых логов.
  3. Запуск EditMode/PlayMode тестов через Unity batchmode с сохранением в `Logs/TestResults.xml`.
  4. Чтение XML и отчет о том, что тесты прошли.
- **Файловая система:** Из-за бага Koda файлы нужно создавать через терминал (`Set-Content`), а не через встроенный инструмент создания файлов.

# Текущее состояние проекта (Что уже сделано)
1. **Инфраструктура:** Настроен `AGENTS.md`, `.koda/prompts/project-rules.prompt`, asmdef-структура (Core, Simulation, Presentation, Bootstrap), скрипт `check_arch.py`. Работает пайплайн тестов через терминал.
2. **Core (Данные и математика):**
    - `IslandId` (с защитой от невалидных дефолтных значений: `Value == 0` означает «нет острова»).
    - `IslandTier` (enum с `None = 0`, значения Hot=1, Warm=2, Cold=3, Background=4).
    - `WorldHasher` (FNV-1a для golden-hash тестов).
    - `WorldMath` (безопасный расчёт квадратов расстояний в `long`).
    - `Core.Voxels.VoxelConstants` — единственный источник значений из ADR-0002 (радиусы, полосы, LOD, промилле формы, ёмкость сегмента журнала).
    - `Core.Voxels.IslandFootprint` — целочисленная геометрия силуэта и правила раскладки: `Extent`, `MinEdgeGap`, `MinCenterSpacing`, `EdgeGap`, `IsValidRadius`. Единственный источник дистанций раскладки.
    - `Core.Voxels.VoxelLod` — уровни детализации: `VoxelStepMilli(k)`, `ChunkWorldSize(k)`, `LodMax(R)`, `LodSwitchRadius(k)`. Целочисленный эквивалент `ceil(log2(ceil(R/4)))`, ограничено `LodHardMax`.
    - `Core.Voxels.VoxelCoord` — island-local воксельные координаты LOD0: перевод в мм, floor-деление, `ClampY`, `VoxelsPerMeter()`.
    - `Core.Voxels.VoxelChunkCoord` — упаковка ключей чанков в `long` (ADR-0002, D8): `Pack`, `UnpackX/Y/Z/Lod`, `IsValid`, `PackFromVoxel`.
    - `Core.Voxels.VoxelDensity` — квантование SDF→`short` и интерполяция `t` вдоль ребра в fixed-point Q16.
    - `Core.Voxels.VoxelNoise` — детерминированный целочисленный шум: `Hash` (Murmur3 fmix32), `ValueNoise2D`, `Fbm2D` (5 октав, gain 1/2, lacunarity 2), `ValueNoise3D` (8 узлов, Q16), `Fbm3D` (5 октав).
    - `Core.Voxels.VoxelMaterial` — enum (`None=0`, `Dirt=1`, `Coal=2`, `Sand=3`, `Quartz=4`).
    - `Core.Voxels.VoxelBrushKind` — enum CSG-операций (`None=0`, `Add=1`, `Subtract=2`).
    - `Core.Voxels.IslandSdf` — параметрический SDF острова: `ApexDepth`, `TopHeightMax`, `TopSurface`, `Sdf` (метры), `SdfVoxel` (единицы шага LOD0, 0.5 м), `FloorSqrt`. Чисто int-пайплайн (C#-деление, truncation toward zero).
    - `Core.Voxels.IslandMaterialField` — `At(x,y,z,id)` → `VoxelMaterial`. Argmax 4×`Fbm3D`, клетка 32 м (`MATERIAL_SCALE=2048`).
3. **Simulation (ECS):**
    - `IslandManifest` (компонент острова: `Id`, `Origin`, `Radius` — статика, не меняется).
    - `IslandTierState` (компонент острова: `Tier`, `Revision`, `LastSimulationTime` — динамическое состояние;
    - `PlayerPosition` (ECS-синглтон: позиция игрока в `float3`).
    - `TierCalculator` (чистая функция определения тира по квадрату расстояния и радиусу острова).
    - `TierManagerSystem` (раз в секунду пересчитывает тир: обновляет `Tier` и `Revision` в `IslandTierState`).
    - `Simulation.Voxels.SampleVoxelChunkJob` — `[BurstCompile] IJob`, заполняет scratch-буферы
      через generic `VoxelFieldSampler.FillDensity` / `FillNodeMaterial` (работает с любым
      `IVoxelDensityField`/`IVoxelMaterialField`, не знает про остров).
    - `Simulation.Voxels.MeshVoxelChunkJob` — `[BurstCompile] IJob`, вход: chunkKey+islandId+radius+SeamFaceMask;
      выход: NativeArray positions/normals/indices/materials. Вызывает `TransvoxelMesher.MeshInto`
      с NativeList-полями (паттерн Burst IJob без IJob<T>).
    - `Simulation.Voxels.VoxelChunkBuildJob<TD,TM>` — generic `[BurstCompile] IJob`: field + request →
      объединённая геометрия (regular + transition) через `VoxelChunkMesher.BuildChunk`. Джоба видит
      только контракты полей — автономность движка в job-системе.
4. **Presentation (рендер):**
    - `Presentation.Voxels.VoxelChunkMeshBuilder` — сборка `UnityEngine.Mesh` из `VoxelChunkGeometry`:
      grid→world, vertex color по палитре `VoxelMaterial`, indexFormat UInt32.
    - `Presentation.Voxels.VoxelChunkView` — MonoBehaviour-мост: `IslandDensityField`/`IslandMaterialAdapter`
      → `VoxelChunkMesher.BuildChunk` → `VoxelChunkMeshBuilder.Build` → `MeshFilter`.
    - `Presentation.VoxelDebugLodCascade`, `Presentation.VoxelDebugSphere` — debug-сцены на движке:
      `SphereDensityField` + `NullMaterialField` + `VoxelLodPlanner.Plan` + `VoxelChunkMesher.BuildChunk`.
5. **Bootstrap:** `WorldBootstrapSystem` (спавнит тестового игрока и 100 островов при старте). Радиусы берутся из `VoxelConstants`, острова раскладываются rejection sampling по `IslandFootprint`: расстояние между краями не меньше радиуса меньшего острова. Верхней границы дистанции нет.
6. **Тесты:**
    - **EditMode (18 фикстур, 242 теста):** `IslandIdTests`, `IslandTierStateTests`, `TierCalculatorTests`, `WorldHasherTests`, `IslandFootprintTests`, `VoxelLodTests`, `VoxelCoordTests`, `VoxelChunkCoordTests`, `VoxelDensityTests`, `VoxelNoiseTests`, `VoxelEnumTypesTests`, `IslandSdfTests`, `IslandMaterialFieldTests`, `TransvoxelTablesTests`, `TransvoxelMesherTests`, `TransvoxelNormalInterpolationTests`,
      `VoxelFieldSamplerTests` (generic fill + геометрия), `VoxelLodPlannerTests` (инвариант seam↔finer-сосед,
      нет двойного покрытия, детерминизм, chunkKey roundtrip), `VoxelChunkMesherTests` (валидная геометрия,
      unit-нормали, transition в coarse-пространстве — регрессия на дыры, seam mask меняет геометрию,
      детерминизм, паритет джобы, bitwise-совпадение с островом).
    - **PlayMode (4):** `TierManagerPlayModeTests`, `WorldBootstrapPlayModeTests`, `VoxelPresentationPlayModeTests` (2 теста: сборка меша + рендер через RenderTexture).

# P4a завершён: Transvoxel regular cells (LOD 0)
Реализовано в `Core/Voxels/TransvoxelTables.cs` + `Core/Voxels/TransvoxelMesher.cs`:
- Таблицы Laine–Krahenbuhl (GDC 2006) перенесены из `transvoxel_core/` в Burst-safe flat arrays.
- Алгоритм: case code (8 бит) → `RegularCellClass` → lookup геометрии → интерполяция Q16 → vertex cache (2 dock-а 16×16×4 int, swap по слоям y).
- Vertex cache: сброс слота 0 ДО `continue`; угловые вершины (`cacheIdx == 0`) пишутся в выбранный dock; интерполированные (`cornerIdx1 == 7`) пишутся в `currentCache[x*16*4 + z*4 + cacheIdx]` (эталон transvoxel_core строки 239-242).
- Нормали: центральная разность плотности по 19³ кэшу (полу база = 1 шаг), направлены наружу (от грунта к воздуху).
- **Winding fix (2026-11-21):** `MeshInto` разворачивает обход таблиц на (ia, ic, ib) —
  таблицы Laine–Krahenbuhl дают обход, согласованный с inward-нормалями эталона
  transvoxel_core, а P4a считает нормали наружу. Без разворота треугольники были
  вывернуты относительно нормалей (backface culling показывал сферу наизнанку).
  Transition-мешер (P4b) разворачивал аналогично, теперь regular и transition
  согласованы между собой. Контракт зафиксирован в XML-doc `TransvoxelMeshOutput`
  и тестами: `Mesh_Sphere_CrossProductNormal_MatchesVertexNormal` (dot(cross,
  vertexNormal) > 0.5 на каждом невырожденном треугольнике сферы) и знак
  знакового объёма в `Mesh_Sphere_ClosedSurface_VolumeAndFlux`. Таблицы не
  менялись — golden hashes валидны.
- Материалы: `IslandMaterialField.At` в grid-ячейке вершины.
- Golden hashes (FNV-1a 64): `RegularCornerOffset 0x229B64048EA9BCC5`, `RegularCellClass 0xEFFBF2112A9922D7`, `RegularCellGeometryCounts 0x132487C81C138B5D`, `RegularCellTriangleIndices 0xD656F9622A6EECE0`, `RegularVertexDataFlat 0x25A2E854318F80CD`.

# P5 завершён: чистые буферные входные мешера (2026-11-22)

`TransvoxelMesher.MeshInto` и `TransvoxelTransitionMesher.MeshTransitionInto`
превращены в чистые функции трёх входных буферов:

| Буфер | Размер | Тип | Описание |
|-------|--------|-----|----------|
| `density` | 19³ | `short` | Плотность чанка LOD0. |
| `nodeMaterial` | 17³ | `byte` | Узловая сетка материала. |
| `face` | 35×35×3 | `short` + `byte` | `TransitionFaceSamples` для transition-граней. |

Ключевые изменения:
- Убрана зависимость `MeshTransitionInto` от `IslandSdf.SdfVoxel` — вторичные
  точки берутся из `TransitionFaceSamples`, заполняемого через
  `VoxelChunkSampler.SampleFace`.
- Убрана зависимость `MeshInto` / `MeshTransitionInto` от `IslandMaterialField.At` —
  материал берётся из `nodeMaterial` / `face.Material`, заполняемого через
  `VoxelChunkSampler.SampleNodeMaterial`.
- Единая система координат материала: regular и transition используют chunk-local
  grid (0..16).
- `VoxelChunkSampler` расширен: `SampleNodeMaterial` (17³ byte), `SampleFace`
  (35×35×3 short + byte), константы `NodeMaterialVolume = 17³`,
  `FaceSamplesVolume = 35×35×3 = 3675`.
- `SampleVoxelChunkJob` и `MeshVoxelChunkJob` обновлены для `NodeMaterial`.
- Presentation обновлена: `VoxelChunkView`, `VoxelDebugSphere`,
  `VoxelDebugLodCascade` — все заполняют буферы до вызова мешера.
- ADR-0002 разделы D4/D7 обновлены.
- Все 273 EditMode + 4 PlayMode тестов прошли.

**Следующий этап:** Реализация `VoxelChunkView` с полным стримингом и
`Hydrate`/`Dehydrate` для transition-островов.

# P4b завершён: Transvoxel transition cells (LOD > 0)
Реализовано в `Core/Voxels/TransvoxelTransitionTables.cs` + `Core/Voxels/TransvoxelMesher.Transitions.cs`:
- Таблицы transition cells Laine–Krahenbuhl (GDC 2006) — Burst-safe flat arrays: 13 углов ячейки (4 угла coarse-сетки + 5 вторичных точек + 4 алиаса), 512 case-кодов → 56 классов, `MaxCellTriangleIndices = 36`.
- `TransvoxelTransitionMesher.MeshTransitionInto` — transition cells одной грани чанка (lod ≥ 1): 4 угла ячейки берутся из кэша 19³ (узлы coarse-сетки = узлы мелкой сетки соседа), 5 вторичных точек (середины coarse-рёбер, центр) — из `IslandSdf.SdfVoxel` точно в полушаге (int3, без округления); Q16-интерполяция через общий `TransvoxelMesher.EdgeT`; vertex cache — 2 дока 16×10 int (инициализация −1, swap по строкам); выход — Q16-полуячейки × 2⁻¹⁷ = ячейки lod-1.
- Нормали: целочисленный градиент генератора (центральная разность, шаг 1 воксель LOD0) в опорном вокселе — концевая точка ребра с меньшим |density|; нормализация — на стороне Presentation. Материал — `IslandMaterialField.At` в grid-ячейке опорного вокселя.
- Отличия от эталона (принятые решения): **D1** — бисекция удалена, единая Q16-интерполяция делает позицию вершины чистой функцией (концы, плотности) → LOD-шов герметичен по построению; **D2** — secondary vertices удалены (компенсировали lod-зависимый сдвиг бисекции); **D3** — единая система координат выхода (Q16-полуячейки, ×2⁻¹⁷); **D4** — нормали = integer-градиент (Presentation нормализует).
- Golden hashes (FNV-1a 64): `TransitionCornerOffset 0x147BBBD3785D8EC5`, `TransitionCellClass 0x7AB69356F98DB6BF`, `TransitionCellGeometryCounts 0x2BA3A2147DAD229B`, `TransitionCellTriangleIndices 0xCA3019016258D2BF`, `TransitionCornerData 0x094C32965AB74A93`, `TransitionVertexDataFlat 0xC31123ABB3D0B4A5`.
- Тесты: +15 (guard lod=0, all-air/solid, реальный остров, детерминизм, все 6 граней, **шов с fine-соседом** — позиции совпадают в пределах Q16-кванта).

**Следующий этап — P5:** Ядро и переходный слой (см. `docs/VOXEL_ROADMAP.md`).

# P6 завершён: автономный воксельный движок (generic field-контракт)

Движок переведён на полностью generic-контракты: ANY shape (остров, сфера, том, что
угодно) мешируется одним путём. Вызывающий поставляет только плотность + материал +
AABB + позицию игрока; всё остальное (LOD-каскад, seam-шов, геометрия) считает движок,
и всё считается в джобах (Burst-safe, без managed-аллокаций).

## Новые модули (Core/Voxels)
- `IVoxelDensityField { short SampleDensity(int3 voxel); }` — generic-контракт плотности.
  Burst деvirtуализирует вызовы (struct + интерфейс).
- `IVoxelMaterialField { byte SampleMaterial(int3 voxel); }` — generic-контракт материала.
- `VoxelFieldSampler` — generic выборка, знает только контракт + геометрию чанка (не остров/сферу):
  `FillDensity<TD>` (19³ scratch), `FillNodeMaterial<TM>` (17³ scratch), `FillFace<TD,TM>` (35×35×3),
  geometry helpers `ChunkOrigin0`, `VoxelFromIndices`, `FaceMinCorner/Size`. Константы:
  `CacheVolume=6859`, `NodeMaterialVolume=4913`, `FaceSamplesVolume=3675`.
- `VoxelChunkRequest` — struct: `ChunkCoord`, `Lod`, `Origin0`, `SeamFaceMask`.
- `VoxelLodPlanner` — планировщик LOD-каскада (geometry-clipmap): `Plan(boundsMin, boundsMax,
  playerVox, lodMax, NativeList<VoxelChunkRequest>)`. Регион R_k — куб (2·`RegionHalfChunks`)³
  чанков уровня k, центр снапан на сетку k+1. Чанк мешируется, если не покрыт R_{k-1} и
  пересекает AABB фигуры. `RegionHalfChunks = 4`.
- `VoxelChunkGeometry` — struct с NativeList positions/normals/materials/indices, IDisposable.
- `VoxelChunkMesher` — generic-оркестратор `BuildChunk<TD,TM>(fields, request, ref geometry,
  densityScratch, nodeMatScratch)`: fill → regular → transition → combined geometry.
- `IslandDensityField` (обёртка `IslandSdf.SdfVoxel`), `IslandMaterialAdapter` (обёртка
  `IslandMaterialField.At`), `SphereDensityField`, `NullMaterialField` — реализации контрактов.
  Все в Core (чистая математика), чтобы EditMode-тесты использовали их без ссылки на Presentation.

## Исправлены дыры между LOD-чанками (корневая причина)
Старый `VoxelDebugLodCascade` ставил seam-бит по совпадению грани региона с границей
(`origin0 + chunkVox == cf - hf`), не проверяя, что finer-сосед реально мешируется → на
угловых чанках появлялась transition-полоса без finer-геометрии (дыра/двойная стенка).
- **Фикс в `VoxelLodPlanner`:** seam-бит ставится ТОГДА И ТОЛЬКО КОГДА сосед через грань
  покрыт finer-регионом И сам сосед пересекает AABB фигуры (значит будет меширован).
  За счёт снапа центра finer-региона на сетку k+1 частичного перекрытия нет по построению —
  coarse-чанк покрыт R_{k-1} целиком либо вовсе.
- **Фикс в `VoxelChunkMesher.AppendTransitions`:** transition-вершины выходили в half-cell
  пространстве (0..32), но масштабировались тем же `cellVox = 1<<lod`, что и regular (0..16) →
  шов уезжал в 2 раза дальше → трещина. Теперь позиции transition делятся на 2 (half-cell →
  coarse-cell), единая система координат. Регрессия зафиксирована тестом
  `VoxelChunkMesherTests.TransitionVertices_InCoarseCellSpace_NotHalfCell`.

## Миграция вызывающих сторон
- `Simulation.Voxels.VoxelChunkSampler` **удалён** (+ .meta); его заменил generic `VoxelFieldSampler`.
- `SampleVoxelChunkJob` / `MeshVoxelChunkJob` переведены на `VoxelFieldSampler.Fill*`;
  `MeshVoxelChunkJob` получил поле `SeamFaceMask`. Добавлен generic `VoxelChunkBuildJob<TD,TM>`.
- `Presentation.VoxelDebugLodCascade`, `VoxelDebugSphere`, `Voxels/VoxelChunkView` переписаны на движок
  (`VoxelLodPlanner.Plan` + `VoxelChunkMesher.BuildChunk`); `VoxelChunkMeshBuilder` получил
  новую сигнатуру `Build(in VoxelChunkGeometry, origin0, lod, worldOrigin)` с масштабом `cellVox = 1<<lod`.
- `Presentation.Voxels/SphereDensityField.cs` перемещён в Core.

## Тесты
- **Удалены устаревшие:** `VoxelChunkSamplerTests`.
- **Новые:** `VoxelFieldSamplerTests`, `VoxelLodPlannerTests`, `VoxelChunkMesherTests`.
- **Починены golden-тесты:** `TransvoxelMesherTests`, `TransvoxelNormalInterpolationTests`
  (вызовы `VoxelChunkSampler` заменены на `IslandDensity/IslandNodeMat/IslandFace` helpers),
  `VoxelPresentationPlayModeTests` (новая сигнатура builder).
- Итог: `check_arch.py` → OK; EditMode **242/242 Passed**; PlayMode **4/4 Passed**.

**Следующий этап:** полный стриминг чанков (`VoxelChunkView`) + `Hydrate`/`Dehydrate`
transition-островов (см. `docs/VOXEL_ROADMAP.md`).