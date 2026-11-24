using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class SmrModeSwitchPlanTests
{
    private static SmrModeSwitchSnapshot State(
        string mode = "sum", decimal amount = 100.25m, decimal position = 100.25m,
        bool locked = false, bool stageChild = false, bool stageMoney = false,
        bool acts = false, bool ds = false, int reprices = 0, int formulas = 0) =>
        new(mode, "rev-001", locked, stageChild, stageMoney, acts, ds,
            amount, position, reprices, formulas);

    [Theory]
    [InlineData("sum", "sum", true)]
    [InlineData("sum", "pos", true)]
    [InlineData("sum", "percent", false)]
    [InlineData("sum", "systems", false)]
    [InlineData("pos", "sum", true)]
    [InlineData("pos", "pos", true)]
    [InlineData("pos", "percent", false)]
    [InlineData("pos", "systems", false)]
    [InlineData("percent", "sum", false)]
    [InlineData("percent", "pos", false)]
    [InlineData("percent", "percent", true)]
    [InlineData("percent", "systems", false)]
    [InlineData("systems", "sum", false)]
    [InlineData("systems", "pos", false)]
    [InlineData("systems", "percent", false)]
    [InlineData("systems", "systems", true)]
    public void AllSixteenTransitionsAreClassified(string from, string to, bool safe)
    {
        var plan = SmrModeSwitchPlanning.Build(State(mode: from), to);
        Assert.Equal(safe, plan.Allowed);
        Assert.Equal(from, plan.CurrentMode);
        Assert.Equal(to, plan.TargetMode);
        Assert.Equal("rev-001", plan.ExpectedRevision);
        if (!safe) Assert.False(string.IsNullOrWhiteSpace(plan.RefusalReason));
        if (from == to) Assert.Empty(plan.Differences);
    }

    [Theory]
    [InlineData("sum", "pos")]
    [InlineData("pos", "sum")]
    public void DifferentStoredMoneyRefusesOverwrite(string from, string to)
    {
        var plan = SmrModeSwitchPlanning.Build(State(from, amount: 19.99m, position: 20.00m), to);
        Assert.False(plan.Allowed);
        Assert.Contains("различ", plan.RefusalReason!);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(10000, 9000)]
    public void ManualWorkPriceOrFormulaRefusesEvenWhenMoneyMatches(int reprices, int formulas)
    {
        var plan = SmrModeSwitchPlanning.Build(State(reprices: reprices, formulas: formulas), "pos");
        Assert.False(plan.Allowed);
        Assert.Contains("цены или формулы", plan.RefusalReason!);
    }

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public void LocksStagesActsAndDsRejectChange(
        bool locked, bool stageChild, bool stageMoney, bool acts, bool ds)
    {
        var plan = SmrModeSwitchPlanning.Build(State(
            locked: locked, stageChild: stageChild, stageMoney: stageMoney,
            acts: acts, ds: ds), "pos");
        Assert.False(plan.Allowed);
    }

    [Fact]
    public void SameModeIsPureNoOpEvenWhenLockedOrLinked()
    {
        var plan = SmrModeSwitchPlanning.Build(State(
            mode: "pos", locked: true, acts: true, ds: true, reprices: 5, formulas: 3), "pos");
        Assert.True(plan.Allowed);
        Assert.True(plan.IsNoOp);
        Assert.Empty(plan.Differences);
    }

    [Theory]
    [InlineData("sum", "pos")]
    [InlineData("pos", "sum")]
    public void ZeroMoneyMetadataOnlySwitchIsSafe(string from, string to)
    {
        var plan = SmrModeSwitchPlanning.Build(State(from, 0m, 0m), to);
        Assert.True(plan.Allowed);
        Assert.Equal("contracts.smr_mode", Assert.Single(plan.Differences).Field);
        Assert.Equal(0m, plan.BeforeSmr);
        Assert.Equal(0m, plan.AfterSmr);
    }

    [Theory]
    [InlineData("bad", "sum")]
    [InlineData("sum", "bad")]
    public void UnknownModeMustFailClosed(string from, string to)
    {
        Assert.False(SmrModeSwitchPlanning.Build(State(mode: from), to).Allowed);
    }
}
