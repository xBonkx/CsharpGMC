using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Cli;

/// <summary>
/// Синтетический бенчмарк ядра C# на пилотной базе GmcSmetaPilot_* (сценарии бенчмарка
/// описаны в bench/README.md репозитория). Чтение и запись идут реальным ядром,
/// как их делает UI. Запись разрешена только в пилот-базы.
///   dotnet run --project src/GmcSmeta.Cli -- bench "&lt;строка подключения&gt;" [contractId] [passes]
/// Сценарии:
///   open        — открытие КП целиком (Revision + EstimateReader.Load + Totals.ReadOne + Undo-снимок)
///   totals      — только сводка итогов (Totals.ReadOne)
///   savecell    — правка одной ячейки qty (SaveCell, транзакция с rev-гвардом)
///   reload      — перечитывание после правки (Load + ReadOne + снимок)
///   save100     — 100 правок qty подряд (как групповая правка)
///   addpos      — добавление 10 позиций (AddPosition)
///   addsec      — добавление 3 разделов (AddSection)
///   buyrows     — открытие закупок (BuyRows)
/// </summary>
public static class Bench
{
    private const int TopPositionsForSave = 100;

    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Использование: bench <строка подключения> [contractId] [passes]");
            return 2;
        }
        var connectionString = args[0];
        // The benchmark performs INSERT, UPDATE and DELETE in addition to reads.
        // Validate BEFORE opening a connection: SqlServerDatabase.Open is read-only
        // and intentionally accepts any schema-v28 database, so it is not a write guard.
        var target = new SqlConnectionStringBuilder(connectionString);
        if (!Regex.IsMatch(target.InitialCatalog,
                @"^GmcSmetaPilot_Bench_[A-Za-z0-9_]{1,50}$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            Console.Error.WriteLine(
                "Бенчмарк с записью разрешён только для отдельной GmcSmetaPilot_Bench_* базы.");
            return 2;
        }
        var contractId = args.Length > 1 && int.TryParse(args[1], out var id) ? id : 1;
        var passes = args.Length > 2 && int.TryParse(args[2], out var p) ? Math.Max(1, p) : 3;
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"База: {new SqlConnectionStringBuilder(connectionString).InitialCatalog}, " +
                          $"КП: {contractId}, прогонов: {passes}");

        using (var conn = SqlServerDatabase.Open(connectionString))
        {
            // Check the server-selected database, not just user-supplied InitialCatalog.
            using var verify = new SqlCommand("SELECT DB_NAME()", conn);
            var actualDatabase = Convert.ToString(
                verify.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
            if (!string.Equals(actualDatabase, target.InitialCatalog,
                    StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"База подключения {actualDatabase} не совпадает с ожидаемой {target.InitialCatalog}.");
                return 2;
            }
            Console.WriteLine($"SQL Server: {conn.DataSource}; Database: {actualDatabase}");
            Console.WriteLine($"meta.rev: {SqlServerDatabase.Revision(conn)}");
        }

        var order = new List<string>();
        var samples = new Dictionary<string, List<double>>();
        var notes = new Dictionary<string, string>();
        void Report(string name, IEnumerable<double> values, string note = "")
        {
            if (!samples.TryGetValue(name, out var list))
            {
                list = [];
                samples[name] = list;
                order.Add(name);
            }
            list.AddRange(values);
            if (note.Length > 0) notes[name] = note;
        }

        // ------------------------------------------------------------- open / totals / buyrows
        for (var i = 0; i < passes; i++)
        {
            using var conn = SqlServerDatabase.Open(connectionString);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var est = EstimateReader.Load(conn, contractId);
            clock.Stop();
            var loadMs = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            var totals = Totals.ReadOne(conn, contractId);
            clock.Stop();
            var totalsMs = clock.Elapsed.TotalMilliseconds;
            if (i == 0)
                Console.WriteLine($"КП: {est.Title}, позиций {est.Positions}, строк {est.Rows.Count}, " +
                                  $"итог {totals?.Total:0.00}");

            clock.Restart();
            _ = EstimateViewProjection.Apply(est.Rows);
            clock.Stop();
            var projMs = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            using (var snapConn = SqlServerDatabase.Open(connectionString))
            {
                var snapshot = SqlServerUndo.Snapshot(snapConn, null, contractId);
                var snapMs = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                _ = snapshot?.Canonical;
                var canonicalMs = clock.Elapsed.TotalMilliseconds;
                Report("open (полный путь)", [loadMs + totalsMs + snapMs + canonicalMs + projMs],
                    $"load {loadMs:0.0} + totals {totalsMs:0.0} + undo {snapMs:0.0} + " +
                    $"canon {canonicalMs:0.0} + proj {projMs:0.0} мс");
                Report("  ├─ undo snapshot (9 SELECT)", [snapMs]);
                Report("  ├─ undo canonical (JSON)", [canonicalMs]);
            }

            Report("  ├─ EstimateReader.Load", [loadMs], $"{est.Rows.Count} строк");
            Report("  ├─ Totals.ReadOne", [totalsMs]);
            Report("  ├─ проекция/фильтр", [projMs]);
            Report("totals (ReadOne отдельно)", [totalsMs]);

            clock.Restart();
            var buy = SqlServerProcurement.BuyRows(connectionString, contractId);
            clock.Stop();
            Report("buyrows (закупки)", [clock.Elapsed.TotalMilliseconds], $"{buy.Count} строк");

            clock.Restart();
            var allTotals = Totals.ReadAll(conn);
            clock.Stop();
            Report("object card (Totals.ReadAll)", [clock.Elapsed.TotalMilliseconds],
                $"{allTotals.Count} документов, вся база");
        }

        // ------------------------------------------------------------- savecell / reload / save100
        var topIds = new List<long>();
        var originalQty = new Dictionary<long, decimal>();
        using (var conn = SqlServerDatabase.Open(connectionString))
        using (var cmd = new SqlCommand(
            "SELECT TOP (@n) p.id, p.qty FROM dbo.positions p " +
            "JOIN dbo.systems sy ON sy.id = p.system_id " +
            "WHERE sy.contract_id = @c AND p.parent_id IS NULL ORDER BY p.id", conn))
        {
            cmd.Parameters.AddWithValue("@n", TopPositionsForSave);
            cmd.Parameters.AddWithValue("@c", contractId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var posId = SqlInteger.ReadInt64(reader, 0);
                topIds.Add(posId);
                originalQty[posId] = (decimal)reader.GetDouble(1);
            }
        }
        if (topIds.Count == 0)
        {
            Console.WriteLine("В КП нет позиций — сценарии правки пропущены.");
            PrintTable(order, samples, notes);
            return 0;
        }

        for (var pass = 0; pass < passes; pass++)
        {
            // одна правка
            var rev = CurrentRevision(connectionString);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var saved = SqlServerDatabase.SaveCell(connectionString, topIds[0], "qty",
                (12.34 + pass).ToString(CultureInfo.InvariantCulture), rev);
            clock.Stop();
            var saveMs = clock.Elapsed.TotalMilliseconds;
            if (!saved.Saved) throw new InvalidOperationException($"SaveCell не сохранил: {saved.Error}");
            Report("savecell (1 правка)", [saveMs]);

            // перечитывание после правки (ReloadDocumentAsync)
            clock.Restart();
            using (var conn = SqlServerDatabase.Open(connectionString))
            {
                var reloaded = EstimateReader.Load(conn, contractId);
                Totals.ReadOne(conn, contractId);
                EstimateViewProjection.Apply(reloaded.Rows);
            }
            using (var snapConn = SqlServerDatabase.Open(connectionString))
            {
                var reloadSnapshot = SqlServerUndo.Snapshot(snapConn, null, contractId);
                _ = reloadSnapshot?.Canonical;   // UndoManager.Remember compares canonical JSON
            }
            clock.Stop();
            var reloadMs = clock.Elapsed.TotalMilliseconds;
            Report("reload (после правки)", [reloadMs]);
            Report("edit UI (save+reload)", [saveMs + reloadMs],
                $"save {saveMs:0.0} + reload {reloadMs:0.0} мс");

            // 100 правок подряд, каждая со своей транзакцией и свежей ревизией
            clock.Restart();
            var currentRev = CurrentRevision(connectionString);
            for (var k = 0; k < topIds.Count; k++)
            {
                var r = SqlServerDatabase.SaveCell(connectionString, topIds[k], "qty",
                    (1000 + k + pass).ToString(CultureInfo.InvariantCulture), currentRev);
                if (!r.Saved) throw new InvalidOperationException($"SaveCell[{k}] не сохранил: {r.Error}");
                currentRev = r.Revision;
            }
            clock.Stop();
            Report("save100 (100 правок)", [clock.Elapsed.TotalMilliseconds], "всего, мс");

            RestoreQuantities(connectionString, originalQty);
        }

        // ------------------------------------------------------------- addpos / addsec
        var systemId = FirstSystemId(connectionString, contractId);
        for (var pass = 0; pass < passes; pass++)
        {
            var createdPositions = new List<long>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var rev = CurrentRevision(connectionString);
            for (var k = 0; k < 10; k++)
            {
                var added = SqlServerDatabase.AddPosition(connectionString, contractId, systemId,
                    null, null, rev);
                if (!added.Saved) throw new InvalidOperationException($"AddPosition не добавил: {added.Error}");
                if (added.PositionId is { } newId) createdPositions.Add(newId);
                rev = added.Revision;
            }
            clock.Stop();
            Report("addpos (10 позиций)", [clock.Elapsed.TotalMilliseconds], "всего, мс");

            var createdSystems = new List<long>();
            clock.Restart();
            for (var k = 0; k < 3; k++)
            {
                var sec = SqlServerDatabase.AddSection(connectionString, contractId,
                    $"БЕНЧ раздел {pass}-{k}-{Guid.NewGuid():N}", subsection: false, null, rev);
                if (!sec.Saved) throw new InvalidOperationException($"AddSection не добавил: {sec.Error}");
                if (sec.SystemId is { } secId) createdSystems.Add(secId);
                rev = sec.Revision;
            }
            clock.Stop();
            Report("addsec (3 раздела)", [clock.Elapsed.TotalMilliseconds], "всего, мс");

            CleanupCreated(connectionString, createdPositions, createdSystems);
        }

        PrintTable(order, samples, notes);
        return 0;
    }

    private static string CurrentRevision(string connectionString)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        return SqlServerDatabase.Revision(conn);
    }

    private static long FirstSystemId(string connectionString, int contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        using var cmd = new SqlCommand(
            "SELECT MIN(id) FROM dbo.systems WHERE contract_id = @c", conn);
        cmd.Parameters.AddWithValue("@c", contractId);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void RestoreQuantities(string connectionString, Dictionary<long, decimal> original)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction();
        foreach (var (id, qty) in original)
        {
            using var cmd = new SqlCommand(
                "UPDATE dbo.positions SET qty = @q WHERE id = @id", conn, tx);
            cmd.Parameters.AddWithValue("@q", qty);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void CleanupCreated(string connectionString, List<long> positions, List<long> systems)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction();
        foreach (var id in positions)
        {
            using var cmd = new SqlCommand("DELETE FROM dbo.positions WHERE id = @id", conn, tx);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
        foreach (var id in systems)
        {
            using var cmd = new SqlCommand("DELETE FROM dbo.systems WHERE id = @id", conn, tx);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void PrintTable(
        List<string> order, Dictionary<string, List<double>> samples, Dictionary<string, string> notes)
    {
        Console.WriteLine();
        Console.WriteLine($"{"сценарий",-28}{"мин, мс",10}{"медиана",10}{"макс",10}  примечание");
        foreach (var name in order)
        {
            var sorted = samples[name].OrderBy(x => x).ToList();
            double median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
                : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
            var note = notes.TryGetValue(name, out var n) ? n : "";
            Console.WriteLine($"{name,-28}{sorted[0],10:0.0}{median,10:0.0}{sorted[^1],10:0.0}  {note}");
        }
    }
}
