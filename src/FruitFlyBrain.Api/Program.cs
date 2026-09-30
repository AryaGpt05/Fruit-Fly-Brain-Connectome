using System.Diagnostics;
using FruitFlyBrain.Engine.Data;
using FruitFlyBrain.Engine.Graph;
using FruitFlyBrain.Engine.Traversal;

var builder = WebApplication.CreateBuilder(args);

// Enable CORS for web clients
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddOpenApi();

// Discover precomputed graph directory
string precomputedDir = FindPrecomputedDirectory();
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine($"[Startup] Loading precomputed CSR connectome from: '{precomputedDir}'");
Console.ResetColor();

CompactCsrGraph graph;
var swBoot = Stopwatch.StartNew();

if (Directory.Exists(precomputedDir) && File.Exists(Path.Combine(precomputedDir, "metadata.json")))
{
    graph = CompactCsrGraph.LoadFromDirectory(precomputedDir);
    swBoot.Stop();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[Startup] Graph loaded in {swBoot.ElapsedMilliseconds} ms!");
    Console.WriteLine($"  - Neurons:  {graph.NodeCount:N0}");
    Console.WriteLine($"  - Synapses: {graph.EdgeCount:N0}");
    Console.WriteLine($"  - In-Memory: {graph.MemorySizeBytes / (1024.0 * 1024.0):F2} MB");
    Console.ResetColor();
}
else
{
    swBoot.Stop();
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"[Startup] No precomputed data found. Generating fallback synthetic connectome (50k nodes)...");
    graph = SyntheticConnectomeGenerator.Generate(50_000, averageDegree: 20);
    Console.ResetColor();
}

builder.Services.AddSingleton<INeuronGraph>(graph);
builder.Services.AddSingleton<BfsTraceEngine>();

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Endpoint: Graph Statistics & Sample Neurons
app.MapGet("/api/graph/stats", (INeuronGraph g) =>
{
    var sampleNeurons = new List<SampleNeuronInfo>();
    var random = new Random(42);

    // Find top hub neuron
    int maxDeg = 0;
    int maxDegIdx = 0;
    for (int i = 0; i < Math.Min(g.NodeCount, 5000); i++)
    {
        int deg = g.GetDegree(i);
        if (deg > maxDeg)
        {
            maxDeg = deg;
            maxDegIdx = i;
        }
    }
    sampleNeurons.Add(new SampleNeuronInfo
    {
        NeuronId = g.IdMap.GetId(maxDegIdx),
        Degree = maxDeg,
        Label = "Top Connectome Hub Neuron"
    });

    // Pick 5 random active neurons
    for (int i = 0; i < 5; i++)
    {
        int idx = random.Next(g.NodeCount);
        sampleNeurons.Add(new SampleNeuronInfo
        {
            NeuronId = g.IdMap.GetId(idx),
            Degree = g.GetDegree(idx),
            Label = $"Sample Neuron #{i + 1}"
        });
    }

    return Results.Ok(new
    {
        nodeCount = g.NodeCount,
        edgeCount = g.EdgeCount,
        memorySizeBytes = g.MemorySizeBytes,
        memorySizeMb = Math.Round(g.MemorySizeBytes / (1024.0 * 1024.0), 2),
        sampleNeurons = sampleNeurons
    });
})
.WithName("GetGraphStats")
.WithDescription("Returns total neuron and synapse counts, RAM footprint, and sample neuron IDs");

// Endpoint: Trace Activation Cascade via BFS
app.MapPost("/api/trace/bfs", (BfsTraceRequest req, BfsTraceEngine engine) =>
{
    if (req.SourceNeuronId == 0)
    {
        return Results.BadRequest(new { error = "SourceNeuronId must be greater than 0" });
    }

    var result = engine.Trace(req);
    return Results.Ok(result);
})
.WithName("TraceNeuronActivation")
.WithDescription("Performs zero-allocation BFS to trace all downstream neurons activated from a source neuron");

