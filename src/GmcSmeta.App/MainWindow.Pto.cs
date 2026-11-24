using System.Windows;

namespace GmcSmeta.App;

/// <summary>
/// Explicit local user-mode toggle, as in Python. This is a UX permission gate,
/// not a substitute for restrictive SQL Server login/ACL permissions.
/// </summary>
public partial class MainWindow
{
    public static readonly DependencyProperty PtoModeVisibilityProperty =
        DependencyProperty.Register(nameof(PtoModeVisibility), typeof(Visibility), typeof(MainWindow),
            new PropertyMetadata(Visibility.Collapsed));

    public Visibility PtoModeVisibility
    {
        get => (Visibility)GetValue(PtoModeVisibilityProperty);
        private set => SetValue(PtoModeVisibilityProperty, value);
    }

    private void InitializePtoMenu()
    {
        var enabled=PtoUserSettings.Enabled;
        PtoModeToggle.IsChecked=enabled;
        PtoModeVisibility=PtoMenuItem.Visibility=enabled?Visibility.Visible:Visibility.Collapsed;
    }
    private void PtoMode_Click(object sender,RoutedEventArgs e)
    {
        PtoUserSettings.Enabled=PtoModeToggle.IsChecked;
        PtoModeVisibility=PtoMenuItem.Visibility=PtoModeToggle.IsChecked?
            Visibility.Visible:Visibility.Collapsed;
    }
    private void Pto_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            if(!PtoUserSettings.Enabled || _sqlConnectionString is null || !SqlWritesEnabled)
                throw new InvalidOperationException(
                    "Исполнительная документация доступна только в режиме ПТО и "+
                    "тестовой SQL Server GmcSmetaPilot_*. Настройка режима — локальная; "+
                    "права на сервере контролирует администратор SQL.");
            new PtoWindow(_sqlConnectionString){Owner=this}.ShowDialog();
        }
        catch(Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this,ex.Message,"Исполнительная документация",
                MessageBoxButton.OK,MessageBoxImage.Warning);
        }
    }
}
