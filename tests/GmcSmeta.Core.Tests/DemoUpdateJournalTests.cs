using GmcSmeta.Launcher;

namespace GmcSmeta.Core.Tests;

public sealed class DemoUpdateJournalTests
{
    [Fact]
    public void WritesPhasesAndOnlyFreshSmokeDetails()
    {
        var root = Path.Combine(Path.GetTempPath(), "gmc-demo-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var journal = new DemoUpdateJournal(root);
            var report = Path.Combine(root, "smoke-error.txt");
            File.WriteAllText(report, "old-secret-example");
            journal.Write("Кандидат", "1.11.0");
            journal.IncludeSmokeReport(report, false);
            File.WriteAllText(report, "workspace-initialization\nSQL error");
            journal.IncludeSmokeReport(report, true);
            var log = File.ReadAllText(journal.FilePath);
            Assert.Contains("Кандидат: 1.11.0", log);
            Assert.Contains("workspace-initialization SQL error", log);
            Assert.DoesNotContain("old-secret-example", log);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RotatesLargeJournalAndDoesNotThrowWhenUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "gmc-demo-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var journal = new DemoUpdateJournal(root);
            File.WriteAllBytes(journal.FilePath, new byte[(int)DemoUpdateJournal.MaxBytes]);
            journal.Write("Проверка", "Код 1");
            Assert.True(File.Exists(journal.FilePath + ".previous"));
            Assert.Contains("Код 1", File.ReadAllText(journal.FilePath));
            new DemoUpdateJournal(Path.Combine(root, "missing")).Write("Откат", "safe");
        }
        finally { Directory.Delete(root, true); }
    }
}
