using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class MainWindow
{
    private bool _workspaceDeletionBusy;
    private bool _workspaceDeletionUncertain;

    private bool CanDeleteWorkspace => SqlWritesEnabled && !_startupClosed &&
        !_workspaceDeletionBusy && !_workspaceDeletionUncertain;

    private bool DeletionDocumentsSettled(IEnumerable<long> ids) =>
        ids.All(id => id <= int.MaxValue && id > 0 &&
            (!_estimateCache.TryGetValue((int)id, out var editor) || editor.CanTransferDocument));

    private void InvalidateDeletedDocuments(IEnumerable<long> ids)
    {
        foreach (var id in ids)
        {
            var key = checked((int)id);
            _estimateCache.Remove(key);
            _estimateCacheOrder.Remove(key);
            Undo?.Forget(key);
        }
    }

    private async Task<T> ReadOrDeleteAsync<T>(Func<T> operation)
    {
        // Prevent navigation while the worker is preparing a snapshot or committing.
        // Modal prompts run outside this helper, with the normal WPF owner handling.
        var enabled = IsEnabled;
        IsEnabled = false;
        try { return await Task.Run(operation); }
        finally { IsEnabled = enabled; }
    }

    private void DeletionFailed(Exception ex, bool writeAttempted, IReadOnlyList<long> affected, int objectId, bool wholeObject)
    {
        LogError(ex);
        if (ex is TrashCommitUnknownException)
        {
            _workspaceDeletionUncertain = true;
            InvalidateDeletedDocuments(affected);
            if ((_openDocumentId is int open && affected.Contains(open)) ||
                (wholeObject && _selectedObject?.Id == objectId))
                ShowEmptyPage();
            MessageBox.Show(this, ex.Message +
                "\nНе повторяйте удаление. Проверьте карточку и корзину; для следующей операции откройте программу заново.",
                "Результат удаления неизвестен", MessageBoxButton.OK, MessageBoxImage.Warning);
            _ = ReloadNavigationAfterWriteAsync(rebuildTree: true);
            return;
        }
        MessageBox.Show(this, ex.Message, writeAttempted ? "Не удалось удалить" : "Удаление недоступно",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void DeleteSqlServerObjectFlow(ObjectRow obj)
    {
        if (!CanDeleteWorkspace || obj.Archived ||
            CaptureObjectMutation(obj) is not { } target) return;
        var page = PageHost.Content;
        var navigation = _estimateOpenGeneration;
        var data = _navigationDataGeneration;
        bool Current() => !_startupClosed && ObjectMutationCurrent(obj, target) &&
            navigation == _estimateOpenGeneration && data == _navigationDataGeneration &&
            ReferenceEquals(page, PageHost.Content);
        if (!DeletionDocumentsSettled(_docs.Where(d => d.ObjectId == obj.Id).Select(d => (long)d.Id)))
        {
            StatusText.Text = "Сначала завершите сохранение всех документов карточки.";
            return;
        }

        _workspaceDeletionBusy = true;
        CancelEventHandler preventClose = (_, args) => args.Cancel = true;
        Closing += preventClose;
        bool attempted = false;
        IReadOnlyList<long> affected = Array.Empty<long>();
        try
        {
            StatusText.Text = "Проверка возможности восстановления карточки…";
            var preview = await ReadOrDeleteAsync(() =>
                SqlServerWorkspace.PrepareObjectDelete(target.Connection, obj.Id, target.Revision));
            affected = preview.DocumentIds;
            if (!Current() || !DeletionDocumentsSettled(preview.DocumentIds)) return;

            var info = preview.Info;
            var bullets = string.Join("\n", info.Details.Select(d => "• " + d));
            var question = $"Удалить карточку «{info.Name}» и все её документы?\n\n" +
                (bullets.Length > 0 ? bullets : "Карточка пуста.") +
                "\n\nКопия сохранится в корзине. Если работы завершены — лучше отправить карточку в архив.";
            if (MessageBox.Show(this, question, "Удаление карточки", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (new ConfirmDeleteDialog(info.Name, info.Details, "карточка") { Owner = this }.ShowDialog() != true ||
                !Current() || !DeletionDocumentsSettled(preview.DocumentIds)) return;

            attempted = true;
            StatusText.Text = "Удаление карточки…";
            await ReadOrDeleteAsync(() =>
            {
                SqlServerWorkspace.DeleteObject(target.Connection, obj.Id, target.Revision, preview.DocumentIds);
                return true;
            });
            InvalidateDeletedDocuments(preview.DocumentIds);
            _syncingNavigation = true;
            try { if (NavigationTree.SelectedItem is TreeViewItem selected) selected.IsSelected = false; }
            finally { _syncingNavigation = false; }
            _selectedObject = null;
            ShowEmptyPage();
            try { await ReloadNavigationDataAsync(); RefreshNavigation(); }
            catch (Exception ex) { LogError(ex); StatusText.Text = "Карточка удалена. Обновите список (F5)."; return; }
            StatusText.Text = "Карточка удалена. Копия сохранена в корзине.";
        }
        catch (Exception ex) { DeletionFailed(ex, attempted, affected, obj.Id, wholeObject: true); }
        finally { Closing -= preventClose; _workspaceDeletionBusy = false; }
    }

    private async void DeleteSqlServerContractFlow(DocRow doc, string? expectedRevision = null)
    {
        if (!CanDeleteWorkspace || doc.Locked || doc.IsStage || doc.StageRest ||
            _objects.FirstOrDefault(row => row.Id == doc.ObjectId && !row.Archived) is not { } parent ||
            CaptureObjectMutation(parent) is not { } captured || !CanStartDocumentMutation(doc)) return;
        var page = PageHost.Content;
        var navigation = _estimateOpenGeneration;
        var data = _navigationDataGeneration;
        bool Current() => !_startupClosed && ObjectMutationCurrent(parent, captured) &&
            navigation == _estimateOpenGeneration && data == _navigationDataGeneration &&
            ReferenceEquals(page, PageHost.Content) && DocumentLocallyCurrent(doc) && CanStartDocumentMutation(doc);

        _workspaceDeletionBusy = true;
        CancelEventHandler preventClose = (_, args) => args.Cancel = true;
        Closing += preventClose;
        bool attempted = false;
        try
        {
            if (!DocumentMatchesServer(doc, captured.Revision) ||
                (expectedRevision is not null && captured.Revision != expectedRevision))
                throw new InvalidOperationException("Документ изменился. Обновите список.");
            StatusText.Text = "Проверка возможности восстановления документа…";
            var info = await ReadOrDeleteAsync(() => SqlServerWorkspace.PrepareContractDelete(
                captured.Connection, doc.Id, doc.ObjectId, captured.Revision));
            if (!Current()) return;

            var question = $"Удалить «{info.Title}» из объекта «{info.ObjectName}»?\n" +
                "Все системы и позиции документа будут удалены. Копия сохранится в корзине.";
            if (MessageBox.Show(this, question, "Удаление", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (new ConfirmDeleteDialog(info.Title, info.Details, "документ") { Owner = this }.ShowDialog() != true ||
                !Current()) return;

            attempted = true;
            StatusText.Text = "Удаление документа…";
            await ReadOrDeleteAsync(() =>
            {
                SqlServerWorkspace.DeleteContract(captured.Connection, doc.Id, captured.Revision, doc.ObjectId);
                return true;
            });
            InvalidateDeletedDocuments(new[] { (long)doc.Id });
            if (_openDocumentId == doc.Id) ShowEmptyPage();
            try { ReloadAndSelect(doc.ObjectId); }
            catch (Exception ex) { LogError(ex); StatusText.Text = "Документ удалён. Обновите список (F5)."; return; }
            StatusText.Text = $"Документ удалён: «{info.Title}». Копия сохранена в корзине.";
        }
        catch (Exception ex) { DeletionFailed(ex, attempted, new[] { (long)doc.Id }, doc.ObjectId, wholeObject: false); }
        finally { Closing -= preventClose; _workspaceDeletionBusy = false; }
    }
}
