using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TUnit.Mutation.Driver.Mutators;

public sealed class BooleanLiteralMutator : IMutator
{
    public IEnumerable<MutationCandidate> GetCandidates(SyntaxNode node)
    {
        if (node is not LiteralExpressionSyntax lit) yield break;

        SyntaxKind? flipped = lit.Kind() switch
        {
            SyntaxKind.TrueLiteralExpression  => SyntaxKind.FalseLiteralExpression,
            SyntaxKind.FalseLiteralExpression => SyntaxKind.TrueLiteralExpression,
            _ => null,
        };
        if (flipped is null) yield break;

        var token = flipped == SyntaxKind.TrueLiteralExpression
            ? SyntaxFactory.Token(SyntaxKind.TrueKeyword)
            : SyntaxFactory.Token(SyntaxKind.FalseKeyword);
        var mutated = SyntaxFactory.LiteralExpression(flipped.Value, token);
        yield return new MutationCandidate(
            Operator: $"BooleanLiteral({lit.Token.Text}→{token.Text})",
            Replacement: mutated,
            MutatedText: token.Text);
    }
}
