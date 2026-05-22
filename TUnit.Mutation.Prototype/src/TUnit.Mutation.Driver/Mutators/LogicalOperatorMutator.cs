using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TUnit.Mutation.Driver.Mutators;

public sealed class LogicalOperatorMutator : IMutator
{
    private static readonly Dictionary<SyntaxKind, (SyntaxKind ExprKind, SyntaxKind OpKind)> Flips = new()
    {
        [SyntaxKind.LogicalAndExpression] = (SyntaxKind.LogicalOrExpression,  SyntaxKind.BarBarToken),
        [SyntaxKind.LogicalOrExpression]  = (SyntaxKind.LogicalAndExpression, SyntaxKind.AmpersandAmpersandToken),
    };

    public IEnumerable<MutationCandidate> GetCandidates(SyntaxNode node)
    {
        if (node is not BinaryExpressionSyntax bin) yield break;
        if (!Flips.TryGetValue(bin.Kind(), out var rep)) yield break;

        var newOp = SyntaxFactory.Token(rep.OpKind);
        var mutated = SyntaxFactory.BinaryExpression(rep.ExprKind, bin.Left, newOp, bin.Right);
        yield return new MutationCandidate(
            Operator: $"Logical({bin.OperatorToken.Text}→{newOp.Text})",
            Replacement: mutated,
            MutatedText: $"{bin.Left} {newOp.Text} {bin.Right}");
    }
}
