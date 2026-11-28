using System.IO;

namespace GmcSmeta.Launcher;

/// <summary>Best-effort local DEMO launcher journal. Never affects update decisions.</summary>
internal sealed class DemoUpdateJournal
{
    internal const long MaxBytes = 256 * 1024;
    private const int MaxDetailChars = 4000;
    internal string FilePath { get; }
    private readonly string _attempt = Guid.NewGuid().ToString("N")[..8];

    internal DemoUpdateJournal(string root) =>
        FilePath = Path.Combine(root, "update-journal.log");

    internal void Write(string phase, string detail)
    {
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length >= MaxBytes)
                File.Move(FilePath, FilePath + ".previous", overwrite: true);
            // Keep each entry on one line and bound messages from external processes.
            var safe = detail.Replace("\r", " ").Replace("\n", " ");
            if (safe.Length > MaxDetailChars) safe = safe[..MaxDetailChars] + "…";
            File.AppendAllText(FilePath, $"{DateTime.UtcNow:O} [{_attempt}] {phase}: {safe}{Environment.NewLine}");
        }
        catch { /* a locked or full journal must never block rollback or launch */ }
    }

    internal void IncludeSmokeReport(string path, bool fresh)
    {
        if (!fresh)
        {
            Write("Диагностика кандидата", "Не удалось очистить предыдущий отчёт; старые данные не используются.");
            return;
        }
        try
        {
            if (!File.Exists(path))
            {
                Write("Диагностика кандидата", "Новая сборка не записала подробности. Причина не установлена.");
                return;
            }
            using var reader = File.OpenText(path);
            var buffer = new char[MaxDetailChars];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            Write("Диагностика кандидата", new string(buffer, 0, count));
        }
        catch { Write("Диагностика кандидата", "Не удалось прочитать отчёт smoke."); }
    }
}
