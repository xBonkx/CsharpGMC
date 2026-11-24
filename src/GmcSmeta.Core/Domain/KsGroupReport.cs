using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// One KS-3 for all KS-2 with the same group key, across annexes of one legal
/// agreement. Every amount is calculated from FULL Act.total, not lines only.
/// </summary>
public sealed record KsPreviousGroup(string Key, DateTime Date,
    decimal Net, decimal Vat, decimal Gross);

public sealed record KsGroupReport(
    string Key, string AgreementNumber, DateTime Date,
    DateTime? PeriodFrom, DateTime? PeriodTo,
    decimal PeriodNet, decimal PeriodVat, decimal PeriodGross,
    decimal YearNet, decimal SinceStartNet,
    IReadOnlyList<SqlServerActs.ActInfo> Acts,
    IReadOnlyList<KsPreviousGroup> Previous, bool GrossCumulative = false)
{
    public static KsGroupReport Calculate(SqlServerKsContractScope.Scope scope,
        string groupKey)
    {
        var groups = KsWorkspaceProjection.Build(scope.Acts);
        var index = groups.ToList().FindIndex(g => g.Key == groupKey);
        if (index < 0 || groups[index].Acts.Count == 0)
            throw new InvalidOperationException("КС-3 № " + groupKey + " не найдена.");

        var current = groups[index];
        var acts = current.Acts.OrderBy(a => a.Date).ThenBy(a => a.Id).ToList();
        var end = acts.Max(a => a.Date);
        var previous = groups.Take(index).Select(g =>
            new KsPreviousGroup(g.Key, g.Acts.Max(a => a.Date),
                g.Acts.Sum(a => a.Ks3Net), g.Acts.Sum(a => a.Vat),
                g.Acts.Sum(a => a.Total))).ToList();
        var net = acts.Sum(a => a.Ks3Net);
        var cumulative = scope.Opening?.Cumulative(end,
            groups.Take(index).SelectMany(g => g.Acts).Select(a => (a.Date, a.Total)),
            acts.Select(a => (a.Date, a.Total)));
        return new KsGroupReport(groupKey, scope.AgreementNumber, end,
            acts.Where(a => a.PeriodFrom is not null).Select(a => a.PeriodFrom)
                .DefaultIfEmpty(null).Min(),
            acts.Where(a => a.PeriodTo is not null).Select(a => a.PeriodTo)
                .DefaultIfEmpty(null).Max(),
            net, acts.Sum(a => a.Vat), acts.Sum(a => a.Total),
            cumulative?.YearGross ?? (net + previous.Where(p => p.Date.Year == end.Year).Sum(p => p.Net)),
            cumulative?.SinceStartGross ?? (net + previous.Sum(p => p.Net)),
            acts, previous, cumulative is not null);
    }

    /// <summary>Single KS-2/KS-3, not the whole numbered group.
    /// Normal cumulative amounts follow the selected document's acts; an imported
    /// gross opening follows the legal agreement's previous groups, as in Python.</summary>
    public static KsGroupReport CalculateSingle(SqlServerKsContractScope.Scope scope, long actId)
    {
        var act = scope.Acts.SingleOrDefault(a => a.Id == actId &&
            a.ContractId == scope.SelectedDocumentId)
            ?? throw new InvalidOperationException("Акт не принадлежит выбранному документу.");
        var local = scope.Acts.Where(a => a.ContractId == act.ContractId)
            .OrderBy(a => a.Date).ThenBy(a => a.Id).ToList();
        var index = local.FindIndex(a => a.Id == actId);
        var previous = local.Take(index).Select(a =>
            new KsPreviousGroup(a.Number, a.Date, a.Ks3Net, a.Vat, a.Total)).ToList();
        var report = SqlServerActs.CalculateKs3ReportFromActs(local, actId);
        var groups = KsWorkspaceProjection.Build(scope.Acts);
        var groupIndex = groups.ToList().FindIndex(g => g.Key == KsWorkspaceProjection.GroupKey(act));
        if (groupIndex < 0) throw new InvalidOperationException("Группа КС-3 не найдена.");
        var cumulative = scope.Opening?.Cumulative(act.Date,
            groups.Take(groupIndex).SelectMany(g => g.Acts).Select(a => (a.Date, a.Total)),
            new[] { (act.Date, act.Total) });
        return new KsGroupReport(act.Number, scope.AgreementNumber, act.Date,
            act.PeriodFrom, act.PeriodTo, report.Period, report.Vat, report.Total,
            cumulative?.YearGross ?? report.Year, cumulative?.SinceStartGross ?? report.Start,
            new[] { act }, previous, cumulative is not null);
    }
}
