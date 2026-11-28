namespace GmcSmeta.Core.Tests;

public sealed class PilotConcurrencyCoverageTests
{
    [Fact]
    public void IntegrationSuiteRegistersAllFourNewScenarios()
    {
        var cases = Source("tests/GmcSmeta.Pilot.E2E/PilotCases.cs");
        foreach (var method in new[] { "ThreeClients", "GlobalRevision", "CancelBlockedRevision", "PreCancelledReads" })
            Assert.Contains("PilotConcurrencyReadCases." + method + "(p)", cases);
    }

    [Fact]
    public void ThreeClientCaseUsesOwnedFixtureAndChecksRejectedWrites()
    {
        var source = Source("tests/GmcSmeta.Pilot.E2E/PilotConcurrencyReadCases.cs");
        Assert.Contains("p.RequireOwned(conn)", source);
        Assert.Contains("ids.Distinct().Count() == 3", source);
        Assert.Contains("winners.Length == 1", source);
        Assert.Contains("i == winner ? markers[i] : before[i]", source);
        Assert.Contains("results[i].Revision == after", source);
        Assert.Contains("Note(readback, ids[winner]) == markers[winner]", source);
    }

    [Fact]
    public void BlockedReadCancellationIsMeasuredBeforeReleasingLock()
    {
        var source = Source("tests/GmcSmeta.Pilot.E2E/PilotConcurrencyReadCases.cs");
        Assert.Contains("Pooling = false", source);
        Assert.Contains("WITH (XLOCK,HOLDLOCK)", source);
        Assert.Contains("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ", source);
        Assert.Contains("RevisionAsync(cancellable, cancel.Token)", source);
        Assert.Contains("pending.WaitAsync(TimeSpan.FromSeconds(10))", source);
        Assert.Contains("ex.Number == 0", source);
        Assert.True(source.IndexOf("PilotRunner.Check(cancelled,", StringComparison.Ordinal) <
            source.IndexOf("tx.Rollback();", StringComparison.Ordinal));
        Assert.DoesNotContain("ALTER DATABASE", source);
        Assert.DoesNotContain("ClearAllPools", source);
    }

    [Fact]
    public void UnrelatedWriteDoesNotBypassGlobalRevisionOrAlterProcurement()
    {
        var source = Source("tests/GmcSmeta.Pilot.E2E/PilotConcurrencyReadCases.cs");
        Assert.Contains("refreshed.Revision == saved.Revision", source);
        Assert.Contains("SequenceEqual(snapshot.Rows.Select", source);
        Assert.Contains("SqlServerStages.ReadStageDistribution(conn, p.SmallContract, token)", source);
        Assert.Contains("ReadBuySnapshotIfChanged(p.PilotConnection,", source);
    }

    private static string Source(string relative)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, relative)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, relative));
    }
}
