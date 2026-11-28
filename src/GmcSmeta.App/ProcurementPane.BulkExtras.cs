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

        var selected = BuyGrid.SelectedItems.OfType<ProcurementPreviewRow>().ToArray();
        if (!CurrentBuyingSelection(selected) || selected.Any(row => !row.OutOfKp || row.IsSub))
        {
            SetStatus("Выделите одну или несколько дополнительных позиций вне КП.");
            return;
        }
        var ids = selected.Select(row => row.PositionId).Distinct().ToArray();

        try
        {
            SetStatus($"Проверка связей для {ids.Length} дополнительных позиций…");
            var preview = await Task.Run(() => SqlServerProcurement.PreviewDeleteExtras(
                _connectionString, _contractId, ids));
            if (!await CheckPreviewRevisionAsync(operation, preview.Revision)) return;
            if (!preview.Decisions.Select(x => x.PositionId).SequenceEqual(ids))
                throw new InvalidOperationException("Предпросмотр не соответствует выделению.");
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
                    "При конфликте все удаления откатываются. Восстановление этих строк не предусмотрено.",
                    "Удаление докупленного: подтверждение",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                SetStatus("Удаление отменено без изменений.");
                return;
            }
            if (!CurrentBuyingSelection(selected))
                throw new InvalidOperationException("Выделение изменилось после предпросмотра.");
            var revision = operation.BeginWrite();
            var saved = await Task.Run(() => SqlServerProcurement.DeleteExtras(
                _connectionString, _contractId, ids, revision));
            if (!saved.Saved)
            {
                SetStatus("Пакетное удаление не выполнено: " + saved.Error);
                await TryReloadAsync(preserveStatus: true);
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
