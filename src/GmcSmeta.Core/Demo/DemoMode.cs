namespace GmcSmeta.Core.Demo;

/// <summary>Explicit, fail-closed boundary for the temporary offline mode.</summary>
public static class DemoMode
{
    public static bool IsRequested(IEnumerable<string> args) =>
        args.Any(arg => string.Equals(arg, "--demo", StringComparison.Ordinal));

    public static void RejectSqlConnection(IEnumerable<string> args)
    {
        if (IsRequested(args))
            throw new InvalidOperationException("В тестовом режиме SQL Server недоступен.");
    }
}
