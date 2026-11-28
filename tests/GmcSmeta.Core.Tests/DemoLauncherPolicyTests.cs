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
        Assert.True(DemoReleasePolicy.IsDemoEntry(["--DEMO"], "any.exe"));
        Assert.Throws<InvalidOperationException>(() => DemoReleasePolicy.Parse(["--demo", Db]));
        Assert.False(DemoReleasePolicy.IsDemoEntry([], "GMC-Smeta-CSharp-Start.exe"));
        Assert.Throws<InvalidOperationException>(() => DemoReleasePolicy.Parse([]));
    }

    [Fact]
    public void TestChildDoesNotInheritOfficePathsOrSql()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GMC_SMETA_SQL_CONNECTION"] = "office", ["GMC_SMETA_SQL_DATABASE"] = "office",
            ["GMC_SMETA_HOME"] = "office", ["GMC_SMETA_LAUNCHER"] = "office",
            ["GMC_SMETA_STARTUP_TRACE_DIR"] = "office", ["GMC_SMETA_PTO_ROOT"] = "office",
            ["GMC_SMETA_LETTERS_ROOT"] = "office", ["GMC_SMETA_PASSPORTS_ROOT"] = "office",
            ["GMC_SMETA_NO_UPDATE"] = "0", ["GMC_SMETA_AUTO_RESTART"] = "1", ["PATH"] = "runtime"
        };
        DemoReleasePolicy.PrepareChildEnvironment(environment);
        Assert.Equal(3, environment.Count);
        Assert.Equal("1", environment["GMC_SMETA_NO_UPDATE"]);
        Assert.Equal("0", environment["GMC_SMETA_AUTO_RESTART"]);
        Assert.Equal("runtime", environment["PATH"]);
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

    [Theory]
    [InlineData("seed-20261128-123456-abcdef12", "0.0.0", "sc", "", true)]
    [InlineData("seed-20261128-123456-abcdef12", "1.2.3", "sc", "", false)]
    [InlineData("seed-20261128-123456-abcdef12", "0.0.0", "fx", "", false)]
    [InlineData("seed-20261128-123456-abcdef12", "0.0.0", "sc", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("seed-other", "0.0.0", "sc", "", false)]
    public void OnlyExplicitDemoReaderAcceptsInstallerSeed(string folder, string version, string flavor, string hash, bool valid)
    {
        var root = Path.Combine(Path.GetTempPath(), "gmc-demo-seed-" + Guid.NewGuid().ToString("N"));
        var apps = Path.Combine(root, "apps");
        var directory = Path.Combine(apps, folder);
        Directory.CreateDirectory(directory);
        try
        {
            var exe = Path.Combine(directory, "GMC-Smeta-Next.exe");
            File.WriteAllText(exe, "placeholder");
            var state = Path.Combine(root, "installed.txt");
            File.WriteAllLines(state, ["version=" + version, "flavor=" + flavor, "directory=" + directory,
                "exe=" + exe, "sha256=" + hash]);
            Assert.Null(InstalledApp.Read(apps, state));
            var installed = InstalledApp.Read(apps, state, allowDemoSeed: true);
            Assert.Equal(valid, installed is not null);
            if (installed is not null)
            {
                var previous = Path.Combine(root, "previous-installed.txt");
                var newDirectory = Path.Combine(apps, "1.2.3-sc");
                Directory.CreateDirectory(newDirectory);
                var newExe = Path.Combine(newDirectory, "GMC-Smeta-Next.exe");
                File.WriteAllText(newExe, "new");
                (installed with { Version = "1.2.3", Directory = newDirectory, Exe = newExe })
                    .Publish(apps, state, previous, allowDemoSeed: true);
                Assert.Equal(exe, InstalledApp.Read(apps, previous, allowDemoSeed: true)?.Exe);
                Assert.Null(InstalledApp.Read(apps, previous));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("1.2.3", "32", true)]
    [InlineData("1.2.4", "32", false)]
    [InlineData("1.2.3", "33", false)]
    [InlineData("1.2.3", null, false)]
    public void CandidateMetadataMustMatchReleaseAndDemoSchema(string declared, string? schema, bool valid)
    {
        var root = Path.Combine(Path.GetTempPath(), "gmc-demo-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, PackageIntegrity.VersionFile), declared);
            if (schema is not null) File.WriteAllText(Path.Combine(root, PackageIntegrity.SchemaFile), schema);
            if (valid) DemoReleasePolicy.RequireCandidateMetadata(root, "1.2.3");
            else Assert.NotNull(Record.Exception(() => DemoReleasePolicy.RequireCandidateMetadata(root, "1.2.3")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LocalSeedAndUpdatesCannotEscapeAppsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "demo-apps");
        Assert.True(DemoReleasePolicy.IsInside(root, Path.Combine(root, "seed")));
        Assert.False(DemoReleasePolicy.IsInside(root, root + "-office"));
        Assert.False(DemoReleasePolicy.IsInside(root, Path.Combine(root, "..", "office")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DemoRecoveryPreservesPreviousPointerAndRejectsTamperedRelease(bool tamper)
    {
        var root = Path.Combine(Path.GetTempPath(), "gmc-demo-recovery-" + Guid.NewGuid().ToString("N"));
        var apps = Path.Combine(root, "apps");
        var directory = Path.Combine(apps, "1.2.3-sc");
        Directory.CreateDirectory(directory);
        try
        {
            var exe = Path.Combine(directory, "GMC-Smeta-Next.exe");
            File.WriteAllText(exe, "original");
            File.WriteAllText(Path.Combine(directory, DemoReleasePolicy.CapabilityFile), "GMC-DEMO-1\nschema=32\n");
            File.WriteAllText(Path.Combine(directory, PackageIntegrity.VersionFile), "1.2.3");
            File.WriteAllText(Path.Combine(directory, PackageIntegrity.SchemaFile), "32");
            var seal = PackageIntegrity.Seal(directory, "1.2.3");
            var previous = Path.Combine(root, "previous-installed.txt");
            var state = Path.Combine(root, "installed.txt");
            new InstalledApp { Version = "1.2.3", Flavor = "sc", Directory = directory, Exe = exe,
                Sha256 = new string('a', 64), PackageSchema = seal.Schema, ManifestHash = seal.ManifestHash }
                .Publish(apps, previous, Path.Combine(root, "unused.txt"), allowDemoSeed: true);
            File.WriteAllText(state, "damaged");
            var previousBytes = File.ReadAllBytes(previous);
            if (tamper)
            {
                File.WriteAllText(exe, "changed");
                Assert.Throws<InvalidOperationException>(() => InstalledApp.RecoverDemoPrevious(apps, state, previous));
                Assert.Equal("damaged", File.ReadAllText(state));
            }
            else
            {
                Assert.Equal(exe, InstalledApp.RecoverDemoPrevious(apps, state, previous).Exe);
                Assert.Equal(exe, InstalledApp.Read(apps, state, allowDemoSeed: true)?.Exe);
            }
            Assert.Equal(previousBytes, File.ReadAllBytes(previous));
        }
        finally { Directory.Delete(root, true); }
    }

}
