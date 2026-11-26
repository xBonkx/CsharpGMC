using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class CatalogSnapshotLifecycleTests
{
    [Fact]
    public void CancelledPageDoesNotConnect()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SqlServerWorkspaceNavigation.CatalogSnapshot(
            "Server=invalid;Database=GmcSmetaPilot_Catalog;Integrated Security=true",
            "клапан", 0, 201, token: cancellation.Token));
    }

    [Fact]
    public void PickerAndManagerShareRevisionCheckedPageReader()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "CatalogPickerWindow.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var picker = File.ReadAllText(Path.Combine(app, "CatalogPickerWindow.cs"));
        var manager = File.ReadAllText(Path.Combine(app, "WorkspaceQueryWindows.cs"));
        var sql = File.ReadAllText(Path.Combine(root.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerWorkspaceNavigation.cs"));
        Assert.Contains("CatalogSnapshot(", picker);
        Assert.Contains("PageSize + 1, _expectedRevision", picker);
        Assert.Contains("includeUsageCount: false, token: token", picker);
        Assert.Contains("_pageCancellation?.Cancel();", picker);
        Assert.Contains("token.IsCancellationRequested || generation != _request", picker);
        Assert.Contains("CatalogSnapshot(", manager);
        Assert.Contains("before != expectedRevision", sql);
        Assert.Contains("ReadCatalogPage(conn, query, skip, take, includeUsageCount, token)", sql);
        Assert.Contains("before != Revision()", sql);
        Assert.Contains("bool includeUsageCount = true", sql);
        Assert.Contains("includeUsageCount ? \"(SELECT COUNT_BIG(*)", sql);
    }
}
