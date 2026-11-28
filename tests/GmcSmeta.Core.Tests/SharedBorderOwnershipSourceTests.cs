using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class SharedBorderOwnershipSourceTests
{
    [Fact]
    public void DirectNestedBordersDoNotReintroduceDuplicateChrome()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var files = Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(app, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "obj" or "bin")).ToArray();
        Assert.True(files.Length >= 40);
        var violations = new List<string>();
        foreach (var file in files)
        {
            var doc = XDocument.Load(file);
            foreach (var border in doc.Descendants(w + "Border"))
                if (border.Parent?.Name == w + "Border" ||
                    border.Parent?.Name == w + "Border.Child" && border.Parent.Parent?.Name == w + "Border")
                    violations.Add(Path.GetRelativePath(app, file) + ": " + border);
        }
        // Indirect ancestors through panels, templates and content hosts are deliberately
        // not banned: cards and local group frames may own distinct visual roles.
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }
}
