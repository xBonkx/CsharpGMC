using System.Windows;
using System.Windows.Controls;

namespace GmcSmeta.App;

public partial class EstimateView
{
    private bool MaterialsCanChangeDisclosure()
    {
        if (_detached || _materialsLoading || _materialsBusy || MaterialsGrid is not { } grid)
            return false;
        if (HasGridEdit(grid))
        {
            SetStatus("Завершите редактирование ячейки перед изменением вида.", true);
            return false;
        }
        return true;
    }

    private void MaterialsSetNestedExpanded(bool expand)
    {
        if (!MaterialsCanChangeDisclosure()) return;
        _materialsVisibleRows.SetNestedExpanded(expand);
        MaterialsApplyViewFilter();
    }

    internal void MaterialsToggleChildren_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MaterialsRow row } ||
            !row.HasChildren || !MaterialsCanChangeDisclosure() ||
            MaterialsGrid?.Items.Contains(row) != true) return;
        row.IsExpanded = !row.IsExpanded;
        MaterialsApplyViewFilter();
        e.Handled = true;
    }
}
