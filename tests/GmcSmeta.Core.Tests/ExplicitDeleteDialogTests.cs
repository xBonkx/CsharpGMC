using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class ExplicitDeleteDialogTests
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
    [InlineData("ConfirmDeleteDialog.xaml")]
    [InlineData("DeletePositionsDialog.xaml")]
    public void DeleteIsNotDefault(string file)
    {
        var buttons = XDocument.Parse(Source(file)).Descendants()
            .Where(e => e.Name.LocalName == "Button").ToArray();
        var delete = Assert.Single(buttons.Where(e => (string?)e.Attribute("Click") == "Delete_Click"));
        Assert.Equal("False", (string?)delete.Attribute("IsDefault"));
        Assert.Contains(buttons, e => (string?)e.Attribute("IsCancel") == "True");
    }

    [Fact]
    public void EnterInChallengeDoesNotDelete()
    {
        var source = Source("ConfirmDeleteDialog.xaml.cs");
        var start = source.IndexOf("private void AnswerBox_PreviewKeyDown", StringComparison.Ordinal);
        var end = source.IndexOf("private void Delete_Click", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        Assert.Contains("if (e.Key == Key.Enter)", source[start..end]);
        Assert.Contains("e.Handled = true;", source[start..end]);
        Assert.DoesNotContain("DialogResult", source[start..end]);
        Assert.Contains("if (AnswerConfirmed && DeleteButton.IsEnabled) DialogResult = true;", source);
    }

    [Fact]
    public void BatchDeleteDefaultsToCancel()
    {
        var xaml = Source("DeletePositionsDialog.xaml");
        var cancel = Assert.Single(XDocument.Parse(xaml).Descendants()
            .Where(e => e.Name.LocalName == "Button" && (string?)e.Attribute("IsCancel") == "True"));
        Assert.Equal("True", (string?)cancel.Attribute("IsDefault"));
        Assert.Contains("CancelButton.Focus()", Source("DeletePositionsDialog.xaml.cs"));
        Assert.Contains("включая не показанные в кратком списке", xaml);
    }
}
