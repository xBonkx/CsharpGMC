namespace GmcSmeta.Core.Tests;

public sealed class ObjectCardTransitionSourceTests
{
    [Fact]
    public void ObjectPageIsPublishedOnlyAfterReadAndCardsArePrepared()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "MainWindow.ObjectCards.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var cards = File.ReadAllText(Path.Combine(app, "MainWindow.ObjectCards.cs"));
        var main = File.ReadAllText(Path.Combine(app, "MainWindow.xaml.cs"));
        var navigation = File.ReadAllText(Path.Combine(app, "MainWindow.Navigation.cs"));
        var read = cards.IndexOf("await Task.Run", StringComparison.Ordinal);
        Assert.True(read > 0);
        var pending = cards[..read];
        Assert.Contains("PageHost.IsEnabled = false", pending);
        Assert.Contains("++_estimateOpenGeneration", pending);
        Assert.Contains("CancelEstimateCacheValidation()", pending);
        Assert.DoesNotContain("ShowPage(ObjectPage)", pending);
        Assert.DoesNotContain("DocumentCards.ItemsSource = null", pending);
        Assert.DoesNotContain("ObjectTotals.Visibility = ObjectCardsScroll.Visibility = Visibility.Collapsed", pending);
        var publish = cards.IndexOf("// A different object/estimate", StringComparison.Ordinal);
        Assert.True(publish > read);
        var success = cards[publish..];
        Assert.Contains("if (!IsCurrent()) return;", success);
        Assert.Contains("_objectCardTransitionPending && _selectedObject?.Id == o.Id", pending);
        Assert.True(success.IndexOf("DocumentCards.ItemsSource = ObjectCardLayout.AppendNewContract", StringComparison.Ordinal) <
            success.IndexOf("ShowPage(ObjectPage)", StringComparison.Ordinal));
        Assert.Contains("documents.Cards, SqlWritesEnabled && !o.Archived", success);
        Assert.Contains("_objectCardTransitionPending = false", main);
        Assert.Contains("PageHost.IsEnabled = true", main);
        Assert.Contains("if (_objectCardTransitionPending || !ReferenceEquals(PageHost.Content, ObjectPage)", main);
        Assert.Contains("_openDocumentId is not null && PageHost.Content is EstimateView editing", navigation);
    }
}
