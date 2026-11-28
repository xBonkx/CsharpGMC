using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class SharedTextRoleCoverageTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string AppDirectory()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App")))
            root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "src", "GmcSmeta.App");
    }

    [Fact]
    public void DirectTextBlockHintsUseSharedContrastRoleAcrossXaml()
    {
        var app = AppDirectory();
        var files = Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(app, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "obj" or "bin")).ToArray();
        Assert.True(files.Length >= 40, "Expected complete app XAML tree.");
        var violations = new List<string>();
        foreach (var file in files)
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var element in doc.Descendants(W + "TextBlock"))
            {
                var foreground = (string?)element.Attribute("Foreground");
                if (foreground is "#64748B" or "#94A3B8")
                    violations.Add(Path.GetRelativePath(app, file) + ": " +
                        ((string?)element.Attribute(X + "Name") ?? (string?)element.Attribute("Text") ?? "unnamed") +
                        " uses " + foreground);
            }
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("StageMoneyDialog.xaml", "SaveButton", "SmrBox")]
    [InlineData("DeletePositionsDialog.xaml", "DeleteButton", "NamesList")]
    public void LongDialogBodyScrollsIndependentlyOfActions(string file, string action, string body)
    {
        var doc = XDocument.Load(Path.Combine(AppDirectory(), file));
        XElement Named(string name) =>
            Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Name") == name));
        Assert.NotNull(doc.Root!.Attribute("MaxHeight"));
        Assert.Empty(Named(action).Ancestors(W + "ScrollViewer"));
        Assert.Contains(Named(body).Ancestors(W + "ScrollViewer"), scroll =>
            (string?)scroll.Attribute("VerticalScrollBarVisibility") == "Auto" &&
            (string?)scroll.Attribute("HorizontalScrollBarVisibility") == "Disabled");
    }

    [Theory]
    [InlineData("CostSummaryPane.xaml")]
    [InlineData("EstimateTotalsPanel.xaml")]
    public void TotalsTableUsesSharedFontRole(string file)
    {
        var doc = XDocument.Load(Path.Combine(AppDirectory(), file));
        Assert.Contains(doc.Descendants(W + "DataGrid"),
            grid => (string?)grid.Attribute("FontSize") == "{StaticResource UiTotalsTableSize}");
    }

    [Fact]
    public void BatchDeleteNamesWrapWithoutLosingCompleteTooltip()
    {
        var doc = XDocument.Load(Path.Combine(AppDirectory(), "DeletePositionsDialog.xaml"));
        var names = Assert.Single(doc.Descendants(W + "ItemsControl")
            .Where(e => (string?)e.Attribute(X + "Name") == "NamesList"));
        var text = Assert.Single(names.Descendants(W + "TextBlock"));
        Assert.Equal("Wrap", (string?)text.Attribute("TextWrapping"));
        Assert.Equal("{Binding}", (string?)text.Attribute("ToolTip"));
        Assert.Null(text.Attribute("TextTrimming"));
    }
    [Theory]
    [InlineData("ReferenceWindowUi.cs")]
    [InlineData("AttachmentWindow.cs")]
    [InlineData("WorkspaceReferenceWindows.cs")]
    [InlineData("MetalRegisterWindow.cs")]
    public void DynamicSecondaryLabelsDoNotUseLegacySlateGray(string file)
    {
        var source = File.ReadAllText(Path.Combine(AppDirectory(), file));
        Assert.DoesNotContain("Brushes.SlateGray", source);
        Assert.True(source.Contains("UiMutedTextBrush", StringComparison.Ordinal) ||
            source.Contains("DynamicFormLayout.Secondary(", StringComparison.Ordinal));
    }

}
