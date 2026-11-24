using System.Text;
using System.Text.Json;

namespace GmcSmeta.Core;

/// <summary>Fixed event identifiers; never serialize Exception.Message, stack, SQL or user input.</summary>
public enum ErrorCategory
{
    SqlFailure,
    SqlCommitUnknown,
    SettingsFile,
    BackupIntegrity,
    TrashRejected,
    FilePermission,
    Unexpected
}

public sealed record ErrorLogReceipt(Guid CorrelationId, bool Saved, string Notification);

/// <summary>
/// Locally structured, privacy-reduced diagnostic events. Reporting failure remains visible
/// through the receipt; logging must never trigger a retry of an uncertain SQL commit.
/// </summary>
public sealed class ErrorLog
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();
    public string FilePath { get; }

    public ErrorLog(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GMC-Smeta-Next", "logs", "errors.jsonl"));
    }

    public ErrorLogReceipt Record(ErrorCategory category, Exception? cause = null,
        Guid? correlationId = null)
    {
        if (!Enum.IsDefined(category))
            throw new ArgumentOutOfRangeException(nameof(category));

        var id = correlationId ?? Guid.NewGuid();
        var entry = new SafeEntry(DateTimeOffset.UtcNow, id.ToString("D"),
            category.ToString(), SafeExceptionType(cause));
        var json = JsonSerializer.Serialize(entry);
        var persisted = false;

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length >= MaxLogBytes)
                {
                    // Retain only one prior local segment. No remote upload is performed.
                    File.Move(FilePath, FilePath + ".previous", overwrite: true);
                }
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write,
                    FileShare.Read, 4096, FileOptions.WriteThrough);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
                persisted = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Do not leak the file-system path, user name, exception text or SQL command.
            }
        }

        var notice = category == ErrorCategory.SqlCommitUnknown
            ? "Исход записи на сервере неизвестен. Не повторяйте операцию: сначала перечитайте данные."
            : "Операция не выполнена. Данные необходимо проверить перед повтором.";
        if (!persisted)
            notice += " Локальный журнал недоступен.";
        notice += " Код обращения: " + id.ToString("D");
        return new ErrorLogReceipt(id, persisted, notice);
    }

    private static string SafeExceptionType(Exception? cause) => cause switch
    {
        null => "None",
        System.IO.IOException => "IOException",
        UnauthorizedAccessException => "AccessDenied",
        TimeoutException => "Timeout",
        System.Text.Json.JsonException => "JsonException",
        Microsoft.Data.SqlClient.SqlException => "SqlException",
        _ => "Other"
    };

    private sealed record SafeEntry(DateTimeOffset TimestampUtc, string CorrelationId,
        string Category, string ExceptionType);
}
