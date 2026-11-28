using GmcSmeta.Launcher;

namespace GmcSmeta.Core.Tests;

public sealed class DemoLauncherPolicyTests
{
    private const string Db = DemoReleasePolicy.Prefix + "123456abcdef";

    [Fact]
    public void TestExecutableCannotFallThroughToOfficeMode()
    {
        Assert.True(DemoReleasePolicy.IsDemoEntry([], DemoReleasePolicy.ExecutableName));
        Assert.True(DemoReleasePolicy.IsDemoEntry(["--demo-launch"], "any.exe"));
        Assert.False(DemoReleasePolicy.IsDemoEntry([], "GMC-Smeta-CSharp-Start.exe"));
        Assert.Throws<InvalidOperationException>(() => DemoReleasePolicy.Parse([]));
    }

    [Fact]
    public void OnlyExactDemoArgumentsAreAllowed()
    {
        Assert.Equal((Db, false), DemoReleasePolicy.Parse(["--demo-launch", Db]));
        Assert.Equal((Db, true), DemoReleasePolicy.Parse(["--demo-launch", Db, "--no-update"]));
        Assert.Throws<InvalidOperationException>(() => DemoReleasePolicy.Parse(["--demo-launch", "GmcSmetaPilot_Cutover_20261125"]));
        Assert.Throws<InvalidOperationException>(() => DemoReleasePolicy.Parse(["--demo-launch", Db, "--dev"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("GMC-DEMO-1\nschema=33\n")]
    [InlineData("GMC-DEMO-1\nschema=32\nschema=33\n")]
    public void RejectsUnknownOrIncompatibleRelease(string? capability) =>
        Assert.Throws<InvalidOperationException>(() => DemoReleasePolicy.RequireCapability(capability));

    [Fact]
    public void AcceptsExplicitCompatibleRelease() =>
        DemoReleasePolicy.RequireCapability("GMC-DEMO-1\r\nschema=32\r\n");

    [Fact]
    public void LocalSeedAndUpdatesCannotEscapeAppsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "demo-apps");
        Assert.True(DemoReleasePolicy.IsInside(root, Path.Combine(root, "seed")));
        Assert.False(DemoReleasePolicy.IsInside(root, root + "-office"));
        Assert.False(DemoReleasePolicy.IsInside(root, Path.Combine(root, "..", "office")));
    }
}
