namespace GmcSmeta.Core.Tests;

public sealed class ContextNavigationAndClearSourceTests
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
    public void NavigationTargetsHeaderNotExpandedChildrenOrOldSelection()
    {
        var main = Source("MainWindow.xaml.cs");
        Assert.Contains("NavigationTree.SelectedItem as TreeViewItem", main);
        Assert.Contains("Border { Name: \"HeaderBorder\", TemplatedParent: TreeViewItem item }", main);
        Assert.Contains("if (node is TreeViewItem) return null;", main);
        Assert.Contains("x:Name=\"HeaderBorder\"", Source("MainWindow.xaml"));
        Assert.Contains("ReferenceEquals(currentNode, target)", main);
        Assert.Contains("Equals(fresh, selected)", main);
        Assert.Contains("Equals(current, doc)", main);
        Assert.Contains("RunDocumentCommand(() => DeleteSqlServerContractFlow(doc))", main);
        Assert.Contains("AddDocumentFileMenu(menu, doc, ValidDocumentTarget)", main);
        Assert.Contains("menuTarget?.Invoke() != false", main);
    }

    [Theory]
    [InlineData("Цена клиенту", "price")]
    [InlineData("Закуп за ед.", "purchase_price")]
    [InlineData("Факт по счёту", "cost_price")]
    [InlineData("Поставщик", "supplier")]
    [InlineData("Пометки", "cost_note")]
    public void MaterialsClearUsesActualColumnAndCapturedDocument(string caption, string field)
    {
        var code = Source("EstimateView.Materials.cs");
        Assert.Contains("\"" + caption + "\" => (\"" + field + "\",", code);
        Assert.Contains("ContextMenuTarget.TargetCell(MaterialsGrid, e)", code);
        Assert.Contains("CanExecute() && MaterialsClearIds(clear.Field).SequenceEqual(clearIds)", code);
        Assert.Contains("contractId != _contractId || connectionString != _sqlConnectionString", code);
        Assert.Contains("connectionString, contractId, ids, field, revision", code);
        Assert.Contains("MessageBoxResult.No", code);
    }
}
