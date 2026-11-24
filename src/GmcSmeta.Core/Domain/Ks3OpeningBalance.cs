using System.Globalization;
using System.Text.Json;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Import marker meta['ks3_opening:{object_id}:{contract_number}'].
/// Python stores a gross (VAT included) opening amount, NOT a net balance.
/// Keep this type explicit to avoid mixing it with ordinary KS-3 net totals.
/// </summary>
public sealed record Ks3OpeningBalance(DateTime Through, decimal Gross)
{
    public static Ks3OpeningBalance Parse(string json)
    {
        using var doc=JsonDocument.Parse(json);
        var root=doc.RootElement;
        if (!root.TryGetProperty("through",out var dateProperty) ||
            !DateTime.TryParseExact(dateProperty.GetString(),"yyyy-MM-dd",
                CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))
            throw new FormatException("Неверная дата начального остатка КС-3.");
        if (!root.TryGetProperty("gross_cents",out var centsProperty) ||
            !centsProperty.TryGetInt64(out var cents))
            throw new FormatException("Неверная сумма начального остатка КС-3.");
        return new Ks3OpeningBalance(date.Date,cents/100m);
    }

    /// <summary>
    /// Same cutoff/group rules as Python ks3_gross_cumulative.
    /// Previous groups before cutoff are already included in the imported opening.
    /// Current group's acts are counted regardless of the individual date, as in Python.
    /// </summary>
    public (decimal SinceStartGross,decimal YearGross)? Cumulative(
        DateTime end,
        IEnumerable<(DateTime Date,decimal Gross)> previousGroups,
        IEnumerable<(DateTime Date,decimal Gross)> currentGroup)
    {
        end=end.Date;
        if (end<=Through.Date) return null;
        var included=previousGroups
            .Where(a=>a.Date.Date>Through.Date && a.Date.Date<=end)
            .Concat(currentGroup)
            .ToList();
        var start=Gross+included.Sum(a=>a.Gross);
        var year=included.Where(a=>a.Date.Year==end.Year).Sum(a=>a.Gross);
        if (Through.Year==end.Year) year+=Gross;
        return (decimal.Round(start,2,MidpointRounding.ToEven),
                decimal.Round(year,2,MidpointRounding.ToEven));
    }
}
