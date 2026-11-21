using SmilemanStudio.Core;
using SmilemanStudio.Core.Voxels;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace SmilemanStudio.Presentation
{
    /// <summary>
    /// Отладочный MonoBehaviour для визуализации воксельного меша в редакторе/игре.
    /// Генерирует сферу, разбитую на сетку чанков (по умолчанию 4x4x4),
    /// чтобы проверить корректность мешинга и генерации на швах.
    /// Transform этого объекта является центром сферы.
    /// </summary>
    public class VoxelDebugSphere : MonoBehaviour
    {
        [Header("Grid Settings")]
        [Tooltip("Количество чанков по каждой оси (X, Y, Z).")]
        public int3 ChunkGridSize = new int3(4, 4, 4);

        [Header("Sphere Settings")]
        [Tooltip("Радиус сферы в метрах. Диаметр 8м означает радиус 4м. Шаг сетки 0.5м.")]
        public float SphereRadiusMeters = 10f;

        [Header("Rendering")]
        public Material MeshMaterial;
        [Tooltip("Раскрасить вершины цветами в зависимости от материала (Dirt, Coal, Sand, Quartz).")]
        public bool UseVertexColors = true;

        private GameObject _chunksRoot;
        private const float VOXEL_STEP = 0.5f;

        private void Start()
        {
            Generate();
        }

        [ContextMenu("Regenerate Sphere")]
        public void Generate()
        {
            Clear();

            _chunksRoot = new GameObject("VoxelSphere_Chunks");
            _chunksRoot.transform.SetParent(transform, false);

            // Переводим метры в воксели (1 воксель = 0.5 метра)
            int radiusVoxels = Mathf.RoundToInt(SphereRadiusMeters / VOXEL_STEP);
            float3 sphereCenterVoxels = float3.zero; 

            // Валидный ID острова (0 = invalid по правилам ADR/AGENTS)
            IslandId islandId = new IslandId(1); 
            
            int chunkVoxelSize = 1 << VoxelConstants.ChunkSizeLog2; // 16 вокселей

            // Центрируем сетку чанков относительно центра (0,0,0)
            int3 minChunk = -(ChunkGridSize / 2);

            for (int cx = 0; cx < ChunkGridSize.x; cx++)
            {
                for (int cy = 0; cy < ChunkGridSize.y; cy++)
                {
                    for (int cz = 0; cz < ChunkGridSize.z; cz++)
                    {
                        int3 chunkCoord = minChunk + new int3(cx, cy, cz);
                        int3 chunkOriginVoxels = chunkCoord * chunkVoxelSize;

                        // 1. Генерация кэша плотности 19^3 (17 интерьер + apron по 1 с каждой стороны)
                        var density = new NativeArray<short>(TransvoxelMesher.CacheVolume, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                        
                        for (int i = 0; i < VoxelConstants.CacheSize; i++)
                        {
                            for (int j = 0; j < VoxelConstants.CacheSize; j++)
                            {
                                for (int k = 0; k < VoxelConstants.CacheSize; k++)
                                {
                                    // Индексы кэша 1..17 маппятся в воксели чанка 0..16.
                                    // Индексы 0 и 18 — apron (воксели -1 и 17).
                                    int3 voxelCoord = chunkOriginVoxels + new int3(i - 1, j - 1, k - 1);
                                    
                                    // SDF сферы: distance_to_center - radius. 
                                    // d <= 0 означает грунт, d > 0 — воздух.
                                    float3 diff = new float3(voxelCoord.x, voxelCoord.y, voxelCoord.z) - sphereCenterVoxels;
                                    float sdf = math.length(diff) - radiusVoxels;

                                    int idx = (i * VoxelConstants.CacheSize + j) * VoxelConstants.CacheSize + k;
                                    density[idx] = VoxelDensity.Quantize(sdf);
                                }
                            }
                        }

                        // 2. Меширование (Transvoxel)
                        var meshOutput = TransvoxelMesher.Mesh(density, islandId, Allocator.Temp);
                        density.Dispose();

                        if (meshOutput.VertexCount == 0)
                        {
                            meshOutput.Dispose();
                            continue;
                        }

                        // 3. Создание GameObject для чанка
                        var chunkGo = new GameObject($"Chunk_{chunkCoord.x}_{chunkCoord.y}_{chunkCoord.z}");
                        chunkGo.transform.SetParent(_chunksRoot.transform, false);
                        // Позиция чанка в метрах относительно центра
                        chunkGo.transform.localPosition = new Vector3(chunkOriginVoxels.x, chunkOriginVoxels.y, chunkOriginVoxels.z) * VOXEL_STEP;

                        var mf = chunkGo.AddComponent<MeshFilter>();
                        var mr = chunkGo.AddComponent<MeshRenderer>();
                        
                        if (MeshMaterial == null)
                        {
                            MeshMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit")); 
                            if (MeshMaterial == null) MeshMaterial = new Material(Shader.Find("Standard"));
                        }
                        mr.material = MeshMaterial;

                        var mesh = new Mesh();
                        mesh.name = $"Mesh_{chunkCoord}";
                        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

                        var vertices = new Vector3[meshOutput.VertexCount];
                        var normals = new Vector3[meshOutput.VertexCount];
                        var colors = new Color[meshOutput.VertexCount];
                        
                        for (int v = 0; v < meshOutput.VertexCount; v++)
                        {
                            // Позиции вершин локальны относительно чанка (в шагах сетки)
                            float3 p = meshOutput.Positions[v];
                            vertices[v] = new Vector3(p.x, p.y, p.z) * VOXEL_STEP;
                            
                            float3 n = meshOutput.Normals[v];
                            normals[v] = new Vector3(n.x, n.y, n.z);
                            
                            if (UseVertexColors)
                            {
                                colors[v] = GetMaterialDebugColor((VoxelMaterial)meshOutput.Materials[v]);
                            }
                        }

                        var triangles = new int[meshOutput.Indices.Length];
                        for (int i = 0; i < meshOutput.Indices.Length; i++)
                        {
                            // Winding уже согласован с нормалями (наружу) после
                            // фикса P4a в TransvoxelMesher.MeshInto — инверсия не нужна.
                            triangles[i] = (int)meshOutput.Indices[i];
                        }

                        mesh.vertices = vertices;
                        mesh.normals = normals;
                        mesh.triangles = triangles;
                        
                        if (UseVertexColors)
                        {
                            mesh.colors = colors;
                        }
                        
                        mf.mesh = mesh;
                        meshOutput.Dispose();
                    }
                }
            }
        }

        private Color GetMaterialDebugColor(VoxelMaterial mat)
        {
            switch (mat)
            {
                case VoxelMaterial.Dirt:   return new Color(0.55f, 0.27f, 0.07f); 
                case VoxelMaterial.Coal:   return new Color(0.1f, 0.1f, 0.1f);    
                case VoxelMaterial.Sand:   return new Color(0.93f, 0.84f, 0.56f); 
                case VoxelMaterial.Quartz: return new Color(0.9f, 0.9f, 1.0f);    
                default:                   return Color.magenta; // VoxelMaterial.None
            }
        }

        public void Clear()
        {
            if (_chunksRoot != null)
            {
                if (Application.isPlaying)
                    Destroy(_chunksRoot);
                else
                    DestroyImmediate(_chunksRoot);
                _chunksRoot = null;
            }
        }

        private void OnDestroy()
        {
            Clear();
        }
    }
}