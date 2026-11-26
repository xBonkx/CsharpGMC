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
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--column-menu-check")
            {
                ColumnMenuChecks.Run();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--legacy-summary-check")
            {
                LegacySummaryChecks.Run();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--offline-check")
            {
                var offline = CreateApplication();
                RunOfflineChecks();
                Console.WriteLine("Offline WPF checks passed (no SQL).");
                offline.Shutdown();
                return 0;
            }
            var app = CreateApplication();
            RunOfflineChecks();
            var cs = Environment.GetEnvironmentVariable("GMC_SMETA_SQL_CONNECTION");
            if (string.IsNullOrWhiteSpace(cs))
                throw new InvalidOperationException("Set GMC_SMETA_SQL_CONNECTION to the synthetic pilot database.");
            using (var probe = SqlServerDatabase.Open(cs))
                if (!probe.Database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("WPF checks require a GmcSmetaPilot_* database.");

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
    /// Builds the real App.xaml resource dictionary and ru-RU language override so the
    /// WPF checks see production templates. Shared by the offline and SQL modes.
    /// </summary>
    private static Application CreateApplication()
    {
        // Load the app assembly up front: the merged dictionary pack URI needs it resolved.
        var appAssembly = typeof(MainWindow).Assembly.GetName().Name!;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/GmcSmeta.App/App.xaml")))
            root = root.Parent;
        if (root is null) throw new FileNotFoundException("Cannot find App.xaml");
        var appXml = System.Xml.Linq.XDocument.Load(
            Path.Combine(root.FullName, "src/GmcSmeta.App/App.xaml")).Root!;
        var appDictionary = appXml.Descendants()
            .First(e => e.Name.LocalName == "ResourceDictionary");
        // Relative merged-dictionary sources would resolve against this checks assembly,
        // so point them at the real GmcSmeta.App resource assembly.
        foreach (var dictionary in appDictionary.DescendantsAndSelf()
                     .Where(e => e.Name.LocalName == "ResourceDictionary"))
        {
            var sourceAttribute = dictionary.Attribute("Source");
            if (sourceAttribute is null) continue;
            var value = sourceAttribute.Value.Trim();
            if (value.Length == 0 || value.StartsWith('/')) continue;
            sourceAttribute.Value = "/" + appAssembly + ";component/" + value.TrimStart('/');
        }
        var resources = new System.Xml.Linq.XElement(appDictionary.Name,
            appXml.Attributes().Where(a => a.IsNamespaceDeclaration),
            appDictionary.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("ru-RU")));
        return app;
    }

    /// <summary>
    /// Every WPF check that needs no database. CI runs this mode on windows-latest so the
    /// App project is not the only thing compiled and these regressions cannot rot silently.
    /// </summary>
    private static void RunOfflineChecks()
    {
        CheckEstimateUiSettingsRecovery();
        CheckComboBoxDisplayMemberPath();
        CheckCatalogPageSelection();
        CheckEstimateCalculatedColumns();
        CheckEstimateRowViewIndex();
        EstimateRowIndexReadbackChecks.Run();
        CheckEstimateVisibleRowPresentation();
        EstimateRowReadbackChecks.Run();
        EstimateGridRowUpdateChecks.Run();
        CheckProcurementUnchangedDisplay();
        CheckShipmentTabReuse();
        CheckSignedActTabReuse();
        CheckKsReferenceHighlights();
        KsRowNotificationChecks.Run();
        CheckSummaryTableFrame();
        SummaryColumnFitChecks.Run();
        CheckNumericPastePlanning();
        ColumnMenuChecks.Run();
        LegacySummaryChecks.Run();
        SummaryRowSequenceChecks.Run();
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
        var root = (FrameworkElement)window.FindName("RootLayout")!;
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
    private static void CheckSummaryTableFrame()
    {
        var frame = new SummaryTableFrame { CornerRadius = new CornerRadius(9) };
        frame.Measure(new Size(200, 100));
        frame.Arrange(new Rect(0, 0, 200, 100));
        frame.UpdateRoundedClip();
        if (frame.Clip is not System.Windows.Media.RectangleGeometry clip ||
            clip.Rect.Width != 200 || clip.Rect.Height != 100 || clip.RadiusX != 9 ||
            clip.FillContains(new Point(0, 0)) || !clip.FillContains(new Point(100, 50)))
            throw new Exception("Summary table rounded clip does not match its bounds.");
        frame.Measure(new Size(400, 150));
        frame.Arrange(new Rect(0, 0, 400, 150));
        frame.UpdateRoundedClip();
        if (frame.Clip.Bounds.Width != 400 || frame.Clip.Bounds.Height != 150)
            throw new Exception("Summary table clip was not resized.");
        var grouped = new EstimateView.SummaryRow { Kind = "system", SystemId = 5 };
        var supplier = new EstimateView.SummaryRow { Kind = "system", SystemId = 0, SupplierGroupKey = "" };
        var noGroup = new EstimateView.SummaryRow { Kind = "system", SystemId = 0 };
        if (!grouped.CanCollapseSystem || !supplier.CanCollapseSystem || noGroup.CanCollapseSystem ||
            new EstimateView.SummaryRow().IsIndented)
            throw new Exception("Summary group affordances do not match supported actions.");
        var column = new DataGridTextColumn { Header = "Закупка ∑", SortMemberPath = "w_wcost" };
        TableColumnMenu.SetMenuTitle(column, "СМР · закупка ∑");
        if (TableColumnMenu.Title(column) != "СМР · закупка ∑" || column.SortMemberPath != "w_wcost")
            throw new Exception("Column menu title must distinguish the financial group without changing identity.");
        Console.WriteLine("Summary table clip, group affordances and column labels OK.");
    }

    private static void CheckEstimateRowViewIndex()
    {
        static EditableEstimateRow Section(int key, int id, string name) =>
            new(new GmcSmeta.Core.Domain.EstimateRow(
                GmcSmeta.Core.Domain.RowKind.System, 0, key, 0, false, "",
                name, "", 0, 0, 0, null, false, "") { SystemId = id });
        static EditableEstimateRow Position(int key, int id, int section, bool area = false) =>
            new(new GmcSmeta.Core.Domain.EstimateRow(
                GmcSmeta.Core.Domain.RowKind.Position, 1, key, id, false, "1",
                "Строка " + id, "шт", 1, 1, 1, null, false, "")
                { SystemId = section, IsAreaSummary = area });

        var section = Section(0, 5, "П1");
        var second = Section(1, 6, "В1");
        var rows = new List<EditableEstimateRow> { section, second };
        rows.AddRange(Enumerable.Range(1, 2500)
            .Select(id => Position(id + 1, id, 5, area: id == 10)));
        var index = new EstimateRowViewIndex(rows);
        if (index.BySortKey.Count != 2502 || index.ByPositionId.Count != 2500 ||
            !ReferenceEquals(index.BySortKey[11], index.ByPositionId[10]) ||
            !index.AreaSummaryIds.SetEquals(new[] { 10 }) ||
            !ReferenceEquals(index.BySortKey, index.BySortKey) ||
            !ReferenceEquals(index.ByPositionId, index.ByPositionId) ||
            !ReferenceEquals(index.AreaSummaryIds, index.AreaSummaryIds))
            throw new Exception("Estimate structural lookups must be reused for the same snapshot.");

        if (index.SectionName(5, "?") != "П1" || index.SectionName(6, "?") != "В1" ||
            index.SectionName(7, "?") != "?")
            throw new Exception("Section names must use system IDs, not the shared row ID.");
        section.Name = "П1 изменена";
        if (index.SectionName(5, "?") != section.Name)
            throw new Exception("Section labels must not cache editable text.");

        var replacement = Position(2, 3000, 6, area: true);
        var next = new EstimateRowViewIndex(new[] { Section(0, 6, "В2"), replacement });
        if (ReferenceEquals(next.Rows, index.Rows) || next.ByPositionId.ContainsKey(10) ||
            !next.AreaSummaryIds.SetEquals(new[] { 3000 }) ||
            next.SectionName(6, "?") != "В2" || next.SectionName(5, "?") != "?" ||
            !ReferenceEquals(next.BySortKey[2], replacement))
            throw new Exception("Reloaded estimates must not retain old structural lookups.");
        Console.WriteLine("Estimate row snapshot index and live section labels OK.");
    }

    private static void CheckEstimateVisibleRowPresentation()
    {
        var section = new EditableEstimateRow(new GmcSmeta.Core.Domain.EstimateRow(
            GmcSmeta.Core.Domain.RowKind.System, 1, 0, 0, false, "4",
            "Подсистема П1", "", 0, 0, 0, null, false, "") { SystemId = 5 });
        var parent = new EditableEstimateRow(new GmcSmeta.Core.Domain.EstimateRow(
            GmcSmeta.Core.Domain.RowKind.Position, 2, 1, 10, false, "5",
            "Клапан", "шт", 1, 1, 1, null, false, "") { SystemId = 5 });
        var child = new EditableEstimateRow(new GmcSmeta.Core.Domain.EstimateRow(
            GmcSmeta.Core.Domain.RowKind.Position, 3, 2, 11, true, "",
            "Привод", "шт", 1, 0, 0, null, false, "")
            { SystemId = 5, ParentPositionId = 10 });
        var index = new EstimateRowViewIndex(new[] { section, parent, child });

        // Simulate rows reused from grouped mode, including a hidden child.
        parent.DisplayNum = "Старая группа";
        parent.DisplayIndentLevel = 1;
        child.DisplayNum = "Старая группа";
        child.DisplayIndentLevel = 2;
        index.PrepareDocumentRow(parent, flat: true);
        if (parent.DisplayNum != section.Name || parent.DisplayIndentLevel != 0 ||
            child.DisplayNum != "Старая группа" || child.DisplayIndentLevel != 2)
            throw new Exception("Preparing one visible row must not rewrite a hidden child.");

        section.Name = "Подсистема переименована";
        index.PrepareDocumentRow(parent, flat: true);
        index.PrepareDocumentRow(child, flat: true);
        if (parent.DisplayNum != section.Name || child.DisplayNum != section.Name ||
            parent.DisplayIndentLevel != 0 || child.DisplayIndentLevel != 1)
            throw new Exception("Flat view must refresh live system labels and component depth.");

        foreach (var row in index.Rows)
        {
            index.PrepareDocumentRow(row, flat: false);
            if (row.DisplayNum != row.Num || row.DisplayIndentLevel != row.Level)
                throw new Exception("Returning to document order must restore numbers and structural depth.");
        }

        var changes = 0;
        parent.PropertyChanged += (_, _) => changes++;
        index.PrepareDocumentRow(parent, flat: false);
        if (changes != 0)
            throw new Exception("An unchanged visible row must not raise redundant notifications.");
        Console.WriteLine("Estimate visible-row presentation and grouping transitions OK.");
    }

    private static void CheckKsReferenceHighlights()
    {
        var rows = Enumerable.Range(1, 25000)
            .Select(id => new KsRowVm { PositionId = id }).ToArray();
        var section = new KsRowVm { IsSection = true, PositionId = 1 };
        var duplicate = new KsRowVm { PositionId = 1 };
        var enumerated = 0;
        IEnumerable<KsRowVm> Source()
        {
            foreach (var row in rows.Append(section).Append(duplicate))
            {
                enumerated++;
                yield return row;
            }
        }

        var highlights = new KsReferenceHighlights(Source());
        highlights.Update(new long[] { 1, 25000, 99999, 2 });
        var first = rows[0].ReferenceBrush;
        var second = rows[^1].ReferenceBrush;
        if (first?.ToString() != "#FF2563EB" || second?.ToString() != "#FFDC2626" ||
            rows[1].ReferenceBrush?.ToString() != "#FF9333EA" ||
            !ReferenceEquals(duplicate.ReferenceBrush, first) || section.HasReference)
            throw new Exception("KS reference palette, duplicate ID or section handling changed.");

        var notifications = 0;
        rows[0].PropertyChanged += (_, _) => notifications++;
        highlights.Update(new long[] { 1, 25000, 99999, 2 });
        if (notifications != 0)
            throw new Exception("Unchanged reference color must not notify WPF.");
        highlights.Update(new long[] { 25000, 1 });
        if (!ReferenceEquals(rows[^1].ReferenceBrush, first) ||
            !ReferenceEquals(rows[0].ReferenceBrush, second) ||
            rows[1].HasReference || !ReferenceEquals(duplicate.ReferenceBrush, second))
            throw new Exception("KS reference removal or reordering failed.");

        highlights.Clear();
        highlights.Clear();
        if (rows.Any(row => row.HasReference) || duplicate.HasReference || section.HasReference ||
            enumerated != rows.Length + 2)
            throw new Exception("Reference cleanup or single source enumeration failed.");

        var replacement = new KsRowVm { PositionId = 1 };
        var replacementHighlights = new KsReferenceHighlights(new[] { replacement });
        replacementHighlights.Update(new long[] { 1 });
        if (!replacement.HasReference || rows[0].HasReference)
            throw new Exception("A replacement grid must not highlight old rows.");
        replacementHighlights.Clear();
        Console.WriteLine("KS sparse reference highlighting, colors and snapshot replacement OK.");
    }

    private static void CheckSignedActTabReuse()
    {
        static void Expect(bool expected, string published, string displayed,
            string list, string observed, long publishedId, long selectedId, bool clean)
        {
            if (ActsEditorView.CanReuseActOnReturn(published, displayed, list, observed,
                publishedId, selectedId, clean) != expected)
                throw new Exception("Act tab reuse guard accepted stale or mutable data.");
        }
        Expect(true, "r1", "r1", "r1", "r1", 42, 42, true);
        Expect(true, "r1", "r1", "r1", "r1", 0, 0, true);
        Expect(false, "r1", "r1", "r1", "r1", 42, 42, false); // draft or active editor
        Expect(false, "r1", "r2", "r1", "r2", 42, 42, true); // write receipt
        Expect(false, "r1", "r1", "r2", "r1", 42, 42, true);
        Expect(false, "r1", "r1", "r1", "r2", 42, 42, true);
        Expect(false, "r1", "r1", "r1", "r1", 42, 43, true);
        Expect(false, "", "", "", "", 0, 0, true);
        Console.WriteLine("Signed act tab revision and published snapshot guards OK.");
    }

    private static void CheckShipmentTabReuse()
    {
        static void Expect(bool expected, string? cached, string displayed,
            string observed, long current, long requested, bool dirty = false)
        {
            if (ShipmentsPane.CanReuseShipmentSnapshot(cached, displayed, observed,
                current, requested, dirty) != expected)
                throw new Exception("Shipment tab reuse accepted an invalid snapshot or rejected a clean one.");
        }
        Expect(true, "r1", "r1", "r1", 42, 42);
        Expect(true, "r1", "r1", "r1", 0, 0); // confirmed empty object
        Expect(false, "r1", "r1", "r2", 42, 42);
        Expect(false, "r1", "r2", "r2", 42, 42); // newer write receipt
        Expect(false, null, "r1", "r1", 42, 42); // invalidated by incremental write
        Expect(false, "r1", "r1", "r1", 42, 43);
        Expect(false, "r1", "r1", "r1", 42, 42, dirty: true);
        Expect(false, "", "", "", 0, 0);
        Console.WriteLine("Shipment tab revision, selection and readback guards OK.");
    }

    private static void CheckProcurementUnchangedDisplay()
    {
        static ProcurementPreviewRow Row(int id) => new()
        {
            PositionId = id, GroupLabel = "П1 · 2500 поз.", GroupIdentity = "system:1",
            GroupTitle = "П1", GroupDisplayNumber = "1"
        };
        var current = Enumerable.Range(1, 2500).Select(Row).ToList();
        var visible = current.ToList();
        if (!ProcurementPane.SameBuyRowReferences(current, visible))
            throw new Exception("Unchanged procurement rows must retain their current view.");

        visible[1234] = Row(1235);
        if (ProcurementPane.SameBuyRowReferences(current, visible))
            throw new Exception("Authoritative readback must replace a visible row even if its ID matches.");
        visible[1234] = current[1234];
        visible.Reverse();
        if (ProcurementPane.SameBuyRowReferences(current, visible) ||
            ProcurementPane.SameBuyRowReferences(current, visible.Take(10).ToArray()))
            throw new Exception("Changed order or membership must not be a no-op.");

        var first = current[0];
        var key = ProcurementPane.BuyDisplayKey.Of(first);
        first.GroupTitle = "Новое название";
        if (key == ProcurementPane.BuyDisplayKey.Of(first))
            throw new Exception("Group title changes must invalidate detached view keys.");
        first.GroupTitle = "П1";
        first.GroupDisplayNumber = "2";
        if (key == ProcurementPane.BuyDisplayKey.Of(first))
            throw new Exception("Group number changes must invalidate detached view keys.");
        Console.WriteLine("Procurement no-op projection and authoritative replacements OK.");
    }

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
