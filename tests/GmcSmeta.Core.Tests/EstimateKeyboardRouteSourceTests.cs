namespace GmcSmeta.Core.Tests;

public sealed class EstimateKeyboardRouteSourceTests
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
    public void AltInsertUsesNormalizedSystemKey()
    {
        var source = Source("EstimateView.Undo.cs");
        Assert.Contains("var key = e.Key == Key.System ? e.SystemKey : e.Key;", source);
        Assert.Contains("if (inTable && key == Key.Insert)", source);
        Assert.Contains("AddSubsection_Click(sender, e);", source);
    }

    [Fact]
    public void SectionKeyboardCommandsUseExistingGuardedRoutes()
    {
        var source = Source("EstimateView.Undo.cs");
        Assert.Contains("RenameSection_Click(sender, e);", source);
        Assert.Contains("DeleteSection_Click(sender, e);", source);
        Assert.Contains("ReferenceEquals(EstimateGrid.CurrentItem, selectedSection)", source);
        Assert.Contains("if (e.IsRepeat) return;", source);
        Assert.Contains("!CommandSelectionCurrent(selection) || selection.Any(row => row.IsSystem)", source);
        Assert.Contains("TextBoxBase or PasswordBox or ComboBox", source);
    }

    [Fact]
    public void KeyboardMenuRejectsStaleCellAndLeavesEditorMenuAlone()
    {
        var source = Source("EstimateView.TemporaryView.cs");
        Assert.Contains("Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox", source);
        Assert.Contains("!EstimateGrid.IsKeyboardFocusWithin", source);
        Assert.Contains("!EstimateGrid.SelectedItems.Contains(targetCell.Item)", source);
        Assert.Contains("!ReferenceEquals(EstimateGrid.CurrentItem, targetCell.Item)", source);
    }
}
