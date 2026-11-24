namespace GmcSmeta.Core.Domain;

/// <summary>Schema 30/31 section allocation validation and proportional suggestion.
/// Pure calculation only. A future writer must obtain all limits under the revision lock.</summary>
public static class KsSectionEditPolicy
{
    public sealed record SystemBudget(long Id,long? ParentId,decimal Smr);
    public sealed record Quantity(long SystemId,decimal PlannedQty,decimal ClosedQty,decimal Price,bool OutsideKp=false);
    public sealed record Validated(KsActAllocation.SectionAmounts Amounts,decimal Smr,decimal Transport)
    {
        public string Json => System.Text.Json.JsonSerializer.Serialize(new
        {
            smr = Amounts.Smr, transport = Amounts.Transport
        });
    }

    public sealed record PriorAct(decimal Smr,decimal Transport,bool BySystem,string SectionJson,
        IReadOnlyList<KsActAllocation.Line> Lines);
    public sealed record Limits(decimal Smr,decimal Transport,IReadOnlyDictionary<long,decimal> BySystem);

    /// <summary>All other acts count, including drafts. Legacy section allocations use
    /// their stored line order; explicit manual maps never get redistributed.</summary>
    public static Limits Remaining(IReadOnlyList<SystemBudget> systems,IReadOnlyList<PriorAct> others,
        string mode,decimal smrBudget,decimal transportBudget,bool roundRows)
    {
        var valid=systems.Select(s=>s.Id).ToHashSet();
        var used=new Dictionary<long,decimal>();
        if(mode=="systems")
            foreach(var a in others.Where(a=>a.BySystem))
            {
                var manual=KsActAllocation.ReadManual(a.SectionJson);
                var split=manual?.Smr ?? KsActAllocation.Split(a.Lines,a.Smr,false,roundRows);
                foreach(var (sid,amount) in split)
                    if(valid.Contains(sid)) used[sid]=used.GetValueOrDefault(sid)+amount;
            }
        return new(decimal.Round(smrBudget-others.Sum(a=>a.Smr),2,MidpointRounding.ToEven),
            decimal.Round(transportBudget-others.Sum(a=>a.Transport),2,MidpointRounding.ToEven),
            mode=="systems" ? systems.ToDictionary(s=>s.Id,s=>Money.Round2(s.Smr-used.GetValueOrDefault(s.Id)))
                : new Dictionary<long,decimal>());
    }

    public static Validated Validate(KsActAllocation.SectionAmounts amounts,IReadOnlySet<long> validSystems,
        bool perPosition,decimal smrLeft,decimal transportLeft,IReadOnlyDictionary<long,decimal> sectionLeft)
    {
        Dictionary<long,decimal> Clean(IReadOnlyDictionary<long,decimal> values)
        {
            var result=new Dictionary<long,decimal>();
            foreach(var (id,amount) in values)
            {
                if ((id!=0 && !validSystems.Contains(id)) || amount<0m || amount>1000000000000m)
                    throw new ArgumentException("Проверьте разделы и суммы.");
                var rounded=Money.Round2(amount);
                if (rounded!=0m) result[id]=rounded;
            }
            return result;
        }
        var smr=Clean(amounts.Smr);
        var transport=Clean(amounts.Transport);
        var works=smr.Values.Sum();
        var delivery=transport.Values.Sum();
        if (perPosition && works!=0m) throw new ArgumentException("СМР по позициям рассчитывается автоматически.");
        if (!perPosition && works>Math.Max(0m,smrLeft)+0.005m)
            throw new ArgumentException("СМР превышает остаток по договору.");
        if (delivery>Math.Max(0m,transportLeft)+0.005m)
            throw new ArgumentException("Транспорт превышает остаток по договору.");
        foreach(var (id,amount) in smr)
            if (sectionLeft.TryGetValue(id,out var limit) && amount>Math.Max(0m,limit)+0.005m)
                throw new ArgumentException($"СМР превышает остаток по разделу {id}.");
        return new(new(smr,transport),works,delivery);
    }

    public static KsActAllocation.SectionAmounts Suggest(IReadOnlyList<SystemBudget> systems,
        IReadOnlyList<Quantity> quantities,string mode,decimal smrBudget,decimal transportBudget,
        decimal smrLeft,decimal transportLeft,IReadOnlyDictionary<long,decimal> sectionLeft)
    {
        var byId=systems.ToDictionary(s=>s.Id);
        var planned=new Dictionary<long,decimal>();
        var closed=new Dictionary<long,decimal>();
        foreach(var q in quantities)
        {
            if(q.OutsideKp || q.PlannedQty<=0m || q.Price<=0m || !byId.ContainsKey(q.SystemId)) continue;
            planned[q.SystemId]=planned.GetValueOrDefault(q.SystemId)+q.PlannedQty*q.Price;
            if(q.ClosedQty>0m)
                closed[q.SystemId]=closed.GetValueOrDefault(q.SystemId)+Math.Min(q.PlannedQty,q.ClosedQty)*q.Price;
        }
        decimal Share(decimal budget,decimal plan,decimal actual) =>
            plan<=0m ? 0m : Math.Max(0m,budget)*Math.Clamp(actual/plan,0m,1m);
        Dictionary<long,decimal> Capped(Dictionary<long,decimal> raw,decimal available)
        {
            var total=raw.Values.Sum();
            var cap=Math.Min(total,Math.Max(0m,available));
            // Never round a fractional available balance upward past the remaining kopecks.
            cap=Math.Min(Money.Round2(cap),Math.Floor(Math.Max(0m,available)*100m)/100m);
            var result=new Dictionary<long,decimal>();
            if(total<=0m || cap<=0m) return result;
            decimal cumulative=0m,previous=0m;
            foreach(var (id,amount) in raw)
            {
                cumulative+=amount;
                var rounded=Money.Round2(cap*cumulative/total);
                var value=rounded-previous;
                if(value!=0m) result[id]=value;
                previous=rounded;
            }
            return result;
        }
        var planTotal=planned.Values.Sum();
        var delivery=Capped(closed.ToDictionary(x=>x.Key,x=>Share(transportBudget,planTotal,x.Value)),transportLeft);
        if(mode=="pos") return new(new Dictionary<long,decimal>(),delivery);
        if(mode!="systems")
            return new(Capped(closed.ToDictionary(x=>x.Key,x=>Share(smrBudget,planTotal,x.Value)),smrLeft),delivery);
        var budgetPlan=new Dictionary<long,decimal>();
        var budgetClosed=new Dictionary<long,decimal>();
        foreach(var (id,plan) in planned)
        {
            var seen=new HashSet<long>();
            long? current=id;
            while(current is long sid && seen.Add(sid) && byId.TryGetValue(sid,out var system))
            {
                if(system.Smr>0m)
                {
                    budgetPlan[sid]=budgetPlan.GetValueOrDefault(sid)+plan;
                    budgetClosed[sid]=budgetClosed.GetValueOrDefault(sid)+closed.GetValueOrDefault(id);
                    break;
                }
                current=system.ParentId;
            }
        }
        var raw=budgetPlan.ToDictionary(x=>x.Key,x=>Math.Min(
            Money.Round2(Share(byId[x.Key].Smr,x.Value,budgetClosed.GetValueOrDefault(x.Key))),
            Math.Floor(Math.Max(0m,sectionLeft.GetValueOrDefault(x.Key))*100m)/100m));
        return new(Capped(raw,smrLeft),delivery);
    }
}
