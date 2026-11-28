using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;
namespace GmcSmeta.App;

/// <summary>Отмена/повтор правок сметы: кнопки, Ctrl+Z/Ctrl+Y и маршрутизация клавиш.</summary>
public partial class EstimateView
{
    private void UpdateUndoButtons()
    {
        var canEdit = _estimate?.Locked == false && !_serverConflict &&
            Volatile.Read(ref _pendingWrites) == 0;
        UndoButton.IsEnabled = canEdit && MainWindow.Undo?.CanUndo(_contractId) == true;
        RedoButton.IsEnabled = canEdit && MainWindow.Undo?.CanRedo(_contractId) == true;
    }

    private async void Undo_Click(object sender, RoutedEventArgs e) => await UndoRedoAsync(undo: true);
    private async void Redo_Click(object sender, RoutedEventArgs e) => await UndoRedoAsync(undo: false);

    /// <summary>
    /// Ленивый undo-базлайн: снимок «до правки» нужен только перед первой
    /// локальной записью, а не при открытии документа и фоновых перезагрузках.
    /// </summary>
    private async Task EnsureUndoBaselineAsync()
    {
        Volatile.Write(ref _undoCheckpointNeeded, 1);
        var undo = MainWindow.Undo;
        if (undo is null || _contractId <= 0 || undo.HasCheckpoint(_contractId)) return;
        try
        {
            var expectedRevision = _serverRevision;
            if (string.IsNullOrWhiteSpace(expectedRevision)) return;
            await Task.Run(() =>
            {
                using var timing = StartupTrace.Measure("undo_first_edit_baseline");
                var snapshot = SqlServerUndo.ReadConsistent(_sqlConnectionString, _contractId, expectedRevision);
                if (snapshot is not null && !undo.HasCheckpoint(_contractId) &&
                    _serverRevision == expectedRevision)
                    undo.CheckpointSnapshot(_contractId, snapshot);
            }).ConfigureAwait(false);
        }
        catch
        {
            // Undo — вспомогательная функция: сбой снимка не блокирует запись.
        }
    }

    private async Task UndoRedoAsync(bool undo)
    {
        if (MainWindow.Undo is null || _estimate?.Locked != false || _serverConflict)
        {
            SetStatus("Отмена недоступна: документ заблокирован или есть конфликт SQL.", true);
            return;
        }
        if (Volatile.Read(ref _pendingWrites) > 0 || _refreshing)
        {
            SetStatus("Сохранение или обновление ещё выполняется.", false);
            return;
        }
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        Interlocked.Increment(ref _pendingWrites);
        InvalidateSparseReadback(); // структурная правка: только полный reload
        UpdateUndoButtons();
        var status = UndoManager.ApplyStatus.Indeterminate;
        await _writeLock.WaitWithoutBaselineAsync();
        try
        {
            if (_serverConflict || _detached)
                throw new InvalidOperationException("Документ изменился до отмены.");
            var result = await Task.Run(() =>
            {
                string? revision;
                var applied = undo
                    ? MainWindow.Undo.DoUndo(_contractId, out revision)
                    : MainWindow.Undo.DoRedo(_contractId, out revision);
                return (Status: applied, Revision: revision);
            });
            status = result.Status;
            if (status == UndoManager.ApplyStatus.Applied)
            {
                if (string.IsNullOrWhiteSpace(result.Revision))
                    throw new InvalidOperationException("SQL Server не подтвердил ревизию отмены.");
                _serverRevision = result.Revision;
                _saveAwaitingReadback = true;
            }
        }
        catch (Exception ex)
        {
            MainWindow.Undo.Forget(_contractId);
            status = UndoManager.ApplyStatus.Indeterminate;
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = "Отмена не подтверждена SQL Server: " + ex.Message;
            SetStatus(_saveError, true);
        }
        finally
        {
            _writeLock.Release();
            Interlocked.Decrement(ref _pendingWrites);
        }

        // Apply() already published the restored in-memory baseline, so the
        // reload below must not take another full undo snapshot.
        Interlocked.Exchange(ref _undoCheckpointNeeded, 0);

        switch (status)
        {
            case UndoManager.ApplyStatus.Applied:
                SetStatus(undo ? "Действие отменено" : "Действие возвращено", false);
                break;
            case UndoManager.ApplyStatus.Moved:
                _serverConflict = true;
                _saveError = "Документ изменили коллеги — история отмен сброшена.";
                SetStatus(_saveError, true);
                break;
            case UndoManager.ApplyStatus.Failed:
                SetStatus("Отмена заблокирована: документ подписан, связан с выдачей или снимок не восстановлен.", true);
                break;
            case UndoManager.ApplyStatus.Indeterminate:
                _serverConflict = true;
                _saveAwaitingReadback = true;
                _saveError ??= "Исход COMMIT неизвестен. Не повторяй отмену до обновления.";
                SetStatus(_saveError, true);
                break;
            default:
                DetachSaveCloseGuard();
                UpdateUndoButtons();
                return;
        }
        _reloadTimer.Start();
        await ReloadDocumentAsync();
        UpdateUndoButtons();
    }

