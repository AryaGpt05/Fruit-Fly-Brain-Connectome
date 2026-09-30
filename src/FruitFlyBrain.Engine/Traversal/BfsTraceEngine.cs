using System.Diagnostics;
using FruitFlyBrain.Engine.Graph;

namespace FruitFlyBrain.Engine.Traversal;

/// <summary>
/// Highly optimized BFS traversal engine for connectome graphs.
/// Employs FastBitSet visited tracking and selective reset for ultra-low latency.
/// </summary>
public sealed class BfsTraceEngine
{
    private readonly INeuronGraph _graph;

    public BfsTraceEngine(INeuronGraph graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
    }

    public BfsTraceResult Trace(BfsTraceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        int sourceIdx = _graph.IdMap.TryGetIndex(request.SourceNeuronId);
        if (sourceIdx < 0)
        {
            return new BfsTraceResult
            {
                SourceNeuronId = request.SourceNeuronId,
                FoundSource = false,
                TotalVisited = 0,
                MaxDepthReached = 0,
                ElapsedMicroseconds = 0,
                DepthHistogram = new Dictionary<int, int>(),
                ActivatedNeurons = Array.Empty<ActivatedNeuron>(),
                Edges = Array.Empty<TraceEdge>()
            };
        }

        var sw = Stopwatch.StartNew();

        int maxNodes = Math.Clamp(request.MaxResults, 1, _graph.NodeCount);
        int maxDepth = Math.Clamp(request.MaxDepth, 0, 30);
        ushort minWeight = request.MinSynapseWeight;

        // Dedicated visited bitset for capacity
        var bitset = new FastBitSet(_graph.NodeCount);

        // Preallocate working buffers
        var currentFrontier = new List<int>(256) { sourceIdx };
        var nextFrontier = new List<int>(512);
        var visitedIndices = new List<int>(Math.Min(maxNodes, 4096)) { sourceIdx };

        var activatedNeurons = new List<ActivatedNeuron>(Math.Min(maxNodes, 4096));
        var edges = new List<TraceEdge>(Math.Min(maxNodes * 2, 8192));
        var depthHistogram = new Dictionary<int, int> { [0] = 1 };

        // Mark source visited
        bitset.Set(sourceIdx);
        activatedNeurons.Add(new ActivatedNeuron
        {
            NeuronId = request.SourceNeuronId,
            Depth = 0,
            PredecessorId = 0,
            SynapseWeight = 0,
            ActivationScore = 1.0f
        });

        int currentDepth = 0;
        int maxDepthReached = 0;

        while (currentFrontier.Count > 0 && currentDepth < maxDepth && visitedIndices.Count < maxNodes)
        {
            currentDepth++;
            nextFrontier.Clear();
            int levelCount = 0;

            foreach (int u in currentFrontier)
            {
                if (visitedIndices.Count >= maxNodes) break;

                ulong uId = _graph.IdMap.GetId(u);
                _graph.GetNeighbors(u, out var neighbors, out var weights);

                for (int i = 0; i < neighbors.Length; i++)
                {
                    ushort w = weights[i];
                    if (w < minWeight) continue;

                    int v = neighbors[i];
                    ulong vId = _graph.IdMap.GetId(v);

                    // Test-and-set visited bit
                    if (bitset.TestAndSet(v))
                    {
                        visitedIndices.Add(v);
                        nextFrontier.Add(v);
                        levelCount++;

                        float decayedScore = (float)Math.Pow(0.85, currentDepth) * (Math.Min(w, (ushort)50) / 10.0f);

                        activatedNeurons.Add(new ActivatedNeuron
                        {
                            NeuronId = vId,
                            Depth = currentDepth,
                            PredecessorId = uId,
                            SynapseWeight = w,
                            ActivationScore = MathF.Round(decayedScore, 4)
                        });

                        edges.Add(new TraceEdge
                        {
                            SourceId = uId,
                            TargetId = vId,
                            Weight = w,
                            Depth = currentDepth
                        });

                        if (visitedIndices.Count >= maxNodes) break;
                    }
                }
            }

            if (levelCount > 0)
            {
                depthHistogram[currentDepth] = levelCount;
                maxDepthReached = currentDepth;
            }

            // Swap frontiers
            (currentFrontier, nextFrontier) = (nextFrontier, currentFrontier);
        }

        sw.Stop();
        double elapsedUs = sw.Elapsed.TotalMicroseconds;

        return new BfsTraceResult
        {
            SourceNeuronId = request.SourceNeuronId,
            FoundSource = true,
            TotalVisited = visitedIndices.Count,
            MaxDepthReached = maxDepthReached,
            ElapsedMicroseconds = elapsedUs,
            DepthHistogram = depthHistogram,
            ActivatedNeurons = activatedNeurons,
            Edges = edges
        };
    }
}
