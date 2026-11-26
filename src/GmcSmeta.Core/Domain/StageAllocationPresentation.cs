using System.Globalization;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Presentation-only rules from Python ui/stage_mode.py for inline stage
/// columns. No provider, SQL mutation, or change to the full estimate.
/// </summary>
public static class StageAllocationPresentation
{
    public const decimal Epsilon = 0.000001m;
    public const string All = "all";
    public const string Undistributed = "undistributed";
    public const string Assigned = "assigned";
    public const string Overrun = "overrun";

    public static decimal Remaining(decimal full, IEnumerable<decimal> stages) =>
        full - stages.Sum();

    public static bool Matches(decimal remaining, IReadOnlyList<decimal> stages, string filter) =>
        filter switch
        {
            All => true,
            Undistributed => Math.Abs(remaining) > Epsilon,
            Assigned => stages.Any(x => x > Epsilon),
            Overrun => remaining < -Epsilon,
            _ => throw new ArgumentOutOfRangeException(nameof(filter))
        };

    public static string Qty(decimal value) =>
        Math.Abs(value) <= Epsilon ? "" : value.ToString("0.######", CultureInfo.GetCultureInfo("ru-RU"));

    public static string RemainingText(decimal value) =>
        Math.Abs(value) <= Epsilon ? "✓" : Qty(value);

    public static bool TryParseNonnegative(string? input, out decimal qty)
    {
        qty = 0m;
        if (string.IsNullOrWhiteSpace(input)) return true;
        var parsed = NumberText.Parse(input);
        if (parsed is null || parsed < 0m || parsed > NumberText.MaxNumber)
            return false;
        qty = decimal.Round(parsed.Value, 6, MidpointRounding.ToEven);
        return true;
    }

    public static bool NeedsOverrunConfirmation(decimal full, IReadOnlyList<decimal> current,
        int stageIndex, decimal requested)
    {
        if (stageIndex < 0 || stageIndex >= current.Count)
            throw new ArgumentOutOfRangeException(nameof(stageIndex));
        return requested - (full - current.Where((_, index) => index != stageIndex).Sum())
            > Epsilon;
    }

    /// <summary>Format one row once; final cell is the automatic remainder.</summary>
    public static IReadOnlyList<string> CellLabels(IReadOnlyList<decimal> stages, decimal remaining)
    {
        var result = new string[stages.Count + 1];
        for (var i = 0; i < stages.Count; i++) result[i] = Qty(stages[i]);
        result[^1] = RemainingText(remaining);
        return Array.AsReadOnly(result);
    }

    public sealed record Metrics(IReadOnlyList<int> AssignedCounts, int UnassignedCount,
        int OverrunCount, decimal RemainingAmount);

    /// <summary>All card and remainder statistics in one pass over the snapshot.</summary>
    public static Metrics Summarize(int stageCount,
        IEnumerable<(decimal Price, decimal Rest, IReadOnlyList<decimal> PerStage)> rows)
    {
        var assigned = new int[stageCount];
        int unassigned = 0, overrun = 0;
        decimal amount = 0;
        foreach (var row in rows)
        {
            for (var i = 0; i < stageCount; i++)
                if (row.PerStage[i] > Epsilon) assigned[i]++;
            if (Math.Abs(row.Rest) > Epsilon) unassigned++;
            if (row.Rest < -Epsilon) overrun++;
            amount += Math.Max(row.Rest, 0m) * row.Price;
        }
        return new Metrics(Array.AsReadOnly(assigned), unassigned, overrun, amount);
    }

    public static decimal AmountOfRemaining(
        IEnumerable<(decimal Price, decimal Remaining)> rows) =>
        rows.Sum(row => Math.Max(row.Remaining, 0m) * row.Price);
}
