using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GmcSmeta.App;

public partial class EstimateView
{
    private EstimateMaterialsPanel? MaterialsView => MaterialsPanel?.Child as EstimateMaterialsPanel;

    private ToggleButton MaterialsNoCostToggle => MaterialsView?.MaterialsNoCostToggle!;
    private TextBlock MaterialsNoCostText => MaterialsView?.MaterialsNoCostText!;
    private ToggleButton MaterialsNoPriceToggle => MaterialsView?.MaterialsNoPriceToggle!;
    private TextBlock MaterialsNoPriceText => MaterialsView?.MaterialsNoPriceText!;
    private Button MaterialsResetViewButton => MaterialsView?.MaterialsResetViewButton!;
    private ComboBox MaterialsSystemFilter => MaterialsView?.MaterialsSystemFilter!;
    private TextBox MaterialsSearch => MaterialsView?.MaterialsSearch!;
    private Border? MaterialsHintBar => MaterialsView?.MaterialsHintBar;
    private DataGrid? MaterialsGrid => MaterialsView?.MaterialsGrid;
    private TextBlock MatTileRevCaption => MaterialsView?.MatTileRevCaption!;
    private TextBlock MatTileRevValue => MaterialsView?.MatTileRevValue!;
    private TextBlock MatTileCostValue => MaterialsView?.MatTileCostValue!;
    private Button MatTileTrans => MaterialsView?.MatTileTrans!;
    private TextBlock MatTileMarginCaption => MaterialsView?.MatTileMarginCaption!;
    private TextBlock MatTileMarginValue => MaterialsView?.MatTileMarginValue!;

    private void EnsureMaterialsView()
    {
        if (MaterialsView is not null) return;
        using var timing = GmcSmeta.Core.StartupTrace.Measure("estimate_lazy_materials");
        var view = new EstimateMaterialsPanel { Owner = this };
        view.MaterialsMarkupButton.Click += MaterialsMarkup_Click;
        view.MaterialsCatalogButton.Click += MaterialsCatalog_Click;
        view.MaterialsRequestButton.Click += MaterialsRequest_Click;
        view.MaterialsColorButton.Click += MaterialsColor_Click;
        view.MaterialsGroupingButton.Click += MaterialsGrouping_Click;
        view.MaterialsSortingButton.Click += MaterialsSorting_Click;
        view.MaterialsNoCostToggle.Click += MaterialsFilter_Click;
        view.MaterialsNoCostToggle.Checked += MaterialsFilter_Click;
        view.MaterialsNoCostToggle.Unchecked += MaterialsFilter_Click;
        view.MaterialsNoPriceToggle.Click += MaterialsFilter_Click;
        view.MaterialsNoPriceToggle.Checked += MaterialsFilter_Click;
        view.MaterialsNoPriceToggle.Unchecked += MaterialsFilter_Click;
        view.MaterialsResetViewButton.Click += MaterialsResetView_Click;
        view.MaterialsSystemFilter.SelectionChanged += MaterialsSystemFilter_Changed;
        view.MaterialsSearch.TextChanged += MaterialsSearch_TextChanged;
        view.MaterialsExportButton.Click += MaterialsExport_Click;
        view.MaterialsHintCloseButton.Click += MaterialsHintClose_Click;
        view.MaterialsGrid.BeginningEdit += MaterialsGrid_BeginningEdit;
        view.MaterialsGrid.PreparingCellForEdit += MaterialsGrid_PreparingCellForEdit;
        view.MaterialsGrid.CellEditEnding += MaterialsGrid_CellEditEnding;
        view.MaterialsGrid.ContextMenuOpening += MaterialsGrid_ContextMenuOpening;
        view.MaterialsGrid.PreviewKeyDown += MaterialsGrid_PreviewKeyDown;
        view.MatTileTrans.Click += MatTileTrans_Click;
        view.MaterialsHintBar.Visibility = _uiSettings.MaterialsHintHidden
            ? Visibility.Collapsed : Visibility.Visible;
        MaterialsPanel.Child = view;
    }
}
