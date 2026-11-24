using System.Windows;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Python kp_editor._fill_area and form-specific basis visibility. Hidden m²
/// columns do not trigger area calculation for 2500+ row contracts.
/// </summary>
public partial class EstimateView
{
    private void ConfigureSpecialColumns()
    {
        if (_estimate is null || AreaUnitColumn is null ||
            AreaTotalColumn is null || BasisColumn is null) return;

        AreaUnitColumn.Visibility = _uiSettings.AreaUnitVisible
            ? Visibility.Visible : Visibility.Collapsed;
        AreaTotalColumn.Visibility = _uiSettings.AreaTotalVisible
            ? Visibility.Visible : Visibility.Collapsed;
        BasisColumn.Header = AreaCellPresentation.BasisHeader(_estimate.Form);
        BasisColumn.Visibility = _estimate.Form == "r4" ||
            (_estimate.BasisAllowed && _uiSettings.BasisVisible)
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshAreaColumnValues();
    }

    private void RefreshAreaColumnValues()
    {
        if (AreaUnitColumn is null || AreaTotalColumn is null ||
            (AreaUnitColumn.Visibility != Visibility.Visible &&
             AreaTotalColumn.Visibility != Visibility.Visible))
            return;
        foreach (var row in _rows)
            row.FillArea();
    }
}
