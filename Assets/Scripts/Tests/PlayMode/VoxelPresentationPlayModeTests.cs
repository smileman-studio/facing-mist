using System.Collections;
using NUnit.Framework;
using SmilemanStudio.Core;
using SmilemanStudio.Core.Voxels;
using SmilemanStudio.Presentation.Voxels;
using SmilemanStudio.Simulation.Voxels;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace SmilemanStudio.Tests.PlayMode
{
    /// <summary>
    /// PlayMode-тест P4a: Presentation-мост (VoxelChunkView + VoxelChunkMeshBuilder)
    /// собирает UnityEngine.Mesh из процедурного острова и рендерит его.
    /// </summary>
    /// <remarks>
    /// Проверки:
    ///   1. VoxelChunkMeshBuilder — чистая сборка: вершины/нормали/цвета в
    ///      ожидаемых пределах, нормали единичные, индексы валидные.
    ///   2. VoxelChunkView в сцене — mesh с треугольниками, MeshRenderer
    ///      с vertex-color материалом, камера видит треугольники (ScreenSpaceOverdraw).
    /// </remarks>
    public class VoxelPresentationPlayModeTests
    {
        const int IslandValue = 1;
        const int Radius = 200;

        // --- 1. Сборка меша (без сцены) ---

        [Test]
        public void MeshBuilder_BuildsValidMeshFromRealIslandChunk()
        {
            long key = FindSurfaceChunkKey();
            int3 origin0 = VoxelFieldSampler.ChunkOrigin0(key);
            int lod = VoxelChunkCoord.UnpackLod(key);

            var densityField = new IslandDensityField(new IslandId(IslandValue), Radius);
            var materialField = new IslandMaterialAdapter(new IslandId(IslandValue));
            var req = new VoxelChunkRequest
            {
                ChunkCoord = new int3(
                    VoxelChunkCoord.UnpackX(key), VoxelChunkCoord.UnpackY(key), VoxelChunkCoord.UnpackZ(key)),
                Lod = (byte)lod,
                Origin0 = origin0,
                SeamFaceMask = 0,
            };

            var geometry = new VoxelChunkGeometry();
            geometry.Allocate(Allocator.Persistent);
            var density = new NativeArray<short>(VoxelFieldSampler.CacheVolume, Allocator.Persistent);
            var nodeMat = new NativeArray<byte>(VoxelFieldSampler.NodeMaterialVolume, Allocator.Persistent);

            VoxelChunkMesher.BuildChunk(densityField, materialField, req, ref geometry, density, nodeMat);

            try
            {
                Mesh mesh = VoxelChunkMeshBuilder.Build(geometry, origin0, lod, Vector3.zero);

                Assert.IsNotNull(mesh);
                Assert.Greater(mesh.vertexCount, 0, "mesh must have vertices");
                Assert.Greater(mesh.triangles.Length, 0, "mesh must have triangles");
                Assert.AreEqual(mesh.vertexCount, mesh.colors.Length, "one color per vertex");

                Vector3[] vertices = mesh.vertices;
                Vector3[] normals = mesh.normals;
                for (int i = 0; i < vertices.Length; i++)
                {
                    float3 v = new float3(vertices[i].x, vertices[i].y, vertices[i].z);
                    Assert.IsTrue(math.isfinite(v.x) && math.isfinite(v.y) && math.isfinite(v.z),
                        "vertex must be finite");
                    Assert.AreEqual(1f, normals[i].magnitude, 1e-3f, "normal must be unit");
                }

                // Индексы в пределах вершин.
                int[] tris = mesh.triangles;
                for (int i = 0; i < tris.Length; i++)
                    Assert.Less(tris[i], vertices.Length, "triangle index out of range");

                Object.DestroyImmediate(mesh);
            }
            finally
            {
                density.Dispose();
                nodeMat.Dispose();
                geometry.Dispose();
            }
        }

        // --- 2. VoxelChunkView в сцене: рендер ---

        [UnityTest]
        public IEnumerator VoxelChunkView_RendersMeshVisibleToCamera()
        {
            // Камера над центром острова, смотрит вниз-вбок.
            var camGo = new GameObject("Test Camera");
            var cam = camGo.AddComponent<Camera>();
            camGo.transform.position = new Vector3(80f, 120f, -160f);
            camGo.transform.LookAt(Vector3.zero);

            // Чанк, пересекающий поверхность (тот же поиск, что в EditMode).
            long key = FindSurfaceChunkKey();
            int chunkX = VoxelChunkCoord.UnpackX(key);
            int chunkY = VoxelChunkCoord.UnpackY(key);
            int chunkZ = VoxelChunkCoord.UnpackZ(key);

            var viewGo = new GameObject("Voxel Chunk");
            var view = viewGo.AddComponent<VoxelChunkView>();
            view.IslandValue = IslandValue;
            view.Radius = Radius;
            view.ChunkX = chunkX;
            view.ChunkY = chunkY;
            view.ChunkZ = chunkZ;
            view.Lod = 0;
            view.IslandWorldOrigin = Vector3.zero;
            view.Build();

            var filter = viewGo.GetComponent<MeshFilter>();
            var renderer = viewGo.GetComponent<MeshRenderer>();

            Assert.IsNotNull(filter.sharedMesh, "mesh must be assigned");
            Assert.Greater(filter.sharedMesh.triangles.Length, 0, "mesh must have triangles");
            Assert.IsNotNull(renderer.sharedMaterial, "renderer must have a material");
            Assert.AreEqual(Shader.Find("Sprites/Default"), renderer.sharedMaterial.shader,
                "material must be the unlit vertex-color shader");

            // Рендер в RenderTexture: кадр должен отличаться от цвета очистки —
            // значит, треугольники чанка попали в кадр камеры.
            cam.clearFlags = CameraClearFlags.SolidColor;
            // Чистый зелёный не входит в палитру VoxelChunkMeshBuilder.MaterialColor
            // (Dirt/Coal/Sand/Quartz/None), поэтому любой попавший в кадр пиксель
            // — это треугольник чанка.
            cam.backgroundColor = new Color(0f, 1f, 0f);
            var rt = RenderTexture.GetTemporary(256, 256, 16);
            cam.targetTexture = rt;

            yield return null; // один кадр рендера

            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;

            Color[] pixels = tex.GetPixels();
            int hit = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color p = pixels[i];
                if (Mathf.Abs(p.r - 1f) > 0.05f || Mathf.Abs(p.g - 0f) > 0.05f || Mathf.Abs(p.b - 1f) > 0.05f)
                {
                    hit++;
                }
            }

            RenderTexture.ReleaseTemporary(rt);
            cam.targetTexture = null;

            Assert.Greater(hit, 0, "камера должна видеть треугольники чанка (пиксели != цвета очистки)");
        }

        // --- helpers ---

        // Находим чанк LOD0, пересекающий поверхность острова: идём вниз по -Y
        // от воздуха, ловим смену знака SDF, берём чанк этой точки.
        static long FindSurfaceChunkKey()
        {
            var id = new IslandId(IslandValue);
            int[] probes = { 8, 24, 40, -16, -32 };
            foreach (int px in probes)
            {
                foreach (int pz in probes)
                {
                    int prev = IslandSdf.SdfVoxel(new Unity.Mathematics.int3(px, 128, pz), id, Radius);
                    for (int y = 128; y >= -600; y -= 4)
                    {
                        int cur = IslandSdf.SdfVoxel(new Unity.Mathematics.int3(px, y, pz), id, Radius);
                        if ((cur < 0) != (prev < 0))
                        {
                            return VoxelChunkCoord.Pack(px >> 4, y >> 4, pz >> 4, 0);
                        }
                        prev = cur;
                    }
                }
            }

            Assert.Fail("surface chunk not found for island " + IslandValue + " R=" + Radius);
            return 0;
        }
    }
}
