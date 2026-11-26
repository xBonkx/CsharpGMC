using System.Windows;

namespace GmcSmeta.App;

public partial class PeopleWindow
{
    private void ClearPeopleSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }
}
