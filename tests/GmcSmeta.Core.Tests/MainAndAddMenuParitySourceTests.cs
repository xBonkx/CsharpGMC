namespace GmcSmeta.Core.Tests;

public sealed class MainAndAddMenuParitySourceTests
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
    public void MainMenuRetainsLocalHelpAndSeparatesPilotCommands()
    {
        var code = Source("MainWindow.Navigation.cs");
        Assert.Contains("new[] { LettersMenuItem, MetalWarehouseMenuItem, MetalRegisterMenuItem }", code);
        Assert.Contains("if (writes && !SqlWritesEnabled) return;", code);
        Assert.Contains("RunLocalNavigationAction(\"Как работать\", OpenWorkspaceHelp)", code);
        Assert.Contains("Резервные копии SQL Server", code);
        Assert.Contains("Сведения о подключении SQL Server", code);
        Assert.Contains("top.Items.Add(settings);", code);
    }

    [Fact]
    public void AddMenuUsesPythonGroupsAndCapturedSelectionForEveryCommand()
    {
        var code = Source("EstimateView.xaml.cs");
        var start = code.IndexOf("private void AddOptions_Click", StringComparison.Ordinal);
        var end = code.IndexOf("private void Help_Click", start, StringComparison.Ordinal);
        var menu = code[start..end];
        Assert.True(menu.IndexOf("Group(\"Строки\")", StringComparison.Ordinal) <
            menu.IndexOf("Group(\"Системы\")", StringComparison.Ordinal));
        Assert.True(menu.IndexOf("Group(\"Системы\")", StringComparison.Ordinal) <
            menu.IndexOf("Group(\"Готовые позиции\")", StringComparison.Ordinal));
        Assert.Contains("SequenceEqual(selected, ReferenceEqualityComparer.Instance)", menu);
        Assert.Contains("if (!item.IsEnabled || !Current())", menu);
        Assert.Contains("CanEditEstimateHeader(_contractId, _objectId)", menu);
        Assert.Contains("glyph: \"Copy\"", menu);
        Assert.Contains("glyph: \"Download\"", menu);
        Assert.Contains("Загрузить строки из файла Excel…", menu);
        Assert.Contains("M4 6h16M4 12h9M4 18h9 M18 14v6M15 17h6", Source("Theme/ToolbarIcons.xaml"));
    }
}
