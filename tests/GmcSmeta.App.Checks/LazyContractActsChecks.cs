using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using GmcSmeta.App;

internal static class LazyContractActsChecks
{
    internal static void Run()
    {
        // Check the actual XAML, not a second hand-written copy of the page.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/GmcSmeta.App/EstimateView.xaml")))
            root = root.Parent;
        if (root is null) throw new FileNotFoundException("Cannot find EstimateView.xaml");
        var document = XDocument.Load(Path.Combine(root.FullName, "src/GmcSmeta.App/EstimateView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var panel = document.Descendants(presentation + "Border")
            .Single(e => (string?)e.Attribute(xaml + "Name") == "ContractActsPanel");
        if (panel.Elements().Any() || (string?)panel.Attribute("Visibility") != "Collapsed" ||
            document.Descendants().Any(e => e.Name.LocalName == "ContractActsWorkspace"))
            throw new Exception("Estimate XAML eagerly creates the hidden KS workspace.");

        // Run the same factory used by the production tab, without Bind or SQL.
        var host = new Border { Visibility = Visibility.Collapsed };
        var first = EstimateView.EnsureContractActsWorkspace(host);
        if (!ReferenceEquals(host.Child, first) || first.HasCacheActivity ||
            first.FindName("EmbeddedEditor") is not ActsEditorView)
            throw new Exception("KS workspace did not initialize on first access.");
        host.Visibility = Visibility.Visible;
        host.Visibility = Visibility.Collapsed;
        var second = EstimateView.EnsureContractActsWorkspace(host);
        if (!ReferenceEquals(first, second) || !ReferenceEquals(host.Child, first))
            throw new Exception("Returning to KS must retain the same editor instance.");
        Console.WriteLine("Lazy KS workspace construction and reuse OK (no SQL).");
    }
}
