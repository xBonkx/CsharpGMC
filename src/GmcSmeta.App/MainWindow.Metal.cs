using System.Windows;

namespace GmcSmeta.App;

public partial class MainWindow
{
    private void MetalWarehouse_Click(object sender, RoutedEventArgs e) =>
        OpenMetalWindow(register: false);

    private void MetalRegister_Click(object sender, RoutedEventArgs e) =>
        OpenMetalWindow(register: true);

    private void OpenMetalWindow(bool register)
    {
        try
        {
            if (_sqlConnectionString is null || !SqlWritesEnabled)
                throw new InvalidOperationException(
                    "Учёт металла в C# разрешён только на тестовой базе GmcSmetaPilot_* до приёмки.");
            if (register)
                new MetalRegisterWindow(_sqlConnectionString) { Owner = this }.ShowDialog();
            else
                new MetalWarehouseWindow(_sqlConnectionString) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, register ? "Учёт металла" : "Склад металла",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
