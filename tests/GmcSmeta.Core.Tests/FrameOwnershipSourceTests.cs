using System.Globalization;
using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

/// <summary>Source audit of nested insets. Distinct content cards are allowed;
/// an empty transparent frame must not introduce a second page inset.</summary>
public sealed class FrameOwnershipSourceTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly HashSet<string> Boundaries = new(StringComparer.Ordinal)
    { "ControlTemplate", "DataTemplate", "ItemsPanelTemplate", "Popup", "ContextMenu" };

    private static bool IsFrame(XElement e) => e.Name.LocalName is "Border" or "SummaryTableFrame";

    private static bool OwnsInset(XElement e)
    {
        if ((string?)e.Attribute("Padding") is { } padding)
        {
            if (padding.Contains("LayoutNoInset", StringComparison.Ordinal)) return false;
            return padding.Split(',').Any(part => !double.TryParse(part, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value) || value != 0);
        }
        return e.Attribute("Style") is not null;
    }

    [Fact]
    public void EveryNestedXamlInsetHasAReviewedIndependentRole()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var files = Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(app, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj")).OrderBy(file => file, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        var unexpected = new List<string>();
        int ksTiles = 0, sidebarGroups = 0;
        foreach (var file in files)
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var inner in doc.Descendants().Where(e => IsFrame(e) && OwnsInset(e)))
            {
                var outer = inner.Ancestors().TakeWhile(e => !Boundaries.Contains(e.Name.LocalName))
                    .FirstOrDefault(e => IsFrame(e) && OwnsInset(e));
                if (outer is null) continue;
                // White report surface owns the list and the row of independent
                // colored amount cards. Their padding is not a duplicate page inset.
                if (Path.GetFileName(file) == "AllContractActsWindow.xaml" &&
                    (string?)outer.Attribute("Grid.Column") == "1" &&
                    (string?)outer.Attribute("Padding") == "16" &&
                    (string?)outer.Attribute("BorderThickness") == "1" &&
                    inner.Parent?.Name.LocalName == "UniformGrid" &&
                    (string?)inner.Attribute("Style") == "{StaticResource KsCard}")
                { ksTiles++; continue; }
                // Sidebar background owns the outside edge; the segment
                // frame contains precisely the three navigation modes.
                if (Path.GetFileName(file) == "MainWindow.xaml" &&
                    (string?)outer.Attribute(X + "Name") == "Sidebar" &&
                    (string?)outer.Attribute("Padding") == "{StaticResource LayoutSidebarPadding}" &&
                    (string?)inner.Attribute("Padding") == "3" &&
                    inner.Descendants().Where(e => e.Name.LocalName == "RadioButton")
                        .Select(e => (string?)e.Attribute(X + "Name"))
                        .SequenceEqual(new[] { "ContractsMode", "KpMode", "ArchiveMode" }))
                { sidebarGroups++; continue; }

                var line = ((System.Xml.IXmlLineInfo)inner).LineNumber;
                unexpected.Add(Path.GetRelativePath(app, file) + ":" + line + " " +
                    outer.Name.LocalName + "/" + inner.Name.LocalName);
            }
        }
        Assert.True(unexpected.Count == 0, "Review newly nested insets: " + string.Join("; ", unexpected));
        Assert.Equal(4, ksTiles);
        Assert.Equal(1, sidebarGroups);
    }
}
