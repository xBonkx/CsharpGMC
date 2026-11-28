using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GmcSmeta.App;

public partial class EstimateView
{
    private EstimateSummaryPanel? SummaryView => SummaryPanel?.Child as EstimateSummaryPanel;

    private Button SummaryTransferSmrButton => SummaryView?.SummaryTransferSmrButton!;
    private Button SummaryExcelButton => SummaryView?.SummaryExcelButton!;
    private Border SummaryMissingChrome => SummaryView?.SummaryMissingChrome!;
    private ToggleButton SummaryMissingToggle => SummaryView?.SummaryMissingToggle!;
    private TextBlock SummaryMissingText => SummaryView?.SummaryMissingText!;
    private Button SummaryResetViewButton => SummaryView?.SummaryResetViewButton!;
    private Button SummarySystemFilter => SummaryView?.SummarySystemFilter!;
    private TextBlock SummarySystemFilterText => SummaryView?.SummarySystemFilterText!;
    private TextBox SummarySearch => SummaryView?.SummarySearch!;
    private DataGrid SummaryGrid => SummaryView?.SummaryGrid!;
    private TextBlock SummaryNote => SummaryView?.SummaryNote!;
    private Border SummaryTileMat => SummaryView?.SummaryTileMat!;
    private TextBlock SummaryTileMatValue => SummaryView?.SummaryTileMatValue!;
    private TextBlock SummaryTileMatDetail => SummaryView?.SummaryTileMatDetail!;
    private Border SummaryTileWork => SummaryView?.SummaryTileWork!;
    private TextBlock SummaryTileWorkValue => SummaryView?.SummaryTileWorkValue!;
    private TextBlock SummaryTileWorkDetail => SummaryView?.SummaryTileWorkDetail!;
    private TextBlock SummaryTileCostValue => SummaryView?.SummaryTileCostValue!;
    private TextBlock SummaryTileSellValue => SummaryView?.SummaryTileSellValue!;
    private TextBlock SummaryTileMatMarginValue => SummaryView?.SummaryTileMatMarginValue!;
    private TextBlock SummaryTileWorkMarginValue => SummaryView?.SummaryTileWorkMarginValue!;

    internal static EstimateSummaryPanel EnsureSummaryView(Border host, EstimateView? owner)
    {
        if (host.Child is EstimateSummaryPanel existing) return existing;
        using var timing = GmcSmeta.Core.StartupTrace.Measure("estimate_lazy_summary");
        var view = new EstimateSummaryPanel { Owner = owner };
        host.Child = view;
        return view;
    }
}
