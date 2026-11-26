namespace GmcSmeta.Core.Domain;

public static partial class EstimateReader
{
    private static ContractTotals CalculateTotals(Head head, TagRules rules,
        IReadOnlyList<SystemRow> systems, IReadOnlyList<PositionRow> positions,
        CancellationToken token = default) =>
        Totals.CalculateOneFromRows(rules,
            new TotalsContract(head.ContractId, head.Kind, head.Form, head.VatRate,
                head.SmrMode, head.SmrAmount, head.SmrPercent, head.Transport, head.Pnr, head.DiscountPercent),
            systems.Select(static s => new TotalsSystem(s.Id, s.SmrAmount)),
            positions, static p => p.SystemId, static p => p.ParentId is null && !p.OutOfKp,
            static p => new TotalsPosition(p.SystemId, p.Qty, p.Price,
                p.WorkPrice ?? 0m, p.OutOfKp, p.ParentId is not null),
            positions.Count, token);

    private static EstimateStatistics Statistics(ContractTotals totals, IReadOnlyList<PositionRow> positions)
    {
        // Python work_rows_sum rounds the sum once, independently of round_rows.
        var work = decimal.Round(positions.Where(p => p.ParentId is null && !p.OutOfKp && EstimateStatistics.IsWorkName(p.Name))
            .Sum(p => p.Qty * p.Price), 2, MidpointRounding.ToEven);
        return new EstimateStatistics(decimal.Round(totals.Materials - work, 2, MidpointRounding.ToEven),
            decimal.Round(totals.Smr + work, 2, MidpointRounding.ToEven), work);
    }
}
