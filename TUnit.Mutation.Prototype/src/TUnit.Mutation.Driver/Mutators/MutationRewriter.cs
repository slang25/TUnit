using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TUnit.Mutation.Driver.Mutators;

public sealed class MutationRewriter : CSharpSyntaxRewriter
{
    private readonly IReadOnlyList<IMutator> _mutators;
    private readonly MutationRegistry _registry;
    private readonly string _filePath;

    public MutationRewriter(IReadOnlyList<IMutator> mutators, MutationRegistry registry, string filePath)
    {
        _mutators = mutators;
        _registry = registry;
        _filePath = filePath;
    }

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        if (node is null) return null;

        var visited = base.Visit(node);
        if (visited is not ExpressionSyntax visitedExpr) return visited;
        if (IsInExcludedContext(node)) return visited;

        var candidates = _mutators
            .SelectMany(m => m.GetCandidates(visitedExpr))
            .ToList();
        if (candidates.Count == 0) return visited;

        var span = node.GetLocation().GetLineSpan().StartLinePosition;
        var originalText = node.ToString();
        var scope = DetectScope(node);

        ExpressionSyntax chain = visitedExpr.WithoutTrivia();
        foreach (var c in candidates)
        {
            var id = _registry.Register(
                @operator: c.Operator,
                filePath: _filePath,
                line: span.Line + 1,
                column: span.Character + 1,
                original: originalText,
                mutated: c.MutatedText,
                scope: scope);
            var isActive = SyntaxFactory.ParseExpression(
                $"global::TUnit.Mutation.Generated.MutationRuntime.IsActive({id})");
            chain = SyntaxFactory.ConditionalExpression(
                isActive,
                c.Replacement.WithoutTrivia(),
                chain);
        }

        // C# spec: only invocation, object-creation, assignment, await, ++ / -- (prefix or postfix)
        // are valid as expression statements. The same restriction applies to `for` initializers
        // and incrementors. A ConditionalExpression is NOT in that set. If the mutated node sits
        // at one of those statement-expression positions, wrap the ternary in an invocation of
        // MutationRuntime.Discard so the resulting expression IS a valid InvocationExpression.
        if (IsStatementExpressionPosition(node))
        {
            var discardCall = SyntaxFactory.ParseExpression(
                $"global::TUnit.Mutation.Generated.MutationRuntime.Discard(({chain}))");
            return discardCall.WithTriviaFrom(visited);
        }

        return SyntaxFactory.ParenthesizedExpression(chain).WithTriviaFrom(visited);
    }

    private static bool IsStatementExpressionPosition(SyntaxNode node)
    {
        switch (node.Parent)
        {
            case ExpressionStatementSyntax:
                return true;
            case ForStatementSyntax forStmt:
                return forStmt.Incrementors.Any(e => ReferenceEquals(e, node))
                    || forStmt.Initializers.Any(e => ReferenceEquals(e, node));
            default:
                return false;
        }
    }

    /// Walks ancestors to classify where the mutation will execute. First member-decl we hit wins:
    /// static cctor / static field initializer / static property initializer → StaticInit.
    /// Anything else (instance ctor, method body, accessor, local function, lambda) → Instance.
    private static MutationScope DetectScope(SyntaxNode node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            switch (p)
            {
                case ConstructorDeclarationSyntax ctor:
                    return ctor.Modifiers.Any(SyntaxKind.StaticKeyword)
                        ? MutationScope.StaticInit
                        : MutationScope.Instance;
                case MethodDeclarationSyntax:
                case AccessorDeclarationSyntax:
                case LocalFunctionStatementSyntax:
                case AnonymousFunctionExpressionSyntax:
                    return MutationScope.Instance;
                case VariableDeclaratorSyntax v
                    when v.Parent is VariableDeclarationSyntax
                        { Parent: FieldDeclarationSyntax fd } && fd.Modifiers.Any(SyntaxKind.StaticKeyword):
                    return MutationScope.StaticInit;
                case PropertyDeclarationSyntax prop when prop.Modifiers.Any(SyntaxKind.StaticKeyword):
                    // Reaching the property decl without first hitting an AccessorDeclarationSyntax
                    // means we're inside its initializer (or arrow body, but those are Instance-ish —
                    // they execute every read, not once. Conservatively label StaticInit only if
                    // there's an initializer present and we passed through it.)
                    return prop.Initializer is not null
                        ? MutationScope.StaticInit
                        : MutationScope.Instance;
            }
        }
        return MutationScope.Instance;
    }

    private static bool IsInExcludedContext(SyntaxNode node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            switch (p)
            {
                case AttributeSyntax:
                case AttributeArgumentSyntax:
                case EnumMemberDeclarationSyntax:
                case ConstantPatternSyntax:
                case CaseSwitchLabelSyntax:
                    return true;
                case EqualsValueClauseSyntax ev when ev.Parent is ParameterSyntax:
                    return true;
                case VariableDeclaratorSyntax v
                    when v.Parent is VariableDeclarationSyntax
                        { Parent: FieldDeclarationSyntax fd } && fd.Modifiers.Any(SyntaxKind.ConstKeyword):
                    return true;
                case VariableDeclaratorSyntax v
                    when v.Parent is VariableDeclarationSyntax
                        { Parent: LocalDeclarationStatementSyntax ld } && ld.Modifiers.Any(SyntaxKind.ConstKeyword):
                    return true;
            }
        }
        return false;
    }
}
