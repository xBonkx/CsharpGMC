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
            Console.WriteLine("Использование: sql <status|smoke|trash|trash-obj|restore> <строка подключения> [id]");
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
            "smoke" => Smoke(connectionString),
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
            trashId = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
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
            trashId = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
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
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string Scalar(SqlConnection conn, string sql, long id)
    {
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }
}
