using System.Globalization;

namespace GmcSmeta.Core.Domain;

/// <summary>Python 0.67.3: one discount input model for independent final discount
/// and the legacy price-reduction mode. No position or act mutation.</summary>
public static class EstimateDiscountInput
{
    public sealed record State(decimal Gross,decimal Total,decimal Materials,decimal Smr,
        bool SmrIsPercent,decimal CurrentDiscount,bool SupportsTotal);
    public sealed record Preview(bool OnTotal,decimal Percent,decimal Total,decimal Adjustment,
        bool Smr,bool WholeRubles,bool Changed);

    public static decimal? Parse(string? text)
    {
        var value=(text??"").Replace("\u00a0","").Replace("\u202f","").Replace(" ","")
            .Replace("₽","").Replace(',','.');
        return decimal.TryParse(value,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,out var parsed)?parsed:null;
    }

    private static decimal Basis(State state,bool onTotal,bool smr)=>
        onTotal?Money.Round2(state.Gross):state.Materials+(smr||state.SmrIsPercent?state.Smr:0m);

    public static Preview? FromPercent(State state,bool onTotal,decimal? input,bool smr,bool whole)
    {
        if(input is not decimal percent) return null;
        var basis=Basis(state,onTotal,smr);
        if(onTotal)
        {
            percent=decimal.Round(percent,9,MidpointRounding.ToEven);
            if(!state.SupportsTotal||basis<=0m||percent<0m||percent>100m) return null;
            var payable=Money.Round2(DiscountPolicy.Payable(basis,percent));
            return new(true,percent,payable,basis-payable,false,false,percent!=state.CurrentDiscount);
        }
        if(state.CurrentDiscount!=0m||state.Materials==0m||percent==0m||percent>=100m) return null;
        try
        {
            var payable=Money.Round2(state.Total-basis*percent/100m);
            return new(false,percent,payable,Money.Round2(state.Total-payable),smr,whole,true);
        }
        catch(OverflowException){return null;}
    }

    public static Preview? FromTarget(State state,bool onTotal,decimal? input,bool smr,bool whole)
    {
        if(input is not decimal target) return null;
        var basis=Basis(state,onTotal,smr);
        if(basis==0m||onTotal&&(target<0m||target>basis)) return null;
        try
        {
            var amount=onTotal?Money.Round2(target):target;
            var percent=decimal.Round(((onTotal?basis:state.Total)-amount)/basis*100m,
                onTotal?9:4,MidpointRounding.ToEven);
            var preview=FromPercent(state,onTotal,percent,smr,whole);
            return onTotal&&preview?.Total!=amount?null:preview;
        }
        catch(OverflowException){return null;}
    }
}
