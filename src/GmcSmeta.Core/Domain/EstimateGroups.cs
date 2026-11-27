using System.Globalization;
using System.Text.RegularExpressions;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Read-only grouping used by Python kp_editor._build_view (same / type).
/// Group keys use the same normalization as the catalog. Original rows,
/// their SortKey, price and SQL order are never modified.
/// </summary>
public static class EstimateGroups
{
    private static readonly Regex Word = new(@"^[\p{L}]+", RegexOptions.CultureInvariant);
    public const string Same = "same";
    public const string Type = "type";

    // Python spec_update.item_key: distinguish grouping from the catalog's
    // historical key. Names normalize ё→е, units also normalize m²/m³.
    private static string Normalize(string? text) =>
        Catalog.NormalizeName((text ?? "").Replace('ё', 'е').Replace('Ё', 'Е'));

    public static string Key(string? name, string? unit) =>
        Normalize(name) + "\u001f" +
        Normalize(unit).TrimEnd('.').Replace('²', '2').Replace('³', '3');

    public sealed record Group(
        string Key, string TypeKey, string Name, string Unit,
        IReadOnlyList<EstimateRow> Members,
        IReadOnlyList<EstimateRow> Components,
        decimal Quantity, decimal Amount,
        decimal? CommonPrice, decimal? CommonPurchase, decimal? CommonWork,
        bool MissingPrice, int FirstSortKey)
    {
        public IReadOnlyList<int> PositionIds => Members.Select(x => x.Id).ToArray();
    }

    public sealed record Section(string TypeKey, string Title, decimal Amount,
        int FirstSortKey, IReadOnlyList<Group> Groups);

    public static string TypeOf(string? name)
    {
        var match = Word.Match((name ?? "").Trim());
        return match.Success ? match.Value.ToLowerInvariant().Replace('ё', 'е') : "прочее";
    }

    /// <summary>
    /// Build groups before filtering: a row displayed due to a search matches
    /// all equal positions, as in the Python editor. Out-of-KP rows are excluded.
    /// </summary>
    public static IReadOnlyList<Section> Build(IReadOnlyList<EstimateRow> rows,
        string mode, string sort = EstimateViewProjection.DocumentOrder,
        string? query = null, bool missingOnly = false,
        IReadOnlyDictionary<string, int>? frozen = null)
    {
        if (mode is not (Same or Type))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (!EstimateViewProjection.IsValidSort(sort))
            throw new ArgumentOutOfRangeException(nameof(sort));

        var allowed = string.IsNullOrWhiteSpace(query) ? null
            : EstimateFilter.Apply(rows, query).Select(r => r.SortKey).ToHashSet();
        var components = rows.Where(r => r.IsSub && r.ParentPositionId is not null)
            .GroupBy(r => r.ParentPositionId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.SortKey).ToArray());

        var groups = rows.Where(r => !r.IsSystem && !r.IsSub && !r.OutOfKp)
            .GroupBy(r => Key(r.Name, r.Unit), StringComparer.Ordinal)
            .Select(g =>
            {
                var members = g.OrderBy(r => r.SortKey).ToArray();
                var allChildren = members.SelectMany(r =>
                    components.GetValueOrDefault(r.Id) ?? Array.Empty<EstimateRow>()).ToArray();
                var first = members[0];
                decimal quantity = 0, amount = 0;
                decimal? price = first.Price, purchase = first.PurchasePrice, work = first.WorkPrice;
                bool missing = false;
                foreach (var member in members)
                {
                    quantity += member.Qty;
                    amount += member.Amount;
                    if (price != member.Price) price = null;
                    if (purchase != member.PurchasePrice) purchase = null;
                    if (work != member.WorkPrice) work = null;
                    missing |= member.HasNoPrice;
                }
                return new Group(g.Key, TypeOf(first.Name),
                    first.Name, first.Unit, members, allChildren, quantity, amount,
                    price, purchase, work, missing, first.SortKey);
            })
            .Where(g => !missingOnly || g.MissingPrice)
            .Where(g => allowed is null ||
                g.Members.Any(r => allowed.Contains(r.SortKey)) ||
                g.Components.Any(r => allowed.Contains(r.SortKey)))
            .ToArray();

