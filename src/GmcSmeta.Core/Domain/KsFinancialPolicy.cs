using System.Text.Json;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Act financial rules (Python Act.materials/net/total/vat). Both the contract
/// KS list and cumulative KS-3 must use the same VAT-inclusive gross amount.
/// No database access: this is also used by regression tests.
/// </summary>
public static class KsFinancialPolicy
{
    public sealed record Amounts(decimal Materials, decimal Smr, decimal Transport,
        decimal Pnr, decimal Net, decimal Vat, decimal Gross, decimal Ks3Net);

    public static bool HasRule(string? json, string rule)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(rule, out var enabled) &&
                   enabled.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static decimal Materials(IEnumerable<(decimal Quantity, decimal Price)> lines,
        bool rowsExact, bool roundEachRow)
    {
        decimal total = 0m;
        foreach (var (quantity, price) in lines)
        {
            var product = quantity * price;
            total += rowsExact ? product :
                decimal.Round(product, 2, roundEachRow
                    ? MidpointRounding.AwayFromZero : MidpointRounding.ToEven);
        }
        return decimal.Round(total, 2, rowsExact
            ? MidpointRounding.AwayFromZero : MidpointRounding.ToEven);
    }

    public static Amounts Calculate(decimal materials, decimal smr,
        decimal transport, decimal pnr, decimal vatRate, bool vatOnTop,
        long grossAdjustmentCents = 0, decimal discountPercent = 0m, long manualTotalCents = -1)
    {
        if (vatRate < 0m || vatRate > 100m)
            throw new ArgumentOutOfRangeException(nameof(vatRate));
        var net = decimal.Round(materials + smr + transport + pnr, 2,
            MidpointRounding.ToEven);
        if (!vatOnTop)
        {
            var gross = DiscountPolicy.Payable(net, discountPercent, manualTotalCents);
            var vatIncluded = decimal.Round(gross * vatRate / (100m + vatRate), 2,
                MidpointRounding.ToEven);
            return new Amounts(materials, smr, transport, pnr,
                net, vatIncluded, gross, gross - vatIncluded);
        }

        var vat = decimal.Round(net * vatRate / 100m, 2,
            MidpointRounding.ToEven);
        var adjustment = grossAdjustmentCents / 100m;
        var adjustedGross = DiscountPolicy.Payable(net + vat + adjustment, discountPercent, manualTotalCents);
        return new Amounts(materials, smr, transport, pnr,
            net, vat, adjustedGross, adjustedGross - vat);
    }
}
