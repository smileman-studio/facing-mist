using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using SmilemanStudio.Core;
using SmilemanStudio.Core.Voxels;
using SmilemanStudio.Simulation.Voxels;

namespace SmilemanStudio.Presentation.Voxels
{
    /// <summary>
    /// Показывает один воксельный чанк острова как UnityEngine.Mesh (P4a, первый видимый результат).
    /// </summary>
    /// <remarks>
    /// Мост Simulation -> Presentation: берёт кэш плотности (<see cref="VoxelChunkSampler"/>),
    /// меширует regular cells (<see cref="TransvoxelMesher"/>, главный поток — для одного чанка
    /// этого достаточно, Burst-джоба <see cref="MeshVoxelChunkJob"/> подключится в P5 для
    /// пакетной сборки) и отдаёт меш в <see cref="MeshFilter"/>.
    ///
    /// Материал — vertex-color: берётся встроенный unlit "Sprites/Default" (читает COLOR,
    /// Cull Off, не требует URP-ассетов) — гарантирует видимость без кастомного шейдера.
    /// Освещение на нём не работает; lit-шейдер с vertex color — задача P5.
    /// </remarks>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class VoxelChunkView : MonoBehaviour
    {
        [Tooltip("IslandId.Value острова (0 — невалидно).")]
        public int IslandValue = 1;

        [Tooltip("Радиус острова, м.")]
        public int Radius = 200;

        public int ChunkX;
        public int ChunkY;
        public int ChunkZ;

        [Tooltip("Уровень LOD (P4a: только 0).")]
        public int Lod = 0;

        [Tooltip("Мировая позиция начала координат острова.")]
        public Vector3 IslandWorldOrigin = Vector3.zero;

        MeshFilter _filter;
        MeshRenderer _renderer;

        void Awake()
        {
            _filter = GetComponent<MeshFilter>();
            _renderer = GetComponent<MeshRenderer>();
            EnsureVertexColorMaterial();
        }

        void Start()
        {
            Build();
        }

        /// <summary>Пересобрать меш текущего чанка из процедурного острова.</summary>
        public void Build()
        {
            if (_filter == null) _filter = GetComponent<MeshFilter>();
            if (_renderer == null) _renderer = GetComponent<MeshRenderer>();
            EnsureVertexColorMaterial();

            long chunkKey = VoxelChunkCoord.Pack(ChunkX, ChunkY, ChunkZ, Lod);
            var islandId = new IslandId(IslandValue);

            using var density = VoxelChunkSampler.Sample(chunkKey, islandId, Radius, Allocator.Persistent);
            var output = TransvoxelMesher.Mesh(density, islandId, Allocator.Persistent);

            int3 origin0 = VoxelChunkSampler.ChunkOrigin0(chunkKey);
            Mesh mesh = VoxelChunkMeshBuilder.Build(in output, origin0, IslandWorldOrigin);
            output.Dispose();

            Mesh previous = _filter.sharedMesh;
            _filter.sharedMesh = mesh;
            if (previous != null) Destroy(previous);
        }

        void EnsureVertexColorMaterial()
        {
            if (_renderer == null || _renderer.sharedMaterial != null) return;
            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null)
                _renderer.sharedMaterial = new Material(shader);
        }
    }
}
