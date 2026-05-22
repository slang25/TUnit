using System.Threading.Channels;

namespace TUnit.Mutation.Driver.Execution;

/// Manages N warm test-exe workers. Each batch grabs an idle worker off a channel, runs,
/// and returns it. Concurrency is naturally bounded to the worker count.
/// On worker timeout / cancellation, the worker is killed and replaced with a fresh one —
/// otherwise a hung test process (infinite-loop mutant) would poison subsequent batches.
public sealed class WarmPool : IAsyncDisposable
{
    private readonly string _testExecutable;
    private readonly string _workingDirectory;
    private readonly string _poolDir;
    private readonly object _workersLock = new();
    private readonly List<WarmWorker> _allWorkers = [];
    private readonly Channel<WarmWorker> _idle = Channel.CreateUnbounded<WarmWorker>();
    private int _nextWorkerId = 0;
    private readonly int _initialSize;

    public int Size => _initialSize;

    public WarmPool(int size, string testExecutable, string workingDirectory, string poolDir)
    {
        Directory.CreateDirectory(poolDir);
        _testExecutable = testExecutable;
        _workingDirectory = workingDirectory;
        _poolDir = poolDir;
        _initialSize = size;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var initialWorkers = new WarmWorker[_initialSize];
        for (var i = 0; i < _initialSize; i++)
        {
            initialWorkers[i] = NewWorker();
        }
        await Task.WhenAll(initialWorkers.Select(w => w.StartAsync(ct))).ConfigureAwait(false);
        foreach (var w in initialWorkers) await _idle.Writer.WriteAsync(w, ct).ConfigureAwait(false);
    }

    public async Task<TestRunOutcome> RunBatchAsync(string activeCsv, string? treeFilter, CancellationToken ct)
    {
        // Workers occasionally go silent (TUnit/MTP server-mode session lifetime limit).
        // Retry the batch on fresh workers up to N times before declaring the batch broken.
        const int maxAttempts = 3;
        TestRunOutcome lastOutcome = new(ExitCode: -1, Results: [], StdOut: "", StdErr: "no attempts", TimedOut: true);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var worker = await _idle.Reader.ReadAsync(ct).ConfigureAwait(false);
            TestRunOutcome outcome;
            try
            {
                outcome = await worker.RunBatchAsync(activeCsv, treeFilter, ct).ConfigureAwait(false);
            }
            catch
            {
                outcome = new TestRunOutcome(ExitCode: -1, Results: [], StdOut: "", StdErr: "throw", TimedOut: true);
            }

            if (worker.IsHealthy)
            {
                await _idle.Writer.WriteAsync(worker, ct).ConfigureAwait(false);
                return outcome;
            }

            // Worker is broken — dispose + respawn in background, retry on next iteration.
            _ = Task.Run(async () =>
            {
                try { await worker.DisposeAsync().ConfigureAwait(false); } catch { }
                try
                {
                    var fresh = NewWorker();
                    await fresh.StartAsync(CancellationToken.None).ConfigureAwait(false);
                    await _idle.Writer.WriteAsync(fresh, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }, CancellationToken.None);

            lastOutcome = outcome;
        }

        return lastOutcome;
    }

    private WarmWorker NewWorker()
    {
        int id;
        lock (_workersLock) id = _nextWorkerId++;
        var activePath = Path.Combine(_poolDir, $"worker-{id}.active");
        var w = new WarmWorker(id, _testExecutable, _workingDirectory, activePath);
        lock (_workersLock) _allWorkers.Add(w);
        return w;
    }

    public async ValueTask DisposeAsync()
    {
        _idle.Writer.TryComplete();
        WarmWorker[] snapshot;
        lock (_workersLock) snapshot = _allWorkers.ToArray();
        foreach (var w in snapshot)
        {
            try { await w.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }
}
