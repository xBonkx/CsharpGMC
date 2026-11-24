namespace GmcSmeta.Core.Domain;

/// <summary>
/// Python acts.smr_by_system: exact per-system work prices for 'pos',
/// otherwise proportional material amounts; the final system absorbs
/// any cent rounding residue, preserving the act's exact SMR total.
/// Pure and deterministic, without database access.
/// </summary>
public static class KsActAllocation
{
    public sealed record Line(long SystemId, decimal Quantity, decimal Price, decimal WorkPrice);

    public sealed record SectionAmounts(IReadOnlyDictionary<long, decimal> Smr,
        IReadOnlyDictionary<long, decimal> Transport);

    /// <summary>Empty string is legacy allocation; an explicit empty JSON map is not.</summary>
    public static SectionAmounts? ReadManual(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new FormatException("Неверная разбивка сумм КС.");
        Dictionary<long, decimal> Read(string kind)
        {
            var result = new Dictionary<long, decimal>();
            if (!doc.RootElement.TryGetProperty(kind, out var map)) return result;
            foreach (var entry in map.EnumerateObject())
            {
                if (!long.TryParse(entry.Name, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var id))
                    throw new FormatException("Неверный номер раздела КС.");
                var amount = entry.Value.GetDecimal();
                if (amount < 0m || amount > 1000000000000m)
                    throw new FormatException("Неверная сумма раздела КС.");
                if (amount != 0m) result[id] = amount;
            }
            return result;
        }
        return new SectionAmounts(Read("smr"), Read("transport"));
    }

    public static SectionAmounts Resolve(IReadOnlyList<Line> lines, IReadOnlySet<long> validSystems,
        decimal smr, decimal transport, bool perPosition, bool bySystem, string? json,
        bool roundRows = false)
    {
        var manual = ReadManual(json);
        Dictionary<long, decimal> Known(IReadOnlyDictionary<long, decimal> source) =>
            source.Where(x => x.Key != 0 && validSystems.Contains(x.Key) && x.Value != 0m)
                .ToDictionary(x => x.Key, x => x.Value);
        var works = perPosition ? Known(Split(lines, smr, true, roundRows)) :
            !bySystem ? new Dictionary<long, decimal>() :
            manual is not null ? Known(manual.Smr) : Known(Split(lines, smr, false, roundRows));
        var delivery = manual is null ? new Dictionary<long, decimal>() : Known(manual.Transport);
        // Explicit remainder includes old allocations to deleted sections.
        // Keep any inconsistency visible instead of silently changing saved totals.
        var workRest = Money.Round2(smr - works.Values.Sum());
        var transportRest = Money.Round2(transport - delivery.Values.Sum());
        if (workRest != 0m) works[0] = workRest;
        if (transportRest != 0m) delivery[0] = transportRest;
        return new SectionAmounts(works, delivery);
    }

    public static IReadOnlyDictionary<long,decimal> Split(
        IReadOnlyList<Line> lines, decimal smr, bool perPosition, bool roundRows = false)
    {
        if (lines is null) throw new ArgumentNullException(nameof(lines));
        var perSystem=new Dictionary<long,decimal>();
        foreach (var line in lines)
        {
            if (line.SystemId<=0) throw new ArgumentOutOfRangeException(nameof(lines));
            var amount=decimal.Round(line.Quantity*(perPosition ? line.WorkPrice : line.Price),2,
                !perPosition && roundRows ? MidpointRounding.AwayFromZero : MidpointRounding.ToEven);
            perSystem[line.SystemId]=perSystem.GetValueOrDefault(line.SystemId)+amount;
        }

        if (perPosition)
            return perSystem.Where(kv => kv.Value!=0m).ToDictionary(
                kv=>kv.Key,kv=>decimal.Round(kv.Value,2,MidpointRounding.ToEven));

        if (perSystem.Count==0 || smr==0m)
            return new Dictionary<long,decimal>();
        var materialTotal=perSystem.Values.Sum();
        var result=new Dictionary<long,decimal>();
        decimal assigned=0m;
        var keys=perSystem.Keys.ToList();
        for (var i=0;i<keys.Count;i++)
        {
            var part=i==keys.Count-1 ? decimal.Round(smr-assigned,2,MidpointRounding.ToEven) :
                materialTotal==0m ? 0m :
                decimal.Round(smr*perSystem[keys[i]]/materialTotal,2,MidpointRounding.ToEven);
            result[keys[i]]=part;
            assigned+=part;
        }
        return result;
    }
}
