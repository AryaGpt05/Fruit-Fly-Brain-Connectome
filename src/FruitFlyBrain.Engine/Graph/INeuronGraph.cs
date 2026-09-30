namespace FruitFlyBrain.Engine.Graph;

/// <summary>
/// Interface for a high-performance connectome graph.
/// </summary>
public interface INeuronGraph
{
    int NodeCount { get; }
    long EdgeCount { get; }
    NeuronIdMap IdMap { get; }

    /// <summary>
    /// Gets neighbor node indices and corresponding synaptic weights for a dense node index.
    /// Zero allocations: slices directly into CSR arrays.
    /// </summary>
    void GetNeighbors(int nodeIndex, out ReadOnlySpan<int> neighbors, out ReadOnlySpan<ushort> weights);

    /// <summary>
    /// Out-degree (synaptic partner count) for the node.
    /// </summary>
    int GetDegree(int nodeIndex);

    /// <summary>
    /// Total memory footprint of the graph structure in bytes.
    /// </summary>
    long MemorySizeBytes { get; }
}
