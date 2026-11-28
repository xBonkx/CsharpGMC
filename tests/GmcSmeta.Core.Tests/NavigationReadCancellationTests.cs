using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Tests;

public sealed class NavigationReadCancellationTests
{
    [Theory]
    [InlineData("navigation")]
    [InlineData("consistent")]
    [InlineData("metadata")]
    [InlineData("cards")]
    public void CancelledReadNeverTouchesClosedSqlConnection(string route)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var connection = new SqlConnection();
        var token = cancellation.Token;
        var obj = new ObjectRow(1, "Object", "", "", false, "");
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            switch (route)
            {
                case "navigation": Workspace.ReadNavigation(connection, token); break;
                case "consistent": Workspace.ReadConsistentNavigation(connection, token); break;
                case "metadata": Workspace.ObjectCardMetadataMatches(connection, obj, Array.Empty<DocRow>(), token); break;
                case "cards": Workspace.ReadObjectDocuments(connection, obj, Array.Empty<DocRow>(), NavigationMode.Kp, token); break;
            }
        });
    }

    [Theory]
    [InlineData("MainWindow.xaml.cs", "_startupReadCancellation?.Cancel();")]
    [InlineData("MainWindow.xaml.cs", "Workspace.ReadConsistentNavigation(connection, token)")]
    [InlineData("MainWindow.xaml.cs", "await Task.Run(() => ReadStartupData(token), token)")]
    [InlineData("MainWindow.xaml.cs", "if (_startupClosed || token.IsCancellationRequested)")]
    [InlineData("MainWindow.NavigationTree.cs", "navigation_tree_nodes")]
    [InlineData("MainWindow.NavigationTree.cs", "NavigationReusePolicy.SameBranch(oldGroup, group)")]
    [InlineData("MainWindow.NavigationTree.cs", "ReconcileNavigationItems(yearNode.Items, desiredObjects)")]
    [InlineData("MainWindow.NavigationTree.cs", "finally { _syncingNavigation = wasSyncing; }")]
    [InlineData("MainWindow.Navigation.cs", "CancelNavigationRefresh();")]
    [InlineData("MainWindow.Navigation.cs", "Workspace.ReadConsistentNavigation(read, token)")]
    [InlineData("MainWindow.ObjectCards.cs", "Workspace.ObjectCardMetadataMatches(read, o, documentsSnapshot, token)")]
    public void WorkerAndPublicationFencesAreWired(string file, string expected)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        Assert.Contains(expected, File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file)));
    }
}
