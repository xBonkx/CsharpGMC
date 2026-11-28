using System.Threading;

namespace GmcSmeta.App;

public partial class ShipmentsPane
{
    private readonly record struct ShipmentSummaryCounts(int Over, int Planned, int Done);

    // Used on the worker for full snapshots; sparse readback keeps the same
    // authoritative counting rule. No WPF control is accessed here.
    private static ShipmentSummaryCounts CountShipmentRows(
        IEnumerable<ShipmentPreviewRow> rows, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var over = 0;
        var planned = 0;
        var done = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            _ = row.SystemGroup; // Prepare immutable grouping key before binding.
            if (row.StockOverrun) over++;
            if (!row.Planned) continue;
            planned++;
            if (row.FullyIssued) done++;
        }
        token.ThrowIfCancellationRequested();
        return new(over, planned, done);
    }
}
