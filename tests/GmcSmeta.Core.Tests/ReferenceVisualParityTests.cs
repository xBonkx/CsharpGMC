namespace GmcSmeta.Core.Tests;

public sealed class ReferenceVisualParityTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root=new(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"src","GmcSmeta.App",file)))
            root=root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName,"src","GmcSmeta.App",file));
    }

    [Fact]
    public void SearchDecorationUsesOriginalGlyphsAndExistingInput()
    {
        var source=Source("ReferenceWindowUi.cs");
        Assert.Contains("FindResource(\"GlyphSearch\")",source);
        Assert.Contains("\"IconOnlyX\"",source);
        Assert.Contains("Source=input",source);
        Assert.Contains("input.Clear();input.Focus();",source);
        Assert.Contains("VectorGlyph.Create",source);
        Assert.Contains("IsHitTestVisible = false",Source("VectorGlyph.cs"));
        Assert.Contains("ReferenceWindowUi.Search(_input",Source("WorkspaceQueryWindows.cs"));
        Assert.Contains("ReferenceWindowUi.Search(_filter",Source("WorkspaceQueryWindows.cs"));
        Assert.Contains("ReferenceWindowUi.Search(_filter",Source("CatalogPickerWindow.cs"));
    }

    [Fact]
    public void CatalogHistoryCanResizeWithoutChangingPageOrRevisionGuards()
    {
        var source=Source("WorkspaceQueryWindows.cs");
        Assert.Contains("ReferenceWindowUi.HistorySplitter()",source);
        Assert.Contains("historyPanel.Children.Add(_history)",source);
        Assert.Contains("generation != _request",source);
        Assert.Contains("Selected?.Id == id",source);
        Assert.Contains("TableColumnMenu.SetKey(_history, \"catalog-price-history\")",source);
        Assert.Contains("GridResizeBehavior.PreviousAndNext",Source("ReferenceWindowUi.cs"));
    }

    [Fact]
    public void PickerKeepsPerItemQuantitiesAndPageSelection()
    {
        var source=Source("CatalogPickerWindow.cs");
        Assert.Contains("Добавить в КП",source);
        Assert.Contains("SavePageState();",source);
        Assert.Contains("SetSelectedOnPage(_rows, selected)",source);
        Assert.Contains("TryQty(row.Qty",source);
        Assert.Contains("_expectedRevision",source);
    }

    [Fact]
    public void OrganizationLogoAndTagFieldsKeepExistingMutationHandlers()
    {
        var source=Source("WorkspaceReferenceWindows.cs");
        Assert.Contains("ReferenceWindowUi.LogoPreview(_logoView)",source);
        Assert.Contains("_logo = null; _logoView.Source = null; _dirty = true;",source);
        Assert.Contains("IconLabelUpload",source);
        Assert.Contains("IconLabelX",source);
        Assert.Contains("TextWrapping=TextWrapping.Wrap",source);
        Assert.Contains("_dirty && MessageBox.Show",source);
        Assert.Contains("нет логотипа",Source("ReferenceWindowUi.cs"));
    }
}
