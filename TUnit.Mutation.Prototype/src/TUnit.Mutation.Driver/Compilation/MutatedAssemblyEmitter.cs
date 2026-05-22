using Microsoft.CodeAnalysis;
using TUnit.Mutation.Driver.Mutators;

namespace TUnit.Mutation.Driver.Compilation;

public sealed record EmitResult(string AssemblyPath, IReadOnlyList<MutationInfo> Mutations);

public static class MutatedAssemblyEmitter
{
    public static async Task<EmitResult> EmitAsync(
        Project project,
        string outputAssemblyPath,
        CancellationToken ct)
    {
        var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No compilation for {project.FilePath}.");

        var mutators = new IMutator[]
        {
            new ArithmeticMutator(),
            new ConditionalBoundaryMutator(),
            new BooleanLiteralMutator(),
            new LogicalOperatorMutator(),
            new UnaryNegationMutator(),
            new AssignmentOperatorMutator(),
            new IncrementDecrementMutator(),
            new StringLiteralMutator(),
            new NumberLiteralMutator(),
        };
        var registry = new MutationRegistry();

        var newTrees = new List<SyntaxTree>(compilation.SyntaxTrees.Count() + 1);
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (ShouldSkip(tree.FilePath))
            {
                newTrees.Add(tree);
                continue;
            }

            var rewriter = new MutationRewriter(mutators, registry, tree.FilePath);
            var root = await tree.GetRootAsync(ct).ConfigureAwait(false);
            var rewritten = rewriter.Visit(root);
            newTrees.Add(tree.WithRootAndOptions(rewritten!, tree.Options));
        }

        // The MutationRuntime source is dropped on disk under the under-test project before
        // load, so it's already in compilation.SyntaxTrees and ShouldSkip leaves it untouched.
        var mutatedCompilation = compilation
            .RemoveAllSyntaxTrees()
            .AddSyntaxTrees(newTrees);

        Directory.CreateDirectory(Path.GetDirectoryName(outputAssemblyPath)!);
        var pdbPath = Path.ChangeExtension(outputAssemblyPath, ".pdb");

        await using var peStream = File.Create(outputAssemblyPath);
        await using var pdbStream = File.Create(pdbPath);
        var emit = mutatedCompilation.Emit(
            peStream,
            pdbStream,
            options: new Microsoft.CodeAnalysis.Emit.EmitOptions(
                debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb),
            cancellationToken: ct);

        if (!emit.Success)
        {
            var errors = string.Join(Environment.NewLine,
                emit.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Take(10)
                    .Select(d => d.ToString()));
            throw new InvalidOperationException(
                $"Failed to emit mutated assembly. First errors:{Environment.NewLine}{errors}");
        }

        return new EmitResult(outputAssemblyPath, registry.All);
    }

    private static bool ShouldSkip(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }
}
