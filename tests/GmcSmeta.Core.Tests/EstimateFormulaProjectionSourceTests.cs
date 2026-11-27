using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateFormulaProjectionSourceTests
{
    [Fact]
    public void ReferencesPreserveFirstAppearanceAndIgnoreOverflowAndZero()
    {
        Assert.Equal(new[] { 3, 1, 2 }, EstimateCellFormula.References(
            "=[3]+[1]+[3]+[0]+[0002]+[999999999999999999999]"));
        Assert.Empty(EstimateCellFormula.References(null));
        Assert.Empty(EstimateCellFormula.References("=2+2"));
    }

    [Fact]
    public void FormulaAdornerReusesFrozenPaletteAndSkipsUnchangedReferences()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "EstimateFormulaAdorner.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var code = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "EstimateFormulaAdorner.cs"));
        Assert.Contains("ink.Freeze();", code);
        Assert.Contains("fill.Freeze();", code);
        Assert.Contains("border.Freeze();", code);
        Assert.Contains("if (_refs.SequenceEqual(refs)) return;", code);
        Assert.Contains("Color.FromArgb(30, color.R, color.G, color.B)", code);
        Assert.Contains("new Pen(ink, 2)", code);
    }
}
