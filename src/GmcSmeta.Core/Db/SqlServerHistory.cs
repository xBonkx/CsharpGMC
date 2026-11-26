using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Чтение журнала изменений SQL Server — перенос history_dialog.show_history.
/// Только SELECT: история одного документа вместе со строками его объекта либо
/// последние записи всей базы, свежие сверху.
/// </summary>
public static class SqlServerHistory
{
    public const int Limit = 3000;

    public sealed record HistoryEntry(long Id, DateTime? Ts, string User, string Entity,
        long? EntityId, string Action, string Details, long? ContractId, long? ObjectId);

    public sealed record HistoryScope(string Title, string ObjectName);

    /// <summary>
    /// Заголовок и объект документа для окна истории. null — документ не найден
    /// (Python в этом случае просто не открывает окно).
    /// </summary>
    public static HistoryScope? ContractScope(string connectionString, long contractId,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var conn = token.CanBeCanceled
            ? SqlServerDatabase.OpenAsync(connectionString, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Open(connectionString);
        using var cmd = new SqlCommand(
            "SELECT c.kind,c.number,c.[date],c.name,o.name FROM dbo.contracts c " +
            "JOIN dbo.objects o ON o.id=c.object_id WHERE c.id=@id", conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var registration = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        if (!r.Read()) { token.ThrowIfCancellationRequested(); return null; }
        token.ThrowIfCancellationRequested();
        string Text(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
        return new HistoryScope(
            Documents.ContractTitleText(Text(0), Text(1), Text(2), Text(3)), Text(4));
    }

    /// <summary>
    /// Записи change_log по убыванию id. Для документа — его собственные строки плюс
    /// строки без contract_id, привязанные к объекту документа (ведомости, объект,
    /// выдача); для null — последние limit записей всей базы. Пустая таблица — пустой
    /// список, не исключение.
    /// </summary>
    public static IReadOnlyList<HistoryEntry> List(string connectionString,
        long? contractId = null, int limit = Limit, CancellationToken token = default)
    {
        if (limit is < 1 or > Limit) throw new ArgumentOutOfRangeException(nameof(limit));
        token.ThrowIfCancellationRequested();
        using var conn = token.CanBeCanceled
            ? SqlServerDatabase.OpenAsync(connectionString, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Open(connectionString);
        using var cmd = new SqlCommand(
            "SELECT TOP (@limit) id,ts,[user],entity,entity_id,action,details,contract_id,object_id " +
            "FROM dbo.change_log " +
            "WHERE @contractId IS NULL " +
            "   OR contract_id=@contractId " +
            "   OR (contract_id IS NULL AND object_id=" +
            "       (SELECT object_id FROM dbo.contracts WHERE id=@contractId)) " +
            "ORDER BY id DESC", conn) { CommandTimeout = 20 };
        cmd.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        cmd.Parameters.Add("@contractId", SqlDbType.BigInt).Value =
            contractId is long id ? id : DBNull.Value;

        using var registration = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        var rows = new List<HistoryEntry>();
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            string Text(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            long? Id(int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
            rows.Add(new HistoryEntry(
                Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                r.IsDBNull(1) ? null : Convert.ToDateTime(r.GetValue(1), CultureInfo.InvariantCulture),
                Text(2), Text(3), Id(4), Text(5), Text(6), Id(7), Id(8)));
        }
        token.ThrowIfCancellationRequested();
        return rows;
    }
}