    private void EstimateView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // PreviewKeyDown tunnels through the entire visual tree. Once the
        // active page is a live SQL Server procurement/cost/shipments editor,
        // its own keyboard commands must not trigger estimate Ctrl+Z/F/etc.
        if (_isContract && _activeContractWorkspace != "estimate") return;
        var modifiers = Keyboard.Modifiers;
        var ctrl = modifiers == ModifierKeys.Control;
        var ctrlShift = modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        var inTable = EstimateGrid.IsKeyboardFocusWithin;
        // A real cell editor (or search box) owns its normal Ctrl+C/V/Z and
        // typing shortcuts. Never replace a cell's text by pasting SQL rows.
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or ComboBox) return;
        if (TryZoomShortcut(e, modifiers)) return;
        // WPF reports Alt combinations as SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (inTable && ctrl && e.Key == Key.V && GridEditInteraction.TryPasteInEditingCell(EstimateGrid))
        {
            e.Handled = true;
            return;
        }

        // Python editor shortcuts: safe navigation and explicit delete
        // confirmation (CanUserDeleteRows remains disabled).
        if (ctrl && e.Key == Key.F)
        {
            EstimateSearch.Focus();
            EstimateSearch.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F1)
        {
            Help_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (inTable && modifiers == ModifierKeys.None && e.Key == Key.F2 &&
            EstimateGrid.SelectedItem is EditableEstimateRow { IsSystem: true } section)
        {
            e.Handled = true;
            if (!e.IsRepeat && EstimateGrid.SelectedItems.Count == 1 &&
                ReferenceEquals(EstimateGrid.CurrentItem, section) && IsRealCommandRow(section))
                RenameSection_Click(sender, e);
            return;
        }
        if (inTable && modifiers == ModifierKeys.None && e.Key == Key.Delete)
        {
            e.Handled = true;
            if (e.IsRepeat) return;
            if (EstimateGrid.SelectedItem is EditableEstimateRow { IsSystem: true } selectedSection)
            {
                if (EstimateGrid.SelectedItems.Count == 1 &&
                    ReferenceEquals(EstimateGrid.CurrentItem, selectedSection) &&
                    IsRealCommandRow(selectedSection))
                    DeleteSection_Click(sender, e);
            }
            else
                DeletePosition_Click(sender, e);
            return;
        }
        if (inTable && ctrl && e.Key == Key.M)
        {
            e.Handled = true;
            var selection = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().ToArray();
            if (!CommandSelectionCurrent(selection) || selection.Any(row => row.IsSystem))
            {
                SetStatus("Выбери только реальные позиции для переноса.", true);
                return;
            }
            MoveSection_Click(sender, e);
            return;
        }
        if (inTable && ctrl && (e.Key == Key.Oem6 || e.Key == Key.Oem4))
        {
            _ = IndentSelectedAsync(e.Key == Key.Oem6);
            e.Handled = true;
            return;
        }

        if (inTable && ctrlShift && e.Key == Key.V)
        {
            ReplaceRows_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (inTable && ctrl)
        {
            if (e.Key == Key.C) { CopySelectedRows(); e.Handled = true; return; }
            if (e.Key == Key.V) { PasteRows_Click(sender, e); e.Handled = true; return; }
            if (e.Key == Key.D) { Duplicate_Click(sender, e); e.Handled = true; return; }
            if (e.Key == Key.K) { AddFromCatalog_Click(sender, e); e.Handled = true; return; }
        }

        // Python kp_editor._shortcuts: fast structural edits without opening
        // "Действия". Existing click handlers enforce locked and SQL checks.
        if (inTable && key == Key.Insert)
        {
            if (modifiers == ModifierKeys.None) { AddRow_Click(sender, e); e.Handled = true; return; }
            if (modifiers == ModifierKeys.Shift) { AddComponent_Click(sender, e); e.Handled = true; return; }
            if (ctrlShift) { AddSection_Click(sender, e); e.Handled = true; return; }
            if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
            {
                AddSubsection_Click(sender, e); e.Handled = true; return;
            }
        }
        if (inTable && modifiers == ModifierKeys.Alt)
        {
            if (key == Key.Up) { MoveUp_Click(sender, e); e.Handled = true; return; }
            if (key == Key.Down) { MoveDown_Click(sender, e); e.Handled = true; return; }
        }
        if (e.Key == Key.Z && ctrl)
        {
            Undo_Click(sender, e);
            e.Handled = true;
        }
        else if ((e.Key == Key.Y && ctrl) || (e.Key == Key.Z && ctrlShift))
        {
            Redo_Click(sender, e);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Блокировка записи документа, которая перед первой правкой готовит
    /// ленивый undo-базлайн. Остальные ожидающие видят уже готовый снимок.
    /// </summary>
    private sealed class UndoAwareWriteLock
    {
        private readonly SemaphoreSlim _inner;
        private readonly Func<Task> _beforeWrite;
        private readonly OrderedWriteGate _order = new();
        private OrderedWriteGate.Ticket? _held;

        public UndoAwareWriteLock(SemaphoreSlim inner, Func<Task> beforeWrite)
        {
            _inner = inner;
            _beforeWrite = beforeWrite;
        }

        public OrderedWriteGate.Ticket Reserve() => _order.Reserve();

        public Task WaitAsync() => WaitReservedAsync(Reserve());

        public async Task WaitReservedAsync(OrderedWriteGate.Ticket reservation, bool baseline = true)
        {
            var acquired = false;
            try
            {
                await reservation.WaitAsync().ConfigureAwait(false);
                await _inner.WaitAsync().ConfigureAwait(false);
                acquired = true;
                if (baseline) await _beforeWrite().ConfigureAwait(false);
                _held = reservation;
            }
            catch
            {
                if (acquired) _inner.Release();
                reservation.Dispose();
                throw;
            }
        }

        // Undo/Redo already owns a history baseline; do not create a new one.
        public Task WaitWithoutBaselineAsync() => WaitReservedAsync(Reserve(), baseline: false);

        public void Release()
        {
            var reservation = _held ?? throw new InvalidOperationException("No acquired write reservation.");
            _held = null;
            try { _inner.Release(); }
            finally { reservation.Dispose(); }
        }
    }
}
