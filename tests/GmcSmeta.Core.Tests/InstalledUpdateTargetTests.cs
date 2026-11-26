using GmcSmeta.Core;

namespace GmcSmeta.Core.Tests;

public sealed class InstalledUpdateTargetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmc-update-" + Guid.NewGuid().ToString("N"));
    private string Apps => Path.Combine(_root, "apps");
    private string Current => Exe("1.0.2-fx");

    public InstalledUpdateTargetTests() => Create("1.0.2-fx");

    private string Exe(string folder) => Path.Combine(Apps, folder, InstalledUpdateTarget.AppExeName);
    private void Create(string folder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Exe(folder))!);
        File.WriteAllText(Exe(folder), "test placeholder, never executed");
    }

    private void Publish(string version, string folder, string? exe = null)
    {
        File.WriteAllLines(Path.Combine(_root, "installed.txt"), [
            "version=" + version, "flavor=fx", "directory=" + Path.GetDirectoryName(Exe(folder)),
            "exe=" + (exe ?? Exe(folder))
        ]);
    }

    [Fact]
    public void FullyPublishedNewerVersionIsSelected()
    {
        Create("1.0.3-fx");
        Publish("1.0.3", "1.0.3-fx");
        var target = InstalledUpdateTarget.TryRead(Current, Apps);
        Assert.NotNull(target);
        Assert.Equal(Exe("1.0.3-fx"), target!.Exe);
        Assert.Equal(new Version(1, 0, 3, 0), target.Version);
    }

    [Theory]
    [InlineData("1.0.2", "1.0.2-fx")]
    [InlineData("1.0.1", "1.0.1-fx")]
    [InlineData("1.0.3", "1.0.3-fx.staging")]
    [InlineData("invalid", "invalid-fx")]
    [InlineData("1.0.3", "1.0.4-fx")]
    public void SameOlderMalformedOrUnpublishedVersionIsIgnored(string version, string folder)
    {
        Create(folder);
        Publish(version, folder);
        Assert.Null(InstalledUpdateTarget.TryRead(Current, Apps));
    }

    [Fact]
    public void MissingStateOrExecutableIsIgnored()
    {
        Assert.Null(InstalledUpdateTarget.TryRead(Current, Apps));
        Publish("1.0.3", "1.0.3-fx");
        Assert.Null(InstalledUpdateTarget.TryRead(Current, Apps));
    }

    [Fact]
    public void ExecutableFromAnotherRootIsRejected()
    {
        Create("1.0.3-fx");
        Publish("1.0.3", "1.0.3-fx", Current);
        Assert.Null(InstalledUpdateTarget.TryRead(Current, Apps));
        Assert.Null(InstalledUpdateTarget.TryRead(Current, Path.Combine(_root, "other")));
    }

    [Fact]
    public void InstallerSuffixIsSupported()
    {
        Create("1.0.3-fx-123456");
        Publish("1.0.3", "1.0.3-fx-123456");
        Assert.Equal(Exe("1.0.3-fx-123456"), InstalledUpdateTarget.TryRead(Current, Apps)?.Exe);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
