namespace GmcSmeta.Core.Domain;

/// <summary>Arithmetic shared by stage-money refresh; intentionally retains the
/// two distinct rounding rules used by the existing parent and stage writers.</summary>
public static class StageMoneyAllocation
{
    public static decimal RoundParent(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.ToEven);

    public static decimal ParentPercent(decimal materials, decimal percent) =>
        RoundParent(materials * percent / 100m);

    public static decimal Share(decimal fullMaterials, decimal stageMaterials) =>
        fullMaterials > 0.000001m ? stageMaterials / fullMaterials : 0m;

    public static decimal Proportional(decimal amount, decimal share) =>
        Money.Round2(amount * share);

    public static decimal MaterialLine(decimal qty, decimal price, bool roundRows)
    {
        var rowAmount = qty * price;
        return roundRows ? Money.Round2(rowAmount) : rowAmount;
    }
}
