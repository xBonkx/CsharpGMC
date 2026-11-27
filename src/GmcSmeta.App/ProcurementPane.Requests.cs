using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Supplier request and workshop commands, isolated from shared WPF code.</summary>
public partial class ProcurementPane
{
    private async void ProcurementSupplierRequest_Click(object sender, RoutedEventArgs e)
    {
        using var operation = TryBeginOperation();
        if (operation is null) return;
        SetStatus("Подготовка заявки поставщику…");
        try
        {
            var snapshot = await Task.Run(() => ProcurementRequestPlanner.ReadReport(
                _connectionString, _contractId, ProcurementRequestPlanner.ReportKind.Supplier, operation.Revision));
            var selectedSupplier = (BuyGrid.SelectedItem as ProcurementPreviewRow)?.Supplier ?? "";
            var dialog = new ProcurementRequestDialog(
                snapshot.Document, snapshot.Rows, snapshot.Children,
                snapshot.Number, selectedSupplier)
            { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || !dialog.FileSaved)
            {
                SetStatus("Формирование заявки отменено.");
                return;
            }
            if (dialog.MarkedIds.Count > 0)
            {
                var revision = operation.BeginWrite();
                var saved = await Task.Run(() => SqlServerProcurement.SetProc(
                    _connectionString, _contractId, dialog.MarkedIds, null, "ordered",
                    $"Заявка №{(string.IsNullOrWhiteSpace(dialog.RequestNumber) ? "б/н" : dialog.RequestNumber)}",
                    null, null, null, null, null, null, revision));
                if (!saved.Saved)
                {
                    SetStatus("Excel сохранён, но строки НЕ отмечены заказанными: " + saved.Error +
                        ". Обновите закупки и проверьте данные перед повтором.");
                    await TryReloadAsync(preserveStatus: true);
                    return;
                }

                await RefreshCommittedBuyRowsAsync(dialog.MarkedIds, saved.Revision);
                SetStatus($"Заявка сохранена; отмечено «Заказано»: {dialog.MarkedIds.Count} позиций.");
            }
            else
                SetStatus("Заявка Excel сохранена без изменения статусов закупок.");
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Ошибка заявки: " + ex.Message +
                ". При ошибке после сохранения файла проверьте состояния позиций.");
            await TryReloadAsync(preserveStatus: true);
        }

    }

    private async void ProcurementToShop_Click(object sender, RoutedEventArgs e)
    {
        using var operation = TryBeginOperation();
        if (operation is null) return;

        var ids = SelectedBuyingIds();
        if (ids.Count == 0) { SetStatus("Выделите позиции для цеха."); return; }
        var prompt = new PromptDialog("Заявка в цех",
            $"Отдать в цех {ids.Count} позиций. Номер заявки (можно пусто):", "")
        { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true) return;

        try
        {
            var rev = operation.BeginWrite();
            var reference = prompt.Value.Trim();
            var result = await Task.Run(() => SqlServerProcurement.SetProc(
                _connectionString, _contractId, ids, SqlServerProcurement.ShopSupplier,
                "shop_queued", reference.Length == 0 ? null : $"цех {reference}",
                null, null, null, null, null, null, rev));
            if (!result.Saved) { SetStatus("Заявка в цех не сохранена: " + result.Error); return; }

            await RefreshCommittedBuyRowsAsync(ids, result.Revision);
            SetStatus($"В цех направлено: {ids.Count} позиций.");
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Неизвестный результат заявки в цех: " + ex.Message +
                ". Проверьте данные SQL Server перед повтором.");
            await TryReloadAsync(preserveStatus: true);
        }

    }

    private async void ProcurementOrder_Click(object sender, RoutedEventArgs e)
    {
        using var operation = TryBeginOperation();
        if (operation is null) return;

        var ids = SelectedBuyingIds();
        if (ids.Count == 0) { SetStatus("Выделите позиции для заказа или оплаты."); return; }
        var selected = _allRows.Where(x => ids.Contains(x.PositionId)).ToArray();
        var names = _allRows.Select(x => x.Supplier).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!).ToArray();
        var dialog = new ProcurementOrderDialog(selected, names) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        SetStatus("Сохранение заказа / оплаты…");
        try
        {
            var revision = operation.BeginWrite();
            var stage = dialog.PaidDate is not null && dialog.Stage is null or "" or "ordered" or "invoice"
                ? "paid" : dialog.Stage;
            var result = await Task.Run(() => SqlServerProcurement.SetProc(
                _connectionString, _contractId, ids, dialog.Supplier, stage,
                dialog.OrderRef, dialog.InvoiceRef, null, null, null, null, null,
                revision, paidDate: dialog.PaidDate,
                clearPaidDate: dialog.ClearPaidDate, explicitSource: dialog.Source));
            if (!result.Saved)
            {
                SetStatus("Заказ / оплата не сохранены: " + result.Error);
                return;
            }

            await RefreshCommittedBuyRowsAsync(ids, result.Revision);
            SetStatus($"Заказ / оплата: обновлено {ids.Count} позиций.");
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Сохранение не подтверждено: " + ex.Message +
                ". При потере связи проверьте значения перед повтором.");
            await TryReloadAsync(preserveStatus: true);
        }

    }

}
