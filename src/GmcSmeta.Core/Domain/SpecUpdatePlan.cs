using System.Text.RegularExpressions;

namespace GmcSmeta.Core.Domain;

/// <summary>Input from SQL Server, deliberately detached from ADO.NET and WPF.</summary>
public sealed record SpecExistingSystem(long Id, string Name, int Order, long? ParentId);
public sealed record SpecExistingPosition(long Id, long SystemId, long? ParentId,
    string Name, string Unit, decimal Quantity, decimal Price, decimal? PurchasePrice,
    string Note, bool FromSpecification, string SourceName, bool HasReferences,
    int? SourceRow, bool OutOfKp);

public sealed record SpecIncomingPosition(string Name, string Unit, decimal Quantity,
    string Note, int Order);

public sealed record SpecIncomingSystem(string Name, IReadOnlyList<SpecIncomingPosition> Positions);

public sealed record SpecUpdateChange(
    string Kind, string System, string Name, string Unit,
    decimal? OldQuantity, decimal? NewQuantity, decimal Price,
    long? PositionId, IReadOnlyList<long> MatchingIds,
    string OldSystem, string Warning, int Order)
{
    // Stable identities are validated again against the fresh SQL snapshot before applying.
    public string Key => string.Join("\u001e", Kind, SpecUpdatePlan.Normalize(System),
        SpecUpdatePlan.Normalize(OldSystem), SpecUpdatePlan.ItemKey(Name, Unit));

    public decimal MaterialsDelta => Kind switch
    {
        "qty" => ((NewQuantity ?? 0) - (OldQuantity ?? 0)) * Price,
        "add" or "move_in" => (NewQuantity ?? 0) * Price,
        "del" or "move_out" => -(OldQuantity ?? 0) * Price,
        _ => 0
    };
}

public sealed record SpecUpdateDiff(
    IReadOnlyList<SpecUpdateChange> Changes,
    IReadOnlyDictionary<string, int> Unchanged,
    IReadOnlyList<string> NewSystemOrder)
{
    public int Count(params string[] kinds) => Changes.Count(x => kinds.Contains(x.Kind));
}

