using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class DesignAuditSourceTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, file))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, file));
    }

    [Theory]
    [InlineData("xaml-metric")]
    [InlineData("cs-creation")]
    [InlineData("cs-assignment")]
    [InlineData("path-role")]
    [InlineData("grid-style")]
    [InlineData("data_fingerprint")]
    [InlineData("GmcSmetaPilot_")]
    public void StaticAuditCoversRequiredEvidence(string contract)
        => Assert.Contains(contract, Source("tools/local-qa/design_audit.py"));

    [Fact]
    public void VisualTreeReportsValueSourceAndBounds()
    {
        var source = Source("tests/GmcSmeta.App.Checks/VisualAuditChecks.cs");
        Assert.Contains("TransformBounds", source);
        Assert.Contains("DependencyPropertyHelper.GetValueSource", source);
        Assert.Contains("templatedParent", source);
        Assert.Contains("marginSource", source);
        Assert.Contains("paddingSource", source);
    }

    [Theory]
    [InlineData("EstimateMaterialsPanel.xaml")]
    [InlineData("EstimateSummaryPanel.xaml")]
    public void MainToolbarGlyphsUseEighteenDip(string file)
    {
        XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var doc = XDocument.Parse(Source("src/GmcSmeta.App/" + file));
        var direct = doc.Descendants(w + "Viewbox").Where(v =>
            v.Ancestors(w + "StackPanel").Any(p => (string?)p.Attribute("Orientation") == "Horizontal") &&
            v.Descendants(w + "Path").Any(p => new[] { "GlyphList", "GlyphBook", "GlyphFile", "GlyphEdit",
                "GlyphLayers", "GlyphSort", "GlyphAlert" }.Any(g => (string?)p.Attribute("Data") == "{StaticResource " + g + "}")));
        Assert.NotEmpty(direct);
        foreach (var glyph in direct)
        {
            Assert.Equal("18", (string?)glyph.Attribute("Width"));
            Assert.Equal("18", (string?)glyph.Attribute("Height"));
        }
    }
}
