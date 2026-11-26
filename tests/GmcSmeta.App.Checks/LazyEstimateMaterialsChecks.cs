using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Xml.Linq;
using GmcSmeta.App;

internal static class LazyEstimateMaterialsChecks
{
    internal static void Run()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/GmcSmeta.App/EstimateView.xaml")))
            root = root.Parent;
        if (root is null) throw new FileNotFoundException("Cannot find EstimateView.xaml");
        var document = XDocument.Load(Path.Combine(root.FullName, "src/GmcSmeta.App/EstimateView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var host = document.Descendants(presentation + "Border")
            .Single(e => (string?)e.Attribute(xaml + "Name") == "MaterialsPanel");
        if (host.Elements().Any() || (string?)host.Attribute("Visibility") != "Collapsed")
            throw new Exception("Estimate eagerly creates materials panel.");

        var view = new EstimateMaterialsPanel();
        if (view.FindName("MaterialsGrid") is not DataGrid grid ||
            grid.SelectionMode != DataGridSelectionMode.Extended ||
            grid.SelectionUnit != DataGridSelectionUnit.FullRow || grid.IsReadOnly ||
            !grid.EnableRowVirtualization || !grid.EnableColumnVirtualization)
            throw new Exception("Lazy materials grid changed selection, editing or virtualization.");
        if (view.FindName("MaterialsSearch") is not TextBox ||
            view.FindName("MaterialsSystemFilter") is not ComboBox ||
            view.FindName("MaterialsNoCostToggle") is not ToggleButton ||
            view.FindName("MaterialsNoPriceToggle") is not ToggleButton ||
            view.FindName("MatTileTrans") is not Button transport)
            throw new Exception("Missing materials filters or transport control.");
        transport.ApplyTemplate();
        if (transport.Template.FindName("MatTileTransCaption", transport) is not TextBlock ||
            transport.Template.FindName("MatTileTransValue", transport) is not TextBlock)
            throw new Exception("Missing transport tile template parts.");
        Console.WriteLine("Lazy estimate materials visual panel OK (no SQL).");
    }
}
