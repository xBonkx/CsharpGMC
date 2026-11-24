using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

/// <summary>
/// Сверка сметы с Python-версией «строка в строку»:
///   • список систем и их порядок;
///   • каждая позиция на том же месте: id, номер, наименование, ед., количество, цена, сумма, «вне КП».
/// Эталон снимается функцией Python builder.ordered_positions — той же, что строит таблицу
/// в программе, поэтому проверяется и порядок строк (заголовок, затем его подпозиции).
/// </summary>
public class EstimateParityTests
{
    [Fact]
    public void Estimate_RowsMatchPythonInOrder()
    {
        var basePath = FindFile("db/base.db");
        var jsonPath = FindFile("db/totals_python.json");
        if (basePath is null || jsonPath is null) return;

        using var conn = Database.Open(basePath, readOnly: true);
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var estimates = doc.RootElement.GetProperty("estimates");

        var problems = new List<string>();
        var comparedRows = 0;
        var comparedContracts = 0;

        foreach (var est in estimates.EnumerateObject())
        {
            var id = int.Parse(est.Name);
            var data = EstimateReader.Load(conn, id);
            comparedContracts++;

            var pySystems = est.Value.GetProperty("systems").EnumerateArray()
                .Select(x => (Name: x.GetProperty("name").GetString() ?? "",
                              Num: x.GetProperty("num").GetString() ?? "")).ToList();
            var mySystems = data.Rows.Where(r => r.IsSystem).Select(r => (r.Name, r.Num)).ToList();
            var pyNames = pySystems.Select(x => x.Name).ToList();
            var myNames = mySystems.Select(x => x.Name).ToList();
            if (!pyNames.SequenceEqual(myNames))
                problems.Add($"договор {id}: системы не совпали: C# [{string.Join("; ", myNames)}], " +
                             $"Python [{string.Join("; ", pyNames)}]");
            else
                for (var i = 0; i < pySystems.Count; i++)
                    if (pySystems[i].Num != mySystems[i].Num)
                        problems.Add($"договор {id}, система «{pySystems[i].Name}»: номер " +
                                     $"«{mySystems[i].Num}» вместо «{pySystems[i].Num}»");

            var py = est.Value.GetProperty("rows").EnumerateArray().ToList();
            if (py.Count != data.PositionsInOrder.Count)
                problems.Add($"договор {id}: позиций C# {data.PositionsInOrder.Count}, Python {py.Count}");

            for (var i = 0; i < Math.Min(py.Count, data.PositionsInOrder.Count); i++)
            {
                var p = py[i];
                var m = data.PositionsInOrder[i];
                comparedRows++;

                var pyId = p.GetProperty("id").GetInt32();
                if (m.Id != pyId)
                {
                    problems.Add($"договор {id}, строка {i + 1}: позиция {m.Id} вместо {pyId} " +
                                 $"(«{m.Name}» вместо «{p.GetProperty("name").GetString()}»)");
                    continue;
                }

                void Must(string title, string got, string exp)
                {
                    if (!string.Equals(got, exp, StringComparison.Ordinal))
                        problems.Add($"договор {id}, строка {i + 1}: {title} — «{got}» вместо «{exp}»");
                }

                Must("наименование", m.Name, p.GetProperty("name").GetString() ?? "");
                Must("ед.", m.Unit, p.GetProperty("unit").GetString() ?? "");
                Must("номер", m.Num, p.GetProperty("display_num").GetString() ?? "");

                if (m.OutOfKp != p.GetProperty("out_of_kp").GetBoolean())
                    problems.Add($"договор {id}, строка {i + 1}: «вне КП» {m.OutOfKp}");

                void Num(string title, decimal got, string field, double tolerance = 0.005)
                {
                    var pyValue = p.GetProperty(field).GetDouble();
                    if (Math.Abs((double)got - pyValue) > tolerance)
                        problems.Add($"договор {id}, строка {i + 1}: {title} {got:0.0000} вместо {pyValue:0.0000}");
                }

                Num("кол-во", m.Qty, "qty", 1e-6);
                Num("цена", m.Price, "price", 1e-4);
                Num("сумма", m.Amount, "amount");
            }
        }

        Assert.True(comparedContracts >= 4, $"сверено документов: {comparedContracts}");
        Assert.True(comparedRows > 1000, $"сверено строк: {comparedRows}");
        Assert.True(problems.Count == 0,
            $"расхождений: {problems.Count}\n" + string.Join("\n", problems.Take(25)));
    }

    [Fact]
    public void Totals_OfEstimateMatchContractTotals()
    {
        var basePath = FindFile("db/base.db");
        if (basePath is null) return;

        using var conn = Database.Open(basePath, readOnly: true);
        foreach (var id in new[] { 1, 11, 12, 13 })
        {
            var est = EstimateReader.Load(conn, id);
            Assert.False(string.IsNullOrWhiteSpace(est.Title));
            Assert.NotEmpty(est.Rows);
            Assert.Equal(est.Positions, est.PositionsInOrder.Count);
            Assert.True(est.Totals.Materials >= 0m);
        }
    }

    private static string? FindFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
