namespace GmcSmeta.Core.Domain;

public sealed record KsBatchPosition(long PositionId, decimal QtyByKp, decimal ClosedByOthers,
    decimal LineQty, decimal Price, bool OutOfKp, bool Linked);

/// <summary>Pure selection commands; persistence and signed-state checks remain in the editor.</summary>
public static class KsBatchQuantities
{
    public static List<(long PositionId, decimal Qty)> FillRemaining(IReadOnlyList<KsBatchPosition> states)
    {
        var byId = states.ToDictionary(s => s.PositionId);
        return states
            .Where(s => !s.OutOfKp && !s.Linked)
            .Select(s => (s.PositionId, Qty: ActQuantityPolicy.Remaining(s.QtyByKp, s.ClosedByOthers)))
            .Where(s => Math.Abs(s.Qty - byId[s.PositionId].LineQty) > 0.000001m)
            .Select(s => (s.PositionId, s.Qty))
            .ToList();
    }

    public static List<(long PositionId, decimal Qty)> Clear(IReadOnlyList<KsBatchPosition> states) =>
        states.Where(s => s.LineQty != 0m).Select(s => (s.PositionId, Qty: 0m)).ToList();

    public static List<(long PositionId, decimal Qty)> FillIssued(IReadOnlyList<KsBatchPosition> states,
        IReadOnlyDictionary<long, decimal> issued)
    {
        var edits = new List<(long PositionId, decimal Qty)>();
        foreach (var s in states)
        {
            if (s.OutOfKp || s.Linked) continue;
            var rem = ActQuantityPolicy.Remaining(s.QtyByKp, s.ClosedByOthers);
            var closedOther = s.QtyByKp - rem;
            var q = Math.Min(rem, Math.Max(0m, issued.GetValueOrDefault(s.PositionId) - closedOther));
            if (Math.Abs(q - s.LineQty) > 0.000001m)
                edits.Add((s.PositionId, q));
        }
        return edits;
    }
}
