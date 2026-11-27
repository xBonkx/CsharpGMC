using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using GmcSmeta.App;
using GmcSmeta.Core.Domain;

/// <summary>Production estimate templates, synthetic rows, no SQL or global input/focus.</summary>
internal static class BackgroundEstimateChecks
{
    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        CaptureSelection(output);
        var results = new List<object>();
        foreach (var count in new[] { 300, 2500, 10000, 25000 })
        foreach (var scale in new[] { 1d, 1.25, 1.5 })
        {
            var page = LoadEstimateGrid();
            page.LayoutTransform = new ScaleTransform(scale, scale);
            var grid = (DataGrid)page.FindName("EstimateGrid");
            var rows = Enumerable.Range(1, count).Select(id =>
                new EditableEstimateRow(new EstimateRow(RowKind.Position, 1, id, id,
                    false, id.ToString(), "Вентилятор канальный прямоугольный, длинное наименование позиции " + id,
                    "компл", 2m, 12345.67m, 24691.34m, 1500m, false, "Проверка прокрутки")
                { SystemId = 1 })).ToArray();
            grid.ItemsSource = rows;
            // Off-screen and non-activating: do not steal the user's keyboard or mouse.
            var host = new Window
            {
                Content = page, Width = 1065, Height = 720, Left = -30000, Top = -30000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
            };
            host.Show();
            try
            {
                Flush(grid);
                var viewer = Find<ScrollViewer>(grid).First();
                Require(!viewer.IsDeferredScrollingEnabled, "Production estimate defers visible rows.");
                var latencies = new List<double>();
                var maxRealized = 0;
                for (var step = 0; step < 36; step++)
                {
                    // Warm up recycling/JIT, then sample continuous movement across nearby rows.
                    var desired = step < 12 ? viewer.ScrollableHeight * ((step * 7 % 11) / 11d) :
                        viewer.ScrollableHeight / 2 + (step - 24) * 54;
                    var watch = Stopwatch.StartNew();
                    ScrollBar.ScrollToVerticalOffsetCommand.Execute(desired, viewer);
                    Flush(grid);
                    watch.Stop();
                    if (step >= 12) latencies.Add(watch.Elapsed.TotalMilliseconds);
                    Require(Math.Abs(viewer.ContentVerticalOffset - viewer.VerticalOffset) < 1,
                        "Content offset trails scrollbar position.");
                    var realized = Find<DataGridRow>(grid).Where(r => r.IsVisible).ToArray();
                    maxRealized = Math.Max(maxRealized, realized.Length);
                    Require(realized.Length is > 0 and < 150, "Estimate virtualization lost its bounded viewport.");
                    Require(realized.Any(r => r.Item is EditableEstimateRow), "Visible rows lost their data.");
                }
                // Scroll back to the middle and capture the actual production cells.
                viewer.ScrollToVerticalOffset(viewer.ScrollableHeight / 2);
                Flush(grid);
                if (count == 2500)
                    Capture(page, Path.Combine(output, $"estimate-{scale:0.##}.png"));
                var ordered = latencies.Order().ToArray();
                double Percentile(double percentile) => ordered[(int)Math.Ceiling((ordered.Length - 1) * percentile)];
                results.Add(new { count, scale, maxRealized, p50Ms = Percentile(.50), p95Ms = Percentile(.95), p99Ms = Percentile(.99) });
                Console.WriteLine($"Production grid: {count} rows, scale {scale}, containers {maxRealized}, p95 {Percentile(.95):F1} ms.");
            }
            finally { host.Close(); }
        }
        File.WriteAllText(Path.Combine(output, "scroll-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Background production estimate checks passed; no SQL or user input used.");
    }

    private static UserControl LoadEstimateGrid()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/GmcSmeta.App/EstimateView.xaml"))) root = root.Parent;
        if (root is null) throw new FileNotFoundException("EstimateView.xaml");
        var doc = XDocument.Load(Path.Combine(root.FullName, "src/GmcSmeta.App/EstimateView.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var grid = doc.Descendants(p + "DataGrid").Single(e => (string?)e.Attribute(x + "Name") == "EstimateGrid");
        var resources = doc.Root!.Element(p + "UserControl.Resources")!;
        var fixture = new XElement(p + "UserControl", doc.Root.Attributes().Where(a => a.IsNamespaceDeclaration),
            new XElement(resources), new XElement(grid));
        // Keep production resources/styles/bindings; remove only code-behind commands and
        // handlers that would need a live document. No duplicate hand-written cell templates.
        foreach (var setter in fixture.Descendants(p + "EventSetter").ToArray()) setter.Remove();
        foreach (var attribute in fixture.Descendants().Attributes().Where(a =>
                     a.Name.Namespace == XNamespace.None && Regex.IsMatch(a.Value, @"^[A-Za-z]\w*_[A-Za-z]\w*$")).ToArray())
            attribute.Remove();
        var markup = fixture.ToString().Replace("clr-namespace:GmcSmeta.App\"", "clr-namespace:GmcSmeta.App;assembly=" + typeof(EstimateView).Assembly.GetName().Name + "\"");
        return (UserControl)XamlReader.Parse(markup);
    }

    private static void CaptureSelection(string output)
    {
        var editor = new TextBox
        {
            Text = "Выделенный текст поставщика 12345",
            Style = (Style)Application.Current.FindResource("PythonGridEditor"),
            IsInactiveSelectionHighlightEnabled = true, Width = 500, Height = 36
        };
        var host = new Window { Content = editor, Width = 510, Height = 46, Left = -30000, Top = -30000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.Show();
        try
        {
            editor.SelectAll();
            Flush(editor);
            Capture(editor, Path.Combine(output, "selected-editor.png"));
        }
        finally { host.Close(); }
    }

    private static void Flush(FrameworkElement element)
    {
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        element.UpdateLayout();
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }

    private static void Capture(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
