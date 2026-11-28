namespace GmcSmeta.Core.Tests;

public sealed class KsCloseSectionsSourceTests
{
    [Fact]
    public void ModalSelectionIsSeparateFromExistingBatchWrite()
    {
        DirectoryInfo? root=new(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"src","GmcSmeta.App","KsCloseSectionsDialog.cs")))
            root=root.Parent;
        Assert.NotNull(root);
        var app=Path.Combine(root!.FullName,"src","GmcSmeta.App");
        var dialog=File.ReadAllText(Path.Combine(app,"KsCloseSectionsDialog.cs"));
        var editor=File.ReadAllText(Path.Combine(app,"ActsEditorView.xaml.cs"));
        Assert.Contains("IsChecked = rest > 0.005m",dialog);
        Assert.Contains("owner.FindResource",dialog);
        Assert.Contains("Window.GetWindow(owner)",dialog);
        Assert.DoesNotContain("SqlServer",dialog);
        Assert.Contains("KsCloseSectionsDialog.Show(this, current.Number,",editor);
        Assert.Contains("if (chosenTops is null) return;",editor);
        Assert.Contains("await SaveBatchAsync(edits,",editor);
    }
}
