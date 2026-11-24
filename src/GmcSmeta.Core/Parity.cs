using System.Text.Json;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core;

/// <summary>
/// Сверка итогов C# с эталоном Python (db/totals_python.json).
/// Допуск — полкопейки; всё, что больше, печатается списком.
/// </summary>
public static class Parity
{
    public const double Tolerance = 0.006;

    public static int Check(IReadOnlyList<ContractTotals> mine, string jsonPath)
    {
        var byId = mine.ToDictionary(x => x.Id);
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var rows = doc.RootElement.GetProperty("contracts");

        var problems = new List<string>();
        var worst = 0d;
        var compared = 0;

        foreach (var row in rows.EnumerateArray())
        {
            var id = row.GetProperty("id").GetInt32();
            if (!byId.TryGetValue(id, out var m))
            {
                problems.Add($"договор {id}: есть в Python, нет в C#");
                continue;
            }

            void Compare(string title, decimal got, string field)
            {
                var py = row.GetProperty(field).GetDouble();
                var diff = Math.Abs((double)got - py);
                worst = Math.Max(worst, diff);
                compared++;
                if (diff > Tolerance)
                    problems.Add($"договор {id}: {title} — C# {got:0.00}, Python {py:0.00} (разница {diff:0.0000})");
            }

            Compare("материалы", m.Materials, "materials");
            Compare("СМР", m.Smr, "smr");
            Compare("без НДС", m.Net, "net");
            Compare("итого", m.Total, "total");
            Compare("НДС", m.Vat, "vat");
        }

        foreach (var extra in byId.Keys.Except(rows.EnumerateArray().Select(r => r.GetProperty("id").GetInt32())))
            problems.Add($"договор {extra}: есть в C#, нет в Python");

        Console.WriteLine($"Сверено документов: {rows.GetArrayLength()}, чисел: {compared}");
        Console.WriteLine($"Максимальное расхождение: {worst:0.000000} (допуск {Tolerance})");
        if (problems.Count == 0)
        {
            Console.WriteLine("Совпало.");
            return 0;
        }
        Console.WriteLine($"Расхождений: {problems.Count}");
        foreach (var p in problems.Take(30)) Console.WriteLine("  " + p);
        return 1;
    }
}
