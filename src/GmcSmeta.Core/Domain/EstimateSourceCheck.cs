using System.Globalization;

namespace GmcSmeta.Core.Domain;

/// <summary>Python 0.67.7: compare the imported estimate independently of a final discount.</summary>
public static class EstimateSourceCheck
{
    public sealed record Result(bool? Matches,string Details);
    public static Result Compare(decimal? declaredMaterial,decimal? declaredTotal,ContractTotals totals)
    {
        var checks=new List<bool>();
        var details=new List<string>();
        string F(decimal value)=>value.ToString("N2",CultureInfo.GetCultureInfo("ru-RU"))+" ₽";
        if(declaredMaterial is decimal material)
        {
            var delta=totals.Materials-material;
            var ok=Math.Abs(delta)<=1m;
            checks.Add(ok);
            details.Add($"Материалы: файл {F(material)} · расчёт {F(totals.Materials)}"+
                (ok?"":$" · разница {F(delta)}"));
        }
        if(declaredTotal is decimal declared)
        {
            // Totals.Net is the pre-discount amount excluding VAT only when VAT is on top;
            // the final amount plus its separate adjustment recovers the gross amount.
            var gross=totals.Total+totals.DiscountAmount;
            var before=Math.Abs(gross-declared)<=1m;
            var after=Math.Abs(totals.Total-declared)<=1m;
            checks.Add(before||after);
            details.Add($"Итого в файле: {F(declared)}");
            if(totals.DiscountPercent!=0m)
            {
                details.Add($"До скидки: {F(gross)}");
                details.Add($"После скидки: {F(totals.Total)}");
                details.Add(before?"Совпадает с итогом до скидки.":after?
                    "Совпадает с итогом после скидки.":"Не совпадает ни с итогом до скидки, ни после неё.");
            }
            else details.Add($"Расчётный итог: {F(totals.Total)}");
            if(!before&&!after) details.Add($"Разница до скидки: {F(gross-declared)}"+
                (totals.DiscountPercent!=0m?$" · после скидки: {F(totals.Total-declared)}":""));
        }
        return new(checks.Count==0?null:checks.All(x=>x),string.Join(Environment.NewLine,details));
    }
}
