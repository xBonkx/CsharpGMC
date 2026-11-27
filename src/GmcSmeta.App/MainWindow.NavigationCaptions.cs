using System.Windows.Automation;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class MainWindow
{
    private void UpdateNavigationCaptions()
    {
        var captions = NavigationModeCaptions.For(CurrentMode);
        NewObjectButton.Content = EmptyNewObjectButton.Content = captions.Create;
        NewObjectButton.ToolTip = EmptyNewObjectButton.ToolTip = captions.CreateHint;
        AutomationProperties.SetName(NewObjectButton, captions.Create);
        AutomationProperties.SetName(EmptyNewObjectButton, captions.Create);
        NavigationSearchPlaceholder.Text = captions.Search + "…";
        AutomationProperties.SetName(ObjectSearch, captions.Search);
        ClearNavigationSearchButton.ToolTip = "Очистить поиск";
        AutomationProperties.SetName(ClearNavigationSearchButton, "Очистить поиск");
        EmptyNavigationHeading.Text = captions.EmptyHeading;
        EmptyNavigationHint.Text = captions.EmptyHint;
    }
}
