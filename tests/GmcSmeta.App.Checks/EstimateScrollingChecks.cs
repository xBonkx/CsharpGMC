using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using GmcSmeta.App;

internal static class EstimateScrollingChecks
{
    internal static void Run()
    {
        CheckThumb(Orientation.Vertical, 40);
        CheckThumb(Orientation.Horizontal, 34);

        var grid = new DataGrid
        {
            AutoGenerateColumns = false, CanUserAddRows = false,
            EnableRowVirtualization = true, EnableColumnVirtualization = true,
            RowHeight = 27,
        };
        // A real host initializes theme templates and resource lookup, as in the app.
        var host = new Window { Content = grid, Width = 800, Height = 500, ShowInTaskbar = false, ShowActivated = false };
        host.Show();
        try
        {
            grid.Columns.Add(new DataGridTextColumn { Binding = new Binding("."), Width = 260 });
            VirtualizingPanel.SetIsVirtualizing(grid, true);
            VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(grid, ScrollUnit.Pixel);
            ScrollViewer.SetCanContentScroll(grid, true);
            ScrollViewer.SetIsDeferredScrollingEnabled(grid, false);
            foreach (var count in new[] { 0, 30, 300, 1000, 2499, 2500, 10000, 25000, 300 })
            {
                grid.ItemsSource = Enumerable.Range(1, count).ToArray();
                Layout(grid, new Size(800, 500));
                Require(!ScrollViewer.GetIsDeferredScrollingEnabled(grid),
                    $"Wrong live scrolling policy for {count} rows.");
                var viewer = grid.Template?.FindName("DG_ScrollViewer", grid) as ScrollViewer;
                Require(viewer is not null && !viewer.IsDeferredScrollingEnabled,
                    $"DataGrid template did not forward live scrolling policy for {count} rows.");
                if (count > 30)
                {
                    var offset = viewer!.ScrollableHeight / 2;
                    ScrollBar.ScrollToVerticalOffsetCommand.Execute(offset, viewer);
                    grid.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    grid.UpdateLayout();
                    Require(viewer.ContentVerticalOffset > 0 &&
                        Math.Abs(viewer.ContentVerticalOffset - viewer.VerticalOffset) < 1,
                        $"Visible rows did not follow scrollbar offset for {count} rows.");
                    var realized = Enumerable.Range(0, count)
                        .Where(i => grid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow).ToArray();
                    Require(realized.Length > 0 && realized.Length < 100 && realized[0] > 0,
                        $"Viewport did not advance with bounded row containers for {count} rows.");
                    Console.WriteLine($"Live scroll {count}: offset={viewer.ContentVerticalOffset:F0}, realized={realized.Length}.");
                }
                Require(grid.EnableRowVirtualization && grid.EnableColumnVirtualization &&
                    VirtualizingPanel.GetVirtualizationMode(grid) == VirtualizationMode.Recycling,
                    "Scrolling policy disabled virtualization.");
            }
        }
        finally { host.Close(); }
        Console.WriteLine("Estimate scroll thumb geometry and all-size live scrolling policy checks passed (no SQL).");
    }

    private static void CheckThumb(Orientation orientation, double minimum)
    {
        var vertical = orientation == Orientation.Vertical;
        var bar = new ScrollBar
        {
            Orientation = orientation,
            Minimum = 0, Maximum = 250000, ViewportSize = 500, Value = 125000,
            Style = (Style)Application.Current.FindResource(typeof(ScrollBar)),
        };
        Layout(bar, vertical ? new Size(11, 500) : new Size(500, 11));
        var track = bar.Template.FindName("PART_Track", bar) as Track;
        Require(track?.Thumb is not null, "Missing scrollbar track or thumb.");
        var length = vertical ? track!.Thumb.ActualHeight : track!.Thumb.ActualWidth;
        var trackLength = vertical ? track.ActualHeight : track.ActualWidth;
        Require(length >= minimum && length < trackLength,
            $"Invalid {orientation} thumb geometry: {length} of {trackLength}.");
        var travel = trackLength - length;
        var fullRange = vertical ? track.ValueFromDistance(0, travel) : track.ValueFromDistance(travel, 0);
        Require(Math.Abs(Math.Abs(fullRange) - bar.Maximum) < 1,
            "Thumb length and track-to-value mapping disagree.");
    }

    private static void Layout(FrameworkElement element, Size size)
    {
        // Flush template-parent bindings before checking the inner viewer.
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        element.Measure(size);
        element.Arrange(new Rect(new Point(), size));
        element.UpdateLayout();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
