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
        string groupKey, bool selectedDocumentOnly = false) =>
        new Prepared(scope).Group(groupKey, selectedDocumentOnly);

    public static KsGroupReport CalculateSingle(SqlServerKsContractScope.Scope scope, long actId) =>
        new Prepared(scope).Single(actId);

    /// <summary>One read-only report context per accepted SQL snapshot.
    /// Batch export reuses ordering, group membership and previous-group totals.
    /// No financial result is shared across different snapshots or VAT rates.</summary>
    public sealed class Prepared
    {
        private readonly SqlServerKsContractScope.Scope _scope;
        private readonly IReadOnlyList<KsWorkspaceProjection.Ks3Group> _groups;
        private readonly Dictionary<string, int> _groupIndexes;
        private readonly KsPreviousGroup?[] _previousGroups;
        private readonly Lazy<LocalActs> _local;

        private sealed record LocalActs(SqlServerActs.ActInfo[] Acts,
            (long Id, string Number, DateTime Date, decimal Total)[] Amounts,
            IReadOnlyDictionary<long, int> Indexes);

        public Prepared(SqlServerKsContractScope.Scope scope)
        {
            _scope = scope;
            _groups = KsWorkspaceProjection.Build(scope.Acts);
            _groupIndexes = _groups.Select((g, i) => (g.Key, Index: i))
                .ToDictionary(g => g.Key, g => g.Index, StringComparer.Ordinal);
            _previousGroups = new KsPreviousGroup?[_groups.Count];
            _local = new Lazy<LocalActs>(() =>
            {
                var acts = scope.Acts.Where(a => a.ContractId == scope.SelectedDocumentId)
                    .OrderBy(a => a.Date).ThenBy(a => a.Id).ToArray();
                return new LocalActs(acts,
                    acts.Select(a => (a.Id, a.Number, a.Date, a.Total)).ToArray(),
                    acts.Select((a, i) => (a.Id, Index: i)).ToDictionary(a => a.Id, a => a.Index));
            });
        }

        private KsPreviousGroup PreviousGroup(int index)
        {
            if (_previousGroups[index] is { } cached) return cached;
            var group = _groups[index];
            return _previousGroups[index] = new KsPreviousGroup(group.Key,
                group.Acts[^1].Date, group.Acts.Sum(a => a.Ks3Net),
                group.Acts.Sum(a => a.Vat), group.Acts.Sum(a => a.Total));
        }

        public KsGroupReport Group(string groupKey, bool selectedDocumentOnly = false)
        {
            if (!_groupIndexes.TryGetValue(groupKey, out var index) || _groups[index].Acts.Count == 0)
                throw new InvalidOperationException("КС-3 № " + groupKey + " не найдена.");

            var current = _groups[index];
            IReadOnlyList<SqlServerActs.ActInfo> acts = selectedDocumentOnly
                ? current.Acts.Where(a => a.ContractId == _scope.SelectedDocumentId).ToArray()
                : current.Acts;
            if (acts.Count == 0)
                throw new InvalidOperationException("В выбранном документе нет актов этой группы.");
            var end = acts[^1].Date;
            var previous = Enumerable.Range(0, index).Select(PreviousGroup).ToArray();
            var net = acts.Sum(a => a.Ks3Net);
            var cumulative = _scope.Opening?.Cumulative(end,
                _groups.Take(index).SelectMany(g => g.Acts).Select(a => (a.Date, a.Total)),
                acts.Select(a => (a.Date, a.Total)));
            return new KsGroupReport(groupKey, _scope.AgreementNumber, end,
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
        /// Retain the current act VAT rate for every cumulative calculation;
        /// gross openings continue to follow legal-agreement group history.</summary>
        public KsGroupReport Single(long actId)
        {
            var local = _local.Value;
            if (!local.Indexes.TryGetValue(actId, out var index))
                throw new InvalidOperationException("Акт не принадлежит выбранному документу.");
            var act = local.Acts[index];
            var report = SqlServerActs.CalculateKs3Report(local.Amounts, actId, act.VatRate);
            var previous = local.Acts.Take(index).Zip(report.Previous,
                (a, amount) => new KsPreviousGroup(a.Number, a.Date, amount.Net,
                    a.Total - amount.Net, a.Total)).ToList();
            if (!_groupIndexes.TryGetValue(KsWorkspaceProjection.GroupKey(act), out var groupIndex))
                throw new InvalidOperationException("Группа КС-3 не найдена.");
            var cumulative = _scope.Opening?.Cumulative(act.Date,
                _groups.Take(groupIndex).SelectMany(g => g.Acts).Select(a => (a.Date, a.Total)),
                new[] { (act.Date, act.Total) });
            return new KsGroupReport(act.Number, _scope.AgreementNumber, act.Date,
                act.PeriodFrom, act.PeriodTo, report.Period, report.Vat, report.Total,
                cumulative?.YearGross ?? report.Year, cumulative?.SinceStartGross ?? report.Start,
                new[] { act }, previous, cumulative is not null);
        }
    }
}
