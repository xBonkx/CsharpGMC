namespace GmcSmeta.Core.Domain;

/// <summary>
/// Pure validation of KS-2 quantity against the *unclosed* contract balance.
/// An existing over-closed row may be reduced but must not be increased.
/// No SQL or SQLite dependencies.
/// </summary>
public static class ActQuantityPolicy
{
    public static decimal Remaining(decimal quantityByKp, decimal closedInOtherActs) =>
        Math.Max(0m, quantityByKp - closedInOtherActs);

    public static bool ExceedsKp(decimal proposed, decimal quantityByKp,
        decimal closedInOtherActs, decimal existingInThisAct, decimal tolerance = 0.000001m) =>
        proposed > Remaining(quantityByKp, closedInOtherActs) + tolerance &&
        proposed > existingInThisAct + tolerance;

    /// <summary>
    /// Fills ONLY top-level estimate positions with a real remaining balance.
    /// Outside-KP positions require explicit manual entry, never implicit one-unit closure.
    /// </summary>
    public static IReadOnlyList<(long PositionId, decimal Quantity)> PlanFillRemaining(
        IEnumerable<(long PositionId, decimal QuantityByKp, decimal ClosedInOtherActs, bool OutsideKp)> rows) =>
        rows.Where(r => !r.OutsideKp && r.PositionId > 0)
            .Select(r => (r.PositionId, Quantity: Remaining(r.QuantityByKp, r.ClosedInOtherActs)))
            .Where(r => r.Quantity > 0m)
            .ToList();
}
