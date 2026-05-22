namespace TUnit.Mutation.Driver.Mutators;

public enum MutationScope
{
    /// Mutation in an instance method, instance ctor, static method body, accessor body,
    /// local function, or lambda — i.e. evaluated every time the enclosing member is called.
    /// Coverage-directed scheduling is reliable here.
    Instance,

    /// Mutation in a static constructor body, static field initializer, or static property
    /// initializer — evaluated *once* per process when the type is initialized. Coverage
    /// attribution is inherently unreliable; the orchestrator runs these against the full suite.
    StaticInit,
}

public sealed record MutationInfo(
    int Id,
    string Operator,
    string FilePath,
    int Line,
    int Column,
    string OriginalText,
    string MutatedText,
    MutationScope Scope);
