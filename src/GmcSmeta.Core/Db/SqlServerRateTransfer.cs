using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Перенос расценок между КП на SQL Server (rate_transfer.py): чтение двух документов
/// как RateRow-наборов, сверка «свежести» и применение выбранных цен. Материальные
/// цены защищены, если строка уже в неподписанном акте или связана с этапом.
/// </summary>
public static class SqlServerRateTransfer
{
    /// <summary>Прочитать пару КП как данные переноса (rate_transfer.read_transfer).</summary>
    public static TransferData ReadTransfer(string connectionString, int sourceId, int targetId,
        bool includeMaterials)
    {
        if (sourceId == targetId)
            throw new InvalidOperationException("Выберите другое КП как источник.");
        using var conn = SqlServerDatabase.Open(connectionString);
        RateDocument? source = null, target = null;
        var rules = TagRules.Load(conn);
        foreach (var id in new[] { sourceId, targetId })
        {
            var (document, kind, locked, stageOf) = ReadDocument(conn, rules, id);
            if (document is null || kind != "kp")
                throw new InvalidOperationException("Одно из КП больше недоступно.");
            if (id == targetId)
            {
                if (locked || stageOf is not null)
                    throw new InvalidOperationException("Выберите незаблокированное КП, которое можно редактировать.");
                target = document;
            }
            else source = document;
        }
        if (source is null || target is null) throw new InvalidOperationException("Одно из КП больше недоступно.");

        var protectedPrices = ImmutableHashSet<int>.Empty;
        if (includeMaterials)
        {
            var ids = new List<int>();
            using (var read = new SqlCommand(
                "SELECT DISTINCT al.position_id FROM dbo.act_lines al " +
                "JOIN dbo.acts a ON a.id=al.act_id " +
                "JOIN dbo.positions p ON p.id=al.position_id " +
                "JOIN dbo.systems sy ON sy.id=p.system_id " +
                "WHERE sy.contract_id=@id AND a.signed=0", conn))
            {
                read.Parameters.Add("@id", SqlDbType.BigInt).Value = targetId;
                using var r = read.ExecuteReader();
                while (r.Read()) ids.Add(Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture));
            }
            using (var read = new SqlCommand(
                "SELECT DISTINCT p.stage_src FROM dbo.positions p " +
                "JOIN dbo.systems sy ON sy.id=p.system_id " +
                "JOIN dbo.positions s2 ON s2.id=p.stage_src " +
                "JOIN dbo.systems s2sy ON s2sy.id=s2.system_id " +
                "JOIN dbo.contracts sc ON sc.id=s2sy.contract_id " +
                "WHERE sy.contract_id=@id AND sc.stage_of IS NOT NULL AND sc.stage_sum IS NULL", conn))
            {
                read.Parameters.Add("@id", SqlDbType.BigInt).Value = targetId;
                using var r = read.ExecuteReader();
                while (r.Read())
                {
                    if (!r.IsDBNull(0)) ids.Add(Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture));
                }
            }
            protectedPrices = ids.ToImmutableHashSet();
        }
        return new TransferData(source, target, protectedPrices);
    }

    /// <summary>
    /// Применить перенос (rate_transfer.apply_transfer): сверка «свежести», пересчёт
    /// совпадений и запись в одной транзакции с meta.rev; материальные формулы сбрасываются.
    /// Возвращает (перенесённых цен, позиций, СМР работ после переноса).
    /// </summary>
    public sealed record Receipt(int Prices, int Positions, double WorkTotal, string Revision,
        UndoSnapshot Before, UndoSnapshot After);

    public static (int Prices, int Positions, double WorkTotal) ApplyTransfer(string connectionString,
        TransferData data, IReadOnlyList<int> fields, bool overwrite,
        IReadOnlyDictionary<int, int> manual, IReadOnlySet<int> excluded, IReadOnlySet<int> allowZero,
        string expectedRevision)
    {
        var result = ApplyTransferWithReceipt(connectionString, data, fields, overwrite, manual,
            excluded, allowZero, expectedRevision);
        return (result.Prices, result.Positions, result.WorkTotal);
    }

    public static Receipt ApplyTransferWithReceipt(string connectionString,
        TransferData data, IReadOnlyList<int> fields, bool overwrite,
        IReadOnlyDictionary<int, int> manual, IReadOnlySet<int> excluded, IReadOnlySet<int> allowZero,
        string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        if (string.IsNullOrWhiteSpace(expectedRevision))
            throw new InvalidOperationException("Нет подтверждённой ревизии предпросмотра.");
        var includeMaterials = fields.Any(f => f is RateFields.PurchasePrice or RateFields.Price);
        var fresh = ReadTransfer(connectionString, data.Source.Id, data.Target.Id, includeMaterials);
        if (!fresh.Equals(data))
            throw new InvalidOperationException("КП изменились после проверки. Обновите совпадения и проверьте цены ещё раз.");
        var rows = RateTransfer.MakePreview(fresh, fields, overwrite, manual, excluded, allowZero)
            .Where(r => r.Changes.Length > 0).ToList();
        if (rows.Count == 0)
            throw new InvalidOperationException("Нет расценок для переноса.");

        var changes = rows.Where(r => r.Target is not null).ToDictionary(
            r => r.Target!.Id, r => r.Changes);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) throw new InvalidOperationException("Отсутствует meta.rev.");
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("Данные изменены другим сотрудником. Сначала обнови смету.");

        using (var guard = new SqlCommand(
            "SELECT TOP(1) c.id FROM dbo.contracts c WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id " +
            "WHERE c.id=@id AND c.kind=N'kp' AND ISNULL(c.locked,0)=0 " +
            "AND c.stage_of IS NULL AND ISNULL(c.stage_rest,0)=0 AND ISNULL(o.archived,0)=0", conn, tx))
        {
            guard.Parameters.Add("@id", SqlDbType.BigInt).Value = data.Target.Id;
            if (guard.ExecuteScalar() is null)
                throw new InvalidOperationException("Целевое КП заблокировано или находится в архиве.");
        }

        var before = SqlServerUndo.Snapshot(conn, tx, data.Target.Id)
            ?? throw new InvalidOperationException("Не удалось снять исходное состояние для отмены.");
        var count = 0;
        foreach (var (positionId, changeList) in changes)
        {
            string? formulas = null;
            using (var read = new SqlCommand(
                "SELECT formulas FROM dbo.positions WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
            {
                read.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
                formulas = Convert.ToString(read.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            var set = new List<string>();
            var withFormulaReset = changeList.Where(c => c.Field is RateFields.PurchasePrice or RateFields.Price).ToList();
            using (var update = new SqlCommand { Connection = conn, Transaction = tx })
            {
                update.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
                var index = 0;
                foreach (var (field, amount) in changeList)
                {
                    var name = field switch
                    {
                        RateFields.WorkCost => "work_cost",
                        RateFields.WorkSell => "work_sell",
                        RateFields.PurchasePrice => "purchase_price",
                        _ => "price",
                    };
                    set.Add($"[{name}]=@f{index}");
                    update.Parameters.Add($"@f{index}", SqlDbType.Float).Value = (double)amount;
                    index++;
                }
                if (withFormulaReset.Count > 0)
                {
                    System.Text.Json.Nodes.JsonObject? node;
                    try
                    {
                        node = string.IsNullOrWhiteSpace(formulas) ? new() :
                            System.Text.Json.Nodes.JsonNode.Parse(formulas) as System.Text.Json.Nodes.JsonObject
                            ?? throw new FormatException("Некорректный JSON формул.");
                    }
                    catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
                    {
                        throw new InvalidOperationException("Нельзя безопасно обновить формулы позиции: " + ex.Message);
                    }
                    foreach (var (field, _) in withFormulaReset)
                        node.Remove(field switch
                        {
                            RateFields.PurchasePrice => "purchase_price",
                            _ => "price",
                        });
                    set.Add("formulas=@formulas");
                    update.Parameters.Add("@formulas", SqlDbType.NVarChar, -1).Value = node.ToJsonString();
                }
                update.Parameters.Add("@target", SqlDbType.BigInt).Value = data.Target.Id;
                update.CommandText = $"UPDATE dbo.positions SET {string.Join(", ", set)} WHERE id=@id " +
                    "AND system_id IN (SELECT id FROM dbo.systems WHERE contract_id=@target)";
                if (update.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Строка исчезла во время переноса.");
            }
            count += changeList.Length;
        }

        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,N'contract',@id,N'update',@details,@contract,@object)", conn, tx))
        {
            log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
            log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = data.Target.Id;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                $"Расценки из «{data.Source.Title}»: {count} цен, {rows.Count} позиций";
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = data.Target.Id;
            log.Parameters.Add("@object", SqlDbType.BigInt).Value = DBNull.Value;
            log.ExecuteNonQuery();
        }
        var after = SqlServerUndo.Snapshot(conn, tx, data.Target.Id)
            ?? throw new InvalidOperationException("Не удалось снять итоговое состояние для отмены.");

        // СМР работ после переноса — для предупреждения о расхождении с итогом сметы.
        double workTotal = 0;
        foreach (var row in fresh.Target.Rows.Where(r => r.ParentKey is null))
        {
            decimal? sell = null;
            if (changes.TryGetValue(row.Id, out var list))
            {
                var sellChange = list.FirstOrDefault(c => c.Field == RateFields.WorkSell);
                if (sellChange.Field == RateFields.WorkSell) sell = sellChange.Amount;
            }
            var value = list0(sell, row.Rate(RateFields.WorkSell));
            workTotal += (double)(row.Qty * value);
        }
        workTotal = FinanceReader.PythonRound(workTotal);
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new Receipt(count, rows.Count, workTotal, updated, before, after);
    }

    private static decimal list0(decimal? changed, decimal? current) =>
        changed ?? current ?? 0m;

    // ---------------------------------------------------------------- чтение документа

    private static (RateDocument?, string Kind, bool Locked, int? StageOf) ReadDocument(
        SqlConnection conn, TagRules rules, int id)
    {
        string kind = "", form = "", objectName = "", number = "", date = "", name = "";
        bool locked = false;
        int? stageOf = null;
        decimal vatRate;
        using (var head = new SqlCommand(
            "SELECT c.kind,c.locked,c.stage_of,c.form,c.vat_rate,c.number,c.[date],c.name,o.name " +
            "FROM dbo.contracts c JOIN dbo.objects o ON o.id=c.object_id WHERE c.id=@id", conn))
        {
            head.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            using var r = head.ExecuteReader();
            if (!r.Read()) return (null, "", false, null);
            kind = r.GetString(0);
            locked = Convert.ToBoolean(r.GetValue(1), CultureInfo.InvariantCulture);
            stageOf = r.IsDBNull(2) ? (int?)null : Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture);
            form = r.IsDBNull(3) ? "" : r.GetString(3);
            vatRate = r.IsDBNull(4) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture));
            number = r.IsDBNull(5) ? "" : r.GetString(5);
            date = r.IsDBNull(6) ? "" : r.GetString(6);
            name = r.IsDBNull(7) ? "" : r.GetString(7);
            objectName = r.IsDBNull(8) ? "" : r.GetString(8);
        }
        var title = $"{objectName} · " + Documents.ContractTitleText(kind, number, date, name);

        var systems = new Dictionary<int, (int? ParentId, string Name)>();
        using (var read = new SqlCommand(
            "SELECT id,parent_id,name FROM dbo.systems WHERE contract_id=@id ORDER BY [order],id", conn))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            using var r = read.ExecuteReader();
            while (r.Read())
                systems[Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture)] = (
                    r.IsDBNull(1) ? null : Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture),
                    r.IsDBNull(2) ? "" : r.GetString(2));
        }

        // Все строки системы сначала в память: владельцы комплектующих ищутся по parent_id.
        var all = new List<(RateRow Row, int SystemId, int? ParentId, bool OutOfKp)>();
        using (var read = new SqlCommand(
            "SELECT p.id, p.system_id, p.parent_id, p.num, p.name, p.unit, p.basis, p.catalog_item_id, " +
            "p.qty, p.work_cost, p.work_sell, p.purchase_price, p.price, p.formulas, p.out_of_kp, p.stage_src " +
            "FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@id ORDER BY p.id", conn))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            using var r = read.ExecuteReader();
            decimal? Dec(int i) => r.IsDBNull(i) ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture));
            while (r.Read())
            {
                var rowId = Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture);
                var systemId = Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture);
                var parentId = r.IsDBNull(2) ? (int?)null : Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture);
                if (!systems.TryGetValue(systemId, out var system)) continue;
                var path = system.ParentId is int pid && systems.TryGetValue(pid, out var parent)
                    ? ImmutableArray.Create(parent.Name, system.Name)
                    : ImmutableArray.Create(system.Name);
                var row = new RateRow(
                    rowId,
                    r.IsDBNull(15) ? rowId : Convert.ToInt32(r.GetValue(15), CultureInfo.InvariantCulture),
                    r.IsDBNull(4) ? "" : r.GetString(4),
                    r.IsDBNull(3) ? "" : r.GetString(3),
                    r.IsDBNull(5) ? "" : r.GetString(5),
                    path,
                    RateTransfer.Norm(r.IsDBNull(6) ? "" : r.GetString(6)),
                    null, "", // владелец заполняется вторым проходом
                    r.IsDBNull(7) ? null : Convert.ToInt32(r.GetValue(7), CultureInfo.InvariantCulture),
                    r.IsDBNull(8) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(8), CultureInfo.InvariantCulture)),
                    ImmutableArray.Create(Dec(9), Dec(10), Dec(11), Dec(12)),
                    r.IsDBNull(13) ? "" : r.GetString(13));
                all.Add((row, systemId, parentId,
                    !r.IsDBNull(14) && Convert.ToBoolean(r.GetValue(14), CultureInfo.InvariantCulture)));
            }
        }
        var byId = all.ToDictionary(x => x.Row.Id, x => x);
        var rows = new List<RateRow>();
        foreach (var (row, systemId, parentId, outOfKp) in all)
        {
            // «Вне КП» пропускаются; комплектующая вне КП-владельца — тоже (в смете она стала бы нулевой строкой).
            if (outOfKp) continue;
            RateRow? owner = null;
            if (parentId is int pid) owner = byId.TryGetValue(pid, out var found) ? found.Row : null;
            if (owner is not null && byId[owner.Id].OutOfKp) continue;
            var parentKey = owner is null ? null : Catalog.Key(owner.Name, owner.Unit);
            var parentName = owner?.Name ?? "";
            rows.Add(row with { ParentKey = parentKey, ParentName = parentName });
        }
        rows.Sort((a, b) => a.Id.CompareTo(b.Id));

        var vatTop = rules.Has(form, "vat_top");
        var document = new RateDocument(id, title, stageOf ?? id, (vatTop, vatRate), 0m, rows);
        // СМР документа — проверенный расчёт Totals (Contract.stats()["smr"] эталона).
        var totals = Totals.ReadOne(conn, id);
        return (totals is null ? document : document with { Smr = totals.Smr }, kind, locked, stageOf);
    }
}
