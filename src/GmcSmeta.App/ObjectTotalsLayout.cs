using System.Windows;
using System.Windows.Controls;

namespace GmcSmeta.App;

/// <summary>Object summary tile arrangement, independent of shell lifecycle and data reads.</summary>
internal static class ObjectTotalsLayout
{
    internal static void Apply(Grid grid, FrameworkElement total, FrameworkElement progress,
        FrameworkElement materials, FrameworkElement smr, FrameworkElement earned,
        bool wide, bool showProgress, double gap)
    {
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        var cards = new[] { total, progress, materials, smr, earned };
        if (wide)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var visible = cards.Where(c => c != progress || showProgress).ToList();
            for (var i = 0; i < visible.Count; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(visible[i] == progress ? 4 : 2, GridUnitType.Star) });
                Position(visible[i], 0, i);
                visible[i].Margin = new Thickness(0, 0, i == visible.Count - 1 ? 0 : gap, 0);
            }
        }
        else
        {
            for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < 2; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Position(total, 0, 0, showProgress ? 1 : 3);
            Position(progress, 0, 1, 2);
            Position(materials, 1, 0); Position(smr, 1, 1); Position(earned, 1, 2);
            total.Margin = new Thickness(0, 0, showProgress ? gap : 0, gap);
            progress.Margin = new Thickness(0, 0, 0, gap);
            materials.Margin = smr.Margin = new Thickness(0, 0, gap, 0);
            earned.Margin = new Thickness(0);
        }
    }

    private static void Position(FrameworkElement element, int row, int column, int span = 1)
    {
        Grid.SetRow(element, row); Grid.SetColumn(element, column); Grid.SetColumnSpan(element, span);
    }

}
