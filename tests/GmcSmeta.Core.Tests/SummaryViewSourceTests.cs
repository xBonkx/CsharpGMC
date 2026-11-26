using System.Xml.Linq;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

/// <summary>Source-level guard for SUM-VIS-01. Does not replace WPF runtime acceptance.</summary>
public sealed class SummaryViewSourceTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Local = "clr-namespace:GmcSmeta.App";

    private static XElement Panel()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "EstimateView.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        return XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "EstimateSummaryPanel.xaml")).Root!;
    }

    [Fact]
    public void MissingOptionsRefreshOnlyCaptionWhenFilterIsOff()
    {
        var source = SummarySource("EstimateView.CostSummary.cs");
        Assert.Contains("if (_summaryMissingOn) SummaryRebuild();", source);
        Assert.Contains("else SummaryUpdateMissingCounts(_summaryMissingChecked.Aggregate(", source);
        Assert.Contains("if (_summaryMissingOn == enabled) return;", source);
        Assert.Contains("if (_summaryMode == key) return;", source);
        Assert.Contains("if (_summarySort == key) return;", source);
        Assert.Contains("ReferenceEquals(_summaryMissingItems, menuItems)", source);
    }

    [Fact]
    public void SummaryMarkupRechecksLockedAndRevisionAndUsesSetEquality()
    {
        var source = SummarySource("EstimateView.CostSummary.cs");
        Assert.Contains("new MenuItem { Header = \"Наценка на СМР…\", IsEnabled = _estimate?.Locked == false }", source);
        Assert.Contains("new HashSet<long>(ids).SetEquals(SummarySelectedIds())", source);
        Assert.Contains("!SummarySnapshotReady || _estimate?.Locked != false || revision != _summaryRevision", source);
        Assert.Contains("IsCancel = true", source);
    }

    [Fact]
    public void SummaryReportCapturesRowsBeforeSaveAndRejectsStaleSnapshot()
    {
        var source = SummarySource("EstimateView.SummaryExcel.cs");
        Assert.Contains("if (!SummarySnapshotReady ||", source);
        Assert.True(source.IndexOf("var snapshot = rows.ToArray();", StringComparison.Ordinal) <
            source.IndexOf("XlsxExport.Save(", StringComparison.Ordinal));
        Assert.Contains("SummaryDisplayedExcel.Fill(sheet, snapshot)", source);
        Assert.DoesNotContain("NumberText.Parse", source);
        Assert.DoesNotContain("FormulaA1 =", source);
    }

    private static string SummarySource(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    [Fact]
    public void SummaryHostIsEmptyAndCompiledPanelPreservesTemplateAndEditHandlers()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "EstimateView.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var editor = XDocument.Load(Path.Combine(app, "EstimateView.xaml"));
        var host = editor.Descendants(Wpf + "Border").Single(e => (string?)e.Attribute(Xaml + "Name") == "SummaryPanel");
        Assert.Empty(host.Elements());
        Assert.Equal("Collapsed", (string?)host.Attribute("Visibility"));
        var panel = Panel();
        Assert.Equal("GmcSmeta.App.EstimateSummaryPanel", (string?)panel.Attribute(Xaml + "Class"));
        Assert.Single(panel.Descendants(Wpf + "Button").Where(e =>
            (string?)e.Attribute("Click") == "SummaryToggleSystem_Click"));
        var grid = panel.Descendants(Wpf + "DataGrid").Single();
        Assert.Equal("SummaryGrid_BeginningEdit", (string?)grid.Attribute("BeginningEdit"));
        Assert.Equal("SummaryGrid_PreparingCellForEdit", (string?)grid.Attribute("PreparingCellForEdit"));
        Assert.Equal("SummaryGrid_CellEditEnding", (string?)grid.Attribute("CellEditEnding"));
        Assert.Equal("SummaryGrid_ContextMenuOpening", (string?)grid.Attribute("ContextMenuOpening"));
        var forwarding = File.ReadAllText(Path.Combine(app, "EstimateSummaryPanel.xaml.cs"));
        Assert.Contains("Owner?.SummaryToggleSystem_Click(sender, e)", forwarding);
        Assert.Contains("Owner?.SummaryGrid_CellEditEnding(sender, e)", forwarding);
        var factory = File.ReadAllText(Path.Combine(app, "EstimateView.SummaryPanel.cs"));
        Assert.Contains("if (host.Child is EstimateSummaryPanel existing) return existing;", factory);
        var logic = File.ReadAllText(Path.Combine(app, "EstimateView.CostSummary.cs"));
        Assert.Contains("if (_visibleEstimateMode != \"summary\" || SummaryPanel is null) return;", logic);
    }

    [Fact]
    public void SummaryDoesNotOwnDuplicateRowsOrRetainSupersededMeasurementSource()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "EstimateView.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var logic = File.ReadAllText(Path.Combine(app, "EstimateView.CostSummary.cs"));
        Assert.Contains("SummaryRecs => _costSummaryData?.Rows ?? Array.Empty<CostSummaryRow>()", logic);
        Assert.DoesNotContain("snapshot.Data.Rows.ToList()", logic);
        var fit = File.ReadAllText(Path.Combine(app, "SummaryColumnFit.cs"));
        Assert.Contains("WeakReference<object>? _measuredSource", fit);
        Assert.Contains("_measuredSource.TryGetTarget(out var measured)", fit);
        Assert.DoesNotContain("private object? _measuredSource", fit);
    }

    [Fact]
    public void SummaryColumnsHaveStableKeysAndDefaultVisibility()
    {
        var grid = Panel().Descendants(Wpf + "DataGrid").Single();
        var columns = grid.Element(Wpf + "DataGrid.Columns")!.Elements().ToArray();
        var keys = columns.Select(c => (string)c.Attribute("SortMemberPath")!).ToArray();
        Assert.Equal(19, keys.Length);
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.Equal("name", keys[0]);
        Assert.Equal("False", (string?)columns[0].Attribute("CanUserReorder"));
        Assert.True(Array.IndexOf(keys, "markup") < Array.IndexOf(keys, "t_cost"));
        var hidden = columns.Where(c => (string?)c.Attribute(Local + "TableColumnMenu.DefaultHidden") == "True").ToArray();
        Assert.Equal(new[] { "m_legacy_margin", "w_legacy_margin", "t_legacy_margin" },
            hidden.Select(c => (string)c.Attribute("SortMemberPath")!).ToArray());
        Assert.All(hidden, c => {
            Assert.Equal("Collapsed", (string?)c.Attribute("Visibility"));
            Assert.Equal("True", (string?)c.Attribute("IsReadOnly"));
        });
        Assert.All(keys, key => Assert.True(Enum.IsDefined(SummaryHeaderLayout.GroupFor(key))));
        Assert.DoesNotContain("supplier", keys);
        Assert.Equal("estimate-summary", (string?)grid.Attribute(Local + "TableColumnMenu.Key"));
        Assert.Equal("True", (string?)grid.Attribute("CanUserReorderColumns"));
        Assert.Equal("True", (string?)grid.Attribute("EnableRowVirtualization"));
        Assert.Equal("True", (string?)grid.Attribute("EnableColumnVirtualization"));
        Assert.Equal("Pixel", (string?)grid.Attribute("VirtualizingPanel.ScrollUnit"));
    }

    [Fact]
    public void MaterialAndWorkRetailUseDifferentBindings()
    {
        var columns = Panel().Descendants(Wpf + "DataGrid.Columns").Single().Elements()
            .ToDictionary(c => (string)c.Attribute("SortMemberPath")!);
        void HasBinding(string key, string property)
        {
            var attributes = columns[key].DescendantsAndSelf().Attributes();
            Assert.Contains(attributes, a => a.Value == "{Binding " + property + "}");
        }
        HasBinding("m_uprice", "PriceUnitText");
        HasBinding("m_msell", "MSellText");
        HasBinding("w_usell", "WorkSellText");
        HasBinding("w_wsell", "WSellText");
        HasBinding("t_sell", "TotalSellText");
    }

    [Fact]
    public void HeaderToolbarAndFiveTilesRemainOnTheSamePanel()
    {
        var panel = Panel();
        var frame = panel.Descendants(Local + "SummaryTableFrame").Single();
        var header = frame.Descendants(Local + "SummaryHeaderBands").Single();
        Assert.Single(frame.Descendants(Wpf + "DataGrid"));
        Assert.Equal("{Binding ElementName=SummaryGrid}", (string?)header.Attribute("SourceGrid"));
        Assert.Equal("False", (string?)header.Attribute("IsHitTestVisible"));
        var toolbar = panel.Descendants(Local + "SummaryToolbarPanel").Single();
        Assert.Equal(new[] { "SummaryActions", "SummaryFilters" },
            toolbar.Elements().Select(e => (string?)e.Attribute(Xaml + "Name")).ToArray());
        foreach (var name in new[] { "SummaryMissingChrome", "SummaryMissingToggle",
                     "SummaryMissingArrow", "SummarySystemFilter", "SummarySearch" })
            Assert.Single(toolbar.Descendants().Where(e => (string?)e.Attribute(Xaml + "Name") == name));
        var tiles = panel.Descendants(Wpf + "Border")
            .Where(e => (string?)e.Attribute("Style") == "{StaticResource SummaryTileFrame}").ToArray();
        Assert.Equal(new[] { "0", "2", "4", "6", "8" }, tiles.Select(e => (string?)e.Attribute("Grid.Column")).ToArray());
    }
}
