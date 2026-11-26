using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Строка «Сводной» себестоимости — одна верхняя строка КП
/// (cost_summary_view.row_rec): закуп компонентов сложен в свою строку.</summary>
public sealed record CostSummaryRow(
    long Id, long SystemId, string Name, string Unit, decimal Qty, string Supplier,
    decimal MCost, decimal MSell, decimal WCost, decimal WSell,
    bool NoCost, bool NoPrice, bool NowCost, bool NowSell,
    decimal? UCost, decimal UPrice, decimal? UWcost, decimal? UWsell,
    bool Fact, decimal? Plan, bool Kids, bool Work, string CostColor = "", string CostNote = "",
    decimal? FactPrice = null)
{
    /// <summary>Маржа материалов: розница − закуп (включая «Монтаж …» в закупе).</summary>
    public decimal MatMargin => decimal.Round(MSell - MCost, 2);

    /// <summary>Маржа работ: розница работ − закуп работ.</summary>
    public decimal WorkMargin => decimal.Round(WSell - WCost, 2);

    /// <summary>Итог закупа: материалы + работы.</summary>
    public decimal TotalCost => decimal.Round(MCost + WCost, 2);

    /// <summary>Итог розницы: материалы + работы.</summary>
    public decimal TotalSell => decimal.Round(MSell + WSell, 2);

    /// <summary>Итоговая маржа.</summary>
    public decimal TotalMargin => decimal.Round(TotalSell - TotalCost, 2);

    /// <summary>СМР: наценка, % — маржа работ к их закупу.</summary>
    public decimal? MarkupPct => WCost == 0m ? null
        : decimal.Round((WSell - WCost) / WCost * 100m, 1);
}

/// <summary>Данные «Сводной» документа (cost_summary_view.summary).</summary>
public sealed record CostSummaryData(
    IReadOnlyList<(long Id, string Label, bool IsSub)> Systems,
    IReadOnlyList<CostSummaryRow> Rows,
    IReadOnlyList<(string Name, decimal Cost, decimal Sell, string Kind)> Extra,
    IReadOnlyList<string> Notes,
    decimal SmrContract)
{
    // Already read from systems.parent_id; metadata only, no additional SQL or writes.
    public IReadOnlyDictionary<long, long> SystemParents { get; init; } = new Dictionary<long, long>();
    public decimal TotalCost => Rows.Sum(r => r.TotalCost) + Extra.Sum(e => e.Cost);
    public decimal TotalSell => Rows.Sum(r => r.TotalSell) + Extra.Sum(e => e.Sell);
}

/// <summary>
/// Себестоимость документа — перенос cost_summary_view.summary: по каждой верхней
/// строке закуп материалов (факт из закупок, иначе plan) + закуп комплектующих,
/// розница, цены работ, флаги «без закупа/цены», поставщик из закупки; «вне КП»
/// уходит в «Куплено вне КП», прочие затраты на монтаж — из smr_costs.
/// </summary>
public static partial class SqlServerCostSummary
{
    public static CostSummaryData Summary(string connectionString, int contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        return Summary(conn, contractId);
    }

    /// <summary>
    /// Finish material/work financial rows without mutating the source collection.
    /// Incoming MCost may already include component procurement costs; that amount
    /// must be kept exactly once alongside the parent's unit procurement cost.
    /// SQL Server reads only top-level positions into this projection.
    /// </summary>
    /// <summary>Use actual procurement price when present, otherwise the planned price.</summary>
    public static decimal? EffectiveUnitCost(decimal? actual, decimal? planned) =>
        actual ?? planned;

    public static IReadOnlyList<CostSummaryRow> FinalizeRows(IEnumerable<CostSummaryRow> source)
    {
        return source.Select(row =>
        {
            var mSell = row.Work ? 0m : row.Qty * row.UPrice;
            return row with
            {
                MCost = decimal.Round(row.MCost + (row.UCost is { } cu ? row.Qty * cu : 0m), 2),
                MSell = decimal.Round(mSell, 2),
                WCost = row.UWcost is { } wc ? decimal.Round(wc * row.Qty, 2) : 0m,
                WSell = row.UWsell is { } ws ? decimal.Round(ws * row.Qty, 2) : 0m,
                NoCost = !row.Work && row.UCost is null && row.Qty * row.UPrice != 0m,
                NoPrice = row.UPrice == 0m,
                NowCost = row.UWcost is null,
                NowSell = row.UWsell is null,
            };
        }).ToList();
    }

