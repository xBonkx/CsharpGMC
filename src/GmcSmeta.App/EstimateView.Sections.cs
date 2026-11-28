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

/// <summary>Разделы и позиции: добавление, переименование, перемещение, удаление, импорт из Excel.</summary>
public partial class EstimateView
{
    private async void AddSection_Click(object sender, RoutedEventArgs e) =>
        await AddSectionAsync(subsection: false);

    private async void AddSubsection_Click(object sender, RoutedEventArgs e) =>
        await AddSectionAsync(subsection: true);

    private async Task AddSectionAsync(bool subsection)
    {
        if (_estimate?.Locked != false)
            return;
        if (_serverConflict)
        {
            SetStatus("Сначала обнови смету: данные на сервере изменились.", true);
            return;
        }

        if (!EstimateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        var revision = _serverRevision;
        if (_estimate is not { } snapshot || !CanAppendImport(revision) || HasUnsettledCacheWrites) return;
        var primary = EstimateGrid.SelectedItem;
        var selection = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().ToArray();
        var selected = EstimateGrid.SelectedItem as EditableEstimateRow;
        if (selection.Length > 1 || (selected is not null && !IsRealCommandRow(selected)))
        {
            SetStatus("Выбери один реальный раздел или позицию, а не временную группу.", true);
            return;
        }
        // For a subsection a parent is required. Top-level sections may be added to an empty document.
        selected ??= subsection ? EstimateGrid.Items.OfType<EditableEstimateRow>()
            .FirstOrDefault(r => r.IsSystem && r.Level == 0 && IsRealCommandRow(r)) : null;
        long? anchorId = selected?.Source.SystemId;
        if (subsection && anchorId is null)
        {
            SetStatus("Сначала создай или выбери раздел.", true);
            return;
        }
        var prompt = new PromptDialog(subsection ? "Новый подраздел" : "Новый раздел",
            subsection ? "Наименование подраздела" : "Наименование раздела", "")
        { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true) return;
        var name = prompt.Value;
        if (name.Length == 0 || name.Length > 255)
        {
            SetStatus("Название раздела должно содержать от 1 до 255 символов.", true);
            return;
        }

        if (!CanAppendImport(revision) || HasUnsettledCacheWrites ||
            !ReferenceEquals(primary, EstimateGrid.SelectedItem) ||
            !EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().SequenceEqual(selection, ReferenceEqualityComparer.Instance) ||
            (selected is not null && !_rows.Contains(selected))) return;
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var count = Interlocked.Increment(ref _pendingWrites);
        InvalidateSparseReadback(); // структурная правка: только полный reload
        SetStatus($"Создание раздела… ({count})", false);
        await _writeLock.WaitAsync();
        try
        {
            if (!CommandWriteCurrent(snapshot, revision) ||
                !ReferenceEquals(primary, EstimateGrid.SelectedItem) ||
                !EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().SequenceEqual(selection, ReferenceEqualityComparer.Instance) ||
                (selected is not null && !IsRealCommandRow(selected)))
                throw new InvalidOperationException("Смета или выделение изменились.");
            var result = await Task.Run(() => SqlServerDatabase.AddSection(
                _sqlConnectionString, _contractId, name, subsection, anchorId, revision));
            if (!result.Saved)
            {
                _serverConflict = true;
                throw new InvalidOperationException(result.Error ?? "Сервер отклонил создание раздела.");
            }
            _serverRevision = result.Revision;
            _saveAwaitingReadback = true;
            _focusSystemAfterReload = checked((int)result.SystemId!.Value);
            if (!string.IsNullOrEmpty(EstimateSearch.Text)) EstimateSearch.Clear();
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? "Раздел добавлен ✓" : "Ошибка записи: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }

    private async void RenameSection_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate?.Locked != false)
            return;
        if (_serverConflict)
        {
            SetStatus("Сначала обнови смету: данные на сервере изменились.", true);
            return;
        }
        if (!EstimateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        var revision = _serverRevision;
        if (_estimate is not { } snapshot || !CanAppendImport(revision) || HasUnsettledCacheWrites) return;
        var primary = EstimateGrid.SelectedItem;
        var selection = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().ToArray();
        if (EstimateGrid.SelectedItem is not EditableEstimateRow selected ||
            !selected.IsSystem || !IsRealCommandRow(selected) || selection.Length != 1)
        {
            SetStatus("Выбери один заголовок раздела.", true);
            return;
        }
        var systemId = selected.Source.SystemId;
        var prompt = new PromptDialog("Переименовать раздел", "Наименование раздела", selected.Name)
        { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true) return;
        var name = prompt.Value;
        if (name.Length == 0 || name.Length > 255)
        {
            SetStatus("Название должно содержать от 1 до 255 символов.", true);
            return;
        }
        if (string.Equals(selected.Name, name, StringComparison.Ordinal)) return;

        if (!CanAppendImport(revision) || HasUnsettledCacheWrites ||
            !ReferenceEquals(primary, EstimateGrid.SelectedItem) ||
            !EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().SequenceEqual(selection, ReferenceEqualityComparer.Instance) ||
            (selected is not null && !_rows.Contains(selected))) return;
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var count = Interlocked.Increment(ref _pendingWrites);
        InvalidateSparseReadback(); // структурная правка: только полный reload
        SetStatus($"Переименование… ({count})", false);
        await _writeLock.WaitAsync();
        try
        {
            if (!CommandWriteCurrent(snapshot, revision) ||
                !ReferenceEquals(primary, EstimateGrid.SelectedItem) ||
                !EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().SequenceEqual(selection, ReferenceEqualityComparer.Instance) ||
                (selected is not null && !IsRealCommandRow(selected)))
                throw new InvalidOperationException("Смета или выделение изменились.");
            var result = await Task.Run(() => SqlServerDatabase.RenameSection(
                _sqlConnectionString, _contractId, systemId, name, revision));
            if (!result.Saved)
            {
                _serverConflict = true;
                throw new InvalidOperationException(result.Error ?? "Сервер отклонил переименование.");
            }
            _serverRevision = result.Revision;
            _saveAwaitingReadback = true;
            _focusSystemAfterReload = systemId;
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? "Раздел переименован ✓" : "Ошибка записи: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }

    private async void AddRow_Click(object sender, RoutedEventArgs e) =>
        await AddPositionAsync(component: false);

    private async void AddComponent_Click(object sender, RoutedEventArgs e) =>
        await AddPositionAsync(component: true);

    /// <summary>
    /// Adds a row into the currently selected system, then opens its name for editing.
    /// Uses the same serialized write queue as cell edits, so quick edits cannot overtake inserts.
    /// </summary>
    private async Task AddPositionAsync(bool component)
    {
        if (_estimate?.Locked != false || !ActiveEstimateWriter() || _refreshing ||
            string.IsNullOrWhiteSpace(_publishedEstimateRevision) || _doc.StageRest) return;
        if (_serverConflict || _cellSaveQueue.Blocked || _saveError is not null)
        {
            SetStatus("Сначала обнови смету: есть неподтверждённые правки.", true);
            return;
        }
        if (!EstimateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        var selected = EstimateGrid.SelectedItem as EditableEstimateRow;
        if (EstimateGrid.SelectedItems.Count > 1)
        {
            SetStatus("Выбери одну строку для добавления.", true);
            return;
        }
        selected ??= EstimateGrid.Items.OfType<EditableEstimateRow>().FirstOrDefault(r => r.IsSystem && IsRealCommandRow(r));
        if (selected is not null && !selected.IsPendingInsert && !IsRealCommandRow(selected))
        {
            SetStatus("В режиме группировки сначала раскрой группу и выбери конкретную позицию.", true);
            return;
        }
        if (selected is null || selected.Source.SystemId <= 0)
        {
            SetStatus("В смете пока нет разделов. Нажми «+ Раздел».", true);
            return;
        }
        if (component && (selected.IsSystem || selected.Source.OutOfKp))
        {
            SetStatus("Для комплектующей сначала выбери обычную позицию КП.", true);
            return;
        }

        var systemId = selected.Source.SystemId;
        var anchorDraft = selected.IsPendingInsert ? FindPendingInsert(selected.Id) : null;
        if (selected.IsPendingInsert && anchorDraft is null) return;
        long? afterId = selected.IsSystem ? null : selected.Id;
        long? parentId = component
            ? selected.Source.ParentPositionId ?? selected.Id
            : null;
        var parentDraft = parentId is < 0 ? FindPendingInsert(checked((int)parentId.Value)) : null;
        if (parentId is < 0 && parentDraft is null) return;
        if (anchorDraft is not null && !CanShowPendingInsert)
        {
            SetStatus("Для добавления под новой строкой верни обычный вид сметы.", true);
            return;
        }
        var previousInsert = _pendingInsertTail;
        using var reservation = _writeLock.Reserve();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingInsertTail = completion.Task;
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var count = Interlocked.Increment(ref _pendingWrites);
        InvalidateSparseReadback(); // структурная правка: только полный reload
        SetStatus($"Добавление строки… ({count})", false);
        PendingEstimateInsert? preview = null;
        var acquired = false;
        try
        {
            // Publish a local draft before any SQL/undo wait; it is NOT in the
            // authoritative document or in the SQL row index.
            preview = TryShowPendingInsert(selected, component);
            await previousInsert;
            await _writeLock.WaitReservedAsync(reservation);
            acquired = true;
            if (_serverConflict || _cellSaveQueue.Blocked || _saveError is not null ||
                _estimate?.Locked != false)
                throw new InvalidOperationException("Серверная смета устарела, обнови её.");
            var realAfterId = anchorDraft is null ? afterId : ResolvePendingAnchor(anchorDraft);
            var realParentId = parentDraft is null ? parentId : ResolvePendingAnchor(parentDraft);
            var result = await Task.Run(() => SqlServerDatabase.AddPosition(
                _sqlConnectionString, _contractId, systemId, realAfterId, realParentId, _serverRevision));
            if (!result.Saved)
                throw new InvalidOperationException(result.Error ?? "Сервер отклонил вставку.");
            _serverRevision = result.Revision;
            var id = checked((int)result.PositionId!.Value);
            _saveAwaitingReadback = true;
            if (preview is not null)
            {
                preview.ServerId = id;
                // Earlier typed values belong to this insertion. Flush them
                // under its reservation, before any subsequent structural command.
                var buffered = preview.BufferedEdits.ToArray();
                preview.BufferedEdits.Clear();
                foreach (var edit in buffered)
                {
                    var receipt = await Task.Run(() => WriteCellUnderLock(
                        new ContractCellSaveQueue.Edit(id, edit.Key, edit.Value)));
                    if (!receipt.Saved)
                        throw new InvalidOperationException(receipt.Error ?? "Правка новой позиции не подтверждена.");
                }
            }
            else
            {
                _focusAfterReload = id;
                _editAfterReload = true;
                if (!string.IsNullOrEmpty(EstimateSearch.Text)) EstimateSearch.Clear();
            }
        }
        catch (Exception ex)
        {
            // A lost COMMIT acknowledgement must not cause another insertion.
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = "Добавление не подтверждено: " + ex.Message;
            if (preview is not null) preview.Failed = true;
        }
        finally
        {
            if (acquired) _writeLock.Release();
            else reservation.Dispose();
            completion.TrySetResult(true);
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? "Строка добавлена; сверка с SQL Server…" : "Ошибка записи: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }

    /// <summary>
    /// Ctrl+K / «Из справочника…»: отметить номенклатуру с количеством и вставить
    /// позиции под выбранной строкой (kp_editor.add_from_catalog). Цена и закуп
    /// берутся из справочника, как в Python.
    /// </summary>
    private async void AddFromCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate?.Locked == true)
            return;
        if (_serverConflict)
        {
            SetStatus("Сначала обнови смету: серверная ревизия изменилась.", true);
            return;
        }

        if (!EstimateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        var revision = _serverRevision;
        if (_estimate is not { } snapshot || !CanAppendImport(revision))
        {
            SetStatus("Заверши сохранение и обнови смету.", true);
            return;
        }
        var selected = EstimateGrid.SelectedItem as EditableEstimateRow;
        selected ??= EstimateGrid.Items.OfType<EditableEstimateRow>().FirstOrDefault(r => r.IsSystem && IsRealCommandRow(r));
        if (EstimateGrid.SelectedItems.Count > 1 || (selected is not null && !IsRealCommandRow(selected)))
        {
            SetStatus("Выбери одну реальную позицию или раздел.", true);
            return;
        }
        if (selected is null || selected.Source.SystemId <= 0)
        {
            SetStatus("В смете пока нет разделов. Нажми «+ Раздел».", true);
            return;
        }

        var primary = EstimateGrid.SelectedItem;
        var selection = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().ToArray();
        bool TargetCurrent() => IsRealCommandRow(selected) &&
            ReferenceEquals(primary, EstimateGrid.SelectedItem) &&
            EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().SequenceEqual(selection, ReferenceEqualityComparer.Instance);
        var systemId = selected.Source.SystemId;
        long? afterId = selected.IsSystem ? null : selected.Source.ParentPositionId ?? selected.Id;
        var targetName = _rows.FirstOrDefault(r => r.IsSystem && r.Source.SystemId == systemId)?.Name
            ?? "Без системы";
        var picker = new CatalogPickerWindow(_sqlConnectionString, targetName, revision)
        {
            Owner = Window.GetWindow(this),
        };
        if (picker.ShowDialog() != true || picker.Picked.Count == 0) return;
        if (!CanAppendImport(revision) || !ReferenceEquals(snapshot, _estimate) || !TargetCurrent())
        {
            SetStatus("Смета изменилась. Выбери позиции из справочника заново.", true);
            return;
        }
        var rows = picker.Picked.Select(p => new CopiedRow(
            p.Name, p.Unit, p.Qty, p.Price, p.PurchasePrice, "", "", null)).ToList();

        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var count = Interlocked.Increment(ref _pendingWrites);
        InvalidateSparseReadback(); // структурная правка: только полный reload
        SetStatus($"Добавление из справочника… ({count})", false);
        await _writeLock.WaitAsync();
        try
        {
            if (!CommandWriteCurrent(snapshot, revision) || !TargetCurrent())
                throw new InvalidOperationException("Серверная смета устарела, обнови её.");
            var result = await Task.Run(() => SqlServerDatabase.InsertCopiedRows(
                _sqlConnectionString, _contractId, systemId, afterId, rows, revision));
            if (!result.Saved)
            {
                _serverConflict = true;
                throw new InvalidOperationException(result.Error ?? "Сервер отклонил вставку.");
            }
            _serverRevision = result.Revision;
            _saveAwaitingReadback = true;
            if (!string.IsNullOrEmpty(EstimateSearch.Text)) EstimateSearch.Clear();
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = "Вставка из справочника не подтверждена: " + ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null
                        ? $"Из справочника добавлено: {rows.Count} поз. Выполняется сверка…"
                        : "Ошибка записи: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }

    /// <summary>
    /// Ctrl+] / Ctrl+[ : сделать строку компонентом строки выше (или обратно) —
    /// перенос builder.make_subposition. SQL Server версия — SqlServerDatabase.MakeSubposition.
    /// </summary>
    private bool CanIndentRow(EditableEstimateRow selected, bool makeSub)
    {
        if (selected.IsSystem || selected.IsVirtual || selected.Id <= 0 ||
            selected.IsAreaSummary || selected.Source.OutOfKp || !_rows.Contains(selected)) return false;
        try
        {
            _ = IndentHierarchyPlan.Build(_rows.Where(row => !row.IsSystem && !row.IsVirtual &&
                row.Source.SystemId == selected.Source.SystemId)
                .Select(row => new IndentHierarchyPlan.Row(row.Id, row.Source.ParentPositionId,
                    row.SortKey, !row.IsAreaSummary && !row.Source.OutOfKp)), selected.Id, makeSub);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    private async Task IndentSelectedAsync(bool makeSub)
    {
        if (!CanPlaceEstimateRows ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Row, true) ||
            !CanPlaceEstimateRows)
        {
            SetStatus("Вложенность доступна только в обычном порядке КП без фильтра и незавершённых записей.", true);
            return;
        }
        var selected = EstimateGrid.SelectedItem as EditableEstimateRow;
        if (selected is null || EstimateGrid.SelectedItems.Count != 1 ||
            !CanIndentRow(selected, makeSub) || HasUnsettledCacheWrites ||
            !CanAppendImport(_serverRevision))
        {
            SetStatus("Выбери обычную позицию (не раздел и не группа).", true);
            return;
        }

        var revision = _serverRevision;
        var contractId = _contractId;
        var connectionString = _sqlConnectionString;
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var count = Interlocked.Increment(ref _pendingWrites);
        InvalidateSparseReadback(); // структурная правка: только полный reload
        SetStatus($"Изменение вложенности… ({count})", false);
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict || _detached || _estimate?.Locked != false ||
                _serverRevision != revision || _contractId != contractId ||
                _sqlConnectionString != connectionString || _cellSaveQueue.Blocked ||
                _saveAwaitingReadback || _saveError is not null ||
                !ReferenceEquals(EstimateGrid.SelectedItem, selected) ||
                EstimateGrid.SelectedItems.Count != 1 || !CanIndentRow(selected, makeSub))
                throw new InvalidOperationException("Серверная смета устарела, обнови её.");
            var result = await Task.Run(() => SqlServerDatabase.MakeSubposition(
                connectionString, contractId, selected.Id, makeSub, revision));
            if (!result.Saved)
            {
                _serverConflict = result.Revision != _serverRevision;
                throw new InvalidOperationException(result.Error ?? "Сервер отклонил изменение.");
            }
            _serverRevision = result.Revision;
            _saveAwaitingReadback = true;
            _focusAfterReload = selected.Id;
            if (!string.IsNullOrEmpty(EstimateSearch.Text)) EstimateSearch.Clear();
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? "Вложенность изменена ✓" : "Ошибка: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }

    private async void MoveUp_Click(object sender, RoutedEventArgs e) => await MoveSelectedAsync(-1);
    private async void MoveDown_Click(object sender, RoutedEventArgs e) => await MoveSelectedAsync(1);

    private async Task MoveSelectedAsync(int direction)
    {
        if (_estimate?.Locked != false) return;
        if (!EstimateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Row, true) || !CanReorderEstimateRows)
        {
            SetStatus("Перестановка доступна в исходном порядке, без фильтров и незавершённых записей.", true);
            return;
        }
        if (EstimateGrid.SelectedItem is not EditableEstimateRow row ||
            !IsRealCommandRow(row) || EstimateGrid.SelectedItems.Count != 1)
        {
            SetStatus("Выбери одну позицию или раздел.", true);
            return;
        }
        var revision = _publishedEstimateRevision;
        if (_estimate is not { } snapshot || !CanAppendImport(revision)) return;
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var count = Interlocked.Increment(ref _pendingWrites);
        InvalidateSparseReadback(); // структурная правка: только полный reload
        SetStatus($"Перемещение… ({count})", false);
        await _writeLock.WaitAsync();
        try
        {
            if (!CommandWriteCurrent(snapshot, revision) || !IsRealCommandRow(row) ||
                EstimateGrid.SelectedItems.Count != 1 || !ReferenceEquals(EstimateGrid.SelectedItem, row))
                throw new InvalidOperationException("Смета или выделение изменились.");
            var result = row.IsSystem
                ? await MoveSelectedSystemAsync(row, direction)
                : await Task.Run(() => SqlServerDatabase.MovePosition(
                    _sqlConnectionString, _contractId, row.Source.SystemId, row.Id, direction, revision));
            if (!result.Saved)
            {
                _serverConflict = true;
                throw new InvalidOperationException(result.Error ?? "Перемещение отклонено.");
            }
            _serverRevision = result.Revision;
            _saveAwaitingReadback = true;
            if (row.IsSystem) _focusSystemAfterReload = row.Source.SystemId;
            else _focusAfterReload = row.Id;
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? "Порядок обновлён ✓" : "Ошибка: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }

    private async void DeletePosition_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate?.Locked != false) return;
        if (!EstimateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            ShowPositionDeleteError("Заверши редактирование ячейки перед удалением.");
            return;
        }
        // Delete and the context-menu command operate on the SAME Extended selection.
        // Never silently delete just SelectedItem when several rows are highlighted.
        var selected = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().ToArray();
        if (!CommandSelectionCurrent(selected) || selected.Any(row => row.IsSystem))
        {
            ShowPositionDeleteError("Выдели только обычные позиции или комплектующие, без разделов и групп.");
            return;
        }
        if (_estimate is not { } snapshot || !CanAppendImport(_publishedEstimateRevision))
        {
            ShowPositionDeleteError("Сохранение или сверка с SQL Server ещё не завершены. " +
                "Дождись завершения или обнови смету.");
            return;
        }

        var positionIds = selected.Select(row => (long)row.Id).Distinct().ToArray();
        var selectedIds = positionIds.ToHashSet();
        // Removing a root includes its components, but explicitly selected components
        // must not be double-counted in the preview.
        var extraComponents = _rows.Count(row => !row.IsSystem && !row.IsVirtual &&
            row.Source.ParentPositionId is int parent && selectedIds.Contains(parent) &&
            !selectedIds.Contains(row.Id));
        var revision = _serverRevision;
        var dialog = new DeletePositionsDialog(selected.Select(row => row.Name).ToArray(), extraComponents)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true) return;

        if (!CanAppendImport(revision) || !ReferenceEquals(snapshot, _estimate) ||
            !CommandSelectionCurrent(selected))
        {
            ShowPositionDeleteError("Выделение или данные изменились. Повтори удаление после обновления.");
            return;
        }

        var sectionToFocus = selected[0].Source.SystemId;
        await RunSectionCommandAsync(rev =>
        {
            if (rev != revision)
                return SqlServerDatabase.SaveResult.Fail("Смета изменилась. Повтори удаление.", rev);
            return SqlServerDatabase.DeletePositions(
                _sqlConnectionString, _contractId, positionIds, rev);
        }, "Удаление позиций", $"Удалено позиций: {positionIds.Length}",
            () =>
            {
                _focusAfterReload = null;
                _focusSystemAfterReload = sectionToFocus;
            }, showDeleteFailure: true, targetCurrent: () =>
                CommandWriteCurrent(snapshot, revision) && CommandSelectionCurrent(selected));
    }

    /// <summary>Run every structural command behind the same save lock as cell editing.</summary>
    private async Task RunSectionCommandAsync(Func<string, SqlServerDatabase.SaveResult> operation,
        string busy, string success, Action? onSuccess = null, bool showDeleteFailure = false,
        Func<bool>? targetCurrent = null)
    {
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var count = Interlocked.Increment(ref _pendingWrites);
        InvalidateSparseReadback(); // структурная правка: только полный reload
        SetStatus($"{busy}… ({count})", false);
        string? deleteError = null;
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict || _saveError is not null || !ActiveEstimateWriter() ||
                _estimate?.Locked != false || (targetCurrent is not null && !targetCurrent()))
                throw new InvalidOperationException("Смета или выделение изменились. Обнови её.");
            var revision = _serverRevision;
            var result = await Task.Run(() => operation(revision));
            if (!result.Saved)
            {
                if (result.Revision != _serverRevision) _serverConflict = true;
                if (showDeleteFailure) deleteError = result.Error ?? "Сервер отклонил удаление.";
                throw new InvalidOperationException(result.Error ?? "Сервер отклонил команду.");
            }
            _serverRevision = result.Revision;
            _saveAwaitingReadback = true;
            onSuccess?.Invoke();
        }
        catch (Exception ex)
        {
            _saveError = ex.Message;
            _serverConflict = true;
            _saveAwaitingReadback = true;
            if (showDeleteFailure && deleteError is null)
            {
                _serverConflict = true;
                _saveAwaitingReadback = true;
                deleteError = "Удаление не подтверждено SQL Server. Обнови смету перед повтором.\n" + ex.Message;
            }
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? success + " ✓" : "Ошибка: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
        if (showDeleteFailure && Volatile.Read(ref _pendingWrites) == 0)
        {
            _reloadTimer.Stop();
            await ReloadDocumentAsync();
        }
        if (deleteError is not null) ShowPositionDeleteError(deleteError);
    }

    private void ShowPositionDeleteError(string message)
    {
        SetStatus("Удаление: " + message, true);
        MessageBox.Show(message, "Удаление позиций", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void DeleteSection_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate?.Locked != false) return;
        if (!EstimateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        if (EstimateGrid.SelectedItem is not EditableEstimateRow selected ||
            !selected.IsSystem || !IsRealCommandRow(selected))
        {
            SetStatus("Выбери заголовок раздела или подраздела.", true);
            return;
        }
        var revision = _serverRevision;
        if (_estimate is not { } snapshot || !CanAppendImport(revision) || HasUnsettledCacheWrites ||
            EstimateGrid.SelectedItems.Count != 1 || !_rows.Contains(selected)) return;
        var sectionId = selected.Source.SystemId;
        if (MessageBox.Show(Window.GetWindow(this),
            $"Удалить раздел «{selected.Name}»?\n\n" +
            "Будут удалены все его позиции и комплектующие, а для корневого раздела — и подразделы. " +
            "Если есть КС-2 или проведённые выдачи, удаление будет запрещено.\n\nЭто действие нельзя отменить.",
            "Удаление раздела", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;
        if (!CanAppendImport(revision) || HasUnsettledCacheWrites ||
            !ReferenceEquals(EstimateGrid.SelectedItem, selected) ||
            EstimateGrid.SelectedItems.Count != 1 || !_rows.Contains(selected)) return;
        await RunSectionCommandAsync(rev => rev != revision
                ? SqlServerDatabase.SaveResult.Fail("Смета изменилась. Повтори удаление.", rev)
                : SqlServerDatabase.DeleteSection(_sqlConnectionString, _contractId, sectionId, revision),
            "Удаление раздела", "Раздел удалён",
            () => { _focusAfterReload = null; _focusSystemAfterReload = null; }, showDeleteFailure: true,
            targetCurrent: () => CommandWriteCurrent(snapshot, revision) && IsRealCommandRow(selected) &&
                EstimateGrid.SelectedItems.Count == 1 && ReferenceEquals(EstimateGrid.SelectedItem, selected));
    }

    /// <summary>
    /// Python move_to_system_dialog/place_positions: move every selected root (with
    /// components) to the end of the selected section, preserving ID and row order.
    /// SQL DragPositions rechecks contract, meta.rev and dependent documents atomically.
    /// </summary>
    private async void MoveSection_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate?.Locked != false) return;
        if (!EstimateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        if (!CanPlaceEstimateRows)
        {
            SetStatus("Перенос доступен только в обычном порядке КП, без фильтра и незавершённых записей.", true);
            return;
        }
        var selection = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>()
            .Where(r => !r.IsSystem && !r.IsVirtual)
            .OrderBy(r => r.SortKey).ToList();
        if (selection.Count == 0 && EstimateGrid.SelectedItem is EditableEstimateRow current &&
            !current.IsSystem && !current.IsVirtual)
            selection.Add(current);
        if (selection.Count == 0)
        {
            SetStatus("Выдели позиции для переноса в другой раздел.", true);
            return;
        }
        if (!CommandSelectionCurrent(EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().ToArray()) ||
            selection.Any(r => r.IsSystem || !IsRealCommandRow(r)))
        {
            SetStatus("Не смешивай позиции и заголовки разделов в одном переносе.", true);
            return;
        }

        // A selected component represents its parent position, as in Python.
        var roots = selection.Select(r => r.Source.ParentPositionId ?? r.Id)
            .Distinct().Select(id => _rows.FirstOrDefault(r => r.Id == id && !r.IsSystem))
            .ToList();
        if (roots.Any(r => r is null))
        {
            SetStatus("Не найдена родительская позиция: обнови КП.", true);
            return;
        }
        var picked = roots.Select(r => (long)r!.Id).ToArray();
        if (picked.Length > EstimateDragPlan.MaxPositions)
        {
            SetStatus("Выбрано слишком много позиций для одного переноса.", true);
            return;
        }
        var sources = roots.Select(r => r!.Source.SystemId).Distinct().ToHashSet();
        var targets = _rows.Where(r => r.IsSystem &&
                (sources.Count != 1 || !sources.Contains(r.Source.SystemId)))
            .Select(r => ((long)r.Source.SystemId, new string(' ', r.Level * 3) + r.Name))
            .ToList();
        if (targets.Count == 0)
        {
            SetStatus("В документе нет другого доступного раздела.", true);
            return;
        }
        var revision = _serverRevision;
        if (_estimate is not { } snapshot || !CanAppendImport(revision)) return;
        var selectedRows = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().ToArray();
        var picker = new SectionPickerDialog(targets) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.SelectedSectionId is not long to) return;
        if (!CanAppendImport(revision) || HasUnsettledCacheWrites || !CanPlaceEstimateRows ||
            !EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().SequenceEqual(selectedRows, ReferenceEqualityComparer.Instance) ||
            !targets.Any(target => target.Item1 == to) || !roots.All(row => row is not null && _rows.Contains(row))) return;

        // The drag planner interprets null as insertion at the start. To match
        // Python's 'move_to_system' we explicitly anchor after the last root.
        var last = _rows.Where(r => !r.IsSystem && !r.IsSub && !r.IsVirtual &&
                r.Source.SystemId == to && !picked.Contains(r.Id))
            .OrderBy(r => r.SortKey).LastOrDefault();
        long? anchor = last?.Id;
        await RunSectionCommandAsync(rev => rev != revision
            ? SqlServerDatabase.SaveResult.Fail("Смета изменилась. Повтори перенос.", rev)
            : SqlServerDatabase.DragPositions(_sqlConnectionString, _contractId, picked, to, anchor, revision),
            $"Перенос {picked.Length} позиций", $"Перенесено позиций: {picked.Length}",
            () =>
            {
                _focusAfterReload = checked((int)picked[0]);
                if (!string.IsNullOrWhiteSpace(EstimateSearch.Text)) EstimateSearch.Clear();
            }, targetCurrent: () => CommandWriteCurrent(snapshot, revision) &&
                CommandSelectionCurrent(selectedRows) && roots.All(row => row is not null && IsRealCommandRow(row)));
    }

    private async void PasteExcel_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate?.Locked != false) return;
        if (!EstimateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            SetStatus("Заверши редактирование перед вставкой.", true);
            return;
        }
        var revision = _serverRevision;
        if (_estimate is not { } snapshot || !CanAppendImport(revision) || HasUnsettledCacheWrites)
        {
            SetStatus("Заверши сохранение и обнови смету перед вставкой.", true);
            return;
        }
        var selected = EstimateGrid.SelectedItem as EditableEstimateRow ??
            EstimateGrid.Items.OfType<EditableEstimateRow>().FirstOrDefault(r => r.IsSystem && IsRealCommandRow(r));
        if (selected is null || !IsRealCommandRow(selected) || EstimateGrid.SelectedItems.Count > 1)
        {
            SetStatus("Выбери конкретный раздел или позицию для вставки Excel.", true);
            return;
        }
        var primary = EstimateGrid.SelectedItem;
        var selection = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().ToArray();
        bool TargetCurrent() => IsRealCommandRow(selected) &&
            ReferenceEquals(primary, EstimateGrid.SelectedItem) &&
            EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().SequenceEqual(selection, ReferenceEqualityComparer.Instance);
        IReadOnlyList<PastedPosition> parsed;
        try
        {
            var clipboard = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            parsed = PastedPositions.Parse(clipboard);
        }
        catch (Exception ex) { ShowExcelPasteError(ex.Message); return; }
        if (parsed.Count == 0) { ShowExcelPasteError("В буфере нет позиций."); return; }

        var section = selected.Source.SystemId;
        long? after = selected.IsSystem ? null : selected.Source.ParentPositionId ?? selected.Id;
        var name = _rows.FirstOrDefault(r => r.IsSystem && r.Source.SystemId == section)?.Name ?? "Без названия";
        if (!ConfirmExcelPaste(parsed, name)) return;
        if (!CanAppendImport(revision) || HasUnsettledCacheWrites ||
            !ReferenceEquals(snapshot, _estimate) || !TargetCurrent())
        {
            ShowExcelPasteError("Смета или выбранная строка изменились. Обнови смету и повтори вставку.");
            return;
        }

        // Use the same guarded write/readback lifecycle as the other clipboard
        // operations. Capture the target and revision BEFORE the modal dialog.
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        Interlocked.Increment(ref _pendingWrites);
        InvalidateSparseReadback(); // структурная правка: только полный reload
        SetStatus($"Вставка из Excel: {parsed.Count} строк…", false);
        string? error = null;
        var saved = false;
        await _writeLock.WaitAsync();
        try
        {
            if (!CommandWriteCurrent(snapshot, revision) || !TargetCurrent())
                throw new InvalidOperationException("Смета или выделение изменились до записи.");
            var result = await Task.Run(() => SqlServerDatabase.PastePositions(
                _sqlConnectionString, _contractId, section, after, parsed, revision));
            if (!result.Saved)
            {
                _serverConflict = result.Revision != _serverRevision;
                error = result.Error ?? "SQL Server отклонил вставку.";
            }
            else
            {
                _serverRevision = result.Revision;
                _saveAwaitingReadback = true;
                saved = true;
                // Keep the anchor in view, not the top of a long section.
                _focusAfterReload = after is long id ? checked((int)id) : null;
                _focusSystemAfterReload = after is null ? section : null;
                if (!string.IsNullOrWhiteSpace(EstimateSearch.Text)) EstimateSearch.Clear();
            }
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            error = "Запись не подтверждена SQL Server. Не повторяй вставку до обновления.\n" + ex.Message;
        }
        finally
        {
            if (error is not null) _saveError = error;
            _writeLock.Release();
            Interlocked.Decrement(ref _pendingWrites);
            _reloadTimer.Stop();
            _reloadTimer.Start();
        }
        // Refresh the actual document immediately, including its stage snapshot.
        // Never replay an insert after an uncertain COMMIT.
        if (Volatile.Read(ref _pendingWrites) == 0)
        {
            _reloadTimer.Stop();
            await ReloadDocumentAsync();
        }
        if (error is not null)
            ShowExcelPasteError(error);
        else if (saved && !_saveAwaitingReadback && !_serverConflict &&
                 _publishedEstimateRevision == _serverRevision)
            SetStatus($"Вставлено из Excel: {parsed.Count} строк ✓", false);
    }

    private bool ConfirmExcelPaste(IReadOnlyList<PastedPosition> rows, string sectionName, string? source = null)
    {
        var window = CreateExcelPasteWindow("Импорт из Excel", out var body);
        var heading = new TextBlock
        {
            Text = "Вставить строки из Excel?",
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Foreground = ExcelPasteBrush("#0F172A")
        };
        heading.SetResourceReference(TextBlock.FontSizeProperty, "UiValueSize");
        body.Children.Add(heading);
        body.Children.Add(new TextBlock
        {
            Text = source is null ? "Из буфера обмена" : source,
            Foreground = (Brush)FindResource("UiMutedTextBrush"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 16)
        });
        var preview = new StackPanel();
        preview.Children.Add(new TextBlock
        {
            Text = $"Раздел: {sectionName}", FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Foreground = ExcelPasteBrush("#1E40AF")
        });
        preview.Children.Add(new TextBlock
        {
            Text = $"Новых позиций: {rows.Count}", Margin = new Thickness(0, 6, 0, 9),
            Foreground = ExcelPasteBrush("#334155")
        });
        foreach (var row in rows.Take(5))
            preview.Children.Add(new TextBlock
            {
                Text = "•  " + row.Name.Replace('\r', ' ').Replace('\n', ' '),
                TextWrapping = TextWrapping.Wrap, ToolTip = row.Name,
                Margin = new Thickness(0, 2, 0, 2), Foreground = ExcelPasteBrush("#334155")
            });
        if (rows.Count > 5)
            preview.Children.Add(new TextBlock
            {
                Text = $"…и ещё {rows.Count - 5}", Margin = new Thickness(0, 6, 0, 0),
                Foreground = (Brush)FindResource("UiMutedTextBrush")
            });
        body.Children.Add(new Border
        {
            Background = ExcelPasteBrush("#F8FAFC"), BorderBrush = ExcelPasteBrush("#E2E8F0"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14), Child = preview
        });
        body.Children.Add(new TextBlock
        {
            Text = "Колонки: название · ед. · количество · цена · закупка · примечание",
            Foreground = (Brush)FindResource("UiMutedTextBrush"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 13, 0, 8)
        });
        body.Children.Add(new TextBlock
        {
            Text = "Будут добавлены новые строки. Существующие позиции и объёмы этапов не заменяются.",
            Foreground = ExcelPasteBrush("#334155"), TextWrapping = TextWrapping.Wrap
        });
        AddExcelPasteButtons(window, body, $"Вставить ({rows.Count})", true);
        return window.ShowDialog() == true;
    }

    private void ShowExcelPasteError(string message)
    {
        SetStatus("Ошибка вставки: " + message, true);
        var window = CreateExcelPasteWindow("Ошибка вставки", out var body);
        var heading = WorkspaceWindowUi.Heading("Не удалось подтвердить вставку");
        heading.Foreground = ExcelPasteBrush("#991B1B");
        body.Children.Add(heading);
        body.Children.Add(new ScrollViewer
        {
            MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 12, 0, 0),
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
        });
        AddExcelPasteButtons(window, body, "Закрыть", false);
        window.ShowDialog();
    }

    private Window CreateExcelPasteWindow(string title, out StackPanel body)
    {
        var owner = Window.GetWindow(this);
        var window = new Window
        {
            Title = title, Width = 560, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Foreground = ExcelPasteBrush("#0F172A"), UseLayoutRounding = true
        };
        DynamicFormLayout.Apply(window);
        body = new StackPanel();
        var chrome = new Border
        {
            Margin = new Thickness(15), Padding = new Thickness(24),
            Background = Brushes.White, BorderBrush = ExcelPasteBrush("#E2E8F0"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(15, 23, 42), BlurRadius = 24, ShadowDepth = 7, Opacity = 0.2
            },
            Child = body
        };
        chrome.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ChangedButton == MouseButton.Left && args.ButtonState == MouseButtonState.Pressed)
                window.DragMove();
        };
        window.Content = chrome;
        return window;
    }

    private static void AddExcelPasteButtons(Window window, StackPanel body, string action, bool confirmation)
    {
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        if (confirmation)
        {
            var cancel = new Button
            {
                Content = "Отмена", IsCancel = true, MinWidth = 100,
                Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 10, 0)
            };
            footer.Children.Add(cancel);
        }
        var accept = new Button
        {
            Content = action, Style = (Style)window.FindResource("PrimaryButton"),
            MinWidth = 128, Padding = new Thickness(16, 8, 16, 8), IsDefault = true
        };
        accept.Click += (_, _) => window.DialogResult = true;
        footer.Children.Add(accept);
        var chrome = (Border)window.Content;
        chrome.Child = null;
        chrome.Child = DynamicFormLayout.WithFooter(body, footer, "LayoutNoInset");
    }

    private static SolidColorBrush ExcelPasteBrush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
