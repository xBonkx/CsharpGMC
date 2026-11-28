using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using GmcSmeta.App;
using GmcSmeta.Core.Domain;

internal static class LayoutPerformanceChecks
{
    // Dedicated, opt-in Windows STA check: real compiled panels and application
    // styles, synthetic rows only; no SQL, no mutation or export of user data.
    internal static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "layout-performance.csv");
        using var output = new StreamWriter(path);
        output.WriteLine("case,requested_items,viewport_width,viewport_height,dpi_x,dpi_y,phase,elapsed_ms,realized_rows,realized_cells,visual_nodes,scroll_offset,status,iteration");
        var iteration = 0;
        var failures = 0;
        var materials = new EstimateMaterialsPanel();
        var summary = new EstimateSummaryPanel();
        GridCase("materials", materials, (DataGrid)materials.FindName("MaterialsGrid"),
            count => Enumerable.Range(1, count).Select(i => new EstimateView.MaterialsRow
            {
                PositionId = i, Num = i.ToString(), Name = "Синтетический материал " + i,
                Unit = "шт", QtyText = "1", PriceText = "100", AmountText = "100",
                CostText = "80", CostSumText = "80"
            }).ToArray());
        GridCase("summary", summary, (DataGrid)summary.FindName("SummaryGrid"),
            count => Enumerable.Range(1, count).Select(i => new EstimateView.SummaryRow
            {
                PositionId = i, Name = "Синтетическая позиция " + i, Unit = "шт",
                QtyText = "1", CostUnitText = "80", PriceUnitText = "100", MCostText = "80", MSellText = "100"
            }).ToArray());
        var document = new DataGrid
        {
            Style = (Style)Application.Current.FindResource("DocumentTable"),
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
        };
        for (var i = 0; i < 30; i++)
            document.Columns.Add(new DataGridTextColumn
            {
                Header = "Колонка " + i, Binding = new Binding("."), Width = 140
            });
        document.FrozenColumnCount = 1;
        GridCase("document_table_wide", document, document,
            count => Enumerable.Range(1, count).Select(i => "Позиция " + i).ToArray());

        CardCase();
        output.Flush();
        Console.WriteLine("Layout evidence: " + path);
        if (failures != 0)
            throw new InvalidOperationException($"Layout performance checks: {failures} failed viewport assertions.");

        void GridCase(string name, FrameworkElement content, DataGrid grid,
            Func<int, System.Collections.IEnumerable> createRows)
        {
            var host = new Window
            {
                Content = content, Width = 1024, Height = 640,
                ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20
            };
            host.Show();
            try
            {
                foreach (var count in new[] { 300, 2500, 10000, 25000 })
                {
                    var items = createRows(count);
                    // Warm-up plus 30 samples on 2500+ rows; retain each raw sample.
                    for (iteration = 0; iteration <= (count >= 2500 ? 30 : 1); iteration++)
                    {
                    var watch = Stopwatch.StartNew();
                    grid.ItemsSource = items;
                    // Reset a prior scroll position even if a template kept its viewer.
                    grid.ScrollIntoView(grid.Items[0], grid.Columns[0]);
                    Flush(host);
                    watch.Stop();
                    var viewer = FindViewer(grid);
                    RecordGrid(name, count, "top", watch.Elapsed.TotalMilliseconds, viewer,
                        grid.ItemContainerGenerator.ContainerFromIndex(0) is DataGridRow);
                    watch.Restart();
                    grid.ScrollIntoView(grid.Items[count - 1], grid.Columns[grid.Columns.Count - 1]);
                    Flush(host);
                    watch.Stop();
                    viewer = FindViewer(grid);
                    RecordGrid(name, count, "last", watch.Elapsed.TotalMilliseconds, viewer,
                        grid.ItemContainerGenerator.ContainerFromIndex(count - 1) is DataGridRow);
                    grid.ItemsSource = null;
                    Flush(host);
                    }
                }
            }
            finally { host.Close(); }

            void RecordGrid(string area, int requested, string phase, double elapsed,
                ScrollViewer? viewer, bool targetRealized)
            {
                var counts = CountVisuals(grid);
                var bounded = counts.Rows > 0 && counts.Rows < Math.Min(requested, 200);
                var pass = targetRealized && bounded && viewer is not null &&
                    viewer.CanContentScroll && grid.EnableRowVirtualization &&
                    grid.EnableColumnVirtualization;
                if (!pass) failures++;
                Write(area, requested, grid, viewer, phase, elapsed, counts.Rows, counts.Cells,
                    counts.Nodes, viewer?.VerticalOffset ?? 0, pass ? "PASS" : "FAIL");
            }
        }

        void CardCase()
        {
            iteration = 0;
            // MainWindow's constructor prepares a shell, but only the explicit
            // InitializeWorkspaceAsync call connects to SQL. Never call it here.
            // _ready stays false, so OnClosed does not persist window geometry.
            var host = new MainWindow
            {
                WindowState = WindowState.Normal, Width = 1366, Height = 768,
                ShowInTaskbar = false, ShowActivated = false
            };
            var page = (FrameworkElement)host.FindName("ObjectPage");
            var pageHost = (ContentControl)host.FindName("PageHost");
            var cards = (ItemsControl)host.FindName("DocumentCards");
            var viewer = (ScrollViewer)host.FindName("ObjectCardsScroll");
            pageHost.Content = page;
            viewer.Visibility = Visibility.Visible;
            host.Show();
            try
            {
                foreach (var count in new[] { 10, 50, 200 })
                {
                    var data = Enumerable.Range(1, count).Select(SyntheticCard).ToArray();
                    var items = ObjectCardLayout.AppendNewContract(data, canCreate: false);
                    var last = ObjectCardLayout.PageRange(items.Count, int.MaxValue);
                    // Warm-up and 30 measured complete forward/back traversals.
                    // Distinct phases keep middle-page and return timings separate.
                    for (iteration = 0; iteration <= 30; iteration++)
                    {
                        for (var pageIndex = 0; pageIndex <= last.Page; pageIndex++)
                            Visit(pageIndex, pageIndex == 0 ? "top" : pageIndex == last.Page ? "bottom" : "page_" + pageIndex);
                        if (last.Page == 0) Visit(0, "bottom");
                        for (var pageIndex = last.Page - 1; pageIndex >= 0; pageIndex--)
                            Visit(pageIndex, "back_" + pageIndex);
                        // Request the same immutable snapshot and page again.
                        var sameSource = cards.ItemsSource;
                        host.PresentObjectCardItems(items, 0, true);
                        if (!ReferenceEquals(sameSource, cards.ItemsSource)) failures++;
                    }
                    void Visit(int pageIndex, string phase)
                    {
                        var expected = ObjectCardLayout.PageRange(items.Count, pageIndex);
                        var watch = Stopwatch.StartNew();
                        host.PresentObjectCardItems(items, pageIndex, true);
                        if (phase == "bottom") viewer.ScrollToBottom();
                        Flush(host);
                        watch.Stop();
                        var realized = 0;
                        for (var i = 0; i < cards.Items.Count; i++)
                            if (cards.ItemContainerGenerator.ContainerFromIndex(i) is not null) realized++;
                        var counts = CountVisuals(cards);
                        var correctItems = cards.Items.Count == expected.Count &&
                            Enumerable.Range(0, expected.Count).All(i => ReferenceEquals(cards.Items[i], items[expected.First + i]));
                        var creationCount = cards.Items.OfType<NewContractCardItem>().Count();
                        var valid = correctItems && creationCount == (expected.Page == last.Page ? 1 : 0) &&
                            realized > 0 && realized <= ObjectCardLayout.CardsPerPage &&
                            viewer.ViewportWidth > 0 && viewer.ViewportHeight > 0;
                        if (!valid) failures++;
                        Write("object_cards", count + 1, cards, viewer, phase, watch.Elapsed.TotalMilliseconds,
                            realized, 0, counts.Nodes, viewer.VerticalOffset, valid ? "PASS" : "FAIL");
                    }
                    host.PresentObjectCardItems(Array.Empty<object>(), 0, true);
                    Flush(host);
                }
            }
            finally { host.Close(); }
        }

        void Write(string name, int count, FrameworkElement element, ScrollViewer? viewer, string phase,
            double elapsed, int rows, int cells, int nodes, double offset, string status)
        {
            var dpi = VisualTreeHelper.GetDpi(element);
            output.WriteLine(FormattableString.Invariant(
                $"{name},{count},{viewer?.ViewportWidth ?? element.ActualWidth:F2},{viewer?.ViewportHeight ?? element.ActualHeight:F2},{dpi.PixelsPerInchX:F2},{dpi.PixelsPerInchY:F2},{phase},{elapsed:F4},{rows},{cells},{nodes},{offset:F2},{status},{iteration}"));
            output.Flush();
        }
    }

    private static ObjectDocumentCard SyntheticCard(int id)
    {
        var kind = id % 2 == 0 ? "main" : "kp";
        var title = "Тестовый документ " + id + (id % 3 == 0 ? " — длинное название для проверки переноса" : "");
        var doc = new DocRow(id, 1, kind, kind == "main" ? "Договор" : "КП",
            title, title, "Т-" + id, "2026-11-28", false, 1000m, 500m, 1500m, 20);
        var totals = new ContractTotals(id, kind, "", 22m, 1000m, 500m, 1500m, 1500m, 0m, 20);
        var estimate = new EstimateCardData("Тестовый контрагент", "", 22m, false, 2, 20,
            totals, new EstimateStatistics(1000m, 500m, 0));
        return new ObjectDocumentCard(doc, estimate, "", id % 2 == 0 ? "Тест" : "");
    }

    private static ScrollViewer? FindViewer(DataGrid grid) =>
        grid.Template?.FindName("DG_ScrollViewer", grid) as ScrollViewer ??
        Descendants(grid).OfType<ScrollViewer>().FirstOrDefault();

    private static (int Nodes, int Rows, int Cells) CountVisuals(DependencyObject root)
    {
        int nodes = 0, rows = 0, cells = 0;
        foreach (var child in Descendants(root))
        {
            nodes++;
            if (child is DataGridRow) rows++;
            if (child is DataGridCell) cells++;
        }
        return (nodes, rows, cells);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            yield return item;
            for (var i = VisualTreeHelper.GetChildrenCount(item) - 1; i >= 0; i--)
                stack.Push(VisualTreeHelper.GetChild(item, i));
        }
    }

    private static void Flush(Window host)
    {
        host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
        host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
    }
}
