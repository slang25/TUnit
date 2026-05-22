using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TUnit.Mutation.Driver.Mutators;

public readonly record struct MutationCandidate(
    string Operator,
    ExpressionSyntax Replacement,
    string MutatedText);

public interface IMutator
{
    IEnumerable<MutationCandidate> GetCandidates(SyntaxNode node);
}
