using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class ProcurementRequestPlanner
{
    public enum ReportKind { Supplier, Shop, Control }

    public sealed record ReportSnapshot(DocumentInfo Document,
        IReadOnlyList<SqlServerProcurement.BuyRow> Rows,
        IReadOnlySet<long> Children, string Number,
        IReadOnlyDictionary<long, string> IssueDates, string Revision);

    private const string IssueDatesQuery =
        "SELECT m.position_id,m.qty,m.[date] FROM dbo.movements m " +
        "JOIN dbo.positions p ON p.id=m.position_id " +
        "JOIN dbo.systems sy ON sy.id=p.system_id " +
        "WHERE sy.contract_id=@id AND m.kind=N'out' " +
        "ORDER BY m.position_id,m.[date],m.id";

    public static string ReportQuery(ReportKind kind) =>
        DocumentInfoQuery + ";\n" + SqlServerProcurement.BuyRowsQuery() + (kind switch
        {
            ReportKind.Supplier => ";\n" + RequestNumberQuery,
            ReportKind.Shop => "",
            ReportKind.Control => ";\n" + IssueDatesQuery,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });

    public static IReadOnlySet<long> ChildPositionIds(IEnumerable<SqlServerProcurement.BuyRow> rows) =>
        rows.Where(row => row.ParentPositionId.HasValue).Select(row => row.PositionId).ToHashSet();

    /// <summary>One guarded connection, one report data batch, revision fence
    /// on both sides. No write or reservation of the suggested request number.</summary>
    public static ReportSnapshot ReadReport(string connectionString, int contractId,
        ReportKind kind, string? expectedRevision = null, CancellationToken token = default)
    {
        if (contractId <= 0) throw new ArgumentOutOfRangeException(nameof(contractId));
        token.ThrowIfCancellationRequested();
        var query = ReportQuery(kind);
        using var conn = token.CanBeCanceled
            ? SqlServerDatabase.OpenAsync(connectionString, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Open(connectionString);
        string Revision() => token.CanBeCanceled
            ? SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Revision(conn);
        var revision = Revision();
        if (string.IsNullOrWhiteSpace(revision) ||
            expectedRevision is not null && revision != expectedRevision)
            throw new InvalidOperationException("Закупки изменились. Обновите таблицу.");

        var day = DateTime.Today;
        DocumentInfo document;
        IReadOnlyList<SqlServerProcurement.BuyRow> rows;
        var number = "";
        IReadOnlyDictionary<long, string> issues = new Dictionary<long, string>();
        using (var cmd = new SqlCommand(query, conn))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var registration = SqlReadCancellation.Register(cmd, token);
            using var reader = SqlReadCancellation.ExecuteReader(cmd, token);
            document = ReadDocumentInfo(reader);
            NextResult(reader, token);
            rows = SqlServerProcurement.ReadBuyRows(reader, day, token);
            if (kind == ReportKind.Supplier)
            {
                NextResult(reader, token);
                number = ReadNextNumber(reader, token);
            }
            else if (kind == ReportKind.Control)
            {
                NextResult(reader, token);
                issues = ReadIssueDates(reader, token);
            }
        }

        token.ThrowIfCancellationRequested();
        if (Revision() != revision || DateTime.Today != day)
            throw new InvalidOperationException("Данные изменились во время подготовки отчёта. Повторите выгрузку.");
        token.ThrowIfCancellationRequested();
        return new ReportSnapshot(document, rows,
            kind == ReportKind.Supplier ? ChildPositionIds(rows) : new HashSet<long>(),
            number, issues, revision);
    }

    private static void NextResult(IDataReader reader, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!reader.NextResult())
            throw new InvalidOperationException("Не получен полный пакет данных закупок.");
    }

    public static IReadOnlyDictionary<long, string> ReadIssueDates(IDataReader reader, CancellationToken token = default)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var issues = new Dictionary<long, List<string>>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            var qty = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
            var date = reader.IsDBNull(2) ? "б/д" : reader.GetDateTime(2).ToString("dd.MM.yy", ru);
            if (!issues.TryGetValue(id, out var parts))
                issues[id] = parts = new List<string>();
            parts.Add(qty.ToString("0.######", ru) + " (" + date + ")");
        }
        token.ThrowIfCancellationRequested();
        return issues.ToDictionary(kv => kv.Key, kv => string.Join(", ", kv.Value));
    }
}
