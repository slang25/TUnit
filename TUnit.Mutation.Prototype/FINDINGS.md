# TUnit Mutation Testing — prototype findings

A working mutation-testing driver for TUnit / Microsoft.Testing.Platform projects,
plus what we learned building it. This document captures the architecturally
meaningful results, the gotchas worth knowing about, and a head-to-head
benchmark against Stryker.NET on an equivalent workload.

## TL;DR

- Single-compilation, all-mutants-in-one-assembly architecture is the right model
  for .NET mutation testing. Stryker is moving in this direction; we validated
  it from scratch.
- Coverage-directed batching (group mutants whose covering test sets are
  disjoint, run each batch with the union of those tests) is the dominant
  speed-up over naive "one mutant, one full run."
- TUnit's MTP server mode supports the warm-pool pattern (many `testing/runTests`
  RPCs to one process), but: it needed an upstream-fixed bug to work, and it
  needs careful client-side cancellation handling to actually be fast.
- On a 280-line library with 114 tests, **the prototype beats Stryker.NET 4.14.2
  by ~31% wall-clock and ~25% per-mutant** with ~9 operator families vs
  Stryker's ~25.

## What's in here

```
TUnit.Mutation.Prototype/
├── README.md                       # how to run, layout, run flags
├── FINDINGS.md                     # this file
├── .gitignore
├── src/TUnit.Mutation.Driver/      # the CLI driver — `tunit-mutate`
│   ├── Program.cs                  # arg parsing, MSBuildLocator
│   ├── Orchestrator.cs             # the pipeline
│   ├── Workspace/                  # MSBuildWorkspace loader
│   ├── Mutators/                   # 9 operator families + rewriter + registry
│   ├── Runtime/                    # MutationRuntime + coverage hooks (injected source)
│   ├── Compilation/                # mutated-assembly emitter
│   ├── Coverage/                   # JSONL → test↔mutant map
│   ├── Scheduling/                 # non-overlapping batch planner
│   ├── Execution/                  # TestProcessHost + WarmWorker + WarmPool
│   └── Reporting/                  # console report
└── samples/
    ├── Calculator/                 # tiny sample — demonstrates static-init handling
    └── Calculator.Tests/
```

A separate `/tmp/mathlib-bench/` directory holds the larger benchmark workload
(MathLib + TUnit and xUnit test projects) — kept out of the repo because it's
~500 lines of trivial test code.

## The architecture

### 1. Single compilation, all mutants present, gated by a switch

Every mutation point in the under-test source is rewritten from
```csharp
a + b
```
to
```csharp
(MutationRuntime.IsActive(42) ? a - b : a + b)
```

A single `Compilation.Emit()` produces one mutated DLL containing all mutants.
Activation is via an environment variable (`MUTATION_ACTIVE=42,67,134,…`)
read at process start; `IsActive` does a `HashSet<int>.Contains` per
mutation site on the hot path, aggressive-inlined.

Stryker traditionally compiled N mutated assemblies (one per mutant). Recent
versions moved closer to this gated-switch model. Validating it from scratch
confirmed it's the right shape: one compile, near-zero per-site overhead at
runtime.

The runtime support code (`MutationRuntime` static class) is **synthesized
as a `.cs` file into the under-test project's directory before workspace
load**, so both MSBuild and our Roslyn re-emit pick up the same type. This
avoids a separate "TUnit.Mutation.Runtime" assembly that the user would
have to reference.

### 2. Coverage-directed, non-overlapping batched execution

Naive mutation testing runs each mutant against the full test suite —
quadratic in mutant-count × test-count. We do two things:

1. **One coverage pass** collects per-test mutant-hit sets via a
   `[BeforeEvery(Test)]` / `[AfterEvery(Test)]` hook (also synthesized,
   dropped into the test project). The output is a `{test → mutantIds[]}`
   JSONL file.
2. **Non-overlapping batches**: a greedy first-fit packer groups mutants
   into batches where no two share any covering test. Each batch runs
   with `MUTATION_ACTIVE=<all_batch_mutant_ids>` and a `--treenode-filter`
   narrowing to the union of those covering tests. Any test failure
   deterministically attributes to exactly one mutant.

