using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class StageMutationPolicyTests
{
    private static StageMutationPolicy.State Editable => new(
        StageOf: 10, IsRest: false, StageLocked: false,
        SignedTarget: null, ParentLocked: false,
        ParentKind: "kp", ParentStageOf: null);

    [Theory]
    [InlineData(StageMutationPolicy.Operation.Quantity)]
    [InlineData(StageMutationPolicy.Operation.Rename)]
    [InlineData(StageMutationPolicy.Operation.Target)]
    [InlineData(StageMutationPolicy.Operation.Delete)]
    public void OrdinaryUnsignedStageCanBeEdited(StageMutationPolicy.Operation action) =>
        Assert.Null(StageMutationPolicy.Denial(10, Editable, action));

    [Theory]
    [InlineData(StageMutationPolicy.Operation.Quantity)]
    [InlineData(StageMutationPolicy.Operation.Rename)]
    [InlineData(StageMutationPolicy.Operation.Target)]
    [InlineData(StageMutationPolicy.Operation.Delete)]
    public void SignedContractAmountFreezesExistingStage(StageMutationPolicy.Operation action)
    {
        Assert.NotNull(StageMutationPolicy.Denial(10,
            Editable with { SignedTarget = 12345.67m }, action));
    }

    [Theory]
    [InlineData(StageMutationPolicy.Operation.Quantity)]
    [InlineData(StageMutationPolicy.Operation.Rename)]
    [InlineData(StageMutationPolicy.Operation.Target)]
    [InlineData(StageMutationPolicy.Operation.Delete)]
    public void ComputedRestCannotBeEditedAsOrdinaryStage(StageMutationPolicy.Operation action) =>
        Assert.Contains("Остаток", StageMutationPolicy.Denial(
            10, Editable with { IsRest = true }, action)!);

    [Fact]
    public void LockedStageAndLockedParentAreBothImmutable()
    {
        Assert.NotNull(StageMutationPolicy.Denial(10,
            Editable with { StageLocked = true }, StageMutationPolicy.Operation.Quantity));
        Assert.NotNull(StageMutationPolicy.Denial(10,
            Editable with { ParentLocked = true }, StageMutationPolicy.Operation.Delete));
    }

    [Fact]
    public void ForeignOrNestedParentIsRejected()
    {
        Assert.NotNull(StageMutationPolicy.Denial(11, Editable,
            StageMutationPolicy.Operation.Delete));
        Assert.NotNull(StageMutationPolicy.Denial(10,
            Editable with { ParentStageOf = 9 }, StageMutationPolicy.Operation.Quantity));
        Assert.NotNull(StageMutationPolicy.Denial(10,
            Editable with { ParentKind = "dop" }, StageMutationPolicy.Operation.Target));
        Assert.NotNull(StageMutationPolicy.Denial(10,
            Editable with { StageOf = null }, StageMutationPolicy.Operation.Quantity));
    }

    [Fact]
    public void QuantityChangeIsComparedAtStoredSixDecimalPrecision()
    {
        Assert.False(StageMutationPolicy.ChangesQuantity(1.000000m, 1.000000m));
        Assert.False(StageMutationPolicy.ChangesQuantity(1m, 1.000001m));
        Assert.True(StageMutationPolicy.ChangesQuantity(1m, 1.000002m));
        Assert.True(StageMutationPolicy.ChangesQuantity(2m, 0m));
    }
}
