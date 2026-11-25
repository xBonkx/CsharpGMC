namespace GmcSmeta.Core.Domain;

/// <summary>Decides whether the two summary toolbar groups fit on one row.</summary>
public static class SummaryToolbarLayout
{
    public const double Gap = 6;
    public static bool Fits(double available, double actions, double filters) =>
        double.IsPositiveInfinity(available) || actions + Gap + filters <= available;
}
