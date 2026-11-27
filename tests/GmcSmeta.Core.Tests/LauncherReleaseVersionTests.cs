using GmcSmeta.Launcher;

namespace GmcSmeta.Core.Tests;

public sealed class LauncherReleaseVersionTests
{
    [Theory]
    [InlineData(null, "1.10.3")]
    [InlineData("1.9.9", "1.10.3")]
    [InlineData("1.10.3", "1.10.3")]
    [InlineData("1.10.3.0", "1.10.3")]
    [InlineData("1.10", "1.10.0")]
    public void ValidEqualOrNewerReleaseIsEligible(string? installed, string offered)
    {
        Assert.True(AppVersion.CanOfferRelease(installed, offered));
    }

    [Theory]
    [InlineData("1.10.3", "1.9.99")]
    [InlineData("1.10.3.1", "1.10.3")]
    [InlineData("1.10.3", "bad")]
    [InlineData("bad", "1.10.3")]
    [InlineData(null, "")]
    [InlineData(null, "1")]
    [InlineData(null, "1.2.3.4.5")]
    [InlineData(null, "1.2.2147483648")]
    [InlineData(null, "../1.2")]
    public void InvalidOrOlderReleaseIsRejected(string? installed, string offered)
    {
        Assert.False(AppVersion.CanOfferRelease(installed, offered));
    }

    [Fact]
    public void EquivalentVersionsDoNotTriggerAReinstall()
    {
        Assert.False(AppVersion.IsOlder("1.10.3.0", "1.10.3"));
        Assert.False(AppVersion.IsOlder("1.10", "1.10.0"));
        Assert.True(AppVersion.IsOlder("1.9.9", "1.10.3"));
    }
}
