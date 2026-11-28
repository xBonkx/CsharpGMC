namespace GmcSmeta.Core.Tests;

public sealed class AuxiliaryWindowPolishSourceTests
{
    [Theory]
    [InlineData("Ks3ReportWindow.xaml", "Text=\"Итого за период · с НДС\" TextWrapping=\"Wrap\"")]
    [InlineData("KsGroupReportWindow.xaml", "Binding=\"{Binding SourceName}\" Width=\"*\" MinWidth=\"150\"")]
    [InlineData("KsRequisitesWindow.xaml", "Text=\"Подписывает со стороны заказчика\" TextWrapping=\"Wrap\"")]
    [InlineData("HistoryWindow.xaml", "Binding=\"{Binding Details}\" Width=\"*\" MinWidth=\"180\"")]
    [InlineData("TrashWindow.xaml", "Header=\"Строк\" Binding=\"{Binding RowsText}\" Width=\"60\" ElementStyle=\"{StaticResource CompactCostCenter}\"")]
    [InlineData("LettersWindow.xaml", "Header=\"Тема\" Width=\"2*\" MinWidth=\"150\"")]
    [InlineData("LetterEditorWindow.xaml", "Content=\"Сохранить\" ContentTemplate=\"{StaticResource IconLabelCheck}\"")]
    [InlineData("PassportsWindow.xaml", "Content=\"PDF выбранных\" Padding=\"14,8\" Margin=\"0,0,8,0\" ContentTemplate=\"{StaticResource IconLabelDownload}\"")]
    [InlineData("OrdersWindow.xaml", "x:Name=\"NumberBox\" MinHeight=\"{StaticResource LayoutFieldMinHeight}\"")]
    [InlineData("PtoWindow.xaml", "Content=\"Создать комплект\" ContentTemplate=\"{StaticResource IconLabelPlus}\"")]
    [InlineData("PtoEditorWindow.xaml", "Content=\"Выше\" ContentTemplate=\"{StaticResource IconLabelArrowUp}\"")]
    [InlineData("MetalWarehouseWindow.xaml", "Content=\"Отчёт Excel…\" ContentTemplate=\"{StaticResource IconLabelDownload}\"")]
    public void VisualChangesArePresentAndXamlIsWellFormed(string file, string expected)
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
