# AGENTS.md — Unity DOTS Project Rules

## PROJECT CONTEXT
- **Engine:** Unity 6.6 (DOTS / Entities).
- **Company:** SmilemanStudio
- **Root Namespace:** `SmilemanStudio` (Use this for ALL code to avoid conflicts with Asset Store).

## PROJECT STATE
- Always read `docs/PROJECT_STATE.md` at the beginning of any task to understand what is currently implemented. Do not guess the current state.

## ARCHITECTURE DECISIONS
- Before modifying simulation logic, you MUST read the relevant files in `docs/adr/`.
- Current active ADRs:
- `0001-island-as-atom.md`: Islands are atomic units. Every island ALWAYS exists as an anchor entity with a fixed component set (`IslandManifest` + `IslandTierState` + `IslandVoxelEdits`). Cold/Background islands are "conserved": they have no internal entities (inhabitants, buildings, items) — only macro simulation driven by `IslandTierState`. Never spawn internal entities for inactive islands, and never add/remove components on an island anchor at runtime.
- `0002-voxel-island-terrain.md`: Voxel terrain is generated parametrically from `IslandManifest.Radius` and `Id`. The mutable crust (`Y ∈ [−64, 448]`, voxel step 0.5 m, dig limit `Y = −50`) is backed by a segmented append-only CSG edit journal (`IslandVoxelEdits`); the immutable transition layer `[−128, −64)` is meshed by the same pipeline, and the immutable baked core below `CrustStitchMinY = −128` is regenerated from `Id` and `Radius`. Baked chunks are a derived cache carrying `BakedThroughSeq`, not save data. 16³ chunks, `short` density at 256 units per voxel, integer-only noise, Marching Cubes with skirts. LOD count is `LodMax(Radius) + 1` (6 levels at `R = 200`, 9 at `R = 2000`), not a constant; chunk keys use 4 bits for LOD.

## ECS PERFORMANCE: ARCHETYPES & STRUCTURAL CHANGES
Unity ECS is archetype-based: an entity's **archetype** is its exact component set, and entities are stored in chunks per archetype. Operations that change an archetype are **structural changes**: creating/destroying entities, adding/removing components, and setting shared component values. A structural change moves the entity to another chunk and may allocate a new one. Doing this per-entity at runtime causes **archetype fragmentation** (a.k.a. archetype explosion) and **chunk fragmentation**, plus sync points that stall the job system — a major performance loss.

Rules (apply to ALL simulation code):
- **Pre-create archetypes.** Build the full archetype set once, at bootstrap/authoring time (`EntityManager.CreateArchetype`, prefabs, subscenes). Runtime systems must reuse existing archetypes, never invent new component combinations on the fly.
- **Never add/remove components to toggle behavior at runtime.** Use `IEnableableComponent` + `EntityManager.SetComponentEnabled` / `EnabledRefRW` instead — toggling an enableable component is NOT a structural change and creates no new archetype.
- **Changing a component's value is not a structural change.** Prefer data flags (`Tier`, state enums, bools inside a component) over the presence/absence of a component.
- **Batch all unavoidable structural changes** (spawning/despawning entities, e.g. `Hydrate`/`Dehydrate`) through `EntityCommandBuffer`, played back once per frame — never inside a per-entity loop or a hot path.
- **Adding a new archetype is an architectural decision**: document it and ask the user first (see ASK FIRST).

## ECS DATA MODELLING: SEPARATE BY ACCESS PATTERN
ECS rewards splitting data by *access pattern*, not by *object*. If different systems need different subsets of an entity's data, or the subsets change at different rates, they belong in separate components on the same entity — not in one large "god component". A wide component forces every system to load the whole thing into cache and makes write conflicts appear where there is no real sharing.

Rules:
- **Design the split up front.** Component layout is decided at authoring/bootstrap time. Splitting or merging components at runtime is a structural change and is forbidden (see above).
- **Hot/cold split.** Fields read every frame live apart from fields read rarely (debug data, save metadata, counters).
- **Ownership split.** Fields written by different systems live apart, so systems don't contend for write access to the same chunk.
- **Smell test.** A component with more than ~8-10 fields is usually two components. Ask which systems need which fields.
- **Repeated groups.** If several components share the same group of fields (e.g. a source/target pair, a time window, a min/max range), extract that group into its own component or nested struct rather than duplicating the fields.
- **Exception:** the island anchor set (`IslandManifest` + `IslandTierState` + `IslandVoxelEdits`) is frozen by ADR-0001 and is never split.

