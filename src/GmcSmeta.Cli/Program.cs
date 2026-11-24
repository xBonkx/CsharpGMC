using System.Text;
using GmcSmeta.Cli;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

// Небольшая консоль для проверок. Команда bench изменяет ТОЛЬКО выделенную GmcSmetaPilot_Bench_* БД.
//   dotnet run --project src/GmcSmeta.Cli -- totals   [путь к базе]
//   dotnet run --project src/GmcSmeta.Cli -- estimate <id> [путь к базе]
//   dotnet run --project src/GmcSmeta.Cli -- check    [путь к базе]
//   dotnet run --project src/GmcSmeta.Cli -- sql <status|smoke|trash|trash-obj> "<строка подключения>" [id]
//   dotnet run --project src/GmcSmeta.Cli -- bench "<строка подключения>" [contractId] [passes]
Console.OutputEncoding = Encoding.UTF8;

var command = args.Length > 0 ? args[0] : "totals";
if (command == "sql")
{
    return SqlLive.Run(args.Skip(1).ToArray());
}
if (command == "bench")
{
    return Bench.Run(args.Skip(1).ToArray());
}

var estimateId = 1;
var dbPath = DefaultBasePath();
if (command == "estimate")
{
    if (args.Length > 1) int.TryParse(args[1], out estimateId);
    if (args.Length > 2) dbPath = args[2];
}
else if (args.Length > 1)
{
    dbPath = args[1];
}

if (!File.Exists(dbPath))
{
    Console.WriteLine($"Нет файла базы: {dbPath}");
    return 2;
}

using var conn = Database.Open(dbPath, readOnly: true);
var totals = Totals.ReadAll(conn);

switch (command)
{
    case "totals":
        Console.WriteLine($"База: {dbPath}");
        Console.WriteLine($"Документов: {totals.Count}, позиций: {totals.Sum(t => t.Positions)}");
        Console.WriteLine();
        Console.WriteLine($"{"id",4}  {"вид",-6} {"позиций",8} {"материалы",14} {"СМР",12} {"итого",14} {"НДС",12}");
        foreach (var t in totals.Take(12))
            Console.WriteLine($"{t.Id,4}  {t.Kind,-6} {t.Positions,8} {t.Materials,14:0.00} {t.Smr,12:0.00} {t.Total,14:0.00} {t.Vat,12:0.00}");
        if (totals.Count > 12) Console.WriteLine($"… ещё {totals.Count - 12}");
        return 0;

    case "check":
        var json = Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "totals_python.json");
        if (!File.Exists(json))
        {
            Console.WriteLine($"Нет эталона: {json} (снимите tools/parity_dump.py)");
            return 2;
        }
        return Parity.Check(totals, json);

    case "estimate":
        var id = estimateId;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var est = EstimateReader.Load(conn, id);
        clock.Stop();
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
        Console.WriteLine("Команды: totals | estimate <id> | check");
        return 2;
}

static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

static string DefaultBasePath()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "db", "base.db");
        if (File.Exists(candidate)) return candidate;
    }
    return Path.Combine(Directory.GetCurrentDirectory(), "db", "base.db");
}