        var areaKeys = new EstimateAreaSortKeys(EstimateViewProjection.SortField(sort));
        int Compare(Group a, Group b, string key,
            IReadOnlyDictionary<string, int>? ranks)
        {
            if (ranks is not null && ranks.TryGetValue(a.Key, out var ai) &&
                ranks.TryGetValue(b.Key, out var bi) && ai != bi)
                return ai.CompareTo(bi);

            var value = EstimateViewProjection.SortField(key) switch
            {
                EstimateViewProjection.ByName => EstimateViewProjection.NaturalCompare(a.Name, b.Name),
                EstimateViewProjection.ByNumber => EstimateViewProjection.NaturalCompare(a.Members[0].Num, b.Members[0].Num),
                EstimateViewProjection.ByUnit => EstimateViewProjection.NaturalCompare(a.Unit, b.Unit),
                EstimateViewProjection.ByBasis => EstimateViewProjection.NaturalCompare(a.Members[0].Basis, b.Members[0].Basis),
                EstimateViewProjection.ByNote => EstimateViewProjection.NaturalCompare(a.Members[0].Note, b.Members[0].Note),
                EstimateViewProjection.ByAmount => a.Amount.CompareTo(b.Amount),
                EstimateViewProjection.ByPrice => (a.CommonPrice ?? 0).CompareTo(b.CommonPrice ?? 0),
                EstimateViewProjection.ByQuantity => a.Quantity.CompareTo(b.Quantity),
                EstimateViewProjection.ByPurchase => Nullable.Compare(a.CommonPurchase, b.CommonPurchase),
                EstimateViewProjection.ByWork => Nullable.Compare(a.CommonWork, b.CommonWork),
                EstimateViewProjection.ByWorkTotal => Nullable.Compare(a.CommonWork * a.Quantity, b.CommonWork * b.Quantity),
                EstimateViewProjection.ByAreaUnit or EstimateViewProjection.ByAreaTotal =>
                    Nullable.Compare(areaKeys.Get(a.Members[0]), areaKeys.Get(b.Members[0])),
                _ => a.FirstSortKey.CompareTo(b.FirstSortKey)
            };
            return value != 0 ? (EstimateViewProjection.IsDescending(key) ? -Math.Sign(value) : value)
                : a.FirstSortKey.CompareTo(b.FirstSortKey);
        }

        if (mode == Same)
        {
            var sorted = groups.OrderBy(g => g, Comparer<Group>.Create((a, b) =>
                Compare(a, b, sort, frozen))).ToArray();
            return new[] { new Section("", "", sorted.Sum(g => g.Amount),
                sorted.Length > 0 ? sorted.Min(g => g.FirstSortKey) : 0, sorted) };
        }

        // Type = first alphabetic word in title, Python _build_view.
        var sections = groups.GroupBy(g => g.TypeKey, StringComparer.Ordinal)
            .Select(g =>
            {
                var ordered = g.OrderBy(row => row, Comparer<Group>.Create((a, b) =>
                    Compare(a, b, sort, frozen))).ToArray();
                var type = g.Key;
                var title = CultureInfo.GetCultureInfo("ru-RU").TextInfo.ToTitleCase(type);
                return new Section(type, $"{title} — {g.Sum(x => x.Members.Count)} строк",
                    g.Sum(x => x.Amount), g.Min(x => x.FirstSortKey), ordered);
            }).ToArray();
        var field = EstimateViewProjection.SortField(sort);
        var numericKeys = field is EstimateViewProjection.ByQuantity or EstimateViewProjection.ByPrice
            ? sections.ToDictionary(section => section.TypeKey, section => field == EstimateViewProjection.ByQuantity
                ? section.Groups.Sum(x => x.Quantity)
                : section.Groups.Max(x => x.CommonPrice ?? 0), StringComparer.Ordinal)
            : null;
        return sections.OrderBy(group => group, Comparer<Section>.Create((a, b) =>
            {
                var result = field switch
                {
                    EstimateViewProjection.ByName => EstimateViewProjection.NaturalCompare(a.Title, b.Title),
                    EstimateViewProjection.ByAmount => a.Amount.CompareTo(b.Amount),
                    EstimateViewProjection.ByQuantity or EstimateViewProjection.ByPrice =>
                        numericKeys![a.TypeKey].CompareTo(numericKeys[b.TypeKey]),
                    _ => 0
                };
                return result != 0 ? (EstimateViewProjection.IsDescending(sort) ? -Math.Sign(result) : result)
                    : a.FirstSortKey.CompareTo(b.FirstSortKey);
            })).ToArray();
    }

    /// <summary>
    /// Resolve a group selected in the UI against a fresh SQL-side snapshot;
    /// never silently update a subset if the group composition has changed.
    /// </summary>
    public static bool SameMembers(IEnumerable<(long Id, string Name, string Unit,
        bool Top, bool Outside)> rows, string key, IReadOnlyCollection<long> selected)
    {
        if (selected.Count == 0 || selected.Count != selected.Distinct().Count())
            return false;
        var current = rows.Where(r => r.Top && !r.Outside &&
            Key(r.Name, r.Unit) == key).Select(r => r.Id).ToHashSet();
        return current.SetEquals(selected);
    }
}
