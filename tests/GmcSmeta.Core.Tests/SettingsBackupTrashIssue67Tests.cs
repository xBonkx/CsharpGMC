using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class SettingsBackupTrashIssue67Tests
{
    private const string PilotConnection =
        "Server=localhost;Database=GmcSmetaPilot_Issue67;Integrated Security=true;TrustServerCertificate=true";
    private const string ProductionConnection =
        "Server=localhost;Database=GmcSmetaProduction;Integrated Security=true";

    [Fact]
    public void WpfSettingsRoundtripStaysInSeparateFile()
    {
        using var scratch = new Scratch();
        var store = new WpfSettingsStore(Path.Combine(scratch.Root, "GMC-Smeta-Next", "settings.json"));
        var initial = store.Load();
        Assert.False(initial.Recovered);
        Assert.Equal("Segoe UI", initial.Settings.FontFamily);

        var value = new WpfSettings
        {
            FontFamily = "Arial", Scale = 1.25m, Theme = "dark",
            ExportDirectory = scratch.Root, ConfirmDeletion = false, ShowHelpHints = false
        };
        store.Save(value);
        var loaded = store.Load();
        Assert.False(loaded.Recovered);
        Assert.Equal(value, loaded.Settings);
        Assert.False(File.Exists(Path.Combine(scratch.Root, "GMC-Smeta", "settings.json")));
    }

    [Fact]
    public void WindowGeometryRoundtripsAndOutOfRangeIsRejected()
    {
        using var scratch = new Scratch();
        var store = new WpfSettingsStore(Path.Combine(scratch.Root, "settings.json"));
        Assert.Equal(0d, store.Load().Settings.WindowWidth);

        store.Save(new WpfSettings { WindowWidth = 1720, WindowHeight = 980, WindowMaximized = true });
        var loaded = store.Load().Settings;
        Assert.Equal(1720, loaded.WindowWidth);
        Assert.Equal(980, loaded.WindowHeight);
        Assert.True(loaded.WindowMaximized);

        Assert.Throws<InvalidDataException>(() =>
            store.Save(new WpfSettings { WindowWidth = 100, WindowHeight = 980 }));
        Assert.Throws<InvalidDataException>(() =>
            store.Save(new WpfSettings { WindowWidth = 1720, WindowHeight = 100 }));
        Assert.Equal(1720, store.Load().Settings.WindowWidth);
    }

    [Fact]
    public void InvalidScaleAndRelativeExportAreRejectedBeforeWriting()
    {
        using var scratch = new Scratch();
        var file = Path.Combine(scratch.Root, "settings.json");
        var store = new WpfSettingsStore(file);
        Assert.Throws<InvalidDataException>(() => store.Save(new WpfSettings { Scale = 0.5m }));
        Assert.Throws<InvalidDataException>(() =>
            store.Save(new WpfSettings { ExportDirectory = "relative/path" }));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void CorruptionQuarantinesOriginalAndUsesLastValidBackup()
    {
        using var scratch = new Scratch();
        var file = Path.Combine(scratch.Root, "settings.json");
        var store = new WpfSettingsStore(file);
        store.Save(new WpfSettings { Scale = 1.15m });
        store.Save(new WpfSettings { Scale = 1.40m });
        Assert.True(File.Exists(file + ".bak"));
        File.WriteAllText(file, "{not json");
        var loaded = store.Load();
        Assert.True(loaded.Recovered);
        Assert.Equal(1.15m, loaded.Settings.Scale);
        Assert.NotNull(loaded.CorruptCopyPath);
        Assert.True(File.Exists(loaded.CorruptCopyPath!));
        Assert.False(File.Exists(file)); // never silently clobber the damaged original
    }

    [Fact]
    public void BackupRemainsUsableAfterQuarantineAndRestart()
    {
        using var scratch = new Scratch();
        var file = Path.Combine(scratch.Root, "settings.json");
        var store = new WpfSettingsStore(file);
        store.Save(new WpfSettings { Scale = 1.15m });
        store.Save(new WpfSettings { Scale = 1.40m });
        File.WriteAllText(file, "{\"export_directory\":null}");
        var recovered = store.Load();
        Assert.True(recovered.Recovered);
        Assert.Equal(1.15m, recovered.Settings.Scale);
        Assert.False(File.Exists(file));
        var nextLaunch = new WpfSettingsStore(file).Load();
        Assert.True(nextLaunch.Recovered);
        Assert.Equal(1.15m, nextLaunch.Settings.Scale);
    }

    [Fact]
    public void InvalidSettingsValuesAreRejectedBeforeWriting()
    {
        using var scratch = new Scratch();
        var file = Path.Combine(scratch.Root, "settings.json");
        var store = new WpfSettingsStore(file);
        Assert.Throws<InvalidDataException>(() =>
            store.Save(new WpfSettings { ExportDirectory = null! }));
        Assert.Throws<InvalidDataException>(() =>
            store.Save(new WpfSettings { WindowWidth = double.NaN, WindowHeight = 980 }));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void SettingsDiskFailureDoesNotClaimSuccess()
    {
        using var scratch = new Scratch();
        var blocker = Path.Combine(scratch.Root, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var store = new WpfSettingsStore(Path.Combine(blocker, "settings.json"));
        Assert.Throws<IOException>(() => store.Save(new WpfSettings()));
    }

    [Fact]
    public void ErrorLogUsesCorrelationAndDoesNotSerializeSecrets()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Root, "logs", "errors.jsonl");
        var log = new ErrorLog(path);
        var id = Guid.Parse("89af71e9-794f-4245-ad8e-51f1dbb28845");
        var result = log.Record(ErrorCategory.SqlCommitUnknown,
            new InvalidOperationException("Password=TOP_SECRET; User=Alice; Server=prod"), id);
        Assert.True(result.Saved);
        Assert.Equal(id, result.CorrelationId);
        Assert.Contains(id.ToString("D"), result.Notification);
        Assert.Contains("перечитайте", result.Notification);
        var content = File.ReadAllText(path);
        Assert.Contains(id.ToString("D"), content);
        Assert.Contains("SqlCommitUnknown", content);
        Assert.DoesNotContain("TOP_SECRET", content);
        Assert.DoesNotContain("Alice", content);
        Assert.DoesNotContain("prod", content);
    }

    [Fact]
    public void ErrorLogDiskFailureIsVisible()
    {
        using var scratch = new Scratch();
        var blocker = Path.Combine(scratch.Root, "blocker");
        File.WriteAllText(blocker, "x");
        var result = new ErrorLog(Path.Combine(blocker, "errors.jsonl"))
            .Record(ErrorCategory.FilePermission, new IOException("secret"));
        Assert.False(result.Saved);
        Assert.Contains("недоступен", result.Notification);
    }

    [Fact]
    public void PilotPackageHashesRoundtripAndExtractsOnlyIntoNewPilotDirectory()
    {
        using var scratch = new Scratch();
        var source = Path.Combine(scratch.Root, "GmcSmetaPilot_Source");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "one.txt"), "synthetic test attachment", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(source, "sub", "two.bin"), [0, 1, 2, 3, 255]);
        var archive = Path.Combine(scratch.Root, "pilot.zip");
        var written = BackupPilotPackage.Create(PilotConnection, source, archive);
        var checkedManifest = BackupPilotPackage.Verify(PilotConnection, archive);
        Assert.Equal(2, written.Files.Count);
        Assert.Equal(written.CreatedUtc, checkedManifest.CreatedUtc);
        Assert.All(checkedManifest.Files, item => Assert.Equal(64, item.Sha256.Length));

        var dest = Path.Combine(scratch.Root, "GmcSmetaPilot_Restore");
        var restored = BackupPilotPackage.ExtractToNewPilotDirectory(PilotConnection, archive, dest);
        Assert.Equal(written.Files.Count, restored.Files.Count);
        Assert.Equal(File.ReadAllBytes(Path.Combine(source, "sub", "two.bin")),
            File.ReadAllBytes(Path.Combine(dest, "sub", "two.bin")));
        Assert.Equal(File.ReadAllText(Path.Combine(source, "one.txt")),
            File.ReadAllText(Path.Combine(dest, "one.txt")));
        Assert.Throws<IOException>(() =>
            BackupPilotPackage.ExtractToNewPilotDirectory(PilotConnection, archive, dest));
    }

    [Fact]
    public void ProductionIsRejectedBeforeArchiveOrExtraction()
    {
        using var scratch = new Scratch();
        var source = Path.Combine(scratch.Root, "GmcSmetaPilot_Source");
        Directory.CreateDirectory(source);
        var archive = Path.Combine(scratch.Root, "pilot.zip");
        Assert.Throws<InvalidOperationException>(() =>
            BackupPilotPackage.Create(ProductionConnection, source, archive));
        Assert.False(File.Exists(archive));
        Assert.Throws<InvalidOperationException>(() =>
            BackupPilotPackage.ExtractToNewPilotDirectory(ProductionConnection, archive,
                Path.Combine(scratch.Root, "GmcSmetaPilot_Restore")));
    }

    [Fact]
    public void CorruptedEntryFailsHashAndDoesNotPublishDestination()
    {
        using var scratch = new Scratch();
        var source = Path.Combine(scratch.Root, "GmcSmetaPilot_Source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "document.txt"), "original");
        var archive = Path.Combine(scratch.Root, "pilot.zip");
        BackupPilotPackage.Create(PilotConnection, source, archive);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            zip.GetEntry("files/document.txt")!.Delete();
            var replacement = zip.CreateEntry("files/document.txt");
            using var writer = new StreamWriter(replacement.Open());
            writer.Write("tampered");
        }
        Assert.Throws<InvalidDataException>(() => BackupPilotPackage.Verify(PilotConnection, archive));
        var dest = Path.Combine(scratch.Root, "GmcSmetaPilot_Restore");
        Assert.Throws<InvalidDataException>(() =>
            BackupPilotPackage.ExtractToNewPilotDirectory(PilotConnection, archive, dest));
        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public void ZipTraversalAndExtraEntriesAreRejected()
    {
        using var scratch = new Scratch();
        var archive = Path.Combine(scratch.Root, "attack.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var manifest = new PilotBackupManifest(1, "GmcSmetaPilot_Issue67",
                "pilot-attachments-only", DateTimeOffset.UtcNow,
                [new PilotBackupEntry("../escape.txt", 1, new string('0', 64))]);
            using (var output = zip.CreateEntry("manifest.json").Open())
                JsonSerializer.Serialize(output, manifest);
            using var text = new StreamWriter(zip.CreateEntry("files/../escape.txt").Open());
            text.Write("X");
        }
        Assert.Throws<InvalidDataException>(() => BackupPilotPackage.Verify(PilotConnection, archive));
        Assert.False(File.Exists(Path.Combine(scratch.Root, "escape.txt")));
    }

    [Fact]
    public void UnavailableNetworkLikeDestinationIsNotSilentlyIgnored()
    {
        using var scratch = new Scratch();
        var source = Path.Combine(scratch.Root, "GmcSmetaPilot_Source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "test");
        var blocker = Path.Combine(scratch.Root, "not-a-directory");
        File.WriteAllText(blocker, "x");
        Assert.Throws<IOException>(() =>
            BackupPilotPackage.Create(PilotConnection, source, Path.Combine(blocker, "pilot.zip")));
    }

    [Fact]
    public void TrashPreflightAcceptsOnlyIndependentOpenKp()
    {
        using var snapshot = JsonDocument.Parse("""
            {
              "objects": [],
              "contracts": [{"id": 5, "object_id": 2, "kind": "kp", "locked": false}],
              "systems": [{"id": 6, "contract_id": 5}],
              "positions": [{"id": 7, "system_id": 6}]
            }
            """);
        TrashRestoreSafety.ValidateSnapshot(snapshot.RootElement, "contract");
    }

    [Fact]
    public void TrashPreflightRejectsSignedAndCrossContractFk()
    {
        using var signed = JsonDocument.Parse("""
            {"contracts":[{"id":1,"kind":"kp","locked":true}]}
            """);
        Assert.Throws<InvalidOperationException>(() =>
            TrashRestoreSafety.ValidateSnapshot(signed.RootElement, "contract"));

        using var linked = JsonDocument.Parse("""
            {"contracts":[{"id":1,"kind":"kp","locked":false}],
             "systems":[{"id":2,"contract_id":999}]}
            """);
        Assert.Throws<InvalidOperationException>(() =>
            TrashRestoreSafety.ValidateSnapshot(linked.RootElement, "contract"));

        using var withAct = JsonDocument.Parse("""
            {"contracts":[{"id":1,"kind":"kp","locked":false}],
             "acts":[{"id":8,"signed":true}]}
            """);
        Assert.Throws<InvalidOperationException>(() =>
            TrashRestoreSafety.ValidateSnapshot(withAct.RootElement, "contract"));
    }

    [Fact]
    public void TrashPreflightRejectsArchivedObjectAndForeignHistory()
    {
        using var archived = JsonDocument.Parse("""
            {"objects":[{"id":3,"archived":true}]}
            """);
        Assert.Throws<InvalidOperationException>(() =>
            TrashRestoreSafety.ValidateSnapshot(archived.RootElement, "object"));

        using var history = JsonDocument.Parse("""
            {"objects":[{"id":3,"archived":false}],
             "movements":[{"id":99,"position_id":3}]}
            """);
        Assert.Throws<InvalidOperationException>(() =>
            TrashRestoreSafety.ValidateSnapshot(history.RootElement, "object"));
    }

    [Fact]
    public void TrashPreflightRejectsUnknownSchemaTables()
    {
        using var unknown = JsonDocument.Parse("""
            {"objects":[{"id":3,"archived":false}],
             "new_linked_records":[]}
            """);
        Assert.Throws<InvalidDataException>(() =>
            TrashRestoreSafety.ValidateSnapshot(unknown.RootElement, "object"));
    }

    [Fact]
    public void TrashPreflightRejectsDuplicateSnapshotGroups()
    {
        using var duplicate = JsonDocument.Parse("""
            {"objects":[{"id":3,"archived":false}],
             "objects":[{"id":4,"archived":false}]}
            """);
        Assert.Throws<InvalidDataException>(() =>
            TrashRestoreSafety.ValidateSnapshot(duplicate.RootElement, "object"));
    }

    private sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "GmcSmetaIssue67-" + Guid.NewGuid().ToString("N"));

        public Scratch() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
