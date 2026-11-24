using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Safe extra procurement flows: create, edit, delete and supplement.</summary>
public partial class ProcurementPane
{
    private async void ProcurementAddExtra_Click(object sender, RoutedEventArgs e)
    {
        using var operation = TryBeginOperation();
        if (operation is null) return;

        var current = BuyGrid.SelectedItem as ProcurementPreviewRow;
        var dialog = new ProcurementExtraDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        SetStatus("Создание позиции вне КП…");
        try
        {
            var revision = operation.BeginWrite();
            var result = await Task.Run(() => SqlServerProcurement.AddExtra(
                _connectionString, _contractId, current?.SystemId,
                dialog.PositionName, dialog.Unit, dialog.Quantity, dialog.PurchaseCost, revision));
            if (!result.Saved)
            {
                SetStatus("Не добавлено: " + result.Error);
                return;
            }

            await TryReloadAsync(preserveStatus: true);
            var created = _allRows.FirstOrDefault(x => x.PositionId == result.Id);
            if (created is not null)
            {
                BuyGrid.SelectedItem = created;
                BuyGrid.ScrollIntoView(created);
            }
            SetStatus("Докупленная позиция создана; она не входит в КП и КС.");
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Создание не подтверждено: " + ex.Message +
                ". При потере связи проверьте таблицу до повторения.");
            await TryReloadAsync(preserveStatus: true);
        }

    }

    private async void ProcurementEditExtra_Click(object sender, RoutedEventArgs e)
    {
        using var operation = TryBeginOperation();
        if (operation is null) return;

        var row = BuyGrid.SelectedItem as ProcurementPreviewRow;
        if (row is null || !row.OutOfKp)
        {
            SetStatus("Выберите одну строку «вне КП».");
            return;
        }
        var dialog = new ProcurementExtraDialog(row) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var revision = operation.BeginWrite();
            var result = await Task.Run(() => SqlServerProcurement.EditExtra(
                _connectionString, _contractId, row.PositionId, dialog.PositionName,
                dialog.Unit, dialog.Quantity, revision));
            if (!result.Saved) { SetStatus("Изменение не сохранено: " + result.Error); return; }

            await TryReloadAsync(preserveStatus: true);
            var updated = _allRows.FirstOrDefault(x => x.PositionId == row.PositionId);
            if (updated is not null)
            {
                BuyGrid.SelectedItem = updated;
                BuyGrid.ScrollIntoView(updated);
            }
            SetStatus("Дополнительная закупка изменена.");
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Не удалось подтвердить редактирование: " + ex.Message);
            await TryReloadAsync(preserveStatus: true);
        }

    }

    private void ProcurementDeleteExtra_Click(object sender, RoutedEventArgs e) =>
        ProcurementDeleteExtras_Click(sender, e);

    private async void ProcurementExtraDop_Click(object sender, RoutedEventArgs e)
    {
        using var operation = TryBeginOperation();
        if (operation is null) return;

        var count = _allRows.Count(x => x.OutOfKp);
        if (count == 0) { SetStatus("Дополнительных закупок вне КП нет."); return; }
        if (MessageBox.Show(Window.GetWindow(this),
                $"Создать допсоглашение из докупленного ({count} строк)?\n\n" +
                "Движения и поставки перенесутся с исходными ID. " +
                "Цены подставятся из справочника или закупки; проверьте их в «Смете».",
                "Допсоглашение из докупленного",
                MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        SetStatus("Создание допсоглашения из докупленного…");
        try
        {
            var revision = operation.BeginWrite();
            var result = await Task.Run(() =>
                SqlServerProcurement.CreateDopFromExtras(_connectionString, _contractId, revision));
            if (!result.Saved) { SetStatus("Допсоглашение не создано: " + result.Error); return; }

            await TryReloadAsync(preserveStatus: true);
            SetStatus($"ДС № {result.Id} создано из докупленного. " +
                "Обновите дерево документов и проверьте цены в «Смете», укажите номер в свойствах.");
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Создание ДС не подтверждено: " + ex.Message +
                ". Проверьте дерево документов перед повтором, чтобы избежать дублей.");
            await TryReloadAsync(preserveStatus: true);
        }

    }
}
