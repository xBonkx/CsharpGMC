namespace GmcSmeta.Core.Tests;

public sealed class EstimateStatusSourceTests
{
    [Fact]
    public void FirstLoadUsesDocumentContextAndKeepsWriteErrorsAheadOfSuccess()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var editor = File.ReadAllText(Path.Combine(app, "EstimateView.xaml.cs"));
        Assert.DoesNotContain("SetStatus(\"Смета загружена\", false)", editor);
        Assert.Contains("main.SetEstimateStatus(message, error);", editor);
        var error = editor.IndexOf("SetStatus(\"Ошибка записи: \" + _saveError", StringComparison.Ordinal);
        var readback = editor.IndexOf("else if (_saveAwaitingReadback)", error < 0 ? 0 : error, StringComparison.Ordinal);
        var ready = editor.IndexOf("SetStatus(loaded.Estimate.Title, false);", StringComparison.Ordinal);
        Assert.True(error >= 0 && readback > error && ready > readback);
        var main = File.ReadAllText(Path.Combine(app, "MainWindow.xaml.cs"));
        Assert.Contains("CompleteEstimateOpen(doc, page, loading: true);", main);
        Assert.Contains("CompleteEstimateOpen(doc, cached);", main);
        Assert.Contains("SetEstimateStatus(loading ? $\"{doc.Title} · загрузка сметы…\" : doc.Title, false);", main);
        Assert.Contains("SetEstimateStatus(\"Смета не проверена · редактирование недоступно\", true);", main);
        Assert.Contains("StatusText.Foreground = new System.Windows.Media.SolidColorBrush(error", main);
    }
}
