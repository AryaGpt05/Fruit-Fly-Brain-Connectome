# 🧠 Fruit Fly Brain Connectome — Neural Cascade Tracer

High-performance .NET 10 web application for tracing neural activation cascades across the complete *Drosophila melanogaster* (fruit fly) brain connectome — **138,639 neurons** and **16.8 million synaptic connections**.

Given a neuron ID, the system performs **sub-millisecond BFS graph traversals** to identify all downstream activated neurons, synaptic weights, propagation depths, and cascade flow patterns.

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![Neurons](https://img.shields.io/badge/Neurons-138%2C639-10b981)
![Synapses](https://img.shields.io/badge/Synapses-16.8M-06b6d4)
![BFS p50](https://img.shields.io/badge/p50%20Latency-172µs-8b5cf6)

## Architecture

The system uses a **two-program precomputation model** optimized for 50M+ node scale:

1. **`FruitFlyBrain.Ingest`** — CLI tool that parses raw FlyWire `.feather` connectome data and precomputes a Compressed Sparse Row (CSR) graph into flat binary files
2. **`FruitFlyBrain.Api`** — ASP.NET Core Web API + interactive web dashboard that memory-loads the precomputed CSR in ~60ms and serves zero-allocation BFS queries

### Why CSR?

At 16.8M edges, a naive `Dictionary<long, List<long>>` graph would consume **~2 GB RAM** with constant GC pressure. Our CSR representation uses:
- `long[] RowOffsets` — 1.1 MB
- `int[] ColumnIndices` — 67 MB
- `ushort[] Weights` — 34 MB
- `FastBitSet` visited tracking — **0.017 MB** (138K bits)
- **Total: 98.5 MB** with **zero GC allocations** during traversal

## Quick Start

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- FlyWire connectome dataset (`proofread_connections_*.feather`)

### 1. Precompute the CSR Graph
```bash
# Place your .feather file in the project root, then:
dotnet run --project src/FruitFlyBrain.Ingest -- --input proofread_connections_783.feather --output precomputed
```

This parses the 812 MB `.feather` file in ~13 seconds and generates binary CSR files in `precomputed/`.

### 2. Run the Web API & Dashboard
```bash
dotnet run --project src/FruitFlyBrain.Api
```

Open **http://localhost:5000** in your browser.

### 3. Run Tests
```bash
dotnet test
```

## API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/graph/stats` | Neuron/synapse counts, RAM footprint, sample neuron IDs |
| `POST` | `/api/trace/bfs` | Trace downstream activation cascade from a source neuron |
| `GET` | `/api/neurons/{id}` | Get immediate synaptic targets for a specific neuron |
| `POST` | `/api/benchmark` | Run 50-iteration BFS benchmark with latency percentiles |

### Example: Trace a Neuron Cascade

```bash
curl -X POST http://localhost:5000/api/trace/bfs \
  -H "Content-Type: application/json" \
  -d '{"sourceNeuronId": 720575940606692542, "maxDepth": 3, "minSynapseWeight": 2, "maxResults": 1000}'
```

### Sample Neuron IDs to Try
| Neuron ID | Description | Out-Degree |
|-----------|-------------|------------|
| `720575940606692542` | Top Hub Neuron | 3,015 synapses |
| `720575940629245903` | Sample Neuron #1 | 313 synapses |
| `720575940614186415` | Sample Neuron #2 | 114 synapses |
| `720575940625653235` | Sample Neuron #4 | 58 synapses |

## Performance

Benchmark results on the full 138K-neuron, 16.8M-synapse connectome:

| Metric | Value |
|--------|-------|
| Graph load time | **59 ms** |
| BFS p50 latency | **172 µs** |
| BFS p95 latency | **730 µs** |
| Avg nodes visited (depth 3) | 1,440 |
| RAM footprint | 98.5 MB |

## Project Structure

```
├── src/
│   ├── FruitFlyBrain.Engine/      # Core: CSR graph, FastBitSet, BFS engine
│   ├── FruitFlyBrain.Ingest/      # CLI: .feather parser → binary CSR precomputer
│   └── FruitFlyBrain.Api/         # Web API + interactive dashboard (wwwroot/)
├── tests/
│   └── FruitFlyBrain.Tests/       # xUnit: BitSet, CSR, BFS correctness tests
└── precomputed/                   # Generated binary CSR files (gitignored)
```

## Data Source

This project uses the [FlyWire](https://flywire.ai/) whole-brain connectome of *Drosophila melanogaster*, which was publicly released as part of the [Dorkenwald et al. 2024](https://www.nature.com/articles/s41586-024-07558-y) paper.

## License

MIT
