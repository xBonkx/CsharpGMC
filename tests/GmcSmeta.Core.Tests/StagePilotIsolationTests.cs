using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class StagePilotIsolationTests
{
    private const string Production =
        "Server=localhost;Database=GmcSmetaProduction;Integrated Security=True;TrustServerCertificate=True";

    [Fact]
    public void StageAndRestMutationsCannotOpenProductionDatabase()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerStages.CreateStage(Production, 100, "rev"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerStages.CreateRest(Production, 100, "rev"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerStages.SyncRest(Production, 100, "rev"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerStages.SetStageQty(Production, 101, 300, 2m, "rev"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerStages.PutRest(Production, 100, 101, "rev"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerStages.ClearStage(Production, 100, 101, "rev"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerStages.ChangeSelectedStageRows(Production, 100, 101, new long[] { 300, 301 }, true, "rev"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerStages.ChangeSelectedStageRows(Production, 100, 101, new long[] { 300, 301 }, false, "rev"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerStages.RenameStage(Production, 100, 101, "Этап", "rev"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerStages.SetStageTarget(Production, 101, 100m, "rev"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerStages.DeleteStage(Production, 101, "rev"));
    }

    [Fact]
    public void MoneyEditsCannotOpenProductionDatabase()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerStages.SaveStageMoney(Production, 100, 101, 100m, 10m, null, "rev"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerStages.SaveStageSystems(Production, 100, 101,
                new Dictionary<long, decimal> { [5] = 200m }, "rev"));
    }
}
