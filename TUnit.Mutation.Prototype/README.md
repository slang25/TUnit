# TUnit.Mutation.Prototype

A prototype mutation testing driver built on top of TUnit / Microsoft.Testing.Platform.
The goal of this prototype is **architectural** — proving out the
single-compilation-with-all-mutants approach and the coverage-directed batched
execution model — not feature parity with mature tools like Stryker.

## What it does

1. Loads the project-under-test via `MSBuildWorkspace` (out-of-process MSBuild).
2. Walks every C# source tree in that project and applies a Roslyn
   `CSharpSyntaxRewriter` that injects **every** mutation as a
   `MutationRuntime.IsActive(N) ? mutated : original` ternary, gated by a
   runtime switch.
3. Emits **one** mutated assembly (PE + portable PDB). Stryker's traditional
   model compiles many assemblies; we compile once.
4. Drops a generated TUnit hook file (`[BeforeEvery(Test)]` /
   `[AfterEvery(Test)]`) into the test project, then `dotnet build`s the test
   project as normal (TUnit's source generator runs).
5. Copies the mutated under-test assembly over the project-referenced copy
   sitting in the test project's `bin` directory.
6. Runs the test exe once with `MUTATION_COVERAGE=1` to capture a per-test
   `{testId → mutantIds[]}` JSONL coverage map.
7. Plans non-overlapping batches (greedy first-fit by descending coverage size)
   so that any test failure deterministically attributes to one mutant.
8. For each batch: runs the test exe with `MUTATION_ACTIVE=<csv>`, parses TRX,
   attributes failures back to mutants.
9. Prints a killed / survived / no-coverage report.

## Layout

```
TUnit.Mutation.Prototype/
├── src/TUnit.Mutation.Driver/        # the CLI tool ("tunit-mutate")
│   ├── Workspace/                    # MSBuildWorkspace loader
│   ├── Mutators/                     # rewriter + 3 operators
│   ├── Runtime/                      # source templates injected into target/test
│   ├── Compilation/                  # single-compile emitter
│   ├── Coverage/                     # JSONL → test↔mutant map
│   ├── Scheduling/                   # non-overlapping batch planner
│   ├── Execution/                    # MTP process host + TRX parser
│   ├── Reporting/                    # console report
│   ├── Orchestrator.cs               # the pipeline
│   └── Program.cs                    # System.CommandLine entry
└── samples/
    ├── Calculator/                   # tiny target lib
    └── Calculator.Tests/             # 8-test TUnit project
```

## Run it

```bash
# from repo root
dotnet build TUnit.Mutation.Prototype/src/TUnit.Mutation.Driver -c Release

dotnet run --project TUnit.Mutation.Prototype/src/TUnit.Mutation.Driver -c Release -- \
  --test-project  TUnit.Mutation.Prototype/samples/Calculator.Tests/Calculator.Tests.csproj \
  --under-test    TUnit.Mutation.Prototype/samples/Calculator/Calculator.csproj \
  --max-parallel  8       \
  --strict
```

Useful flags:
- `--max-parallel N` — concurrent test processes (default: half CPUs).
- `--warm-pool N` — N long-lived workers driven over MTP server mode.
  Removes per-batch startup tax. Static-init mutants and `--strict`
  re-verification still use fresh processes.
- `--strict` — re-verify every `Survived`/`NoCoverage` instance mutant
  against the full suite. Catches coverage-attribution gaps.
- `--max-batch-size N` — cap on mutants per batch (default 64).

Artifacts (mutated dll, mutations.txt, coverage.jsonl, batch TRX files) land in
a temp folder; the path is printed at the end.

## Mutation operators in v0

Three families, ~50 lines each, all expression-level:

| Family               | Replacements |
|----------------------|----------------------------------------|
| Arithmetic           | `+ ↔ -`, `* ↔ /`, `% → *`              |
| ConditionalBoundary  | `< ↔ <=`, `> ↔ >=`, `== ↔ !=`          |
| BooleanLiteral       | `true ↔ false`                         |

Adding more is mechanical — one class implementing `IMutator`.

## Speed mechanics already wired in

- **Single compilation.** All mutants live in one dll.
- **Coverage-directed scheduling.** Mutants with no covering test are scored
  `NoCoverage` and never executed.
- **Non-overlapping batched execution.** ~N mutants per test process where
  their covering test-sets are disjoint, so a failure → exactly one mutant.
- **MTP-native test runs.** The test executable launches in MTP mode; TUnit's
  own intra-process parallelism handles per-process scale.

## Correctness mechanics

Execution-based coverage is a *scheduling heuristic*, not a correctness
primitive. Two specific failure modes get explicit handling:

### Static-init scope classification (always on)

Mutations inside a static cctor body, static field initializer, or static
property initializer run *once* per process. Whichever test first triggers
type init records the hit; subsequent tests inherit the initialized value.
That means coverage attribution lies for these mutants and the batched-run
optimization would produce false `Survived` verdicts.

The Roslyn rewriter detects these contexts at injection time (ancestor walk
on the syntax tree) and tags each `MutationInfo.Scope` as `StaticInit`. The
orchestrator then runs each such mutant against the **entire test suite**,
one at a time. You give up batching for those mutants — there's no way to
batch them safely without semantic-level disjointness analysis — but you
gain correctness.

A run reports the split:

```
[tunit-mutate]   → scope split: 5 instance, 1 static-init
```

### `--strict` opt-in verification pass

Even within `Instance`-scope mutants, coverage attribution can be wrong:

- A `Lazy<T>` factory runs once and records all its hits against whichever
  test happened to materialize the value. Other tests depend on the cached
  result but record nothing.
- Background threads with suppressed `ExecutionContext` flow
  (`ThreadPool.UnsafeQueueUserWorkItem`, manually started threads, timer
  callbacks) write to whatever the current bucket is, or none at all.
- Anything cached behind a singleton boundary.

These all produce false `Survived` or `NoCoverage` labels because the test
that *would* have killed the mutant wasn't in the covering-test set the
batched run executed.

`--strict` adds a second pass that re-runs every `Survived` and `NoCoverage`
instance mutant against the full test suite, one mutant at a time. Any
failure → reclassified as `Killed`. The fast pass stays fast; correctness
is a flag away:

```bash
dotnet run --project ... -- --test-project ... --under-test ... --strict
```

Static-init mutants are skipped by the strict pass — they already had the
full-suite treatment.

## Speed wins already in v0.2

- **Parallel batches.** `--max-parallel N` (default: half of CPUs) runs N
  test processes concurrently for the instance batches, static-init mutants,
  and `--strict` suspects. `dotnet exec` startup tax (~300ms) is amortized
  across workers rather than paid serially.
- **Coverage-directed test filtering.** Each batch passes
  `--treenode-filter "/Asm/Ns/Class/(m1|m2|m3)"` so only the covering tests
  execute. Batches whose covering tests span multiple classes fall back to
  no filter (TUnit's `TreeNodeFilter` doesn't support top-level path-OR;
  see below).

## Warm process pool

`--warm-pool N` keeps N long-lived test-exe processes alive and dispatches
each batch as a `testing/runTests` JSON-RPC call. Per-batch process-startup
tax (~300ms) goes away after the first N batches.

The mechanism that makes this work isn't widely documented: **MTP has a
TCP JSON-RPC server mode** (`--server --client-host --client-port`) used by
IDEs. Internally, `ServerTestHost.HandleMessagesAsync` is a `while` loop
that processes many sequential `testing/runTests` requests in one process —
each request builds a fresh per-request service provider while the main
one stays alive. That's the warm-pool primitive, hiding in plain sight.

Pipeline:

1. Driver opens a TCP listener on a random port.
2. Driver spawns the test exe with `--server --client-host 127.0.0.1
   --client-port <port>` plus a `MUTATION_ACTIVE_FILE` env var pointing at
   a per-worker file.
3. Test exe connects back over TCP. Driver sends `initialize`, receives
   capabilities.
4. For each batch:
   - Driver writes the active mutant CSV to the worker's file.
   - Driver sends `testing/runTests` with a `graphFilter` narrowing tests.
   - In-process TUnit `[BeforeEvery(Test)]` hook re-reads the file (cheap
     mtime check) and calls `MutationRuntime.SetActive(csv)`.
   - Tests run with the new active set.
   - `testing/testUpdates/tests` notifications stream pass/fail per test
     back to the driver, accumulated by `runId`.
   - The `runTests` response signals completion.
5. Driver sends `exit` and waits for the process to terminate.

A `Channel<WarmWorker>` load-balances batches across workers; a free worker
gets pulled off the channel, used, returned.

### Two important exceptions: static-init and `--strict`

A warm worker has already executed static cctors / static field
initializers by the time we start dispatching batches. Setting
`MutationRuntime.SetActive` for a static-init mutant after the fact won't
undo the cached value. So:

- **Static-init mutants always use fresh processes.** Detected at injection
  time; bypass the pool. Negligible cost — these are rare.
- **`--strict` re-verification also uses fresh processes.** That's the
  whole point of "strict" — maximum correctness, even at the cost of
  startup tax. Warm-pool semantics aren't strong enough.

Coverage-pass also runs per-process (it needs `MUTATION_COVERAGE=1` and the
coverage JSONL file flow), but it's a one-shot cost.

### Gotchas that took some debugging

- **StreamJsonRpc `InvokeAsync` sends params as a positional array**
  (`[{...}]`), but MTP expects `params` to be an object (`{...}`). Use
  `InvokeWithParameterObjectAsync` for both `initialize` and
  `testing/runTests`. Otherwise the server logs a deserialization error
  and tears down the connection.
- **Notification handlers need `UseSingleObjectParameterDeserialization =
  true`**. Otherwise StreamJsonRpc tries to name-bind the params object's
  fields (`runId`, `changes`) to your method's parameter names and rejects
  the handler with `"arguments do not match"`. Use the
  `AddLocalRpcMethod(MethodInfo, target, JsonRpcMethodAttribute)` overload
  with the attribute's `UseSingleObjectParameterDeserialization = true`.
- `experimental_multiRequestSupport: false` in the capabilities response
  refers to *concurrent* requests (multiple runTests in flight at once),
  not sequential. Sequential many-runTests-per-process works fine.

## What's deliberately stubbed (in priority order)

These are tractable next iterations.

1. **Cross-class treenode filtering.** When a batch's covering tests span
   multiple classes, we drop the filter today and run the full suite (TUnit's
   `TreeNodeFilter` only supports per-segment alternation `(a|b)`, not
   top-level path-OR `/p1|/p2`). Worth raising upstream as an MTP
   enhancement; once available, the orchestrator change is one line.
2. **Strict pass: deduplicate work.** When `--strict` re-runs a suspect,
   it runs the full suite. Most of those tests have nothing to do with the
   mutant. If we landed cross-class filtering above, we could narrow strict
   runs to "any test that imports/references the mutant's containing type"
   via static analysis.
3. **Test-name matching to TRX.** v0 uses substring/prefix matching between
   the TUnit-emitted `TestDetails` id and TRX `testName`. Tighten this with
   the same `UniqueId` TUnit uses internally.
5. **Equivalent-mutant heuristics.** Stryker-style guards (constant
   propagation, no-effect detection). Just labels survivors better.
6. **Mutator coverage.** Statement-level (return-value, statement removal,
   negate-conditional), assignment, string-literal, increment-operator,
   etc. The framework is in place; add `IMutator`s.
7. **Timeout per test.** Detect "infinite loop" mutants without hanging
   forever. Per-test wall clock.
8. **Multi-TFM.** v0 picks whatever `Project.OutputFilePath` resolves to.
   For multi-targeted projects, run a pass per TFM.
9. **Auto-detect under-test.** Walk `ProjectReference`s of the test project,
   exclude framework refs, take the remainder.
10. **Better reports.** HTML / SARIF / JSON; per-file mutation score map.

## Known fragilities

- TRX test-name matching is loose. False negatives possible if test names
  have unusual characters.
- `obj/*.g.cs` skipping is path-based, not metadata-based. If the project
  has hand-written `.g.cs` it'll be skipped too.
- The mutated build skips MSBuild for the under-test project — we emit
  directly from the Roslyn `Compilation` MSBuildWorkspace gives us. That
  means custom MSBuild targets on the under-test project are bypassed for
  the mutated copy. The non-mutated copy that MSBuild produces is then
  overwritten in the test project's bin.
- Coverage hook file is written into the test project directory. Add the
  `TUnitMutation/` folder to `.gitignore`.
