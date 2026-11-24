using UnityEngine;
using Unity.Mathematics;
using SmilemanStudio.Core.Voxels;

namespace SmilemanStudio.Presentation.Voxels
{
    /// <summary>
    /// Сборка UnityEngine.Mesh из единой геометрии чанка <see cref="VoxelChunkGeometry"/>
    /// (regular + transition), ADR-0002 D7.
    /// </summary>
    /// <remarks>
    /// Единственное место, где появляется float-позиция: chunk-local coarse-ячейки
    /// (0..16) переводятся в метры: world = (origin0 + pos * 2^lod) * VoxelStep(0).
    /// transition-вершины уже приведены мешером к единой coarse-системе, поэтому scale
    /// единый для всего чанка. Материал вершины → vertex color по палитре.
    /// Индексы uint (чанк может превысить 65k вершин).
    /// </remarks>
    public static class VoxelChunkMeshBuilder
    {
        /// <summary>
        /// Построить Mesh по геометрии одного чанка.
        /// </summary>
        /// <param name="geometry">Геометрия из <see cref="VoxelChunkMesher.BuildChunk"/>.</param>
        /// <param name="chunkOrigin0Voxel">Origin чанка в island-local вокселях LOD0.</param>
        /// <param name="lod">Уровень LOD чанка (scale = 2^lod).</param>
        /// <param name="islandWorldOrigin">Мировая позиция начала координат острова.</param>
        public static Mesh Build(
            in VoxelChunkGeometry geometry,
            int3 chunkOrigin0Voxel,
            int lod,
            Vector3 islandWorldOrigin)
        {
            int vertexCount = geometry.VertexCount;
            float voxelSizeMeters = VoxelLod.VoxelStepMilli(0) * 0.001f; // 0,5 м
            float cellVox = 1 << lod;
            float3 origin = new float3(chunkOrigin0Voxel.x, chunkOrigin0Voxel.y, chunkOrigin0Voxel.z);

            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var colors = new Color[vertexCount];

            for (int i = 0; i < vertexCount; i++)
            {
                float3 world = (origin + geometry.Positions[i] * cellVox) * voxelSizeMeters;
                vertices[i] = new Vector3(
                    world.x + islandWorldOrigin.x,
                    world.y + islandWorldOrigin.y,
                    world.z + islandWorldOrigin.z);

                float3 n = geometry.Normals[i];
                normals[i] = new Vector3(n.x, n.y, n.z);
                colors[i] = MaterialColor(geometry.Materials[i]);
            }

            var triangles = new int[geometry.Indices.Length];
            for (int i = 0; i < triangles.Length; i++)
                triangles[i] = (int)geometry.Indices[i];

            var mesh = new Mesh { name = "VoxelChunk" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Палитра материала вершины в vertex color. None — маджента (брак).</summary>
        static Color MaterialColor(byte material)
        {
            switch ((VoxelMaterial)material)
            {
                case VoxelMaterial.Dirt: return new Color(0.45f, 0.30f, 0.18f);
                case VoxelMaterial.Coal: return new Color(0.14f, 0.14f, 0.16f);
                case VoxelMaterial.Sand: return new Color(0.76f, 0.68f, 0.45f);
                case VoxelMaterial.Quartz: return new Color(0.86f, 0.90f, 0.95f);
                default: return new Color(1f, 0f, 1f);
            }
        }
    }
}
