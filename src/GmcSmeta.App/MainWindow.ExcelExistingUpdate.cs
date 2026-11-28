using System.Windows;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class MainWindow
{
    /// <summary>
    /// Narrow integration hook for the separate safe-update engine.
    /// No EstimateView or position editor methods are changed.
    /// </summary>
    private async Task UpdateExistingEstimateFromExcelAsync(
        string filePath, ExcelImportResult imported, string parsedFingerprint, int? preferredObjectId,
        IReadOnlyList<DocRow> documents, Func<bool> contextCurrent)
    {
        if (_sqlConnectionString is null || _serverConn is null || !SqlWritesEnabled)
        {
            MessageBox.Show(this, "Изменение доступно только в GmcSmetaPilot_*.",
                "Excel safe-update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var connection = _sqlConnectionString;
        if (!contextCurrent()) return;
        var preview = new ExcelExistingUpdatePreviewWindow(this, imported, documents,
            preferredObjectId, connection, filePath, parsedFingerprint);
        if (preview.ShowDialog() != true || preview.Plan is not { } plan || !contextCurrent() ||
            !documents.Any(doc => doc.Id == plan.ContractId &&
                (preferredObjectId is null || doc.ObjectId == preferredObjectId))) return;

        using var cancel = new CancellationTokenSource();
        var progress = ImportProgressWindow("Безопасное обновление КП в одной SQL-транзакции…", cancel);
        progress.Show();
        try
        {
            var saved = await Task.Run(() =>
                SqlServerExcelExistingUpdate.Apply(connection, filePath, plan, cancel.Token));
            if (!saved.Saved)
                throw new InvalidOperationException(saved.Error ?? "SQL Server не подтвердил обновление.");
            var objectId = _docs.FirstOrDefault(x => x.Id == plan.ContractId)?.ObjectId;
            if (objectId is int id) ReloadAndSelect(id);
            StatusText.Text = "Операция Excel подтверждена. Ревизия операции: " + saved.Revision;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Обновление отменено до COMMIT; SQL-транзакция не зафиксирована.";
        }
        catch (ExcelExistingCommitUncertainException ex)
        {
            try
            {
                // Observe the journal only; never run the SQL mutation a second time.
                var committed = await Task.Run(() =>
                    SqlServerExcelExistingUpdate.WasCommitted(connection,
                        plan.ContractId, ex.OperationId));
                if (committed)
                {
                    var objectId = _docs.FirstOrDefault(x => x.Id == plan.ContractId)?.ObjectId;
                    if (objectId is int id) ReloadAndSelect(id);
                    StatusText.Text = "COMMIT подтверждён по SQL-журналу, повтор не требуется.";
                    MessageBox.Show(this, "Изменения найдены в журнале SQL Server. Повтор не требуется.",
                        "Excel safe-update", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    StatusText.Text = "Исход COMMIT неизвестен; проверь журнал и сеть до повтора.";
                    MessageBox.Show(this, ex.Message + "\nЖурнал пока не подтверждает операцию. Не повторяй без проверки.",
                        "Excel safe-update", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception verificationError)
            {
                StatusText.Text = "Нет подтверждения COMMIT; автоматический повтор запрещён.";
                MessageBox.Show(this, ex.Message + "\nНе удалось прочитать журнал: " + verificationError.Message,
                    "Excel safe-update", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            // After a broken connection during COMMIT we must NOT assert rollback:
            // the operationId in the message can be checked via WasCommitted.
            StatusText.Text = "Обновление Excel не подтверждено: " + ex.Message;
            MessageBox.Show(this,
                ex.Message + "\n\nПри ошибке связи во время COMMIT не повторяй операцию вслепую.",
                "Excel safe-update", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { progress.Close(); }
    }
}
