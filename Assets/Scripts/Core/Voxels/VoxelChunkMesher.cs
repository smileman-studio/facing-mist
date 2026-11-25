using Unity.Collections;
using Unity.Mathematics;

namespace SmilemanStudio.Core.Voxels
{
    /// <summary>
    /// Автономный мешировщик одного воксельного чанка (ADR-0002, D7).
    /// </summary>
    /// <remarks>
    /// Принимает поле плотности/материала по контракту (<see cref="IVoxelDensityField"/>,
    /// <see cref="IVoxelMaterialField"/>) и заявку <see cref="VoxelChunkRequest"/> (из
    /// <see cref="VoxelLodPlanner"/>) и выдаёт объединённую геометрию regular + transition
    /// cells. Движок НЕ знает, что строит: остров это, сфера или загруженный том — у него
    /// только функция плотности, origin и LOD чанка.
    ///
    /// Пайплайн одного чанка:
    ///   1. <see cref="VoxelFieldSampler"/> заполняет density 19³ и nodeMaterial 17³ из поля.
    ///   2. <see cref="TransvoxelMesher.MeshInto"/> меширует regular cells с faceMask из
    ///      <see cref="VoxelChunkRequest.SeamFaceMask"/> (приграничный слой пропускается там,
    ///      где нужен шов).
    ///   3. Для каждой seam-грани заполняется face-буфер и мешируются transition cells;
    /// конвертация происходит внутри мешера, AppendTransitions не масштабирует;
    ///
    /// Все методы Burst-safe: generic-поля (struct + interface), никаких managed-аллокаций
    /// в ядре; scratch-буферы передаёт вызывающий (переиспользование между чанками).
    /// </remarks>
    public static class VoxelChunkMesher
    {
        /// <summary>
        /// Построить геометрию чанка в переданную <paramref name="geometry"/> (append).
        /// </summary>
        /// <remarks>
        /// Вызывающий обязан обнулить geometry (<see cref="VoxelChunkGeometry.Reset"/>)
        /// перед вызовом. Scratch-буферы <paramref name="densityScratch"/> (длина
        /// <see cref="VoxelFieldSampler.CacheVolume"/>) и <paramref name="nodeMatScratch"/>
        /// (длина <see cref="VoxelFieldSampler.NodeMaterialVolume"/>) переиспользуются.
        /// </remarks>
        public static void BuildChunk<TD, TM>(
            in TD densityField,
            in TM materialField,
            in VoxelChunkRequest req,
            ref VoxelChunkGeometry geometry,
            NativeArray<short> densityScratch,
            NativeArray<byte> nodeMatScratch)
            where TD : struct, IVoxelDensityField
            where TM : struct, IVoxelMaterialField
        {
            int lod = req.Lod;

            VoxelFieldSampler.FillDensity(req.Origin0, lod, densityField, densityScratch);
            VoxelFieldSampler.FillNodeMaterial(req.Origin0, lod, materialField, nodeMatScratch);

            // 1. Regular cells: пропуск seam-слоёв по faceMask планировщика.
            // Эталон: regular меширует все слои; фартук шьёт кривые на плоскости шва.
            TransvoxelMesher.MeshInto(
                densityScratch, nodeMatScratch,
                geometry.Positions, geometry.Normals, geometry.Materials, geometry.Indices,
                0);

            // 2. Transition cells на seam-гранях (только lod >= 1).
            if (lod >= 1 && req.SeamFaceMask != 0)
            {
                AppendTransitions(in densityField, in materialField, req, lod,
                    densityScratch, nodeMatScratch, ref geometry);
            }
        }

        private static void AppendTransitions<TD, TM>(
            in TD densityField, in TM materialField, in VoxelChunkRequest req, int lod,
            NativeArray<short> densityScratch, NativeArray<byte> nodeMatScratch,
            ref VoxelChunkGeometry geometry)
            where TD : struct, IVoxelDensityField
            where TM : struct, IVoxelMaterialField
        {
            for (int d = 1; d <= 6; d++)
            {
                var dir = (TransitionDirection)d;
                if ((req.SeamFaceMask & TransvoxelMesher.SkipFaceBit(dir)) == 0)
                    continue;

                // Чистые per-face списки: MeshTransitionInto пишет индексы от 0 относительно
                // своих positions, поэтому base = текущая длина geometry добавляется при переносе.
                var tPos = new NativeList<float3>(64, Allocator.Temp);
                var tGrad = new NativeList<int3>(64, Allocator.Temp);
                var tMat = new NativeList<byte>(64, Allocator.Temp);
                var tIdx = new NativeList<uint>(96, Allocator.Temp);

                var face = new TransitionFaceSamples();
                VoxelFieldSampler.FillFace(req.Origin0, lod, dir, densityField, materialField, ref face);
                TransvoxelTransitionMesher.MeshTransitionInto(
                    densityScratch, nodeMatScratch, face, dir, tPos, tGrad, tMat, tIdx);
                face.Density.Dispose();
                face.Material.Dispose();

                int baseVertex = geometry.VertexCount;

                // Перенос вершин: MeshTransitionInto уже отдаёт coarse-ячейки (0..16) —
                // Q16-полуячейки × 2^-17 (D3). Дополнительный /2 сжимал полосу к min-углу чанка.
                for (int v = 0; v < tPos.Length; v++)
                {
                    geometry.Positions.Add(tPos[v]);
                    geometry.Normals.Add(TransvoxelMesher.NormalizeGradient(tGrad[v]));
                    geometry.Materials.Add(tMat[v]);
                }

                // Индексы triangle-вершин -> абсолютная нумерация geometry.
                for (int i = 0; i < tIdx.Length; i++)
                    geometry.Indices.Add((uint)(tIdx[i] + baseVertex));

                tPos.Dispose();
                tGrad.Dispose();
                tMat.Dispose();
                tIdx.Dispose();
            }
        }
    }
}
