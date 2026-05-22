using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TUnit.Mutation.Driver.Mutators;

/// `"abc" → ""` and `"" → "Stryker was here!"` — Stryker's convention.
public sealed class StringLiteralMutator : IMutator
{
    private const string NonEmptyReplacement = "Stryker was here!";

    public IEnumerable<MutationCandidate> GetCandidates(SyntaxNode node)
    {
        if (node is not LiteralExpressionSyntax lit) yield break;
        if (lit.Kind() != SyntaxKind.StringLiteralExpression) yield break;

        var value = lit.Token.ValueText;
        var replacement = value.Length == 0 ? NonEmptyReplacement : "";
        var mutated = SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(replacement));
        var origPreview = value.Length > 16 ? value[..16] + "…" : value;
        var newPreview = replacement.Length > 16 ? replacement[..16] + "…" : replacement;
        yield return new MutationCandidate(
            Operator: $"StringLiteral(\"{origPreview}\"→\"{newPreview}\")",
            Replacement: mutated,
            MutatedText: "\"" + replacement + "\"");
    }
}
