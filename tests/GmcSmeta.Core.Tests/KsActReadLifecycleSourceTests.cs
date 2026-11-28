namespace GmcSmeta.Core.Tests;

// Source guard; visibility and SQL cancellation still need WPF/Pilot acceptance.
public sealed class KsActReadLifecycleSourceTests
{
    [Fact]
    public void NavigationCancelsOnlyOrdinaryReadsAndResumesRequestedAct()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ActsEditorView.Loading.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var loader = File.ReadAllText(Path.Combine(app, "ActsEditorView.Loading.cs"));
        var editor = File.ReadAllText(Path.Combine(app, "ActsEditorView.xaml.cs"));
        var extra = File.ReadAllText(Path.Combine(app, "ActsEditorView.ExtraMenu.cs"));
        Assert.Contains("IsVisibleChanged += (_, _) => UpdateActReadVisibility();", editor);
        Assert.Contains("Loaded += (_, _) => UpdateActReadVisibility();", editor);
        Assert.Contains("if (_actLoadRequired || ActWritePending ||", loader);
        Assert.Contains("_resumeActId = _activeReadActId;", loader);
        Assert.Contains("_resumeActId is not long id", loader);
        Assert.Contains("ReloadAsync(id, reuseList: true, requiredReadback: false)", loader);
        Assert.Contains("bool requiredReadback = true", loader);
        Assert.Contains("requiredReadback ? CancellationToken.None : cancellation.Token", loader);
        Assert.Contains("if (_contractId == 0 || ActWritePending || _actLoadRequired) return;", loader);
        Assert.Contains("token.IsCancellationRequested || generation != _actLoadGeneration", loader);
        Assert.Contains("token.IsCancellationRequested || generation != _actLoadGeneration", editor);
        Assert.Contains("SqlServerDatabase.RevisionAsync(conn, token)", editor);
        Assert.Contains("if (token.IsCancellationRequested) ResumeActReadIfNeeded();", editor);
        Assert.Contains("if (_contractId == 0 || _building || ActWritePending || _actLoadRequired) return;", editor);
        Assert.Contains("ReloadAsync(actId, reuseList: true, requiredReadback: false)", editor);
        Assert.Contains("await ReloadAsync(actId, reuseList: false);", editor);
        Assert.Contains("await ReloadAsync(actId, reuseList: false);", extra);
        Assert.Contains("ReadActSnapshot(connectionString, contractId,", editor);
        Assert.Contains("actId, null, \"\");", editor);
    }
}
