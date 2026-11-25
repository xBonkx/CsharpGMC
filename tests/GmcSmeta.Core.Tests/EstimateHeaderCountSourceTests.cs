using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateHeaderCountSourceTests
{
    [Fact]
    public void CountsAreInTitleTooltipRatherThanAnExtraHeaderRow()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var view = XDocument.Load(Path.Combine(app, "EstimateView.xaml"));
        Assert.DoesNotContain(view.Descendants(), e => (string?)e.Attribute(xaml + "Name") == "CountText");
        Assert.Single(view.Descendants().Where(e => (string?)e.Attribute(xaml + "Name") == "TitleText"));
        var code = File.ReadAllText(Path.Combine(app, "EstimateView.xaml.cs"));
        Assert.DoesNotContain("CountText.", code);
        Assert.Contains("TitleText.ToolTip = $\"{est.Title}\\n\"", code);
        Assert.Contains("CountForm(est.Positions,", code);
        Assert.Contains("CountForm(est.Systems,", code);
    }
}
