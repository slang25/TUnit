using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using StreamJsonRpc;

namespace TUnit.Mutation.Driver.Execution;

/// One long-lived test-exe process driven over the MTP JSON-RPC server protocol.
/// MTP supports many `testing/runTests` calls in a single process — that's the warm pool primitive.
/// Active mutant set is swapped between batches by writing a small CSV file that the in-process
/// TUnit [BeforeEvery(Test)] hook reads and forwards to MutationRuntime.SetActive.
public sealed class WarmWorker : IAsyncDisposable
{
    private readonly string _testExecutable;
    private readonly string _workingDirectory;
    private readonly string _activeFilePath;
    private readonly int _id;

    private Process? _proc;
    private TcpListener? _listener;
    private TcpClient? _client;
    private JsonRpc? _rpc;
    private readonly ConcurrentDictionary<string, ConcurrentBag<TestResult>> _runResults = new();

    public WarmWorker(int id, string testExecutable, string workingDirectory, string activeFilePath)
    {
        _id = id;
        _testExecutable = testExecutable;
        _workingDirectory = workingDirectory;
        _activeFilePath = activeFilePath;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_activeFilePath)!);
        await File.WriteAllTextAsync(_activeFilePath, "", ct);

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        var fileName = _testExecutable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? "dotnet"
            : _testExecutable;
        var args = _testExecutable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? $"exec \"{_testExecutable}\" --server --client-host 127.0.0.1 --client-port {port}"
            : $"--server --client-host 127.0.0.1 --client-port {port}";

        var psi = new ProcessStartInfo(fileName, args)
        {
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["MUTATION_ACTIVE_FILE"] = _activeFilePath;
        _proc = Process.Start(psi)!;
        var pid = _proc.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                var s = await _proc.StandardOutput.ReadToEndAsync(ct);
                if (!string.IsNullOrWhiteSpace(s))
                    Console.Error.WriteLine($"[worker-{_id} stdout] {s.TrimEnd()}");
            }
            catch { }
        }, ct);
        _ = Task.Run(async () =>
        {
            try
            {
                var s = await _proc.StandardError.ReadToEndAsync(ct);
                if (!string.IsNullOrWhiteSpace(s))
                    Console.Error.WriteLine($"[worker-{_id} stderr] {s.TrimEnd()}");
            }
            catch { }
        }, ct);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(30));
        _client = await _listener.AcceptTcpClientAsync(connectCts.Token).ConfigureAwait(false);
        var stream = _client.GetStream();

        var formatter = new SystemTextJsonFormatter();
        var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);
        _rpc = new JsonRpc(handler);
        // Register notification handler. UseSingleObjectParameterDeserialization is essential —
        // without it StreamJsonRpc tries to name-bind params object members to method parameters
        // and rejects the handler.
        _rpc.AddLocalRpcMethod(
            handler: new Action<JsonElement>(OnTestUpdate).Method,
            target: this,
            methodRpcSettings: new JsonRpcMethodAttribute("testing/testUpdates/tests")
            {
                UseSingleObjectParameterDeserialization = true,
            });
        _rpc.StartListening();

        var initResp = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "initialize",
            new
            {
                processId = Environment.ProcessId,
                clientInfo = new { name = "tunit-mutate", version = "0.1.0" },
                capabilities = new { testing = new { debuggerProvider = false } },
            }).ConfigureAwait(false);
        _ = initResp;
    }

    public bool IsHealthy { get; private set; } = true;

    public async Task<TestRunOutcome> RunBatchAsync(
        string activeCsv,
        string? treeFilter,
        CancellationToken ct)
    {
        if (_rpc is null) throw new InvalidOperationException($"Worker {_id} not started.");

        await File.WriteAllTextAsync(_activeFilePath, activeCsv ?? "", ct);

        var runId = Guid.NewGuid().ToString("D");
        _runResults[runId] = new ConcurrentBag<TestResult>();

        try
        {
            // Always pass a non-null graphFilter: TUnit's MTP server returns 0 tests on
            // subsequent runTests RPCs when the filter is null. Match-everything filter avoids it.
            var rpcTask = _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                "testing/runTests",
                new
                {
                    runId,
                    testNodes = (object?)null,
                    graphFilter = treeFilter ?? "/*/*/*/*",
                },
                ct);

            // StreamJsonRpc's default cancellation strategy sends $/cancelRequest and waits for
            // the server's ack. A hung TUnit process (e.g., infinite-loop mutant) never acks, so
            // the await stays pinned for ~30s before StreamJsonRpc internally gives up. Race the
            // RPC against the cancellation token; if the token wins, force-kill the worker process
            // to unblock the channel and free up the parallelism slot.
            var cancelWatch = ct.CanBeCanceled
                ? Task.Delay(System.Threading.Timeout.Infinite, ct)
                : Task.Delay(System.Threading.Timeout.Infinite);
            var completed = await Task.WhenAny(rpcTask, cancelWatch).ConfigureAwait(false);
            if (completed != rpcTask)
            {
                // Force-kill the worker process. The hung test exe is occupying our parallelism
                // slot and our cancellation token already fired upstream.
                try { _proc?.Kill(entireProcessTree: true); } catch { }
                IsHealthy = false;
                return new TestRunOutcome(ExitCode: -1, Results: [], StdOut: "", StdErr: "ct fired; killed", TimedOut: true);
            }
            await rpcTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            IsHealthy = false;
            try { _proc?.Kill(entireProcessTree: true); } catch { }
            return new TestRunOutcome(ExitCode: -1, Results: [], StdOut: "", StdErr: "rpc cancelled", TimedOut: true);
        }
        catch (Exception ex)
        {
            IsHealthy = false;
            return new TestRunOutcome(ExitCode: -1, Results: [], StdOut: "", StdErr: ex.ToString());
        }

        _runResults.TryRemove(runId, out var bag);
        var results = bag?.ToArray() ?? [];
        // Workers that return 0 test results are broken. TUnit/MTP server mode has a
        // limited session lifetime — after several runTests calls, the test runtime stops
        // reporting test executions. Mark unhealthy so the pool kills + respawns. The
        // orchestrator retries the batch on a fresh worker.
        if (results.Length == 0)
        {
            IsHealthy = false;
            return new TestRunOutcome(ExitCode: -1, Results: [], StdOut: "", StdErr: "0 results", TimedOut: true);
        }
        return new TestRunOutcome(ExitCode: 0, Results: results, StdOut: "", StdErr: "");
    }

    private void OnTestUpdate(JsonElement param)
    {
        if (!param.TryGetProperty("runId", out var runIdEl)) return;
        var runId = runIdEl.GetString();
        if (runId is null) return;
        if (!_runResults.TryGetValue(runId, out var bag)) return;
        if (!param.TryGetProperty("changes", out var changes)) return;
        if (changes.ValueKind != JsonValueKind.Array) return;

        foreach (var change in changes.EnumerateArray())
        {
            if (!change.TryGetProperty("node", out var node)) continue;
            var name = node.TryGetProperty("display-name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            if (!node.TryGetProperty("execution-state", out var stateEl)) continue;
            var state = stateEl.GetString();
            if (state is null) continue;
            // Terminal states: anything except "in-progress" / "discovered".
            switch (state)
            {
                case "passed":
                    bag.Add(new TestResult(name, Passed: true));
                    break;
                case "failed":
                case "error":
                case "timed-out":
                case "canceled":
                    bag.Add(new TestResult(name, Passed: false));
                    break;
                // skipped / discovered / in-progress: ignore
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_rpc is not null)
            {
                try { await _rpc.NotifyAsync("exit").ConfigureAwait(false); } catch { }
            }
        }
        finally
        {
            _rpc?.Dispose();
            _client?.Dispose();
            _listener?.Stop();
            if (_proc is { HasExited: false })
            {
                try
                {
                    using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _proc.WaitForExitAsync(killCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    try { _proc.Kill(entireProcessTree: true); } catch { }
                }
            }
            _proc?.Dispose();
        }
    }
}
