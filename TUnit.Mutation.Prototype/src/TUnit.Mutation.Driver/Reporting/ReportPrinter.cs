using TUnit.Mutation.Driver.Mutators;

namespace TUnit.Mutation.Driver.Reporting;

public enum MutantStatus { Killed, Survived, NoCoverage }

public sealed record MutantOutcome(MutationInfo Mutation, MutantStatus Status, string? KilledBy);

public static class ReportPrinter
{
    public static void Print(IReadOnlyList<MutantOutcome> outcomes)
    {
        var killed    = outcomes.Count(o => o.Status == MutantStatus.Killed);
        var survived  = outcomes.Count(o => o.Status == MutantStatus.Survived);
        var noCov     = outcomes.Count(o => o.Status == MutantStatus.NoCoverage);
        var total     = outcomes.Count;
        var scored    = killed + survived;
        var score     = scored == 0 ? 0.0 : 100.0 * killed / scored;

        Console.WriteLine();
        Console.WriteLine("Mutation results");
        Console.WriteLine("================");
        Console.WriteLine($"  Total:       {total}");
        Console.WriteLine($"  Killed:      {killed}");
        Console.WriteLine($"  Survived:    {survived}");
        Console.WriteLine($"  No coverage: {noCov}");
        Console.WriteLine($"  Score:       {score:F1}%  (killed / [killed+survived])");
        Console.WriteLine();

        var survivors = outcomes.Where(o => o.Status == MutantStatus.Survived).ToList();
        if (survivors.Count > 0)
        {
            Console.WriteLine("Surviving mutants:");
            foreach (var s in survivors)
            {
                Console.WriteLine(
                    $"  #{s.Mutation.Id}  {Path.GetFileName(s.Mutation.FilePath)}:{s.Mutation.Line}  " +
                    $"{s.Mutation.Operator}  '{s.Mutation.OriginalText}' → '{s.Mutation.MutatedText}'");
            }
            Console.WriteLine();
        }
    }
}