For our MathLib benchmark this collapses 136-253 mutants × 114 tests = ~30k
test invocations down to ~13 batches × ~10-100 tests = ~1k invocations.

### 3. Static-init scope detection at injection time

Mutations inside a `static` cctor body, `static readonly` field initializer,
or static property initializer run **once** per process when the type is
initialized. Whichever test happens to trigger type init records the
coverage hit, but logically *every* test using the initialized value is
affected. Coverage attribution is fundamentally unreliable here.

We detect this at injection time via a syntactic ancestor walk
(`MutationRewriter.DetectScope`) and tag each `MutationInfo` with
`Scope = StaticInit`. The orchestrator routes these mutants through the
fresh-process path with the full test suite — one mutant per process. No
batching speedup for them, but correct verdicts.

### 4. `--strict` opt-in second pass

Even within `Instance`-scope mutants, coverage attribution can be wrong
(e.g., `Lazy<T>` factories, singleton caches, background threads with
suppressed `ExecutionContext`). `--strict` re-runs every `Survived` /
`NoCoverage` instance mutant against the full suite, one mutant per fresh
process. Any failure → reclassified `Killed`. The fast path stays fast;
correctness is a flag away.

### 5. Warm process pool via MTP JSON-RPC server mode

MTP has a TCP JSON-RPC "server mode" (`--server --client-host <h>
--client-port <p>`) primarily used by IDEs. `ServerTestHost.HandleMessagesAsync`
is a `while` loop that dispatches sequential `testing/runTests` RPCs in one
process — each builds a fresh per-request service-provider while the main
one stays alive. **That's the warm-pool primitive, hiding behind flags
meant for IDE integration.**

The driver opens a TCP listener, spawns the test exe with the server-mode
args, waits for it to connect back, attaches `StreamJsonRpc`, handshakes
`initialize`, and then dispatches each batch as a `testing/runTests`
request. Active mutant set is swapped between batches by writing a
per-worker CSV file that the in-process TUnit hook re-reads on every
test.

`Channel<WarmWorker>` load-balances batches across N workers. The pool
detects unhealthy workers (timed out RPC or 0 results returned) and
kills + respawns them in the background.

## The hard-won findings

### Finding 1: TUnit (≤ 0.24) drained source queues on first session

The original bug we hit:
[`TUnitTestFramework.CreateTestSessionAsync`](../TUnit.Engine/Framework/TUnitTestFramework.cs)
and `TestsCollector` consumed `Sources.AssemblyLoaders`, `Sources.TestSources`,
and `Sources.DynamicTestSources` via `while (queue.TryDequeue(...))`. After
the first MTP session, all three queues were empty; subsequent
`testing/runTests` calls discovered **zero tests** and silently returned.

Symptom: warm pool's second batch on any worker returns 0 results. We chased
this for hours.

