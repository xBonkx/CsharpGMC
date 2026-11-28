using System.Data;
using System.Globalization;
using System.Text.Json;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.RemoteSqlBench;

// Independent correlated SQL reference: same row shape as the actual reader,
// not string replacement of the production aggregate. No data leaves this process.
internal static class SqlDiagnosticQueries
{
    internal const string BuyReferenceSql = """
        SELECT p.id,sy.name,p.name,p.unit,p.qty,p.price,p.out_of_kp,
          ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'in'),0)
          - ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'back_supplier'),0),
          ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'out'),0)
          - ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'back'),0),
          ISNULL(pr.stage,N''),ISNULL(pr.order_ref,N''),ISNULL(pr.invoice_ref,N''),pr.cost_price,
          pr.due_date,ISNULL(pr.replacement,N''),ISNULL(pr.note,N''),ISNULL(pr.direct_to_site,0),
          sup.name,ISNULL(pr.source,N'buy'),sy.id,sy.parent_id,
          (SELECT MAX(m.[date]) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'out'),
          ISNULL(pr.buy_name,N''),pr.paid_date,ISNULL(pr.responsible,N''),p.parent_id,
          ISNULL(pp.name,N''),sy.[order],p.[order]
        FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id
        LEFT JOIN dbo.positions pp ON pp.id=p.parent_id
        LEFT JOIN dbo.procurement pr ON pr.position_id=p.id
        LEFT JOIN dbo.suppliers sup ON sup.id=pr.supplier_id
        WHERE sy.contract_id=@id ORDER BY sy.[order],sy.id,p.[order],p.id;
        """;

    internal static bool SameBuyRow(SqlServerProcurement.BuyRow expected, SqlServerProcurement.BuyRow actual) =>
        expected.Flags.SequenceEqual(actual.Flags, StringComparer.Ordinal) &&
        expected == (actual with { Flags = expected.Flags });

    internal sealed record BuyParity(int ExpectedRows, int ActualRows, int Mismatches, bool RevisionStable)
    {
        public bool Passed => RevisionStable && ExpectedRows > 0 && Mismatches == 0;
    }

