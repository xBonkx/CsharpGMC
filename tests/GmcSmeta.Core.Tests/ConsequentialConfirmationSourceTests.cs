namespace GmcSmeta.Core.Tests;

public sealed class ConsequentialConfirmationSourceTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    [Theory]
    [InlineData("EstimateView.Import.cs", "MessageBoxImage.Question, MessageBoxResult.Cancel);", "if (mode == MessageBoxResult.Cancel) return;")]
    [InlineData("PtoWindow.Packages.cs", "MessageBoxButton.YesNoCancel,MessageBoxImage.Question,MessageBoxResult.Cancel);", "if(kind==MessageBoxResult.Cancel)return;")]
    [InlineData("MetalRegisterWindow.cs", "\"Учёт металла\",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)", "!= MessageBoxResult.Yes) return;")]
    [InlineData("MetalRegisterWindow.cs", "\"Первоначальный импорт\",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)", "!= MessageBoxResult.Yes) return;")]
    [InlineData("MetalWarehouseWindow.xaml.cs", "MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)", "!= MessageBoxResult.Yes) return;")]
    public void ConsequentialMessageBoxesDefaultToNonMutation(string file, string expectedDefault, string cancelGuard)
    {
        var source = Source(file);
        var offset = source.IndexOf(expectedDefault, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"Missing safe default in {file}");
        var suffix = source.Substring(offset + expectedDefault.Length);
        Assert.Contains(cancelGuard, suffix[..Math.Min(suffix.Length, 120)]);
    }

    [Theory]
    [InlineData("ReplaceDialog.xaml", "Accept_Click")]
    [InlineData("ArchiveYearDialog.xaml", "Move_Click")]
    public void ConsequentialDialogDoesNotDefaultToApply(string file, string click)
    {
        var doc = System.Xml.Linq.XDocument.Parse(Source(file));
        var buttons = doc.Descendants().Where(e => e.Name.LocalName == "Button").ToArray();
        var apply = Assert.Single(buttons.Where(e => (string?)e.Attribute("Click") == click));
        Assert.NotEqual("True", (string?)apply.Attribute("IsDefault"));
        Assert.Contains(buttons, e => (string?)e.Attribute("IsCancel") == "True");
    }
}