    private static decimal? CostOf(SqlDataReader r, int factIndex, int planIndex)
    {
        if (!r.IsDBNull(factIndex))
            return Money.FromDouble(Convert.ToDouble(r.GetValue(factIndex), CultureInfo.InvariantCulture));
        if (!r.IsDBNull(planIndex))
            return Money.FromDouble(Convert.ToDouble(r.GetValue(planIndex), CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>
    /// Правка ячейки сводной (cost_summary_view.save_cell): закуп за единицу,
    /// цена/количество клиента и текстовые поля идут через SaveCell (формулы,
    /// каталог, история, акты); работы (work_cost/work_sell) пишутся напрямую,
    /// как в эталоне, но с meta.rev. Поле transport_cost — реквизит договора.
    /// </summary>
    public static SqlServerDatabase.SaveResult SaveSummaryCell(string connectionString,
        int contractId, long positionId, string field, string input, string expectedRevision)
    {
        switch (field)
        {
            case "work_cost" or "work_sell":
                return SaveSummaryCells(connectionString, contractId, new[] { (positionId, input) }, field, expectedRevision);
            case "transport_cost":
            {
                var value = input.Trim().Length == 0
                    ? (decimal?)null
                    : NumberText.Parse(input) ?? throw new InvalidOperationException("Недопустимое число: " + input);
                RequirePilotWrite(connectionString);
                using var conn = SqlServerDatabase.Open(connectionString);
                using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
                var rev = SqlServerDatabase.LockRevision(conn, tx);
                if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
                if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
                    return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови сводную.", rev);
                using (var update = new SqlCommand(
                    "UPDATE dbo.contracts SET transport_cost=@v, transport_note=N'' WHERE id=@id", conn, tx))
                {
                    update.Parameters.Add("@v", SqlDbType.Float).Value =
                        value is null ? DBNull.Value : (object)(double)value.Value;
                    update.Parameters.AddWithValue("@id", contractId);
                    if (update.ExecuteNonQuery() != 1)
                        return SqlServerDatabase.SaveResult.Fail("Договор не найден.", rev);
                }
                using (var log = new SqlCommand(
                    "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
                    "VALUES (@ts,@user,N'contract',@id,N'update',@details,@contract,NULL)", conn, tx))
                {
                    log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
                    log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
                    log.Parameters.AddWithValue("@id", contractId);
                    log.Parameters.AddWithValue("@details",
                        $"transport_cost: «{value?.ToString(CultureInfo.InvariantCulture) ?? ""}»");
                    log.Parameters.AddWithValue("@contract", contractId);
                    log.ExecuteNonQuery();
                }
                var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
                tx.Commit();
                return new SqlServerDatabase.SaveResult(true, null, updated);
            }
            case "name" or "unit" or "qty" or "price" or "purchase_price":
                return SaveSummaryCells(connectionString, contractId, new[] { (positionId, input) }, field, expectedRevision);
            default:
                return SqlServerDatabase.SaveResult.Fail("Эта колонка не редактируется в сводной.", expectedRevision);
        }
    }

    /// <summary>All selected cost cells commit together, or none do.</summary>
    public static SqlServerDatabase.SaveResult SaveSummaryCells(string connectionString,
        int contractId, IReadOnlyList<(long PositionId, string Input)> edits, string field,
        string expectedRevision)
    {
        RequirePilotWrite(connectionString);
        if (contractId <= 0 || edits is null || edits.Count == 0 || edits.Count > 10000 ||
            edits.Any(e => e.PositionId <= 0) ||
            edits.Select(e => e.PositionId).Distinct().Count() != edits.Count)
            return SqlServerDatabase.SaveResult.Fail("Недопустимые позиции себестоимости.", expectedRevision);
        if (field is not ("name" or "unit" or "qty" or "price" or "purchase_price" or "work_cost" or "work_sell"))
            return SqlServerDatabase.SaveResult.Fail("Поле себестоимости не поддерживается.", expectedRevision);
        if (field == "qty" && edits.Count != 1)
            return SqlServerDatabase.SaveResult.Fail("Количество можно изменить только у одной позиции.", expectedRevision);

        var prepared = new List<(long Id, string Input, bool Numeric, string Trimmed, decimal? Value)>();
        foreach (var edit in edits)
        {
            if (field is "work_cost" or "work_sell")
            {
                var trimmed = (edit.Input ?? "").Trim();
                var value = trimmed.Length == 0 ? null : NumberText.Parse(trimmed);
                if (trimmed.Length > 0 && value is null)
                    return SqlServerDatabase.SaveResult.Fail("Недопустимое число: " + trimmed, expectedRevision);
                prepared.Add((edit.PositionId, edit.Input, true, trimmed, value));
            }
            else
            {
                var error = SqlServerDatabase.ValidateCellInput(field, edit.Input,
                    out var numeric, out var trimmed, out var value);
                if (error is not null) return SqlServerDatabase.SaveResult.Fail(error, expectedRevision);
                prepared.Add((edit.PositionId, edit.Input, numeric, trimmed, value));
            }
        }
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = SqlServerDatabase.LockRevision(conn, tx);
        if (string.IsNullOrEmpty(revision) || revision != expectedRevision)
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови себестоимость.",
                revision ?? expectedRevision);
        var changed = false;
        var linkedPriceSources = new List<long>();
        foreach (var edit in prepared)
        {
            if (field is "work_cost" or "work_sell")
            {
                // Work cost/sell are internal per-position planning values in the Python
                // reference. Keep the existing direct-write semantics, now in one transaction.
                using var update = new SqlCommand(
                    $"UPDATE p SET [{field}]=@v FROM dbo.positions p " +
                    "JOIN dbo.systems sy ON sy.id=p.system_id WHERE p.id=@id AND sy.contract_id=@contract",
                    conn, tx);
                update.Parameters.Add("@v", SqlDbType.Float).Value =
                    edit.Value is null ? DBNull.Value : (object)(double)edit.Value.Value;
                update.Parameters.Add("@id", SqlDbType.BigInt).Value = edit.Id;
                update.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
                if (update.ExecuteNonQuery() != 1)
                    return SqlServerDatabase.SaveResult.Fail("Позиция не принадлежит этому договору.", revision);
                using var log = new SqlCommand(
                    "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
                    "VALUES (@ts,@user,N'position',@id,N'update',@details,@contract,NULL)", conn, tx);
                log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
                log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
                log.Parameters.Add("@id", SqlDbType.BigInt).Value = edit.Id;
                log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
                log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                    $"Себестоимость: {field} = {edit.Trimmed}";
                log.ExecuteNonQuery();
                changed = true;
            }
            else
            {
                var result = SqlServerDatabase.SaveCellCore(conn, tx, edit.Id, contractId, field,
                    edit.Input, edit.Numeric, edit.Trimmed, edit.Value, revision, out var rowChanged,
                    linkedPriceSources);
                if (!result.Saved) return result;
                changed |= rowChanged;
            }
        }
        SqlServerStages.RefreshLinkedPrices(conn, tx, contractId, linkedPriceSources);
        var committedRevision = changed ? SqlServerDatabase.BumpPositionRevision(conn, tx) : revision;
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, committedRevision);
    }

    private static void RequirePilotWrite(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!builder.InitialCatalog.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Запись C# разрешена только в отдельную тестовую базу GmcSmetaPilot_*. Рабочая база не изменена.");
    }

    /// <summary>
    /// Наценка на СМР (cost_summary_view._apply_markup): для верхних строк с закупом работ
    /// розница = round(закуп × (1 + pct/100), 2); строки без закупа пропускаются; журнал
    /// contract/update «СМР по позициям, наценка X% (N строк)». Возвращает (изменено, пропущено).
    /// </summary>
    public static (int Changed, int Skipped) ApplySmrMarkup(string connectionString, int contractId,
        IReadOnlyList<long> positionIds, decimal percent, string expectedRevision)
    {
        var result = ApplySmrMarkupWithRevision(connectionString, contractId, positionIds, percent, expectedRevision);
        return (result.Changed, result.Skipped);
    }

    public static (int Changed, int Skipped, string Revision) ApplySmrMarkupWithRevision(
        string connectionString, int contractId, IReadOnlyList<long> positionIds, decimal percent, string expectedRevision)
    {
        WorkPriceBatchPolicy.ValidateSelection(contractId, positionIds, 0m);
        if (percent < -100m || percent > 10000m)
            throw new ArgumentOutOfRangeException(nameof(percent), "Наценка должна быть от −100 до 10000 %.");
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) throw new InvalidOperationException("Отсутствует meta.rev.");
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("Данные изменены другим сотрудником. Обнови сводную.");

        using (var guard = new SqlCommand(
            "SELECT TOP(1) 1 FROM dbo.contracts c WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.objects o ON o.id=c.object_id WHERE c.id=@id AND c.locked=0 AND o.archived=0", conn, tx))
        {
            guard.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            if (guard.ExecuteScalar() is null)
                throw new InvalidOperationException("Документ подписан, архивирован или не найден.");
        }
        var touchedActs = new HashSet<long>();
        var changed = 0;
        var skipped = 0;
        var ids = positionIds.ToList();
        for (var offset = 0; offset < ids.Count; offset += 500)
        {
            var chunk = ids.Skip(offset).Take(500).ToList();
            var placeholders = new List<string>();
            using var read = new SqlCommand { Connection = conn, Transaction = tx };
            for (var i = 0; i < chunk.Count; i++)
            {
                var param = "@p" + i.ToString(CultureInfo.InvariantCulture);
                placeholders.Add(param);
                read.Parameters.Add(param, SqlDbType.BigInt).Value = chunk[i];
            }
            read.CommandText =
                "SELECT p.id, p.work_cost, p.work_sell, sy.contract_id FROM dbo.positions p " +
                "JOIN dbo.systems sy WITH (UPDLOCK,HOLDLOCK) ON sy.id=p.system_id " +
                "WHERE p.id IN (" + string.Join(", ", placeholders) + ") " +
                "AND p.parent_id IS NULL AND p.out_of_kp=0 AND sy.contract_id=@cid";
            read.Parameters.Add("@cid", SqlDbType.BigInt).Value = contractId;
            // SQL Client does not allow updates while a reader is open without MARS.
            // Materialize each <=500-position batch before touching the rows.
            var candidates = new List<(long Id, decimal Cost, decimal? Sell)>();
            using (var r = read.ExecuteReader())
            {
                while (r.Read())
                    candidates.Add((
                        Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                        r.IsDBNull(1) ? 0m : Convert.ToDecimal(r.GetValue(1), CultureInfo.InvariantCulture),
                        r.IsDBNull(2) ? null : Convert.ToDecimal(r.GetValue(2), CultureInfo.InvariantCulture)));
            }
            if (candidates.Count != chunk.Count)
                throw new DBConcurrencyException("Выделение содержит отсутствующие или чужие позиции. Наценка отменена.");
            foreach (var item in candidates)
            {
                if (item.Cost == 0m) { skipped++; continue; }
                var newSell = (decimal)FinanceReader.PythonRound(
                    (double)item.Cost * (1d + (double)percent / 100d), 2);
                if (newSell < 0m || newSell > GmcSmeta.Core.NumberText.MaxNumber)
                    throw new ArgumentOutOfRangeException(nameof(percent), "Цена СМР вне допустимого диапазона.");
                if (item.Sell == newSell) continue;
                using (var update = new SqlCommand(
                    "UPDATE dbo.positions SET work_sell=@v WHERE id=@id", conn, tx))
                {
                    update.Parameters.Add("@v", SqlDbType.Float).Value = (double)newSell;
                    update.Parameters.Add("@id", SqlDbType.BigInt).Value = item.Id;
                    if (update.ExecuteNonQuery() != 1)
                        throw new DBConcurrencyException("Позиция исчезла во время массовой наценки.");
                }
                touchedActs.UnionWith(SqlServerDatabase.TouchedUnsignedActs(conn, tx, item.Id, contractId));
                changed++;
            }
        }
        SqlServerDatabase.SyncActWork(conn, tx, touchedActs);
        if (changed > 0)
        {
            using (var log = new SqlCommand(
                "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
                "VALUES (@ts,@user,N'contract',@id,N'update',@details,@contract,NULL)", conn, tx))
            {
                log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
                log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
                log.Parameters.AddWithValue("@id", contractId);
                log.Parameters.AddWithValue("@details",
                    $"СМР по позициям, наценка {percent.ToString("0.##", CultureInfo.InvariantCulture)}% ({changed} строк)");
                log.Parameters.AddWithValue("@contract", contractId);
                log.ExecuteNonQuery();
            }
        }
        var committedRevision = changed > 0 ? SqlServerDatabase.BumpPositionRevision(conn, tx) : rev;
        tx.Commit();
        return (changed, skipped, committedRevision);
    }

    /// <summary>
    /// «СМР в смету по системам…» (cost_summary_view._transfer_smr): розница СМР
    /// по позициям каждой системы заменяет smr_amount, режим СМР → «systems»;
    /// заблокировано при замке и при расчёте по этапам. Возвращает (систем, сумма).
    /// </summary>
    public static (int Systems, decimal Total) TransferSmrToSystems(
        string connectionString, int contractId, string expectedRevision)
    {
        var result = TransferSmrToSystemsWithRevision(connectionString, contractId, expectedRevision);
        return (result.Systems, result.Total);
    }

    public static (int Systems, decimal Total, string Revision) TransferSmrToSystemsWithRevision(
        string connectionString, int contractId, string expectedRevision)
    {
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) throw new InvalidOperationException("Отсутствует meta.rev.");
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("Данные изменены другим сотрудником. Обнови сводную.");

        bool locked; int? stageOf; string smrMode;
        using (var read = new SqlCommand(
            "SELECT locked, stage_of, smr_mode FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = read.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException("Документ не найден.");
            locked = Convert.ToBoolean(r.GetValue(0), CultureInfo.InvariantCulture);
            stageOf = r.IsDBNull(1) ? null : Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture);
            smrMode = r.IsDBNull(2) ? "sum" : r.GetString(2);
        }
        if (locked) throw new InvalidOperationException("СМР сметы заблокирован или рассчитывается по этапам");
        bool fromStages;
        using (var stages = new SqlCommand(
            "SELECT TOP(1) 1 FROM dbo.contracts WHERE stage_of=@id AND stage_sum IS NOT NULL " +
            "AND ISNULL(stage_rest,0)=0", conn, tx))
        {
            stages.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            fromStages = stages.ExecuteScalar() is not null;
        }
        if (stageOf is null && fromStages)
            throw new InvalidOperationException("СМР сметы заблокирован или рассчитывается по этапам");

        // Сумма розницы СМР по позициям системы (Position.work_sell × qty, верхние строки).
        var amounts = new List<(long SystemId, decimal Amount)>();
        using (var read = new SqlCommand(
            "SELECT sy.id, COALESCE(SUM(p.work_sell*COALESCE(p.qty,0)),0), MAX(sy.[order]) " +
            "FROM dbo.systems sy " +
            "JOIN dbo.positions p ON p.system_id=sy.id AND p.parent_id IS NULL " +
            "AND ISNULL(p.out_of_kp,0)=0 AND p.work_sell IS NOT NULL " +
            "WHERE sy.contract_id=@id GROUP BY sy.id ORDER BY MAX(sy.[order]), sy.id", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = read.ExecuteReader();
            while (r.Read())
                amounts.Add((
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    Money.FromDouble(FinanceReader.PythonRound(
                        Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture), 2))));
        }
        if (amounts.Count == 0)
            throw new InvalidOperationException("В «Сводной» нет рассчитанных цен СМР для переноса");

        // Уже перенесены: режим «по системам» и все суммы совпадают.
        if (smrMode == "systems")
        {
            var same = true;
            using (var check = new SqlCommand(
                "SELECT id, COALESCE(smr_amount,-1) FROM dbo.systems WHERE contract_id=@id", conn, tx))
            {
                check.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
                using var r = check.ExecuteReader();
                var current = new Dictionary<long, decimal>();
                while (r.Read())
                    current[Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture)] =
                        Money.FromDouble(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
                foreach (var (systemId, amount) in amounts)
                    if (!current.TryGetValue(systemId, out var value) || value != amount) same = false;
            }
            if (same)
                throw new InvalidOperationException("Суммы СМР в смете уже совпадают со «Сводной»");
        }

        foreach (var (systemId, amount) in amounts)
        {
            using var update = new SqlCommand(
                "UPDATE dbo.systems SET smr_amount=@v WHERE id=@id AND contract_id=@contract", conn, tx);
            update.Parameters.Add("@v", SqlDbType.Float).Value = (double)amount;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = systemId;
            update.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (update.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Система изменилась при переносе СМР.");
        }
        using (var mode = new SqlCommand(
            "UPDATE dbo.contracts SET smr_mode=N'systems' WHERE id=@id", conn, tx))
        {
            mode.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            if (mode.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Документ изменился при переносе СМР.");
        }
        var total = decimal.Round(amounts.Sum(a => a.Amount), 2);
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,N'contract',@id,N'update',@details,@contract,NULL)", conn, tx))
        {
            log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
            log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
            log.Parameters.AddWithValue("@id", contractId);
            log.Parameters.AddWithValue("@details",
                $"СМР из «Сводной» по системам ({amounts.Count}): {Money.Format(total)} ₽");
            log.Parameters.AddWithValue("@contract", contractId);
            log.ExecuteNonQuery();
        }
        var committedRevision = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return (amounts.Count, total, committedRevision);
    }

    /// <summary>
    /// Поставщик строки сводной (procurement.set_proc, supplier=: строка закупки
    /// создаётся при отсутствии; журнал procurement/update; источник «закуп»).
    /// </summary>
    public static void SetSupplier(string connectionString, int contractId, long positionId,
        string supplierName, string expectedRevision)
    {
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        using (var scope = new SqlCommand(
            "SELECT TOP(1) 1 FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE p.id=@id AND sy.contract_id=@contract AND p.parent_id IS NULL " +
            "AND ISNULL(p.out_of_kp,0)=0", conn, tx))
        {
            scope.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
            scope.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (scope.ExecuteScalar() is null)
                throw new InvalidOperationException("Позиция не принадлежит договору или недоступна.");
        }
        long? supplierId = null;
        using (var find = new SqlCommand(
            "SELECT TOP(1) id FROM dbo.suppliers WHERE name=@name", conn, tx))
        {
            find.Parameters.AddWithValue("@name", supplierName);
            var value = find.ExecuteScalar();
            if (value is not null and not DBNull) supplierId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        if (supplierId is null && supplierName.Trim().Length > 0)
        {
            using var create = new SqlCommand(
                "INSERT dbo.suppliers (name, note) OUTPUT INSERTED.id VALUES (@name, N'')", conn, tx);
            create.Parameters.AddWithValue("@name", supplierName.Trim());
            supplierId = Convert.ToInt64(create.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        long? procurementId = null;
        using (var find = new SqlCommand(
            "SELECT id, supplier_id FROM dbo.procurement WHERE position_id=@id", conn, tx))
        {
            find.Parameters.AddWithValue("@id", positionId);
            using var r = find.ExecuteReader();
            if (r.Read())
            {
                procurementId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                var currentSupplier = r.IsDBNull(1) ? (long?)null : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
                if (currentSupplier == supplierId)
                {
                    tx.Commit();
                    return;
                }
            }
        }
        if (procurementId is null)
        {
            using var create = new SqlCommand(
                "INSERT dbo.procurement (position_id,supplier_id,source,stage,order_ref,invoice_ref," +
                "responsible,replacement,direct_to_site,note,buy_name,updated_at,updated_by) " +
                "SELECT id,@supplier,N'buy',N'plan',N'',N'',N'',N'',0,N'',name,SYSDATETIME(),@user " +
                "FROM dbo.positions WHERE id=@id", conn, tx);
            create.Parameters.AddWithValue("@supplier", supplierId is null ? DBNull.Value : supplierId.Value);
            create.Parameters.AddWithValue("@id", positionId);
            create.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            create.ExecuteNonQuery();
        }
        else
        {
            using var update = new SqlCommand(
                "UPDATE dbo.procurement SET supplier_id=@supplier, updated_at=SYSDATETIME(), " +
                "updated_by=@user WHERE id=@id", conn, tx);
            update.Parameters.AddWithValue("@supplier", supplierId is null ? DBNull.Value : supplierId.Value);
            update.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            update.Parameters.AddWithValue("@id", procurementId.Value);
            update.ExecuteNonQuery();
        }
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details) " +
            "VALUES (@ts,@user,N'procurement',@id,N'update',@details)", conn, tx))
        {
            log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
            log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = ChangeLog.CurrentUser;
            log.Parameters.AddWithValue("@id", positionId);
            log.Parameters.AddWithValue("@details", $"поставщик → {supplierName.Trim()}");
            log.ExecuteNonQuery();
        }
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
    }
}
