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
        if (BuyGrid.SelectedItems.Count > 1 || (current is not null && !CurrentBuyingRow(current)))
        { SetStatus("Выберите один раздел через его позицию или снимите выделение."); return; }
        var dialog = new ProcurementExtraDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        SetStatus("Создание позиции вне КП…");
        try
        {
            var (name, unit, quantity, cost) = (dialog.PositionName, dialog.Unit, dialog.Quantity, dialog.PurchaseCost);
            var system = current?.SystemId;
            var revision = operation.BeginWrite();
            var result = await Task.Run(() => SqlServerProcurement.AddExtra(
                _connectionString, _contractId, system, name, unit, quantity, cost, revision));
            if (!result.Saved)
            {
                SetStatus("Не добавлено: " + result.Error);
                await TryReloadAsync(preserveStatus: true);
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
        if (row is null || !row.OutOfKp || row.IsSub || BuyGrid.SelectedItems.Count != 1 || !CurrentBuyingRow(row))
        {
            SetStatus("Выберите одну строку «вне КП».");
            return;
        }
        var dialog = new ProcurementExtraDialog(row) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var (name, unit, quantity) = (dialog.PositionName, dialog.Unit, dialog.Quantity);
            var revision = operation.BeginWrite();
            var result = await Task.Run(() => SqlServerProcurement.EditExtra(
                _connectionString, _contractId, row.PositionId, name, unit, quantity, revision));
            if (!result.Saved) { SetStatus("Изменение не сохранено: " + result.Error); await TryReloadAsync(preserveStatus: true); return; }

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

        var roots = _allRows.Where(x => x.OutOfKp && !x.IsSub).Select(x => x.PositionId).OrderBy(id => id).ToArray();
        var count = roots.Length;
        if (count == 0) { SetStatus("Дополнительных закупок вне КП нет."); return; }
        if (MessageBox.Show(Window.GetWindow(this),
                $"Создать допсоглашение из докупленного ({count} строк)?\n\n" +
                "Движения и поставки перенесутся с исходными ID. " +
                "Цены подставятся из справочника или закупки; проверьте их в «Смете».",
                "Допсоглашение из докупленного",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;

        SetStatus("Создание допсоглашения из докупленного…");
        try
        {
            var revision = operation.BeginWrite();
            var result = await Task.Run(() =>
                SqlServerProcurement.CreateDopFromExtras(_connectionString, _contractId, revision, roots));
            if (!result.Saved) { SetStatus("Допсоглашение не создано: " + result.Error); await TryReloadAsync(preserveStatus: true); return; }

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
