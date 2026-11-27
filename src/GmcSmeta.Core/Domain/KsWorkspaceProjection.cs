using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Contract-level KS grouping, analogous to Python ks3_key: Acts 7.1 and 7.2
/// belong to KS-3 group 7. This pure projection performs no database writes.
/// </summary>
public static class KsWorkspaceProjection
{
    public sealed record Ks3Group(string Key, int Count, decimal Total,
        bool Signed, IReadOnlyList<SqlServerActs.ActInfo> Acts)
    {
        public string Title => "КС-3 № " + Key;
        public string Status => Signed ? "Подписана" : "На подготовке";
    }

    // The active act ID is authoritative. The ComboBox/ListBox are only views.
    public static long ResolveActId(IReadOnlyList<SqlServerActs.ActInfo> acts, long requestedId) =>
        acts.Any(a => a.Id == requestedId) ? requestedId :
        acts.Count == 0 ? 0 : acts[^1].Id;

    public sealed record SummaryGroup(SqlServerActs.SummarySystem System, bool SectionHeader,
        IReadOnlyList<SqlServerActs.SummaryRow> Positions);

    /// <summary>Visible systems in the original order; section headers are
    /// shown only when a direct child has visible positions. Preserve signed
    /// negative remainders and the existing quantity tolerance.</summary>
    public static IEnumerable<SummaryGroup> VisibleSummaryGroups(
        SqlServerActs.SummaryData summary, bool onlyRest)
    {
        var bySystem = summary.Positions
            .Where(p => !onlyRest || Math.Abs(p.Left) > 0.000001m)
            .ToLookup(p => p.SystemId);
        var visibleParents = summary.Systems
            .Where(s => !s.SectionHeader && bySystem.Contains(s.System.SystemId))
            .Select(s => s.System.ParentId).ToHashSet();
        foreach (var (system, sectionHeader) in summary.Systems)
        {
            if (sectionHeader)
            {
                if (visibleParents.Contains(system.SystemId))
                    yield return new SummaryGroup(system, true, Array.Empty<SqlServerActs.SummaryRow>());
            }
            else if (bySystem.Contains(system.SystemId))
                yield return new SummaryGroup(system, false, bySystem[system.SystemId].ToArray());
        }
    }

    /// <summary>Excel intentionally includes all source headers (even empty)
    /// and ignores the screen's remaining-only filter. Preserve the previous
    /// per-system export order and membership with a single lookup.</summary>
    public static IEnumerable<SummaryGroup> ExportSummaryGroups(SqlServerActs.SummaryData summary)
    {
        var bySystem = summary.Positions.ToLookup(p => p.SystemId);
        foreach (var (system, sectionHeader) in summary.Systems)
            yield return new SummaryGroup(system, sectionHeader, bySystem[system.SystemId].ToArray());
    }

    // Filtering the overview must never keep an invisible act selected.
    public static SqlServerActs.ActInfo? VisibleAct(
        IEnumerable<SqlServerActs.ActInfo> visible, long selectedId) =>
        visible.FirstOrDefault(a => a.Id == selectedId);

    public static string GroupKey(string? number)
    {
        var v = (number ?? "").Trim().TrimStart('№', ' ').Trim();
        if (v.Length == 0) return "Без номера";
        return v.Split('.', 2)[0].Trim();
    }

    public static string GroupKey(SqlServerActs.ActInfo act)
    {
        var number = (act.Number ?? "").Trim();
        if (act.GrossAdjustmentCents != 0)
        {
            var parts = number.Split('-', 2);
            if (parts.Length == 2 &&
                long.TryParse(parts[0], out _) &&
                long.TryParse(parts[1], out _))
                return parts[0];
        }
        return GroupKey(number);
    }

    public static IReadOnlyList<Ks3Group> Build(IReadOnlyList<SqlServerActs.ActInfo> source)
    {
        return source.GroupBy(GroupKey)
            .Select(g => new Ks3Group(g.Key, g.Count(), g.Sum(a => a.Total),
                g.All(a => a.Signed), g.OrderBy(a => a.Date).ThenBy(a => a.Id).ToList()))
            .OrderBy(g => g.Acts.Min(a => a.Date)).ThenBy(g => g.Key).ToList();
    }

    public static IReadOnlyList<Ks3Group> Filter(
        IReadOnlyList<Ks3Group> source, string? search, bool? signed) =>
        new KsOverviewSearchIndex(source).Filter(search, signed);
}
