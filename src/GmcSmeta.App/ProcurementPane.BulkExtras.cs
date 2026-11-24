using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

public partial class ProcurementPane
{
    private async void ProcurementDeleteExtras_Click(object sender, RoutedEventArgs e)
    {
        using var operation = TryBeginOperation();
        if (operation is null) return;

        var ids = BuyGrid.SelectedItems.OfType<ProcurementPreviewRow>()
            .Where(x => x.OutOfKp).Select(x => x.PositionId).Distinct().ToArray();
        if (ids.Length == 0)
        {
            SetStatus("Выделите одну или несколько дополнительных позиций вне КП.");
            return;
        }

        try
        {
            SetStatus($"Проверка связей для {ids.Length} дополнительных позиций…");
            var preview = await Task.Run(() => SqlServerProcurement.PreviewDeleteExtras(
                _connectionString, _contractId, ids));
            if (!await CheckPreviewRevisionAsync(operation, preview.Revision)) return;
            var blocked = preview.Decisions.Where(x => x.State == "BLOCKED").ToArray();
            if (blocked.Length > 0)
            {
                var details = string.Join("\n", blocked.Take(12).Select(x =>
                    $"#{x.PositionId} {x.Name}: {x.Reason}"));
                MessageBox.Show(Window.GetWindow(this),
                    $"Удаление отменено для всей группы ({ids.Length} позиций).\n" +
                    $"Заблокировано: {blocked.Length}\n\n{details}" +
                    (blocked.Length > 12 ? "\n… остальные препятствия скрыты." : "") +
                    "\n\nДанные, движения, выдачи и закупочные цены не изменены.",
                    "Удаление докупленного: предпросмотр",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("Удаление заблокировано: " + blocked.Length + " позиций.");
                return;
            }
            if (MessageBox.Show(Window.GetWindow(this),
                    $"Удалить {preview.Allowed} дополнительных закупок одним действием?\n\n" +
                    "Проверены связи с КС, движениями, ведомостями, заявками и ценами. " +
                    "При конфликте все удаления откатываются.",
                    "Удаление докупленного: подтверждение",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                SetStatus("Удаление отменено без изменений.");
                return;
            }
            var revision = operation.BeginWrite();
            var saved = await Task.Run(() => SqlServerProcurement.DeleteExtras(
                _connectionString, _contractId, ids, revision));
            if (!saved.Saved)
            {
                SetStatus("Пакетное удаление не выполнено: " + saved.Error);
                return;
            }

            await TryReloadAsync(preserveStatus: true);
            SetStatus($"Удалено: {ids.Length} дополнительных закупок.");
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Результат удаления не подтверждён: " + ex.Message +
                ". После сбоя COMMIT требуется повторное чтение SQL, не повторяйте удаление вслепую.");
            await TryReloadAsync(preserveStatus: true);
        }

    }
}
