namespace GmcSmeta.Core.Tests;

public sealed class ObjectMoreMenuSourceTests
{
    [Fact]
    public void ObjectMenuChecksContextAndShowsYearOnlyForArchive()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "MainWindow.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var code = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "MainWindow.xaml.cs"));
        var start = code.IndexOf("private void ObjectMore_Click(", StringComparison.Ordinal);
        var end = code.IndexOf("private void FinishSqlServerObject(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var menu = code[start..end];
        Assert.Contains("_selectedObject?.Id != obj.Id", menu);
        Assert.Contains("connection != _sqlConnectionString || mode != CurrentMode", menu);
        Assert.Contains("fresh.Archived != obj.Archived || fresh.ArchivedOn != obj.ArchivedOn", menu);
        Assert.Contains("if (obj.Archived)", menu);
        Assert.Contains("year.Click += (_, _) => Run(ChangeSqlServerObjectArchiveYear);", menu);
        Assert.Contains("delete.Click += (_, _) => Run(DeleteSqlServerObjectFlow);", menu);
        Assert.Contains("action(fresh);", menu);
    }
}
