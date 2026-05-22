using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using TUnit.Mutation.Driver.Compilation;
using TUnit.Mutation.Driver.Coverage;
using TUnit.Mutation.Driver.Execution;
using TUnit.Mutation.Driver.Mutators;
using TUnit.Mutation.Driver.Reporting;
using TUnit.Mutation.Driver.Runtime;
using TUnit.Mutation.Driver.Scheduling;
using TUnit.Mutation.Driver.Workspace;

namespace TUnit.Mutation.Driver;

public sealed record OrchestratorOptions(
    string TestProject,
    string UnderTestProject,
    string Configuration,
    string WorkDirectory,
    int MaxBatchSize,
    bool Strict,
    int MaxParallel,
    int WarmPoolSize,
    int BatchTimeoutSeconds);

public sealed class Orchestrator
{
    private readonly OrchestratorOptions _opt;

    public Orchestrator(OrchestratorOptions options) => _opt = options;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_opt.WorkDirectory);
        var sw = Stopwatch.StartNew();

        // 1. Drop the runtime source into the under-test project dir so MSBuild and our Roslyn
        //    compilation both include the same MutationRuntime type.
        var underTestProjDir = Path.GetDirectoryName(_opt.UnderTestProject)!;
        var runtimeDropDir = Path.Combine(underTestProjDir, "TUnitMutation");
        Directory.CreateDirectory(runtimeDropDir);
        var runtimeDropPath = Path.Combine(runtimeDropDir, MutationRuntimeSource.FileName);
        await File.WriteAllTextAsync(runtimeDropPath, MutationRuntimeSource.Code, ct);

        // 2. Restore so MSBuildWorkspace can resolve metadata references.
        Log("Restoring test project…");
        var testProjDirEarly = Path.GetDirectoryName(_opt.TestProject)!;
        if (await ShellAsync("dotnet", $"restore \"{_opt.TestProject}\" --nologo -v:quiet", testProjDirEarly, ct) != 0)
            return Fail("Restore failed.");

        // 3. Load projects.
        Log($"Loading workspace ({_opt.Configuration})…");
        using var loader = new ProjectLoader(_opt.Configuration);
        var underTest = await loader.LoadAsync(_opt.UnderTestProject, ct);
        var testProject = await loader.LoadAsync(_opt.TestProject, ct);
        var underTestOutput = underTest.OutputFilePath
            ?? throw new InvalidOperationException("Under-test project has no OutputFilePath.");
        var testOutput = testProject.OutputFilePath
            ?? throw new InvalidOperationException("Test project has no OutputFilePath.");
        var underTestAsmFile = Path.GetFileName(underTestOutput);

        // 2. Mutate + emit under-test assembly to a tmp path.
        Log("Mutating under-test compilation…");
        var mutatedDir = Path.Combine(_opt.WorkDirectory, "mutated");
        var mutatedPath = Path.Combine(mutatedDir, underTestAsmFile);
        var emit = await MutatedAssemblyEmitter.EmitAsync(underTest, mutatedPath, ct);
        Log($"  → emitted {emit.Mutations.Count} mutants to {mutatedPath}");
        await File.WriteAllLinesAsync(
            Path.Combine(_opt.WorkDirectory, "mutations.txt"),
            emit.Mutations.Select(m =>
                $"#{m.Id} {Path.GetFileName(m.FilePath)}:{m.Line} {m.Operator}  '{m.OriginalText}' → '{m.MutatedText}'"),
            ct);

        // 3. Drop coverage hooks file into the test project directory; build test project.
        var testProjDir = Path.GetDirectoryName(_opt.TestProject)!;
        var hooksDir = Path.Combine(testProjDir, "TUnitMutation");
        Directory.CreateDirectory(hooksDir);
        var hooksFile = Path.Combine(hooksDir, CoverageHooksSource.FileName);
        await File.WriteAllTextAsync(hooksFile, CoverageHooksSource.Code, ct);

        Log("Building test project…");
        if (await ShellAsync("dotnet", $"build \"{_opt.TestProject}\" -c {_opt.Configuration} --nologo -v:minimal", testProjDir, ct) != 0)
            return Fail("Test project build failed.");

        // 4. Overwrite the project-referenced under-test dll inside the test bin with our mutated copy.
        var testBinDir = Path.GetDirectoryName(testOutput)!;
        File.Copy(mutatedPath, Path.Combine(testBinDir, underTestAsmFile), overwrite: true);
        var mutatedPdb = Path.ChangeExtension(mutatedPath, ".pdb");
        if (File.Exists(mutatedPdb))
            File.Copy(mutatedPdb, Path.Combine(testBinDir, Path.GetFileName(mutatedPdb)), overwrite: true);
        Log($"  → swapped mutated {underTestAsmFile} into {testBinDir}");

        // 5. Coverage run.
        var coveragePath = Path.Combine(_opt.WorkDirectory, "coverage.jsonl");
        if (File.Exists(coveragePath)) File.Delete(coveragePath);
        var host = new TestProcessHost(testOutput, testBinDir);
        Log("Running coverage pass…");
        var covRun = await host.RunAsync(
            new Dictionary<string, string?>
            {
                ["MUTATION_COVERAGE"]      = "1",
                ["MUTATION_COVERAGE_FILE"] = coveragePath,
                ["MUTATION_ACTIVE"]        = null,
            },
            trxFileName: "coverage.trx",
            ct,
            extraArgs: ["--maximum-parallel-tests", "1"]);
        Log($"  → coverage run exit={covRun.ExitCode}, {covRun.Results.Count} tests");
        if (covRun.ExitCode != 0 || !File.Exists(coveragePath))
        {
            Console.Error.WriteLine("---- coverage run stdout ----");
            Console.Error.WriteLine(covRun.StdOut);
            Console.Error.WriteLine("---- coverage run stderr ----");
            Console.Error.WriteLine(covRun.StdErr);
            return Fail("Coverage run failed or produced no coverage data.");
        }
        var coverage = CoverageMap.FromJsonl(coveragePath);
        Log($"  → covered mutants: {coverage.TestsByMutant.Count} / {emit.Mutations.Count}");

        // 6. Split mutations by scope. Coverage attribution is only trustworthy for Instance-scope
        //    mutants. Static-init mutants run once at type init; whatever test happens to trigger
        //    the cctor records the hit, but logically *every* test depending on the initialized
        //    value is affected. Run them against the full suite, one mutant at a time.
        var instanceMutations = emit.Mutations.Where(m => m.Scope == MutationScope.Instance).ToList();
        var staticInitMutations = emit.Mutations.Where(m => m.Scope == MutationScope.StaticInit).ToList();
        Log($"  → scope split: {instanceMutations.Count} instance, {staticInitMutations.Count} static-init");

        var outcomes = new ConcurrentDictionary<int, MutantOutcome>();
        var coveredIds = new HashSet<int>(coverage.TestsByMutant.Keys);
        foreach (var m in instanceMutations)
        {
            if (!coveredIds.Contains(m.Id))
                outcomes[m.Id] = new MutantOutcome(m, MutantStatus.NoCoverage, null);
        }

        // 7. Optionally spin up a warm pool. Each worker is a long-lived test exe driven over
        //    MTP's JSON-RPC server mode. Active mutant set is swapped per batch via a per-worker
        //    file the in-process [BeforeEvery(Test)] hook re-reads on demand.
        WarmPool? pool = null;
        if (_opt.WarmPoolSize > 0)
        {
            Log($"Starting warm pool with {_opt.WarmPoolSize} worker(s)…");
            pool = new WarmPool(_opt.WarmPoolSize, testOutput, testBinDir,
                poolDir: Path.Combine(_opt.WorkDirectory, "pool"));
            await pool.StartAsync(ct);
        }

        var batchTimeout = TimeSpan.FromSeconds(_opt.BatchTimeoutSeconds);

        async Task<TestRunOutcome> RunOneAsync(string idsCsv, string? treeFilter, string trxName, CancellationToken innerCt)
        {
            if (pool is not null)
            {
                using var poolCts = CancellationTokenSource.CreateLinkedTokenSource(innerCt);
                poolCts.CancelAfter(batchTimeout);
                try { return await pool.RunBatchAsync(idsCsv, treeFilter, poolCts.Token); }
                catch (OperationCanceledException) when (poolCts.IsCancellationRequested && !innerCt.IsCancellationRequested)
                {
                    return new TestRunOutcome(ExitCode: -1, Results: [], StdOut: "", StdErr: "warm-worker timeout", TimedOut: true);
                }
            }
            return await host.RunAsync(
                new Dictionary<string, string?>
                {
                    ["MUTATION_ACTIVE"]        = idsCsv,
                    ["MUTATION_COVERAGE"]      = null,
                    ["MUTATION_COVERAGE_FILE"] = null,
                },
                trxFileName: trxName,
                innerCt,
                extraArgs: treeFilter is null ? null : ["--treenode-filter", treeFilter],
                timeout: batchTimeout);
        }

        // 8. Instance batches with coverage-directed scheduling — run in parallel.
        var batches = BatchPlanner.Plan(instanceMutations, coverage, _opt.MaxBatchSize);
        var effectiveParallel = pool is not null ? pool.Size : _opt.MaxParallel;
        Log($"Planned {batches.Count} non-overlapping instance batches (parallelism={effectiveParallel}).");

        var idToMutation = emit.Mutations.ToDictionary(m => m.Id);
        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = effectiveParallel,
            CancellationToken = ct,
        };
        var batchCounter = 0;
        await Parallel.ForEachAsync(batches, parallelOpts, async (batch, innerCt) =>
        {
            var thisIdx = Interlocked.Increment(ref batchCounter);
            var idsCsv = string.Join(',', batch.MutantIds);
            var coveringPaths = batch.MutantIds
                .SelectMany(id => coverage.PathsByMutant[id])
                .Distinct()
                .ToList();
            var treeFilter = TryBuildTreeFilter(coveringPaths);
            var filterTag = treeFilter is null ? " (no filter)" : "";
            Log($"  batch {thisIdx}/{batches.Count}: {batch.MutantIds.Count} mutants, {coveringPaths.Count} covering tests{filterTag}");
            var run = await RunOneAsync(idsCsv, treeFilter, $"batch-{thisIdx}.trx", innerCt);

            // If the whole batch timed out, we can't tell which mutant hung the suite. Conservatively
            // mark all mutants in the batch as Killed (timeout). Same convention Stryker uses.
            if (run.TimedOut)
            {
                foreach (var id in batch.MutantIds)
                {
                    outcomes[id] = new MutantOutcome(idToMutation[id], MutantStatus.Killed, "(timeout)");
                }
                return;
            }

            var failedTestNames = run.Results
                .Where(r => !r.Passed)
                .Select(r => r.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var id in batch.MutantIds)
            {
                var mutation = idToMutation[id];
                var coveringTests = coverage.TestsByMutant[id];
                var killedBy = coveringTests.FirstOrDefault(t => failedTestNames.Any(f =>
                    f.StartsWith(t, StringComparison.Ordinal) || t.EndsWith(f, StringComparison.Ordinal)));
                outcomes[id] = new MutantOutcome(
                    mutation,
                    killedBy is not null ? MutantStatus.Killed : MutantStatus.Survived,
                    killedBy);
            }
        });

        // 9. Static-init mutants need FRESH processes — their effect is computed once at type
        //    init. A warm worker has already initialized the type, so SetActive(staticInit_id)
        //    won't undo the cached value. Always use TestProcessHost for these.
        if (staticInitMutations.Count > 0)
            Log($"Running {staticInitMutations.Count} static-init mutant(s) against the full suite (fresh processes)…");
        await Parallel.ForEachAsync(staticInitMutations, parallelOpts, async (m, innerCt) =>
        {
            var run = await host.RunAsync(
                new Dictionary<string, string?>
                {
                    ["MUTATION_ACTIVE"]        = m.Id.ToString(),
                    ["MUTATION_COVERAGE"]      = null,
                    ["MUTATION_COVERAGE_FILE"] = null,
                    ["MUTATION_ACTIVE_FILE"]   = null,
                },
                trxFileName: $"static-{m.Id}.trx",
                innerCt,
                timeout: batchTimeout);
            if (run.TimedOut)
            {
                outcomes[m.Id] = new MutantOutcome(m, MutantStatus.Killed, "(timeout)");
                return;
            }
            var firstFailure = run.Results.FirstOrDefault(r => !r.Passed);
            outcomes[m.Id] = new MutantOutcome(
                m,
                firstFailure is not null ? MutantStatus.Killed : MutantStatus.Survived,
                firstFailure?.Name);
        });

        // 10. Optional --strict verification: re-run every Survived / NoCoverage instance mutant
        //    against the full suite. Catches false-survived caused by coverage gaps.
        if (_opt.Strict)
        {
            var suspects = outcomes.Values
                .Where(o => o.Mutation.Scope == MutationScope.Instance)
                .Where(o => o.Status is MutantStatus.Survived or MutantStatus.NoCoverage)
                .ToList();
            Log($"Strict pass: re-verifying {suspects.Count} suspect(s) against full suite (fresh processes)…");
            var strictCounter = 0;
            await Parallel.ForEachAsync(suspects, parallelOpts, async (s, innerCt) =>
            {
                var idx = Interlocked.Increment(ref strictCounter);
                Log($"  strict {idx}/{suspects.Count}: mutant #{s.Mutation.Id}");
                // Strict always uses fresh processes — that's the whole point of "strict".
                var run = await host.RunAsync(
                    new Dictionary<string, string?>
                    {
                        ["MUTATION_ACTIVE"]        = s.Mutation.Id.ToString(),
                        ["MUTATION_COVERAGE"]      = null,
                        ["MUTATION_COVERAGE_FILE"] = null,
                        ["MUTATION_ACTIVE_FILE"]   = null,
                    },
                    trxFileName: $"strict-{s.Mutation.Id}.trx",
                    innerCt,
                    timeout: batchTimeout);
                if (run.TimedOut)
                {
                    outcomes[s.Mutation.Id] = new MutantOutcome(s.Mutation, MutantStatus.Killed, "(timeout)");
                    return;
                }
                var firstFailure = run.Results.FirstOrDefault(r => !r.Passed);
                outcomes[s.Mutation.Id] = new MutantOutcome(
                    s.Mutation,
                    firstFailure is not null ? MutantStatus.Killed : MutantStatus.Survived,
                    firstFailure?.Name);
            });
        }

        if (pool is not null) await pool.DisposeAsync();

        ReportPrinter.Print(outcomes.Values.ToList());
        Log($"Done in {sw.Elapsed.TotalSeconds:F1}s. Artifacts: {_opt.WorkDirectory}");
        return 0;
    }

    /// Build a single TreeNodeFilter expression when all covering paths share an
    /// /Asm/Ns/Class prefix. TUnit's TreeNodeFilter only supports per-segment
    /// alternation `(m1|m2)`, not top-level path-OR, so cross-class batches return
    /// null and the orchestrator runs the full suite for that batch.
    private static string? TryBuildTreeFilter(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        var split = paths.Select(p => p.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToList();
        if (split.Any(s => s.Length < 2)) return null;
        var prefix = split[0][..^1];
        if (split.Any(s => s.Length != prefix.Length + 1 || !s[..^1].SequenceEqual(prefix)))
            return null;
        var methods = split.Select(s => s[^1]).Distinct();
        return "/" + string.Join("/", prefix) + "/(" + string.Join("|", methods) + ")";
    }

    private static void Log(string msg) => Console.WriteLine($"[tunit-mutate] {msg}");
    private static int Fail(string msg) { Console.Error.WriteLine($"[tunit-mutate] {msg}"); return 1; }

    private static async Task<int> ShellAsync(string file, string args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args) { WorkingDirectory = cwd, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }
}
