namespace GmcSmeta.Core.Domain;

/// <summary>Display-only margin semantics from cost_summary_view._money_item.
/// Do not use this to calculate stored values, totals or export amounts.</summary>
public static class SummaryMoneyDisplay
{
    public readonly record struct MarginCell(string Text, bool Negative);

    public static MarginCell Margin(decimal cost, decimal sell)
    {
        // Missing purchase with a nonzero retail price must not imply 100% profit.
        if (cost == 0m && sell != 0m) return new("—", false);
        var margin = Money.Round2(sell - cost);
        return new(Money.Format(margin), margin < 0m);
    }
}
