using GmcSmeta.Launcher;

namespace GmcSmeta.Core.Tests;

public sealed class LauncherBinaryPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmc-launcher-" + Guid.NewGuid().ToString("N"));

    public LauncherBinaryPublisherTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void VerifiedFileReplacesExecutableAndPreservesBackup()
    {
        var exe = Path.Combine(_root, "launcher.exe");
        var staged = exe + ".new";
        File.WriteAllText(exe, "previous");
        File.WriteAllText(staged, "verified");
        LauncherBinaryPublisher.Replace(staged, exe);
        Assert.Equal("verified", File.ReadAllText(exe));
        Assert.Equal("previous", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public void MissingReplacementLeavesOldExecutableIntact()
    {
        var exe = Path.Combine(_root, "launcher.exe");
        File.WriteAllText(exe, "previous");
        Assert.ThrowsAny<IOException>(() => LauncherBinaryPublisher.Replace(exe + ".new", exe));
        Assert.Equal("previous", File.ReadAllText(exe));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
