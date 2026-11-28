using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateGridGeometrySourceTests
{
    [Fact]
    public void EstimateUsesPythonPaddingAndKeepsVariableRowHeight()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var view = XDocument.Load(Path.Combine(app, "EstimateView.xaml"));
        XElement Style(string key) => Assert.Single(view.Descendants(p + "Style")
            .Where(e => (string?)e.Attribute(xaml + "Key") == key));
        string? Value(XElement element, string property) => (string?)Assert.Single(element.Elements(p + "Setter")
            .Where(e => (string?)e.Attribute("Property") == property)).Attribute("Value");
        var grid = Assert.Single(view.Descendants(p + "DataGrid")
            .Where(e => (string?)e.Attribute(xaml + "Name") == "EstimateGrid"));
        Assert.Equal("True", (string?)grid.Attribute(XName.Get("AdaptiveGridRowHeight.Enabled", "clr-namespace:GmcSmeta.App")));
        Assert.Null(grid.Attribute("RowHeight"));
        Assert.Null(grid.Attribute("MinRowHeight"));
        Assert.Equal("True", (string?)grid.Attribute("EnableRowVirtualization"));
        Assert.Equal("Recycling", (string?)grid.Attribute("VirtualizingPanel.VirtualizationMode"));
        Assert.Equal("White", (string?)grid.Attribute("Background"));
        Assert.Null(grid.Attribute("HorizontalGridLinesBrush"));
        Assert.Null(grid.Attribute("VerticalGridLinesBrush"));
        Assert.Equal("4,4", Value(Style("EstimateCell"), "Padding"));
        var header = Assert.Single(view.Descendants(p + "Style")
            .Where(e => (string?)e.Attribute("TargetType") == "DataGridColumnHeader" && e.Attribute(xaml + "Key") is null));
        Assert.Equal("6,8", Value(header, "Padding"));
        var row = Style("EstimateRow");
        Assert.Equal("{Binding MinRowHeight, RelativeSource={RelativeSource AncestorType=DataGrid}}", Value(row, "MinHeight"));
        Assert.DoesNotContain(row.Descendants(p + "Setter"), e => (string?)e.Attribute("Property") == "Height");
        Assert.Equal("White", Value(row, "Background"));
        var system = Assert.Single(row.Descendants(p + "DataTrigger")
            .Where(e => (string?)e.Attribute("Binding") == "{Binding IsSystem}"));
        Assert.Equal("#F1F4F9", Value(system, "Background"));
        Assert.Equal("{StaticResource GridSelectionTextBrush}", Value(system, "Foreground"));
        var subsystem = Assert.Single(row.Descendants(p + "MultiDataTrigger"));
        Assert.Equal("#E8F5FC", Value(subsystem, "Background"));
        Assert.Equal("#246B91", Value(subsystem, "Foreground"));
        Assert.Equal("Wrap", Value(Style("DocumentNameDisplay"), "TextWrapping"));
    }

    [Fact]
    public void EstimateHeaderAndCellAlignmentFollowPythonColumnRoles()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var view = XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "EstimateView.xaml"));
        var grid = Assert.Single(view.Descendants(p + "DataGrid")
            .Where(e => (string?)e.Attribute(xaml + "Name") == "EstimateGrid"));
        var columns = grid.Element(p + "DataGrid.Columns")!.Elements()
            .ToDictionary(e => (string)e.Attribute("SortMemberPath")!);
        XElement Style(string key) => Assert.Single(view.Descendants(p + "Style")
            .Where(e => (string?)e.Attribute(xaml + "Key") == key));
        string? Value(XElement element, string property) => (string?)Assert.Single(element.Elements(p + "Setter")
            .Where(e => (string?)e.Attribute("Property") == property)).Attribute("Value");
        Assert.Equal(13, columns.Count);
        foreach (var key in new[] { "number", "unit", "qty" })
            Assert.Equal("{StaticResource EstimateCenteredHeader}", (string?)columns[key].Attribute("HeaderStyle"));
        foreach (var key in new[] { "price", "sum", "purchase", "work", "workTotal" })
            Assert.Equal("{StaticResource EstimateMoneyHeader}", (string?)columns[key].Attribute("HeaderStyle"));
        foreach (var key in new[] { "name", "basis", "note", "areaUnit", "areaTotal" })
            Assert.Null(columns[key].Attribute("HeaderStyle"));
        Assert.Equal("Center", Value(Style("EstimateCenteredHeader"), "HorizontalContentAlignment"));
        Assert.Equal("Right", Value(Style("EstimateMoneyHeader"), "HorizontalContentAlignment"));
        foreach (var key in new[] { "EstimateCenteredHeader", "EstimateMoneyHeader" })
            Assert.Equal("{StaticResource {x:Type DataGridColumnHeader}}", (string?)Style(key).Attribute("BasedOn"));
        Assert.Equal("Center", Value(Style("CenterCell"), "TextAlignment"));
        Assert.Equal("Center", Value(Style("EstimateCenteredEditor"), "TextAlignment"));
        Assert.Equal("Right", Value(Style("Money"), "TextAlignment"));
        Assert.Equal("Right", Value(Style("EstimateMoneyEditor"), "TextAlignment"));
        foreach (var key in new[] { "unit", "qty" })
        {
            Assert.Equal("{StaticResource CenterCell}", (string?)columns[key].Attribute("ElementStyle"));
            Assert.Equal("{StaticResource EstimateCenteredEditor}", (string?)columns[key].Attribute("EditingElementStyle"));
        }
        foreach (var key in new[] { "sum", "purchase", "workTotal", "areaTotal" })
            Assert.Equal("{StaticResource Money}", (string?)columns[key].Attribute("ElementStyle"));
        foreach (var key in new[] { "price", "work" })
        {
            var display = Assert.Single(columns[key].Descendants(p + "TextBlock"));
            var editor = Assert.Single(columns[key].Descendants(p + "TextBox"));
            Assert.Equal(key == "price" ? "{StaticResource Price}" : "{StaticResource WorkPrice}", (string?)display.Attribute("Style"));
            Assert.Equal("Right", (string?)editor.Attribute("TextAlignment"));
        }
    }

    [Fact]
    public void NumberColumnReservesExpanderSpaceOnlyForExpandableRows()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var view = XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "EstimateView.xaml"));
        var number = Assert.Single(view.Descendants(p + "DataGridTemplateColumn")
            .Where(e => (string?)e.Attribute("SortMemberPath") == "number"));
        Assert.Equal("64", (string?)number.Attribute("Width"));
        Assert.Equal("48", (string?)number.Attribute("MinWidth"));
        Assert.Equal("True", (string?)number.Attribute("IsReadOnly"));
        var grid = number.Element(p + "DataGridTemplateColumn.CellTemplate")!
            .Element(p + "DataTemplate")!.Element(p + "Grid")!;
        Assert.Equal("True", (string?)grid.Attribute("ClipToBounds"));
        var widths = grid.Element(p + "Grid.ColumnDefinitions")!.Elements(p + "ColumnDefinition")
            .Select(e => (string?)e.Attribute("Width")).ToArray();
        Assert.Equal(new[] { "Auto", "*" }, widths);
        var button = Assert.Single(grid.Elements(p + "Button"));
        Assert.Equal("0", (string?)button.Attribute("Grid.Column"));
        Assert.Equal("22", (string?)button.Attribute("Width"));
        Assert.Equal("{Binding IsExpandable, Converter={StaticResource OnlySystems}}", (string?)button.Attribute("Visibility"));
        Assert.Equal("ToggleSection_Click", (string?)button.Attribute("Click"));
        var label = Assert.Single(grid.Elements(p + "TextBlock"));
        Assert.Equal("1", (string?)label.Attribute("Grid.Column"));
        Assert.Equal("{Binding DisplayNum}", (string?)label.Attribute("Text"));
        Assert.Equal("Center", (string?)label.Attribute("TextAlignment"));
        Assert.Equal("CharacterEllipsis", (string?)label.Attribute("TextTrimming"));
        Assert.Equal("False", (string?)label.Attribute("IsHitTestVisible"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.Single(view.Descendants(p + "BooleanToVisibilityConverter")
            .Where(e => (string?)e.Attribute(xaml + "Key") == "OnlySystems"));
    }

    [Fact]
    public void EstimateEditorsKeepCellInsetAndStageTypography()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var view = XDocument.Load(Path.Combine(app, "EstimateView.xaml"));
        XElement Style(string key) => Assert.Single(view.Descendants(p + "Style")
            .Where(e => (string?)e.Attribute(x + "Key") == key));
        string? Value(XElement element, string property) => (string?)Assert.Single(element.Elements(p + "Setter")
            .Where(e => (string?)e.Attribute("Property") == property)).Attribute("Value");
        var editor = Style("EstimateGridEditor");
        Assert.Equal("0", Value(editor, "Margin"));
        Assert.Equal("0", Value(editor, "Padding"));
        foreach (var property in new[] { "FontWeight", "FontStyle" })
            Assert.Equal("{Binding " + property + ", RelativeSource={RelativeSource AncestorType=DataGridCell}}", Value(editor, property));
        foreach (var key in new[] { "Money", "CenterCell", "EstimateTextCell" })
        {
            Assert.Equal("0", Value(Style(key), "Margin"));
            Assert.Equal("Center", Value(Style(key), "VerticalAlignment"));
        }
        Assert.Equal("{StaticResource EstimateTextCell}", (string?)Style("EstimateNoteDisplay").Attribute("BasedOn"));
        Assert.Equal("{StaticResource EstimateMoneyEditor}", (string?)Style("EstimateStageEditor").Attribute("BasedOn"));
        Assert.Equal("SemiBold", Value(Style("EstimateStageEditor"), "FontWeight"));
        var stages = File.ReadAllText(Path.Combine(app, "EstimateView.InlineStages.cs"));
        Assert.Contains("style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold))", stages);
        Assert.Contains("(Style)FindResource(\"EstimateStageEditor\")", stages);
        var name = Assert.Single(view.Descendants(p + "DataGridTemplateColumn")
            .Where(e => (string?)e.Attribute("SortMemberPath") == "name"));
        Assert.Equal((string?)Assert.Single(name.Descendants(p + "TextBlock")).Attribute("Margin"),
                     (string?)Assert.Single(name.Descendants(p + "TextBox")).Attribute("Margin"));
    }

    [Fact]
    public void EstimateColumnDefaultsAndViewportPolicyKeepPythonWidthsAndNoteFlex()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var view = XDocument.Load(Path.Combine(app, "EstimateView.xaml"));
        var grid = view.Descendants(p + "DataGrid").Single(e => (string?)e.Attribute(x + "Name") == "EstimateGrid");
        var columns = grid.Element(p + "DataGrid.Columns")!.Elements().ToDictionary(e => (string)e.Attribute("SortMemberPath")!);
        foreach (var (key, width) in new[] { ("number", "64"), ("name", "460"), ("basis", "150"),
                     ("unit", "64"), ("qty", "80"), ("price", "118"), ("sum", "130"), ("purchase", "110"),
                     ("note", "170"), ("work", "118"), ("workTotal", "130"), ("areaUnit", "90"), ("areaTotal", "90") })
            Assert.Equal(width, (string?)columns[key].Attribute("Width"));
        Assert.Equal("False", (string?)grid.Attribute("CanUserResizeRows"));
        Assert.Equal("Auto", (string?)grid.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
        Assert.Equal("Auto", (string?)grid.Attribute("ScrollViewer.VerticalScrollBarVisibility"));
        Assert.Equal("True", (string?)grid.Attribute("ScrollViewer.CanContentScroll"));
        var fit = File.ReadAllText(Path.Combine(app, "EstimateView.ColumnLayout.cs"));
        Assert.Contains("scroll?.ViewportWidth", fit);
        Assert.Contains("viewport <= 0", fit);
        Assert.Contains("column == NoteColumn ? EstimateColumnLayout.Role.Note", fit);
        Assert.Contains("EstimateColumnLayout.Fit(inputs, available, manual)", fit);
        Assert.Contains("var measured = manual ? null : EstimateMeasuredMinimums();", fit);
        Assert.Contains("Math.Max(minimum, measured![column])", fit);
        Assert.Contains("IsMeasuredIdentity(_estimateMeasuredSource, EstimateGrid.ItemsSource)", fit);
        Assert.Contains("IsMeasuredIdentity(_estimateMeasuredRows, _rows)", fit);
        Assert.Contains("EstimateMeasurementFont()", fit);
        Assert.Contains("if (HasActiveEstimateEdit()) return;", fit);
        Assert.Contains("row.DisplayAmountText", fit);
        Assert.Contains("row.DisplayWorkAmountText", fit);
        Assert.Contains("row.IsExpandable ? 22 : 0", fit);
        Assert.Contains("if (_applyingEstimateOrder) return;", fit);
        var row = File.ReadAllText(Path.Combine(app, "EditableEstimateRow.cs"));
        Assert.Contains("Math.Max(IsSub ? 1 : 0, DisplayIndentLevel - 1)", row);
        Assert.Contains("OnPropertyChanged(nameof(NameIndentLevel));", row);
        Assert.Contains("Math.Max(0, level) * 14", File.ReadAllText(Path.Combine(app, "LevelIndentConverter.cs")));
    }

    [Fact]
    public void MissingPriceFillsEntireCellButSelectionAndEditingWin()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var view = XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "EstimateView.xaml"));
        XElement Style(string key) => view.Descendants(p + "Style").Single(e => (string?)e.Attribute(x + "Key") == key);
        var cell = Style("EstimateCell");
        Assert.Contains(cell.Descendants(p + "Border"), e => (string?)e.Attribute("Background") == "{TemplateBinding Background}" &&
            (string?)e.Attribute("Padding") == "{TemplateBinding Padding}");
        foreach (var (key, colors) in new[] { ("EstimatePriceCell", new[] { "#FDF0F0", "#FEE2E2" }),
                     ("EstimateWorkPriceCell", new[] { "#FDF0F0" }) })
        {
            var triggers = Style(key).Descendants(p + "MultiDataTrigger").ToArray();
            Assert.Equal(colors, triggers.Select(trigger => (string?)trigger.Elements(p + "Setter")
                .Single(e => (string?)e.Attribute("Property") == "Background").Attribute("Value")).ToArray());
            foreach (var trigger in triggers)
            {
                var conditions = trigger.Descendants(p + "Condition").ToArray();
                foreach (var state in new[] { "IsSelected", "IsEditing" })
                    Assert.Contains(conditions, e => (string?)e.Attribute("Binding") == "{Binding " + state + ", RelativeSource={RelativeSource Self}}" &&
                        (string?)e.Attribute("Value") == "False");
                Assert.DoesNotContain(conditions, e => ((string?)e.Attribute("Binding"))?.Contains("IsKeyboardFocusWithin") == true);
            }
        }
        foreach (var key in new[] { "Price", "WorkPrice" })
        {
            var style = Style(key);
            Assert.Equal("{StaticResource Money}", (string?)style.Attribute("BasedOn"));
            Assert.Contains(style.Descendants(p + "Setter"), e => (string?)e.Attribute("Property") == "Foreground" &&
                (string?)e.Attribute("Value") == "#DC2626");
        }
        Assert.Contains(Style("Money").Elements(p + "Setter"), e => (string?)e.Attribute("Property") == "TextAlignment" &&
            (string?)e.Attribute("Value") == "Right");
    }
}
