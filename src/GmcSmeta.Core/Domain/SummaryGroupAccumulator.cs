namespace GmcSmeta.Core.Domain;

/// <summary>One-pass summary amounts, missing flags and uniform unit prices.
/// No rounding or price substitution: the presentation layer retains its rules.</summary>
public struct SummaryGroupAccumulator
{
    public decimal MaterialCost { get; private set; }
    public decimal MaterialSell { get; private set; }
    public decimal WorkCost { get; private set; }
    public decimal WorkSell { get; private set; }
    public bool NoCost { get; private set; }
    public bool NoPrice { get; private set; }
    public bool NowCost { get; private set; }
    public bool NowSell { get; private set; }
    public UniformPrice MaterialUnitCost;
    public UniformPrice MaterialUnitSell;
    public UniformPrice WorkUnitCost;
    public UniformPrice WorkUnitSell;

    public void Add(decimal materialCost, decimal materialSell, decimal workCost, decimal workSell,
        decimal? unitCost, decimal? unitSell, decimal? workUnitCost, decimal? workUnitSell,
        bool unitCostDiff, bool unitSellDiff, bool workUnitCostDiff, bool workUnitSellDiff,
        bool noCost, bool noPrice, bool nowCost, bool nowSell)
    {
        MaterialCost += materialCost;
        MaterialSell += materialSell;
        WorkCost += workCost;
        WorkSell += workSell;
        NoCost |= noCost;
        NoPrice |= noPrice;
        NowCost |= nowCost;
        NowSell |= nowSell;
        MaterialUnitCost.Add(unitCost, unitCostDiff);
        MaterialUnitSell.Add(unitSell, unitSellDiff);
        WorkUnitCost.Add(workUnitCost, workUnitCostDiff);
        WorkUnitSell.Add(workUnitSell, workUnitSellDiff);
    }

    public struct UniformPrice
    {
        private bool _seen;
        private bool _mixed;
        private decimal? _value;
        public bool IsUniform => _seen && !_mixed;
        public decimal? Value => IsUniform ? _value : null;

        internal void Add(decimal? value, bool differs)
        {
            if (!_seen)
            {
                _value = value;
                _seen = true;
            }
            else if (_value != value) _mixed = true;
            _mixed |= differs;
        }
    }
}