/// <summary>
/// SQL-independent Python spec_update.compare parity: original source names, per-system
/// matching, duplicate-quantity aggregation, move detection and system renames.
/// Manual positions never enter the diff. No external database writes.
/// </summary>
public static class SpecUpdatePlan
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    public const decimal Epsilon = 0.000001m;

    public static string Normalize(string? value) => Whitespace.Replace(
        (value ?? "").Replace('\u00a0', ' ').Replace('ё', 'е').Replace('Ё', 'Е').Trim(),
        " ").ToLowerInvariant();

    public static string ItemKey(string? name, string? unit) =>
        Normalize(name) + "\u001f" + Normalize(unit).TrimEnd('.').Replace('²', '2').Replace('³', '3');

    public static SpecUpdateDiff Compare(
        IReadOnlyList<SpecExistingSystem> systems,
        IReadOnlyList<SpecExistingPosition> positions,
        IReadOnlyList<SpecIncomingSystem> incoming)
    {
        ArgumentNullException.ThrowIfNull(systems);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(incoming);

        var ids = systems.ToDictionary(s => s.Id);
        if (systems.GroupBy(s => Normalize(s.Name)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Найдены одноимённые системы: сопоставление неоднозначно.");
        if (positions.Any(p => !ids.ContainsKey(p.SystemId)))
            throw new InvalidOperationException("В снимке есть позиция чужой системы.");
        if (incoming.Any(s => string.IsNullOrWhiteSpace(s.Name)))
            throw new ArgumentException("В спецификации есть система без названия.");
        // Merge repeated incoming sections without dropping duplicate positions.
        var newNames = new List<string>();
        var newRows = new Dictionary<string, Dictionary<string, SpecIncomingPosition>>();
        foreach (var section in incoming)
        {
            var name = Normalize(section.Name);
            if (!newRows.TryGetValue(name, out var rows))
            {
                rows = new Dictionary<string, SpecIncomingPosition>();
                newRows[name] = rows;
                newNames.Add(section.Name);
            }
            foreach (var row in section.Positions)
            {
                if (string.IsNullOrWhiteSpace(row.Name) || row.Quantity < 0)
                    throw new ArgumentException("Пустая строка или отрицательное количество в спецификации.");
                var key = ItemKey(row.Name, row.Unit);
                if (rows.TryGetValue(key, out var old))
                    rows[key] = old with { Quantity = old.Quantity + row.Quantity,
                        Order = Math.Min(old.Order, row.Order) };
                else
                    rows[key] = row;
            }
        }

        var oldRows = new Dictionary<string, Dictionary<string, List<SpecExistingPosition>>>();
        var oldNames = new Dictionary<string, string>();
        foreach (var sys in systems.OrderBy(s => s.Order).ThenBy(s => s.Id))
        {
            var key = Normalize(sys.Name);
            oldNames[key] = sys.Name;
            oldRows[key] = new();
        }
        foreach (var p in positions)
        {
            // A component is not an independent schedule row. Area-collapse rows
            // require special remapping, and the SQL adapter rejects that mode.
            if (p.ParentId is not null || p.OutOfKp || !p.FromSpecification) continue;
            var section = oldRows[Normalize(ids[p.SystemId].Name)];
            var key = ItemKey(string.IsNullOrWhiteSpace(p.SourceName) ? p.Name : p.SourceName, p.Unit);
            if (!section.TryGetValue(key, out var group))
                section[key] = group = new List<SpecExistingPosition>();
            group.Add(p);
        }

        var changes = new List<SpecUpdateChange>();
        var unchanged = new Dictionary<string, int>();
        var absent = oldRows.Keys.Where(x => !newRows.ContainsKey(x) && oldRows[x].Count > 0).ToList();
        var appeared = newRows.Keys.Where(x => !oldRows.ContainsKey(x)).ToList();
        var pairs = new Dictionary<string, string>(); // new normalized name -> previous normalized name

        foreach (var newer in appeared)
        {
            var bestScore = 0m;
            string? best = null;
            foreach (var previous in absent.Where(x => !pairs.ContainsValue(x)))
            {
                var a = newRows[newer].Keys.ToHashSet();
                var b = oldRows[previous].Keys.ToHashSet();
                var union = a.Union(b).Count();
                var score = union == 0 ? 0 : (decimal)a.Intersect(b).Count() / union;
                if (score <= bestScore) continue;
                bestScore = score;
                best = previous;
            }
            if (best is not null && bestScore >= 0.6m)
            {
                pairs[newer] = best;
                changes.Add(new SpecUpdateChange("sys_rename",
                    newNames.First(x => Normalize(x) == newer), "", "",
                    null, null, 0, null, Array.Empty<long>(), oldNames[best], "", 0));
            }
        }

        var usedIds = new HashSet<long>();
        foreach (var newer in newNames)
        {
            var name = Normalize(newer);
            var oldName = pairs.GetValueOrDefault(name) ??
                          (oldRows.ContainsKey(name) ? name : null);
            var prior = oldName is not null ? oldRows[oldName] : new();
            if (oldName is null)
                changes.Add(new SpecUpdateChange("sys_add", newer, "", "",
                    null, null, 0, null, Array.Empty<long>(), "", "",
                    newRows[name].Values.Select(x => x.Order).DefaultIfEmpty(0).Min()));

            var same = 0;
            foreach (var row in newRows[name].Values.OrderBy(x => x.Order))
            {
                var key = ItemKey(row.Name, row.Unit);
                if (prior.TryGetValue(key, out var matches))
                {
                    foreach (var p in matches) usedIds.Add(p.Id);
                    var qty = matches.Sum(x => x.Quantity);
                    if (Math.Abs(qty - row.Quantity) > Epsilon)
                        changes.Add(new SpecUpdateChange("qty", newer, row.Name, row.Unit,
                            qty, row.Quantity, matches[0].Price, matches[0].Id,
                            matches.Select(x => x.Id).ToArray(), "", matches.Count > 1
                                ? $"В КП {matches.Count} одинаковых строк" : "", row.Order));
                    else same++;
                    continue;
                }
                var movedFrom = oldRows.FirstOrDefault(pair => pair.Key != oldName &&
                    pair.Value.TryGetValue(key, out var priorRows) &&
                    priorRows.Any(p => !usedIds.Contains(p.Id)) &&
                    (!newRows.ContainsKey(pair.Key) || !newRows[pair.Key].ContainsKey(key))).Key;
                var moving = movedFrom is not null;
                var price = moving ? oldRows[movedFrom!][key][0].Price : 0;
                changes.Add(new SpecUpdateChange(moving ? "move_in" : "add", newer,
                    row.Name, row.Unit, null, row.Quantity, price,
                    null, Array.Empty<long>(),
                    moving ? oldNames[movedFrom!] : "", "", row.Order));
            }
            unchanged[newer] = same;
        }
        foreach (var (oldKey, rows) in oldRows)
        {
            var renamed = pairs.FirstOrDefault(pair => pair.Value == oldKey).Key;
            var newName = renamed ?? (newRows.ContainsKey(oldKey) ? oldKey : null);
            foreach (var (key, matches) in rows)
            {
                if (matches.Any(p => usedIds.Contains(p.Id))) continue;
                var movedTo = newNames.FirstOrDefault(name =>
                    newRows[Normalize(name)].ContainsKey(key) &&
                    Normalize(name) != newName &&
                    changes.Any(ch => ch.Kind == "move_in" && ch.System == name &&
                                      ItemKey(ch.Name, ch.Unit) == key));
                var p = matches[0];
                var warning = matches.Any(x => x.HasReferences)
                    ? "Есть привязки к документам/движениям — удаление запрещено, строка останется" : "";
                changes.Add(new SpecUpdateChange(movedTo is null ? "del" : "move_out",
                    oldNames[oldKey],
                    string.IsNullOrWhiteSpace(p.SourceName) ? p.Name : p.SourceName,
                    p.Unit, matches.Sum(x => x.Quantity), null,
                    p.Price, p.Id, matches.Select(x => x.Id).ToArray(), movedTo ?? "",
                    warning, 0));
            }
            if (newName is null && rows.Count > 0 && !pairs.ContainsValue(oldKey))
                changes.Add(new SpecUpdateChange("sys_del", oldNames[oldKey], "", "",
                    null, null, 0, null, Array.Empty<long>(), oldNames[oldKey], "", 0));
        }
        if (changes.Select(x => x.Key).Distinct().Count() != changes.Count)
            throw new InvalidOperationException("Неоднозначные изменения спецификации (повторяющиеся ключи).");
        return new SpecUpdateDiff(changes, unchanged, newNames);
    }
}
