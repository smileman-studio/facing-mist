using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace Voxel
{
    // [BurstCompile]
    public struct AllInOneChunkDataJob : IJob
    {
        public int ChunkSize;
        public int3 ChunkMin;
        public int LOD;
        public MeshNativeFullDataContainer MeshData;
        public DensityGenerator DensityGenerator;
        public MaterialGenerator MaterialGenerator;
        [DeallocateOnJobCompletion]
        public NativeArray<float> DensityData;
        public BlobAssetReference<TransvoxelTablesBlobAsset> TablesRef;
        public Mesh.MeshDataArray MeshArray;
        public NativeReference<Bounds> MeshBounds;
        
        private const float TRANSITION_CELL_WIDTH_PERCENTAGE = 0.5f;

        private float3 min;
        private float3 max;

        private enum TransitionDirection
        {
            XMin,
            YMin,
            ZMin,
            XMax,
            YMax,
            ZMax,
        }

        public void Execute()
        {
            int densitySize = ChunkSize + 3;
            int LODScale = 1 << LOD;
            int3 startSamplePos = ChunkMin - new int3(1, 1, 1) * LODScale;
            float minDensity = float.MaxValue;
            float maxDensity = float.MinValue;
            for (int x = 0; x < densitySize; x++)
            for (int y = 0; y < densitySize; y++)
            for (int z = 0; z < densitySize; z++)
            {
                int index = x * densitySize * densitySize + y * densitySize + z;
                
                float density = DensityGenerator.GetValue(startSamplePos.x + x * LODScale,
                    startSamplePos.y + y * LODScale, startSamplePos.z + z * LODScale);
                DensityData[index] = density;
                minDensity = math.min(minDensity, density);
                maxDensity = math.max(maxDensity, density);
            }

            if ((minDensity < 0 & maxDensity < 0) || (minDensity > 0 & maxDensity > 0))
            {
                return;
            }
            
            min = new float3(float.MaxValue);
            max = new float3(float.MinValue);
            
            PoligoniseCenter();
            PoligoniseTransitions();
            GenerateMaterials();
            CreateMeshDataArray();
            MeshBounds.Value = new Bounds
            {
                center = (max + min) * 0.5f,
                size = max - min
            };
        }

        private void PoligoniseCenter()
        {
            int padding = 1;

            int LODScale = 1 << LOD;

            ref TransvoxelTablesBlobAsset tables = ref TablesRef.Value;

            var _currentCache = new NativeArray<int>(ChunkSize * ChunkSize * 4, Allocator.Temp,
                NativeArrayOptions.UninitializedMemory);
            var _previousCache = new NativeArray<int>(ChunkSize * ChunkSize * 4, Allocator.Temp,
                NativeArrayOptions.UninitializedMemory);
            var _vertexIndices = new NativeArray<int>(16, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var _cellValues = new NativeArray<float>(8, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            var vertexData = MeshData.GetMainMeshData().VertexData;
            var indices = MeshData.GetMainMeshData().Indices;
            var secondaryVerts = MeshData.GetMainMeshData().SecondaryVertices;

            for (int y = 0; y < ChunkSize; y++)
            {
                for (int z = 0; z < ChunkSize; z++)
                {
                    for (int x = 0; x < ChunkSize; x++)
                    {
                        var cellPos = new int3(x, y, z);

                        for (int i = 0; i < 8; ++i)
                        {
                            int3 voxelPosition = cellPos + new int3(padding, padding, padding) +
                                                 tables.RegularCornerOffset[i];
                            _cellValues[i] = GetDensityValue(voxelPosition.x, voxelPosition.y, voxelPosition.z);
                        }

                        int caseCode = ((_cellValues[0] < 0 ? 0x01 : 0)
                                        | (_cellValues[1] < 0 ? 0x02 : 0)
                                        | (_cellValues[2] < 0 ? 0x04 : 0)
                                        | (_cellValues[3] < 0 ? 0x08 : 0)
                                        | (_cellValues[4] < 0 ? 0x10 : 0)
                                        | (_cellValues[5] < 0 ? 0x20 : 0)
                                        | (_cellValues[6] < 0 ? 0x40 : 0)
                                        | (_cellValues[7] < 0 ? 0x80 : 0));

                        _currentCache[x * ChunkSize * 4 + z * 4] = -1;

                        if (caseCode == 0 || caseCode == 255)
                        {
                            continue;
                        }

                        int cacheValidator = ((cellPos.x != 0 ? 0x01 : 0)
                                              | (cellPos.z != 0 ? 0x02 : 0)
                                              | (cellPos.y != 0 ? 0x04 : 0));

                        int cellClass = tables.RegularCellClass[caseCode];
                        ref var edgeCodes = ref tables.RegularVertexData[caseCode];
                        ref var cellData = ref tables.RegularCellData[cellClass];

                        long cellVertCount = cellData.GetVertexCount();

                        for (int i = 0; i < cellVertCount; ++i)
                        {
                            ushort edgeCode = edgeCodes[i];

                            ushort cornerIdx0 = (ushort)((edgeCode >> 4) & 0x0F);
                            ushort cornerIdx1 = (ushort)(edgeCode & 0x0F);

                            float density0 = _cellValues[cornerIdx0];
                            float density1 = _cellValues[cornerIdx1];

                            byte cacheIdx = (byte)((edgeCode >> 8) & 0x0F);
                            byte cacheDir = (byte)(edgeCode >> 12);

                            if (density1 == 0)
                            {
                                cacheDir = (byte)(cornerIdx1 ^ 7);
                                cacheIdx = 0;
                            }
                            else if (density0 == 0)
                            {
                                cacheDir = (byte)(cornerIdx0 ^ 7);
                                cacheIdx = 0;
                            }

                            bool bVertexCacheable = (cacheDir & cacheValidator) == cacheDir;
                            int vertexIndex = -1;

                            int cachePosX = x - (cacheDir & 1);
                            int cachePosZ = z - ((cacheDir >> 1) & 1);

                            var selectedCacheDock = ((cacheDir >> 2) & 1) == 1 ? _previousCache : _currentCache;

                            if (bVertexCacheable)
                            {
                                vertexIndex = selectedCacheDock[cachePosX * ChunkSize * 4 + cachePosZ * 4 + cacheIdx];
                            }

                            if (!bVertexCacheable || vertexIndex == -1)
                            {
                                float3 vertex;
                                float3 normal;

                                vertexIndex = vertexData.Length;

                                int vertBoundaryMask = 0;

                                if (cacheIdx == 0)
                                {
                                    int3 cornerOffset = density0 == 0
                                        ? tables.RegularCornerOffset[cornerIdx0]
                                        : tables.RegularCornerOffset[cornerIdx1];

                                    int vertPosX = x + cornerOffset.x;
                                    int vertPosY = y + cornerOffset.y;
                                    int vertPosZ = z + cornerOffset.z;

                                    vertex = new int3(vertPosX, vertPosY, vertPosZ);

                                    if (LOD > 0)
                                    {
                                        vertBoundaryMask = ((vertPosX == 0 ? 1 : 0)
                                                            | (vertPosY == 0 ? 2 : 0)
                                                            | (vertPosZ == 0 ? 4 : 0)
                                                            | (vertPosX == ChunkSize ? 8 : 0)
                                                            | (vertPosY == ChunkSize ? 16 : 0)
                                                            | (vertPosZ == ChunkSize ? 32 : 0));
                                    }

                                    vertPosX += padding;
                                    vertPosY += padding;
                                    vertPosZ += padding;

                                    normal = new Vector3(
                                        GetDensityValue(vertPosX - 1, vertPosY, vertPosZ) -
                                        GetDensityValue(vertPosX + 1, vertPosY, vertPosZ),
                                        GetDensityValue(vertPosX, vertPosY - 1, vertPosZ) -
                                        GetDensityValue(vertPosX, vertPosY + 1, vertPosZ),
                                        GetDensityValue(vertPosX, vertPosY, vertPosZ - 1) -
                                        GetDensityValue(vertPosX, vertPosY, vertPosZ + 1));

                                    if (bVertexCacheable)
                                    {
                                        selectedCacheDock[cachePosX * ChunkSize * 4 + cachePosZ * 4 + cacheIdx] =
                                            vertexIndex;
                                    }
                                }
                                else
                                {
                                    int3 vertLocalPos0 = cellPos + tables.RegularCornerOffset[cornerIdx0];
                                    int3 vertLocalPos1 = cellPos + tables.RegularCornerOffset[cornerIdx1];

                                    float3 vert0Copy = vertLocalPos0;
                                    float3 vert1Copy = vertLocalPos1;

                                    for (int j = 0; j < LOD; ++j)
                                    {
                                        float3 midPointLocalPos = (vert0Copy + vert1Copy) * 0.5f;
                                        float3 midPointWorldPos = ChunkMin + midPointLocalPos * LODScale;
                                        float midPointDensity = DensityGenerator.GetValue(midPointWorldPos);

                                        if (math.sign(midPointDensity) == math.sign(density0))
                                        {
                                            vert0Copy = midPointLocalPos;
                                            density0 = midPointDensity;
                                        }
                                        else
                                        {
                                            vert1Copy = midPointLocalPos;
                                            density1 = midPointDensity;
                                        }
                                    }

                                    float t0 = density1 / (density1 - density0);
                                    float t1 = 1 - t0;

                                    vertex = vert0Copy * t0 + vert1Copy * t1;

                                    int vertPosX0 = vertLocalPos0.x;
                                    int vertPosY0 = vertLocalPos0.y;
                                    int vertPosZ0 = vertLocalPos0.z;

                                    int vertPosX1 = vertLocalPos1.x;
                                    int vertPosY1 = vertLocalPos1.y;
                                    int vertPosZ1 = vertLocalPos1.z;

                                    if (LOD > 0)
                                    {
                                        vertBoundaryMask = (((vertPosX0 == 0 || vertPosX1 == 0) ? 1 : 0)
                                                            | ((vertPosY0 == 0 || vertPosY1 == 0) ? 2 : 0)
                                                            | ((vertPosZ0 == 0 || vertPosZ1 == 0) ? 4 : 0)
                                                            | ((vertPosX0 == ChunkSize || vertPosX1 == ChunkSize)
                                                                ? 8
                                                                : 0)
                                                            | ((vertPosY0 == ChunkSize || vertPosY1 == ChunkSize)
                                                                ? 16
                                                                : 0)
                                                            | ((vertPosZ0 == ChunkSize || vertPosZ1 == ChunkSize)
                                                                ? 32
                                                                : 0));
                                    }

                                    vertPosX0 += padding;
                                    vertPosY0 += padding;
                                    vertPosZ0 += padding;

                                    vertPosX1 += padding;
                                    vertPosY1 += padding;
                                    vertPosZ1 += padding;

                                    float3 normal0 = new float3(
                                        GetDensityValue(vertPosX0 - 1, vertPosY0, vertPosZ0) -
                                        GetDensityValue(vertPosX0 + 1, vertPosY0, vertPosZ0),
                                        GetDensityValue(vertPosX0, vertPosY0 - 1, vertPosZ0) -
                                        GetDensityValue(vertPosX0, vertPosY0 + 1, vertPosZ0),
                                        GetDensityValue(vertPosX0, vertPosY0, vertPosZ0 - 1) -
                                        GetDensityValue(vertPosX0, vertPosY0, vertPosZ0 + 1));

                                    float3 normal1 = new float3(
                                        GetDensityValue(vertPosX1 - 1, vertPosY1, vertPosZ1) -
                                        GetDensityValue(vertPosX1 + 1, vertPosY1, vertPosZ1),
                                        GetDensityValue(vertPosX1, vertPosY1 - 1, vertPosZ1) -
                                        GetDensityValue(vertPosX1, vertPosY1 + 1, vertPosZ1),
                                        GetDensityValue(vertPosX1, vertPosY1, vertPosZ1 - 1) -
                                        GetDensityValue(vertPosX1, vertPosY1, vertPosZ1 + 1));

                                    normal = normal0 + normal1;

                                    if (cornerIdx1 == 7)
                                    {
                                        _currentCache[x * ChunkSize * 4 + z * 4 + cacheIdx] = vertexIndex;
                                    }
                                }

                                normal = math.normalize(normal);

                                if (vertBoundaryMask > 0)
                                {
                                    int3 vertLocalPos0 = cellPos + tables.RegularCornerOffset[cornerIdx0];
                                    int3 vertLocalPos1 = cellPos + tables.RegularCornerOffset[cornerIdx1];
                                    bool isOnChunkEdge = IsOnEdge(vertLocalPos0) && IsOnEdge(vertLocalPos1);;
                                    float3 SecondaryVertPos = vertex;

                                    if (!isOnChunkEdge)
                                    {
                                                                            float3 Delta = new float3(0, 0, 0);

                                    if ((vertBoundaryMask & 1) == 1 && vertex.x < 1)
                                    {
                                        Delta.x = 1 - vertex.x;
                                    }
                                    else if ((vertBoundaryMask & 8) == 8 && vertex.x > (ChunkSize - 1))
                                    {
                                        Delta.x = (ChunkSize - 1) - vertex.x;
                                    }

                                    if ((vertBoundaryMask & 2) == 2 && vertex.y < 1)
                                    {
                                        Delta.y = 1 - vertex.y;
                                    }
                                    else if ((vertBoundaryMask & 16) == 16 && vertex.y > (ChunkSize - 1))
                                    {
                                        Delta.y = (ChunkSize - 1) - vertex.y;
                                    }

                                    if ((vertBoundaryMask & 4) == 4 && vertex.z < 1)
                                    {
                                        Delta.z = 1 - vertex.z;
                                    }
                                    else if ((vertBoundaryMask & 32) == 32 && vertex.z > (ChunkSize - 1))
                                    {
                                        Delta.z = (ChunkSize - 1) - vertex.z;
                                    }

                                    Delta *= TRANSITION_CELL_WIDTH_PERCENTAGE;

                                    SecondaryVertPos = vertex + new float3(
                                        (1 - normal.x * normal.x) * Delta.x - normal.y * normal.x * Delta.y -
                                        normal.z * normal.x * Delta.z,
                                        -normal.x * normal.y * Delta.x + (1 - normal.y * normal.y) * Delta.y -
                                        normal.z * normal.y * Delta.z,
                                        -normal.x * normal.z * Delta.x - normal.y * normal.z * Delta.y +
                                        (1 - normal.z * normal.z) * Delta.z);
                                    }
                                    
                                    SecondaryVertexData SecondaryVertexData = new SecondaryVertexData();
                                    SecondaryVertexData.Position = SecondaryVertPos * LODScale;
                                    SecondaryVertexData.VertexMask = (ushort)vertBoundaryMask;
                                    SecondaryVertexData.VertexIndex = (ushort)vertexIndex;
                                    secondaryVerts.Add(SecondaryVertexData);
                                }

                                var data = new VertexFullData()
                                {
                                    Position = vertex * LODScale,
                                    Normal = normal
                                };
                                vertexData.Add(data);
                            }

                            _vertexIndices[i] = vertexIndex;
                        }

                        long indexCount = cellData.GetTriangleCount() * 3;
                        ref var cellIndices = ref cellData.VertexIndex;
                        
                        bool isBorder = (x == 0 || x == ChunkSize - 1 || y == 0 || y == ChunkSize - 1 || z == 0 || z == ChunkSize - 1);
                        for (int i = 0; i < indexCount; i += 3)
                        {
                            int ia = _vertexIndices[cellIndices[i + 0]];
                            int ib = _vertexIndices[cellIndices[i + 1]];
                            int ic = _vertexIndices[cellIndices[i + 2]];
                            if (isBorder)
                            {
                                // borderIndices.Add(ia); borderIndices.Add(ib); borderIndices.Add(ic);
                                indices.Add((uint)ia);
                                indices.Add((uint)ib);
                                indices.Add((uint)ic);
                            }
                            else
                            {
                                indices.Add((uint)ia);
                                indices.Add((uint)ib);
                                indices.Add((uint)ic);
                            }
                               
                        }
                    }
                }

                (_currentCache, _previousCache) = (_previousCache, _currentCache);
            }
        }
        
        bool IsOnEdge(int3 pos) {
            int boundaryCount = 0;

            if (pos.x == 0 || pos.x == ChunkSize) boundaryCount++;
            if (pos.y == 0 || pos.y == ChunkSize) boundaryCount++;
            if (pos.z == 0 || pos.z == ChunkSize) boundaryCount++;

            return boundaryCount >= 2; 
        }

        public void PoligoniseTransitions()
        {
            if(LOD == 0)
                return;
            PoligoniseTransition(MeshData.GetLeftTransitionMeshData(), TransitionDirection.XMin);
            PoligoniseTransition(MeshData.GetDownTransitionMeshData(), TransitionDirection.YMin);
            PoligoniseTransition(MeshData.GetBackTransitionMeshData(), TransitionDirection.ZMin);
            PoligoniseTransition(MeshData.GetRightTransitionMeshData(), TransitionDirection.XMax);
            PoligoniseTransition(MeshData.GetUpTransitionMeshData(), TransitionDirection.YMax);
            PoligoniseTransition(MeshData.GetForwardTransitionMeshData(), TransitionDirection.ZMax);
        }

        private void PoligoniseTransition(MeshNativeFullData transitionMeshData, TransitionDirection direction)
        {
            int padding = 1;
            int LODScale = 1 << LOD;

            var _trCurrentCache =
                new NativeArray<int>(ChunkSize * 10, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var _trPreviousCache =
                new NativeArray<int>(ChunkSize * 10, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var _trVertexIndices = new NativeArray<int>(36, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var _trCellValues = new NativeArray<float>(13, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            var vertexData = transitionMeshData.VertexData;
            var indices = transitionMeshData.Indices;
            var secondaryVerts = transitionMeshData.SecondaryVertices;

            ref TransvoxelTablesBlobAsset tables = ref TablesRef.Value;

            for (int y = 0; y < ChunkSize; y++)
            {
                for (int x = 0; x < ChunkSize; x++)
                {
                    int3 pos0 = FaceToLocalSpace(direction, ChunkSize, x, y, 0) + padding;
                    int3 pos2 = FaceToLocalSpace(direction, ChunkSize, x + 1, y, 0) + padding;
                    int3 pos6 = FaceToLocalSpace(direction, ChunkSize, x, y + 1, 0) + padding;
                    int3 pos8 = FaceToLocalSpace(direction, ChunkSize, x + 1, y + 1, 0) + padding;

                    float3 pos1 = ChunkMin + FaceToLocalSpace(direction, ChunkSize, x + 0.5f, y, 0) * LODScale;
                    float3 pos3 = ChunkMin + FaceToLocalSpace(direction, ChunkSize, x, y + 0.5f, 0) * LODScale;
                    float3 pos4 = ChunkMin + FaceToLocalSpace(direction, ChunkSize, x + 0.5f, y + 0.5f, 0) * LODScale;
                    float3 pos5 = ChunkMin + FaceToLocalSpace(direction, ChunkSize, x + 1.0f, y + 0.5f, 0) * LODScale;
                    float3 pos7 = ChunkMin + FaceToLocalSpace(direction, ChunkSize, x + 0.5f, y + 1.0f, 0) * LODScale;

                    _trCellValues[0] = GetDensityValue(pos0);
                    _trCellValues[2] = GetDensityValue(pos2);
                    _trCellValues[6] = GetDensityValue(pos6);
                    _trCellValues[8] = GetDensityValue(pos8);

                    _trCellValues[1] = DensityGenerator.GetValue(pos1);
                    _trCellValues[3] = DensityGenerator.GetValue(pos3);
                    _trCellValues[4] = DensityGenerator.GetValue(pos4);
                    _trCellValues[5] = DensityGenerator.GetValue(pos5);
                    _trCellValues[7] = DensityGenerator.GetValue(pos7);

                    _trCellValues[0x9] = _trCellValues[0];
                    _trCellValues[0xA] = _trCellValues[2];
                    _trCellValues[0xB] = _trCellValues[6];
                    _trCellValues[0xC] = _trCellValues[8];

                    int caseCode = ((_trCellValues[0] < 0 ? 1 : 0)
                                    | (_trCellValues[1] < 0 ? 2 : 0)
                                    | (_trCellValues[2] < 0 ? 4 : 0)
                                    | (_trCellValues[5] < 0 ? 8 : 0)
                                    | (_trCellValues[8] < 0 ? 16 : 0)
                                    | (_trCellValues[7] < 0 ? 32 : 0)
                                    | (_trCellValues[6] < 0 ? 64 : 0)
                                    | (_trCellValues[3] < 0 ? 128 : 0)
                                    | (_trCellValues[4] < 0 ? 256 : 0));

                    _trCurrentCache[0 * ChunkSize + x] = -1;
                    _trCurrentCache[1 * ChunkSize + x] = -1;
                    _trCurrentCache[2 * ChunkSize + x] = -1;
                    _trCurrentCache[7 * ChunkSize + x] = -1;

                    if (caseCode == 0 || caseCode == 511)
                    {
                        continue;
                    }

                    int cacheValidator = ((x != 0 ? 0b01 : 0)
                                          | (y != 0 ? 0b10 : 0));

                    int cellClass = tables.TransitionCellClass[caseCode];
                    ref var edgeCodes = ref tables.TransitionVertexData[caseCode];
                    ref var cellData = ref tables.TransitionRegularCellData[cellClass & 0x7F];

                    long cellVertCount = cellData.GetVertexCount();

                    for (int i = 0; i < cellVertCount; ++i)
                    {
                        ushort edgeCode = edgeCodes[i];

                        ushort cornerIdx0 = (ushort)((edgeCode >> 4) & 0x0F);
                        ushort cornerIdx1 = (ushort)(edgeCode & 0x0F);

                        float density0 = _trCellValues[cornerIdx0];
                        float density1 = _trCellValues[cornerIdx1];

                        byte cacheIdx = (byte)((edgeCode >> 8) & 0x0F);
                        byte cacheDir = (byte)(edgeCode >> 12);

                        if (density1 == 0)
                        {
                            byte trCornerData = tables.TransitionCornerData[cornerIdx1];
                            cacheDir = (byte)((trCornerData >> 4) & 0x0F);
                            cacheIdx = (byte)(trCornerData & 0x0F);
                        }
                        else if (density0 == 0)
                        {
                            byte trCornerData = tables.TransitionCornerData[cornerIdx0];
                            cacheDir = (byte)((trCornerData >> 4) & 0x0F);
                            cacheIdx = (byte)(trCornerData & 0x0F);
                        }

                        bool bVertexCacheable = (cacheDir & cacheValidator) == cacheDir;
                        int vertexIndex = -1;

                        int cachePosX = x - (cacheDir & 1);

                        var selectedCacheDock = (cacheDir & 2) > 0 ? _trPreviousCache : _trCurrentCache;

                        if (bVertexCacheable)
                        {
                            vertexIndex = selectedCacheDock[cacheIdx * ChunkSize + cachePosX];
                        }

                        if (!bVertexCacheable || vertexIndex == -1)
                        {
                            float3 vertex;
                            float3 normal;
                            vertexIndex = vertexData.Length;

                            int vertBoundaryMask = 0;
                            bool bIsLowResFace = cacheIdx > 6;

                            if (density0 == 0 || density1 == 0)
                            {
                                int cornerIdx = density0 == 0 ? cornerIdx0 : cornerIdx1;

                                int3 cornerOffset = tables.TransitionCornerOffset[cornerIdx];

                                vertex = FaceToLocalSpace(direction, ChunkSize, x + cornerOffset.x * 0.5f,
                                    y + cornerOffset.y * 0.5f, 0);

                                if (bIsLowResFace)
                                {
                                    // todo avoid division, use 2 CornerOffset arrays?
                                    int3 vertLocalPos = FaceToLocalSpace(direction, ChunkSize, x + cornerOffset.x / 2,
                                        y + cornerOffset.y / 2, 0);

                                    int locX = vertLocalPos.x;
                                    int locY = vertLocalPos.y;
                                    int locZ = vertLocalPos.z;

                                    vertBoundaryMask = ((locX == 0 ? 1 : 0)
                                                        | (locY == 0 ? 2 : 0)
                                                        | (locZ == 0 ? 4 : 0)
                                                        | (locX == ChunkSize ? 8 : 0)
                                                        | (locY == ChunkSize ? 16 : 0)
                                                        | (locZ == ChunkSize ? 32 : 0));

                                    locX += padding;
                                    locY += padding;
                                    locZ += padding;

                                    normal = new float3(
                                        GetDensityValue(locX - 1, locY, locZ) - GetDensityValue(locX + 1, locY, locZ),
                                        GetDensityValue(locX, locY - 1, locZ) - GetDensityValue(locX, locY + 1, locZ),
                                        GetDensityValue(locX, locY, locZ - 1) - GetDensityValue(locX, locY, locZ + 1));
                                }
                                else
                                {
                                    float wX = ChunkMin.x + vertex.x * LODScale;
                                    float wY = ChunkMin.y + vertex.y * LODScale;
                                    float wZ = ChunkMin.z + vertex.z * LODScale;

                                    normal = new float3(
                                        DensityGenerator.GetValue(new float3(wX - 1, wY, wZ)) -
                                        DensityGenerator.GetValue(new float3(wX + 1, wY, wZ)),
                                        DensityGenerator.GetValue(new float3(wX, wY - 1, wZ)) -
                                        DensityGenerator.GetValue(new float3(wX, wY + 1, wZ)),
                                        DensityGenerator.GetValue(new float3(wX, wY, wZ - 1)) -
                                        DensityGenerator.GetValue(new float3(wX, wY, wZ + 1)));
                                }

                                if (cacheDir == 8)
                                {
                                    _trCurrentCache[cacheIdx * ChunkSize + x] = vertexIndex;
                                }
                                else if (bVertexCacheable)
                                {
                                    selectedCacheDock[cacheIdx * ChunkSize + cachePosX] = vertexIndex;
                                }
                            }
                            else
                            {
                                int3 cornerOffset0 = tables.TransitionCornerOffset[cornerIdx0];
                                int3 cornerOffset1 = tables.TransitionCornerOffset[cornerIdx1];

                                float3 corner0Copy = FaceToLocalSpace(direction, ChunkSize, x + cornerOffset0.x * 0.5f,
                                    y + cornerOffset0.y * 0.5f, 0);
                                float3 corner1Copy = FaceToLocalSpace(direction, ChunkSize, x + cornerOffset1.x * 0.5f,
                                    y + cornerOffset1.y * 0.5f, 0);

                                int subEdges = bIsLowResFace ? LOD : LOD - 1;

                                for (int j = 0; j < subEdges; ++j)
                                {
                                    float3 midPointLocalPos = (corner0Copy + corner1Copy) * 0.5f;

                                    float3 midPointWorldPos = ChunkMin + midPointLocalPos * LODScale;

                                    float midPointDensity = DensityGenerator.GetValue(midPointWorldPos);

                                    if (Mathf.Sign(midPointDensity) == Mathf.Sign(density0))
                                    {
                                        corner0Copy = midPointLocalPos;
                                        density0 = midPointDensity;
                                    }
                                    else
                                    {
                                        corner1Copy = midPointLocalPos;
                                        density1 = midPointDensity;
                                    }
                                }

                                float t0 = density1 / (density1 - density0);
                                float t1 = 1 - t0;

                                vertex = corner0Copy * t0 + corner1Copy * t1;
                                float3 normal0, normal1;

                                if (bIsLowResFace)
                                {
                                    int3 vert0LocPos = FaceToLocalSpace(direction, ChunkSize, x + cornerOffset0.x / 2,
                                        y + cornerOffset0.y / 2, 0);
                                    int3 vert1LocPos = FaceToLocalSpace(direction, ChunkSize, x + cornerOffset1.x / 2,
                                        y + cornerOffset1.y / 2, 0);

                                    int VX0 = vert0LocPos.x;
                                    int VY0 = vert0LocPos.y;
                                    int VZ0 = vert0LocPos.z;

                                    int VX1 = vert1LocPos.x;
                                    int VY1 = vert1LocPos.y;
                                    int VZ1 = vert1LocPos.z;

                                    vertBoundaryMask = ((VX0 == 0 || VX1 == 0 ? 1 : 0)
                                                        | (VY0 == 0 || VY1 == 0 ? 2 : 0)
                                                        | (VZ0 == 0 || VZ1 == 0 ? 4 : 0)
                                                        | (VX0 == ChunkSize || VX1 == ChunkSize ? 8 : 0)
                                                        | (VY0 == ChunkSize || VY1 == ChunkSize ? 16 : 0)
                                                        | (VZ0 == ChunkSize || VZ1 == ChunkSize ? 32 : 0));

                                    VX0 += padding;
                                    VY0 += padding;
                                    VZ0 += padding;

                                    VX1 += padding;
                                    VY1 += padding;
                                    VZ1 += padding;

                                    normal0 = new float3(
                                        GetDensityValue(VX0 - 1, VY0, VZ0) - GetDensityValue(VX0 + 1, VY0, VZ0),
                                        GetDensityValue(VX0, VY0 - 1, VZ0) - GetDensityValue(VX0, VY0 + 1, VZ0),
                                        GetDensityValue(VX0, VY0, VZ0 - 1) - GetDensityValue(VX0, VY0, VZ0 + 1));

                                    normal1 = new float3(
                                        GetDensityValue(VX1 - 1, VY1, VZ1) - GetDensityValue(VX1 + 1, VY1, VZ1),
                                        GetDensityValue(VX1, VY1 - 1, VZ1) - GetDensityValue(VX1, VY1 + 1, VZ1),
                                        GetDensityValue(VX1, VY1, VZ1 - 1) - GetDensityValue(VX1, VY1, VZ1 + 1));
                                }
                                else
                                {
                                    float3 vert0LocPos = FaceToLocalSpace(direction, ChunkSize,
                                        x + cornerOffset0.x * 0.5f, y + cornerOffset0.y * 0.5f, 0);
                                    float3 vert1LocPos = FaceToLocalSpace(direction, ChunkSize,
                                        x + cornerOffset1.x * 0.5f, y + cornerOffset1.y * 0.5f, 0);

                                    // world coordinates
                                    float wX0 = ChunkMin.x + vert0LocPos.x * LODScale;
                                    float wY0 = ChunkMin.y + vert0LocPos.y * LODScale;
                                    float wZ0 = ChunkMin.z + vert0LocPos.z * LODScale;

                                    float wX1 = ChunkMin.x + vert1LocPos.x * LODScale;
                                    float wY1 = ChunkMin.y + vert1LocPos.y * LODScale;
                                    float wZ1 = ChunkMin.z + vert1LocPos.z * LODScale;

                                    normal0 = new float3(
                                        DensityGenerator.GetValue(new float3(wX0 - 1, wY0, wZ0)) -
                                        DensityGenerator.GetValue(new float3(wX0 + 1, wY0, wZ0)),
                                        DensityGenerator.GetValue(new float3(wX0, wY0 - 1, wZ0)) -
                                        DensityGenerator.GetValue(new float3(wX0, wY0 + 1, wZ0)),
                                        DensityGenerator.GetValue(new float3(wX0, wY0, wZ0 - 1)) -
                                        DensityGenerator.GetValue(new float3(wX0, wY0, wZ0 + 1)));

                                    normal1 = new float3(
                                        DensityGenerator.GetValue(new float3(wX1 - 1, wY1, wZ1)) -
                                        DensityGenerator.GetValue(new float3(wX1 + 1, wY1, wZ1)),
                                        DensityGenerator.GetValue(new float3(wX1, wY1 - 1, wZ1)) -
                                        DensityGenerator.GetValue(new float3(wX1, wY1 + 1, wZ1)),
                                        DensityGenerator.GetValue(new float3(wX1, wY1, wZ1 - 1)) -
                                        DensityGenerator.GetValue(new float3(wX1, wY1, wZ1 + 1)));
                                }

                                normal = normal0 + normal1;

                                if (cacheDir == 8)
                                {
                                    _trCurrentCache[cacheIdx * ChunkSize + x] = vertexIndex;
                                }
                                else if (bVertexCacheable && cacheDir != 4)
                                {
                                    selectedCacheDock[cacheIdx * ChunkSize + cachePosX] = vertexIndex;
                                }
                            }

                            normal = math.normalize(normal);

                            // if vertex lies on the low resolution face of the transition cell
                            if (vertBoundaryMask > 0)
                            {
                                float3 delta = float3.zero;

                                if ((vertBoundaryMask & 1) == 1 && vertex.x < 1)
                                {
                                    delta.x = 1 - vertex.x;
                                }
                                else if ((vertBoundaryMask & 8) == 8 && vertex.x > (ChunkSize - 1))
                                {
                                    delta.x = (ChunkSize - 1) - vertex.x;
                                }

                                if ((vertBoundaryMask & 2) == 2 && vertex.y < 1)
                                {
                                    delta.y = 1 - vertex.y;
                                }
                                else if ((vertBoundaryMask & 16) == 16 && vertex.y > (ChunkSize - 1))
                                {
                                    delta.y = (ChunkSize - 1) - vertex.y;
                                }

                                if ((vertBoundaryMask & 4) == 4 && vertex.z < 1)
                                {
                                    delta.z = 1 - vertex.z;
                                }
                                else if ((vertBoundaryMask & 32) == 32 && vertex.z > (ChunkSize - 1))
                                {
                                    delta.z = (ChunkSize - 1) - vertex.z;
                                }

                                delta *= TRANSITION_CELL_WIDTH_PERCENTAGE;

                                float3 vertexSecondaryPos = vertex + new float3(
                                    (1 - normal.x * normal.x) * delta.x - normal.y * normal.x * delta.y -
                                    normal.z * normal.x * delta.z,
                                    -normal.x * normal.y * delta.x + (1 - normal.y * normal.y) * delta.y -
                                    normal.z * normal.y * delta.z,
                                    -normal.x * normal.z * delta.x - normal.y * normal.z * delta.y +
                                    (1 - normal.z * normal.z) * delta.z);

                                secondaryVerts.Add(new SecondaryVertexData()
                                {
                                    Position = vertexSecondaryPos * LODScale,
                                    VertexMask = (ushort)vertBoundaryMask,
                                    VertexIndex = (ushort)vertexIndex
                                });
                            }

                            vertexData.Add(new VertexFullData()
                            {
                                Position = vertex * LODScale,
                                Normal = normal
                            });
                        }

                        _trVertexIndices[i] = vertexIndex;
                    }

                    long indexCount = cellData.GetTriangleCount() * 3;
                    ref var cellIndices = ref cellData.VertexIndex;

                    bool bFlipWinding = (cellClass & 0x80) > 0;

                    for (int i = 0; i < indexCount; i += 3)
                    {
                        int ia = _trVertexIndices[cellIndices[i + 0]];
                        int ib = _trVertexIndices[cellIndices[i + 1]];
                        int ic = _trVertexIndices[cellIndices[i + 2]];

                        if (!bFlipWinding)
                        {
                            indices.Add((uint)ic);
                            indices.Add((uint)ib);
                            indices.Add((uint)ia);
                        }
                        else
                        {
                            indices.Add((uint)ia);
                            indices.Add((uint)ib);
                            indices.Add((uint)ic);
                        }
                    }
                }

                var temp = _trCurrentCache;
                _trCurrentCache = _trPreviousCache;
                _trPreviousCache = temp;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        float GetDensityValue(int x, int y, int z)
        {
            int DensitySize = ChunkSize + 3;
            return DensityData[x * DensitySize * DensitySize + y * DensitySize + z];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        float GetDensityValue(int3 position)
        {
            return GetDensityValue(position.x, position.y, position.z);
        }

        public void GenerateMaterials()
        {
            GenerateMaterialsForMesh(MeshData.GetMainMeshData());
            GenerateMaterialsForMesh(MeshData.GetLeftTransitionMeshData());
            GenerateMaterialsForMesh(MeshData.GetRightTransitionMeshData());
            GenerateMaterialsForMesh(MeshData.GetForwardTransitionMeshData());
            GenerateMaterialsForMesh(MeshData.GetBackTransitionMeshData());
            GenerateMaterialsForMesh(MeshData.GetUpTransitionMeshData());
            GenerateMaterialsForMesh(MeshData.GetDownTransitionMeshData());
        }

        void GenerateMaterialsForMesh(MeshNativeFullData meshNativeData)
        {
            var vertexDataList = meshNativeData.VertexData;

            for (int i = 0; i < vertexDataList.Length; i++)
            {
                var vertexData = vertexDataList[i];
                MaterialGenerator.GetMaterial(ref vertexData, in ChunkMin);
                vertexDataList[i] = vertexData;
            }
        }

        public void CreateMeshDataArray()
        {
            var MainMeshData = MeshArray[0];
            var LeftTransitionMeshData = MeshArray[1];
            var DownTransitionMeshData = MeshArray[2];
            var BackTransitionMeshData = MeshArray[3];
            var RightTransitionMeshData = MeshArray[4];
            var UpTransitionMeshData = MeshArray[5];
            var ForwardTransitionMeshData = MeshArray[6];

            CopyToMeshData(MeshData.GetMainMeshData(), MainMeshData, 7, false);
            CopyToMeshData(MeshData.GetLeftTransitionMeshData(), LeftTransitionMeshData, 0);
            CopyToMeshData(MeshData.GetDownTransitionMeshData(), DownTransitionMeshData, 1);
            CopyToMeshData(MeshData.GetBackTransitionMeshData(), BackTransitionMeshData, 2);
            CopyToMeshData(MeshData.GetRightTransitionMeshData(), RightTransitionMeshData, 3);
            CopyToMeshData(MeshData.GetUpTransitionMeshData(), UpTransitionMeshData, 4);
            CopyToMeshData(MeshData.GetForwardTransitionMeshData(), ForwardTransitionMeshData, 5);
        }

        void CopyToMeshData(MeshNativeFullData fromMeshNativeData, Mesh.MeshData toMeshData, int neighborBit, bool applySecondary = true)
        {
            var vertAttributes = new NativeArray<VertexAttributeDescriptor>(5, Allocator.Temp);
            vertAttributes[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32);
            vertAttributes[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32);
            vertAttributes[2] =
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4);
            vertAttributes[3] =
                new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4);
            vertAttributes[4] =
                new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 4);

            toMeshData.SetVertexBufferParams(fromMeshNativeData.VertexData.Length, vertAttributes);

            vertAttributes.Dispose();

            var vertices = toMeshData.GetVertexData<VertexFullData>();
            for (int i = 0; i < fromMeshNativeData.VertexData.Length; i++)
            {
                vertices[i] = fromMeshNativeData.VertexData[i];
                min = math.min(min, vertices[i].Position);
                max = math.max(max, vertices[i].Position);
            }
            int chunkExtents = (ChunkSize >> 1) << LOD;
            var pos = ChunkMin + chunkExtents;
            if (pos.Equals(new int3(512, 512, 512)))
            {
                int a = 1;
            }
            for (int i = 0; i < fromMeshNativeData.SecondaryVertices.Length; ++i)
            {
                var secondaryVert = fromMeshNativeData.SecondaryVertices[i];
                var vertData = vertices[secondaryVert.VertexIndex];
                if (applySecondary)
                {
                    vertData.Position = secondaryVert.Position;
                    vertices[secondaryVert.VertexIndex] = vertData;
                    // int count = 0;
                    // for (int m = 0; m < 8; m++) {
                    //     if (((secondaryVert.VertexMask >> m) & 1) != 0) count++;
                    // }
                    //
                    // if (count == 1)
                    // {
                    //     vertData.Position = secondaryVert.Position;
                    //     vertices[secondaryVert.VertexIndex] = vertData;
                    // }
                }
                else
                {
                    vertData.SecondPosAndMask = new float4(secondaryVert.Position, secondaryVert.VertexMask);
                    vertices[secondaryVert.VertexIndex] = vertData;
                }
            }

            var fromMeshNativeDataIndices = fromMeshNativeData.Indices;
            var indexList = new NativeList<uint>(fromMeshNativeDataIndices.Length, Allocator.Temp);

            // skip invalid triangles (when two vertices are approximately at the same position or the vertices are colinear)
            for (int i = 0; i < fromMeshNativeDataIndices.Length; i += 3)
            {
                if ((int)fromMeshNativeDataIndices[i + 0] < 0)
                {
                    Assert.IsTrue((int)fromMeshNativeDataIndices[i + 0] > 0);
                }

                var vertA = vertices[(int)fromMeshNativeDataIndices[i + 0]].Position;
                var vertB = vertices[(int)fromMeshNativeDataIndices[i + 1]].Position;
                var vertC = vertices[(int)fromMeshNativeDataIndices[i + 2]].Position;

                if (!(vertA.Approx(vertB) || vertB.Approx(vertC) || vertC.Approx(vertA)) &&
                    !math.cross(vertA - vertB, vertA - vertC).Approx(new float3(0, 0, 0)))
                {
                    indexList.Add(fromMeshNativeDataIndices[i + 0]);
                    indexList.Add(fromMeshNativeDataIndices[i + 1]);
                    indexList.Add(fromMeshNativeDataIndices[i + 2]);
                }
            }

            toMeshData.SetIndexBufferParams(indexList.Length, IndexFormat.UInt32);
            var indices = toMeshData.GetIndexData<uint>();
            for (int i = 0; i < indexList.Length; ++i)
            {
                indices[i] = indexList[i];
            }

            toMeshData.subMeshCount = 1;
            toMeshData.SetSubMesh(0, new SubMeshDescriptor(0, indices.Length));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int3 FaceToLocalSpace(
            TransitionDirection direction,
            int chunkSize,
            int x,
            int y,
            int z)
        {
            switch (direction)
            {
                case TransitionDirection.XMin:
                    return new int3(z, x, y);
                case TransitionDirection.XMax:
                    return new int3(chunkSize - z, y, x);
                case TransitionDirection.YMin:
                    return new int3(y, z, x);
                case TransitionDirection.YMax:
                    return new int3(x, chunkSize - z, y);
                case TransitionDirection.ZMin:
                    return new int3(x, y, z);
                case TransitionDirection.ZMax:
                    return new int3(y, x, chunkSize - z);
                default:
                    return new int3(x, y, z);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float3 FaceToLocalSpace(
            TransitionDirection direction,
            int chunkSize,
            float x,
            float y,
            float z)
        {
            switch (direction)
            {
                case TransitionDirection.XMin:
                    return new float3(z, x, y);
                case TransitionDirection.XMax:
                    return new float3(chunkSize - z, y, x);
                case TransitionDirection.YMin:
                    return new float3(y, z, x);
                case TransitionDirection.YMax:
                    return new float3(x, chunkSize - z, y);
                case TransitionDirection.ZMin:
                    return new float3(x, y, z);
                case TransitionDirection.ZMax:
                    return new float3(y, x, chunkSize - z);
                default:
                    return new float3(x, y, z);
            }
        }

        public void Dispose()
        {
            MeshData.Dispose();
        }
    }
}