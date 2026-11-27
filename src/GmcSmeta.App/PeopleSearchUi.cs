using System.Windows;

namespace GmcSmeta.App;

public partial class PeopleView
{
    private void ClearPeopleSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }
}
