using System.Data.Common;
using System.Globalization;

namespace GmcSmeta.Core.Domain;

/// <summary>Python schema 29–31: gross payable, independent of row prices.
/// The saved percentage is authoritative for each act; do not substitute the
/// contract percentage. A nonnegative stored total in kopecks overrides it.</summary>
public static class DiscountPolicy
{
    public sealed record ActDiscount(bool Override, long ManualTotalCents, decimal Percent);

    public static bool SupportsDiscount(bool vatTop, bool ksDns, string? form, string? objectName)
    {
        var name = (objectName ?? "").Trim();
        return !vatTop && !ksDns && !name.Equals("днепровский", StringComparison.OrdinalIgnoreCase)
            && !(form == "r4" && (name.Equals("р4", StringComparison.OrdinalIgnoreCase)
                || name.Equals("p4", StringComparison.OrdinalIgnoreCase)));
    }

    public static bool InheritsContractRate(bool signed,bool discountOverride,long manualTotalCents) =>
        !signed && !discountOverride && manualTotalCents<0;

    public static ActDiscount Normalize(string mode, decimal? value, decimal contractRate)
    {
        if (mode == "contract") return new(false, -1, contractRate);
        if (mode == "percent" && value is decimal p && p >= 0m && p <= 100m)
            return new(true, -1, p);
        if (mode == "total" && value is decimal t && t >= 0m && t <= 1000000000000m
            && t == Money.Round2(t))
            return new(true, checked((long)(t * 100m)), 0m);
        throw new ArgumentException(mode == "percent" ? "Скидка должна быть от 0 до 100%."
            : "Введите неотрицательную сумму с точностью до копейки.");
    }

    public static decimal ReadPercent(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return 0m;
        // Do not use Money.FromDouble: its six-digit rounding loses saved rate precision.
        var value = Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        return decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture),
            NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    public static decimal Amount(decimal gross, decimal percent, long manualTotalCents = -1) =>
        manualTotalCents >= 0
            ? Money.Round2(gross - manualTotalCents / 100m)
            : Money.Round2(Money.Round2(gross) * percent / 100m);

    public static decimal Payable(decimal gross, decimal percent, long manualTotalCents = -1) =>
        manualTotalCents >= 0 ? manualTotalCents / 100m :
        percent == 0m ? gross : Money.Round2(gross - Amount(gross, percent));
}
