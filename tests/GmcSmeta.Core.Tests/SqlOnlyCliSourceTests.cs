namespace GmcSmeta.Core.Tests;

public sealed class SqlOnlyCliSourceTests
{
    private static string Source()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.Cli", "Program.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.Cli", "Program.cs"));
    }

    [Fact]
    public void DiagnosticCommandsNeverDiscoverOrOpenDatabaseFiles()
    {
        var code = Source();
        Assert.DoesNotContain("DefaultBasePath", code);
        Assert.DoesNotContain("base.db", code);
        Assert.DoesNotContain("using var conn = Database.Open(", code);
        Assert.Contains("GMC_SMETA_SQL_CONNECTION", code);
        Assert.Contains("SqlServerDatabase.Open(connectionString)", code);
        Assert.Contains("args.Length == 0", code);
    }

    [Fact]
    public void DiagnosticCommandsValidateTargetBeforeOpening()
    {
        var code = Source();
        var open = code.IndexOf("SqlServerDatabase.Open(connectionString)", StringComparison.Ordinal);
        Assert.True(open > 0);
        var guard = code[..open];
        Assert.Contains("target.InitialCatalog.StartsWith(\"GmcSmetaPilot_\"", guard);
        Assert.Contains("target.InitialCatalog.Contains(\"Cutover\"", guard);
        Assert.Contains("target.AttachDBFilename", guard);
        Assert.Contains("target.FailoverPartner", guard);
        Assert.Contains("int.TryParse(args[1]", guard);
        Assert.Contains("Path.GetExtension(args[1])", guard);
    }

    [Fact]
    public void AllThreeReadsCheckRevisionBeforeOutputAndUseExistingReaders()
    {
        var code = Source();
        Assert.Contains("Totals.ReadAll(conn)", code);
        Assert.Contains("EstimateReader.Load(conn, id)", code);
        Assert.Contains("Parity.Check(checkedTotals, args[1])", code);
        Assert.Equal(3, code.Split("EnsureRevision(conn, revision)", StringSplitOptions.None).Length - 1);
        Assert.Contains("SqlServerDatabase.Revision(conn)", code);
        Assert.Contains("SqlLive.Run(args.Skip(1).ToArray())", code);
        Assert.Contains("Bench.Run(args.Skip(1).ToArray())", code);
    }
}
