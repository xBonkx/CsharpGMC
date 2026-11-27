using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class KsActTransitionSourceTests
{
    [Fact]
    public void ActLoadKeepsPublishedSheetUntilTheNextSnapshotIsReady()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ActsEditorView.Loading.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var loader = File.ReadAllText(Path.Combine(app, "ActsEditorView.Loading.cs"));
        var editor = File.ReadAllText(Path.Combine(app, "ActsEditorView.xaml.cs"));
        var begin = loader.IndexOf("private async Task ReloadAsync(", StringComparison.Ordinal);
        var read = loader.IndexOf("await Task.Run", begin, StringComparison.Ordinal);
        Assert.True(begin >= 0 && read > begin);
        var pending = loader[begin..read];
        Assert.Contains("BeginActLoadingPresentation();", pending);
        Assert.DoesNotContain("ShowActUnavailable(", pending);
        Assert.DoesNotContain("ClearActSheetState();", pending);
        Assert.DoesNotContain("_actId = requested", pending);
        var publish = loader[read..];
        Assert.True(publish.IndexOf("token.IsCancellationRequested || generation != _actLoadGeneration", StringComparison.Ordinal) <
            publish.IndexOf("ClearActSheetState();", StringComparison.Ordinal));
        Assert.True(publish.IndexOf("ApplyActSnapshot(loaded);", StringComparison.Ordinal) <
            publish.IndexOf("EndActLoadingPresentation();", StringComparison.Ordinal));
        Assert.Contains("EditorRoot.IsEnabled = false;", loader);
        Assert.Contains("EditorRoot.IsEnabled = true;", loader);
        Assert.Contains("_actId = requested; // Retry the requested act", loader);
        Assert.Contains("public long SelectedActId => _resumeActId ?? (_loadingAct ? _activeReadActId : _actId);", editor);
        Assert.Contains("if (_building || _loadingAct || _loadFailed || string.IsNullOrEmpty(_revision)", editor);
        var apply = editor[editor.IndexOf("private void ApplyActSnapshot", StringComparison.Ordinal)..
            editor.IndexOf("private void ClearActSheetState", StringComparison.Ordinal)];
        Assert.True(apply.IndexOf("BuildGrid();", StringComparison.Ordinal) <
            apply.IndexOf("EditorRoot.Visibility = Visibility.Visible;", StringComparison.Ordinal));
    }

    [Fact]
    public void ColdEditorStartsWithLoadingSurfaceNotEmptyGrid()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ActsEditorView.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var xaml = XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "ActsEditorView.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => xaml.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
        Assert.Equal("Collapsed", (string?)Named("EditorRoot").Attribute("Visibility"));
        Assert.Equal("Collapsed", (string?)Named("EmptyPanel").Attribute("Visibility"));
        Assert.NotEqual("Collapsed", (string?)Named("ActLoadingPanel").Attribute("Visibility"));
    }

    [Fact]
    public void QuantityWriteBlocksAnotherEditAndAdvancesAfterReadbackGuard()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ActsEditorView.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var editor = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "ActsEditorView.xaml.cs"));
        Assert.Contains("ActWritePending || e.Row.Item is not KsRowVm row", editor);
        Assert.Contains("if (ActWritePending) { e.Cancel = true; return; }", editor);
        var save = editor[editor.IndexOf("private async Task SaveQtyAsync", StringComparison.Ordinal)..
            editor.IndexOf("private decimal RemainingOf", StringComparison.Ordinal)];
        Assert.Contains("advance = await RefreshChangedRowsAsync(positionId)", save);
        Assert.True(save.LastIndexOf("_inSave = false;", StringComparison.Ordinal) <
            save.IndexOf("if (advance) StepRow(positionId, step);", StringComparison.Ordinal));
        Assert.Contains("KsActSheetProjection.QuantityPatch(previousSheet, snapshot.Sheet, editedPositionId)", editor);
        Assert.Contains("if (!patch.TryGetValue(vm.PositionId, out var p))", editor);
    }

}
