using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class ShipmentsPane
{
    // Preserve the user's draft filter when a fixed shipment forces its own rows.
    private int _requestedRowFilter;
    private readonly ShipmentSearchQuery _shipmentSearch = new();

    // Read the current text even for actions/incremental readback invoked
    // outside a normal view.Refresh. Only tokenize when the text changes.
    private bool ShipmentSearchMatches(ShipmentPreviewRow row) =>
        _shipmentSearch.Matches(RowSearch?.Text, row.Name, row.SystemLabel);

    private int EffectiveShipmentFilter =>
        Current?.Fixed == true && !IsEditingFixed ? 2 : _requestedRowFilter;

    private void SyncShipmentFilter(bool hasShipment, bool editable)
    {
        RowFilter.IsEnabled = hasShipment && editable;
        var filter = hasShipment ? EffectiveShipmentFilter : 0;
        // These radios share the same logical Grid parent, not a window-wide
        // GroupName. Programmatic IsChecked changes do not invoke Click.
        RowFilterAll.IsChecked = filter == 0;
        RowFilterStock.IsChecked = filter == 1;
        RowFilterList.IsChecked = filter == 2;
    }

    private void ShipmentSegment_Click(object sender, RoutedEventArgs e)
    {
        if (_building || _quantityBusy || !IsEditable ||
            LinesGrid?.ItemsSource is not ListCollectionView { IsEditingItem: false })
        {
            SyncShipmentFilter(Current is not null, IsEditable);
            return;
        }

        var filter = ReferenceEquals(sender, RowFilterStock) ? 1
            : ReferenceEquals(sender, RowFilterList) ? 2 : 0;
        if (filter == _requestedRowFilter) return;
        _requestedRowFilter = filter;
        RefreshRowFilter();
    }
}
