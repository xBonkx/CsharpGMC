using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Procurement-pane actions kept apart from the shared WPF data-grid editor.</summary>
public partial class ProcurementPane
{
    private async Task OpenMovementDialogAsync(ProcurementPreviewRow clicked, string kind, string defaultDocument = "")
    {
        using var operation = TryBeginOperation();
        if (operation is null) return;
        var valid = _allRows.Where(row => !row.Work && row.PositionId > 0)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        if (!valid.Contains(clicked) || !CurrentBuyingRow(clicked))
        { SetStatus("Выбор изменился. Повторите действие."); return; }
        var chosen = BuyGrid.SelectedItems.OfType<ProcurementPreviewRow>().ToArray();
        if (!CurrentBuyingSelection(chosen) || !chosen.Contains(clicked))
        { SetStatus("Выделите только актуальные строки закупок."); return; }
        var allowed = chosen.Select(row => row.PositionId).ToHashSet();
        var dialog = new ProcurementMovementDialog(kind, chosen, defaultDocument: defaultDocument)
        { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        var items = dialog.Items.ToArray();
        if (items.Length == 0 || items.Any(item => !allowed.Contains(item.PositionId)) ||
            !CurrentBuyingSelection(chosen)) { SetStatus("Выбор изменился. Движение отменено."); return; }
        var date = dialog.MovementDate;
        var document = dialog.Document;
        var note = dialog.Note;
        var direct = dialog.DirectToSite;
        SetStatus($"Проведение движения: {items.Length} позиций…");
        try
        {
            var revision = operation.BeginWrite();
            string committedRevision;
            if (kind == "out")
            {
                var (_, newRevision) = await Task.Run(() =>
                    SqlServerProcurement.IssueViaListBatch(
                        _connectionString, _contractId, items,
                        date!.Value, string.IsNullOrWhiteSpace(note) ? document : note,
                        direct, revision));
                committedRevision = newRevision;
            }
            else
            {
                var result = await Task.Run(() => SqlServerProcurement.AddMovements(
                    _connectionString, _contractId, items, kind,
                    date, document, note, revision));
                if (!result.Saved)
                {
                    SetStatus("Движение не проведено: " + result.Error);
                    await TryReloadAsync(preserveStatus: true);
                    return;
                }
                committedRevision = result.Revision;
            }
            await RefreshCommittedBuyRowsAsync(items.Select(item => item.PositionId).ToArray(), committedRevision);
            SetStatus($"{SqlServerProcurement.MovementKindLabel(kind)}: {items.Length} позиций. " +
                "Остатки и статусы перечитаны из SQL Server.");
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Движение не подтверждено: " + ex.Message +
                ". При обрыве связи на COMMIT результат неопределён: проверьте историю перед повтором.");
            await TryReloadAsync(preserveStatus: true);
        }
    }

    private async void MovementEdit_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartOperation || _historyRevision != _revision ||
            SelectedHistoryRow() is not ProcurementPreviewRow position ||
            MovementHistoryGrid.SelectedItem is not SqlServerProcurement.MovementHistoryRow movement)
            return;
        var error = SqlServerProcurement.MovementDeletionError(movement, position.PositionId);
        if (error is not null) { SetStatus(error); return; }
        using var operation = TryBeginOperation();
        if (operation is null) return;
        var dialog = new ProcurementMovementDialog(movement.Kind, new[] { position }, movement)
        { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        MovementEditButton.IsEnabled = false;
        MovementDeleteButton.IsEnabled = false;
        try
        {
            if (!CurrentHistoryTarget(position, movement, operation.Revision))
                throw new InvalidOperationException("Запись истории изменилась.");
            var item = dialog.Items.Single();
            if (item.PositionId != position.PositionId) throw new InvalidOperationException("Неверная позиция.");
            var date = dialog.MovementDate;
            var document = dialog.Document;
            var note = dialog.Note;
            var revision = operation.BeginWrite();
            var result = await Task.Run(() => SqlServerProcurement.EditMovement(
                _connectionString, _contractId, position.PositionId, movement.Id,
                item.Quantity, date, document, note, revision));
            if (!result.Saved)
            {
                SetStatus("Движение не изменено: " + result.Error);
                await TryReloadAsync(preserveStatus: true);
                return;
            }

            await RefreshCommittedBuyRowsAsync(new[] { position.PositionId }, result.Revision);
            SetStatus("Движение изменено, остатки перечитаны.");
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Редактирование не подтверждено: " + ex.Message +
                ". Если связь оборвалась при COMMIT, проверьте историю перед повтором.");
            await TryReloadAsync(preserveStatus: true);
        }
        finally { UpdateHistoryButtons(); }
    }
}
