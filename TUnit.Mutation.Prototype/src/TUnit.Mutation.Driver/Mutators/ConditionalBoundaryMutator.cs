using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TUnit.Mutation.Driver.Mutators;

public sealed class ConditionalBoundaryMutator : IMutator
{
    private static readonly Dictionary<SyntaxKind, (SyntaxKind ExprKind, SyntaxKind OpKind)> Flips = new()
    {
        [SyntaxKind.LessThanExpression]           = (SyntaxKind.LessThanOrEqualExpression,    SyntaxKind.LessThanEqualsToken),
        [SyntaxKind.LessThanOrEqualExpression]    = (SyntaxKind.LessThanExpression,           SyntaxKind.LessThanToken),
        [SyntaxKind.GreaterThanExpression]        = (SyntaxKind.GreaterThanOrEqualExpression, SyntaxKind.GreaterThanEqualsToken),
        [SyntaxKind.GreaterThanOrEqualExpression] = (SyntaxKind.GreaterThanExpression,        SyntaxKind.GreaterThanToken),
        [SyntaxKind.EqualsExpression]             = (SyntaxKind.NotEqualsExpression,          SyntaxKind.ExclamationEqualsToken),
        [SyntaxKind.NotEqualsExpression]          = (SyntaxKind.EqualsExpression,             SyntaxKind.EqualsEqualsToken),
    };

    public IEnumerable<MutationCandidate> GetCandidates(SyntaxNode node)
    {
        if (node is not BinaryExpressionSyntax bin) yield break;
        if (!Flips.TryGetValue(bin.Kind(), out var replacement)) yield break;

        var newOp = SyntaxFactory.Token(replacement.OpKind);
        var mutated = SyntaxFactory.BinaryExpression(replacement.ExprKind, bin.Left, newOp, bin.Right);
        yield return new MutationCandidate(
            Operator: $"ConditionalBoundary({bin.OperatorToken.Text}→{newOp.Text})",
            Replacement: mutated,
            MutatedText: $"{bin.Left} {newOp.Text} {bin.Right}");
    }
}
