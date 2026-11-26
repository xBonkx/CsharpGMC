using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class CompactDialogLayoutSourceTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    private static XElement Named(XDocument doc, string name) =>
        Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Name") == name));

    [Theory]
    [InlineData("ContractDialog.xaml", "SourceCombo", "OkButton")]
    [InlineData("ObjectDialog.xaml", "NoteInput", "SaveButton")]
    [InlineData("ConfirmDeleteDialog.xaml", "AnswerBox", "DeleteButton")]
    public void FormScrollsIndependentlyOfFooter(string file, string field, string action)
    {
        var doc = Load(file);
        var scroll = Assert.Single(Named(doc, field).Ancestors(W + "ScrollViewer"));
        Assert.Equal("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("Disabled", (string?)scroll.Attribute("HorizontalScrollBarVisibility"));
        Assert.Empty(Named(doc, action).Ancestors(W + "ScrollViewer"));
    }

    [Theory]
    [InlineData("ContractDialog.xaml", "VatHint")]
    [InlineData("ObjectDialog.xaml", "ValidationMessage")]
    public void EmptyHintDoesNotReserveSpace(string file, string name)
    {
        var text = Named(Load(file), name);
        var empty = Assert.Single(text.Descendants(W + "Trigger").Where(e =>
            (string?)e.Attribute("Property") == "Text" && (string?)e.Attribute("Value") == ""));
        Assert.Contains(empty.Elements(W + "Setter"), s =>
            (string?)s.Attribute("Property") == "Visibility" && (string?)s.Attribute("Value") == "Collapsed");
    }

    [Fact]
    public void CopySourceStretchesInsteadOfForcingFixedWidth()
    {
        var doc = Load("ContractDialog.xaml");
        var source = Named(doc, "SourceCombo");
        Assert.Equal("0", (string?)source.Attribute("MinWidth"));
        Assert.Equal("1", (string?)source.Attribute("Grid.Column"));
        Assert.Equal("*", (string?)source.Parent!.Descendants(W + "ColumnDefinition").Last().Attribute("Width"));
        Assert.Equal("CanResize", (string?)doc.Root!.Attribute("ResizeMode"));
        Assert.NotNull(doc.Root.Attribute("MaxHeight"));
        Assert.Equal("Collapsed", (string?)Named(doc, "ImportExcelButton").Attribute("Visibility"));
    }

    [Fact]
    public void FormMetricsFollowPython()
    {
        var doc = Load("Theme/LayoutMetrics.xaml");
        Assert.Equal("22,18,22,18", doc.Descendants().Single(e =>
            (string?)e.Attribute(X + "Key") == "LayoutFormDialogMargin").Value);
        Assert.Equal("0,0,0,9", doc.Descendants().Single(e =>
            (string?)e.Attribute(X + "Key") == "LayoutFormFieldMargin").Value);
    }
}