    internal static BuyParity CompareBuyRows(SqlConnection conn, int id)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        var revision = SqlServerDatabase.Revision(conn);
        var day = DateTime.Today;
        List<SqlServerProcurement.BuyRow> Read(string sql)
        {
            using var command = new SqlCommand(sql, conn) { CommandTimeout = 120 };
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            using var reader = command.ExecuteReader();
            var rows = new List<SqlServerProcurement.BuyRow>();
            while (reader.Read()) rows.Add(SqlServerProcurement.MapBuyRow(reader, day));
            return rows;
        }
        var expected = Read(BuyReferenceSql);
        var actual = Read(SqlServerProcurement.BuyRowsQuery());
        var mismatches = Math.Abs(expected.Count - actual.Count);
        for (var i = 0; i < Math.Min(expected.Count, actual.Count); i++)
            if (!SameBuyRow(expected[i], actual[i])) mismatches++;
        return new(expected.Count, actual.Count, mismatches,
            !string.IsNullOrWhiteSpace(revision) && revision == SqlServerDatabase.Revision(conn) && day == DateTime.Today);
    }

    internal static SqlCommand DirectorReferenceCommand(SqlConnection conn, IReadOnlyList<long> ids, bool countLate)
    {
        // Use the same parameter contract, but independent correlated movement reads.
        var command = SqlServerDirector.CreateContractMetricsReadCommand(conn, ids, countLate);
        var names = string.Join(",", command.Parameters.Cast<SqlParameter>().Select(p => p.ParameterName));
        command.CommandTimeout = 120;
        command.CommandText = $"""
            SELECT sy.contract_id,COALESCE(SUM(p.qty*ISNULL(p.work_cost,0)),0)
            FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id
            WHERE sy.contract_id IN ({names}) AND p.parent_id IS NULL AND p.out_of_kp=0 GROUP BY sy.contract_id;
            SELECT contract_id,COALESCE(SUM(amount),0) FROM dbo.smr_costs
            WHERE contract_id IN ({names}) GROUP BY contract_id;
            SELECT sy.contract_id,p.name,p.qty,p.price,
                ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'out'),0)
                - ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'back'),0)
            """ + (countLate ? """
                ,ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'in'),0)
                - ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'back_supplier'),0),
                pr.due_date,ISNULL(pr.stage,N'')
            """ : "") + """
             FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id
            """ + (countLate ? " LEFT JOIN dbo.procurement pr ON pr.position_id=p.id " : " ") +
            $"WHERE sy.contract_id IN ({names}) AND p.parent_id IS NULL AND ISNULL(p.out_of_kp,0)=0;";
        return command;
    }

    // These metrics are aggregate inputs; SQL does not promise result row order.
    // Compare multisets (including duplicates) separately for each result set.
    // Numeric movement/amount values use the same conversion as Director.
    internal static string[][] ReadDirectorInputs(SqlCommand command)
    {
        using var reader = command.ExecuteReader();
        var result = new List<string[]>();
        do
        {
            var rows = new List<string>();
            while (reader.Read())
            {
                var values = new object?[reader.FieldCount];
                for (var i = 0; i < values.Length; i++)
                {
                    if (reader.IsDBNull(i)) { values[i] = null; continue; }
                    var value = reader.GetValue(i);
                    values[i] = value is double or float or decimal
                        ? Money.FromDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture)) : value;
                }
                rows.Add(JsonSerializer.Serialize(values));
            }
            result.Add(rows.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        } while (reader.NextResult());
        return result.ToArray();
    }

    internal sealed record DirectorParity(int Contracts, int ExpectedRows, int ActualRows,
        bool EqualInputs, bool RevisionStable)
    {
        public bool Passed => EqualInputs && RevisionStable && ExpectedRows > 0;
    }

    internal static DirectorParity CompareDirector(SqlConnection conn, IReadOnlyList<long> ids, bool countLate)
    {
        var before = SqlServerDatabase.Revision(conn);
        using var expectedCommand = DirectorReferenceCommand(conn, ids, countLate);
        var expected = ReadDirectorInputs(expectedCommand);
        using var actualCommand = SqlServerDirector.CreateContractMetricsReadCommand(conn, ids, countLate);
        actualCommand.CommandTimeout = 120;
        var actual = ReadDirectorInputs(actualCommand);
        return new(ids.Count, expected.Sum(x => x.Length), actual.Sum(x => x.Length),
            expected.Length == 3 && actual.Length == 3 &&
            expected.Zip(actual).All(x => x.First.SequenceEqual(x.Second, StringComparer.Ordinal)),
            !string.IsNullOrWhiteSpace(before) && before == SqlServerDatabase.Revision(conn));
    }

    // Same active object and latest root KP selection as the actual reader.
    // Limit only the diagnostics scope, not production reporting.
    internal static long[] DirectorScope(SqlConnection conn, bool countLate, int limit)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        using var cmd = new SqlCommand("""
            SELECT c.id,c.object_id,c.[order],c.kind,c.stage_of
            FROM dbo.contracts c JOIN dbo.objects o ON o.id=c.object_id
            WHERE o.archived=0 ORDER BY c.[order],c.id
            """, conn);
        using var reader = cmd.ExecuteReader();
        var docs = new List<(long Id, long Object, string Kind, bool Stage)>();
        while (reader.Read()) docs.Add((Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            reader.IsDBNull(3) ? "kp" : reader.GetString(3), !reader.IsDBNull(4)));
        var ids = countLate ? docs.Where(d => d.Kind is "main" or "dop").Select(d => d.Id)
            : docs.Where(d => d.Kind == "kp" && !d.Stage).GroupBy(d => d.Object).Select(g => g.Last().Id);
        return ids.Take(limit).ToArray();
    }
}
