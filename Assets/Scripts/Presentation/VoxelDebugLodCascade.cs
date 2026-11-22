using System.Collections.Generic;
using SmilemanStudio.Core;
using SmilemanStudio.Core.Voxels;
using SmilemanStudio.Simulation.Voxels;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace SmilemanStudio.Presentation
{
    /// <summary>
    /// Визуальный тест LOD-каскада (ADR-0002): «игрок» в (Radius * 1.1, 0, 0) метров,
    /// вокруг него LOD0, дальше уровни расходятся каскадом вложенных кубов-регионов.
    /// Половина региона k = 4 чанка своего LOD (чётно ⇒ границы регионов совпадают с
    /// сетками всех более крупных LOD): чанк либо целиком покрыт тонким регионом
    /// (пропуск), либо целиком снаружи (мешируется) — без дыр и перекрытий.
    /// Transition cells мешируются на гранях чанка LOD k >= 1, смежных с тонким
    /// регионом, — видимый шов LOD с нормалями D4a.
    /// Presentation-мост: MonoBehaviour допустим (AGENTS: MonoBehaviour запрещён
    /// только в Simulation).
    /// </summary>
    [ExecuteAlways]
    public class VoxelDebugLodCascade : MonoBehaviour
    {
        [SerializeField] private int islandIdValue = 1;
        [SerializeField] private int radius = 200;
        [SerializeField] [Range(1, 9)] private int lodCount = 6;
        [SerializeField] private bool useVertexColors = true;
        [SerializeField] private Material overrideMaterial;

        /// Метров на воксель LOD0 (шаг 0.5 м, ADR-0002).
        private const float VoxelStep = 0.5f;

        /// Половина региона k в чанках своего LOD (чётное — для выравнивания границ).
        private const int RegionChunks = 4;

        private readonly List<GameObject> spawned = new List<GameObject>();
        private Material cachedMaterial;

        [ContextMenu("Generate")]
        public void Generate()
        {
            Clear();
            var id = new IslandId(islandIdValue);
            if (id.Value == 0) return;

            cachedMaterial = overrideMaterial != null
                ? overrideMaterial
                : new Material(Shader.Find("Sprites/Default"));

            int3 playerVox = PlayerVoxels();
            int islandHalf = radius * 2 + 64; // воксели LOD0: радиус в метрах * 2 + запас

            for (int k = 0; k < lodCount; k++)
            {
                int chunkVox = 16 << k;              // ребро чанка LOD k в вокселях LOD0
                int halfVox = chunkVox * RegionChunks;   // половина своего региона
                int halfInner = chunkVox * RegionChunks / 2; // половина региона k-1

                int3 cMin = FloorDiv(playerVox - new int3(halfVox), chunkVox);
                int3 cMax = FloorDiv(playerVox + new int3(halfVox - 1), chunkVox);
                cMin = math.max(cMin, FloorDiv(new int3(-islandHalf), chunkVox));
                cMax = math.min(cMax, FloorDiv(new int3(islandHalf), chunkVox));

                for (int cx = cMin.x; cx <= cMax.x; cx++)
                for (int cy = cMin.y; cy <= cMax.y; cy++)
                for (int cz = cMin.z; cz <= cMax.z; cz++)
                {
                    int3 origin0 = new int3(cx, cy, cz) * chunkVox;

                    // Покрыт тонким регионом — там отмешируется LOD k-1.
                    if (k > 0 && InsideBox(origin0, chunkVox, playerVox, halfInner)) continue;
                    // Вне габарита острова — пусто по построению.
                    if (!IntersectsBox(origin0, chunkVox, int3.zero, islandHalf)) continue;

                    long key = VoxelChunkCoord.Pack(cx, cy, cz, k);
                    using var density = VoxelChunkSampler.Sample(key, id, radius, Allocator.Temp);
                    var output = TransvoxelMesher.Mesh(density, id, Allocator.Persistent);
                    if (output.VertexCount == 0)
                    {
                        output.Dispose();
                        continue;
                    }

                    // Transition cells: грани, за которыми начинается тонкий регион.
                    var tPos = new NativeList<float3>(64, Allocator.Temp);
                    var tGrad = new NativeList<int3>(64, Allocator.Temp);
                    var tMat = new NativeList<byte>(64, Allocator.Temp);
                    var tIdx = new NativeList<uint>(96, Allocator.Temp);
                    if (k >= 1)
                    {
                        for (int d = 1; d <= 6; d++)
                        {
                            var dir = (TransitionDirection)d;
                            int3 n = origin0 + DirVec(dir) * chunkVox;
                            if (!InsideBox(n, chunkVox, playerVox, halfInner)) continue;
                            TransvoxelTransitionMesher.MeshTransitionInto(
                                density, id, radius, origin0, dir, k, tPos, tGrad, tMat, tIdx);
                        }
                    }

                    BuildChunkGo(k, cx, cy, cz, origin0, output, tPos, tGrad, tMat, tIdx);

                    tPos.Dispose(); tGrad.Dispose(); tMat.Dispose(); tIdx.Dispose();
                    output.Dispose();
                }
            }
        }

        [ContextMenu("Clear")]
        public void Clear()
        {
            for (int i = 0; i < spawned.Count; i++)
                if (spawned[i] != null) DestroyImmediate(spawned[i]);
            spawned.Clear();
        }

        private void OnEnable() => Generate();
        private void OnDisable() => Clear();

        private void OnDrawGizmos()
        {
            int3 playerVox = PlayerVoxels();
            for (int k = 0; k < lodCount; k++)
            {
                int chunkVox = 16 << k;
                int halfVox = chunkVox * RegionChunks;
                Gizmos.color = new Color(1f - k * 0.15f, 0.15f * k, 0.35f, 0.9f);
                Vector3 center = math.float3(playerVox) * VoxelStep;
                Vector3 size = new Vector3(2f * halfVox * VoxelStep, 2f * halfVox * VoxelStep, 2f * halfVox * VoxelStep);
                Gizmos.DrawWireCube(center, size);
            }
        }

        // --- private ---

        /// «Игрок» в метрах (Radius * 1.1, 0, 0) → воксели LOD0 (2 вокселя на метр).
        private int3 PlayerVoxels() => new int3((int)(radius * 1.1f / VoxelStep), 0, 0);

        private void BuildChunkGo(
            int k, int cx, int cy, int cz, int3 origin0,
            TransvoxelMeshOutput output,
            NativeList<float3> tPos, NativeList<int3> tGrad,
            NativeList<byte> tMat, NativeList<uint> tIdx)
        {
            // Ячейка LOD k = 1<<k вокселей LOD0; позиции transition даны в тех же
            // coarse-ячейках (0..16), поэтому масштаб единый.
            int cellVox = 1 << k;
            int vCount = output.VertexCount + tPos.Length;

            var vertices = new Vector3[vCount];
            var normals = new Vector3[vCount];
            var colors = new Color[vCount];

            for (int v = 0; v < output.VertexCount; v++)
            {
                float3 p = output.Positions[v];
                vertices[v] = (origin0 + p * cellVox) * VoxelStep;
                float3 n = output.Normals[v];
                normals[v] = new Vector3(n.x, n.y, n.z);
                colors[v] = useVertexColors ? Palette(output.Materials[v]) : Color.white;
            }
            for (int v = 0; v < tPos.Length; v++)
            {
                float3 p = tPos[v];
                vertices[output.VertexCount + v] = (origin0 + p * cellVox) * VoxelStep;
                float3 n = TransvoxelMesher.NormalizeGradient(tGrad[v]);
                normals[output.VertexCount + v] = new Vector3(n.x, n.y, n.z);
                colors[output.VertexCount + v] = useVertexColors ? Palette(tMat[v]) : Color.white;
            }

            var triangles = new int[output.Indices.Length + tIdx.Length];
            for (int i = 0; i < output.Indices.Length; i++)
                triangles[i] = (int)output.Indices[i];
            // MeshTransitionInto продолжает индексы от tPos.Length внутри своих списков;
            // сшивку с regular-вершинами даём оффсетом здесь.
            for (int i = 0; i < tIdx.Length; i++)
                triangles[output.Indices.Length + i] = (int)tIdx[i] + output.VertexCount;

            var go = new GameObject($"LodCascade_L{k}_{cx}_{cy}_{cz}");
            go.transform.SetParent(transform, false);
            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            var mesh = new Mesh { name = go.name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            filter.sharedMesh = mesh;
            renderer.sharedMaterial = cachedMaterial;
            spawned.Add(go);
        }

        /// Дебаг-палитра по VoxelMaterial (продакшн-палитра живёт в VoxelChunkMeshBuilder).
        private static Color Palette(byte mat)
        {
            switch ((VoxelMaterial)mat)
            {
                case VoxelMaterial.Dirt:   return new Color(0.45f, 0.30f, 0.18f);
                case VoxelMaterial.Coal:   return new Color(0.15f, 0.15f, 0.17f);
                case VoxelMaterial.Sand:   return new Color(0.85f, 0.78f, 0.55f);
                case VoxelMaterial.Quartz: return new Color(0.90f, 0.90f, 0.95f);
                default:                   return new Color(0.60f, 0.60f, 0.60f);
            }
        }

        private static int3 FloorDiv(int3 a, int b) => new int3(
            (int)math.floor(a.x / (float)b),
            (int)math.floor(a.y / (float)b),
            (int)math.floor(a.z / (float)b));

        /// Чанк [o, o+size) целиком внутри куба с центром c и половиной half.
        private static bool InsideBox(int3 o, int size, int3 c, int half) =>
            o.x >= c.x - half && o.x + size <= c.x + half &&
            o.y >= c.y - half && o.y + size <= c.y + half &&
            o.z >= c.z - half && o.z + size <= c.z + half;

        /// Чанк [o, o+size) пересекает куб с центром c и половиной half.
        private static bool IntersectsBox(int3 o, int size, int3 c, int half) =>
            o.x < c.x + half && o.x + size > c.x - half &&
            o.y < c.y + half && o.y + size > c.y - half &&
            o.z < c.z + half && o.z + size > c.z - half;

        private static int3 DirVec(TransitionDirection d)
        {
            switch (d)
            {
                case TransitionDirection.XMin: return new int3(-1, 0, 0);
                case TransitionDirection.XMax: return new int3(1, 0, 0);
                case TransitionDirection.YMin: return new int3(0, -1, 0);
                case TransitionDirection.YMax: return new int3(0, 1, 0);
                case TransitionDirection.ZMin: return new int3(0, 0, -1);
                case TransitionDirection.ZMax: return new int3(0, 0, 1);
                default: return int3.zero;
            }
        }
    }
}