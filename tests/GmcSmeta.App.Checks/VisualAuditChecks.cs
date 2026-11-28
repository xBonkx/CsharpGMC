using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GmcSmeta.App;

/// <summary>Offline visual evidence from the real WPF resource dictionary.
/// Uses synthetic fixtures only. Does not open a database or user documents.</summary>
internal static class VisualAuditChecks
{
    internal static void RunLayoutChecks()
    {
        RunNavigationGlyphChecks();
        var panel = new ResponsiveToolbarPanel();
        var left = new WrapPanel();
        var right = new WrapPanel();
        left.Children.Add(new Border { Width = 180, Height = 32 });
        right.Children.Add(new Border { Width = 160, Height = 32 });
        panel.Children.Add(left);
        panel.Children.Add(right);
        Layout(panel, 600, 100);
        Require(LayoutInformation.GetLayoutSlot(right).Top == 0,
            "Wide toolbar must keep both groups on the same row.");
        Layout(panel, 260, 100);
        Require(LayoutInformation.GetLayoutSlot(right).Top >= 32,
            "Narrow toolbar must move the right group to a second row.");
        Layout(panel, 600, 100);
        Require(LayoutInformation.GetLayoutSlot(right).Top == 0,
            "Toolbar must restore one row when width returns.");
        Console.WriteLine("Responsive toolbar measured at narrow and wide sizes (no SQL).");
    }

    private static void RunNavigationGlyphChecks()
    {
        var pen = new Pen(Brushes.Black, 1.9)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        // Independent SVG contours must retain their absolute starts. Bounds
        // alone cannot catch a disconnected, but still in-viewBox, lower layer.
        var fixtures = new (string Key, Point[] Starts)[]
        {
            ("GlyphLayers", new[] { new Point(12, 3), new Point(3, 13) }),
            ("GlyphStage", new[] { new Point(6, 4), new Point(15, 11) }),
            ("GlyphChevronLeft", new[] { new Point(11, 6), new Point(19, 6) }),
            ("GlyphMenu", new[] { new Point(4, 6), new Point(4, 12), new Point(4, 18) }),
            ("GlyphHistory", new[] { new Point(3, 12), new Point(3, 3), new Point(12, 7) }),
            ("GlyphFolder", new[] { new Point(3, 7) }),
            ("GlyphFile", new[] { new Point(14, 3), new Point(14, 3), new Point(9, 13), new Point(9, 17) }),
            ("GlyphSearch", new[] { new Point(18, 11), new Point(20, 20) }),
        };
        foreach (var (key, starts) in fixtures)
        {
            var geometry = (PathGeometry)Application.Current.FindResource(key);
            Require(geometry.Figures.Select(f => f.StartPoint).SequenceEqual(starts),
                key + ": independent contours shifted or missing.");
            var bounds = geometry.GetRenderBounds(pen);
            Require(!bounds.IsEmpty && bounds.Left >= -0.01 && bounds.Top >= -0.01 &&
                bounds.Right <= 24.01 && bounds.Bottom <= 24.01,
                key + ": painted geometry outside the 24x24 SVG viewBox: " + bounds);
            if (key == "GlyphMenu")
                Require(Math.Abs(bounds.Width - 17.9) < 0.1 && Math.Abs(bounds.Height - 13.9) < 0.1,
                    "Hamburger painted bounds differ from the original three lines.");
        }
        Console.WriteLine("Navigation glyph contour starts and stroked bounds checked (no SQL).");
    }

    internal static void Export(string directory)
    {
        var output = Path.GetFullPath(directory);
        Directory.CreateDirectory(output);
        RunLayoutChecks();
        VisualDifferenceChecks.SelfCheck();
        ExportMenuFixtures(output);
        // These are simulated DIP viewports, not changes to Windows display settings.
        foreach (var (width, height) in new[] { (1366, 768), (1920, 1080), (2560, 1440) })
        {
            foreach (var scale in new[] { 1.0, 1.1, 1.2 })
            {
                var root = CreateTotalsFixture();
                if (scale != 1.0) root.LayoutTransform = new ScaleTransform(scale, scale);
                var host = new Border { Child = root, Background = Brushes.White };
                Layout(host, width, height);
                // Keep the original 100% file names for existing evidence consumers.
                var suffix = scale == 1.0 ? "" : $"-scale{scale * 100:0}";
                Capture(host, output, $"totals-{width}x{height}{suffix}", 96);
            }
        }
        var narrow = CreateTotalsFixture();
        Layout(narrow, 640, 320);
        Capture(narrow, output, "totals-narrow-640x320", 96);

        var catalog = CreateIconCatalog();
        var catalogHeight = catalog.Children.Count * 42 + 24;
        Layout(catalog, 960, catalogHeight);
        foreach (var dpi in new[] { 96, 120, 144, 192 })
            Capture(catalog, output, $"icons-raster-{dpi}dpi", dpi);

        Console.WriteLine("Visual audit written to " + output);
        Console.WriteLine("Synthetic offscreen layout only; actual monitor DPI and interactive states still require Windows acceptance.");
    }

    private static void ExportMenuFixtures(string output)
    {
        foreach (var scale in new[] { 1.0, 1.1, 1.2 })
        {
            var menu = MenuBehaviorChecks.CreateFixture();
            menu.LayoutTransform = new ScaleTransform(scale, scale);
            var border = new Border { Background = Brushes.White, Child = menu };
            Layout(border, 640, 480);
            foreach (var dpi in new[] { 96, 120, 144, 192 })
                Capture(border, output, $"menu-scale{scale * 100:0}-raster{dpi}dpi", dpi);
        }
    }

