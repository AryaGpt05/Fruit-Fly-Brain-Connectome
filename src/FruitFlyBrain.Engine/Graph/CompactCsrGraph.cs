using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FruitFlyBrain.Engine.Graph;

/// <summary>
/// High-performance Compressed Sparse Row (CSR) graph representation.
/// Optimized for 50M+ nodes with sub-millisecond edge traversals.
/// </summary>
public sealed class CompactCsrGraph : INeuronGraph, IDisposable
{
    private readonly NeuronIdMap _idMap;
    private readonly long[] _rowOffsets;
    private readonly int[] _columnIndices;
    private readonly ushort[] _weights;

    public int NodeCount => _idMap.Count;
    public long EdgeCount => _columnIndices.LongLength;
    public NeuronIdMap IdMap => _idMap;
    public long[] RowOffsets => _rowOffsets;
    public int[] ColumnIndices => _columnIndices;
    public ushort[] Weights => _weights;

    public long MemorySizeBytes =>
        ((long)_idMap.Count * sizeof(ulong)) +
        ((long)_rowOffsets.Length * sizeof(long)) +
        ((long)_columnIndices.Length * sizeof(int)) +
        ((long)_weights.Length * sizeof(ushort));

    public CompactCsrGraph(NeuronIdMap idMap, long[] rowOffsets, int[] columnIndices, ushort[] weights)
    {
        _idMap = idMap ?? throw new ArgumentNullException(nameof(idMap));
        _rowOffsets = rowOffsets ?? throw new ArgumentNullException(nameof(rowOffsets));
        _columnIndices = columnIndices ?? throw new ArgumentNullException(nameof(columnIndices));
        _weights = weights ?? throw new ArgumentNullException(nameof(weights));

        if (rowOffsets.Length != idMap.Count + 1)
        {
            throw new ArgumentException($"RowOffsets length ({rowOffsets.Length}) must equal NodeCount + 1 ({idMap.Count + 1})");
        }

        if (columnIndices.Length != weights.Length)
        {
            throw new ArgumentException($"ColumnIndices length ({columnIndices.Length}) must match Weights length ({weights.Length})");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GetNeighbors(int nodeIndex, out ReadOnlySpan<int> neighbors, out ReadOnlySpan<ushort> weights)
    {
        if ((uint)nodeIndex >= (uint)NodeCount)
        {
            neighbors = ReadOnlySpan<int>.Empty;
            weights = ReadOnlySpan<ushort>.Empty;
            return;
        }

        long start = _rowOffsets[nodeIndex];
        long end = _rowOffsets[nodeIndex + 1];
        int count = (int)(end - start);

        if (count == 0)
        {
            neighbors = ReadOnlySpan<int>.Empty;
            weights = ReadOnlySpan<ushort>.Empty;
            return;
        }

        neighbors = _columnIndices.AsSpan((int)start, count);
        weights = _weights.AsSpan((int)start, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetDegree(int nodeIndex)
    {
        if ((uint)nodeIndex >= (uint)NodeCount) return 0;
        return (int)(_rowOffsets[nodeIndex + 1] - _rowOffsets[nodeIndex]);
    }

    /// <summary>
    /// Serializes CSR data and ID map into the target directory as flat binary files.
    /// </summary>
    public void SaveToDirectory(string dirPath, string? sourceInfo = null)
    {
        Directory.CreateDirectory(dirPath);

        string idMapPath = Path.Combine(dirPath, "id_map.bin");
        string offsetsPath = Path.Combine(dirPath, "offsets.bin");
        string neighborsPath = Path.Combine(dirPath, "neighbors.bin");
        string weightsPath = Path.Combine(dirPath, "weights.bin");
        string metadataPath = Path.Combine(dirPath, "metadata.json");

        WriteArray(idMapPath, _idMap.RawArray);
        WriteArray(offsetsPath, _rowOffsets);
        WriteArray(neighborsPath, _columnIndices);
        WriteArray(weightsPath, _weights);

        var metadata = new GraphMetadata
        {
            Version = 1,
            NodeCount = NodeCount,
            EdgeCount = EdgeCount,
            CreatedAtUtc = DateTime.UtcNow,
            SourceInfo = sourceInfo ?? "FruitFly Connectome Precomputer",
            IdMapFile = "id_map.bin",
            OffsetsFile = "offsets.bin",
            NeighborsFile = "neighbors.bin",
            WeightsFile = "weights.bin",
            MemorySizeBytes = MemorySizeBytes
        };

        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Loads CSR data and ID map from precomputed flat binary files.
    /// Direct buffer reading achieves multi-gigabyte/sec throughput.
    /// </summary>
    public static CompactCsrGraph LoadFromDirectory(string dirPath)
    {
        string metadataPath = Path.Combine(dirPath, "metadata.json");
        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException($"Graph metadata not found at: {metadataPath}");
        }

        var json = File.ReadAllText(metadataPath);
        var metadata = JsonSerializer.Deserialize<GraphMetadata>(json)
            ?? throw new InvalidOperationException("Failed to deserialize graph metadata.");

        string idMapPath = Path.Combine(dirPath, metadata.IdMapFile);
        string offsetsPath = Path.Combine(dirPath, metadata.OffsetsFile);
        string neighborsPath = Path.Combine(dirPath, metadata.NeighborsFile);
        string weightsPath = Path.Combine(dirPath, metadata.WeightsFile);

        var idMapArray = ReadArray<ulong>(idMapPath, metadata.NodeCount);
        var offsets = ReadArray<long>(offsetsPath, metadata.NodeCount + 1);
        var neighbors = ReadArray<int>(neighborsPath, (int)metadata.EdgeCount);
        var weights = ReadArray<ushort>(weightsPath, (int)metadata.EdgeCount);

        var idMap = new NeuronIdMap(idMapArray);
        return new CompactCsrGraph(idMap, offsets, neighbors, weights);
    }

    private static void WriteArray<T>(string filePath, T[] array) where T : unmanaged
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
        var byteSpan = MemoryMarshal.AsBytes(array.AsSpan());
        fs.Write(byteSpan);
    }

    private static T[] ReadArray<T>(string filePath, int elementCount) where T : unmanaged
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Required binary file missing: {filePath}");
        }

        var array = GC.AllocateUninitializedArray<T>(elementCount);
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        var byteSpan = MemoryMarshal.AsBytes(array.AsSpan());
        int totalRead = 0;
        while (totalRead < byteSpan.Length)
        {
            int read = fs.Read(byteSpan[totalRead..]);
            if (read == 0) break;
            totalRead += read;
        }

        if (totalRead != byteSpan.Length)
        {
            throw new InvalidDataException($"Expected {byteSpan.Length} bytes in {filePath}, read {totalRead} bytes.");
        }

        return array;
    }

    public void Dispose()
    {
        // Arrays are managed memory collected by GC when dereferenced.
    }
}

public sealed class GraphMetadata
{
    public int Version { get; set; } = 1;
    public int NodeCount { get; set; }
    public long EdgeCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string SourceInfo { get; set; } = string.Empty;
    public string IdMapFile { get; set; } = "id_map.bin";
    public string OffsetsFile { get; set; } = "offsets.bin";
    public string NeighborsFile { get; set; } = "neighbors.bin";
    public string WeightsFile { get; set; } = "weights.bin";
    public long MemorySizeBytes { get; set; }
}