## NAMING & PATTERNS
- **Namespaces MUST match folder path** (e.g., `Assets/Scripts/Simulation/MySystem.cs` -> `namespace SmilemanStudio.Simulation`).
- **Simulation Code:** Must live in `Assets/Scripts/Simulation/` and use namespace `SmilemanStudio.Simulation`.
- Systems: `<Verb><Noun>System` (e.g., `IslandHydrationSystem`).
- Components: Noun only, no `Component` suffix (e.g., `IslandManifest`).
- Default: `ISystem` + `[BurstCompile]`. `SystemBase` only for UI/Managed bridges.

## CODING CONVENTIONS
- **Enums MUST have a `None = 0` (or `Invalid = 0`)** value as the default. Never rely on the first logical value being 0. This prevents "uninitialized" states from being treated as valid game states.
- **IDs MUST treat `0` or empty struct as invalid.** `IslandId(0)` means "no island".

## NEVER DO (Critical Violations)
- **NO** `UnityEngine.Random` in `Simulation` namespaces. Use `Unity.Mathematics.Random` with explicit seeds.
- **NO** `UnityEngine.Time` in `Simulation` namespaces. Use `SystemAPI.Time`.
- **NO** `MonoBehaviour` in simulation logic.
- **NO** modifying `manifest.json` dependency rules without asking.
- **NO** skipping golden-hash test updates if you touch simulation logic.

## ASK FIRST (Do not guess)
- Changing public interfaces in `SmilemanStudio.Core`.
- Adding a new asmdef module.
- Changing system execution order (`[UpdateBefore]`/`[UpdateAfter]`).

## VERIFICATION COMMANDS
Run these in order before saying "done":

1. Architecture Check:
   `python tools/check_arch.py`
   - If output is `OK`, proceed. If output is a list of errors, fix them before continuing.

2. Clean old test results:
   `if (Test-Path 'E:\Unity\GitRepositories\facing-mist\Logs\TestResults.xml') { Remove-Item 'E:\Unity\GitRepositories\facing-mist\Logs\TestResults.xml' -Force }`
   `if (Test-Path 'E:\Unity\GitRepositories\facing-mist\Logs\TestResultsPlayMode.xml') { Remove-Item 'E:\Unity\GitRepositories\facing-mist\Logs\TestResultsPlayMode.xml' -Force }`

3. Run Tests (EditMode):
   `Start-Process -FilePath "E:\Unity\UnityHub\Editor\6000.6.9f1\Editor\Unity.exe" -ArgumentList '-runTests','-batchmode','-projectPath','E:\Unity\GitRepositories\facing-mist','-testResults','E:\Unity\GitRepositories\facing-mist\Logs\TestResults.xml','-testPlatform','EditMode','-logFile','E:\Unity\GitRepositories\facing-mist\Logs\unity_test.log' -Wait -NoNewWindow`

4. Run Tests (PlayMode):
   `Start-Process -FilePath "E:\Unity\UnityHub\Editor\6000.6.9f1\Editor\Unity.exe" -ArgumentList '-runTests','-batchmode','-projectPath','E:\Unity\GitRepositories\facing-mist','-testResults','E:\Unity\GitRepositories\facing-mist\Logs\TestResultsPlayMode.xml','-testPlatform','PlayMode','-logFile','E:\Unity\GitRepositories\facing-mist\Logs\unity_test_playmode.log' -Wait -NoNewWindow`

5. Check results:
   - Read `E:\Unity\GitRepositories\facing-mist\Logs\TestResults.xml` (EditMode).
   - Read `E:\Unity\GitRepositories\facing-mist\Logs\TestResultsPlayMode.xml` (PlayMode).
   - If a file does not exist, or does not contain `<test-run>`, it means Unity failed to compile or run. **Do not assume success.**
   - If `<test-run result="Passed">`, all tests passed.
   - If `result="Failed"`, find `<test-case ... result="Failed">` and report the failing test names.

## ENVIRONMENT NOTES
- IDE tool `create_new_file` does NOT persist files to disk in this environment (reports success, but the file is missing).
- To create a new file: use PowerShell `Set-Content -Path <path> -Value $content -Encoding UTF8` (here-string for multi-line content), then verify with `Test-Path`.
- `edit_file` works correctly for existing files.
- After any file creation, ALWAYS verify: `Test-Path <path>` and `git status --short`.