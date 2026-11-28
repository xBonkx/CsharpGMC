using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerActs
{
    /// <summary>
    /// Acts of a document with FULL gross totals matching Python Act.total:
    /// material lines, SMR, transport, PNR, VAT-on-top and adjustment.
    /// Uses three sequential readers, including inside an existing transaction.
    /// No per-act round trips and no nested DataReaders.
    /// </summary>
    public static List<ActInfo> ActsOf(SqlConnection conn, long contractId, SqlTransaction? transaction = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ActsOfVersion(conn,contractId,transaction,SqlSchemaCompatibility.HasPreviewColumns(conn)?31:28,token);
    }

    // Internal explicit-version projection for isolated migration finance snapshots.
    // The normal application still uses the existing guarded Open and name policy.
    internal static List<ActInfo> ActsOfVersion(SqlConnection conn,long contractId,
        SqlTransaction? transaction,int schemaVersion, CancellationToken token = default) =>
        ReadActsOfVersion(conn, new[] { contractId }, transaction, schemaVersion, token);

    // Keep multi-document reads bounded: the raw act lines are materialized
    // by the established financial calculator. No arbitrary cross-object
    // selection: the caller supplies the IDs resolved by the legal scope.
    internal const int ActDocumentBatchSize = 32;

    internal static List<ActInfo> ActsOfDocuments(SqlConnection conn,
        IReadOnlyList<long> contractIds, int? rehearsalSchema, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (contractIds.Count == 0) return new();
        if (contractIds.Any(id => id <= 0) || contractIds.Distinct().Count() != contractIds.Count)
            throw new ArgumentException("Недопустимый список документов КС.");
        var schema = rehearsalSchema ?? (SqlSchemaCompatibility.HasPreviewColumns(conn) ? 31 : 28);
        var result = new List<ActInfo>();
        foreach (var batch in contractIds.Chunk(ActDocumentBatchSize))
        {
            token.ThrowIfCancellationRequested();
            result.AddRange(ReadActsOfVersion(conn, batch, null, schema, token));
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static List<ActInfo> ReadActsOfVersion(SqlConnection conn, IReadOnlyList<long> contractIds,
        SqlTransaction? transaction, int schemaVersion, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (contractIds.Count is < 1 or > ActDocumentBatchSize)
            throw new ArgumentOutOfRangeException(nameof(contractIds));
        var names = contractIds.Count == 1 ? new[] { "@id" } :
            contractIds.Select((_, i) => "@id" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var predicate = names.Length == 1 ? "=@id" : " IN (" + string.Join(",", names) + ")";
        void Bind(SqlCommand command)
        {
            for (var i = 0; i < names.Length; i++)
                command.Parameters.Add(names[i], SqlDbType.BigInt).Value = contractIds[i];
        }
        var ruleJson = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var rules = new SqlCommand("SELECT [key],rules FROM dbo.tags", conn, transaction))
        {
            using var cancellation = SqlReadCancellation.Register(rules, token);
            using var reader = SqlReadCancellation.ExecuteReader(rules, token);
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (!reader.IsDBNull(0))
                    ruleJson[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
            }
        }

        _ = SqlMigrationPlan.Script(schemaVersion,schemaVersion);
        var raw = new List<(long Id, string Number, DateTime Date, DateTime? Start, DateTime? End,
            bool Signed, decimal Smr, decimal Transport, decimal Pnr, bool Exact,
            long Adjustment, decimal VatRate, string Form, string DsNo, string DsDate,
            decimal Discount, bool Override, long ManualTotal, string SectionJson, bool BySystem, string DsExtra, long ContractId)>();
        using (var cmd = new SqlCommand(
            "SELECT a.id,a.number,a.[date],a.period_from,a.period_to,a.signed," +
            "ISNULL(a.smr_amount,0),ISNULL(a.transport_amount,0),ISNULL(a.pnr_amount,0)," +
            "ISNULL(a.rows_exact,0),ISNULL(a.gross_adjustment_cents,0)," +
            "ISNULL(c.vat_rate,0),ISNULL(c.form,N''),ISNULL(a.ds_no,N''),ISNULL(a.ds_date,N'')" +
            (schemaVersion>=29?",a.discount_percent":",CAST(0 AS float)")+
            (schemaVersion>=31?",a.discount_override,a.manual_total_cents":",CAST(0 AS bit),CAST(-1 AS bigint)")+
            (schemaVersion>=30?",a.section_amounts_json":",CAST(N'' AS nvarchar(max))")+
            ",a.smr_by_system,ISNULL(a.ds_extra,N''),a.contract_id " +
            "FROM dbo.acts a JOIN dbo.contracts c ON c.id=a.contract_id " +
            "WHERE a.contract_id" + predicate + " ORDER BY a.[date],a.id", conn, transaction))
        {
            Bind(cmd);
            using var cancellation = SqlReadCancellation.Register(cmd, token);
            using var r = SqlReadCancellation.ExecuteReader(cmd, token);
            decimal D(int k) => r.IsDBNull(k) ? 0m :
                Money.FromDouble(Convert.ToDouble(r.GetValue(k), CultureInfo.InvariantCulture));
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                raw.Add((
                    SqlInteger.ReadInt64(r, 0),
                    r.IsDBNull(1) ? "" : Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture) ?? "",
                    r.IsDBNull(2) ? DateTime.Today : Convert.ToDateTime(r.GetValue(2), CultureInfo.InvariantCulture),
                    r.IsDBNull(3) ? null : Convert.ToDateTime(r.GetValue(3), CultureInfo.InvariantCulture),
                    r.IsDBNull(4) ? null : Convert.ToDateTime(r.GetValue(4), CultureInfo.InvariantCulture),
                    !r.IsDBNull(5) && SqlFlag.Read(r, 5),
                    D(6), D(7), D(8),
                    !r.IsDBNull(9) && SqlFlag.Read(r, 9),
                    r.IsDBNull(10) ? 0L : SqlInteger.ReadInt64(r, 10),
                    D(11), r.IsDBNull(12) ? "" : r.GetString(12),
                    r.IsDBNull(13) ? "" : r.GetString(13), r.IsDBNull(14) ? "" : r.GetString(14),
                    DiscountPolicy.ReadPercent(r,15),
                    !r.IsDBNull(16)&&SqlFlag.Read(r, 16),
                    !r.IsDBNull(17)?SqlInteger.ReadInt64(r, 17):-1L,
                    !r.IsDBNull(18)?r.GetString(18):"",
                    !r.IsDBNull(19)&&SqlFlag.Read(r, 19),
                    r.GetString(20), SqlInteger.ReadInt64(r, 21)));
            }
        }

        var lines = new Dictionary<long, List<(decimal Quantity, decimal Price)>>();
        using (var command = new SqlCommand(
            "SELECT al.act_id,al.qty,al.price FROM dbo.act_lines al " +
            "JOIN dbo.acts a ON a.id=al.act_id WHERE a.contract_id" + predicate, conn, transaction))
        {
            Bind(command);
            using var cancellation = SqlReadCancellation.Register(command, token);
            using var r = SqlReadCancellation.ExecuteReader(command, token);
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                var id = SqlInteger.ReadInt64(r, 0);
                var qty = r.IsDBNull(1) ? 0m : decimal.Parse(
                    Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture);
                var price = r.IsDBNull(2) ? 0m : decimal.Parse(
                    Convert.ToDouble(r.GetValue(2), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!lines.TryGetValue(id, out var list))
                    lines[id] = list = new List<(decimal Quantity, decimal Price)>();
                list.Add((qty, price));
            }
        }

        var acts = new List<ActInfo>(raw.Count);
        foreach (var a in raw)
        {
            token.ThrowIfCancellationRequested();
            var values = lines.GetValueOrDefault(a.Id) ?? new List<(decimal Quantity, decimal Price)>();
            var json = ruleJson.GetValueOrDefault(a.Form);
            var vatTop = KsFinancialPolicy.HasRule(json, "vat_top") || a.Form == "dns";
            var rowsExact = a.Exact;
            var mat = KsFinancialPolicy.Materials(values, rowsExact,
                KsFinancialPolicy.HasRule(json, "round_rows"));
            var sum = KsFinancialPolicy.Calculate(mat, a.Smr, a.Transport, a.Pnr,
                a.VatRate, vatTop, a.Adjustment, a.Discount, a.ManualTotal);
            acts.Add(new ActInfo(a.Id, a.Number, a.Date, a.Start, a.End,
                a.Signed, values.Count, sum.Gross,
                a.ContractId, "", sum.Ks3Net, sum.Vat, sum.Materials,
                sum.Smr, sum.Transport, sum.Pnr, a.VatRate, vatTop, a.Adjustment,
                a.Exact, KsFinancialPolicy.HasRule(json, "round_rows"),
                a.DsNo, a.DsDate, a.Discount, a.Override, a.ManualTotal, a.SectionJson, a.BySystem) { DsExtra = a.DsExtra });
        }
        token.ThrowIfCancellationRequested();
        return acts;
    }
}
