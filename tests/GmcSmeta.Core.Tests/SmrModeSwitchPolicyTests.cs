using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class SmrModeSwitchPolicyTests
{
    [Theory]
    [InlineData("sum", "pos")]
    [InlineData("pos", "sum")]
    [InlineData("sum", "sum")]
    [InlineData("pos", "pos")]
    public void SafeModesAreAllowed(string from, string to)
    {
        Assert.Null(SmrModeSwitchPolicy.Rejection(from, to,
            locked:false, isStageChild:false, stageMoney:false));
    }

    [Theory]
    [InlineData("percent")]
    [InlineData("systems")]
    [InlineData("")]
    [InlineData(null)]
    public void UnsupportedOriginalModesCannotBeOverwritten(string? from)
    {
        var result = SmrModeSwitchPolicy.Rejection(from, "sum", false, false, false);
        Assert.NotNull(result);
        Assert.Contains("сумм", result!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StageChildCannotSwitchEvenIfCurrentModeIsPositionBased()
    {
        var reason = SmrModeSwitchPolicy.Rejection("pos", "sum", false, true, false);
        Assert.Contains("этапа", reason!);
    }

    [Fact]
    public void StagedFullKpAndSignedDocumentsAreAlsoProtected()
    {
        Assert.NotNull(SmrModeSwitchPolicy.Rejection("sum", "pos",
            false, false, true));
        Assert.NotNull(SmrModeSwitchPolicy.Rejection("pos", "sum",
            true, false, false));
    }

    [Theory]
    [InlineData("systems")]
    [InlineData("percent")]
    [InlineData("other")]
    public void UnsupportedDestinationRejected(string to)
    {
        Assert.NotNull(SmrModeSwitchPolicy.Rejection("sum", to,
            false, false, false));
    }
}
