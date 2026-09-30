using Apache.Arrow;
using Apache.Arrow.Compression;
using Apache.Arrow.Ipc;
using FruitFlyBrain.Engine.Graph;

namespace FruitFlyBrain.Ingest;

/// <summary>
/// High-throughput streaming Apache Arrow Feather (.feather) parser.
/// Features vectorized memory access across Arrow buffers for maximum ingestion speed.
/// </summary>
public static class FeatherConnectomeParser
{
    private static readonly string[] PreColumnCandidates = ["pre_root_id", "pre_pt_root_id", "pre_id", "source", "pre"];
    private static readonly string[] PostColumnCandidates = ["post_root_id", "post_pt_root_id", "post_id", "target", "post"];
    private static readonly string[] WeightColumnCandidates = ["syn_count", "weight", "count", "num_syn", "synapses", "weight_raw"];

    public static CompactCsrGraph ParseFeather(
        string featherFilePath,
        string? preCol = null,
        string? postCol = null,
        string? weightCol = null)
    {
        if (!File.Exists(featherFilePath))
        {
            throw new FileNotFoundException($"Feather file not found: {featherFilePath}");
        }

        Console.WriteLine($"[Feather Parser] Opening '{Path.GetFileName(featherFilePath)}' ({new FileInfo(featherFilePath).Length / (1024.0 * 1024.0):F2} MB)...");

        var codecFactory = new CompressionCodecFactory();
        string resolvedPreCol;
        string resolvedPostCol;
        string? resolvedWeightCol;

        var uniqueIds = new HashSet<ulong>(250_000);
        long totalEdgesCount = 0;

        // Pass 1: Schema discovery and unique ID collection
        using (var fs = File.OpenRead(featherFilePath))
        using (var reader = new ArrowFileReader(fs, compressionCodecFactory: codecFactory))
        {
            var schema = reader.Schema;
            Console.WriteLine($"[Feather Parser] Detected Columns: {string.Join(", ", schema.FieldsList.Select(f => $"{f.Name} [{f.DataType.Name}]"))}");

            resolvedPreCol = preCol ?? DetectColumn(schema, PreColumnCandidates, defaultIndex: 0);
            resolvedPostCol = postCol ?? DetectColumn(schema, PostColumnCandidates, defaultIndex: 1);
            resolvedWeightCol = weightCol ?? TryDetectColumn(schema, WeightColumnCandidates);

            Console.WriteLine($"[Feather Parser] Mappings:");
            Console.WriteLine($"  - Pre-synaptic:   '{resolvedPreCol}'");
            Console.WriteLine($"  - Post-synaptic:  '{resolvedPostCol}'");
            Console.WriteLine($"  - Synapse Weight: '{(resolvedWeightCol ?? "<Default 1>")}'");

            Console.WriteLine("\n[Feather Parser] Pass 1: Streaming batches to identify unique neurons...");
            var batch = reader.ReadNextRecordBatch();
            int batchNum = 0;

            while (batch != null)
            {
                batchNum++;
                var preArray = batch.Column(resolvedPreCol);
                var postArray = batch.Column(resolvedPostCol);
                int rowCount = batch.Length;
                totalEdgesCount += rowCount;

                if (preArray is Int64Array preI64 && postArray is Int64Array postI64)
                {
                    var preSpan = preI64.Values;
                    var postSpan = postI64.Values;
                    for (int i = 0; i < rowCount; i++)
                    {
                        uniqueIds.Add((ulong)preSpan[i]);
                        uniqueIds.Add((ulong)postSpan[i]);
                    }
                }
                else if (preArray is UInt64Array preU64 && postArray is UInt64Array postU64)
                {
                    var preSpan = preU64.Values;
                    var postSpan = postU64.Values;
                    for (int i = 0; i < rowCount; i++)
                    {
                        uniqueIds.Add(preSpan[i]);
                        uniqueIds.Add(postSpan[i]);
                    }
                }
                else
                {
                    for (int i = 0; i < rowCount; i++)
                    {
                        uniqueIds.Add(ExtractUlong(preArray, i));
                        uniqueIds.Add(ExtractUlong(postArray, i));
                    }
                }

                if (batchNum % 20 == 0 || rowCount > 500_000)
                {
                    Console.Write($"\r[Feather Parser] Batch {batchNum}: processed {totalEdgesCount:N0} edges, found {uniqueIds.Count:N0} unique neurons...");
                }

                batch = reader.ReadNextRecordBatch();
            }

            Console.WriteLine($"\r[Feather Parser] Pass 1 Complete: {uniqueIds.Count:N0} neurons, {totalEdgesCount:N0} synaptic connections.");
        }

        // Sort unique IDs and construct dense mapper
        var sortedIds = uniqueIds.ToArray();
        System.Array.Sort(sortedIds);
        var idMap = new NeuronIdMap(sortedIds);
        int nodeCount = sortedIds.Length;

        // Degree counting array
        var outDegrees = new int[nodeCount];

        // Pass 2: Calculate out-degrees for CSR row offsets
        Console.WriteLine("\n[Feather Parser] Pass 2: Computing node out-degrees for CSR offsets...");
        using (var fs = File.OpenRead(featherFilePath))
        using (var reader = new ArrowFileReader(fs, compressionCodecFactory: codecFactory))
        {
            var batch = reader.ReadNextRecordBatch();
            while (batch != null)
            {
                var preArray = batch.Column(resolvedPreCol);
                int rowCount = batch.Length;

                if (preArray is Int64Array preI64)
                {
                    var preSpan = preI64.Values;
                    for (int i = 0; i < rowCount; i++)
                    {
                        int idx = idMap.TryGetIndex((ulong)preSpan[i]);
                        if (idx >= 0) outDegrees[idx]++;
                    }
                }
                else if (preArray is UInt64Array preU64)
                {
                    var preSpan = preU64.Values;
                    for (int i = 0; i < rowCount; i++)
                    {
                        int idx = idMap.TryGetIndex(preSpan[i]);
                        if (idx >= 0) outDegrees[idx]++;
                    }
                }
                else
                {
                    for (int i = 0; i < rowCount; i++)
                    {
                        int idx = idMap.TryGetIndex(ExtractUlong(preArray, i));
                        if (idx >= 0) outDegrees[idx]++;
                    }
                }

                batch = reader.ReadNextRecordBatch();
            }
        }

        // Build CSR row offsets
        var rowOffsets = new long[nodeCount + 1];
        rowOffsets[0] = 0;
        for (int i = 0; i < nodeCount; i++)
        {
            rowOffsets[i + 1] = rowOffsets[i] + outDegrees[i];
        }

        long actualEdgeCount = rowOffsets[nodeCount];
        Console.WriteLine($"[Feather Parser] Allocating CSR contiguous buffers for {actualEdgeCount:N0} edges...");

        var columnIndices = GC.AllocateUninitializedArray<int>((int)actualEdgeCount);
        var weights = GC.AllocateUninitializedArray<ushort>((int)actualEdgeCount);

        var writeCursors = new long[nodeCount];
        System.Array.Copy(rowOffsets, writeCursors, nodeCount);

        // Pass 3: Fill CSR neighbors and synaptic weights
        Console.WriteLine("[Feather Parser] Pass 3: Populating CSR edges and synaptic weights...");
        using (var fs = File.OpenRead(featherFilePath))
        using (var reader = new ArrowFileReader(fs, compressionCodecFactory: codecFactory))
        {
            var batch = reader.ReadNextRecordBatch();
            int batchNum = 0;
            long populated = 0;

            while (batch != null)
            {
                batchNum++;
                var preArray = batch.Column(resolvedPreCol);
                var postArray = batch.Column(resolvedPostCol);
                var weightArray = resolvedWeightCol != null ? batch.Column(resolvedWeightCol) : null;
                int rowCount = batch.Length;

                if (preArray is Int64Array preI64 && postArray is Int64Array postI64)
                {
                    var preSpan = preI64.Values;
                    var postSpan = postI64.Values;

                    if (weightArray is Int32Array wI32)
                    {
                        var wSpan = wI32.Values;
                        for (int i = 0; i < rowCount; i++)
                        {
                            int preIdx = idMap.TryGetIndex((ulong)preSpan[i]);
                            int postIdx = idMap.TryGetIndex((ulong)postSpan[i]);
                            if (preIdx >= 0 && postIdx >= 0)
                            {
                                long pos = writeCursors[preIdx]++;
                                columnIndices[pos] = postIdx;
                                weights[pos] = (ushort)Math.Clamp(wSpan[i], 1, 65535);
                                populated++;
                            }
                        }
                    }
                    else if (weightArray is Int64Array wI64)
                    {
                        var wSpan = wI64.Values;
                        for (int i = 0; i < rowCount; i++)
                        {
                            int preIdx = idMap.TryGetIndex((ulong)preSpan[i]);
                            int postIdx = idMap.TryGetIndex((ulong)postSpan[i]);
                            if (preIdx >= 0 && postIdx >= 0)
                            {
                                long pos = writeCursors[preIdx]++;
                                columnIndices[pos] = postIdx;
                                weights[pos] = (ushort)Math.Clamp(wSpan[i], 1, 65535);
                                populated++;
                            }
                        }
                    }
                    else
                    {
                        for (int i = 0; i < rowCount; i++)
                        {
                            int preIdx = idMap.TryGetIndex((ulong)preSpan[i]);
                            int postIdx = idMap.TryGetIndex((ulong)postSpan[i]);
                            if (preIdx >= 0 && postIdx >= 0)
                            {
                                long pos = writeCursors[preIdx]++;
                                columnIndices[pos] = postIdx;
                                weights[pos] = weightArray != null ? ExtractUshort(weightArray, i) : (ushort)1;
                                populated++;
                            }
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < rowCount; i++)
                    {
                        int preIdx = idMap.TryGetIndex(ExtractUlong(preArray, i));
                        int postIdx = idMap.TryGetIndex(ExtractUlong(postArray, i));
                        if (preIdx >= 0 && postIdx >= 0)
                        {
                            long pos = writeCursors[preIdx]++;
                            columnIndices[pos] = postIdx;
                            weights[pos] = weightArray != null ? ExtractUshort(weightArray, i) : (ushort)1;
                            populated++;
                        }
                    }
                }

                if (batchNum % 20 == 0 || rowCount > 500_000)
                {
                    Console.Write($"\r[Feather Parser] Batch {batchNum}: populated {populated:N0} edges...");
                }

                batch = reader.ReadNextRecordBatch();
            }

            Console.WriteLine($"\r[Feather Parser] Ingestion complete: {populated:N0} edges successfully indexed.");
        }

        return new CompactCsrGraph(idMap, rowOffsets, columnIndices, weights);
    }

    private static string DetectColumn(Schema schema, string[] candidates, int defaultIndex)
    {
        foreach (var name in candidates)
        {
            if (schema.GetFieldIndex(name) >= 0) return name;
        }
        if (schema.FieldsList.Count > defaultIndex)
        {
            return schema.FieldsList[defaultIndex].Name;
        }
        throw new InvalidDataException($"Could not detect column from candidates: {string.Join(", ", candidates)}");
    }

    private static string? TryDetectColumn(Schema schema, string[] candidates)
    {
        foreach (var name in candidates)
        {
            if (schema.GetFieldIndex(name) >= 0) return name;
        }
        return null;
    }

    private static ulong ExtractUlong(IArrowArray array, int index)
    {
        return array switch
        {
            UInt64Array u64 => u64.GetValue(index) ?? 0,
            Int64Array i64 => (ulong)(i64.GetValue(index) ?? 0),
            UInt32Array u32 => u32.GetValue(index) ?? 0,
            Int32Array i32 => (ulong)(i32.GetValue(index) ?? 0),
            StringArray s => ulong.TryParse(s.GetString(index), out var val) ? val : 0,
            _ => Convert.ToUInt64(array)
        };
    }

    private static ushort ExtractUshort(IArrowArray array, int index)
    {
        int val = array switch
        {
            Int32Array i32 => i32.GetValue(index) ?? 1,
            Int64Array i64 => (int)(i64.GetValue(index) ?? 1),
            UInt32Array u32 => (int)(u32.GetValue(index) ?? 1),
            FloatArray f => (int)(f.GetValue(index) ?? 1f),
            DoubleArray d => (int)(d.GetValue(index) ?? 1.0),
            _ => 1
        };
        return (ushort)Math.Clamp(val, 1, 65535);
    }
}
