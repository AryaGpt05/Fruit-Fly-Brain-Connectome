namespace FruitFlyBrain.Engine.Traversal;

public sealed record BfsTraceRequest
{
    public required ulong SourceNeuronId { get; init; }
    public int MaxDepth { get; init; } = 3;
    public ushort MinSynapseWeight { get; init; } = 1;
    public int MaxResults { get; init; } = 2000;
}

public sealed record ActivatedNeuron
{
    public required ulong NeuronId { get; init; }
    public required int Depth { get; init; }
    public required ulong PredecessorId { get; init; }
    public required ushort SynapseWeight { get; init; }
    public required float ActivationScore { get; init; }
}

public sealed record TraceEdge
{
    public required ulong SourceId { get; init; }
    public required ulong TargetId { get; init; }
    public required ushort Weight { get; init; }
    public required int Depth { get; init; }
}

public sealed record BfsTraceResult
{
    public required ulong SourceNeuronId { get; init; }
    public required bool FoundSource { get; init; }
    public required int TotalVisited { get; init; }
    public required int MaxDepthReached { get; init; }
    public required double ElapsedMicroseconds { get; init; }
    public double ElapsedMilliseconds => ElapsedMicroseconds / 1000.0;
    public required IReadOnlyDictionary<int, int> DepthHistogram { get; init; }
    public required IReadOnlyList<ActivatedNeuron> ActivatedNeurons { get; init; }
    public required IReadOnlyList<TraceEdge> Edges { get; init; }
}
