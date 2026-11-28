using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GmcSmeta.App;

internal static class EstimateFooterLayoutChecks
{
    internal static void Run()
    {
        var panel = new EstimateTotalsCardsPanel { Columns = 2 };
        var first = new Border { MinHeight = 120 };
        var second = new Border { MinHeight = 80 };
        var third = new Border { MinHeight = 70 };
        var fourth = new Border { MinHeight = 70 };
        foreach (var card in new[] { first, second, third, fourth })
            panel.Children.Add(card);

        Layout(panel, 410, 200);
        Slot(first, 0, 0, 200, 120);
        Slot(second, 210, 0, 200, 120);
        Slot(third, 0, 130, 200, 70);
        Slot(fourth, 210, 130, 200, 70);

        second.Visibility = Visibility.Collapsed;
        Layout(panel, 410, 200);
        Slot(third, 210, 0, 200, 120);
        Slot(fourth, 0, 130, 200, 70);

        panel.Columns = 3;
        Layout(panel, 620, 120);
        Slot(first, 0, 0, 200, 120);
        Slot(third, 210, 0, 200, 120);
        Slot(fourth, 420, 0, 200, 120);

        second.Visibility = Visibility.Visible;
        panel.Columns = 4;
        Layout(panel, 830, 120);
        Slot(second, 210, 0, 200, 120);
        Slot(fourth, 630, 0, 200, 120);
        // The toggle may leave less than two minimum-width cards even when
        // the surrounding view requested two or four columns.
        panel.Columns = 2;
        Layout(panel, 390, 370);
        Slot(first, 0, 0, 390, 120);
        Slot(second, 0, 130, 390, 80);
        Slot(third, 0, 220, 390, 70);
        Slot(fourth, 0, 300, 390, 70);

        panel.Columns = 4;
        Layout(panel, 620, 200);
        Slot(first, 0, 0, 200, 120);
        Slot(third, 420, 0, 200, 120);
        Slot(fourth, 0, 130, 200, 70);
        // The capped viewport must leave the star-sized document body room even
        // when cards stack vertically at narrow widths.
        var body = new Border();
        var scroll = new ScrollViewer { Content = panel, MaxHeight = 210,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var host = new Grid();
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.Children.Add(body);
        host.Children.Add(scroll);
        Grid.SetRow(scroll, 1);
        host.Measure(new Size(330, 500));
        host.Arrange(new Rect(0, 0, 330, 500));
        host.UpdateLayout();
        if (body.ActualHeight < 289.99 || scroll.ActualHeight > 210.01)
            throw new Exception("Expanded footer consumed the document body.");
        scroll.Visibility = Visibility.Collapsed;
        host.Measure(new Size(330, 500));
        host.Arrange(new Rect(0, 0, 330, 500));
        host.UpdateLayout();
        if (body.ActualHeight < 499.99) throw new Exception("Collapsed footer did not release space.");
        Console.WriteLine("Estimate footer layout OK (no SQL).");
    }

    private static void Layout(EstimateTotalsCardsPanel panel, double width, double expectedHeight)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        if (Math.Abs(panel.DesiredSize.Height - expectedHeight) > 0.01)
            throw new Exception($"Footer height {panel.DesiredSize.Height}, expected {expectedHeight}.");
        panel.Arrange(new Rect(0, 0, width, expectedHeight));
    }

    private static void Slot(FrameworkElement card, double x, double y, double width, double height)
    {
        var actual = LayoutInformation.GetLayoutSlot(card);
        if (Math.Abs(actual.X - x) > 0.01 || Math.Abs(actual.Y - y) > 0.01 ||
            Math.Abs(actual.Width - width) > 0.01 || Math.Abs(actual.Height - height) > 0.01)
            throw new Exception($"Unexpected footer slot: {actual}; expected {new Rect(x, y, width, height)}.");
    }
}