    private static FrameworkElement CreateTotalsFixture()
    {
        var panel = new EstimateTotalsPanel();
        var values = new Dictionary<string, string>
        {
            ["TotalsTileCostValue"] = "12 345 678,90",
            ["TotalsTileSellValue"] = "19 876 543,21",
            ["TotalsTileMarginValue"] = "7 530 864,31  ·  61,0%"
        };
        foreach (var (name, value) in values)
            ((TextBlock)panel.FindName(name)).Text = value;
        var grid = (DataGrid)panel.FindName("TotalsGrid");
        grid.ItemsSource = new[]
        {
            Row("Материалы", "1 234 567,89", "2 345 678,90"),
            Row("СМР (монтаж)", "123 456,78", "234 567,89"),
            Row("ПНР (пусконаладка)", "", "", editable: true),
            Row("Транспортные", "12 345,67", "23 456,78", editable: true),
            Row("Расходные материалы", "1 234,56", "—", editable: true),
            Row("Прочие расходы", "", "—", editable: true),
            Row("Непредвиденные", "", "—", editable: true),
            new EstimateView.TotalsRow { Kind = "total", Name = "ИТОГО (без НДС)", CostText = "12 345 678,90", SellText = "19 876 543,21" },
            new EstimateView.TotalsRow { Kind = "vat", Name = "НДС 20% (сверху)", SellText = "3 975 308,64" },
            new EstimateView.TotalsRow { Kind = "grand", Name = "ИТОГО с НДС", SellText = "23 851 851,85" }
        };
        return new Border { Background = new SolidColorBrush(Color.FromRgb(244, 246, 250)), Child = panel };
    }

    private static EstimateView.TotalsRow Row(string name, string cost, string sell, bool editable = false) =>
        new() { Name = name, CostText = cost, SellText = sell, CostEditable = editable, SellEditable = editable && sell != "—" };

    private static StackPanel CreateIconCatalog()
    {
        var keys = ResourceKeys(Application.Current.Resources)
            .OfType<string>()
            .Where(k => k.StartsWith("IconLabel", StringComparison.Ordinal) || k.StartsWith("IconOnly", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Require(keys.Length >= 37, "Expected the shared production icon templates.");
        var panel = new StackPanel { Background = Brushes.White, Margin = new Thickness(12) };
        foreach (var key in keys)
        {
            var template = (DataTemplate)Application.Current.FindResource(key);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 42 };
            row.Children.Add(new TextBlock { Text = key, Width = 240, VerticalAlignment = VerticalAlignment.Center });
            foreach (var enabled in new[] { true, false })
                row.Children.Add(new Button
                {
                    Content = enabled ? "Обычная" : "Недоступна", ContentTemplate = template,
                    Style = (Style)Application.Current.FindResource("PythonToolbarButton"),
                    IsEnabled = enabled, Width = 210, Margin = new Thickness(4)
                });
            row.Children.Add(new ToggleButton
            {
                Content = "Активная", ContentTemplate = template, IsChecked = true,
                Style = (Style)Application.Current.FindResource("PythonToolToggle"),
                Width = 210, Margin = new Thickness(4)
            });
            panel.Children.Add(row);
        }
        return panel;
    }

    private static IEnumerable<object> ResourceKeys(ResourceDictionary dictionary)
    {
        foreach (var key in dictionary.Keys) yield return key;
        foreach (var child in dictionary.MergedDictionaries)
            foreach (var key in ResourceKeys(child)) yield return key;
    }

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        root.UpdateLayout();
    }

    private static void Capture(FrameworkElement root, string directory, string name, int dpi)
    {
        // Render at requested raster density; WPF still uses the host's actual DPI for layout.
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(root.ActualWidth * dpi / 96d)),
            Math.Max(1, (int)Math.Ceiling(root.ActualHeight * dpi / 96d)),
            dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(directory, name + ".png")))
            encoder.Save(stream);

        using var writer = new StreamWriter(Path.Combine(directory, name + ".tsv"));
        writer.WriteLine("path\ttype\tname\tvisibility\tx\ty\twidth\theight\tdesiredWidth\tdesiredHeight\tmargin\tpadding\tfontSize\tfontFamily\tactualDpiX\tactualDpiY");
        Dump(root, root, "0", writer);
    }

    private static void Dump(Visual visual, Visual root, string path, TextWriter writer)
    {
        if (visual is FrameworkElement element)
        {
            var offset = ReferenceEquals(visual, root) ? new Point() :
                visual.TransformToAncestor(root).Transform(new Point());
            var padding = element switch
            {
                Control control => control.Padding.ToString(),
                Border border => border.Padding.ToString(),
                TextBlock text => text.Padding.ToString(),
                _ => ""
            };
            var fontSize = (double)element.GetValue(TextElementFontSize);
            var font = (FontFamily)element.GetValue(TextElementFontFamily);
            var dpi = VisualTreeHelper.GetDpi(visual);
            writer.WriteLine(FormattableString.Invariant(
                $"{path}\t{element.GetType().Name}\t{element.Name}\t{element.Visibility}\t{offset.X:0.##}\t{offset.Y:0.##}\t{element.ActualWidth:0.##}\t{element.ActualHeight:0.##}\t{element.DesiredSize.Width:0.##}\t{element.DesiredSize.Height:0.##}\t{element.Margin.ToString()}\t{padding}\t{fontSize:0.##}\t{font.Source}\t{dpi.PixelsPerInchX:0.##}\t{dpi.PixelsPerInchY:0.##}"));
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
            if (VisualTreeHelper.GetChild(visual, i) is Visual child)
                Dump(child, root, path + "/" + i, writer);
    }

    private static readonly DependencyProperty TextElementFontSize = System.Windows.Documents.TextElement.FontSizeProperty;
    private static readonly DependencyProperty TextElementFontFamily = System.Windows.Documents.TextElement.FontFamilyProperty;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
