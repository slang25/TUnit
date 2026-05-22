namespace Calculator;

public static class Math2
{
    // Static field initializer — runs once at type init. Mutation here is StaticInit scope:
    // coverage attribution is unreliable, so the orchestrator runs it against the full suite.
    public static readonly int Answer = 6 * 7;

    public static int Add(int a, int b) => a + b;

    public static int Sub(int a, int b) => a - b;

    public static int Max(int a, int b) => a > b ? a : b;

    public static bool IsPositive(int n) => n > 0;

    public static int Abs(int n) => n < 0 ? -n : n;
}
