using System.Data.Common;
using System.Globalization;
using System.Text;
using GmcSmeta.Cli;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

// Diagnostic CLI. No implicit database discovery or filesystem storage backend.
Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 0 || (args.Length == 1 && args[0] is "--help" or "-h"))
{
    PrintUsage();
    return args.Length == 0 ? 2 : 0;
}

var command = args[0];
if (command == "sql") return SqlLive.Run(args.Skip(1).ToArray());
if (command == "bench") return Bench.Run(args.Skip(1).ToArray());

var estimateId = 0;
if (!(command == "totals" && args.Length == 1) &&
    !(command == "estimate" && args.Length == 2 &&
      int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out estimateId) && estimateId > 0) &&
    !(command == "check" && args.Length == 2 &&
      string.Equals(Path.GetExtension(args[1]), ".json", StringComparison.OrdinalIgnoreCase) && File.Exists(args[1])))
{
    PrintUsage();
    return 2;
}

var connectionString = Environment.GetEnvironmentVariable("GMC_SMETA_SQL_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Укажите GMC_SMETA_SQL_CONNECTION для выделенной тестовой базы.");
    return 2;
}

try
{
    var target = new SqlConnectionStringBuilder(connectionString);
    if (!target.InitialCatalog.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase) ||
        target.InitialCatalog.Contains("Cutover", StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(target.DataSource) ||
        !string.IsNullOrEmpty(target.AttachDBFilename) ||
        !string.IsNullOrEmpty(target.FailoverPartner))
    {
        Console.Error.WriteLine("Разрешена только явно выбранная тестовая SQL Server база GmcSmetaPilot_* без Cutover.");
        return 2;
    }
    using var conn = SqlServerDatabase.Open(connectionString);
    var revision = SqlServerDatabase.Revision(conn);
    if (string.IsNullOrWhiteSpace(revision))
        throw new InvalidOperationException("Отсутствует ревизия базы.");
    switch (command)
    {
        case "totals":
            var totals = Totals.ReadAll(conn);
            EnsureRevision(conn, revision);
            Console.WriteLine($"База: {conn.Database}");
            Console.WriteLine($"Документов: {totals.Count}, позиций: {totals.Sum(t => t.Positions)}");
            Console.WriteLine();
            Console.WriteLine($"{"id",4}  {"вид",-6} {"позиций",8} {"материалы",14} {"СМР",12} {"итого",14} {"НДС",12}");
            foreach (var t in totals.Take(12))
                Console.WriteLine($"{t.Id,4}  {t.Kind,-6} {t.Positions,8} {t.Materials,14:0.00} {t.Smr,12:0.00} {t.Total,14:0.00} {t.Vat,12:0.00}");
            if (totals.Count > 12) Console.WriteLine($"… ещё {totals.Count - 12}");
            return 0;
    
        case "check":
            var checkedTotals = Totals.ReadAll(conn);
            EnsureRevision(conn, revision);
            return Parity.Check(checkedTotals, args[1]);
    
        case "estimate":
            var id = estimateId;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var est = EstimateReader.Load(conn, id);
            clock.Stop();
            EnsureRevision(conn, revision);
            Console.WriteLine($"{est.Title}");
            Console.WriteLine($"объект: {est.ObjectName}; фирма: {est.Company}; форма: {est.Form}; НДС: {est.VatRate}");
            Console.WriteLine($"систем: {est.Systems}, позиций: {est.Positions}, строк в таблице: {est.Rows.Count}");
            Console.WriteLine($"загрузка: {clock.ElapsedMilliseconds} мс (чтение базы + сборка таблицы)");
            Console.WriteLine($"материалы: {est.Totals.Materials:0.00}, СМР: {est.Totals.Smr:0.00}, " +
                              $"транспорт: {est.Totals.Transport:0.00}, итого: {est.Totals.Total:0.00}, НДС: {est.Totals.Vat:0.00}");
            Console.WriteLine();
            foreach (var row in est.Rows.Take(15))
                Console.WriteLine(row.IsSystem
                    ? $"  {new string(' ', row.Level * 2)}[ {row.Name} ]"
                    : $"  {new string(' ', row.Level * 2)}{row.Num,-8} {Trunc(row.Name, 60),-60} " +
                      $"{row.Unit,-4} {row.Qty,10:0.###} {row.Price,12:0.00} {row.Amount,14:0.00}");
            if (est.Rows.Count > 15) Console.WriteLine($"  … ещё {est.Rows.Count - 15} строк");
            return 0;
    
        default:
            PrintUsage();
            return 2;
    }
    
}
catch (Exception ex)
{
    // Never echo raw connection strings or provider error text containing server details.
    Console.Error.WriteLine("Проверка не выполнена: " + ex.GetType().Name + ".");
    return 1;
}

static void EnsureRevision(DbConnection conn, string revision)
{
    if (!string.Equals(revision, SqlServerDatabase.Revision(conn), StringComparison.Ordinal))
        throw new InvalidOperationException("База изменилась во время чтения; повторите проверку.");
}

static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

static void PrintUsage()
{
    Console.WriteLine("Команды SQL Server (GMC_SMETA_SQL_CONNECTION — отдельная тестовая база):");
    Console.WriteLine("  totals | estimate <id> | check <эталон.json>");
    Console.WriteLine("  sql <status|smoke|schema31|trash|trash-obj|restore|agreement-audit> <строка подключения> [id]");
    Console.WriteLine("  bench <строка подключения> [contractId] [passes]");
}
