namespace GmcSmeta.Core.Tests;

public sealed class ScopedDialogKeyboardSourceTests
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
    public void PromptDelegatesEnterAndEscapeToDialogButtons()
    {
        var source = Source("PromptDialog.cs");
        Assert.Contains("IsDefault = true, TabIndex = 1", source);
        Assert.Contains("IsCancel = true, TabIndex = 2", source);
        Assert.Contains("TabIndex = 0", source);
        Assert.Contains("Box.Focus(); Box.SelectAll();", source);
        Assert.DoesNotContain("Box.KeyDown", source);
    }

    [Fact]
    public void HelpDoesNotCloseOnDefaultEnterDuringNavigation()
    {
        var source = Source("HelpDialog.cs");
        Assert.DoesNotContain("IsDefault = true", source);
        Assert.Contains("IsCancel = true, TabIndex = 2", source);
        Assert.Contains("Loaded += (_, _) => _list.Focus()", source);
    }

    [Fact]
    public void SectionMoveRequiresExplicitSelectionAndConfirmation()
    {
        var source = Source("SectionPickerDialog.cs");
        Assert.Contains("SelectedIndex = -1, TabIndex = 0", source);
        Assert.Contains("IsEnabled = false, TabIndex = 1", source);
        Assert.Contains("ok.IsEnabled = SelectedSectionId is not null", source);
        Assert.Contains("if (SelectedSectionId is not null) DialogResult = true", source);
        Assert.Contains("IsCancel = true, TabIndex = 2", source);
        Assert.DoesNotContain("IsDefault = true", source);
        Assert.Contains("Loaded += (_, _) => _sections.Focus()", source);
    }
}
