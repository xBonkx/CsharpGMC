namespace GmcSmeta.Core.Domain;

/// <summary>Python stages.refresh_money: each linked system receives its own
/// proportional SMR, based on its own material sums (not the entire estimate).</summary>
public static class StageSystemMoneyPolicy
{
    public sealed record Row(long SystemId,decimal Quantity,decimal Price,bool RoundRows);
    public sealed record Source(long Id,decimal Smr);
    public sealed record Destination(long Id,long? SourceId);

    public static IReadOnlyDictionary<long,decimal> Calculate(IReadOnlyList<Source> sources,
        IReadOnlyList<Destination> destinations,IEnumerable<Row> rows)
    {
        var sourceById=sources.ToDictionary(s=>s.Id);
        var material=rows.GroupBy(r=>r.SystemId).ToDictionary(g=>g.Key,
            g=>g.Sum(r=>r.RoundRows?Money.Round2(r.Quantity*r.Price):r.Quantity*r.Price));
        var amounts=new Dictionary<long,decimal>();
        foreach(var dest in destinations)
        {
            if(dest.SourceId is not long id||!sourceById.TryGetValue(id,out var source))continue;
            var total=material.GetValueOrDefault(id);
            var share=total>0.000000001m?material.GetValueOrDefault(dest.Id)/total:0m;
            amounts.Add(dest.Id,decimal.Round(source.Smr*share,2,MidpointRounding.ToEven));
        }
        return amounts;
    }
}
