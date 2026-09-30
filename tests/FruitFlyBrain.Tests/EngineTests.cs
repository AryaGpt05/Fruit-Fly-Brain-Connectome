using FruitFlyBrain.Engine.Data;
using FruitFlyBrain.Engine.Graph;
using FruitFlyBrain.Engine.Traversal;
using Xunit;

namespace FruitFlyBrain.Tests;

public class EngineTests
{
    [Fact]
    public void FastBitSet_ShouldTrackVisitedBitsAccurately()
    {
        var bitset = new FastBitSet(100_000);

        Assert.False(bitset.IsSet(42));
        Assert.True(bitset.TestAndSet(42));
        Assert.True(bitset.IsSet(42));
        Assert.False(bitset.TestAndSet(42)); // Second time should return false

        Assert.True(bitset.TestAndSet(99_999));
        Assert.True(bitset.IsSet(99_999));

        // Selective clear
        bitset.ClearSelective(new[] { 42 });
        Assert.False(bitset.IsSet(42));
        Assert.True(bitset.IsSet(99_999)); // Other bit remains intact

        // Full clear
        bitset.ClearAll();
        Assert.False(bitset.IsSet(99_999));
    }

    [Fact]
    public void FastBitSet_ShouldScaleTo50MillionBits()
    {
        const int FiftyMillion = 50_000_000;
        var bitset = new FastBitSet(FiftyMillion);

        Assert.True(bitset.TestAndSet(0));
        Assert.True(bitset.TestAndSet(25_000_000));
        Assert.True(bitset.TestAndSet(49_999_999));

        Assert.False(bitset.TestAndSet(25_000_000));
        Assert.True(bitset.IsSet(49_999_999));
        Assert.False(bitset.IsSet(12_345));

        bitset.ClearSelective(new[] { 25_000_000 });
        Assert.False(bitset.IsSet(25_000_000));
    }

    [Fact]
    public void NeuronIdMap_ShouldMapIdsCorrectly()
    {
        ulong[] ids = [100, 250, 500, 720575940600000001UL, 720575940600000099UL];
        var mapper = new NeuronIdMap(ids);

        Assert.Equal(5, mapper.Count);
        Assert.Equal(0, mapper.TryGetIndex(100));
        Assert.Equal(3, mapper.TryGetIndex(720575940600000001UL));
        Assert.Equal(-1, mapper.TryGetIndex(999));

        Assert.Equal(ids[3], mapper.GetId(3));
    }

    [Fact]
    public void CompactCsrGraph_SaveAndLoad_ShouldPreserveIntegrity()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "FruitFlyBrain_Test_" + Guid.NewGuid());
        try
        {
            var original = SyntheticConnectomeGenerator.Generate(nodeCount: 500, averageDegree: 15);
            original.SaveToDirectory(tempDir, "Unit Test Graph");

            var loaded = CompactCsrGraph.LoadFromDirectory(tempDir);

            Assert.Equal(original.NodeCount, loaded.NodeCount);
            Assert.Equal(original.EdgeCount, loaded.EdgeCount);

            for (int i = 0; i < original.NodeCount; i++)
            {
                original.GetNeighbors(i, out var origNeighbors, out var origWeights);
                loaded.GetNeighbors(i, out var loadedNeighbors, out var loadedWeights);

                Assert.True(origNeighbors.SequenceEqual(loadedNeighbors));
                Assert.True(origWeights.SequenceEqual(loadedWeights));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void BfsTraceEngine_ShouldTraverseCascadesCorrectly()
    {
        // Construct a simple DAG:
        // Root (10) -> (20, w: 5), (30, w: 1)
        // (20) -> (40, w: 10)
        // (30) -> (50, w: 2)
        ulong[] ids = [10, 20, 30, 40, 50];
        var idMap = new NeuronIdMap(ids);

        // Indices: 0: 10, 1: 20, 2: 30, 3: 40, 4: 50
        long[] offsets = [0, 2, 3, 4, 4, 4];
        int[] neighbors = [1, 2, 3, 4];
        ushort[] weights = [5, 1, 10, 2];

        var graph = new CompactCsrGraph(idMap, offsets, neighbors, weights);
        var engine = new BfsTraceEngine(graph);

        // Trace from 10 with MinWeight 1, Depth 2
        var res1 = engine.Trace(new BfsTraceRequest
        {
            SourceNeuronId = 10,
            MaxDepth = 2,
            MinSynapseWeight = 1
        });

        Assert.True(res1.FoundSource);
        Assert.Equal(5, res1.TotalVisited); // 10, 20, 30, 40, 50
        Assert.Equal(2, res1.MaxDepthReached);

        // Trace from 10 with MinWeight 2 (filters out 10 -> 30 which has w: 1)
        var res2 = engine.Trace(new BfsTraceRequest
        {
            SourceNeuronId = 10,
            MaxDepth = 2,
            MinSynapseWeight = 2
        });

        Assert.Equal(3, res2.TotalVisited); // 10, 20, 40
        Assert.DoesNotContain(res2.ActivatedNeurons, n => n.NeuronId == 30);
        Assert.DoesNotContain(res2.ActivatedNeurons, n => n.NeuronId == 50);

        // Trace with Depth 1
        var res3 = engine.Trace(new BfsTraceRequest
        {
            SourceNeuronId = 10,
            MaxDepth = 1,
            MinSynapseWeight = 1
        });

        Assert.Equal(3, res3.TotalVisited); // 10, 20, 30
    }
}
