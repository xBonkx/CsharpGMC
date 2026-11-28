using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>Read-only batch planning from the captured KS sheet, without WPF or persistence.
/// The editor retains scope, signature, revision, confirmation and transaction ownership.</summary>
public static class KsClosePlan
{
    public sealed record AllPlan(List<(long PositionId, decimal Qty)> Edits, decimal Amount, bool HasLinked);

    public static AllPlan AllRemaining(IEnumerable<SqlServerActs.ActSheetRow> positions)
    {
        var rows = positions.ToList();
        var linked = rows.Where(p => (p.LineFormula ?? "").Contains('@')).Select(p => p.PositionId).ToHashSet();
        var changes = rows.Where(p => !p.OutOfKp && !linked.Contains(p.PositionId))
            .Select(p => (p.PositionId, Qty: ActQuantityPolicy.Remaining(p.QtyByKp, p.ClosedByOthers), p.LineQty, p.Price))
            .Where(p => Math.Abs(p.Qty - p.LineQty) > 0.000001m).ToList();
        return new(changes.Select(p => (p.PositionId, p.Qty)).ToList(),
            changes.Sum(p => p.Qty * p.Price), linked.Count > 0);
    }

    public static (decimal? Smr, decimal Transport, decimal Pnr) RemainingCharges(
        decimal smr, decimal transport, decimal pnr,
        IEnumerable<(decimal Smr, decimal Transport, decimal Pnr)> otherActs, bool positionSmr)
    {
        var others = otherActs.ToList();
        return (positionSmr ? null : Math.Max(0m, smr - others.Sum(a => a.Smr)),
            Math.Max(0m, transport - others.Sum(a => a.Transport)),
            Math.Max(0m, pnr - others.Sum(a => a.Pnr)));
    }

    public sealed class SectionPlan
    {
        private readonly Dictionary<long, List<long>> _positionsByTop;
        private readonly Dictionary<long, SqlServerActs.ActSheetRow> _byId;
        public IReadOnlyList<(long SystemId, string Label, decimal Remainder)> Options { get; }

        internal SectionPlan(List<(long SystemId, string Label, decimal Remainder)> options,
            Dictionary<long, List<long>> positionsByTop, Dictionary<long, SqlServerActs.ActSheetRow> byId)
        {
            Options = options.AsReadOnly();
            _positionsByTop = positionsByTop;
            _byId = byId;
        }

        public List<(long PositionId, decimal Qty)> EditsFor(IEnumerable<long> chosenTops)
        {
            // Preserve the existing close-sections behavior (including linked quantities);
            // SetQuantitiesBatch remains the authoritative formula/persistence policy.
            var ids = chosenTops.SelectMany(t => _positionsByTop.GetValueOrDefault(t) ?? new List<long>()).ToHashSet();
            return ids.Select(id => (id, Qty: ActQuantityPolicy.Remaining(_byId[id].QtyByKp, _byId[id].ClosedByOthers)))
                .Where(s => Math.Abs(s.Qty - _byId[s.id].LineQty) > 0.000001m)
                .Select(s => (s.id, s.Qty)).ToList();
        }
    }

    public static SectionPlan Sections(IReadOnlyList<SqlServerActs.ActSheetSystem> systems,
        IReadOnlyList<SqlServerActs.ActSheetRow> positions)
    {
        var options = new List<(long SystemId, string Label, decimal Remainder)>();
        var positionsByTop = new Dictionary<long, List<long>>();
        var children = systems.Where(s => s.IsSub).ToLookup(s => s.ParentId, s => s.SystemId);
        foreach (var top in systems.Where(s => !s.IsSub))
        {
            var systemIds = children[top.SystemId].Append(top.SystemId).ToHashSet();
            var members = positions.Where(p => systemIds.Contains(p.SystemId) && !p.OutOfKp).ToList();
            positionsByTop[top.SystemId] = members.Select(p => p.PositionId).ToList();
            options.Add((top.SystemId, top.Label,
                members.Sum(p => ActQuantityPolicy.Remaining(p.QtyByKp, p.ClosedByOthers) * p.Price)));
        }
        return new(options, positionsByTop, positions.ToDictionary(p => p.PositionId));
    }
}
