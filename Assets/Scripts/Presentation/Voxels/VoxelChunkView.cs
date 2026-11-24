using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using SmilemanStudio.Core;
using SmilemanStudio.Core.Voxels;

namespace SmilemanStudio.Presentation.Voxels
{
    /// <summary>
    /// Показывает один воксельный чанк острова как UnityEngine.Mesh (ADR-0002, D7).
    /// </summary>
    /// <remarks>
    /// Мост к автономному воксельному движку: остров задаётся только как источник
    /// плотности/материала (<see cref="IslandDensityField"/> + <see cref="IslandMaterialAdapter"/>)
    /// и заявка чанка (<see cref="VoxelChunkRequest"/>). Меширование — целиком
    /// <see cref="VoxelChunkMesher"/>: он заполняет буферы, строит regular (+ transition при
    /// SeamFaceMask) и отдаёт единую геометрию. View ничего не знает о SDF и материале.
    ///
    /// Материал — vertex-color: встроенный unlit "Sprites/Default" (читает COLOR, Cull Off).
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

        [Tooltip("Уровень LOD (0 по умолчанию).")]
        public int Lod = 0;

        [Tooltip("Seam-маска transition-граней (0 — обычный чанк без LOD-шва).")]
        public int SeamFaceMask = 0;

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

        void Start() => Build();

        /// <summary>Пересобрать меш текущего чанка из процедурного острова.</summary>
        public void Build()
        {
            if (_filter == null) _filter = GetComponent<MeshFilter>();
            if (_renderer == null) _renderer = GetComponent<MeshRenderer>();
            EnsureVertexColorMaterial();

            var islandId = new IslandId(IslandValue);
            var densityField = new IslandDensityField(islandId, Radius);
            var materialField = new IslandMaterialAdapter(islandId);

            var coord = new int3(ChunkX, ChunkY, ChunkZ);
            var req = new VoxelChunkRequest
            {
                ChunkCoord = coord,
                Lod = (byte)Lod,
                Origin0 = VoxelFieldSampler.ChunkOrigin0(coord, Lod),
                SeamFaceMask = SeamFaceMask,
            };

            var geometry = new VoxelChunkGeometry();
            geometry.Allocate(Allocator.Persistent);
            var density = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
            var nodeMat = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);

            VoxelChunkMesher.BuildChunk(densityField, materialField, req, ref geometry, density, nodeMat);

            Mesh mesh = VoxelChunkMeshBuilder.Build(geometry, req.Origin0, Lod, IslandWorldOrigin);

            density.Dispose();
            nodeMat.Dispose();
            geometry.Dispose();

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
