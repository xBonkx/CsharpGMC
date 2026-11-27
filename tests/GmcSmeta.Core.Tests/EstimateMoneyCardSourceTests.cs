using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateMoneyCardSourceTests
{
    [Fact]
    public void MoneyFieldsSharePythonStyleAndKeepCommitHandlers()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var view = XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "EstimateView.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement Named(string name) => Assert.Single(view.Descendants().Where(e => (string?)e.Attribute(xaml + "Name") == name));
        var style = Assert.Single(view.Descendants(p + "Style")
            .Where(e => (string?)e.Attribute(xaml + "Key") == "PythonMoneyEdit"));
        string? Value(string property) => (string?)Assert.Single(style.Elements(p + "Setter")
            .Where(e => (string?)e.Attribute("Property") == property)).Attribute("Value");
        Assert.Equal("{StaticResource UiMoneyInputSize}", Value("FontSize"));
        Assert.Equal("SemiBold", Value("FontWeight"));
        Assert.Equal("6,2", Value("Padding"));
        Assert.Equal("26", Value("MinHeight"));
        Assert.Equal("Right", Value("TextAlignment"));
        foreach (var prefix in new[] { "Smr", "Transport", "Pnr" })
        {
            var field = Named(prefix + "Edit");
            Assert.Equal("{StaticResource PythonMoneyEdit}", (string?)field.Attribute("Style"));
            Assert.Equal("TotalsEdit_KeyDown", (string?)field.Attribute("KeyDown"));
            Assert.Equal(prefix + "Edit_Commit", (string?)field.Attribute("LostFocus"));
        }
        var vat = Named("VatText");
        var discount = Named("DiscountTotalText");
        Assert.Equal("{StaticResource UiTotalDetailSize}", (string?)vat.Attribute("FontSize"));
        Assert.Equal("#E6FFFFFF", (string?)vat.Attribute("Foreground"));
        Assert.Equal("0,2,0,0", (string?)vat.Attribute("Margin"));
        Assert.Same(discount, vat.ElementsAfterSelf().First());
        Assert.Equal("Collapsed", (string?)discount.Attribute("Visibility"));
    }

    [Fact]
    public void FooterRefreshShowsDiscountAndDoesNotReserveAnEmptySmrHint()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var view = XDocument.Load(Path.Combine(app, "EstimateView.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => Assert.Single(view.Descendants().Where(e => (string?)e.Attribute(xaml + "Name") == name));
        Assert.Equal("Collapsed", (string?)Named("SmrHintText").Attribute("Visibility"));
        Assert.Equal("ИТОГО с НДС", (string?)Named("TotalCaption").Attribute("Text"));
        var code = File.ReadAllText(Path.Combine(app, "EstimateView.xaml.cs"));
        Assert.Contains("hasDiscount ? \"ИТОГО со скидкой\" : \"ИТОГО с НДС\"", code);
        Assert.Contains("if (hasDiscount)\n            TotalsSummary.Text +=", code.Replace("\r\n", "\n"));
        Assert.Contains("MaterialsText.ClearValue(ToolTipProperty)", code);
        var header = File.ReadAllText(Path.Combine(app, "EstimateView.Header.cs"));
        Assert.Contains("est.Statistics.Materials != 0 && est.Statistics.Smr != 0", header);
        Assert.Contains("(pct || bySys || byPos) && est.Statistics.WorkRows != 0", header);
        Assert.Contains("SmrHintText.Visibility = string.IsNullOrEmpty(SmrHintText.Text)", header);
    }

    [Fact]
    public void PercentModeDisplaysStoredPercentAndCannotSaveItAsMoney()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var code = File.ReadAllText(Path.Combine(app, "EstimateView.xaml.cs"));
        var header = File.ReadAllText(Path.Combine(app, "EstimateView.Header.cs"));
        Assert.Contains("SmrEdit.Text = est.SmrMode == \"percent\"", code);
        Assert.Contains("NumberText.QtyText(est.SmrPercent)", code);
        Assert.Contains("SmrEdit.IsReadOnly = est.Locked || est.MoneyFromStages || (!pct && mode != \"sum\")", header);
        Assert.Contains("SaveTotalsFieldFromEdit(SmrEdit, \"smr_percent\", _estimate.SmrPercent", header);
        Assert.Contains("SmrEdit.Text.Trim() == NumberText.QtyText(_estimate.SmrPercent)", header);
        Assert.Contains("displayedRevision != _serverRevision", header);
        Assert.Contains("box.IsReadOnly) return;", header);
        Assert.Contains("TransportEdit.Text = est.Totals.Transport == 0m ? \"0\" : N2(est.Totals.Transport)", code);
        Assert.Contains("PnrEdit.Text = est.Totals.Pnr == 0m ? \"0\" : N2(est.Totals.Pnr)", code);
    }
}
