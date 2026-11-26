using System.Windows;
using System.Windows.Controls;

namespace GmcSmeta.App;

/// <summary>Compiled summary visuals. Forward original event senders, including template buttons.</summary>
public partial class EstimateSummaryPanel : Grid
{
    internal EstimateView? Owner { get; set; }

    public EstimateSummaryPanel() => InitializeComponent();

    private void SummaryTransferSmr_Click(object sender, RoutedEventArgs e) => Owner?.SummaryTransferSmr_Click(sender, e);
    private void SummaryExcel_Click(object sender, RoutedEventArgs e) => Owner?.SummaryExcel_Click(sender, e);
    private void SummaryGroup_Click(object sender, RoutedEventArgs e) => Owner?.SummaryGroup_Click(sender, e);
    private void SummarySort_Click(object sender, RoutedEventArgs e) => Owner?.SummarySort_Click(sender, e);
    private void SummaryMissing_Changed(object sender, RoutedEventArgs e) => Owner?.SummaryMissing_Changed(sender, e);
    private void SummaryMissingMenu_Click(object sender, RoutedEventArgs e) => Owner?.SummaryMissingMenu_Click(sender, e);
    private void SummaryResetView_Click(object sender, RoutedEventArgs e) => Owner?.SummaryResetView_Click(sender, e);
    private void SummaryMarkupAll_Click(object sender, RoutedEventArgs e) => Owner?.SummaryMarkupAll_Click(sender, e);
    private void SummarySystemFilter_Click(object sender, RoutedEventArgs e) => Owner?.SummarySystemFilter_Click(sender, e);
    private void SummarySearch_TextChanged(object sender, TextChangedEventArgs e) => Owner?.SummarySearch_TextChanged(sender, e);
    private void ClearSummarySearch_Click(object sender, RoutedEventArgs e) => Owner?.ClearSummarySearch_Click(sender, e);
    private void SummaryGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e) => Owner?.SummaryGrid_BeginningEdit(sender, e);
    private void SummaryGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e) => Owner?.SummaryGrid_PreparingCellForEdit(sender, e);
    private void SummaryGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) => Owner?.SummaryGrid_CellEditEnding(sender, e);
    private void SummaryGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e) => Owner?.SummaryGrid_ContextMenuOpening(sender, e);
    private void SummaryToggleSystem_Click(object sender, RoutedEventArgs e) => Owner?.SummaryToggleSystem_Click(sender, e);
}
