using GmcSmeta.Core;

namespace GmcSmeta.Core.Tests;

public sealed class SettingsRecoveryAcceptanceTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("gmc-settings-recovery-").FullName;
    private string SettingsPath => Path.Combine(_folder, "settings.json");

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"export_directory\":null}")]
    [InlineData("{\"window_width\":\"NaN\",\"window_height\":980}")]
    [InlineData("{\"window_width\":1720,\"window_height\":0}")]
    public void QuarantineAndRepeatedLaunchKeepBackupAndOriginalEvidence(string corrupt)
    {
        var file = SettingsPath;
        var store = new WpfSettingsStore(file);
        var previous = new WpfSettings { Scale = 1.25m, Theme = "dark" };
        store.Save(previous);
        store.Save(previous with { Scale = 1.50m });
        var backup = File.ReadAllBytes(file + ".bak");
        File.WriteAllText(file, corrupt);

        var first = store.Load();
        Assert.True(first.Recovered);
        Assert.Equal(previous, first.Settings);
        Assert.NotNull(first.CorruptCopyPath);
        Assert.Equal(corrupt, File.ReadAllText(first.CorruptCopyPath!));
        Assert.False(File.Exists(file));

        for (var i = 0; i < 3; i++)
        {
            var next = new WpfSettingsStore(file).Load();
            Assert.True(next.Recovered);
            Assert.Equal(previous, next.Settings);
            Assert.Null(next.CorruptCopyPath);
        }
        Assert.Equal(backup, File.ReadAllBytes(file + ".bak"));
        Assert.Single(Directory.GetFiles(_folder, "settings.json.corrupt-*"));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));

        // A deliberate later save restores the primary without discarding the backup.
        var edited = previous with { Scale = 1.35m };
        new WpfSettingsStore(file).Save(edited);
        var saved = new WpfSettingsStore(file).Load();
        Assert.False(saved.Recovered);
        Assert.Equal(edited, saved.Settings);
        Assert.Equal(backup, File.ReadAllBytes(file + ".bak"));
        Assert.Equal(corrupt, File.ReadAllText(first.CorruptCopyPath!));
    }

    [Fact]
    public void TwoCorruptCopiesArePreservedAndDefaultsAreUsable()
    {
        var file = SettingsPath;
        File.WriteAllText(file, "{broken-primary");
        File.WriteAllText(file + ".bak", "{broken-backup");
        var first = new WpfSettingsStore(file).Load();
        Assert.True(first.Recovered);
        Assert.Equal(new WpfSettings(), first.Settings);
        Assert.NotNull(first.CorruptCopyPath);
        Assert.Equal("{broken-primary", File.ReadAllText(first.CorruptCopyPath!));
        Assert.Equal("{broken-backup", File.ReadAllText(file + ".bak"));
        Assert.False(File.Exists(file));

        var next = new WpfSettingsStore(file).Load();
        Assert.Equal(new WpfSettings(), next.Settings);
        Assert.Equal("{broken-backup", File.ReadAllText(file + ".bak"));
        Assert.Single(Directory.GetFiles(_folder, "settings.json.corrupt-*"));
    }

    [Fact]
    public void InvalidSaveDoesNotChangeEitherValidCopy()
    {
        var file = SettingsPath;
        var store = new WpfSettingsStore(file);
        store.Save(new WpfSettings { Scale = 1.10m });
        store.Save(new WpfSettings { Scale = 1.30m });
        var primary = File.ReadAllBytes(file);
        var backup = File.ReadAllBytes(file + ".bak");
        Assert.Throws<InvalidDataException>(() =>
            store.Save(new WpfSettings { WindowWidth = double.PositiveInfinity, WindowHeight = 980 }));
        Assert.Throws<InvalidDataException>(() =>
            store.Save(new WpfSettings { ExportDirectory = null! }));
        Assert.Equal(primary, File.ReadAllBytes(file));
        Assert.Equal(backup, File.ReadAllBytes(file + ".bak"));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
