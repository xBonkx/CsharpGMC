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
        Assert.Equal("16", Value("FontSize"));
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
        Assert.Equal("12", (string?)vat.Attribute("FontSize"));
        Assert.Equal("#E6FFFFFF", (string?)vat.Attribute("Foreground"));
        Assert.Equal("0,2,0,0", (string?)vat.Attribute("Margin"));
        Assert.Same(discount, vat.ElementsAfterSelf().First());
        Assert.Equal("Collapsed", (string?)discount.Attribute("Visibility"));
    }
}
