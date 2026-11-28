using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Част редактора «Импорт из файла»: строки КП из файла Excel — xlsx читается
/// ClosedXML, переводится в TSV и разбирается существующим PastedPositions.Parse
/// (та же валидация и распознавание колонок, что у вставки из буфера).
/// </summary>
public partial class EstimateView
{
    /// <summary>Импорт строк КП из файла Excel: xlsx → TSV → Parse → вставка в раздел.</summary>
    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate?.Locked != false) return;
        if (!EstimateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EstimateGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        var revision = _serverRevision;
        if (_estimate is not { } snapshot || !CanAppendImport(revision))
        {
            SetStatus("Заверши сохранение и обнови смету перед импортом.", true);
            return;
        }
        var selected = EstimateGrid.SelectedItem as EditableEstimateRow ??
            EstimateGrid.Items.OfType<EditableEstimateRow>().FirstOrDefault(r => r.IsSystem && IsRealCommandRow(r));
        if (selected is null || !IsRealCommandRow(selected) || EstimateGrid.SelectedItems.Count > 1)
        {
            SetStatus("Выбери один реальный раздел или позицию для импорта.", true);
            return;
        }
        var primary = EstimateGrid.SelectedItem;
        var selection = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().ToArray();
        bool TargetCurrent() => IsRealCommandRow(selected) && ReferenceEquals(primary, EstimateGrid.SelectedItem) &&
            EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().SequenceEqual(selection, ReferenceEqualityComparer.Instance);

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Загрузить строки КП из Excel",
            Filter = "Excel (*.xlsx;*.xlsm;*.xls)|*.xlsx;*.xlsm;*.xls",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        if (System.IO.Path.GetExtension(dialog.FileName).Equals(".xls", StringComparison.OrdinalIgnoreCase))
        {
            await ImportStructuredExcelFile(dialog.FileName, revision);
            return;
        }

        // Preserve the earlier simple single-section TSV import for users who
        // explicitly choose it; structural import is the default for real estimates.
        var mode = MessageBox.Show(Window.GetWindow(this),
            "Да — импортировать с сохранением разделов, комплектующих и отдельных цен.\n" +
            "Нет — прежняя вставка обычных строк в выбранный раздел (без структуры).\n" +
            "Отмена — ничего не импортировать.",
            "Способ импорта Excel", MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question, MessageBoxResult.Yes);
        if (mode == MessageBoxResult.Cancel) return;
        if (mode == MessageBoxResult.Yes)
        {
            await ImportStructuredExcelFile(dialog.FileName, revision);
            return;
        }

        SetStatus("Чтение файла…", false);
        IReadOnlyList<PastedPosition> parsed;
        try
        {
            parsed = await Task.Run(() =>
            {
                using var workbook = new ClosedXML.Excel.XLWorkbook(dialog.FileName);
                var sheet = workbook.Worksheets.First();
                var used = sheet.RangeUsed();
                if (used is null) return Array.Empty<PastedPosition>();
                var sb = new System.Text.StringBuilder();
                foreach (var rowIn in used.Rows())
                {
                    var cells = new List<string>();
                    foreach (var cell in rowIn.Cells(1, used.RangeAddress.LastAddress.ColumnNumber))
                    {
                        var text = cell.GetString()
                            .Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
                        cells.Add(text);
                    }
                    if (cells.All(string.IsNullOrWhiteSpace)) continue;
                    sb.AppendLine(string.Join("\t", cells));
                }
                return PastedPositions.Parse(sb.ToString());
            });
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка импорта: " + ex.Message, true);
            return;
        }
        if (parsed.Count == 0)
        {
            SetStatus("В файле не нашлось позиций.", true);
            return;
        }

        var sectionName = _rows.FirstOrDefault(r => r.IsSystem && r.Source.SystemId == selected.Source.SystemId)?.Name ?? "";
        var confirm = $"Вставить {parsed.Count} строк из «{System.IO.Path.GetFileName(dialog.FileName)}» в «{sectionName}»?\n" +
            "Существующие строки не заменяются.";
        if (MessageBox.Show(Window.GetWindow(this), confirm, "Импорт из Excel",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        if (!CanAppendImport(revision) || !ReferenceEquals(snapshot, _estimate) || !TargetCurrent())
        {
            SetStatus("Смета или выбранная строка изменились. Повтори импорт.", true);
            return;
        }
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var pending = Interlocked.Increment(ref _pendingWrites);
        SetStatus($"Импорт… ({pending})", false);
        await _writeLock.WaitAsync();
        try
        {
            if (!CommandWriteCurrent(snapshot, revision) || !TargetCurrent())
                throw new InvalidOperationException("Смета или выделение изменились до записи импорта.");
            var afterId = selected.IsSystem ? (long?)null : selected.Source.ParentPositionId ?? selected.Id;
            var copied = parsed.Select(r => new CopiedRow(r.Name, r.Unit, r.Qty, r.Price,
                r.PurchasePrice, r.Note, "", null)).ToList();
            var saved = await Task.Run(() => SqlServerDatabase.InsertCopiedRows(
                _sqlConnectionString, _contractId, selected.Source.SystemId, afterId, copied, revision));
            if (!saved.Saved)
            {
                _saveError = saved.Error ?? "Импорт не выполнен";
                _serverConflict = true;
            }
            else
            {
                _serverRevision = saved.Revision;
                _saveAwaitingReadback = true;
            }
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = "Импорт не подтверждён: " + ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? "Строки записаны, выполняется сверка…" : "Ошибка: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }
    private bool CanAppendImport(string revision) =>
        _estimate is { } snapshot && CommandWriteCurrent(snapshot, revision) &&
        !_refreshing && !HasUnsettledCacheWrites && _reloadCancellation is null &&
        Volatile.Read(ref _pendingWrites) == 0;
}
