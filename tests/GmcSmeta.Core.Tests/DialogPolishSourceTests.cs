namespace GmcSmeta.Core.Tests;

public sealed class DialogPolishSourceTests
{
    [Theory]
    [InlineData("ArchiveYearDialog.xaml", "IsCancel=\"True\"")]
    [InlineData("ConfirmDeleteDialog.xaml", "Style=\"{StaticResource DangerButton}\"")]
    [InlineData("DeletePositionsDialog.xaml", "AutomationProperties.Name=\"Закрыть\"")]
    [InlineData("ObjectDialog.xaml", "MinWidth=\"122\" MinHeight=\"34\"")]
    [InlineData("RateTransferDialog.xaml", "Data=\"{StaticResource GlyphSearch}\"")]
    [InlineData("ReplaceDialog.xaml", "Margin=\"0,0,8,6\"")]
    [InlineData("StageLinksDialog.xaml", "CanUserResizeRows=\"False\"")]
    [InlineData("StageMoneyDialog.xaml", "FontSize=\"{StaticResource UiMoneyInputSize}\"")]
    [InlineData("StageSystemsMoneyDialog.xaml", "MinWidth=\"100\"")]
    [InlineData("CompareDialog.xaml", "MinWidth=\"0\" SelectionChanged=\"Combo_Changed\"")]
    public void DialogRetainsVisualPolish(string file, string expected)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "src", "GmcSmeta.App", file);
        _ = System.Xml.Linq.XDocument.Load(path);
        Assert.Contains(expected, File.ReadAllText(path));
    }
}
