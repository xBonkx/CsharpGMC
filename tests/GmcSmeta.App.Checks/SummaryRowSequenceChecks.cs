using GmcSmeta.App;
using Row = GmcSmeta.App.EstimateView.SummaryRow;

internal static class SummaryRowSequenceChecks
{
    internal static void Run()
    {
        var original = Enumerable.Range(1, 25000).Select(i => new Row
        {
            PositionId = i, MemberIds = new long[] { i }, Name = "Позиция",
            QtyText = "1", CostUnitRaw = "15", TotalCostText = "15,00", IsOdd = i % 2 == 0
        }).ToArray();
        var rebuilt = original.Select(r => r with { MemberIds = r.MemberIds.ToArray() }).ToArray();
        Require(SummaryRowSequence.Same(original, rebuilt), "Identical presentation must be reusable.");
        var unchanged = rebuilt[100];
        foreach (var changed in new[]
        {
            unchanged with { MemberIds = new long[] { 777 } },
            unchanged with { TotalCostText = "20,00" },
            unchanged with { CostUnitRaw = "15,001" },
            unchanged with { CostMark = true },
            unchanged with { IsOdd = !unchanged.IsOdd },
            unchanged with { CostUnitTip = "Новая подсказка" },
            unchanged with { CollapseMark = "▸" }
        })
        {
            rebuilt[100] = changed;
            Require(!SummaryRowSequence.Same(original, rebuilt), "A changed field or write target must replace rows.");
        }
        rebuilt[100] = unchanged;
        (rebuilt[0], rebuilt[1]) = (rebuilt[1], rebuilt[0]);
        Require(!SummaryRowSequence.Same(original, rebuilt), "Changed order must replace rows.");
        Require(!SummaryRowSequence.Same(original, rebuilt.Take(100).ToArray()), "Changed count must replace rows.");
        var group = new Row { Kind = "system", SystemId = 1, MemberIds = new long[] { 1, 2 } };
        Require(!SummaryRowSequence.Same(new[] { group }, new[] { group with { MemberIds = new long[] { 2, 1 } } }),
            "Member order must not silently change.");

        var wanted = new HashSet<string> { "pos:1", "pos:2" };
        var targets = SummaryRowSequence.RestoreCandidates(original, wanted, "pos:25000");
        Require(targets.Count == 3 && ReferenceEquals(targets["pos:25000"], original[^1]), "Selection/anchor targets lost.");
        var ambiguous = original.Append(original[0]).Append(original[0]);
        targets = SummaryRowSequence.RestoreCandidates(ambiguous, wanted, "pos:25000");
        Require(targets.Count == 2 && !targets.ContainsKey("pos:1"), "Ambiguous target must not restore.");
        Require(SummaryRowSequence.RestoreCandidates(ThrowOnEnumeration(), new HashSet<string>()).Count == 0,
            "No restore keys must skip enumeration.");
        Console.WriteLine("Summary row equivalence and exact restore candidates OK.");
    }

    private static IEnumerable<Row> ThrowOnEnumeration()
    {
        foreach (var _ in new[] { 0 })
            throw new Exception("Unneeded restore scan.");
        yield break;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
