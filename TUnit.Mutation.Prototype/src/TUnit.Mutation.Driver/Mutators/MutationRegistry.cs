namespace TUnit.Mutation.Driver.Mutators;

public sealed class MutationRegistry
{
    private readonly List<MutationInfo> _all = [];

    public IReadOnlyList<MutationInfo> All => _all;

    public int Register(
        string @operator,
        string filePath,
        int line,
        int column,
        string original,
        string mutated,
        MutationScope scope)
    {
        var id = _all.Count + 1;
        _all.Add(new MutationInfo(id, @operator, filePath, line, column, original, mutated, scope));
        return id;
    }
}
