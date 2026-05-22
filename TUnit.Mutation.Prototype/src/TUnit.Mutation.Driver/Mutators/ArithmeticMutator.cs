using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TUnit.Mutation.Driver.Mutators;

public sealed class ArithmeticMutator : IMutator
{
    // Maps the binary expression's SyntaxKind to its (newExpressionKind, operatorTokenKind) replacement.
    private static readonly Dictionary<SyntaxKind, (SyntaxKind ExprKind, SyntaxKind OpKind)[]> Flips = new()
    {
        [SyntaxKind.AddExpression]      = [(SyntaxKind.SubtractExpression, SyntaxKind.MinusToken)],
        [SyntaxKind.SubtractExpression] = [(SyntaxKind.AddExpression, SyntaxKind.PlusToken)],
        [SyntaxKind.MultiplyExpression] = [(SyntaxKind.DivideExpression, SyntaxKind.SlashToken)],
        [SyntaxKind.DivideExpression]   = [(SyntaxKind.MultiplyExpression, SyntaxKind.AsteriskToken)],
        [SyntaxKind.ModuloExpression]   = [(SyntaxKind.MultiplyExpression, SyntaxKind.AsteriskToken)],
    };

    public IEnumerable<MutationCandidate> GetCandidates(SyntaxNode node)
    {
        if (node is not BinaryExpressionSyntax bin) yield break;
        if (!Flips.TryGetValue(bin.Kind(), out var replacements)) yield break;

        foreach (var (exprKind, opKind) in replacements)
        {
            var newOp = SyntaxFactory.Token(opKind);
            var mutated = SyntaxFactory.BinaryExpression(exprKind, bin.Left, newOp, bin.Right);
            yield return new MutationCandidate(
                Operator: $"Arithmetic({bin.OperatorToken.Text}→{newOp.Text})",
                Replacement: mutated,
                MutatedText: $"{bin.Left} {newOp.Text} {bin.Right}");
        }
    }
}
