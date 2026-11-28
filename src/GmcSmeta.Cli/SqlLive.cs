using System.Data;
using System.Globalization;
using System.Text;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Cli;

/// <summary>
/// Живые проверки SQL Server на тестовой базе GmcSmetaPilot_* (эталонные операции
/// читаются и выполняются реальным ядром). Запись разрешена только пилот-гвардом.
///   dotnet run --project src/GmcSmeta.Cli -- sql status   "&lt;строка подключения&gt;"
///   dotnet run --project src/GmcSmeta.Cli -- sql smoke    "&lt;строка подключения&gt;"
///   dotnet run --project src/GmcSmeta.Cli -- sql trash    "&lt;строка подключения&gt;" &lt;contractId&gt;
///   dotnet run --project src/GmcSmeta.Cli -- sql trash-obj "&lt;строка подключения&gt;" &lt;objectId&gt;
///   dotnet run --project src/GmcSmeta.Cli -- sql restore  "&lt;строка подключения&gt;" &lt;trashId&gt;
/// </summary>
public static class SqlLive
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Использование: sql <status|smoke|schema31|trash|trash-obj|restore|agreement-audit> <строка подключения> [id]");
            return 2;
        }
        var sub = args[0];
        var connectionString = args[1];
        if (args.Length < 3 && sub is "trash" or "trash-obj" or "restore")
        {
            Console.WriteLine("Укажите id документа/объекта/записи корзины.");
            return 2;
        }
        return sub switch
        {
            "status" => Status(connectionString),
            "agreement-audit" => AgreementAuditCommand.Run(connectionString),
            "smoke" => Smoke(connectionString),
            "schema31" => Schema31Acceptance(connectionString),
            "trash" => TrashRoundtrip(connectionString, long.Parse(args[2], CultureInfo.InvariantCulture)),
            "trash-obj" => TrashObjectRoundtrip(connectionString, long.Parse(args[2], CultureInfo.InvariantCulture)),
            "restore" => RestoreOnly(connectionString, long.Parse(args[2], CultureInfo.InvariantCulture)),
            _ => Fail($"Неизвестная команда sql: {sub}"),
        };
    }

    private static int Fail(string message)
    {
        Console.WriteLine(message);
        return 2;
    }

    // ------------------------------------------------------------------ status

    private static int Status(string connectionString)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        Console.WriteLine($"База: {conn.Database}");
        Console.WriteLine($"meta.rev: {SqlServerDatabase.Revision(conn)}");
        foreach (var (title, sql) in new[]
        {
            ("объектов", "SELECT COUNT_BIG(*) FROM dbo.objects"),
            ("документов", "SELECT COUNT_BIG(*) FROM dbo.contracts"),
            ("разделов", "SELECT COUNT_BIG(*) FROM dbo.systems"),
            ("позиций", "SELECT COUNT_BIG(*) FROM dbo.positions"),
            ("актов", "SELECT COUNT_BIG(*) FROM dbo.acts"),
            ("строк актов", "SELECT COUNT_BIG(*) FROM dbo.act_lines"),
            ("ведомостей", "SELECT COUNT_BIG(*) FROM dbo.shipments"),
            ("закупок", "SELECT COUNT_BIG(*) FROM dbo.procurement"),
            ("движений", "SELECT COUNT_BIG(*) FROM dbo.movements"),
            ("записей корзины", "SELECT COUNT_BIG(*) FROM dbo.trash"),
        })
        {
            using var cmd = new SqlCommand(sql, conn);
            Console.WriteLine($"{title}: {cmd.ExecuteScalar()}");
        }
        return 0;
    }

    // ------------------------------------------------------------------- smoke

    /// <summary>Чтение всех документов реальным EstimateReader — путь приложения на живой базе.</summary>
    private static int Smoke(string connectionString)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        var ids = new List<long>();
        using (var cmd = new SqlCommand("SELECT id FROM dbo.contracts ORDER BY [order], id", conn))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) ids.Add(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture));

        var failures = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var id in ids)
        {
            try
            {
                var est = EstimateReader.Load(conn, checked((int)id));
                var totals = Totals.ReadOne(conn, checked((int)id));
                var mismatch = Math.Abs(est.Totals.Total - totals.Total) >= 0.005m
                    || Math.Abs(est.Totals.Materials - totals.Materials) >= 0.005m
                    || Math.Abs(est.Totals.Smr - totals.Smr) >= 0.005m;
                if (mismatch) failures++;
                Console.WriteLine($"{id,4}  {est.Title,-46} строк: {est.Rows.Count,5}  " +
                    $"мат: {est.Totals.Materials,13:N2}  СМР: {est.Totals.Smr,11:N2}  итого: {est.Totals.Total,13:N2}" +
                    (mismatch ? "  ← РАСХОЖДЕНИЕ Totals" : ""));
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"{id,4}  ОШИБКА: {ex.Message}");
            }
        }
        clock.Stop();
        Console.WriteLine();
        Console.WriteLine($"Документов: {ids.Count}, расхождений/ошибок: {failures}, время: {clock.ElapsedMilliseconds} мс");
        return failures == 0 ? 0 : 1;
    }

    // --------------------------------------------------------- schema 31 gate

    /// <summary>
    /// Приёмочный прогон обычных писателей схемы 31 на выделенной локальной копии:
    /// чтение всех документов, скидка договора, скидка акта, ручная разбивка,
    /// жизненный цикл КС-2 и Undo/Redo. Любая другая база v31 отвергается Open().
    /// </summary>
    private static int Schema31Acceptance(string connectionString)
    {
        var failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "OK: " : "FAIL: ") + what);
            if (!ok) failures++;
        }

        var target = new SqlConnectionStringBuilder(connectionString);
        if (!SqlSchemaCompatibility.IsOrdinarySchema31TestDatabase(target.InitialCatalog, target.DataSource))
            return Fail("Для приёмки требуется локальная " + SqlSchemaCompatibility.OrdinarySchema31TestDatabase);

        using (var conn = SqlServerDatabase.Open(connectionString))
        {
            Check(true, "обычная запись v31 разрешена для " + conn.Database);
            Console.WriteLine($"База: {conn.Database}, схема 31, обычный Open");
        }

        Console.WriteLine("--- чтение всех документов ---");
        if (Smoke(connectionString) != 0) return 1;

        string RevisionOf()
        {
            using var conn = SqlServerDatabase.Open(connectionString);
            return SqlServerDatabase.Revision(conn);
        }

        long contractId;
        using (var conn = SqlServerDatabase.Open(connectionString))
        using (var cmd = new SqlCommand("""
            SELECT c.id,c.form,o.name,t.rules
            FROM dbo.contracts c JOIN dbo.objects o ON o.id=c.object_id
            LEFT JOIN dbo.tags t ON t.[key]=c.form
            WHERE c.locked=0 AND c.stage_of IS NULL AND o.archived=0 AND c.discount_percent=0
              AND NOT EXISTS(SELECT 1 FROM dbo.acts a WHERE a.contract_id=c.id
                    AND (a.signed=1 OR a.discount_override=1 OR a.manual_total_cents>=0))
              AND NOT EXISTS(SELECT 1 FROM dbo.contracts s WHERE s.stage_of=c.id)
              AND NOT EXISTS(SELECT 1 FROM dbo.shipment_lines sl
                    JOIN dbo.shipments sh ON sh.id=sl.shipment_id
                    JOIN dbo.positions p ON p.id=sl.position_id
                    JOIN dbo.systems sy ON sy.id=p.system_id
                    WHERE sy.contract_id=c.id AND sh.fixed=1)
              AND NOT EXISTS(SELECT 1 FROM dbo.movements m
                    JOIN dbo.positions p ON p.id=m.position_id
                    JOIN dbo.systems sy ON sy.id=p.system_id
                    WHERE sy.contract_id=c.id AND m.shipment_id IS NOT NULL)
            ORDER BY c.id
            """, conn))
        {
            contractId = 0;
            using var candidates = cmd.ExecuteReader();
            while (candidates.Read())
            {
                string Field(int ordinal) => candidates.IsDBNull(ordinal) ? "" : candidates.GetString(ordinal);
                var rules = Field(3);
                if (!DiscountPolicy.SupportsDiscount(KsFinancialPolicy.HasRule(rules, "vat_top"),
                        KsFinancialPolicy.HasRule(rules, "ks_dns"), Field(1), Field(2)))
                    continue;
                contractId = Convert.ToInt64(candidates.GetValue(0), CultureInfo.InvariantCulture);
                break;
            }
        }
        if (contractId == 0)
        {
            Console.WriteLine("Нет подходящего черновика для проверки записи.");
            return 1;
        }
        Console.WriteLine($"Черновик для проверки записи: {contractId}");

        decimal Total()
        {
            using var conn = SqlServerDatabase.Open(connectionString);
            return Totals.ReadOne(conn, checked((int)contractId))?.Total ?? 0m;
        }

        var original = Total();
        var saved = SqlServerActs.SaveContractDiscount(connectionString, contractId, 1m, RevisionOf());
        Check(saved.Saved, "SaveContractDiscount 1%: " + (saved.Error ?? "сохранено"));
        if (saved.Saved)
        {
            var discounted = Total();
            Check(discounted < original && Math.Abs(original - discounted - original * 0.01m) < 0.05m,
                $"итог изменился на 1%: {original:N2} → {discounted:N2}");
            var restored = SqlServerActs.SaveContractDiscount(connectionString, contractId, 0m, saved.Revision);
            Check(restored.Saved, "SaveContractDiscount 0%: " + (restored.Error ?? "сохранено"));
            if (restored.Saved) Check(Math.Abs(Total() - original) < 0.005m, "итог договора восстановлен");
        }

        long actId = 0;
        try
        {
            actId = SqlServerActs.Create(connectionString, contractId, RevisionOf());
            Check(actId > 0, "Create КС-2");
        }
        catch (Exception ex) { Check(false, "Create КС-2: " + ex.Message); }
        if (actId > 0)
        {
            try
            {
                var percent = SqlServerActs.SaveDiscount(connectionString, actId, "percent", 1m, RevisionOf());
                Check(percent.Saved, "SaveDiscount percent 1%: " + (percent.Error ?? "сохранено"));
                using (var conn = SqlServerDatabase.Open(connectionString))
                {
                    var act = SqlServerActs.ActsOf(conn, contractId).Single(a => a.Id == actId);
                    Check(act.DiscountOverride && act.DiscountPercent == 1m, "скидка КС прочитана после записи");
                }
                var inherited = SqlServerActs.SaveDiscount(connectionString, actId, "contract", null, RevisionOf());
                Check(inherited.Saved, "SaveDiscount contract: " + (inherited.Error ?? "сохранено"));

                var sections = SqlServerActs.ReadSections(connectionString, actId);
                var savedSections = SqlServerActs.SaveSections(connectionString, actId, sections.Suggested, sections.Revision);
                Check(savedSections.Saved, "SaveSections: " + (savedSections.Error ?? "сохранено"));
                var actual = SqlServerActs.ReadSections(connectionString, actId);
                Check(actual.SavedJson.Length > 0 && actual.SavedJson != sections.SavedJson,
                    "ручная разбивка КС прочитана после записи");

                var sign = SqlServerActs.ToggleSigned(connectionString, actId, true, RevisionOf());
                Check(sign.Saved, "ToggleSigned(true): " + (sign.Error ?? "сохранено"));
                var unsign = sign.Saved
                    ? SqlServerActs.ToggleSigned(connectionString, actId, false, sign.Revision) : null;
                Check(unsign?.Saved == true, "ToggleSigned(false): " + (unsign?.Error ?? "сохранено"));
            }
            finally
            {
                var deleted = SqlServerActs.Delete(connectionString, actId, RevisionOf());
                Check(deleted.Saved, "Delete черновика КС-2: " + (deleted.Error ?? "удалён"));
            }
        }

        using (var conn = SqlServerDatabase.Open(connectionString))
        {
            var s0 = SqlServerUndo.Snapshot(conn, null, checked((int)contractId));
            Check(s0 is not null, "снимок Undo");
            if (s0 is not null)
            {
                var discount = SqlServerActs.SaveContractDiscount(connectionString, contractId, 1m, RevisionOf());
                Check(discount.Saved, "Undo: скидка 1% сохранена");
                if (discount.Saved)
                {
                    using var after = SqlServerDatabase.Open(connectionString);
                    var s1 = SqlServerUndo.Snapshot(after, null, checked((int)contractId));
                    Check(s1 is not null, "снимок после скидки");
                    if (s1 is not null)
                    {
                        var undo = SqlServerUndo.Apply(connectionString, checked((int)contractId), s0, s1, true);
                        Check(undo.Status == UndoApplyStatus.Applied,
                            "Undo: " + undo.Status + (undo.Error is null ? "" : " — " + undo.Error));
                        Check(Math.Abs(Total() - original) < 0.005m, "Undo восстановил итог");
                        var redo = SqlServerUndo.Apply(connectionString, checked((int)contractId), s1, s0, false);
                        Check(redo.Status == UndoApplyStatus.Applied,
                            "Redo: " + redo.Status + (redo.Error is null ? "" : " — " + redo.Error));
                        var undoAgain = SqlServerUndo.Apply(connectionString, checked((int)contractId), s0, s1, true);
                        Check(undoAgain.Status == UndoApplyStatus.Applied,
                            "Повторный Undo: " + undoAgain.Status + (undoAgain.Error is null ? "" : " — " + undoAgain.Error));
                        Check(Math.Abs(Total() - original) < 0.005m, "Итог после Undo/Redo/Undo совпал");
                    }
                }
            }
        }

        Console.WriteLine(failures == 0
            ? "ПРИЁМКА v31: OK (чтение, скидки, разбивка, КС-2, Undo/Redo)."
            : $"ПРИЁМКА v31: ОШИБОК {failures}.");
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------- trash roundtrips

    /// <summary>Удалить документ с копией в корзину и вернуть — сверка до/после по живой базе.</summary>
    private static int TrashRoundtrip(string connectionString, long contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        Console.WriteLine($"База: {conn.Database}, документ: {contractId}");

        var before = ContractShape(conn, contractId);
        Console.WriteLine($"До:       {before.Summary}");

        var revision = SqlServerDatabase.Revision(conn);
        SqlServerWorkspace.DeleteContract(connectionString, contractId, revision);
        Console.WriteLine("Удалён (копия в корзине).");

        long trashId;
        using (var cmd = new SqlCommand("SELECT MAX(id) FROM dbo.trash", conn))
            trashId = SqlInteger.RequireInt64(cmd.ExecuteScalar() ?? 0L);
        Console.WriteLine($"Запись корзины: {trashId}");

        var entry = SqlServerTrash.List(connectionString).FirstOrDefault(e => e.Id == trashId);
        Console.WriteLine($"В корзине: {entry?.Kind} «{entry?.Title}» строк: {entry?.Rows}, осталось: {entry?.DaysLeft} дн.");
        if (entry is null) return Fail("Запись корзины не найдена.");

        var restored = SqlServerTrash.Restore(connectionString, trashId, SqlServerDatabase.Revision(conn));
        Console.WriteLine($"Возвращён как {restored.Kind} с новым id: {restored.Id}");

        var after = ContractShape(conn, restored.Id);
        Console.WriteLine($"После:    {after.Summary}");

        var diffs = Compare(before, after);
        foreach (var diff in diffs) Console.WriteLine("  ← " + diff);
        Console.WriteLine(diffs.Count == 0
            ? "OK: структура, цены, акты, закупки, движения, история цен и итоги совпали."
            : $"РАСХОЖДЕНИЙ: {diffs.Count}");
        return diffs.Count == 0 ? 0 : 1;
    }

    /// <summary>Удалить объект целиком с копией в корзину и вернуть — сверка до/после.</summary>
    private static int TrashObjectRoundtrip(string connectionString, long objectId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        Console.WriteLine($"База: {conn.Database}, объект: {objectId}");

        var before = ObjectShape(conn, objectId);
        Console.WriteLine($"До:       {before.Summary}");

        var revision = SqlServerDatabase.Revision(conn);
        SqlServerWorkspace.DeleteObject(connectionString, objectId, revision);
        Console.WriteLine("Удалён (копия в корзине).");

        long trashId;
        using (var cmd = new SqlCommand("SELECT MAX(id) FROM dbo.trash", conn))
            trashId = SqlInteger.RequireInt64(cmd.ExecuteScalar() ?? 0L);
        var entry = SqlServerTrash.List(connectionString).FirstOrDefault(e => e.Id == trashId);
        Console.WriteLine($"В корзине: {entry?.Kind} «{entry?.Title}» строк: {entry?.Rows}");
        if (entry is null) return Fail("Запись корзины не найдена.");

        var restored = SqlServerTrash.Restore(connectionString, trashId, SqlServerDatabase.Revision(conn));
        Console.WriteLine($"Возвращён как {restored.Kind} с новым id: {restored.Id}");

        var after = ObjectShape(conn, restored.Id);
        Console.WriteLine($"После:    {after.Summary}");

        var diffs = Compare(before, after);
        foreach (var diff in diffs) Console.WriteLine("  ← " + diff);
        Console.WriteLine(diffs.Count == 0
            ? "OK: документы, разделы/позиции, акты, закупки, движения, ведомости, допуски и итоги совпали."
            : $"РАСХОЖДЕНИЙ: {diffs.Count}");
        return diffs.Count == 0 ? 0 : 1;
    }

    /// <summary>Разовое восстановление записи корзины (ручной инструмент).</summary>
    private static int RestoreOnly(string connectionString, long trashId)
    {
        var restored = SqlServerTrash.Restore(connectionString, trashId, RevisionFresh(connectionString));
        Console.WriteLine($"Возвращено: {restored.Kind} id={restored.Id}");
        return 0;
    }

    private static string RevisionFresh(string connectionString)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        return SqlServerDatabase.Revision(conn);
    }

    // ------------------------------------------------------------------ формы

    private sealed class Shape
    {
        public string Summary = "";
        public List<(string Table, long Count)> Counts = new();
        public List<string> Lines = new();
    }

    private static Shape ContractShape(SqlConnection conn, long contractId)
    {
        var shape = new Shape();
        var title = Scalar(conn, "SELECT kind+':'+ISNULL(number,'')+':'+ISNULL(name,'') FROM dbo.contracts WHERE id=@id",
            contractId);
        var totals = Totals.ReadOne(conn, checked((int)contractId));
        var est = EstimateReader.Load(conn, checked((int)contractId));

        foreach (var (table, sql) in new[]
        {
            ("разделов", "SELECT COUNT_BIG(*) FROM dbo.systems WHERE contract_id=@id"),
            ("позиций", "SELECT COUNT_BIG(*) FROM dbo.positions WHERE system_id IN (SELECT id FROM dbo.systems WHERE contract_id=@id)"),
            ("закупок", "SELECT COUNT_BIG(*) FROM dbo.procurement WHERE position_id IN (SELECT id FROM dbo.positions WHERE system_id IN (SELECT id FROM dbo.systems WHERE contract_id=@id))"),
            ("движений", "SELECT COUNT_BIG(*) FROM dbo.movements WHERE position_id IN (SELECT id FROM dbo.positions WHERE system_id IN (SELECT id FROM dbo.systems WHERE contract_id=@id))"),
            ("актов", "SELECT COUNT_BIG(*) FROM dbo.acts WHERE contract_id=@id"),
            ("строк актов", "SELECT COUNT_BIG(*) FROM dbo.act_lines WHERE act_id IN (SELECT id FROM dbo.acts WHERE contract_id=@id)"),
            ("строк выдачи", "SELECT COUNT_BIG(*) FROM dbo.shipment_lines WHERE position_id IN (SELECT id FROM dbo.positions WHERE system_id IN (SELECT id FROM dbo.systems WHERE contract_id=@id))"),
            ("СМР-затрат", "SELECT COUNT_BIG(*) FROM dbo.smr_costs WHERE contract_id=@id"),
            ("файлов", "SELECT COUNT_BIG(*) FROM dbo.attachments WHERE contract_id=@id"),
            ("спецификаций", "SELECT COUNT_BIG(*) FROM dbo.spec_revisions WHERE contract_id=@id"),
            ("истории цен", "SELECT COUNT_BIG(*) FROM dbo.price_history WHERE contract_id=@id"),
        })
        {
            var count = Count(conn, sql, contractId);
            shape.Counts.Add((table, count));
        }

        foreach (var row in est.Rows)
        {
            var indent = new string(' ', row.Level * 2);
            shape.Lines.Add(row.IsSystem
                ? $"{indent}[{row.Name}]"
                : $"{indent}{row.Num}|{row.Name}|{row.Unit}|{row.Qty:0.######}|{row.Price:0.######}|{row.Amount:0.######}");
        }

        shape.Summary = $"«{title}» — {est.Rows.Count} строк, " +
            string.Join(", ", shape.Counts.Select(c => $"{c.Table}: {c.Count}")) +
            $", итог: {totals.Total:N2} (мат {totals.Materials:N2}, СМР {totals.Smr:N2})";
        return shape;
    }

    private static Shape ObjectShape(SqlConnection conn, long objectId)
    {
        var shape = new Shape();
        var name = Scalar(conn, "SELECT name FROM dbo.objects WHERE id=@id", objectId);
        using (var cmd = new SqlCommand(
            "SELECT id, kind, ISNULL(number,''), ISNULL(name,'') FROM dbo.contracts WHERE object_id=@id ORDER BY [order], id", conn))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                shape.Lines.Add($"doc|{Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture)}|" +
                    $"{Convert.ToString(r.GetValue(2), CultureInfo.InvariantCulture)}|{Convert.ToString(r.GetValue(3), CultureInfo.InvariantCulture)}");
        }

        foreach (var (table, sql) in new[]
        {
            ("документов", "SELECT COUNT_BIG(*) FROM dbo.contracts WHERE object_id=@id"),
            ("разделов", "SELECT COUNT_BIG(*) FROM dbo.systems WHERE contract_id IN (SELECT id FROM dbo.contracts WHERE object_id=@id)"),
            ("позиций", "SELECT COUNT_BIG(*) FROM dbo.positions WHERE system_id IN (SELECT id FROM dbo.systems WHERE contract_id IN (SELECT id FROM dbo.contracts WHERE object_id=@id))"),
            ("актов", "SELECT COUNT_BIG(*) FROM dbo.acts WHERE contract_id IN (SELECT id FROM dbo.contracts WHERE object_id=@id)"),
            ("строк актов", "SELECT COUNT_BIG(*) FROM dbo.act_lines WHERE act_id IN (SELECT id FROM dbo.acts WHERE contract_id IN (SELECT id FROM dbo.contracts WHERE object_id=@id))"),
            ("ведомостей", "SELECT COUNT_BIG(*) FROM dbo.shipments WHERE object_id=@id"),
            ("строк ведомостей", "SELECT COUNT_BIG(*) FROM dbo.shipment_lines WHERE shipment_id IN (SELECT id FROM dbo.shipments WHERE object_id=@id)"),
            ("допусков", "SELECT COUNT_BIG(*) FROM dbo.access_lists WHERE object_id=@id"),
            ("металла-ссылок", "SELECT COUNT_BIG(*) FROM dbo.metal_entries WHERE object_id=@id"),
            ("истории цен", "SELECT COUNT_BIG(*) FROM dbo.price_history WHERE contract_id IN (SELECT id FROM dbo.contracts WHERE object_id=@id)"),
        })
        {
            var count = Count(conn, sql, objectId);
            shape.Counts.Add((table, count));
        }

        // Порядок и содержимое строк всех документов объекта.
        var docIds = new List<long>();
        using (var cmd = new SqlCommand(
            "SELECT id FROM dbo.contracts WHERE object_id=@id ORDER BY [order], id", conn))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
            using var r = cmd.ExecuteReader();
            while (r.Read()) docIds.Add(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture));
        }
        for (var i = 0; i < docIds.Count; i++)
        {
            var est = EstimateReader.Load(conn, checked((int)docIds[i]));
            shape.Lines.Add($"est|{i}|{est.Rows.Count}|{est.Totals.Total:N2}");
        }

        shape.Summary = $"«{name}» — " + string.Join(", ", shape.Counts.Select(c => $"{c.Table}: {c.Count}"));
        return shape;
    }

    private static List<string> Compare(Shape before, Shape after)
    {
        var diffs = new List<string>();
        foreach (var (b, a) in before.Counts.Zip(after.Counts))
            if (b.Count != a.Count) diffs.Add($"{b.Table}: до {b.Count}, после {a.Count}");
        if (before.Lines.Count != after.Lines.Count)
            diffs.Add($"строк формы: до {before.Lines.Count}, после {after.Lines.Count}");
        for (var i = 0; i < Math.Min(before.Lines.Count, after.Lines.Count); i++)
            if (!string.Equals(before.Lines[i], after.Lines[i], StringComparison.Ordinal))
            {
                diffs.Add($"строка {i + 1}: до «{before.Lines[i]}», после «{after.Lines[i]}»");
                if (diffs.Count > 15) { diffs.Add("…"); break; }
            }
        return diffs;
    }

    private static long Count(SqlConnection conn, string sql, long id)
    {
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        return SqlInteger.RequireInt64(cmd.ExecuteScalar());
    }

    private static string Scalar(SqlConnection conn, string sql, long id)
    {
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }
}
