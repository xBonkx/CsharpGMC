namespace GmcSmeta.Core.Tests;

public sealed class ContextMenuOpeningIoSourceTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    [Fact]
    public void DocumentFileMenuUsesAppliedPtoModeButExecutionRechecksPersistedMode()
    {
        var code = Source("MainWindow.xaml.cs");
        var start = code.IndexOf("private void AddDocumentFileMenu(", StringComparison.Ordinal);
        var end = code.IndexOf("private void OpenContractDocuments(", start, StringComparison.Ordinal);
        var actionEnd = code.IndexOf("private void CardLetters_Click(", end, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start && actionEnd > end);
        var menu = code[start..end];
        var action = code[end..actionEnd];
        Assert.Contains("if (PtoModeVisibility == Visibility.Visible)", menu);
        Assert.DoesNotContain("PtoUserSettings.Enabled", menu);
        Assert.DoesNotContain("File.Exists(", menu);
        Assert.DoesNotContain("Directory.Exists(", menu);
        Assert.Contains("if (ValidTarget()) OpenContractDocuments(doc, true)", menu);
        Assert.Contains("(pto && !PtoUserSettings.Enabled)", action);
        Assert.Contains("CanOpenContractWorkspace(doc)", action);
        var pto = Source("MainWindow.Pto.cs");
        Assert.Contains("var enabled=PtoUserSettings.Enabled", pto);
        Assert.Contains("PtoUserSettings.Enabled=PtoModeToggle.IsChecked", pto);
        Assert.Contains("PtoModeVisibility=PtoMenuItem.Visibility=", pto);
    }
}
