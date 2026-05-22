using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TUnit.Mutation.Driver.Mutators;

public sealed class AssignmentOperatorMutator : IMutator
{
    private static readonly Dictionary<SyntaxKind, (SyntaxKind ExprKind, SyntaxKind OpKind)> Flips = new()
    {
        [SyntaxKind.AddAssignmentExpression]      = (SyntaxKind.SubtractAssignmentExpression, SyntaxKind.MinusEqualsToken),
        [SyntaxKind.SubtractAssignmentExpression] = (SyntaxKind.AddAssignmentExpression,      SyntaxKind.PlusEqualsToken),
        [SyntaxKind.MultiplyAssignmentExpression] = (SyntaxKind.DivideAssignmentExpression,   SyntaxKind.SlashEqualsToken),
        [SyntaxKind.DivideAssignmentExpression]   = (SyntaxKind.MultiplyAssignmentExpression, SyntaxKind.AsteriskEqualsToken),
        [SyntaxKind.ModuloAssignmentExpression]   = (SyntaxKind.MultiplyAssignmentExpression, SyntaxKind.AsteriskEqualsToken),
    };

    public IEnumerable<MutationCandidate> GetCandidates(SyntaxNode node)
    {
        if (node is not AssignmentExpressionSyntax assign) yield break;
        if (!Flips.TryGetValue(assign.Kind(), out var rep)) yield break;

        var newOp = SyntaxFactory.Token(rep.OpKind);
        var mutated = SyntaxFactory.AssignmentExpression(rep.ExprKind, assign.Left, newOp, assign.Right);
        yield return new MutationCandidate(
            Operator: $"Assignment({assign.OperatorToken.Text}→{newOp.Text})",
            Replacement: mutated,
            MutatedText: $"{assign.Left} {newOp.Text} {assign.Right}");
    }
}
