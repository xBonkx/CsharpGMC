using GmcSmeta.Core.Db;
using Group = GmcSmeta.Core.Domain.KsWorkspaceProjection.Ks3Group;

namespace GmcSmeta.Core.Domain;

/// <summary>Search fields prepared once per authoritative overview snapshot.
/// Preserve ordinal-ignore-case matching, original ordering and filtered totals.</summary>
public sealed class KsOverviewSearchIndex
{
    private sealed record Entry(Group Group, string[] Text);
    private readonly IReadOnlyList<Group> _source;
    private readonly Entry[] _entries;

    public KsOverviewSearchIndex(IReadOnlyList<Group> source)
    {
        _source = source;
        _entries = source.Select(group => new Entry(group, group.Acts.Select(a =>
            $"{group.Title} {a.Number} {a.Date:dd.MM.yyyy} {a.SourceName}").ToArray())).ToArray();
    }

    public IReadOnlyList<Group> Filter(string? search, bool? signed)
    {
        var words = (search ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 && signed is null) return _source;
        var result = new List<Group>();
        foreach (var entry in _entries)
        {
            var group = entry.Group;
            List<SqlServerActs.ActInfo>? matches = null;
            decimal total = 0m;
            bool allSigned = true;
            for (var i = 0; i < group.Acts.Count; i++)
            {
                var act = group.Acts[i];
                if (signed is not null && act.Signed != signed) continue;
                var match = true;
                foreach (var word in words)
                    if (!entry.Text[i].Contains(word, StringComparison.OrdinalIgnoreCase))
                    {
                        match = false;
                        break;
                    }
                if (!match) continue;
                (matches ??= new()).Add(act);
                total += act.Total;
                allSigned &= act.Signed;
            }
            if (matches is null) continue;
            result.Add(matches.Count == group.Acts.Count ? group :
                new Group(group.Key, matches.Count, total, allSigned, matches));
        }
        return result;
    }
}
