using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class HistoryTrashVisualTests
{
    private static string AppSource(string name)
    {
        DirectoryInfo? root=new(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"src","GmcSmeta.App",name)))
            root=root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName,"src","GmcSmeta.App",name));
    }

    [Fact]
    public void HistorySearchMatchesPythonAffordancesAndKeepsExistingFilter()
    {
        var xaml=AppSource("HistoryWindow.xaml");
        _=XDocument.Parse(xaml);
        Assert.Contains("Width=\"300\"",xaml);
        Assert.Contains("Data=\"{StaticResource GlyphSearch}\" Stroke=\"#64748B\"",xaml);
        Assert.Contains("Width=\"18\" Height=\"18\"",xaml);
        Assert.Contains("Найти: кто, что, название строки…",xaml);
        Assert.Equal(2,xaml.Split("Binding Text, ElementName=SearchBox",StringSplitOptions.None).Length-1);
        Assert.Contains("Click=\"ClearSearch_Click\"",xaml);
        Assert.Contains("TextChanged=\"SearchBox_TextChanged\"",xaml);
        var source=AppSource("HistoryWindow.xaml.cs");
        Assert.Contains("SearchBox.Clear();",source);
        Assert.Contains("SearchBox.Focus();",source);
        Assert.Contains("HistoryPresentation.MatchesWords(SearchBox.Text, row.SearchLine)",source);
        Assert.Contains("SearchBox.SelectAll();",source);
    }

    [Theory]
    [InlineData("HistoryWindow.xaml","history")]
    [InlineData("TrashWindow.xaml","trash")]
    public void ArchiveTablesHaveConsistentRowsAndKeepVirtualization(string file,string key)
    {
        var xaml=AppSource(file);
        _=XDocument.Parse(xaml);
        Assert.Contains("MinRowHeight=\"27\"",xaml);
        Assert.Contains("AlternationCount=\"0\"",xaml);
        Assert.Contains("PythonTableRow",xaml);
        Assert.Contains("EnableRowVirtualization=\"True\"",xaml);
        Assert.Contains("VirtualizingPanel.VirtualizationMode=\"Recycling\"",xaml);
        Assert.Contains("local:TableColumnMenu.Key=\""+key+"\"",xaml);
    }

    [Fact]
    public void TrashKeepsOriginalIconsAndDestructiveConfirmation()
    {
        var xaml=AppSource("TrashWindow.xaml");
        Assert.Contains("ContentTemplate=\"{StaticResource IconLabelUndo}\"",xaml);
        Assert.Contains("Style=\"{StaticResource PythonToolbarButton}\" ContentTemplate=\"{StaticResource IconLabelTrash}\"",xaml);
        Assert.Contains("Binding=\"{Binding DaysLeftText}\" Width=\"80\" ElementStyle=\"{StaticResource CompactCostText}\"",xaml);
        var source=AppSource("TrashWindow.xaml.cs");
        Assert.Contains("MessageBoxButton.YesNo",source);
        Assert.Contains("if (_revision != revision || CurrentId() != id || _commitUncertain) return;",source);
    }
}
