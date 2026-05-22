using System.Text.Json;

namespace TUnit.Mutation.Driver.Coverage;

/// Map of mutantId → set of tests that exercise it (and their tree paths), and the inverse.
public sealed class CoverageMap
{
    public required IReadOnlyDictionary<int, IReadOnlyList<string>> TestsByMutant { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<int>> MutantsByTest { get; init; }
    public required IReadOnlyDictionary<int, IReadOnlyList<string>> PathsByMutant { get; init; }

    public static CoverageMap FromJsonl(string path)
    {
        var mutantToTests = new Dictionary<int, List<string>>();
        var mutantToPaths = new Dictionary<int, List<string>>();
        var testToMutants = new Dictionary<string, List<int>>();

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var test = doc.RootElement.GetProperty("test").GetString()!;
            var treePath = doc.RootElement.TryGetProperty("path", out var pathEl)
                ? pathEl.GetString()!
                : test;
            var idsEl = doc.RootElement.GetProperty("ids");
            var ids = new List<int>(idsEl.GetArrayLength());
            foreach (var idEl in idsEl.EnumerateArray()) ids.Add(idEl.GetInt32());

            testToMutants[test] = ids;
            foreach (var id in ids)
            {
                if (!mutantToTests.TryGetValue(id, out var tList))
                    mutantToTests[id] = tList = [];
                tList.Add(test);

                if (!mutantToPaths.TryGetValue(id, out var pList))
                    mutantToPaths[id] = pList = [];
                pList.Add(treePath);
            }
        }

        return new CoverageMap
        {
            TestsByMutant = mutantToTests.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value),
            MutantsByTest = testToMutants.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value),
            PathsByMutant = mutantToPaths.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value),
        };
    }
}
