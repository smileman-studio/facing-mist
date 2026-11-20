using UnityEngine;
using Unity.Mathematics;
using SmilemanStudio.Core.Voxels;

namespace SmilemanStudio.Presentation.Voxels
{
    /// <summary>
    /// Сборка UnityEngine.Mesh из целочисленного вывода <see cref="TransvoxelMesher"/> (P4a).
    /// </summary>
    /// <remarks>
    /// Единственное место, где появляется float-позиция: шаги сетки LOD0 переводятся в
    /// метры (шаг = VoxelLod.VoxelStepMilli(0)/1000 = 0,5 м) и смещаются на origin чанка
    /// и мировую позицию острова. Материал вершины becomes vertex color по палитре.
    /// Индексы — uint: чанк может превысить 65k вершин, поэтому indexFormat UInt32.
    /// </remarks>
    public static class VoxelChunkMeshBuilder
    {
        /// <summary>
        /// Построить Mesh по output'у меширования одного чанка.
        /// </summary>
        /// <param name="output">Результат <see cref="TransvoxelMesher.Mesh"/> (не освобождён).</param>
        /// <param name="chunkOrigin0Voxel">Origin чанка в island-local вокселях LOD0.</param>
        /// <param name="islandWorldOrigin">Мировая позиция начала координат острова.</param>
        public static Mesh Build(
            in TransvoxelMeshOutput output,
            int3 chunkOrigin0Voxel,
            Vector3 islandWorldOrigin)
        {
            int vertexCount = output.VertexCount;
            float voxelSizeMeters = VoxelLod.VoxelStepMilli(0) * 0.001f; // 0,5 м
            float3 origin = new float3(chunkOrigin0Voxel.x, chunkOrigin0Voxel.y, chunkOrigin0Voxel.z);

            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var colors = new Color[vertexCount];

            for (int i = 0; i < vertexCount; i++)
            {
                float3 world = (origin + output.Positions[i]) * voxelSizeMeters;
                vertices[i] = new Vector3(
                    world.x + islandWorldOrigin.x,
                    world.y + islandWorldOrigin.y,
                    world.z + islandWorldOrigin.z);

                float3 n = output.Normals[i];
                normals[i] = new Vector3(n.x, n.y, n.z);
                colors[i] = MaterialColor(output.Materials[i]);
            }

            var triangles = new int[output.Indices.Length];
            for (int i = 0; i < triangles.Length; i++)
                triangles[i] = (int)output.Indices[i];

            var mesh = new Mesh { name = "VoxelChunk LOD0" };
            if (vertexCount > 65000)
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
