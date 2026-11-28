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

/// <summary>Правка ячеек таблицы: разрешение колонок, постановка сохранений в очередь, цена работы.</summary>
public partial class EstimateView
{
    private void EstimateGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not EditableEstimateRow row) { e.Cancel = true; return; }
        if (row.IsPendingInsert)
        {
            e.Cancel = !CanEditPendingInsert(row, Field(e.Column));
            return;
        }
        if (_inlineStageColumns.Contains(e.Column))
        {
            e.Cancel = !CanEditInlineStage(row, e.Column, out _);
            return;
        }
        var field = Field(e.Column);
        if (field is "basis" or "area_manual")
        {
            if (row.IsVirtual || row.IsSystem || _estimate?.Locked == true ||
                _serverConflict || System.Threading.Volatile.Read(ref _pendingWrites) > 0 ||
                field == "basis" && _estimate?.BasisAllowed != true ||
                field == "area_manual" && row.IsAreaSummary)
            {
                e.Cancel = true;
                return;
            }
            if (!new SqlConnectionStringBuilder(_sqlConnectionString).InitialCatalog
                    .StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Новые поля м²/Обоснование пока редактируются только в GmcSmetaPilot_*.", true);
                e.Cancel = true;
            }
            return;
        }
        if (row.IsGroup)
        {
            // Python _save_group: name, unit, price, purchase and work only.
            e.Cancel = field is not ("name" or "unit" or "price" or "purchase_price" or "work_price") ||
                field == "work_price" && _estimate?.SmrMode != "pos" ||
                _estimate?.Locked == true && field != "purchase_price" ||
                _serverConflict;
            return;
        }
        e.Cancel = _serverConflict || row.IsVirtual || row.IsSystem || field is null ||
            row.IsSub && field is "price" or "work_price" ||
            field == "work_price" && _estimate?.SmrMode != "pos" ||
            _estimate?.Locked == true && field != "purchase_price";
    }

    private void EstimateGrid_PreparingCellForEdit(object sender,
        DataGridPreparingCellForEditEventArgs e)
    {
        // Even a cancelled edit can leave transient TwoWay binding values.
        // Require a full reconciliation before taking the tab cache shortcut.
        _publishedEstimateRevision = "";
        // Every editable cell, including the Name/Price template columns,
        // must focus the actual TextBox on the first edit activation.
        GridEditInteraction.FocusTextEditor(EstimateGrid, e);
        BeginEstimateFormulaEdit(e);
    }

    private void EstimateGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit)
        {
            EndEstimateFormulaEdit();
            return;
        }
        if (e.Row.Item is not EditableEstimateRow row || row.IsSystem) return;
        if (!ResolveEstimateFormulaEdit(e)) { e.Cancel = true; return; }
        if (_inlineStageColumns.Contains(e.Column))
        {
            if (FindTextBox(e.EditingElement) is { } stageEditor)
                SaveInlineStageCell(row, e.Column, stageEditor.Text);
            return;
        }
        var field = Field(e.Column);
        var editor = FindTextBox(e.EditingElement);
        if (field is null || editor is null) return;
        if (row.IsTypeHeader) return;

        // CellEditEnding fires BEFORE WPF writes its binding. Capture the editor's exact text,
        // including an unfinished formula, rather than reading the previous model value.
        var input = editor.Text;
        if (row.IsPendingInsert)
        {
            if (!CommitPendingInsertCell(row, field, input)) e.Cancel = true;
            return;
        }
        if (field == "area_manual" &&
            !AreaCellPresentation.TryParseManual(input, out _))
        {
            e.Cancel = true;
            SetStatus("Недопустимая площадь. Введи положительное число, формулу или очисти поле.", true);
            return;
        }
        // Opening and closing a mixed-price group is not a request to
        // overwrite all member prices with the display word «разные».
        if (row.IsGroup && input == "разные" && field is
            ("price" or "purchase_price" or "work_price")) return;
        if (row.IsGroup) QueueGroupSave(row, field, input);
        else QueueSave(row.Id, field, input);
    }

    private static string? Field(DataGridColumn column) => column.Header?.ToString() switch
    {
        "Наименование" => "name",
        "Ед." => "unit",
        "Кол-во" => "qty",
        "Цена" => "price",
        "Закупка" => "purchase_price",
        "Работа за ед." => "work_price",
        "Примечание" => "note",
        "м² за ед." => "area_manual",
        "Обоснование" or "Код работ" => "basis",
        _ => null
    };

    /// <summary>Одна цена работы для всех выделенных верхних строк (set_work_selected эталона).</summary>
    private async void SetWork_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate?.Locked == true) return;
        if (_serverConflict)
        {
            SetStatus("Сначала обнови смету: данные на сервере изменились.", true);
            return;
        }
        if (_estimate is null || _estimate.SmrMode != "pos")
        {
            SetStatus("Сначала включите СМР «по поз.» в итогах внизу", true);
            return;
        }
        var ids = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>()
            .Where(r => !r.IsSystem && !r.IsVirtual && r.Source.ParentPositionId is null)
            .Select(r => (long)r.Id).Distinct().ToList();
        if (ids.Count == 0)
        {
            SetStatus("Выделите строки, которым поставить цену работы", true);
            return;
        }
        var dialog = new PromptDialog("Цена работы",
            $"Работа за 1 ед. — для {ids.Count} строк:", "") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Value.Trim().Length == 0) return;
        if (NumberText.Parse(dialog.Value.Trim()) is not { } value)
        {
            SetStatus("Недопустимое число: " + dialog.Value, true);
            return;
        }

        _reloadTimer.Stop();
        var count = Interlocked.Increment(ref _pendingWrites);
        SetStatus($"Сохранение… ({count})", false);
        await _writeLock.WaitAsync();
        try
        {
            var saved = await Task.Run(() => SqlServerDatabase.SetWorkPrice(
                _sqlConnectionString, _contractId, ids, value, _serverRevision));
            _serverRevision = saved.Revision;
            SetStatus($"Цена работы поставлена: {saved.Updated} поз.", false);
        }
        catch (DBConcurrencyException ex)
        {
            _serverConflict = true;
            _saveError = ex.Message;
        }
        catch (Exception ex)
        {
            _saveError = ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? "Сохранено ✓" : "Ошибка записи: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Start();          // СМР/итоги пересчитываются на сервере
            }
        }
    }

    /// <summary>Себестоимость — сводная по строкам документа (cost_summary_view, read-only v1).</summary>
    private void Summary_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate is null) return;
        var window = new CostSummaryWindow(_sqlConnectionString, _contractId, _estimate.Title)
        {
            Owner = Window.GetWindow(this),
        };
        window.Show();
    }


    /// <summary>Скидка на всю смету (apply_discount): процент, СМР вместе, целые рубли.</summary>
    private async void Discount_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate is not {Locked:false} estimate) return;
        if (_serverConflict || HasUnsettledCacheWrites)
        { SetStatus("Сначала завершите сохранение и обновите смету.",true); return; }
        var totals=estimate.Totals;
        var baselineRevision=_serverRevision;
        var dialog=new EstimateDiscountDialog(new(
            totals.Total+totals.DiscountAmount,totals.Total,totals.Materials,totals.Smr,
            estimate.SmrMode=="percent",totals.DiscountPercent,
            SqlSchemaCompatibility.Schema31WritesAllowed(_sqlConnectionString)&&estimate.SupportsFinalDiscount&&!_doc.IsStage))
            {Owner=Window.GetWindow(this)};
        if(dialog.ShowDialog()!=true||dialog.Result is not {Changed:true} result)return;
        if(_serverRevision!=baselineRevision||_serverConflict||HasUnsettledCacheWrites)
        { SetStatus("Смета изменилась. Откройте окно скидки заново.",true);return; }
        _reloadTimer.Stop();
        var pending = Interlocked.Increment(ref _pendingWrites);
        SetStatus($"Сохранение… ({pending})", false);
        await _writeLock.WaitAsync();
        try
        {
            var saved=await Task.Run(()=>result.OnTotal
                ? SqlServerActs.SaveContractDiscount(_sqlConnectionString,_contractId,result.Percent,baselineRevision)
                : SqlServerEditorOps.ApplyDiscount(_sqlConnectionString,_contractId,result.Percent,result.Smr,
                    result.WholeRubles,baselineRevision));
            if (!saved.Saved) { _saveError = saved.Error ?? "Скидка не применена"; _serverConflict = true; }
            else
            {
                _serverRevision = saved.Revision;
                SetStatus($"Скидка применена", false);
            }
        }
        catch (Exception ex) { _saveError = ex.Message; }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? "Готово ✓" : "Ошибка: " + _saveError, _saveError is not null);
                _reloadTimer.Start();
            }
        }
    }

    /// <summary>Сортировка строк выбранной системы с сохранением комплектующих.</summary>
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate?.Locked != false) return;
        if (_serverConflict)
        {
            SetStatus("Сначала обнови смету: данные изменились.", true);
            return;
        }
        if (EstimateGrid.SelectedItem is not EditableEstimateRow row || row.IsVirtual)
        {
            SetStatus("Выберите реальный раздел или его позицию.", true);
            return;
        }
        var systemId = row.Source.SystemId;
        var menu = new ContextMenu();
        MenuItem Add(string header, string how)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await RunEstimateMutationAsync(
                "Сортировка…", revision => SqlServerEditorOps.SortPositions(
                    _sqlConnectionString, _contractId, systemId, how, revision),
                "Сортировка выполнена ✓");
            return item;
        }
        menu.Items.Add(Add("По названию (А→Я)", "name"));
        menu.Items.Add(Add("По сумме (дорогие вперёд)", "sum"));
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>Выделенные верхние позиции → новые разделы.</summary>
    private async void ToSections_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate?.Locked != false) return;
        if (_serverConflict)
        {
            SetStatus("Сначала обнови смету: данные изменились.", true);
            return;
        }
        if (!EstimateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        var revision = _serverRevision;
        if (!CanAppendImport(revision) || HasUnsettledCacheWrites) return;
        var rows = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().ToArray();
        if (rows.Length == 0 || rows.Any(r => r.IsSystem || r.IsVirtual || r.IsAreaSummary ||
            r.Id <= 0 || r.Source.ParentPositionId is not null || r.Source.OutOfKp || !_rows.Contains(r)) ||
            rows.Select(r => r.Source.SystemId).Distinct().Count() != 1)
        {
            SetStatus("Выделите верхние строки сметы.", true);
            return;
        }
        var selected = rows.Select(r => (long)r.Id).Distinct().ToArray();
        var systemId = rows[0].Source.SystemId;
        await RunEstimateMutationAsync("Преобразование в разделы…",
            currentRevision => currentRevision != revision
                ? SqlServerDatabase.SaveResult.Fail("Смета изменилась. Повторите преобразование.", currentRevision)
                : SqlServerEditorOps.PositionsToSections(
                    _sqlConnectionString, _contractId, systemId, selected, revision),
            $"Разделов создано: {selected.Length} ✓");
    }

    // Resolve by the owning system ID, never by row order or the displayed name.
    private EditableEstimateRow? CopySourceSystem(EditableEstimateRow? selected)
    {
        if (selected is null || selected.IsVirtual || !_rows.Contains(selected)) return null;
        var id = SystemCopySource.Resolve(selected.Source, _rows
            .Where(r => r.IsSystem && !r.IsVirtual).Select(r => r.Source));
        return id is int systemId
            ? _rows.FirstOrDefault(r => r.IsSystem && !r.IsVirtual && r.Source.SystemId == systemId)
            : null;
    }

    private bool CanStartPriceLike() => PriceLikeBatchPlan.CanStart(
        _estimate?.Locked, _serverConflict, HasUnsettledCacheWrites, _detached, _serverRevision);

    private async Task SavePriceLikeAsync(long sourceId, string field,
        PriceLikeBatchPlan.Plan expectedPlan, Func<bool> selectionCurrent)
    {
        // Preserve the exact revision and source the user saw, not a later queue revision.
        if (!CanStartPriceLike() || sourceId <= 0 || field is not ("price" or "purchase_price") ||
            !selectionCurrent() || HasActiveEstimateEdit() || !PriceLikeBatchPlan.PositivePrice(expectedPlan.Price)) return;
        var revision = _serverRevision;
        var snapshot = _estimate;
        if (snapshot is null || !snapshot.PositionsInOrder.Any(row => row.Id == sourceId && !row.IsSystem)) return;
        var connection = _sqlConnectionString;
        var contractId = _contractId;
        // One operation on the same queue as ordinary cell writes: no stale
        // revision and no interleaving of a bulk edit with single-cell edits.
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var pending = Interlocked.Increment(ref _pendingWrites);
        SetStatus($"Перенос цены… ({pending} операций)", false);
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict || _detached || _estimate?.Locked != false ||
                revision != _serverRevision || !ReferenceEquals(snapshot, _estimate) ||
                _cellSaveQueue.Blocked || _saveAwaitingReadback || _saveError is not null ||
                connection != _sqlConnectionString || contractId != _contractId ||
                !selectionCurrent() || HasActiveEstimateEdit())
            {
                _saveError ??= "Смета или выбор изменились во время ожидания. Повторите перенос цены после обновления.";
                return;
            }
            var result = await Task.Run(() => SqlServerPriceLike.SetPriceLike(
                connection, contractId, sourceId, field, revision, expectedPlan));
            if (!result.Saved)
            {
                _saveError = result.Error ?? "Не удалось перенести цену.";
                if (result.Revision != revision) _serverConflict = true;
                return;
            }
            _serverRevision = result.Revision;
            _saveAwaitingReadback = true;
            SetStatus($"Перенесена цена у {result.Changed} одинаковых позиций ✓", false);
        }
        catch (Exception ex)
        {
            // Connection may have failed after commit acknowledgement was lost;
            // require authoritative reload before attempting another batch.
            _serverConflict = true;
            _saveError = ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? "Цены сохранены ✓" :
                    "Ошибка переноса: " + _saveError, _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }

    private static TextBox? FindTextBox(DependencyObject? root)
    {
        if (root is TextBox box) return box;
        if (root is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var result = FindTextBox(VisualTreeHelper.GetChild(root, i));
            if (result is not null) return result;
        }
        return null;
    }

}
