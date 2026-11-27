using GmcSmeta.Launcher;

namespace GmcSmeta.Core.Tests;

public sealed class InstallDirectoryPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmc-publish-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 27, 12, 34, 56, DateTimeKind.Utc);

    private string Create(string folder, string contents)
    {
        var path = Path.Combine(_root, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "app.dll"), contents);
        return path;
    }

    [Fact]
    public void FirstInstallMovesCompleteDirectory()
    {
        var staging = Create("new.staging", "new");
        var target = Path.Combine(_root, "1.0.3-fx");
        Assert.Equal(target, InstallDirectoryPublisher.Publish(staging, target, Now));
        Assert.False(Directory.Exists(staging));
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "app.dll")));
    }

    [Fact]
    public void ExistingInstallationsAndSuffixCollisionsAreNeverModified()
    {
        var target = Create("1.0.3-fx", "running");
        var collision = Create("1.0.3-fx-123456", "previous");
        var older = Create("1.0.1-fx", "older session");
        var staging = Create("new.staging", "new");
        var published = InstallDirectoryPublisher.Publish(staging, target, Now);
        Assert.Equal(target + "-123457", published);
        Assert.Equal("running", File.ReadAllText(Path.Combine(target, "app.dll")));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(collision, "app.dll")));
        Assert.Equal("older session", File.ReadAllText(Path.Combine(older, "app.dll")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(published, "app.dll")));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void MissingStagingDoesNotDestroyExistingVersion()
    {
        var target = Create("1.0.3-fx", "working");
        Assert.ThrowsAny<IOException>(() =>
            InstallDirectoryPublisher.Publish(Path.Combine(_root, "missing"), target, Now));
        Assert.Equal("working", File.ReadAllText(Path.Combine(target, "app.dll")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
