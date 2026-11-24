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
        if (!_allRows.Contains(clicked)) { SetStatus("Выбор изменился. Повторите действие."); return; }
        var chosen = BuyGrid.SelectedItems.OfType<ProcurementPreviewRow>()
            .Where(x => !x.Work && _allRows.Any(y => y.PositionId == x.PositionId))
            .DistinctBy(x => x.PositionId).ToList();
        if (!chosen.Any(x => x.PositionId == clicked.PositionId))
            chosen = new List<ProcurementPreviewRow> { clicked };
        var dialog = new ProcurementMovementDialog(kind, chosen, defaultDocument: defaultDocument)
        { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        SetStatus($"Проведение движения: {dialog.Items.Count} позиций…");
        try
        {
            var revision = operation.BeginWrite();
            string committedRevision;
            if (kind == "out")
            {
                var (_, newRevision) = await Task.Run(() =>
                    SqlServerProcurement.IssueViaListBatch(
                        _connectionString, _contractId, dialog.Items,
                        dialog.MovementDate!.Value,
                        string.IsNullOrWhiteSpace(dialog.Note) ? dialog.Document : dialog.Note,
                        dialog.DirectToSite, revision));
                committedRevision = newRevision;
            }
            else
            {
                var result = await Task.Run(() => SqlServerProcurement.AddMovements(
                    _connectionString, _contractId, dialog.Items, kind,
                    dialog.MovementDate, dialog.Document, dialog.Note, revision));
                if (!result.Saved)
                {
                    SetStatus("Движение не проведено: " + result.Error);
                    await TryReloadAsync(preserveStatus: true);
                    return;
                }
                committedRevision = result.Revision;
            }
            await RefreshCommittedBuyRowsAsync(dialog.Items.Select(item => item.PositionId).ToArray(), committedRevision);
            SetStatus($"{SqlServerProcurement.MovementKindLabel(kind)}: {dialog.Items.Count} позиций. " +
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
            var revision = operation.BeginWrite();
            var result = await Task.Run(() => SqlServerProcurement.EditMovement(
                _connectionString, _contractId, position.PositionId, movement.Id,
                dialog.Items.Single().Quantity, dialog.MovementDate,
                dialog.Document, dialog.Note, revision));
            if (!result.Saved)
            {
                SetStatus("Движение не изменено: " + result.Error);
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
