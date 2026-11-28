using System.Data.Common;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Domain;

public static partial class EstimateReader
{
    // PERF-014: точечное авторитетное перечитывание после одиночных правок.
    // Whitelist только для ячеек SaveCell: позиционный уровень, без изменения
    // структуры (system_id/parent_id/order/num/out_of_kp не перезаписываются).
    // Остальные поля и любые структурные операции — полный reload.
    public static bool IsSparseReadbackSafe(string field) =>
        field is "qty" or "price" or "purchase_price" or "work_price"
            or "name" or "unit" or "note";

    // Те же колонки, что в полном BatchSql: свежая строка сравнима с полной
    // загрузкой по всем полям, включая formulas/area_manual/basis/extra_json.
    private const string SparsePositionsSql =
        "SELECT p.id, p.system_id, p.parent_id, p.num, p.name, p.unit, p.qty, p.price, " +
        "p.purchase_price, p.out_of_kp, p.note, p.\"order\", p.work_price, " +
        "p.area_manual, p.basis, p.extra_json, p.formulas " +
        "FROM positions p JOIN systems sy ON sy.id = p.system_id " +
        "WHERE sy.contract_id = @id AND p.id IN ({0}) ORDER BY p.\"order\", p.id";

    private const int SparseChunkSize = 100;

    /// <summary>
    /// Офлайн-вариант для тестов и фиксаций: свежие строки изменённых позиций
    /// плюс авторитетные итоги документа. null — обязательный полный reload.
    /// </summary>
    public static EstimateDocument? ReadSparse(DbConnection conn, int contractId,
        EstimateDocument current, IReadOnlyCollection<long> positionIds, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var ids = NormalizeSparseIds(contractId, current, positionIds);
        if (ids is null) return null;
        var fresh = new Dictionary<int, PositionRow>(ids.Count);
        foreach (var chunk in ids.Chunk(SparseChunkSize))
        {
            using var cmd = CreateSparseCommand(conn, contractId, chunk);
            using var r = cmd.ExecuteReader();
            while (r.Read()) fresh[SqlInteger.ReadInt32(r, 0)] = ReadPositionCurrent(r);
        }
        var totals = Totals.ReadOne(conn, contractId);
        if (totals is null) return null;
        return TryBuildSparse(current, fresh, totals, TagRules.Load(conn), ids.Count, token);
    }

