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

/// <summary>Внутренний буфер строк: копировать/вставить/дублировать/заменить из Excel.</summary>
public partial class EstimateView
{
    // ---------------------------------------------------------------- внутренний буфер строк

    // Общий на всё приложение буфер скопированных строк (kp_clipboard._ROWS_CLIP).
    private static readonly List<CopiedRow> ClipboardRows = new();
    private static bool ClipboardIsOurs;
    private static string? ClipboardTextAtCopy;

    /// <summary>Выделенные строки в порядке экрана: верхняя приносит комплектующие,
    /// отдельно выделенная комплектующая берётся сама, выделенный раздел — все его строки.</summary>
    private List<CopiedRow> BuildClipboardFromSelection()
    {
        var selected = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().ToHashSet();
        if (selected.Any(row => row.IsVirtual)) return new List<CopiedRow>();
        var result = new List<CopiedRow>();
        var emittedTops = new HashSet<int>();
        foreach (var r in _rows)
        {
            if (r.IsSystem)
            {
                if (selected.Contains(r))
                    foreach (var top in _rows.Where(x => !x.IsSystem &&
                        x.Source.SystemId == r.Source.SystemId && x.Source.ParentPositionId is null))
                        if (emittedTops.Add(top.Id))
                            result.Add(RowToCopied(top));
                continue;
            }
            if (r.Source.ParentPositionId is int parent)
            {
                if (emittedTops.Contains(parent)) continue;      // ушла вместе со своей строкой
                if (selected.Contains(r))
                    result.Add(new(r.Source.Name, r.Source.Unit, r.Source.Qty, 0m, null, r.Source.Note, r.Source.Formulas, null));
                continue;
            }
            if (selected.Contains(r) && emittedTops.Add(r.Id))
                result.Add(RowToCopied(r));
        }
        return result;
    }

    private CopiedRow RowToCopied(EditableEstimateRow top)
    {
        var children = new List<CopiedChild>();
        var index = _rows.IndexOf(top);
        for (var i = index + 1; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (r.IsSystem || r.Source.ParentPositionId is null || r.Source.ParentPositionId != top.Id) break;
            children.Add(new(r.Source.Name, r.Source.Unit, r.Source.Qty, r.Source.Note));
        }
        var src = top.Source;
        return new CopiedRow(src.Name, src.Unit, src.Qty, src.Price, src.PurchasePrice, src.Note, src.Formulas, children)
        {
            WorkPrice = src.WorkPrice
        };
    }

    /// <summary>Ctrl+C: запомнить строки и положить таблицу в буфер Windows для Excel.</summary>
    private void CopySelectedRows()
    {
        if (_estimate?.Locked == true) return;
        var picked = BuildClipboardFromSelection();
        if (picked.Count == 0)
        {
            SetStatus("Выделите строки для копирования", true);
            return;
        }
        ClipboardRows.Clear();
        ClipboardRows.AddRange(picked);
        ClipboardIsOurs = true;
        var lines = new List<string>();
        foreach (var d in picked)
        {
            lines.Add(string.Join("\t", d.Name, d.Unit, N2(d.Qty), N2(d.Price),
                N2(decimal.Round(d.Qty * d.Price, 2))));
            foreach (var ch in d.Children ?? Array.Empty<CopiedChild>())
                lines.Add(string.Join("\t", "   " + ch.Name, ch.Unit, N2(ch.Qty), "", ""));
        }
        var exported = string.Join("\n", lines);
        try
        {
            Clipboard.SetText(exported);
            ClipboardTextAtCopy = exported;
        }
        catch
        {
            // An inaccessible OS clipboard must not be mistaken for a fresh
            // internal copy of an unrelated workbook.
            ClipboardTextAtCopy = null;
        }
        SetStatus($"Скопировано строк: {picked.Count}. Вставить — выделите строку (можно в другом КП) и Ctrl+V", false);
    }

    private void CopyRows_Click(object sender, RoutedEventArgs e) => CopySelectedRows();

