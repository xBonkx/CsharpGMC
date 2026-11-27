namespace GmcSmeta.Core.Domain;

/// <summary>Display-only margin semantics from cost_summary_view._money_item.
/// Do not use this to calculate stored values, totals or export amounts.</summary>
public static class SummaryMoneyDisplay
{
    public readonly record struct MarginCell(string Text, bool Negative);
    public readonly record struct UnitCell(string Text, string Raw, bool Marked);

    /// <summary>Format the unit-price display, editor value and missing marker
    /// together. Zero retail is blank; zero cost/work remains a valid value.
    /// A forced missing-cost marker never removes the underlying editor value.</summary>
    public static UnitCell Unit(decimal? value, bool differs, bool isPrice,
        bool markMissing = false, bool forceMissing = false)
    {
        var visible = !differs && value is decimal v && (v != 0m || !isPrice);
        var marked = forceMissing || (markMissing && !visible);
        return new UnitCell(marked ? "—" : visible ? Money.Format(value!.Value) : "",
            visible ? NumberText.QtyText(value!.Value) : "", marked);
    }

    public static MarginCell Margin(decimal cost, decimal sell)
    {
        // Missing purchase with a nonzero retail price must not imply 100% profit.
        if (cost == 0m && sell != 0m) return new("—", false);
        var margin = Money.Round2(sell - cost);
        return new(Money.Format(margin), margin < 0m);
    }
}
