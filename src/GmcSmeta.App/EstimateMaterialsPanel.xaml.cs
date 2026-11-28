using System.Windows;
using System.Windows.Controls;

namespace GmcSmeta.App;

/// <summary>Visual contents of the materials tab, created only on first access.</summary>
public partial class EstimateMaterialsPanel : Grid
{
    internal EstimateView? Owner { get; set; }

    public EstimateMaterialsPanel() => InitializeComponent();

    private void MaterialsToggleChildren_Click(object sender, RoutedEventArgs e) => Owner?.MaterialsToggleChildren_Click(sender, e);
}
