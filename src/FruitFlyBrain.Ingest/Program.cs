using System.Diagnostics;
using FruitFlyBrain.Engine.Data;
using FruitFlyBrain.Engine.Graph;

namespace FruitFlyBrain.Ingest;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("===============================================================");
        Console.WriteLine("   FRUIT FLY BRAIN CONNECTOME - CSR PRECOMPUTATION INGESTOR    ");
        Console.WriteLine("===============================================================");
        Console.ResetColor();

        string? inputPath = GetArg(args, "--input");
        string outputPath = GetArg(args, "--output") ?? Path.Combine(Directory.GetCurrentDirectory(), "precomputed");
        string? preCol = GetArg(args, "--pre-col");
        string? postCol = GetArg(args, "--post-col");
        string? weightCol = GetArg(args, "--weight-col");
        bool isSynthetic = HasFlag(args, "--synthetic");
        string? syntheticCountStr = GetArg(args, "--synthetic-nodes");

        // If no input specified and not synthetic, look for any .feather or .csv in current and parent directories
        if (string.IsNullOrEmpty(inputPath) && !isSynthetic)
        {
            var detectedFile = FindConnectomeFile();
            if (detectedFile != null)
            {
                Console.WriteLine($"[Auto-Discovery] Detected connectome file: {detectedFile}");
                inputPath = detectedFile;
            }
        }

        if (string.IsNullOrEmpty(inputPath) && !isSynthetic)
        {
            Console.WriteLine("\nUsage Instructions:");
            Console.WriteLine("  dotnet run --project src/FruitFlyBrain.Ingest -- [options]");
            Console.WriteLine("\nOptions:");
            Console.WriteLine("  --input <file.feather>       Path to .feather or .csv connectome file");
            Console.WriteLine("  --output <dir>               Target output directory (default: ./precomputed)");
            Console.WriteLine("  --pre-col <name>             Pre-synaptic ID column name (auto-detected if omitted)");
            Console.WriteLine("  --post-col <name>            Post-synaptic ID column name (auto-detected if omitted)");
            Console.WriteLine("  --weight-col <name>          Synapse weight column name (auto-detected if omitted)");
            Console.WriteLine("  --synthetic                  Generate synthetic connectome for testing");
            Console.WriteLine("  --synthetic-nodes <N>        Node count for synthetic benchmark (default: 100,000)");
            Console.WriteLine("\n[Notice] No input file specified. Generating a 100,000-neuron synthetic benchmark dataset so you can run the API right away...\n");
            isSynthetic = true;
        }

        var sw = Stopwatch.StartNew();

        try
        {
            CompactCsrGraph graph;

            if (isSynthetic)
            {
                int nodeCount = 100_000;
                if (!string.IsNullOrEmpty(syntheticCountStr) && int.TryParse(syntheticCountStr, out int parsed))
                {
                    nodeCount = parsed;
                }

                Console.WriteLine($"[Synthetic Generator] Generating connectome with {nodeCount:N0} neurons...");
                graph = SyntheticConnectomeGenerator.Generate(nodeCount, averageDegree: 25);
                Console.WriteLine($"[Synthetic Generator] Generated {graph.NodeCount:N0} nodes, {graph.EdgeCount:N0} edges.");
            }
            else
            {
                if (inputPath!.EndsWith(".feather", StringComparison.OrdinalIgnoreCase))
                {
                    graph = FeatherConnectomeParser.ParseFeather(inputPath, preCol, postCol, weightCol);
                }
                else
                {
                    throw new NotSupportedException($"Unsupported file format: {Path.GetExtension(inputPath)}. Please provide a .feather file.");
                }
            }

            // Ensure destination path exists
            outputPath = Path.GetFullPath(outputPath);
            Console.WriteLine($"\n[Storage] Serializing binary CSR files to '{outputPath}'...");
            graph.SaveToDirectory(outputPath, sourceInfo: isSynthetic ? "Synthetic Connectome Benchmark" : Path.GetFileName(inputPath));

            sw.Stop();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[SUCCESS] Precomputation completed in {sw.Elapsed.TotalSeconds:F2} seconds!");
            Console.WriteLine($"  - Total Neurons:  {graph.NodeCount:N0}");
            Console.WriteLine($"  - Total Synapses: {graph.EdgeCount:N0}");
            Console.WriteLine($"  - In-Memory Size: {graph.MemorySizeBytes / (1024.0 * 1024.0):F2} MB");
            Console.WriteLine($"  - Output Folder:  {outputPath}");
            Console.ResetColor();

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[ERROR] Precomputation failed: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
            return 1;
        }
    }

    private static string? FindConnectomeFile()
    {
        string[] searchDirs = [
            Directory.GetCurrentDirectory(),
            AppDomain.CurrentDomain.BaseDirectory,
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../.."))
        ];

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            var featherFiles = Directory.GetFiles(dir, "*.feather", SearchOption.TopDirectoryOnly);
            if (featherFiles.Length > 0) return featherFiles[0];
        }

        return null;
    }

    private static string? GetArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        return args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    }
}
