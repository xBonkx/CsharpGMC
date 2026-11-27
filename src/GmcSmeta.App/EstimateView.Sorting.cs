using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class EstimateView
{
    private void EstimateGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // Never allow native WPF sorting to separate systems/components or
        // sort the formatted text in a money/quantity cell.
        e.Handled = true;
        var field = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(field) || field == EstimateViewProjection.DocumentOrder ||
            !EstimateViewProjection.IsValidSort(field)) return;
        SetTemporarySort(EstimateViewProjection.NextSort(_temporarySort, field));
    }

    private void SetTemporarySort(string sort)
    {
        // Do not discard an active edit by rebuilding ItemsSource.
        if (CollectionViewSource.GetDefaultView(EstimateGrid.ItemsSource)
            is IEditableCollectionView { IsEditingItem: true })
        {
            SetStatus("Завершите редактирование ячейки перед сортировкой.", true);
            return;
        }
        _temporarySort = sort;
        _frozenTemporaryOrder.Clear();
        _frozenGroupedOrder.Clear();
        ApplyFilter();
    }

    private void RefreshEstimateSortIndicators()
    {
        var field = EstimateViewProjection.SortField(_temporarySort);
        foreach (var column in EstimateGrid.Columns)
            column.SortDirection = _temporarySort != EstimateViewProjection.DocumentOrder &&
                column.SortMemberPath == field
                ? EstimateViewProjection.IsDescending(_temporarySort)
                    ? ListSortDirection.Descending : ListSortDirection.Ascending
                : null;
    }
}
