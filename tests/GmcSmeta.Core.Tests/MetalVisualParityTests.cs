using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class MetalVisualParityTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root=new(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"src","GmcSmeta.App",file)))
            root=root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName,"src","GmcSmeta.App",file));
    }

    [Fact]
    public void StockPaletteMatchesPythonAndKeepsSelection()
    {
        var xaml=Source("MetalWarehouseWindow.xaml");
        var doc=XDocument.Parse(xaml);
        XNamespace x="http://schemas.microsoft.com/winfx/2006/xaml";
        var grid=Assert.Single(doc.Descendants().Where(n=>(string?)n.Attribute(x+"Name")=="StockGrid"));
        Assert.Equal("42",(string?)grid.Attribute("ColumnHeaderHeight"));
        foreach(var (property,style) in new[]{("Ours","MetalOwnedCell"),("OursPieces","MetalOwnedCell"),
            ("Available","MetalOwnedCell"),("Supplier","MetalSupplierCell"),("SupplierPieces","MetalSupplierCell"),
            ("Deficit","MetalDeficitCell")})
        {
            var column=Assert.Single(grid.Descendants().Where(n=>n.Name.LocalName=="DataGridTextColumn" &&
                ((string?)n.Attribute("Binding"))?.StartsWith("{Binding "+property+(property.EndsWith("Pieces")?"}":","),StringComparison.Ordinal)==true));
            Assert.Equal("{StaticResource "+style+"}",(string?)column.Attribute("CellStyle"));
        }
        Assert.Contains("#EAF5ED",xaml);Assert.Contains("#EAF2FC",xaml);Assert.Contains("#FCE8E6",xaml);
        Assert.Contains("GridSelectionBrush",xaml);
        Assert.Contains("Value=\"27\"",xaml);
        Assert.Contains("PythonTileAccent",xaml);
        Assert.Contains("TotalDeficit.Foreground = totals[4] > 0",Source("MetalWarehouseWindow.xaml.cs"));
    }

    [Fact]
    public void ExistingWarehouseCommandsRetainHandlersWithoutExtraIcons()
    {
        var doc=XDocument.Parse(Source("MetalWarehouseWindow.xaml"));
        foreach(var handler in new[]{"Operation_Click","Import_Click","Export_Click","Refresh_Click","LotAdd_Click","LotPay_Click","LotReceive_Click"})
        {
            var b=Assert.Single(doc.Descendants().Where(n=>n.Name.LocalName=="Button" &&
                (string?)n.Attribute("Click")==handler));
            Assert.Null(b.Attribute("ContentTemplate"));
            Assert.Equal("{StaticResource PythonToolbarButton}",(string?)b.Attribute("Style"));
        }
        Assert.Contains("StockFilter_Changed",doc.ToString());
        Assert.Contains("JournalSearch_Click",doc.ToString());
    }

    [Fact]
    public void RegisterPresentationDoesNotReplaceCellIdentityOrEditGuards()
    {
        var source=Source("MetalRegisterWindow.cs");
        Assert.Contains("ColumnHeaderHeight = _heading == 0 ? 32 : 72",source);
        Assert.Contains("0xE8,0xEF,0xF9",source);
        Assert.Contains("cell.HasFormula || cell.CachedValue.IsNumber",source);
        Assert.Contains("cell.FormulaA1.TrimStart",source);
        Assert.Contains("e.Row.Header = e.Row.Item is MetalGridRow row ? row.ExcelRow",source);
        Assert.Contains("TableColumnMenu.SetStableId(column",source);
        Assert.Contains("e.Cancel = excel.HasFormula || IsMergedFollower",source);
        Assert.Contains("cell.HasFormula || IsMergedFollower(row,excelColumn)",source);
        Assert.Contains("_formulas.Clear();",source);
    }

    [Fact]
    public void OpeningPreviewHighlightsExistingMismatchOnly()
    {
        var source=Source("MetalDialogs.cs");
        Assert.Contains("binding == \"Balance\"",source);
        Assert.Contains("0xFF,0xF2,0xD7",source);
        Assert.Contains("Binding = new Binding(\"Match\"), Value = \"⚠\"",source);
        Assert.Contains("GridSelectionBrush",source);
        Assert.Contains("decimal.Round(MetalWarehouseRules.Sqm(stock-need),2)",source);
    }
}