// Endpoint: Get Single Neuron Details & Neighbors
app.MapGet("/api/neurons/{id}", (string id, INeuronGraph g) =>
{
    if (!ulong.TryParse(id, out var neuronId))
    {
        return Results.BadRequest(new { error = $"Invalid neuron ID '{id}'." });
    }

    int idx = g.IdMap.TryGetIndex(neuronId);
    if (idx < 0)
    {
        return Results.NotFound(new { error = $"Neuron ID '{id}' not found in connectome." });
    }

    g.GetNeighbors(idx, out var neighbors, out var weights);

    var partners = new List<object>(neighbors.Length);
    for (int i = 0; i < neighbors.Length; i++)
    {
        partners.Add(new
        {
            targetNeuronId = g.IdMap.GetId(neighbors[i]).ToString(),
            synapseWeight = weights[i]
        });
    }

    return Results.Ok(new
    {
        neuronId = neuronId.ToString(),
        denseIndex = idx,
        outDegree = neighbors.Length,
        outgoingConnections = partners
    });
})
.WithName("GetNeuronDetails")
.WithDescription("Returns immediate synaptic targets and connection strengths for a specific neuron");

// Endpoint: Latency Benchmark
app.MapPost("/api/benchmark", (INeuronGraph g, BfsTraceEngine engine) =>
{
    const int Iterations = 50;
    var random = new Random(1337);
    var latenciesUs = new double[Iterations];
    int totalVisitedSum = 0;

    // Warm-up
    for (int i = 0; i < 5; i++)
    {
        int idx = random.Next(g.NodeCount);
        engine.Trace(new BfsTraceRequest { SourceNeuronId = g.IdMap.GetId(idx), MaxDepth = 3, MinSynapseWeight = 2, MaxResults = 1000 });
    }

    // Benchmark loop
    for (int i = 0; i < Iterations; i++)
    {
        int idx = random.Next(g.NodeCount);
        var res = engine.Trace(new BfsTraceRequest
        {
            SourceNeuronId = g.IdMap.GetId(idx),
            MaxDepth = 3,
            MinSynapseWeight = 2,
            MaxResults = 1500
        });
        latenciesUs[i] = res.ElapsedMicroseconds;
        totalVisitedSum += res.TotalVisited;
    }

    Array.Sort(latenciesUs);

    return Results.Ok(new
    {
        benchmark = "BFS Activation Cascade (Depth 3, MinWeight 2)",
        iterations = Iterations,
        avgNodesVisited = totalVisitedSum / Iterations,
        p50Us = Math.Round(latenciesUs[Iterations / 2], 2),
        p95Us = Math.Round(latenciesUs[(int)(Iterations * 0.95)], 2),
        p99Us = Math.Round(latenciesUs[(int)(Iterations * 0.99)], 2),
        p50Ms = Math.Round(latenciesUs[Iterations / 2] / 1000.0, 3),
        p95Ms = Math.Round(latenciesUs[(int)(Iterations * 0.95)] / 1000.0, 3),
        minUs = Math.Round(latenciesUs[0], 2),
        maxUs = Math.Round(latenciesUs[^1], 2)
    });
})
.WithName("RunBfsBenchmark")
.WithDescription("Runs a multi-iteration BFS benchmark and returns latency percentiles (p50, p95, p99)");

app.Run();

static string FindPrecomputedDirectory()
{
    string[] candidates = [
        Path.Combine(Directory.GetCurrentDirectory(), "precomputed"),
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "precomputed"),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "precomputed")),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "precomputed")),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "precomputed")),
    ];

    foreach (var dir in candidates)
    {
        if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "metadata.json")))
        {
            return dir;
        }
    }

    return Path.Combine(Directory.GetCurrentDirectory(), "precomputed");
}

sealed class SampleNeuronInfo
{
    [System.Text.Json.Serialization.JsonNumberHandling(
        System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString |
        System.Text.Json.Serialization.JsonNumberHandling.WriteAsString)]
    public ulong NeuronId { get; set; }
    public int Degree { get; set; }
    public string Label { get; set; } = string.Empty;
}