    /// <summary>Ctrl+V: чужой текст — это Excel (PasteExcel), свой буфер — вставка строк под текущей.</summary>
    private async void PasteRows_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate?.Locked != false) return;
        if (!EstimateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        var revision = _serverRevision;
        if (!CanAppendImport(revision))
        {
            SetStatus("Заверши сохранение и обнови смету перед операцией с буфером.", true);
            return;
        }
        // The user can copy from Excel after an internal copy. Compare the
        // actual Windows clipboard, not just our stale in-process flag.
        string? systemText = null;
        try { if (Clipboard.ContainsText()) systemText = Clipboard.GetText(); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            SetStatus("Буфер Windows временно занят. Повтори вставку.", true);
            return;
        }
        var isInternal = EstimateClipboardPolicy.IsOwned(
            ClipboardIsOurs, ClipboardTextAtCopy, systemText);
        if (!isInternal) ClipboardIsOurs = false;
        if (systemText is not null && !isInternal)
        {
            var text = systemText;
            if (TryParseNumberColumn(text, out var values))
            {
                PasteNumbers(values);                // столбец чисел — в текущую колонку вниз
                return;
            }
            if (NumericColumnHasGaps(text))
            {
                SetStatus("В скопированном числовом столбце есть пустые ячейки. " +
                    "Вставка отменена: заполните пропуски и повторите Ctrl+V.", true);
                return;
            }
            PasteExcel_Click(sender, e);             // скопировано в Excel/Word — путь импорта
            return;
        }
        if (!isInternal || ClipboardRows.Count == 0)
        {
            SetStatus("Буфер строк устарел или пуст. Выдели строки и нажми Ctrl+C.", true);
            return;
        }
        var target = EstimateGrid.SelectedItem as EditableEstimateRow;
        if (target is null)
        {
            SetStatus("Выделите строку, под которой вставить", true);
            return;
        }
        if (target.IsVirtual)
        {
            SetStatus("Раскрой группу и выбери реальную строку для вставки.", true);
            return;
        }
        var sectionId = target.Source.SystemId;
        long? afterId = target.IsSystem ? null : target.Source.ParentPositionId ?? target.Id;
        var copied = ClipboardRows.ToArray();
        var count = copied.Length;

        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var pending = Interlocked.Increment(ref _pendingWrites);
        SetStatus($"Вставка… ({pending})", false);
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict || _detached || revision != _serverRevision || _estimate?.Locked != false)
                throw new InvalidOperationException("Смета изменилась до записи.");
            var result = await Task.Run(() => SqlServerDatabase.InsertCopiedRows(
                _sqlConnectionString, _contractId, sectionId, afterId, copied, revision));
            if (!result.Saved)
            {
                _saveError = result.Error ?? "Не удалось вставить строки";
                _serverConflict = true;
            }
            else
            {
                _serverRevision = result.Revision;
                _saveAwaitingReadback = true;
                SetStatus($"Вставлено строк: {count}. Вернуть — Ctrl+Z", false);
            }
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = "Запись не подтверждена: " + ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? "Записано в SQL Server, выполняется сверка…" : "Ошибка записи: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }

    /// <summary>Ctrl+D: копии выделенных строк сразу под последней из них (kp_clipboard.duplicate).</summary>
    private async void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate?.Locked != false) return;
        if (!EstimateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        var revision = _serverRevision;
        if (!CanAppendImport(revision))
        {
            SetStatus("Заверши сохранение и обнови смету перед операцией с буфером.", true);
            return;
        }
        var picked = BuildClipboardFromSelection();
        var last = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().LastOrDefault(r => !r.IsSystem && !r.IsVirtual);
        if (picked.Count == 0 || last is null)
        {
            SetStatus("Выделите строки для дублирования", true);
            return;
        }
        var sectionId = last.Source.SystemId;
        var afterId = last.Source.ParentPositionId ?? last.Id;

        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var pending = Interlocked.Increment(ref _pendingWrites);
        SetStatus($"Копирование… ({pending})", false);
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict || _detached || revision != _serverRevision || _estimate?.Locked != false)
                throw new InvalidOperationException("Смета изменилась до записи.");
            var result = await Task.Run(() => SqlServerDatabase.InsertCopiedRows(
                _sqlConnectionString, _contractId, sectionId, afterId, picked, revision));
            if (!result.Saved)
            {
                _saveError = result.Error ?? "Не удалось продублировать строки";
                _serverConflict = true;
            }
            else
            {
                _serverRevision = result.Revision;
                _saveAwaitingReadback = true;
                SetStatus($"Скопировано строк: {picked.Count}. Вернуть — Ctrl+Z", false);
            }
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = "Запись не подтверждена: " + ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? "Записано в SQL Server, выполняется сверка…" : "Ошибка записи: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }

    /// <summary>Ctrl+Shift+V: строки счёта из буфера становятся на место выделенных (kp_clipboard.replace_rows).</summary>
    private async void ReplaceRows_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate?.Locked != false) return;
        if (!EstimateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        var revision = _serverRevision;
        if (!CanAppendImport(revision))
        {
            SetStatus("Заверши сохранение и обнови смету перед операцией с буфером.", true);
            return;
        }
        if (!Clipboard.ContainsText())
        {
            SetStatus("Сначала скопируйте в Excel строки (например, из счёта), потом выделите строки КП " +
                "и нажмите Ctrl+Shift+V", true);
            return;
        }
        IReadOnlyList<PastedPosition> parsed;
        try { parsed = PastedPositions.Parse(Clipboard.GetText()); }
        catch (Exception ex) { SetStatus("Ошибка разбора буфера: " + ex.Message, true); return; }
        var newRows = parsed.Where(r => !string.IsNullOrWhiteSpace(r.Name)).ToList();
        if (newRows.Count == 0)
        {
            SetStatus("В скопированном нет строк с наименованиями", true);
            return;
        }
        var selected = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().Where(r => !r.IsSystem && !r.IsVirtual).ToList();
        var pickedTops = selected.Where(r => r.Source.ParentPositionId is null && !r.Source.OutOfKp).ToList();
        var extraNames = selected.Where(r => r.Source.OutOfKp).Select(r => r.Source.Name).ToList();
        if (pickedTops.Count == 0)
        {
            SetStatus(extraNames.Count > 0
                ? "Выбраны позиции вне сметы. Проверьте их в КС-2 или «Закупках»."
                : "Выделите строки КП, которые заменить", true);
            return;
        }
        var oldRows = pickedTops.Select(r => new ReplaceDialog.ReplaceRowData(
            r.Source.Name, r.Source.Unit, r.Source.Qty, r.Source.Price, r.Source.PurchasePrice,
            r.Source.Price != 0m)).ToList();
        var bufferRows = newRows.Select(r => new ReplaceDialog.ReplaceRowData(
            r.Name, r.Unit, r.Qty, r.Price, r.PurchasePrice, r.Price != 0m)).ToList();
        var dialog = new ReplaceDialog(oldRows, bufferRows, extraNames) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        var result = dialog.ComputeResult();
        if (!CanAppendImport(revision) || pickedTops.Any(r => !_rows.Contains(r)) ||
            !EstimateGrid.SelectedItems.OfType<EditableEstimateRow>()
                .Where(r => !r.IsSystem && !r.IsVirtual && r.Source.ParentPositionId is null && !r.Source.OutOfKp)
                .Select(r => r.Id).SequenceEqual(pickedTops.Select(r => r.Id)))
        {
            SetStatus("Смета или выделение изменились. Повтори замену.", true);
            return;
        }

        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var pending = Interlocked.Increment(ref _pendingWrites);
        SetStatus($"Замена… ({pending})", false);
        string? successText = null;
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict || _detached || revision != _serverRevision || _estimate?.Locked != false)
                throw new InvalidOperationException("Смета изменилась до записи.");
            if (dialog.SameCount)
            {
                var updates = new List<SqlServerDatabase.ReplaceUpdate>();
                for (var i = 0; i < pickedTops.Count && i < result.Count; i++)
                    updates.Add(new SqlServerDatabase.ReplaceUpdate(pickedTops[i].Id,
                        result[i].Name, result[i].Unit, result[i].Qty, result[i].Price, result[i].PurchasePrice));
                var saved = await Task.Run(() => SqlServerDatabase.ReplaceRows(
                    _sqlConnectionString, _contractId, updates,
                    Array.Empty<long>(), Array.Empty<CopiedRow>(), revision));
                if (!saved.Saved) { _saveError = saved.Error ?? "Замена не выполнена"; _serverConflict = true; }
                else
                {
                    _serverRevision = saved.Revision;
                    _saveAwaitingReadback = true;
                    successText = $"Заменено строк: {updates.Count}. Вернуть — Ctrl+Z";
                }
            }
            else
            {
                var insertRows = result.Select(r => new CopiedRow(
                    r.Name ?? "", r.Unit ?? "шт", r.Qty ?? 0m, r.Price ?? 0m, r.PurchasePrice, "",
                    "", null)).ToList();
                var ids = pickedTops.Select(r => (long)r.Id).ToList();
                var saved = await Task.Run(() => SqlServerDatabase.ReplaceRows(
                    _sqlConnectionString, _contractId, Array.Empty<SqlServerDatabase.ReplaceUpdate>(),
                    ids, insertRows, revision));
                if (!saved.Saved) { _saveError = saved.Error ?? "Замена не выполнена"; _serverConflict = true; }
                else
                {
                    _serverRevision = saved.Revision;
                    _saveAwaitingReadback = true;
                    successText = $"Заменено строк: {ids.Count} → {insertRows.Count}. Вернуть — Ctrl+Z";
                }
            }
        }
        catch (DBConcurrencyException ex)
        {
            _serverConflict = true;
            _saveError = ex.Message;
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = "Запись не подтверждена: " + ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? successText ?? "Записано в SQL Server, выполняется сверка…" : "Ошибка записи: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }
}
