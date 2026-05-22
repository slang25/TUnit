using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TUnit.Mutation.Driver.Mutators;

/// Removes a leading `!` or `-`: `!cond` → `cond`, `-x` → `x`. Highly lethal —
/// `!` removal flips every boolean check the body depends on.
public sealed class UnaryNegationMutator : IMutator
{
    public IEnumerable<MutationCandidate> GetCandidates(SyntaxNode node)
    {
        if (node is not PrefixUnaryExpressionSyntax unary) yield break;

        switch (unary.Kind())
        {
            case SyntaxKind.LogicalNotExpression:
                yield return new MutationCandidate(
                    Operator: "Unary(! removed)",
                    Replacement: unary.Operand,
                    MutatedText: unary.Operand.ToString());
                break;
            case SyntaxKind.UnaryMinusExpression:
                yield return new MutationCandidate(
                    Operator: "Unary(- removed)",
                    Replacement: unary.Operand,
                    MutatedText: unary.Operand.ToString());
                break;
        }
    }
}
