namespace GmcSmeta.Core.Tests;

public sealed class FinancialArchiveDialogKeyboardSourceTests
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
    public void ArchiveYearDoesNotImplicitlyMoveCardOnEnter()
    {
        var xaml = Source("ArchiveYearDialog.xaml");
        var code = Source("ArchiveYearDialog.xaml.cs");
        Assert.Contains("IsCancel=\"True\"", xaml);
        Assert.DoesNotContain("IsDefault=\"True\"", xaml);
        Assert.DoesNotContain("YearBox_KeyDown", xaml);
        Assert.DoesNotContain("YearBox_KeyDown", code);
        Assert.Contains("YearBox.Focus(); YearBox.SelectAll();", code);
        Assert.Contains("year is >= 2000 and <= 2100", code);
    }

    [Fact]
    public void DiscountRequiresExplicitApplyAndStartsOnMode()
    {
        var code = Source("EstimateDiscountDialog.cs");
        Assert.DoesNotContain("IsDefault=true", code);
        Assert.Contains("IsCancel=true,TabIndex=6", code);
        Assert.Contains("Loaded+=(_,_)=>_mode.Focus()", code);
        Assert.Contains("if(Result?.Changed==true)DialogResult=true", code);
    }

    [Fact]
    public void TransportCancelsOnEscapeAndFocusesEditableAmount()
    {
        var code = Source("CostTransportDialog.cs");
        Assert.Contains("IsCancel = true", code);
        Assert.Contains("var initial = locked ? _cost : _customer;", code);
        Assert.Contains("initial.SelectAll();", code);
        Assert.Contains("cancel.Click += (_, _) => DialogResult = false;", code);
    }

    [Fact]
    public void SignedPdfFocusesListWithoutImplicitFileSelection()
    {
        var code = Source("AgreementPdfVersionDialog.cs");
        Assert.Contains("dialog.Loaded += (_, _) => list.Focus()", code);
        Assert.Contains("IsEnabled = false", code);
        Assert.Contains("IsCancel = true", code);
        Assert.DoesNotContain("SelectedIndex = 0", code);
        Assert.DoesNotContain("IsDefault = true", code);
    }
}
