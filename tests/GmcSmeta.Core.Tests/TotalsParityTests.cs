using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

/// <summary>
/// Сверка с Python-версией на РЕАЛЬНОЙ базе (локальная копия db/base.db).
/// Эталон снимает tools/parity_dump.py: для каждого документа — материалы, СМР,
/// сумма без НДС, итого, НДС. Любое расхождение больше полкопейки — провал теста.
/// </summary>
public class TotalsParityTests
{
    [Fact]
    public void Totals_MatchPython_OnAllContracts()
    {
        var basePath = FindFile("db/base.db");
        var jsonPath = FindFile("db/totals_python.json");
        if (basePath is null || jsonPath is null) return;   // эталон не снят — сверять нечего

        using var conn = Database.Open(basePath, readOnly: true);
        var mine = Totals.ReadAll(conn).ToDictionary(x => x.Id);

        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var rows = doc.RootElement.GetProperty("contracts");

        Assert.Equal(rows.GetArrayLength(), mine.Count);
        Assert.True(mine.Count >= 30, $"ожидали не меньше 30 документов, а нашли {mine.Count}");

        var problems = new List<string>();
        var worst = 0d;

        foreach (var row in rows.EnumerateArray())
        {
            var id = row.GetProperty("id").GetInt32();
            Assert.True(mine.ContainsKey(id), $"договор {id} есть в Python, но не найден в C#");
            var m = mine[id];

            void Compare(string title, decimal got, string pythonField)
            {
                var py = row.GetProperty(pythonField).GetDouble();
                var diff = Math.Abs((double)got - py);
                worst = Math.Max(worst, diff);
                if (diff > 0.006)
                    problems.Add($"договор {id}: {title} — C# {got:0.00}, Python {py:0.00}");
            }

            Compare("материалы", m.Materials, "materials");
            Compare("СМР", m.Smr, "smr");
            Compare("без НДС", m.Net, "net");
            Compare("итого", m.Total, "total");
            Compare("НДС", m.Vat, "vat");

            var pyPositions = row.GetProperty("positions").GetInt32();
            if (pyPositions != m.Positions)
                problems.Add($"договор {id}: позиций {m.Positions}, в Python {pyPositions}");
        }

        Assert.True(problems.Count == 0,
            $"расхождений: {problems.Count}, худшее {worst:0.0000}\n" + string.Join("\n", problems.Take(20)));
    }

    [Fact]
    public void RealBase_OpensReadOnly_AndHasMoney()
    {
        var basePath = FindFile("db/base.db");
        if (basePath is null) return;

        using var conn = Database.Open(basePath, readOnly: true);
        var all = Totals.ReadAll(conn);
        Assert.NotEmpty(all);
        Assert.Contains(all, t => t.Materials > 0m);
    }

    /// <summary>Ищет db/base.db вверх от папки тестов (bin/Debug/net10.0 → … → csharp).</summary>
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
