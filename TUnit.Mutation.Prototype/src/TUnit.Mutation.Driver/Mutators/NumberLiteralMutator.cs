using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TUnit.Mutation.Driver.Mutators;

/// `0 ↔ 1` for numeric literals. Restricted to 0 and 1 (and their suffix variants) — anything
/// fancier risks compile errors (e.g., negation of a position-required positive) or false-positive
/// breakage of unrelated code paths (array sizes etc.).
public sealed class NumberLiteralMutator : IMutator
{
    public IEnumerable<MutationCandidate> GetCandidates(SyntaxNode node)
    {
        if (node is not LiteralExpressionSyntax lit) yield break;
        if (lit.Kind() != SyntaxKind.NumericLiteralExpression) yield break;

        var text = lit.Token.Text;
        // Strip suffix to inspect the numeric body. e.g., "0L" → digits "0", suffix "L".
        var (digits, suffix) = SplitSuffix(text);

        string? replacementDigits = digits switch
        {
            "0"   => "1",
            "1"   => "0",
            "0.0" => "1.0",
            "1.0" => "0.0",
            "0d" or "0D" or "0f" or "0F" or "0m" or "0M" => null, // weird suffixes attached to digits — skip
            _     => null,
        };
        if (replacementDigits is null) yield break;

        var newText = replacementDigits + suffix;
        var mutated = (LiteralExpressionSyntax)SyntaxFactory.ParseExpression(newText);
        yield return new MutationCandidate(
            Operator: $"Number({text}→{newText})",
            Replacement: mutated,
            MutatedText: newText);
    }

    private static (string Digits, string Suffix) SplitSuffix(string text)
    {
        // Suffixes: L, UL, F, D, M (and lowercase). For 0/1 specifically.
        for (var i = text.Length; i > 0; i--)
        {
            var c = text[i - 1];
            if (char.IsDigit(c) || c == '.') return (text[..i], text[i..]);
        }
        return (text, "");
    }
}
