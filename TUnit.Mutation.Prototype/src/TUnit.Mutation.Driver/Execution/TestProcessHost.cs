using System.Diagnostics;
using System.Xml.Linq;

namespace TUnit.Mutation.Driver.Execution;

public sealed record TestResult(string Name, bool Passed);

public sealed record TestRunOutcome(int ExitCode, IReadOnlyList<TestResult> Results, string StdOut, string StdErr, bool TimedOut = false);

public sealed class TestProcessHost
{
    public string TestExecutable { get; }
    public string WorkingDirectory { get; }

    public TestProcessHost(string testExecutable, string workingDirectory)
    {
        TestExecutable = testExecutable;
        WorkingDirectory = workingDirectory;
    }

    public async Task<TestRunOutcome> RunAsync(
        IReadOnlyDictionary<string, string?> envVars,
        string trxFileName,
        CancellationToken ct,
        IReadOnlyList<string>? extraArgs = null,
        TimeSpan? timeout = null)
    {
        var trxPath = Path.Combine(WorkingDirectory, trxFileName);
        if (File.Exists(trxPath)) File.Delete(trxPath);

        var (fileName, baseArgs) = ResolveLaunch(TestExecutable);
        var combined = baseArgs.Concat([
            "--report-trx",
            "--report-trx-filename", trxFileName,
            "--results-directory", QuoteIfNeeded(WorkingDirectory),
        ]);
        if (extraArgs is not null) combined = combined.Concat(extraArgs);
        var args = string.Join(' ', combined);

        var psi = new ProcessStartInfo(fileName, args)
        {
            WorkingDirectory = WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var kv in envVars)
        {
            if (kv.Value is null) psi.Environment.Remove(kv.Key);
            else psi.Environment[kv.Key] = kv.Value;
        }

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) watchdog.CancelAfter(t);

        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(watchdog.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (watchdog.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            timedOut = true;
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); } catch { }
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var results = File.Exists(trxPath) ? ParseTrx(trxPath) : [];
        return new TestRunOutcome(
            ExitCode: timedOut ? -1 : proc.ExitCode,
            Results: results,
            StdOut: stdout,
            StdErr: stderr,
            TimedOut: timedOut);
    }

    private static (string fileName, string[] args) ResolveLaunch(string exe)
    {
        if (exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return ("dotnet", ["exec", QuoteIfNeeded(exe)]);
        return (exe, []);
    }

    private static string QuoteIfNeeded(string s) =>
        s.Contains(' ') ? "\"" + s + "\"" : s;

    private static IReadOnlyList<TestResult> ParseTrx(string path)
    {
        var doc = XDocument.Load(path);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var results = doc.Descendants(ns + "UnitTestResult")
            .Select(e => new TestResult(
                Name: (string?)e.Attribute("testName") ?? "",
                Passed: string.Equals((string?)e.Attribute("outcome"), "Passed", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return results;
    }
}
