using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Domain;

/// <summary>Only the fields needed by an object card; never an editable estimate snapshot.</summary>
public sealed record EstimateCardData(string Company, string Form, decimal VatRate, bool VatTop,
    int Systems, int Positions, ContractTotals Totals, EstimateStatistics Statistics)
{
    public static EstimateCardData FromEstimate(EstimateDocument estimate) =>
        new(estimate.Company, estimate.Form, estimate.VatRate, estimate.VatTop,
            estimate.Systems, estimate.Positions, estimate.Totals, estimate.Statistics);
}

public static partial class EstimateReader
{
    // Bound the number of documents buffered at once. The existing financial
    // calculation keeps its decimal arithmetic and original per-document order.
    private const int CardBatchSize = 32;

    public static IReadOnlyDictionary<int, EstimateCardData> LoadCardsSql(
        SqlConnection conn, IEnumerable<int> contractIds) =>
        LoadCardsSql(conn, contractIds, null);

    internal static IReadOnlyDictionary<int, EstimateCardData> LoadCardsSql(
        SqlConnection conn, IEnumerable<int> contractIds, TagRules? knownRules)
    {
        var ids = contractIds.Distinct().OrderBy(id => id).ToArray();
        if (ids.Any(id => id <= 0)) throw new ArgumentOutOfRangeException(nameof(contractIds));
        var result = new Dictionary<int, EstimateCardData>(ids.Length);
        foreach (var batch in ids.Chunk(CardBatchSize))
            ReadCardBatch(conn, batch, result, knownRules);
        return result;
    }

    private static void ReadCardBatch(SqlConnection conn, int[] ids,
        Dictionary<int, EstimateCardData> result, TagRules? knownRules)
    {
        var scope = new ContractReadScope(ids);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = WithDiscountColumn(conn,
            HeadSql.Replace("c.id = @id", scope.Predicate("c.id"), StringComparison.Ordinal) +
            (knownRules is null ? "SELECT [key], rules FROM tags;" : "") +
            "SELECT id, parent_id, smr_amount, contract_id FROM systems WHERE " +
            scope.Predicate("contract_id") + " ORDER BY [order], id;" +
            "SELECT p.id, p.system_id, p.parent_id, p.name, p.qty, p.price, p.out_of_kp, p.work_price, sy.contract_id " +
            "FROM positions p JOIN systems sy ON sy.id = p.system_id WHERE " +
            scope.Predicate("sy.contract_id") + " ORDER BY p.[order], p.id;");
        scope.Bind(cmd);
        using var reader = cmd.ExecuteReader();
        var heads = new Dictionary<int, Head>(ids.Length);
        while (reader.Read())
        {
            var head = ReadHeadCurrent(reader);
            heads.Add(head.ContractId, head);
        }
        foreach (var id in ids)
            if (!heads.ContainsKey(id)) throw new InvalidOperationException($"нет документа {id}");

        void Next()
        {
            if (!reader.NextResult())
                throw new InvalidOperationException("Неполный пакет карточек SQL Server.");
        }

        var rules = knownRules;
        if (rules is null)
        {
            Next();
            rules = TagRules.Read(reader);
        }
        Next();
        var systems = ids.ToDictionary(id => id, _ => new List<SystemRow>());
        while (reader.Read())
            systems[SqlInteger.ReadInt32(reader, 3)].Add(new SystemRow
            {
                Id = SqlInteger.ReadInt32(reader, 0),
                ParentId = reader.IsDBNull(1) ? null : SqlInteger.ReadInt32(reader, 1),
                SmrAmount = Dec(reader, 2)
            });

        Next();
        var positions = ids.ToDictionary(id => id, _ => new List<PositionRow>());
        while (reader.Read())
            positions[SqlInteger.ReadInt32(reader, 8)].Add(new PositionRow
            {
                Id = SqlInteger.ReadInt32(reader, 0),
                SystemId = SqlInteger.ReadInt32(reader, 1),
                ParentId = reader.IsDBNull(2) ? null : SqlInteger.ReadInt32(reader, 2),
                Name = Str(reader, 3),
                Qty = Dec(reader, 4),
                Price = Dec(reader, 5),
                OutOfKp = !reader.IsDBNull(6) && Convert.ToBoolean(reader.GetValue(6)),
                WorkPrice = reader.IsDBNull(7) ? null : Dec(reader, 7)
            });
        reader.Close();

        foreach (var id in ids)
        {
            var head = heads[id];
            var ss = systems[id];
            var pp = positions[id];
            var totals = CalculateTotals(head, rules, ss, pp);

            // Match BuildDocument: only root systems and their immediate
            // subsystems, and only top positions and their immediate children.
            // Financial totals and work rows still cover ALL source rows.
            var roots = ss.Where(s => s.ParentId is null).Select(s => s.Id).ToHashSet();
            var visible = ss.Where(s => s.ParentId is null || roots.Contains(s.ParentId.Value))
                .Select(s => s.Id).ToHashSet();
            var top = pp.Where(p => p.ParentId is null && visible.Contains(p.SystemId))
                .Select(p => (p.SystemId, p.Id)).ToHashSet();
            var count = top.Count + pp.Count(p => p.ParentId is int parent && top.Contains((p.SystemId, parent)));
            result.Add(id, new EstimateCardData(head.Company, head.Form, head.VatRate,
                rules.Has(head.Form, "vat_top"), visible.Count, count, totals, Statistics(totals, pp)));
        }
    }
}
