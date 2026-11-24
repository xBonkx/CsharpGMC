using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class SqlServerTransactionFinalizerTests
{
    [Fact]
    public void RevisionIsUpdatedBeforeCommit()
    {
        var events = new List<string>();
        var revision = SqlServerTransactionFinalizer.BumpAndCommit(
            () => { events.Add("revision"); return "next"; },
            () => events.Add("commit"));
        Assert.Equal("next", revision);
        Assert.Equal(new[] { "revision", "commit" }, events);
    }

    [Fact]
    public void CommitMustNeverRunIfRevisionFails()
    {
        var committed = false;
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerTransactionFinalizer.BumpAndCommit(
                () => throw new InvalidOperationException("DB error"),
                () => committed = true));
        Assert.False(committed);
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerTransactionFinalizer.BumpAndCommit(
                () => "", () => committed = true));
        Assert.False(committed);
    }

    [Fact]
    public void CommitErrorMustNotBeReportedAsSuccess()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerTransactionFinalizer.BumpAndCommit(
                () => "next", () => throw new InvalidOperationException("Commit failed")));
    }
}
