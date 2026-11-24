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

    public static decimal AmountOfRemaining(
        IEnumerable<(decimal Price, decimal Remaining)> rows) =>
        rows.Sum(row => Math.Max(row.Remaining, 0m) * row.Price);
}
