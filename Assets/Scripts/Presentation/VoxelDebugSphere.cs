using SmilemanStudio.Core.Voxels;
using SmilemanStudio.Presentation.Voxels;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace SmilemanStudio.Presentation
{
    /// <summary>
    /// Отладочная воксельная сфера через автономный движок (ADR-0002, D7).
    /// </summary>
    /// <remarks>
    /// Демонстрирует минимальную форму без островов: вызывающий даёт только
    /// <see cref="SphereDensityField"/> + AABB сферы + позицию игрока. LOD и швы
    /// считает движок. По умолчанию lodMax = 0 (один уровень) — быстрый контроль
    /// корректности мешинга; при lodMax &gt; 0 показывает LOD-каскад на сфере.
    /// Transform этого объекта — центр сферы.
    /// </remarks>
    public class VoxelDebugSphere : MonoBehaviour
    {
        [Header("Sphere Settings")]
        [Tooltip("Радиус сферы, м.")]
        public float SphereRadiusMeters = 10f;

        [Header("Engine Settings")]
        [Tooltip("Максимальный LOD (0 — один уровень).")]
        [Range(0, 9)] public int LodMax = 0;

        [Header("Rendering")]
        public Material MeshMaterial;
        [Tooltip("Раскрасить вершины по материалу (для сферы материал None → маджента).")]
        public bool UseVertexColors = false;

        GameObject _chunksRoot;
        VoxelChunkGeometry geometry = new VoxelChunkGeometry();
        NativeArray<short> densityScratch;
        NativeArray<byte> nodeMatScratch;

        const float VoxelStep = 0.5f;

        void Start() => Generate();

        [ContextMenu("Generate")]
        public void Generate()
        {
            Clear();

            int radiusVox = math.max(1, (int)math.round(SphereRadiusMeters / VoxelStep));
            int3 centerVox = int3.zero;

            var sphereField = new SphereDensityField(centerVox, radiusVox);
            var nullMat = new NullMaterialField();

            int3 boundsMin = centerVox - radiusVox;
            int3 boundsMax = centerVox + radiusVox;

            // Игрок в центре → каскад вокруг сферы.
            int3 playerVox = centerVox;

            _chunksRoot = new GameObject("VoxelSphere_Chunks");
            _chunksRoot.transform.SetParent(transform, false);

            if (!densityScratch.IsCreated)
                densityScratch = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
            if (!nodeMatScratch.IsCreated)
                nodeMatScratch = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);
            if (!geometry.Positions.IsCreated)
                geometry.Allocate(Allocator.Persistent);

            var requests = new NativeList<VoxelChunkRequest>(Allocator.Persistent);
            VoxelLodPlanner.Plan(boundsMin, boundsMax, playerVox, LodMax, requests);

            foreach (var req in requests)
            {
                geometry.Reset();
                VoxelChunkMesher.BuildChunk(sphereField, nullMat, req, ref geometry, densityScratch, nodeMatScratch);
                if (geometry.VertexCount == 0)
                    continue;
                BuildChunkGo(req);
            }

            requests.Dispose();
        }

        void BuildChunkGo(in VoxelChunkRequest req)
        {
            int cellVox = 1 << req.Lod;
            int vCount = geometry.VertexCount;

            var vertices = new Vector3[vCount];
            var normals = new Vector3[vCount];
            var colors = new Color[vCount];

            for (int v = 0; v < vCount; v++)
            {
                float3 p = geometry.Positions[v];
                vertices[v] = (req.Origin0 + p * cellVox) * VoxelStep;
                float3 n = geometry.Normals[v];
                normals[v] = new Vector3(n.x, n.y, n.z);
                colors[v] = UseVertexColors ? Color.magenta : Color.white;
            }

            var triangles = new int[geometry.Indices.Length];
            for (int i = 0; i < triangles.Length; i++)
                triangles[i] = (int)geometry.Indices[i];

            var go = new GameObject($"Chunk_L{req.Lod}_{req.ChunkCoord.x}_{req.ChunkCoord.y}_{req.ChunkCoord.z}");
            go.transform.SetParent(_chunksRoot.transform, false);

            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            if (MeshMaterial == null)
            {
                MeshMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                if (MeshMaterial == null) MeshMaterial = new Material(Shader.Find("Standard"));
            }
            mr.sharedMaterial = MeshMaterial;

            var mesh = new Mesh { name = go.name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.normals = normals;
            if (UseVertexColors) mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
        }

        [ContextMenu("Clear")]
        public void Clear()
        {
            if (_chunksRoot != null)
            {
                if (Application.isPlaying) Destroy(_chunksRoot);
                else DestroyImmediate(_chunksRoot);
                _chunksRoot = null;
            }
        }

        void OnDestroy()
        {
            Clear();
            if (densityScratch.IsCreated) densityScratch.Dispose();
            if (nodeMatScratch.IsCreated) nodeMatScratch.Dispose();
            geometry.Dispose();
        }
    }
}