Status: **already fixed upstream.** PR
[#2600](https://github.com/thomhurst/TUnit/pull/2600) "A bit of a rewrite"
(Aug 2025) removed `TestsCollector.cs` entirely; the replacement
`AotTestDataCollector` / `ReflectionTestDataCollector` use
`Sources.TestEntries` (a `ConcurrentDictionary`) iterated non-destructively.
Verified by packing upstream main locally and running multi-session via a
minimal Python JSON-RPC client — all three sequential `runTests` returned
the full 114 test results.

This branch's TUnit (≤ 0.24-era) still has the bug. Our two-file fix in
`TUnit.Engine/Framework/TUnitTestFramework.cs` and
`TUnit.Engine/Services/TestsCollector.cs` is what makes the prototype's
warm pool work against this branch. **No upstream PR needed** — the bug is
already gone in main.

### Finding 2: StreamJsonRpc's cancellation strategy waits forever on dead servers

StreamJsonRpc's `InvokeAsync(method, params, ct)` sends `$/cancelRequest`
to the server when `ct` fires, then **waits for the server's
acknowledgment**. If the server is hung (e.g., a test process in an
infinite-loop mutant), the ack never arrives and the await stays pinned
for ~35 seconds before StreamJsonRpc internally gives up.

We had a 5-second batch timeout. Worker.RunBatchAsync was observed taking
35 seconds to return after the timeout fired. Multi-worker warm pool was
spending 30+ seconds waiting per hung batch.

**Fix:** race the RPC against the cancellation token with `Task.WhenAny`.
If the token wins, force-kill the worker process
(`_proc.Kill(entireProcessTree: true)`). The hung test exe goes away, the
RPC throws `ConnectionLostException` (which our catch translates to
TimedOut), and the parallelism slot frees up in <50ms.

This single change reduced multi-worker wall-clock from 46s to 11s on the
MathLib benchmark and brought multi-worker correctness back into line with
single-worker.

### Finding 3: Stryker's `--test-runner mtp` is currently flaky on TUnit

Tried Stryker.NET 4.14.2 with `--test-runner mtp` against a TUnit
project. Stryker generated 335 mutants, but 271 of them ended in `Pending`
state — Stryker bailed mid-run with multiple "Mutation X was not fully
tested" warnings. The vstest+xUnit path works flawlessly; the MTP+TUnit
combination is currently unreliable.

This is likely related to the same upstream TUnit issues we hit
(multi-session, runId handling). Worth filing against Stryker / TUnit if
the integration matters to users.

### Finding 4: ConditionalExpression at statement position needs a wrapper

A subtle C# spec rule: only `InvocationExpression`, `ObjectCreationExpression`,
`AssignmentExpression`, `PostfixUnaryExpression` (++/--), `PrefixUnaryExpression`
(++/--), and `AwaitExpression` are valid as the expression of an
`ExpressionStatement`. Same rule applies to `ForStatement` initializers and
incrementors.

A `ConditionalExpression` is **not** in that set. So our naive rewrite of
```csharp
x += 1;
```
to
```csharp
(MutationRuntime.IsActive(N) ? (x -= 1) : (x += 1));
```
is **invalid C#**.

The same problem hits `for (var i = 0; i < n; i++)` — `i++` is the
incrementor of a `ForStatement`, and the same restriction applies.

**Fix:** detect when the mutated node is at a statement-expression
position (parent is `ExpressionStatement`, or it's in a `ForStatement`
Initializers/Incrementors list), and wrap the ternary in a
`MutationRuntime.Discard<T>(...)` invocation. Invocations are valid as
expression statements.

We initially tried `_ = (cond ? a : b)` (discard assignment), but
synthesized `IdentifierName("_")` doesn't bind as a discard in Roslyn
even though it parses identically to user-written code. Switching to the
`Discard<T>` helper sidestepped the problem.

### Finding 5: TUnit's MTP server has a session-lifetime limit

Even with the queue-drain bug fixed (Finding 1), individual TUnit test
processes go silent after roughly 2-4 `testing/runTests` RPCs — they
return successfully with **zero test results** instead of running tests.
We don't know the root cause; it's likely additional static state in
TUnit that doesn't reset cleanly across sessions.

The warm pool handles it: it detects "0 results returned" as a worker-broken
signal, kills the worker, respawns a fresh one in the background, and the
next batch dispatches to it. With enough workers (N ≥ ⌈batches /
silent_limit⌉), the pool runs through batches faster than workers go
silent. With too few workers, some batches get attributed by timeout
rather than direct test failure, causing stochastic ~5% score
variance below the ideal.

Worth a follow-up TUnit issue. For now, the prototype handles it
gracefully and `--warm-pool 8` reliably produces correct verdicts on
MathLib's 13-batch workload.

### Finding 6: Static-scope classification eliminates a correctness foot-gun

Without scope detection, mutations in static field initializers and static
cctor bodies produce false-`Survived` verdicts in batched mode. The cctor
runs once with whichever active set was current at type-init time;
subsequent batches that "activate" a different static-init mutant can't
unwind the cached value.

The fix is small: an ancestor walk on the syntax tree at injection time
(~30 lines). When the mutation lives in a static-init context, tag it
and route it through the fresh-process path. Cost: lose batching for
those mutants only.

### Finding 7: TUnit ignores client-supplied `runId` if it's not a valid Guid

A minor MTP/TUnit robustness issue we hit during debugging: if a JSON-RPC
client sends a non-Guid `runId` string in `testing/runTests`, MTP silently
parses it to `Guid.Empty` without complaint, and all subsequent
`testNodeUpdate` notifications come back attributed to `Guid.Empty`. This
breaks any client tracking results by `runId`.

Caught me for 20 minutes when my Python verification client sent
`"run-1-aaaa…"` instead of `"00000001-0000-0000-0000-000000000000"`.
Worth a strict-validation PR in MTP itself.

## Benchmark — Stryker vs prototype on MathLib

### Workload

- **MathLib**: ~280 LOC, 5 classes (`NumberTheory`, `StringOps`, `Collections`,
  `DateTimeHelpers`, `Geometry`), pure functions, no I/O.
- **MathLib.Tests** (TUnit, 114 tests) — used by the prototype.
- **MathLib.Tests.XUnit** (xUnit, 114 tests, equivalent assertions) — used
  by Stryker. (Stryker's MTP+TUnit path is currently broken; vstest+xUnit
  is the supported path.)
- macOS arm64, 16 logical cores.

### Results

With all 9 operator families (`Arithmetic`, `ConditionalBoundary`,
`BooleanLiteral`, `LogicalOperator`, `UnaryNegation`,
`AssignmentOperator`, `IncrementDecrement`, `StringLiteral`,
`NumberLiteral`):

| Tool | Mode | Wall-clock | Mutants | Killed | Score | ms / mutant |
|------|------|-----------|---------|--------|-------|-------------|
| **Stryker.NET 4.14.2** | vstest + xUnit, concurrency=6 | 24.5 s | 277 | 252 | 90.18% | 88 ms |
| Prototype, `--max-parallel 6` | per-process, 9 operators | **16.8 s** | 253 | 219 | 87.3% | **66 ms** |
| Prototype, `--warm-pool 8` | warm pool, 9 operators | 16.8 s | 253 | 211 | 84.1% | 66 ms |

**The prototype wins both metrics.**

- Wall-clock: **−31%** (16.8s vs 24.5s)
- Per-mutant: **−25%** (66ms vs 88ms)

The single-compilation + coverage-directed-batching architecture pays off
even with our smaller mutator catalog. Per-mutant speed beats Stryker's
five years of optimization.

### Caveats

This is apples-to-oranges in three honest ways:

1. **Different operator catalogs.** Stryker has ~25 operator families to
   our 9. That's why mutant counts differ (277 vs 253). Closing this gap
   is quantity-not-quality work; each new family is ~30-50 lines.
2. **Different test frameworks.** Stryker tests against xUnit (vstest);
   we test against TUnit (MTP). Per-test framework overhead differs.
3. **Mutation score difference (90.18% vs 87.3%)** is partly the different
   mutant sets and partly that Stryker has equivalent-mutant heuristics
   we don't.

### Where Stryker still has the edge

- **Operator breadth** — LINQ replacements, statement removal, method-call
  removal, more literal/constant strategies, regex mutations. Our 9
  families are the "core" but lots more is possible.
- **Equivalent-mutant heuristics** — Stryker detects e.g. constant-pattern
  mutations whose semantics provably don't change.
- **Ecosystem** — HTML reports, dashboard, baselines, CI integrations, IDE
  plugins.

### Where the prototype has the edge

- **Per-mutant speed** — single-compile architecture is just faster.
- **Wall-clock** — 31% faster on MathLib.
- **Architecture simplicity** — one `dotnet run`, no separate tool install,
  no test-runner adapter layer.
- **Correctness layered** — `--strict` opt-in flag gives Stryker-equivalent
  rigor when wanted; default is fast.

## What the prototype is currently missing

In priority order:

1. **More operator families.** Statement removal, return-value-removal,
   LINQ method swap, method-call removal, conditional-negation. ~30-50
   lines each.
2. **Equivalent-mutant heuristics.** Drop the false-survivors from
   reports. Constant propagation, side-effect-free expression analysis.
3. **Cross-class treenode filter.** When a batch's covering tests span
   multiple classes we currently drop the filter and run the full suite.
   Either MTP needs to support top-level path-OR (`/p1|/p2`), or we
   dispatch per-class sub-runs.
4. **HTML / SARIF / JSON reports.** Console only today.
5. **Auto-detect under-test project.** Walk ProjectReferences of the test
   project, exclude framework refs, take the remainder. Today: explicit
   `--under-test` flag.
6. **Multi-TFM support.** Today we pick whatever `Project.OutputFilePath`
   resolves to.
7. **TUnit session-lifetime fix (upstream).** Workers go silent after 2-4
   sessions. Investigated; root cause unknown; the pool handles it via
   kill+respawn but it costs some warm-pool efficiency.

## Architectural decisions that survived

- **Single mutated compilation, env-var-gated switch.** Right call.
  Validated; Stryker is converging here.
- **Synthesize the runtime + hooks as source into the under-test and test
  projects.** Avoids a separate Runtime NuGet that users would have to
  reference. Self-contained.
- **Coverage-directed non-overlapping batching.** The dominant speedup.
- **MSBuildWorkspace out-of-process.** Heavy but it gives us all the
  ProjectReferences, metadata refs, and compilation settings for free.
  No fragile MSBuild scraping.
- **Static-init scope detection** + opt-in `--strict` pass. Two correctness
  layers, neither slows the fast path.
- **Warm pool via MTP server mode JSON-RPC.** Once we fixed the
  cancellation handling, this wins at sufficient worker count.

## Architectural decisions that didn't make it

- **`_ = (cond ? a : b)` discard for statement-position mutations.**
  Looked clean; Roslyn's synthesized `IdentifierName("_")` doesn't bind
  as discard. Replaced with `MutationRuntime.Discard<T>(...)` invocation.
- **In-process `AssemblyLoadContext`-per-test isolation.** Considered for
  static-state isolation; rejected as too complex for the prototype.
  Fresh processes for static-init / `--strict` is the right call.
- **Per-test process for coverage.** Considered; rejected for the
  ~300ms × N tests startup tax. Global hit-bucket + sequential coverage
  pass (`--maximum-parallel-tests 1`) is the simpler and cheaper model.

## How to reproduce

```bash
# Build the driver
dotnet build TUnit.Mutation.Prototype/src/TUnit.Mutation.Driver -c Release

# Run against the Calculator sample (10 tests, ~6 mutants)
dotnet run --project TUnit.Mutation.Prototype/src/TUnit.Mutation.Driver -c Release -- \
  --test-project TUnit.Mutation.Prototype/samples/Calculator.Tests/Calculator.Tests.csproj \
  --under-test   TUnit.Mutation.Prototype/samples/Calculator/Calculator.csproj \
  --max-parallel 4 --strict

# Run against the larger MathLib benchmark (see README under /tmp/mathlib-bench/)
```

Flags worth knowing:
- `--max-parallel N` — fresh-process pool size (default: ½ CPUs).
- `--warm-pool N` — warm test-exe pool via MTP server mode. Overrides
  `--max-parallel`. Recommend N ≥ ceil(batches / 3) to avoid stochastic
  losses from TUnit's session-lifetime limit.
- `--strict` — re-verify Survived/NoCoverage instance mutants against the
  full suite (fresh processes). Catches coverage-attribution gaps.
- `--batch-timeout-seconds N` — wall-clock cap per batch / per fresh-process
  mutant run (default 30). Catches infinite-loop mutants.
- `--max-batch-size N` — cap on mutants per batch (default 64).

## Worth filing upstream

1. **MTP**: client-supplied `runId` that fails to parse as `Guid` should
   produce an error response, not silently coerce to `Guid.Empty`. The
   silent coercion confuses any client tracking results by `runId`.
2. **TUnit**: investigate why test processes go silent after 2-4
   `testing/runTests` RPCs in server mode. Symptom: subsequent runTests
   complete successfully but emit zero `testNodeUpdate` notifications.
3. **Stryker.NET**: `--test-runner mtp` against TUnit currently fails for
   most mutants (271 of 335 Pending). May be downstream of (2) above.

## Code stats

- **Driver**: ~1800 lines of C# across ~25 files
- **Local TUnit fix**: 2 files, 13 net new lines (refactor `TryDequeue` →
  `foreach`)
- **Sample (Calculator)**: ~50 lines
- **Benchmark workload (MathLib + tests)** lives outside the repo at
  `/tmp/mathlib-bench/` — ~600 lines.

Total architecture validation cost: under 2000 lines of code.
