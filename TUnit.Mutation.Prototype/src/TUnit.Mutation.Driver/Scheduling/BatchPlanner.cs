using TUnit.Mutation.Driver.Coverage;
using TUnit.Mutation.Driver.Mutators;

namespace TUnit.Mutation.Driver.Scheduling;

public sealed record MutantBatch(IReadOnlyList<int> MutantIds, IReadOnlySet<string> CoveringTests);

/// Greedy first-fit packer: groups mutants into batches such that no two mutants in a batch
/// share any covering test. A failure in a batch deterministically attributes to one mutant.
public static class BatchPlanner
{
    public static IReadOnlyList<MutantBatch> Plan(
        IReadOnlyList<MutationInfo> mutations,
        CoverageMap coverage,
        int maxBatchSize)
    {
        var batches = new List<(HashSet<int> ids, HashSet<string> tests)>();

        // Process in descending coverage-set size: fat mutants first → tighter packing.
        var ordered = mutations
            .Where(m => coverage.TestsByMutant.ContainsKey(m.Id))
            .OrderByDescending(m => coverage.TestsByMutant[m.Id].Count)
            .ToList();

        foreach (var m in ordered)
        {
            var tests = coverage.TestsByMutant[m.Id];
            var placed = false;
            foreach (var b in batches)
            {
                if (b.ids.Count >= maxBatchSize) continue;
                if (tests.Any(b.tests.Contains)) continue;
                b.ids.Add(m.Id);
                foreach (var t in tests) b.tests.Add(t);
                placed = true;
                break;
            }
            if (!placed)
            {
                batches.Add((new HashSet<int> { m.Id }, [..tests]));
            }
        }

        return batches
            .Select(b => new MutantBatch(b.ids.ToArray(), b.tests))
            .ToArray();
    }
}
