using System.Collections.Concurrent;
using GmcSmeta.Core.Domain;
using static GmcSmeta.Core.Db.SqlServerKsPrintData;

namespace GmcSmeta.Core.Db;

/// <summary>Lazy, package-local indexes for read-only export snapshots.
/// Cache section projections by the full act record, not only its ID:
/// changed allocation or rounding settings must get a fresh calculation.</summary>
internal sealed class KsPrintPackageIndex
{
    private readonly Package _package;
    private readonly Lazy<IReadOnlyDictionary<long, IReadOnlyList<Line>>> _lines;
    private readonly Lazy<ILookup<long, Section>> _sections;
    private readonly ConcurrentDictionary<SqlServerActs.ActInfo, IReadOnlyList<PrintSection>> _prepared = new();

    public KsPrintPackageIndex(Package package)
    {
        _package = package;
        _lines = new(() => package.Lines.GroupBy(l => l.ActId).ToDictionary(
            g => g.Key, g => (IReadOnlyList<Line>)Array.AsReadOnly(g.ToArray())));
        _sections = new(() => package.Sections.ToLookup(s => s.ContractId));
    }

    public IReadOnlyList<Line> LinesFor(long actId) =>
        _lines.Value.GetValueOrDefault(actId) ?? Array.Empty<Line>();

    public IReadOnlyList<PrintSection> SectionsFor(SqlServerActs.ActInfo act) =>
        _prepared.GetOrAdd(act, BuildSections);

    private IReadOnlyList<PrintSection> BuildSections(SqlServerActs.ActInfo act)
    {
        var rows = LinesFor(act.Id);
        var sections = _sections.Value[act.ContractId].ToArray();
        if (sections.Length == 0)
            return Array.AsReadOnly(rows.GroupBy(l => l.Section)
                .Select(g => new PrintSection(g.Key, Array.AsReadOnly(g.ToArray()), 0m, 0m)).ToArray());
        var ids = sections.Select(s => s.Id).ToHashSet();
        if (rows.Any(l => !ids.Contains(l.SystemId)))
            throw new InvalidOperationException("Строка КС ссылается на неизвестный раздел.");
        var perPosition = _package.Scope.Documents.Single(d => d.Id == act.ContractId).SmrMode == "pos";
        var amounts = KsActAllocation.Resolve(rows.Select(l => new KsActAllocation.Line(
            l.SystemId, l.Quantity, l.Price, l.WorkPrice)).ToList(),
            ids, act.Smr, act.Transport, perPosition, act.SmrBySystem, act.SectionAmountsJson, act.RoundRows);
        var byId = rows.ToLookup(l => l.SystemId);
        return Array.AsReadOnly(sections.Select(s => new PrintSection(s.Name,
            Array.AsReadOnly(byId[s.Id].ToArray()), perPosition ? 0m : amounts.Smr.GetValueOrDefault(s.Id),
            amounts.Transport.GetValueOrDefault(s.Id)))
            .Where(s => s.Lines.Count > 0 || s.Smr != 0m || s.Transport != 0m).ToArray());
    }
}
