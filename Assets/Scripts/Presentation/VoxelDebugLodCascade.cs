using System.Collections.Generic;
using SmilemanStudio.Core.Voxels;
using SmilemanStudio.Presentation.Voxels;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace SmilemanStudio.Presentation
{
    /// <summary>
    /// Отладочный LOD-каскад: процедурная сфера мешируется через автономный воксельный
    /// движок (ADR-0002, D7).
    ///
    /// «Игрок» в позиции (radius * 1.1, 0, 0) метров. Воксельный движок сам строит
    /// clipmap-каскад: VoxelLodPlanner → VoxelChunkRequest[] (LOD + seam-маска для
    /// каждого чанка) → VoxelChunkMesher → VoxelChunkGeometry (regular + transition).
    ///
    /// Класс НЕ знает, что на строит (сфера) и КАК считает LOD (только bounds+player+field).
    /// Это демонстрация автономности воксельного движка.
    /// </summary>
    [ExecuteAlways]
    public class VoxelDebugLodCascade : MonoBehaviour
    {
        [SerializeField] private int radius = 200;
        [SerializeField] [Range(1, 9)] private int lodCount = 6;
        [SerializeField] private bool useVertexColors = true;
        [SerializeField] private Material overrideMaterial;

        private readonly List<GameObject> spawned = new List<GameObject>();
        private VoxelChunkGeometry geometry = new VoxelChunkGeometry();
        private Material cachedMaterial;

        // Scratch-буферы (переиспользуются между чанками — zero allocation).
        private NativeArray<short> densityScratch;
        private NativeArray<byte> nodeMatScratch;

        /// Метров на воксель LOD0 (шаг 0.5 м).
        private const float VoxelStep = 0.5f;

        private static readonly Color[] LodPalette =
        {
            new Color(1.00f, 0.20f, 0.20f), new Color(1.00f, 0.60f, 0.15f),
            new Color(0.95f, 0.90f, 0.20f), new Color(0.25f, 0.85f, 0.30f),
            new Color(0.20f, 0.75f, 0.95f), new Color(0.30f, 0.40f, 0.95f),
            new Color(0.75f, 0.35f, 0.90f), new Color(0.95f, 0.45f, 0.75f),
            new Color(0.80f, 0.80f, 0.80f),
        };

        [ContextMenu("Generate")]
        public void Generate()
        {
            Clear();

            cachedMaterial = overrideMaterial != null
                ? overrideMaterial
                : new Material(Shader.Find("Sprites/Default"));
            if (cachedMaterial == null)
                cachedMaterial = new Material(Shader.Find("Standard"));

            // --- Setup: поля и bounds ---
            int radiusVox = radius * VoxelCoord.VoxelsPerMeter(); // R * 2 вокселя
            int3 centerVox = int3.zero;

            var sphereField = new SphereDensityField(centerVox, radiusVox);
            var nullMat = new NullMaterialField();

            // Bounds сферы в вокселях LOD0: AABB.
            int3 boundsMin = centerVox - radiusVox;
            int3 boundsMax = centerVox + radiusVox;

            // Позиция игрока: (radius * 1.1, 0, 0) метров → воксели LOD0.
            int3 playerVox = new int3(radius * 11 / 5, 0, 0);

            // lodMax = lodCount - 1.
            int lodMax = lodCount - 1;

            // Scratch-буферы (один раз на всю генерацию).
            if (!densityScratch.IsCreated)
                densityScratch = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
            if (!nodeMatScratch.IsCreated)
                nodeMatScratch = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);

            // Geometry: выделить один раз.
            if (!geometry.Positions.IsCreated)
                geometry.Allocate(Allocator.Persistent);
            geometry.Reset();

            // --- 1. Планирование: движок сам решает, какие чанки мешировать ---
            var requests = new NativeList<VoxelChunkRequest>(Allocator.Persistent);
            VoxelLodPlanner.Plan(boundsMin, boundsMax, playerVox, lodMax, requests);

            // --- 2. Меширование каждого чанка через движок ---
            geometry.Reset();
            foreach (var req in requests)
            {
                // BuildChunk: field + bounds + player → planner → mesher.
                // Движок сам заполняет буферы, считает regular + transition.
                geometry.Reset();
                VoxelChunkMesher.BuildChunk(
                    sphereField, nullMat, req, ref geometry,
                    densityScratch, nodeMatScratch);

                if (geometry.VertexCount == 0)
                    continue;

                BuildChunkGo(req, geometry, lodMax);
            }

            requests.Dispose();
        }

        [ContextMenu("Clear")]
        public void Clear()
        {
            for (int i = 0; i < spawned.Count; i++)
            {
                if (spawned[i] != null)
                {
                    if (Application.isPlaying)
                        Destroy(spawned[i]);
                    else
                        DestroyImmediate(spawned[i]);
                }
            }
            spawned.Clear();
        }

        private void OnEnable() => Generate();
        private void OnDisable() => Clear();
        private void OnDestroy() { Clear(); densityScratch.Dispose(); nodeMatScratch.Dispose(); geometry.Dispose(); }

        private void OnDrawGizmos()
        {
            int radiusVox = radius * VoxelCoord.VoxelsPerMeter();
            int3 playerVox = new int3(radius * 11 / 5, 0, 0);
            int lodMax = lodCount - 1;

            for (int j = 0; j < lodCount; j++)
            {
                int chunkVox = 16 << j;
                int half = chunkVox * VoxelLodPlanner.RegionHalfChunks;
                int3 center = VoxelLodPlanner.CommonCenter(playerVox, lodMax);

                Gizmos.color = j < LodPalette.Length
                    ? new Color(LodPalette[j].r, LodPalette[j].g, LodPalette[j].b, 0.9f)
                    : new Color(0.8f, 0.8f, 0.8f, 0.9f);
                Vector3 centerM = math.float3(center) * VoxelStep;
                Vector3 size = new Vector3(2f * half * VoxelStep, 2f * half * VoxelStep, 2f * half * VoxelStep);
                Gizmos.DrawWireCube(centerM, size);
            }
        }

        // --- Построение GameObject из VoxelChunkGeometry ---

        private void BuildChunkGo(in VoxelChunkRequest req, in VoxelChunkGeometry geo, int lodMax)
        {
            int lod = req.Lod;
            int cellVox = 1 << lod; // 2^lod — вокселей LOD0 на coarse-ячейку уровня lod
            int vCount = geo.VertexCount;

            var vertices = new Vector3[vCount];
            var normals = new Vector3[vCount];
            var colors = new Color[vCount];

            for (int v = 0; v < vCount; v++)
            {
                // chunk-local coarse-cell → world: (origin0 + pos * cellVox) * VoxelStep
                float3 p = geo.Positions[v];
                vertices[v] = (req.Origin0 + p * cellVox) * VoxelStep;

                normals[v] = new Vector3(geo.Normals[v].x, geo.Normals[v].y, geo.Normals[v].z);
                colors[v] = useVertexColors ? LodColor(lod, lodMax) : Color.white;
            }

            var triangles = new int[geo.Indices.Length];
            for (int i = 0; i < triangles.Length; i++)
                triangles[i] = (int)geo.Indices[i];

            var go = new GameObject($"LodCascade_L{lod}_{req.ChunkCoord.x}_{req.ChunkCoord.y}_{req.ChunkCoord.z}");
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

        private static Color LodColor(int lod, int lodMax)
        {
            // Раскраска по LOD: соседние уровни различимы.
            float t = lod / (float)math.max(lodMax, 1);
            return Color.Lerp(new Color(1f, 0.2f, 0.2f), new Color(0.2f, 0.75f, 0.95f), t);
        }
    }
}
