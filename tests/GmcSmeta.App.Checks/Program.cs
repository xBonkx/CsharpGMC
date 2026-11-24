using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using GmcSmeta.App;
using GmcSmeta.Core.Db;

/// <summary>
/// SQL Server-only WPF smoke checks. Read-only; never calls SaveCell/DeletePosition.
/// Requires GMC_SMETA_SQL_CONNECTION pointing to a synthetic pilot database.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            CheckEstimateUiSettingsRecovery();
            var cs = Environment.GetEnvironmentVariable("GMC_SMETA_SQL_CONNECTION");
            if (string.IsNullOrWhiteSpace(cs))
                throw new InvalidOperationException("Set GMC_SMETA_SQL_CONNECTION to the synthetic pilot database.");
            using (var probe = SqlServerDatabase.Open(cs))
                if (!probe.Database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("WPF checks require a GmcSmetaPilot_* database.");

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/GmcSmeta.App/App.xaml")))
                root = root.Parent;
            if (root is null) throw new FileNotFoundException("Cannot find App.xaml");
            var source = System.Xml.Linq.XDocument.Load(
                Path.Combine(root.FullName, "src/GmcSmeta.App/App.xaml")).Root!;
            var resources = new System.Xml.Linq.XElement(source.Name.Namespace + "ResourceDictionary",
                source.Attributes().Where(a => a.IsNamespaceDeclaration),
                source.Elements().Single().Elements());
            app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("ru-RU")));

            CheckComboBoxDisplayMemberPath();
            CheckCatalogPageSelection();
            CheckEstimateCalculatedColumns();
            CheckNumericPastePlanning();

            var window = new MainWindow();
            window.InitializeWorkspaceNavigation();
            var startupRoot = (UIElement)window.FindName("RootLayout");
            if (startupRoot.IsEnabled)
                throw new Exception("Workspace commands enabled before startup data was loaded.");
            // Start on a Dispatcher callback so async continuations return to the STA.
            var startup = Dispatcher.CurrentDispatcher.InvokeAsync<Task<bool>>(
                () => window.InitializeWorkspaceAsync(offerRetry: false)).Task.Unwrap();
            PumpUntil(() => startup.IsCompleted, "workspace initialization");
            if (!startup.GetAwaiter().GetResult() || !startupRoot.IsEnabled)
                throw new Exception("SQL Server workspace initialization did not complete.");
            var host = (ContentControl)window.FindName("PageHost");
            var tree = (TreeView)window.FindName("NavigationTree");
            if (tree.Items.Count == 0) throw new Exception("No SQL Server documents in navigation.");

            var firstDocument = FindDocument(tree.Items) ?? throw new Exception("No SQL Server document to inspect.");
            firstDocument.IsSelected = true;
            if (host.Content is not EstimateView page)
                throw new Exception("Document did not open in the SQL Server editor.");
            var grid = (DataGrid)page.FindName("EstimateGrid");
            PumpUntil(() => grid.Items.Count > 0, "estimate rows");
            foreach (var width in new[] { 1366d, 1920d })
            {
                var element = (FrameworkElement)window.Content;
                element.Measure(new Size(width, 820));
                element.Arrange(new Rect(0, 0, width, 820));
                element.UpdateLayout();
                if (grid.ActualWidth < 300) throw new Exception("Estimate grid is too narrow.");
                Console.WriteLine($"SQL Server WPF {width:0}px: {grid.Items.Count} rows");
            }
            foreach (var name in new[] { "AddSectionButton", "AddRowButton", "MoveUpButton",
                        "MoveDownButton", "DeletePositionButton" })
                if (page.FindName(name) is not Button)
                    throw new Exception($"Missing SQL Server editor button: {name}");

            var filter = (TextBox)page.FindName("EstimateSearch");
            var count = grid.Items.Count;
            filter.Text = "__not_found_123456789__";
            if (grid.Items.Count != 0) throw new Exception("Filtering failed.");
            filter.Clear();
            if (grid.Items.Count != count) throw new Exception("Filtering restore failed.");
            Console.WriteLine("SQL Server editor WPF smoke OK. No writes performed.");
            CheckSettingsMenu(window);
            window.Close();
            app.Shutdown();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void PumpUntil(Func<bool> complete, string operation)
    {
        if (complete()) return;
        var frame = new DispatcherFrame();
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        timer.Tick += (_, _) =>
        {
            if (complete() || timeout.Elapsed > TimeSpan.FromSeconds(60))
                frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!complete()) throw new TimeoutException("Timed out: " + operation);
    }

    /// <summary>
    /// Регрессия S-15: подменю «Настройки» содержит масштаб шрифта (FONT_SCALES эталона)
    /// и «Показать подсказки снова»; ApplyFontScale меняет LayoutTransform корневого layout.
    /// </summary>
    private static void CheckSettingsMenu(MainWindow window)
    {
        var top = (MenuItem)window.FindName("MainMenuItem")!;
        var service = top.Items.OfType<MenuItem>().First(m => (m.Header as string) == "Сервис");
        var settings = service.Items.OfType<MenuItem>().First(m => (m.Header as string) == "Настройки");
        var font = settings.Items.OfType<MenuItem>().First(m => (m.Header as string) == "Размер шрифта");
        var scales = font.Items.OfType<MenuItem>().Select(m => (double)m.Tag!).ToList();
        if (!scales.SequenceEqual(new[] { 1.0, 1.1, 1.2 }))
            throw new Exception("Font scale options do not match Python FONT_SCALES.");
        if (!settings.Items.OfType<MenuItem>().Any(m => (m.Header as string) == "Показать подсказки снова"))
            throw new Exception("Missing 'Показать подсказки снова'.");
        if (!service.Items.OfType<MenuItem>().Any(m => (m.Header as string) == "О программе"))
            throw new Exception("Missing 'О программе'.");

        window.ApplyFontScale(1.2);
        var root = (UIElement)window.FindName("RootLayout")!;
        if (root.LayoutTransform is not System.Windows.Media.ScaleTransform { ScaleX: 1.2, ScaleY: 1.2 })
            throw new Exception("Font scale did not apply ScaleTransform to the root layout.");
        window.ApplyFontScale(1.0);
        if (root.LayoutTransform is not null)
            throw new Exception("Font scale 1.0 must clear the transform.");
        Console.WriteLine("Settings menu and font scale OK.");
    }

    /// <summary>
    /// Регрессия S-04: кастомный шаблон ComboBox должен брать подпись из DisplayMemberPath,
    /// иначе закрытый список показывает ToString() элемента («ViewChoice { Code = ... }»).
    /// </summary>
    private static void CheckComboBoxDisplayMemberPath()
    {
        var combo = new ComboBox
        {
            Width = 220,
            DisplayMemberPath = "Label",
            ItemsSource = new[] { new LabeledChoice("kp", "Как в КП") },
            SelectedIndex = 0,
        };
        var host = new Border { Width = 300, Height = 60, Child = combo };
        host.Measure(new Size(300, 60));
        host.Arrange(new Rect(0, 0, 300, 60));
        host.UpdateLayout();
        var text = FirstTextBlock(combo);
        if (text != "Как в КП")
            throw new Exception($"ComboBox DisplayMemberPath is not applied, shows '{text}'");
        Console.WriteLine("ComboBox DisplayMemberPath OK: " + text);
    }

    /// <summary>S-20: invalid UI zoom and nullable lists must not break startup.</summary>
    private static void CheckEstimateUiSettingsRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "gmc-settings-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "estimate-ui.json");
            File.WriteAllText(path, """
                {"FontScale": 100, "TableZoom": -10, "StageModeKp": null,
                 "ColumnWidths": {"Нормальная": 125, "Слишком широкая": 90000},
                 "ColumnOrder": null, "HiddenColumns": null}
                """);
            var value = EstimateUiSettings.LoadFromPath(path);
            if (value.FontScale != 1.0 || value.TableZoom != 0.8 ||
                value.StageModeKp is null || value.ColumnOrder is null ||
                value.HiddenColumns is null || value.ColumnWidths.Count != 1 ||
                value.ColumnWidths["Нормальная"] != 125)
                throw new Exception("Invalid UI preferences were not normalized.");
            if (!File.Exists(path))
                throw new Exception("Valid JSON with invalid values must be retained.");

            File.WriteAllText(path, "{invalid-json");
            var fallback = EstimateUiSettings.LoadFromPath(path);
            if (fallback.FontScale != 1.0 || File.Exists(path) ||
                Directory.GetFiles(root, "estimate-ui.json.corrupt-*").Length != 1)
                throw new Exception("Corrupt UI JSON was not preserved separately.");
            Console.WriteLine("UI settings recovery OK.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>S-21: page selection must not touch items on another catalog page.</summary>
    private static void CheckCatalogPageSelection()
    {
        var first = new CatalogPickRow { Id = 1, Name = "Вентилятор" };
        var second = new CatalogPickRow { Id = 2, Name = "Воздуховод" };
        var otherPage = new CatalogPickRow { Id = 3, Name = "Решётка", Selected = true };
        CatalogPickerWindow.SetSelectedOnPage([first, second], true);
        if (!first.Selected || !second.Selected || !otherPage.Selected)
            throw new Exception("Select page affected incorrect catalog positions.");

        CatalogPickerWindow.SetSelectedOnPage([first, second], false);
        if (first.Selected || second.Selected || !otherPage.Selected)
            throw new Exception("Clear page changed saved selections from another page.");
        Console.WriteLine("Catalog page selection OK.");
    }

    /// <summary>S-23: typed qty/price/work must repaint calculated estimate cells.</summary>
    private static void CheckEstimateCalculatedColumns()
    {
        var source = new GmcSmeta.Core.Domain.EstimateRow(
            GmcSmeta.Core.Domain.RowKind.Position, 1, 1, 7, false, "1",
            "Монтаж вентиляции", "шт", 2m, 100m, 200m, null, false, "");
        var row = new EditableEstimateRow(source);
        var changed = new HashSet<string>();
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null) changed.Add(e.PropertyName);
        };

        row.QtyText = "3";
        if (row.DisplayAmountText != "300,00" ||
            !changed.Contains(nameof(EditableEstimateRow.DisplayAmountText)))
            throw new Exception("Changing quantity did not refresh the displayed amount.");

        changed.Clear();
        row.PriceText = "250";
        if (row.DisplayAmountText != "750,00" ||
            row.DisplayPriceText != "250" ||
            !changed.Contains(nameof(EditableEstimateRow.DisplayAmountText)) ||
            !changed.Contains(nameof(EditableEstimateRow.DisplayPriceText)))
            throw new Exception("Changing price did not refresh the displayed amount/price.");

        changed.Clear();
        row.PriceText = "0";
        if (row.DisplayPriceText != "—" || !row.HasNoPrice ||
            !changed.Contains(nameof(EditableEstimateRow.DisplayPriceText)) ||
            !changed.Contains(nameof(EditableEstimateRow.HasNoPrice)))
            throw new Exception("Zero price did not refresh the missing-price indicator.");

        changed.Clear();
        row.WorkText = "90";
        if (row.WorkDisplayText != "90" ||
            !changed.Contains(nameof(EditableEstimateRow.WorkDisplayText)))
            throw new Exception("Changing work price did not refresh the displayed work price.");
        Console.WriteLine("Estimate calculated column notifications OK.");
    }

    /// <summary>S-24: numeric paste mapping on a 2500-row synthetic estimate, no SQL.</summary>
    private static void CheckNumericPastePlanning()
    {
        static EditableEstimateRow Row(int id, int? parentId = null) =>
            new(new GmcSmeta.Core.Domain.EstimateRow(
                GmcSmeta.Core.Domain.RowKind.Position, parentId is null ? 1 : 2,
                id, id, parentId is not null, id.ToString(), "Строка " + id,
                "шт", 1m, 1m, 1m, null, false, "")
            { ParentPositionId = parentId, SystemId = 5 });

        var rows = Enumerable.Range(1, 2500).Select(i => Row(i)).ToList();
        rows.Insert(1, Row(9001, parentId: 1));
        var qty = EstimateView.PlanNumericPasteTargets(rows, 0, "qty", 2500);
        if (qty.Count != 2500 || qty[1].Id != 9001)
            throw new Exception("Quantity paste must include component rows.");
        var price = EstimateView.PlanNumericPasteTargets(rows, 0, "price", 2500);
        if (price.Count != 2500 || price[1].Id != 2)
            throw new Exception("Price paste must skip component rows.");
        if (EstimateView.PlanNumericPasteTargets(rows, -1, "price", 1).Count != 0 ||
            EstimateView.PlanNumericPasteTargets(rows, rows.Count, "qty", 1).Count != 0)
            throw new Exception("Stale current-row index must not create paste targets.");
        // From the last real row there is only one destination: the UI must refuse
        // a two-number paste rather than silently discarding the second value.
        var tail = EstimateView.PlanNumericPasteTargets(rows, rows.Count - 1, "qty", 2);
        if (tail.Count != 1)
            throw new Exception("Incomplete destination planning was not detected.");

        // One embedded empty clipboard row must neither shift prices nor
        // accidentally fall through to importing new estimate positions.
        if (EstimateView.TryParseNumberColumn("1\n\n2", out _) ||
            !EstimateView.NumericColumnHasGaps("1\n\n2\n") ||
            EstimateView.NumericColumnHasGaps("1\n2\n") ||
            EstimateView.NumericColumnHasGaps("1\tшт\n2\tм") ||
            !EstimateView.TryParseNumberColumn("1\n2\n", out var trailing) ||
            trailing.Count != 2 ||
            trailing[0] != 1m || trailing[1] != 2m)
            throw new Exception("Numeric clipboard parser must reject holes and allow final newline.");

        if (!EstimateView.TryParseNumberColumn("0,000000123\n1,23456789", out var values) ||
            values.Count != 2 || values[0] != 0.000000123m ||
            values[1].ToString(System.Globalization.CultureInfo.InvariantCulture) != "1.23456789" ||
            EstimateView.TryParseNumberColumn("1000000000001", out _) ||
            EstimateView.TryParseNumberColumn("-1", out _))
            throw new Exception("Numeric paste validation lost precision or accepted invalid values.");
        Console.WriteLine("2500-row numeric paste planning and precision OK.");
    }

    private sealed record LabeledChoice(string Code, string Label);

    private static string? FirstTextBlock(DependencyObject node)
    {
        if (node is TextBlock text) return text.Text;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            if (FirstTextBlock(System.Windows.Media.VisualTreeHelper.GetChild(node, i)) is { } found)
                return found;
        return null;
    }

    private static TreeViewItem? FindDocument(ItemCollection items)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            if (item.Tag is GmcSmeta.Core.Domain.DocRow) return item;
            var next = FindDocument(item.Items);
            if (next is not null) return next;
        }
        return null;
    }
}
