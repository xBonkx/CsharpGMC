using GmcSmeta.Launcher;

namespace GmcSmeta.Core.Tests;

public sealed class LauncherRollbackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmc-rollback-" + Guid.NewGuid().ToString("N"));
    private string Apps => Path.Combine(_root, "apps");
    private string Current => Path.Combine(_root, "installed.txt");
    private string Previous => Path.Combine(_root, "previous-installed.txt");

    private InstalledApp Create(string version, int? schema = 32, string flavor = "sc")
    {
        var folder = Path.Combine(Apps, version + "-" + flavor);
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, "GMC-Smeta-Next.exe");
        File.WriteAllText(exe, "placeholder");
        File.WriteAllText(Path.Combine(folder, "dependency.dll"), "dependency");
        if (schema.HasValue)
        {
            File.WriteAllText(Path.Combine(folder, PackageIntegrity.VersionFile), version);
            File.WriteAllText(Path.Combine(folder, PackageIntegrity.SchemaFile), schema.Value.ToString());
        }
        var seal = PackageIntegrity.Seal(folder, version);
        return new InstalledApp
        {
            Version = version, Flavor = flavor, Directory = folder, Exe = exe,
            Sha256 = new string('a', 64), PackageSchema = seal.Schema, ManifestHash = seal.ManifestHash
        };
    }

    [Fact]
    public void RollbackUsesVerifiedPreviousAndBlocksOnlyRejectedOrOlderReleases()
    {
        var old = Create("1.2.3");
        var newer = Create("1.2.4");
        old.Publish(Apps, Current, Previous);
        newer.Publish(Apps, Current, Previous);
        Assert.Equal(old.Exe, InstalledApp.Read(Apps, Previous)!.Exe);
        var rollback = InstalledApp.Rollback(Apps, Current, Previous);
        Assert.Equal(old.Exe, rollback.Exe);
        Assert.Equal(old.Exe, InstalledApp.Read(Apps, Current)!.Exe);
        Assert.True(rollback.IsBlockedRelease("1.2.4"));
        Assert.True(rollback.IsBlockedRelease("1.2.3"));
        Assert.False(rollback.IsBlockedRelease("1.2.5"));
        Assert.True(File.Exists(newer.Exe));
        Assert.Throws<InvalidOperationException>(() => InstalledApp.Rollback(Apps, Current, Previous));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(null)]
    public void UnknownOrDifferentSchemaNeverChangesCurrentPointer(int? oldSchema)
    {
        Create("1.2.3", oldSchema).Publish(Apps, Current, Previous);
        var newer = Create("1.2.4");
        newer.Publish(Apps, Current, Previous);
        Assert.Throws<InvalidOperationException>(() => InstalledApp.Rollback(Apps, Current, Previous));
        Assert.Equal(newer.Exe, InstalledApp.Read(Apps, Current)!.Exe);
    }

    [Theory]
    [InlineData("dependency.dll")]
    [InlineData(PackageIntegrity.ManifestFile)]
    [InlineData("extra.dll")]
    public void DamagedOrUnexpectedPreviousFileRejectsRollback(string name)
    {
        var old = Create("1.2.3");
        var newer = Create("1.2.4");
        old.Publish(Apps, Current, Previous);
        newer.Publish(Apps, Current, Previous);
        File.WriteAllText(Path.Combine(old.Directory, name), "changed");
        Assert.Throws<InvalidOperationException>(() => InstalledApp.Rollback(Apps, Current, Previous));
        Assert.Equal(newer.Exe, InstalledApp.Read(Apps, Current)!.Exe);
    }

    [Fact]
    public void MissingCurrentExecutableDoesNotPreventKnownSchemaRecovery()
    {
        var old = Create("1.2.3");
        var newer = Create("1.2.4");
        old.Publish(Apps, Current, Previous);
        newer.Publish(Apps, Current, Previous);
        File.Delete(newer.Exe);
        Assert.Null(InstalledApp.Read(Apps, Current));
        Assert.Equal(old.Exe, InstalledApp.Rollback(Apps, Current, Previous).Exe);
    }

    [Fact]
    public void PointerOutsideInstallationRootIsRejected()
    {
        var old = Create("1.2.3");
        old.Publish(Apps, Current, Previous);
        var external = Path.Combine(_root, "other");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "GMC-Smeta-Next.exe"), "not an installation");
        File.WriteAllText(Current, File.ReadAllText(Current).Replace(old.Directory, external));
        Assert.Null(InstalledApp.Read(Apps, Current));
    }

    [Fact]
    public void WrongPackageVersionIsRejectedBeforePublishing()
    {
        var folder = Path.Combine(Apps, "1.2.3-sc");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, PackageIntegrity.VersionFile), "1.2.4");
        File.WriteAllText(Path.Combine(folder, PackageIntegrity.SchemaFile), "32");
        Assert.Throws<InvalidOperationException>(() => PackageIntegrity.Seal(folder, "1.2.3"));
        Assert.False(File.Exists(Path.Combine(folder, PackageIntegrity.ManifestFile)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