    /// <summary>
    /// SQL Server: свежие строки изменённых позиций одним-двумя ограниченными
    /// запросами плюс авторитетные итоги через ReadOneWithRules. null — полный
    /// reload (структура сдвинулась, позиция исчезла или итоги недоступны).
    /// </summary>
    public static async Task<EstimateDocument?> ReadSparseSqlAsync(SqlConnection conn, int contractId,
        EstimateDocument current, IReadOnlyCollection<long> positionIds, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var ids = NormalizeSparseIds(contractId, current, positionIds);
        if (ids is null) return null;
        var fresh = new Dictionary<int, PositionRow>(ids.Count);
        foreach (var chunk in ids.Chunk(SparseChunkSize))
        {
            using var cmd = CreateSparseCommand(conn, contractId, chunk);
            using var r = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await r.ReadAsync(token).ConfigureAwait(false))
                fresh[SqlInteger.ReadInt32(r, 0)] = ReadPositionCurrent(r);
        }
        var (totals, rules) = Totals.ReadOneWithRules(conn, contractId, token);
        if (totals is null) return null;
        return TryBuildSparse(current, fresh, totals, rules, ids.Count, token);
    }

    private static List<long>? NormalizeSparseIds(int contractId,
        EstimateDocument current, IReadOnlyCollection<long> positionIds)
    {
        if (current.ContractId != contractId || positionIds.Count == 0) return null;
        var ids = positionIds.Distinct().OrderBy(id => id).ToList();
        if (ids.Any(id => id is < 1 or > int.MaxValue)) return null;
        return ids;
    }

    private static DbCommand CreateSparseCommand(DbConnection conn, int contractId, long[] chunk)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = string.Format(SparsePositionsSql,
            string.Join(",", chunk.Select((_, i) => "@p" + i)));
        var contract = cmd.CreateParameter();
        contract.ParameterName = "@id";
        contract.Value = contractId;
        cmd.Parameters.Add(contract);
        for (var i = 0; i < chunk.Length; i++)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "@p" + i;
            parameter.Value = chunk[i];
            cmd.Parameters.Add(parameter);
        }
        return cmd;
    }

    /// <summary>
    /// Свежие строки подставляются вместо локальных при полном совпадении
    /// структуры; нумерация, разделение и SortKey сохраняются. Итоги — только
    /// авторитетные (SQL), статистика и суммы разделов пересчитываются по тем
    /// же правилам, что в BuildDocument. Результат обязан совпадать с полным
    /// reload той же ревизии (тесты EstimateSparseReadbackTests).
    /// </summary>
    private static EstimateDocument? TryBuildSparse(EstimateDocument current,
        Dictionary<int, PositionRow> fresh, ContractTotals totals, TagRules rules,
        int expectedCount, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (fresh.Count != expectedCount) return null;
        var roundRows = rules.Has(current.Form, "round_rows");
        var rows = new List<EstimateRow>(current.Rows.Count);
        var replaced = 0;
        foreach (var row in current.Rows)
        {
            if (row.Kind == RowKind.Position && fresh.TryGetValue(row.Id, out var p))
            {
                // Сдвиг структуры не подставляется точечно — только полный reload.
                if (p.SystemId != row.SystemId || p.ParentId != row.ParentPositionId) return null;
                rows.Add(MakeRow(p, row.Level, roundRows, row.SortKey, row.Num));
                replaced++;
            }
            else rows.Add(row);
        }
        if (replaced != fresh.Count) return null;

        // Суммы разделов: только верхние позиции без «вне КП», как в BuildDocument.
        var sumBySystem = new Dictionary<int, decimal>();
        foreach (var row in rows)
            if (row.Kind == RowKind.Position && row.ParentPositionId is null && !row.OutOfKp)
            {
                var amount = row.Qty * row.Price;
                sumBySystem[row.SystemId] = sumBySystem.GetValueOrDefault(row.SystemId) +
                    (roundRows ? Money.Round2(amount) : amount);
            }
        // GroupMaterial: сумма раздела плюс суммы его прямых подсистем.
        var childSums = new Dictionary<int, decimal>();
        foreach (var row in rows)
            if (row.Kind == RowKind.System && row.ParentSystemId is int parentId)
                childSums[parentId] = childSums.GetValueOrDefault(parentId) +
                    sumBySystem.GetValueOrDefault(row.SystemId);
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Kind == RowKind.System)
                rows[i] = rows[i] with
                {
                    Amount = sumBySystem.GetValueOrDefault(rows[i].SystemId) +
                        childSums.GetValueOrDefault(rows[i].SystemId)
                };

        // Статистика как Statistics(): монтаж строками — корневые позиции без «вне КП».
        var work = decimal.Round(rows.Where(r => r.Kind == RowKind.Position && !r.IsSub && !r.OutOfKp &&
                EstimateStatistics.IsWorkName(r.Name)).Sum(r => r.Qty * r.Price),
            2, MidpointRounding.ToEven);
        var statistics = new EstimateStatistics(
            decimal.Round(totals.Materials - work, 2, MidpointRounding.ToEven),
            decimal.Round(totals.Smr + work, 2, MidpointRounding.ToEven), work);

        token.ThrowIfCancellationRequested();
        var inOrder = rows.Where(r => r.Kind == RowKind.Position).ToList();
        return current with { Rows = rows, PositionsInOrder = inOrder, Totals = totals, Statistics = statistics };
    }
}
