using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TUnit.Mutation.Driver.Mutators;

/// `++i ↔ --i` and `i++ ↔ i--`. Lethal in loops (off-by-one) and counters.
public sealed class IncrementDecrementMutator : IMutator
{
    public IEnumerable<MutationCandidate> GetCandidates(SyntaxNode node)
    {
        switch (node)
        {
            case PrefixUnaryExpressionSyntax pre when pre.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression:
            {
                var flipped = pre.Kind() == SyntaxKind.PreIncrementExpression
                    ? (SyntaxKind.PreDecrementExpression, SyntaxKind.MinusMinusToken)
                    : (SyntaxKind.PreIncrementExpression, SyntaxKind.PlusPlusToken);
                var newOp = SyntaxFactory.Token(flipped.Item2);
                var mutated = SyntaxFactory.PrefixUnaryExpression(flipped.Item1, newOp, pre.Operand);
                yield return new MutationCandidate(
                    Operator: $"Increment(prefix {pre.OperatorToken.Text}→{newOp.Text})",
                    Replacement: mutated,
                    MutatedText: $"{newOp.Text}{pre.Operand}");
                break;
            }
            case PostfixUnaryExpressionSyntax post when post.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression:
            {
                var flipped = post.Kind() == SyntaxKind.PostIncrementExpression
                    ? (SyntaxKind.PostDecrementExpression, SyntaxKind.MinusMinusToken)
                    : (SyntaxKind.PostIncrementExpression, SyntaxKind.PlusPlusToken);
                var newOp = SyntaxFactory.Token(flipped.Item2);
                var mutated = SyntaxFactory.PostfixUnaryExpression(flipped.Item1, post.Operand, newOp);
                yield return new MutationCandidate(
                    Operator: $"Increment(postfix {post.OperatorToken.Text}→{newOp.Text})",
                    Replacement: mutated,
                    MutatedText: $"{post.Operand}{newOp.Text}");
                break;
            }
        }
    }
}
