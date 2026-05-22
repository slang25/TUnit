using Microsoft.Build.Locator;
using TUnit.Mutation.Driver;

if (!MSBuildLocator.IsRegistered)
{
    MSBuildLocator.RegisterDefaults();
}

string? testProject = null;
string? underTest = null;
var configuration = "Release";
string? workDir = null;
var maxBatchSize = 64;
var strict = false;
var maxParallel = Math.Max(1, Environment.ProcessorCount / 2);
var warmPool = 0;
var batchTimeoutSeconds = 30;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--test-project":     testProject   = Require(args, ++i); break;
        case "--under-test":       underTest     = Require(args, ++i); break;
        case "--configuration":    configuration = Require(args, ++i); break;
        case "--work-dir":         workDir       = Require(args, ++i); break;
        case "--max-batch-size":   maxBatchSize  = int.Parse(Require(args, ++i)); break;
        case "--max-parallel":     maxParallel   = Math.Max(1, int.Parse(Require(args, ++i))); break;
        case "--warm-pool":        warmPool      = Math.Max(0, int.Parse(Require(args, ++i))); break;
        case "--batch-timeout-seconds": batchTimeoutSeconds = Math.Max(1, int.Parse(Require(args, ++i))); break;
        case "--strict":           strict        = true; break;
        case "-h" or "--help":     Usage(); return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            Usage();
            return 2;
    }
}

if (testProject is null || underTest is null)
{
    Console.Error.WriteLine("--test-project and --under-test are required.");
    Usage();
    return 2;
}

var options = new OrchestratorOptions(
    TestProject: Path.GetFullPath(testProject),
    UnderTestProject: Path.GetFullPath(underTest),
    Configuration: configuration,
    WorkDirectory: workDir is null
        ? Path.Combine(Path.GetTempPath(), "tunit-mutate-" + Guid.NewGuid().ToString("N")[..8])
        : Path.GetFullPath(workDir),
    MaxBatchSize: maxBatchSize,
    Strict: strict,
    MaxParallel: maxParallel,
    WarmPoolSize: warmPool,
    BatchTimeoutSeconds: batchTimeoutSeconds);

return await new Orchestrator(options).RunAsync(CancellationToken.None);

static string Require(string[] args, int i)
{
    if (i >= args.Length) throw new ArgumentException("Missing value for argument.");
    return args[i];
}

static void Usage()
{
    Console.WriteLine("""
        Usage: tunit-mutate --test-project <path> --under-test <path> [options]

        Required:
          --test-project <path>     Path to the TUnit test project (.csproj).
          --under-test <path>       Path to the project-under-test (.csproj).

        Optional:
          --configuration <name>    Build configuration (default: Release).
          --work-dir <path>         Working directory for artifacts (default: tmp).
          --max-batch-size <n>      Max mutants per non-overlapping batch (default: 64).
          --max-parallel <n>        Max concurrent test processes (default: half of CPUs).
          --warm-pool <n>           Use N long-lived test processes driven over MTP's
                                    JSON-RPC server mode. Each batch becomes a runTests RPC
                                    call. Removes the per-batch startup tax. Overrides
                                    --max-parallel.
          --batch-timeout-seconds <n>  Wall-clock cap per batch / mutant run (default: 30).
                                    Catches infinite-loop mutants; the batch is killed and
                                    its mutants reported as Killed(timeout).
          --strict                  Re-verify every Survived/NoCoverage mutant against the
                                    full suite. Catches false-survived from coverage gaps
                                    (Lazy<T>, singletons, background threads).
        """);
}
