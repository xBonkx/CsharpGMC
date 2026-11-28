namespace GmcSmeta.Core.Domain;

/// <summary>Legacy gross-based KS-3 calculation. Legal group/opening balances
/// are handled by KsGroupReport; this component does not infer legal ownership.</summary>
public static class Ks3GrossCalculation
{
    public sealed record Result(decimal Period, decimal Year, decimal Start, decimal Vat,
        decimal Total, IReadOnlyList<(string Title, decimal Net)> Previous);

    public static Result Calculate(
        IReadOnlyList<(long Id, string Number, DateTime Date, decimal Total)> acts,
        long actId, decimal vatRate)
    {
        if (vatRate < 0m)
            throw new ArgumentOutOfRangeException(nameof(vatRate));
        var index = -1;
        for (var i = 0; i < acts.Count; i++)
            if (acts[i].Id == actId) { index = i; break; }
        if (index < 0) throw new InvalidOperationException("Акт не найден.");

        var current = acts[index];
        var upto = acts.Take(index + 1).ToList();
        var inYear = upto.Where(a => a.Date.Year == current.Date.Year).ToList();
        decimal Net(decimal amount) =>
            decimal.Round(amount - amount * vatRate / (100m + vatRate), 2,
                MidpointRounding.ToEven);
        var previous = upto.Take(index)
            .Select(a => (Title: $"КС-2 № {a.Number} от {a.Date:dd.MM.yyyy}",
                Net: Net(a.Total)))
            .ToList();

        return new Result(
            Net(current.Total),
            Net(inYear.Sum(a => a.Total)),
            Net(upto.Sum(a => a.Total)),
            decimal.Round(current.Total * vatRate / (100m + vatRate), 2,
                MidpointRounding.ToEven),
            current.Total, previous);
    }
}
