namespace GmcSmeta.Core.Tests;

public sealed class CellRenderingResourcesSourceTests
{
    private static string Source(string name)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", name)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", name));
    }

    [Fact]
    public void SharedCellAndMenuBrushesAreFrozenAndColorsArePreserved()
    {
        var ks = Source("KsRowVm.cs");
        Assert.Contains("TextBrush = FrozenBrush(0x0F, 0x17, 0x2A)", ks);
        Assert.Contains("WarnBrush = FrozenBrush(0xB4, 0x53, 0x09)", ks);
        Assert.Contains("DangerBrush = FrozenBrush(0xDC, 0x26, 0x26)", ks);
        Assert.Contains("brush.Freeze();", ks);
        var icons = Source("ContextMenuIcons.cs");
        Assert.Contains("NormalStroke = FrozenStroke(0x64, 0x74, 0x8B)", icons);
        Assert.Contains("DangerStroke = FrozenStroke(0xDC, 0x26, 0x26)", icons);
        Assert.Contains("HighlightStroke = FrozenStroke(0x1E, 0x40, 0xAF)", icons);
        Assert.Contains("DisabledStroke = FrozenStroke(0x94, 0xA3, 0xB8)", icons);
        Assert.Contains("brush.Freeze();", icons);
        Assert.Contains("Source = item", icons); // item-specific style/binding remains separate
        Assert.DoesNotContain("ColorConverter.ConvertFromString", icons);
    }

    [Fact]
    public void IndentCacheIsBoundedAndUnusualLevelsKeepOriginalFormula()
    {
        var code = Source("LevelIndentConverter.cs");
        Assert.Contains("Enumerable.Range(0, 16)", code);
        Assert.Contains("Math.Max(0, value is int i ? i : 0)", code);
        Assert.Contains("level < Common.Length ? Common[level] : new Thickness(level * 14, 0, 0, 0)", code);
    }
}
