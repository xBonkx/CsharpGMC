using System.Globalization;

namespace GmcSmeta.Core.Domain;

/// <summary>Presentation only: Python cost_totals_view._show and _margin_cells.
/// Null and zero have different meanings. Never use these strings as calculation inputs.</summary>
public static class TotalsDisplayPolicy
{
    public static string MoneyText(decimal? value) => value is decimal amount && amount != 0m ? Money.Format(amount) : "";
    public static string SellText(decimal? value) => value is null ? "—" : MoneyText(value);
    public static string PercentText(decimal cost, decimal sell) =>
        cost != 0m ? ((sell - cost) / cost * 100m).ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + "%" : "";

    public static (string MarginText, string PctText, bool Neg) Margin(decimal? cost, decimal? sell)
    {
        if (sell is null)
            return cost is decimal c && c != 0m ? ("−" + Money.Format(c), "", true) : ("", "", false);
        if (cost is null or 0m)
            return (sell.Value != 0m ? "—" : "", "", false);
        var margin = sell.Value - cost.Value;
        return (Money.Format(margin), PercentText(cost.Value, sell.Value), margin < 0m);
    }
}
