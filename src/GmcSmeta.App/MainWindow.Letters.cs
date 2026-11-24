using System.Windows;

namespace GmcSmeta.App;

public partial class MainWindow
{
    private void Letters_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sqlConnectionString is null || !SqlWritesEnabled)
                throw new InvalidOperationException(
                    "Письма в C# доступны только на тестовой базе GmcSmetaPilot_* до приёмки.");
            new LettersWindow(_sqlConnectionString) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Письма", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
