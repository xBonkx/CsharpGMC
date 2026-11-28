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
        RunSharedGlyphCatalogChecks();
        RunDirectorTextLayoutChecks();
        RunSharedTypographyChecks();
        RunEstimateHeaderToolbarParityChecks();
        RunDynamicFormLayoutChecks();
        RunSharedSpacingChecks();
        RunEstimateScrollLayoutChecks();
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

    private static void RunSharedGlyphCatalogChecks()
    {
        var pen = new Pen(Brushes.Black, 1.9)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        var keys = ResourceKeys(Application.Current.Resources).OfType<string>()
            .Distinct(StringComparer.Ordinal).ToArray();
        foreach (var key in keys.Where(k => k.StartsWith("Glyph", StringComparison.Ordinal)))
        {
            if (Application.Current.FindResource(key) is not Geometry geometry) continue;
            var bounds = geometry.GetRenderBounds(pen);
            Require(!bounds.IsEmpty && bounds.Left >= -0.02 && bounds.Top >= -0.02 &&
                bounds.Right <= 24.02 && bounds.Bottom <= 24.02,
                key + ": geometry outside 24x24: " + bounds);
        }
        foreach (var key in keys.Where(k => k.StartsWith("IconLabel", StringComparison.Ordinal) ||
                     k.StartsWith("IconOnly", StringComparison.Ordinal)))
        {
            if (Application.Current.FindResource(key) is not DataTemplate template) continue;
            var content = (FrameworkElement)template.LoadContent();
            var view = content as Viewbox ?? (content as Panel)?.Children.OfType<Viewbox>().Single();
            Require(view is not null, key + ": missing viewbox");
            var expected = key == "IconLabelMenu" ? 20d : key == "IconOnlyX" ? 14d :
                key == "IconOnlyChevronDown" ? 12d : 18d;
            Require(view!.Width == expected && view.Height == expected &&
                view.Stretch == Stretch.Uniform && !view.IsHitTestVisible, key + ": glyph role");
            Require(view.Child is Canvas canvas && canvas.Width == 24 && canvas.Height == 24,
                key + ": canvas size");
            if (key.StartsWith("IconLabel", StringComparison.Ordinal))
                Require(view.Margin == new Thickness(0, 0, 6, 0), key + ": caption gap");
        }
        Console.WriteLine("Shared glyph geometry and all icon template metrics checked (no SQL).");
    }

    private static void RunSharedTypographyChecks()
    {
        double Size(string key) => (double)Application.Current.FindResource(key);
        var family = (FontFamily)Application.Current.FindResource("UiFontFamily");
        Require(family.Source == "Segoe UI Variable Text, Segoe UI Variable, Segoe UI",
            "Qt font preference order must be retained");
        var face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Console.WriteLine(face.TryGetGlyphTypeface(out var resolved)
            ? "Resolved UI font: " + string.Join(", ", resolved.FamilyNames.Values.Distinct())
            : "UI font uses composite/fallback resolution; inspect actual Windows text render.");
        Require(Math.Abs(Size("UiBodySize") - 10 * 96d / 72) < .001, "Body pt conversion");
        Require(Math.Abs(Size("UiCaptionSize") - 8.5 * 96d / 72) < .001, "Caption pt conversion");
        Require(Math.Abs(Size("UiH1Size") - 18 * 96d / 72) < .001, "Heading pt conversion");
        Require(Size("UiSummaryTitleSize") == 19 && Size("UiSummaryValueSize") == 22 &&
                Size("UiSummaryCaptionSize") == 11, "Explicit pixel roles must not be scaled twice");
        var heading = new TextBlock
        {
            Style = (Style)Application.Current.FindResource("UiHeadingText"),
            FontSize = Size("UiH2Size"),
            Text = "Длинное название договора с несколькими строками и дополнительным соглашением"
        };
        heading.Measure(new Size(140, double.PositiveInfinity));
        Require(heading.DesiredSize.Height > heading.FontSize * 2 &&
            heading.LineStackingStrategy == LineStackingStrategy.MaxHeight &&
            heading.TextWrapping == TextWrapping.Wrap, "Wrapped heading must grow");
        var money = new TextBox
        {
            Style = (Style)Application.Current.FindResource("UiMoneyTileEditor"),
            Text = "123 456 789,01"
        };
        money.Measure(new Size(200, double.PositiveInfinity));
        Require(money.FontSize == Size("UiMoneyInputSize") && money.FontWeight == FontWeights.SemiBold &&
            money.TextAlignment == TextAlignment.Right && money.VerticalContentAlignment == VerticalAlignment.Center,
            "Money input typography");
        var tile = new TextBlock { Style = (Style)Application.Current.FindResource("PythonTileValue"), Text = "123 456 789,01 ₽" };
        tile.Measure(new Size(100, double.PositiveInfinity));
        Require(tile.FontSize == Size("UiValueSize") && tile.FontWeight == FontWeights.Bold &&
            tile.TextWrapping == TextWrapping.Wrap && tile.DesiredSize.Height > tile.FontSize,
            "Money tile text must grow rather than trim");
        Console.WriteLine("Shared typography roles and natural line heights checked (no SQL).");
    }

    private static void RunDynamicFormLayoutChecks()
    {
        var body = new StackPanel();
        for (var i = 0; i < 16; i++)
            body.Children.Add(new TextBlock { Text = "Строка " + i, Height = 32 });
        var footer = new Button { Content = "Сохранить", Height = 34 };
        var root = DynamicFormLayout.WithFooter(body, footer);
        Layout(root, 440, 240);
        var scroll = (ScrollViewer)root.Children[1];
        Require(scroll.ScrollableHeight > 0, "Long form body must scroll at small height");
        Require(footer.ActualHeight >= 34 && LayoutInformation.GetLayoutSlot(footer).Bottom <= root.ActualHeight + .1,
            "Commit footer must remain inside the dialog viewport");
        var caption = new CheckBox { Content = "Длинное описание параметра с переносом на несколько строк" };
        DynamicFormLayout.WrapCaption(caption);
        Require(caption.Content is TextBlock { TextWrapping: TextWrapping.Wrap }, "Wrap long check captions");
        var window = new Window();
        DynamicFormLayout.Apply(window);
        Require(window.FontFamily.Source == ((FontFamily)Application.Current.FindResource("UiFontFamily")).Source &&
            TextOptions.GetTextFormattingMode(window) == TextFormattingMode.Display &&
            window.UseLayoutRounding, "Dynamic window uses application typography");
        window.Close();
    }

    private static void RunEstimateHeaderToolbarParityChecks()
    {
        var row = new DocumentCrumbPanel { MinHeight = 20, ClipToBounds = true };
        TextBlock Text(string text) => new()
        {
            Text = text, TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = (double)Application.Current.FindResource("UiCaptionSize"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var crumb = Text(new string('О', 100)); crumb.MaxWidth = 300;
        var company = Text(new string('К', 100)); company.MaxWidth = 180;
        DocumentCrumbPanel.SetCanShrink(crumb, true);
        DocumentCrumbPanel.SetCanShrink(company, true);
        var status = Text("Не сходится с файлом");
        status.Visibility = Visibility.Collapsed;
        row.Children.Add(crumb);
        row.Children.Add(Text("КП"));
        row.Children.Add(company);
        row.Children.Add(status);
        var titles = new StackPanel();
        titles.Children.Add(row);
        titles.Children.Add(Text("Документ"));
        var actions = new WrapPanel();
        actions.Children.Add(new Button { Content = "Отменить", Width = 100, Height = 34 });
        actions.Children.Add(new Button { Content = "Сохранить в Excel / PDF", Width = 210, Height = 34 });
        var header = new ResponsiveToolbarPanel { MinimumLeftWidth = 420 };
        header.Children.Add(titles);
        header.Children.Add(actions);
        Layout(header, 900, 150);
        var titleY = LayoutInformation.GetLayoutSlot(titles).Top;
        var rowHeight = row.DesiredSize.Height;
        var actionY = LayoutInformation.GetLayoutSlot(actions).Top;
        Require(titleY == 0 && actionY == 0, "Wide header must keep actions beside metadata");
        status.Visibility = Visibility.Visible;
        Layout(header, 900, 150);
        Require(Math.Abs(row.DesiredSize.Height - rowHeight) < .1 &&
                LayoutInformation.GetLayoutSlot(actions).Top == actionY,
            "Warning must not change header rows or action alignment");
        Layout(header, 520, 200);
        Require(LayoutInformation.GetLayoutSlot(actions).Top >= titles.DesiredSize.Height - .1,
            "Narrow header must move actions below the title");
        foreach (FrameworkElement child in row.Children)
            Require(LayoutInformation.GetLayoutSlot(child).Right <= row.ActualWidth + .1,
                "Crumb/status slot outside viewport");

        var toolbar = new ResponsiveToolbarPanel();
        var left = new WrapPanel();
        var right = new WrapPanel();
        Button Tool(string text, string template)
        {
            return new Button { Content = text, ContentTemplate = (DataTemplate)Application.Current.FindResource(template),
                Style = (Style)Application.Current.FindResource("PythonToolbarButton"),
                MinHeight = 34, Margin = new Thickness(0, 0, 6, 4) };
        }
        left.Children.Add(Tool("+ Строка", "IconLabelRowPlus"));
        left.Children.Add(Tool("Из справочника", "IconLabelBook"));
        left.Children.Add(Tool("Группировка", "IconLabelLayers"));
        left.Children.Add(Tool("Сортировка", "IconLabelSort"));
        right.Children.Add(Tool("Закуп", "IconLabelCalculator"));
        right.Children.Add(Tool("Столбцы", "IconLabelList"));
        right.Children.Add(new TextBox { Width = 170, MinHeight = 34 });
        toolbar.Children.Add(left);
        toolbar.Children.Add(right);
        foreach (var scale in new[] { .9, 1.0, 1.1, 1.2 })
        {
            Layout(toolbar, 1920 / scale - 300 - 48, 120);
            Require(LayoutInformation.GetLayoutSlot(right).Top == 0,
                "Normal estimate toolbar must fit at 1920px/96dpi and app scale " + scale);
        }
        var noPrice = Tool("Без цены", "IconLabelAlert");
        var reset = Tool("Как в КП", "IconLabelX");
        left.Children.Add(noPrice);
        left.Children.Add(reset);
        foreach (var scale in new[] { .9, 1.0, 1.1, 1.2 })
        {
            var width = 1920 / scale - 300 - 48;
            left.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            right.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var fits = left.DesiredSize.Width + 10 + right.DesiredSize.Width <= width;
            Layout(toolbar, width, 160);
            if (fits) Require(LayoutInformation.GetLayoutSlot(right).Top == 0,
                "Active filter captions that fit must stay on one row");
        }
        left.Children[0].Visibility = left.Children[1].Visibility = Visibility.Collapsed;
        Layout(toolbar, 1920 - 300 - 48, 120);
        Require(LayoutInformation.GetLayoutSlot(right).Top == 0,
            "Locked toolbar must collapse addition controls and fit");
        left.Children[0].Visibility = left.Children[1].Visibility = Visibility.Visible;
        Layout(toolbar, 420, 160);
        Require(LayoutInformation.GetLayoutSlot(right).Top > 0, "Narrow toolbar must wrap");
    }

    private static void RunSharedSpacingChecks()
    {
        var fixtures = new (Control Control, string Key, string Chrome)[]
        {
            (new RadioButton { Content = "Режим" }, "Segment", "SegmentBorder"),
            (new RadioButton { Content = "Договор" }, "ObjectTab", "TabBorder"),
            (new RadioButton { Content = "Этап" }, "ObjectSubTab", "SubTabBorder"),
            (new Button { Content = "Свойства" }, "PythonTabAction", "TabSurface"),
            (new CheckBox { Content = "Без закупа" }, "PythonFilterChip", "FilterChrome"),
            (new RadioButton { Content = "Все" }, "PythonFilterRadio", "FilterSurface"),
            (new TabItem { Header = "Материалы" }, "PythonSecondaryTab", "TabUnderline"),
        };
        var requested = new Thickness(11, 3, 5, 7);
        foreach (var (control, key, chromeName) in fixtures)
        {
            control.Style = (Style)Application.Current.FindResource(key);
            control.Padding = requested;
            control.HorizontalContentAlignment = HorizontalAlignment.Left;
            control.VerticalContentAlignment = VerticalAlignment.Bottom;
            Layout(control, 240, 64);
            var border = control.Template.FindName(chromeName, control) as Border;
            Require(border is not null && border.Padding == requested,
                key + ": template must honor padding exactly once");
            Require(border!.Child is ContentPresenter presenter &&
                presenter.HorizontalAlignment == HorizontalAlignment.Left &&
                presenter.VerticalAlignment == VerticalAlignment.Bottom,
                key + ": content alignment must honor the control");
        }
        foreach (var key in new[] { "CompactCostText", "TotalsArticleText", "ReportGridReviewText",
                     "ReportGridMuted", "ReportGridCenter", "ReportGridRight", "ReportGridStrongRight" })
        {
            var text = new TextBlock { Text = "123 456 789,00", Style = (Style)Application.Current.FindResource(key) };
            Require(text.Margin == new Thickness(0) && text.Padding == new Thickness(0),
                key + ": cell chrome owns inset");
        }
        var editor = new TextBox { Style = (Style)Application.Current.FindResource("PythonGridEditor") };
        Require(editor.Margin == new Thickness(0) && editor.Padding == new Thickness(0),
            "Editing must not duplicate cell inset");
        Console.WriteLine("Shared template padding, content alignment and cell insets checked (no SQL).");
    }

    private static void RunDirectorTextLayoutChecks()
    {
        // Construct but do not load/show: DirectorView starts its SQL reader
        // only on Loaded. Measure real local styles using synthetic long text.
        var view = new DirectorView("");
        Require(!view.IsLoaded, "Offline director fixture must not load a database.");
        var headerStyle = (Style)view.FindResource("DirectorHeader");
        foreach (var label in new[] { "Докуплено\nвне сметы", "Выдано\nна объект", "Просрочено\nпоставок" })
        {
            var header = new DataGridColumnHeader { Content = label.Replace("\n", "\n"), Style = headerStyle };
            header.Measure(new Size(58, double.PositiveInfinity));
            Require(header.DesiredSize.Height > 48, "Narrow header must grow instead of clipping: " + label);
        }
        var money = new TextBlock
        {
            Style = (Style)view.FindResource("DirectorMoney"),
            Text = "123 456 789 012 345,67 ₽"
        };
        money.Measure(new Size(90, double.PositiveInfinity));
        Require(money.DesiredSize.Height > money.FontSize * 2 && money.TextTrimming == TextTrimming.None,
            "Long money text must occupy additional lines without ellipsis.");
        Console.WriteLine("Director long headers and amounts measured (no SQL).");
    }

    private static void RunEstimateScrollLayoutChecks()
    {
        // Use the real DataGrid/ScrollViewer theme with synthetic rows only.
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, CanUserAddRows = false,
            RowHeaderWidth = 0, MinRowHeight = 27,
            ItemsSource = Enumerable.Range(1, 40).Select(i => i.ToString()).ToArray()
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
        grid.Columns.Add(new DataGridTextColumn { Header = "Название", Width = 460, Binding = new Binding(".") });
        grid.Columns.Add(new DataGridTextColumn { Header = "Примечание", Width = 170, Binding = new Binding(".") });
        Layout(grid, 420, 200);
        var scroll = (ScrollViewer)grid.Template.FindName("DG_ScrollViewer", grid);
        Require(scroll.ComputedHorizontalScrollBarVisibility == Visibility.Visible &&
            scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible, "Narrow grid must expose both scrollbars.");
        var presenter = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
        var vertical = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
        var horizontal = (ScrollBar)scroll.Template.FindName("PART_HorizontalScrollBar", scroll);
        var contentOrigin = presenter.TransformToAncestor(scroll).Transform(new Point());
        var verticalOrigin = vertical.TransformToAncestor(scroll).Transform(new Point());
        var horizontalOrigin = horizontal.TransformToAncestor(scroll).Transform(new Point());
        Require(contentOrigin.X + presenter.ActualWidth <= verticalOrigin.X + 1 &&
            contentOrigin.Y + presenter.ActualHeight <= horizontalOrigin.Y + 1,
            "Scrollbars must reserve layout space outside the content viewport.");
        Require(Math.Abs(vertical.ActualWidth - 11) < 1 &&
            Math.Abs(horizontal.ActualHeight - 11) < 1, "Scrollbar thickness must remain 11 DIP.");
        Layout(grid, 900, 200);
        Require(scroll.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed,
            "Horizontal scrollbar must disappear when both columns fit.");
        Console.WriteLine("Estimate scrollbar viewport geometry checked (no SQL).");
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

    // Real WPF input states; this is deliberately separate from offscreen PNG evidence.
    // No application window or database is created.
    internal static void ShowInteractiveIconCatalog()
    {
        RunSharedGlyphCatalogChecks();
        var body = new DockPanel { Background = Brushes.White };
        var hint = new TextBlock
        {
            Text = "Tab / Shift+Tab — фокус; мышь — наведение и нажатие; Space — переключение. " +
                "Сравните обычное, недоступное, отмеченное и снятое состояния. Esc — закрыть.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12)
        };
        DockPanel.SetDock(hint, Dock.Top);
        body.Children.Add(hint);
        body.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = CreateIconCatalog(interactive: true)
        });
        var window = new Window
        {
            Title = "GMC — каталог иконок (без SQL)", Content = body,
            Width = 1040, Height = 700, MinWidth = 480, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            window.Close();
        };
        window.ShowDialog();
    }

    private static StackPanel CreateIconCatalog(bool interactive = false)
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
                Content = interactive ? "Переключатель" : "Активная", ContentTemplate = template, IsChecked = true,
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
        writer.WriteLine("path\ttype\tname\tvisibility\tx\ty\twidth\theight\tdesiredWidth\tdesiredHeight\tmargin\tpadding\tfontSize\tfontFamily\tactualDpiX\tactualDpiY\tpaintBoundsX\tpaintBoundsY\tpaintBoundsWidth\tpaintBoundsHeight\tmarginSource\tpaddingSource\ttemplatedParent");
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
            var bounds = ReferenceEquals(visual, root) ? new Rect(element.RenderSize) :
                visual.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            var paddingProperty = element switch
            {
                Control => Control.PaddingProperty,
                Border => Border.PaddingProperty,
                TextBlock => TextBlock.PaddingProperty,
                _ => null
            };
            var marginSource = DependencyPropertyHelper.GetValueSource(element, FrameworkElement.MarginProperty).BaseValueSource;
            var paddingSource = paddingProperty is null ? "" :
                DependencyPropertyHelper.GetValueSource(element, paddingProperty).BaseValueSource.ToString();
            var templatedParent = element.TemplatedParent?.GetType().Name ?? "";
            var fontSize = (double)element.GetValue(TextElementFontSize);
            var font = (FontFamily)element.GetValue(TextElementFontFamily);
            var dpi = VisualTreeHelper.GetDpi(visual);
            writer.WriteLine(FormattableString.Invariant(
                $"{path}\t{element.GetType().Name}\t{element.Name}\t{element.Visibility}\t{offset.X:0.##}\t{offset.Y:0.##}\t{element.ActualWidth:0.##}\t{element.ActualHeight:0.##}\t{element.DesiredSize.Width:0.##}\t{element.DesiredSize.Height:0.##}\t{element.Margin.ToString()}\t{padding}\t{fontSize:0.##}\t{font.Source}\t{dpi.PixelsPerInchX:0.##}\t{dpi.PixelsPerInchY:0.##}\t{bounds.X:0.##}\t{bounds.Y:0.##}\t{bounds.Width:0.##}\t{bounds.Height:0.##}\t{marginSource}\t{paddingSource}\t{templatedParent}"));
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
