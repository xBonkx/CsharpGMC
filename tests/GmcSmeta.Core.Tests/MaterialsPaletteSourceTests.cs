using System.Text.RegularExpressions;

namespace GmcSmeta.Core.Tests;

/// <summary>Source-level parity with Python cost_view.py at aa6b34dcf0314df93086a297f01d549ff58cf705.</summary>
public sealed class MaterialsPaletteSourceTests
{
    [Fact]
    public void PaletteAndMenuGlyphsMatchPython()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "EstimateView.Materials.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var code = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "EstimateView.Materials.cs"));
        var start = code.IndexOf("MatRowColors =", StringComparison.Ordinal);
        var end = code.IndexOf("private static string MatMoney", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var pairs = Regex.Matches(code[start..end], @"\(""([^""]+)"", ""(#[A-F0-9]{6})""\)")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToArray();
        Assert.Equal(new[]
        {
            ("Жёлтый", "#FFF1A8"),
            ("Зелёный", "#D4F0C8"),
            ("Красный", "#F9D0D0"),
            ("Голубой", "#D2E6FF"),
            ("Оранжевый", "#FFDDB3"),
            ("Сиреневый", "#E6DAFA"),
            ("Серый", "#E3E6EA"),
        }, pairs);
        Assert.Contains("Width = 16, Height = 16, CornerRadius = new CornerRadius(0)", code);
        Assert.Contains("MaterialsMarkup_Click, \"List\");", code);
        Assert.Contains("MaterialsSupplier_Click, \"Building\");", code);
        Assert.Contains("ContextMenuIcons.Create(\"Цвет строки\", \"Edit\")", code);
        Assert.Contains("ContextMenuIcons.Create(\"Без цвета\", \"X\")", code);
        Assert.Contains("FillMaterialsColorItems(menu.Items, CanExecute);", code);
        Assert.Contains("FillMaterialsColorItems(colors.Items, CanExecute);", code);
    }
}
