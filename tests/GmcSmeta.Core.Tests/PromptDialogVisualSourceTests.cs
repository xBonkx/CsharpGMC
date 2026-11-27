namespace GmcSmeta.Core.Tests;

public sealed class PromptDialogVisualSourceTests
{
    [Fact]
    public void SharedPromptUsesStandardChromeAndPreservesInputContract()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        const string relative = "src/GmcSmeta.App/PromptDialog.cs";
        while (root is not null && !File.Exists(Path.Combine(root.FullName, relative)))
            root = root.Parent;
        Assert.NotNull(root);
        var code = File.ReadAllText(Path.Combine(root!.FullName, relative));
        Assert.Contains("ResizeMode = ResizeMode.NoResize", code);
        Assert.Contains("SizeToContent = SizeToContent.Height", code);
        Assert.Contains("TextWrapping = TextWrapping.Wrap", code);
        Assert.Contains("PythonToolbarButton", code);
        Assert.Contains("PythonToolbarPrimary", code);
        Assert.Contains("UiH2Size", code);
        Assert.Contains("AutomationProperties.SetName(Box, label)", code);
        Assert.Contains("Box.Text.Trim()", code);
        Assert.Contains("Box.Focus(); Box.SelectAll();", code);
        Assert.Contains("IsDefault = true", code);
        Assert.Contains("IsCancel = true", code);
        Assert.DoesNotContain("string.IsNullOrWhiteSpace", code);
    }
}
