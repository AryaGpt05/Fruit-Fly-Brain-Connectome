using FruitFlyBrain.Engine.Graph;

namespace FruitFlyBrain.Engine.Data;

/// <summary>
/// Generates realistic synthetic connectome networks for benchmarking and verification.
/// Simulates biological power-law synaptic distributions.
/// </summary>
public static class SyntheticConnectomeGenerator
{
    private const ulong FlyWireIdBase = 720575940600000000UL;

    public static CompactCsrGraph Generate(int nodeCount, int averageDegree = 20, int seed = 42)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(averageDegree);

        var random = new Random(seed);
        var sortedIds = new ulong[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            sortedIds[i] = FlyWireIdBase + (ulong)i;
        }
        var idMap = new NeuronIdMap(sortedIds);

        // Precompute degrees using a power-law / Pareto-like distribution
        var degrees = new int[nodeCount];
        long totalEdges = 0;

        for (int i = 0; i < nodeCount; i++)
        {
            // Pareto-like degree: most neurons have 5-30 partners, some hubs have hundreds
            double u = random.NextDouble();
            int deg = Math.Max(1, (int)(averageDegree * 0.4 / Math.Pow(1.0 - (u * 0.95), 0.5)));
            deg = Math.Min(deg, Math.Min(500, nodeCount - 1));
            degrees[i] = deg;
            totalEdges += deg;
        }

        var rowOffsets = new long[nodeCount + 1];
        rowOffsets[0] = 0;
        for (int i = 0; i < nodeCount; i++)
        {
            rowOffsets[i + 1] = rowOffsets[i] + degrees[i];
        }

        var columnIndices = new int[totalEdges];
        var weights = new ushort[totalEdges];

        long edgeWriteIdx = 0;
        var picked = new HashSet<int>();

        for (int u = 0; u < nodeCount; u++)
        {
            picked.Clear();
            picked.Add(u); // No self-loops

            int deg = degrees[u];
            for (int e = 0; e < deg; e++)
            {
                int target;
                int attempts = 0;
                do
                {
                    // Locality bias: biological connectomes have high clustering coefficient
                    if (random.NextDouble() < 0.7 && nodeCount > 50)
                    {
                        int localWindow = Math.Min(nodeCount, 200);
                        int offset = random.Next(-localWindow / 2, localWindow / 2);
                        target = Math.Clamp(u + offset, 0, nodeCount - 1);
                    }
                    else
                    {
                        target = random.Next(nodeCount);
                    }
                    attempts++;
                } while (picked.Contains(target) && attempts < 10);

                if (picked.Contains(target))
                {
                    target = (u + e + 1) % nodeCount;
                }

                picked.Add(target);

                columnIndices[edgeWriteIdx] = target;
                // Realistic synapse counts (log-normal distribution between 1 and 40)
                ushort weight = (ushort)Math.Clamp((int)Math.Exp(random.NextDouble() * 2.5), 1, 100);
                weights[edgeWriteIdx] = weight;
                edgeWriteIdx++;
            }
        }

        return new CompactCsrGraph(idMap, rowOffsets, columnIndices, weights);
    }
}
