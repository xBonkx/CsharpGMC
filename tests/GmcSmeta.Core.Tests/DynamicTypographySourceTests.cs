using System.Text.RegularExpressions;

namespace GmcSmeta.Core.Tests;

public sealed class DynamicTypographySourceTests
{
    private static string AppDirectory()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App")))
            root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "src", "GmcSmeta.App");
    }

    [Fact]
    public void CodeCreatedUiFontSizesUseNamedRolesOrMeasuredScale()
    {
        // Numeric spreadsheet font sizes are part of the print/export format, not WPF typography.
        // Match direct literal assignments and dependency-property setters; computed scales and
        // FindResource/SetResourceReference are intentionally allowed.
        var direct = new Regex(@"(?<!\.Font\.)\bFontSize\s*=\s*\d+(?:\.\d+)?(?:[dDfF])?\b");
        var setter = new Regex(@"\bFontSizeProperty\s*,\s*\d+(?:\.\d+)?(?:[dDfF])?\b");
        var violations = new List<string>();
        var app = AppDirectory();
        foreach (var file in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(app, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "obj" or "bin")))
        {
            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (direct.IsMatch(line) || setter.IsMatch(line))
                    violations.Add($"{Path.GetRelativePath(app, file)}:{lineNo}: {line.Trim()}");
            }
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("PtoVorWindow.cs")]
    [InlineData("PtoCertificatesWindow.cs")]
    [InlineData("MetalRegisterWindow.cs")]
    [InlineData("MetalDialogs.cs")]
    [InlineData("KsActPropertiesDialog.cs")]
    public void DynamicWindowsUseCommonChromeAndWorkArea(string file)
    {
        var source = File.ReadAllText(Path.Combine(AppDirectory(), file));
        Assert.Contains("DynamicFormLayout.Apply(", source);
    }

    [Fact]
    public void StageCardsUseResourceBasedFontSizesAndReadableLongValues()
    {
        var source = File.ReadAllText(Path.Combine(AppDirectory(), "EstimateView.InlineStages.Visual.cs"));
        Assert.Contains("amountText.SetResourceReference(TextBlock.FontSizeProperty, \"UiBodySize\")", source);
        Assert.Contains("statusText.SetResourceReference(TextBlock.FontSizeProperty, \"UiCaptionSize\")", source);
        Assert.Contains("TextWrapping = TextWrapping.Wrap, ToolTip = amount", source);
        Assert.Contains("TextWrapping = TextWrapping.Wrap, ToolTip = status", source);
        Assert.Contains("TextWrapping = TextWrapping.Wrap, ToolTip = name", source);
    }
}
