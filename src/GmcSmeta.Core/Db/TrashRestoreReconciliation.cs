using System.Data;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Thrown when the connection cannot confirm COMMIT. The caller MUST NOT retry the
/// restore; it must refresh server state or escalate with a correlation ID.
/// </summary>
public sealed class TrashCommitUnknownException : Exception
{
    public TrashCommitUnknownException(Exception cause)
        : base("Неизвестный результат SQL COMMIT: нужна сверка состояния сервера.", cause) { }
}

public static partial class SqlServerTrash
{
    /// <summary>
    /// Read-only reconciliation on a NEW SQL connection after an uncertain COMMIT.
    /// Confirm success only when the generated ID exists and its trash item is gone.
    /// Never replay an INSERT, DELETE or UPDATE here.
    /// </summary>
    private static bool RestoreReadbackConfirmed(string connectionString, string kind,
        long createdId, long trashId)
    {
        try
        {
            SqlServerWorkspace.RequirePilotDatabase(connectionString);
            using var conn = SqlServerDatabase.Open(connectionString);
            var table = kind == "object" ? "objects" :
                kind == "contract" ? "contracts" :
                throw new InvalidDataException("Неизвестный тип восстановления.");
            using var cmd = new SqlCommand(
                $"SELECT CASE WHEN EXISTS(SELECT 1 FROM dbo.[{table}] WHERE id=@created) " +
                "AND NOT EXISTS(SELECT 1 FROM dbo.trash WHERE id=@trash) " +
                "THEN 1 ELSE 0 END", conn);
            cmd.Parameters.Add("@created", SqlDbType.BigInt).Value = createdId;
            cmd.Parameters.Add("@trash", SqlDbType.BigInt).Value = trashId;
            return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
        }
        catch (Exception ex) when (ex is SqlException or IOException or InvalidOperationException)
        {
            return false; // cannot establish success, so the caller reports UNKNOWN
        }
    }
}
