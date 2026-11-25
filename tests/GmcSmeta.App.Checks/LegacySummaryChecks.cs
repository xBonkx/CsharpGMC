using GmcSmeta.App;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using System.Globalization;

internal static class LegacySummaryChecks
{
    internal static void Run()
    {
        var work = new CostSummaryRow(1, 1, "Монтаж П1", "шт", 2m, "",
            6m, 0m, 10m, 20m, false, false, false, false,
            3m, 40m, 5m, 10m, false, 3m, false, true);
        var material = work with { Id = 2, Name = "Воздуховод", Work = false, MSell = 80m };
        var data = new CostSummaryData([(1L, "П1", false)], [work, material],
            [("Прочие затраты", 90m, 15m, "works"),
             ("Куплено вне КП", 50m, 0m, "mat"),
             ("Транспорт", 12m, 25m, "trans")], [], 0m);
        var rows = CostSummaryPane.BuildSummaryPreview(data);
        Require(rows.Count == 5, "Unexpected legacy row count.");
        var w = rows[0];
        Require(w.UPrice == "" && w.SummaryUnitPrice == 40m.ToString("0.######", CultureInfo.CurrentCulture),
            "Work unit price must be visible only in Summary.");
        Require(w.MSell == "—" && w.WSell == Money.Format(20m), "Work retail allocation changed.");
        w.SummaryUnitPrice = "999";
        Require(w.SummaryUnitPrice == 40m.ToString("0.######", CultureInfo.CurrentCulture),
            "Manual work unit price must not be changed.");
        var m = rows[1];
        m.SummaryUnitPrice = "41";
        Require(m.UPrice == "41", "Normal material editing must retain the shared unit price.");
        var extra = rows[2];
        Require(extra.MCost == "—" && extra.MSell == "—" && extra.WCost == Money.Format(90m) &&
            extra.WSell == Money.Format(15m) && extra.WorkMargin == Money.Format(-75m),
            "Extra work expense belongs in SMR, not materials.");
        Require(rows[3].MCost == Money.Format(50m) && rows[3].WCost == "—", "Out-of-KP expense belongs in materials.");
        Require(rows[4].IsTransport && rows[4].Id == -1 && rows[4].MCost == Money.Format(12m) &&
            rows[4].MSell == Money.Format(25m) && rows[4].WCost == "—", "Transport route changed.");
        decimal Parse(string s) => s is "" or "—" ? 0m : decimal.Parse(s, CultureInfo.GetCultureInfo("ru-RU"));
        Require(rows.Sum(r => Parse(r.TotalCost)) == data.TotalCost &&
            rows.Sum(r => Parse(r.TotalSell)) == data.TotalSell, "Legacy preview lost or duplicated an amount.");
        Console.WriteLine("Legacy summary projection checks passed (no SQL).");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
