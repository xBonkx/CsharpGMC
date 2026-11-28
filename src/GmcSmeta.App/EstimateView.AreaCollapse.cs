using System.Threading;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Reversible Python area.collapse/expand on test SQL Server only.</summary>
public partial class EstimateView
{
    private readonly HashSet<int> _expandedAreaSummaries = new();

    private bool CanTransformArea =>
        _estimate is { } snapshot && !_doc.IsStage &&
        CanChangeHeader(snapshot, _serverRevision, false);

    private void CollapseAreaSystem_Click(object sender, RoutedEventArgs e) =>
        TransformArea(whole: false);

    private void CollapseAreaWhole_Click(object sender, RoutedEventArgs e) =>
        TransformArea(whole: true);

    private void ExpandAreaAll_Click(object sender, RoutedEventArgs e) =>
        ExpandArea(null);

    private void TransformArea(bool whole)
    {
        if (!CanTransformArea) return;
        var revision = _serverRevision;
        var subject = whole ? "на всё КП" : "по системам";
        if (MessageBox.Show(Window.GetWindow(this),
            $"Свернуть воздуховоды, фасонку и изоляцию в м² {subject}?\n\n" +
            "Исходные позиции сохранятся вложенными строками. " +
            "Позиции с актами, закупками, движениями или комплектующими будут пропущены. " +
            "При ошибке SQL отменит все изменения.",
            "Сворачивание в м²", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (revision == _serverRevision) _ = ExecuteAreaTransformAsync(whole, null, false, revision);
    }

    private void ExpandArea(long? only)
    {
        if (!CanTransformArea) return;
        var revision = _serverRevision;
        if (MessageBox.Show(Window.GetWindow(this),
            "Вернуть вложенные позиции в исходные системы и порядок? " +
            "Их прежние цены и количества сохранятся, сводные строки м² удалятся. " +
            "При связанных актах и выдачах операция полностью отменится.",
            "Развернуть м²", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (revision == _serverRevision) _ = ExecuteAreaTransformAsync(false, only, true, revision);
    }

    private async Task ExecuteAreaTransformAsync(bool whole, long? only, bool expand, string revision)
    {
        if (!CanTransformArea || revision != _serverRevision) return;
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        Interlocked.Increment(ref _pendingWrites);
        SetStatus(expand ? "Разворачивание м²…" : "Сворачивание м²…", false);
        await _writeLock.WaitAsync();
        var saved = false;
        try
        {
            if (_serverConflict || _detached || revision != _serverRevision ||
                _estimate?.Locked != false)
                throw new InvalidOperationException("Смета изменилась после подтверждения.");
            var result = await Task.Run(() => expand
                ? SqlServerDatabase.ExpandArea(_sqlConnectionString, _contractId, only, revision)
                : SqlServerDatabase.CollapseArea(_sqlConnectionString, _contractId, whole, revision));
            if (!result.Saved)
            {
                _saveError = "м²: " + result.Error;
                _serverConflict = true;
                return;
            }
            _serverRevision = result.Revision;
            _saveAwaitingReadback = true;
            _expandedAreaSummaries.Clear();
            SetStatus(expand
                ? $"Развёрнуто: {result.Rows} позиций из {result.Groups} сводных строк."
                : $"Свёрнуто: {result.Rows} позиций → {result.Groups} групп. " +
                  $"Пропущено связанных: {result.Skipped}; без площади: {result.NoArea}.",
                false);
            saved = true;
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = "Преобразование м² не подтверждено: " + ex.Message;
            SetStatus(_saveError, true);
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
                _reloadTimer.Start();
        }
        if (saved && Volatile.Read(ref _pendingWrites) == 0) await ReloadDocumentAsync();
    }
}
