using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>
/// Част редактора «Работа» (СМР «по поз.»): колонка «Работа за ед.» уже в гриде,
/// здесь — вставка столбца чисел из Excel в текущую колонку от текущей строки
/// вниз (kp_clipboard._paste_numbers).
/// </summary>
public partial class EstimateView
{
    /// <summary>Весь буфер — один столбец неотрицательных чисел (paste.parse numbers_only).</summary>
    internal static bool TryParseNumberColumn(string? text, out List<decimal> values)
    {
        values = new List<decimal>();
        if (string.IsNullOrWhiteSpace(text)) return false;
        // A blank cell inside a copied Excel column must not shift all later numbers.
        // Excel may append a trailing newline, which is harmless.
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var last = lines.Length - 1;
        while (last >= 0 && string.IsNullOrWhiteSpace(lines[last])) last--;
        for (var i = 0; i <= last; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) return false;
            if (!decimal.TryParse(line.Replace(',', '.'), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var value) || value < 0m ||
                value > GmcSmeta.Core.NumberText.MaxNumber)
                return false;
            values.Add(value);
        }
        return values.Count > 0;
    }

    /// <summary>
    /// Distinguish a numeric Excel column with internal blanks from a table of
    /// position descriptions, so Ctrl+V never imports it as new estimate rows.
    /// </summary>
    internal static bool NumericColumnHasGaps(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var last = lines.Length - 1;
        while (last >= 0 && string.IsNullOrWhiteSpace(lines[last])) last--;
        var hasBlank = false;
        var hasNumber = false;
        for (var i = 0; i <= last; i++)
        {
            var value = lines[i].Trim();
            if (value.Length == 0) { hasBlank = true; continue; }
            if (!decimal.TryParse(value.Replace(',', '.'), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out _))
                return false;
            hasNumber = true;
        }
        return hasBlank && hasNumber;
    }

    /// <summary>Pure selection plan, independent of SQL and UI events.</summary>
    internal static IReadOnlyList<EditableEstimateRow> PlanNumericPasteTargets(
        IReadOnlyList<EditableEstimateRow> visible, int startIndex, string field, int requested)
    {
        var targets = new List<EditableEstimateRow>();
        if (startIndex < 0 || startIndex >= visible.Count || requested <= 0 ||
            field is not ("qty" or "price" or "purchase_price" or "work_price"))
            return targets;
        for (var i = startIndex; i < visible.Count && targets.Count < requested; i++)
        {
            var row = visible[i];
            if (row.IsSystem || row.IsVirtual) continue;
            if (field != "qty" && row.Source.ParentPositionId is not null) continue;
            targets.Add(row);
        }
        return targets;
    }

    /// <summary>
    /// Excel numeric-column paste: ONE SQL transaction for all affected rows.
    /// Never claim a commit until the server replies; never retry an ambiguous COMMIT.
    /// </summary>
    private async void PasteNumbers(IReadOnlyList<decimal> values)
    {
        if (_estimate?.Locked != false)
        {
            SetStatus("Документ закрыт или подписан — вставка недоступна.", true);
            return;
        }
        var revision = _publishedEstimateRevision;
        if (_estimate is not { } snapshot || !CanAppendImport(revision))
        {
            SetStatus("Дождитесь завершения сохранения или обновите смету после ошибки SQL.", true);
            return;
        }
        var field = Field(EstimateGrid.CurrentColumn);
        if (field is not ("qty" or "price" or "purchase_price" or "work_price"))
        {
            SetStatus("Для числовой вставки выберите столбец количества, цены, закупки или работы.", true);
            return;
        }
        if (field == "work_price" && _estimate.SmrMode != "pos")
        {
            SetStatus("Цена работы доступна только при СМР «по позициям».", true);
            return;
        }
        if (EstimateGrid.CurrentItem is not EditableEstimateRow current ||
            current.IsSystem || current.IsVirtual || current.Id <= 0 || !_rows.Contains(current))
        {
            SetStatus("Выберите реальную строку сметы, с которой начинать вставку.", true);
            return;
        }

        if (values.Count > SqlServerDatabase.MaxAtomicNumericPasteRows)
        {
            SetStatus($"За раз можно вставить не более {SqlServerDatabase.MaxAtomicNumericPasteRows} чисел.", true);
            return;
        }
        var visible = (EstimateGrid.ItemsSource as IEnumerable<EditableEstimateRow>)?.ToList() ?? _rows;
        var targets = PlanNumericPasteTargets(visible, visible.IndexOf(current), field, values.Count);
        if (targets.Count != values.Count)
        {
            SetStatus($"В буфере {values.Count} чисел, а от выбранной строки доступно только " +
                $"{targets.Count} подходящих позиций. Ничего не записано: выберите другую строку " +
                "или скопируйте меньше чисел.", true);
            return;
        }
        var anchor = EstimateGrid.CurrentCell;
        bool TargetsCurrent()
        {
            if (!EstimateGrid.CurrentCell.Equals(anchor)) return false;
            var rows = (EstimateGrid.ItemsSource as IEnumerable<EditableEstimateRow>)?.ToList() ?? _rows;
            return PlanNumericPasteTargets(rows, rows.IndexOf(current), field, values.Count)
                .SequenceEqual(targets, ReferenceEqualityComparer.Instance);
        }
        var edits = targets.Select((row, index) =>
            new NumericPasteEdit(row.Id, values[index])).ToArray();
        if (SqlServerDatabase.ValidateNumericPaste(_contractId, field, edits) is { } invalid)
        {
            SetStatus(invalid, true);
            return;
        }
        if (targets.Count > 1)
        {
            var column = EstimateGrid.CurrentColumn.Header?.ToString() ?? field;
            var confirm = MessageBox.Show(Window.GetWindow(this),
                $"Атомарно вставить {targets.Count} чисел в столбец «{column}»?\n" +
                $"Первая строка: {targets[0].Name}\n" +
                $"Последняя строка: {targets[^1].Name}\n\n" +
                "SQL Server сохранит весь пакет одной транзакцией. " +
                "При отклонении транзакции данные останутся без изменений.",
                "ГМЦ-Смета — массовая вставка",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
        }
        if (!CanAppendImport(revision) || !ReferenceEquals(snapshot, _estimate) || !TargetsCurrent())
        {
            SetStatus("Смета или диапазон вставки изменились. Повтори вставку.", true);
            return;
        }

        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var count = Interlocked.Increment(ref _pendingWrites);
        InvalidateSparseReadback(); // структурная правка: только полный reload
        SetStatus($"Массовое сохранение: {edits.Length} чисел ({count} операций)…", false);
        var tookLock = false;
        try
        {
            await _writeLock.WaitAsync();
            tookLock = true;
            if (!CommandWriteCurrent(snapshot, revision) || !TargetsCurrent())
            {
                _saveError = "Данные или диапазон изменились во время ожидания. Обнови смету.";
                _serverConflict = true;
                return;
            }

            var result = await Task.Run(() => SqlServerDatabase.SaveNumericPasteAtomic(
                _sqlConnectionString, _contractId, field, edits, revision));
            if (!result.Saved)
            {
                _serverConflict = true;
                _saveAwaitingReadback = false;
                _saveError = result.Error ?? "Вся массовая вставка отклонена SQL Server.";
            }
            else
            {
                _serverRevision = result.Revision;
                _saveAwaitingReadback = true;
                SetStatus($"SQL Server подтвердил транзакцию: {result.Changed} изменённых " +
                    $"из {result.Requested} строк. Выполняется сверка итогов…", false);
            }
        }
        catch (Exception ex)
        {
            // The connection might drop AFTER SQL committed: reconciliation only.
            _serverConflict = true;
            _saveAwaitingReadback = false;
            _saveError = "Исход COMMIT неизвестен: " + ex.Message +
                ". Не повторяйте вставку до сверки с сервером.";
        }
        finally
        {
            if (tookLock) _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                if (_saveError is not null)
                    SetStatus("Массовая вставка не подтверждена: " + _saveError, true);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }

    /// <summary>СМР: сумма ↔ по поз. (_save_smr_mode эталона; процент/системы не перенесены).</summary>
    private async void SmrMode_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate?.Locked != false) return;
        if (_serverConflict) { SetStatus("Сначала обнови смету: данные на сервере изменились.", true); return; }
        if (_estimate is null || _estimate.SmrMode is not ("sum" or "pos")) return;
        var mode = _estimate.SmrMode == "pos" ? "sum" : "pos";
        var expected = _serverRevision;
        if (!CanAppendImport(expected)) { SetStatus("Заверши сохранение и обнови смету.", true); return; }

        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var pending = Interlocked.Increment(ref _pendingWrites);
        InvalidateSparseReadback(); // структурная правка: только полный reload
        SetStatus($"Сохранение… ({pending})", false);
        string? successText = null;
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict || _detached || _saveError is not null || expected != _serverRevision ||
                _estimate?.Locked != false)
                throw new DBConcurrencyException("Смета изменилась до переключения режима СМР.");
            var saved = await Task.Run(() => SqlServerSmrMode.SetSmrMode(
                _sqlConnectionString, _contractId, mode, expected));
            if (!saved.Saved)
            {
                _saveError = saved.Error ?? "Режим СМР не изменён";
                _serverConflict = true;
            }
            else
            {
                _serverRevision = saved.Revision;
                _saveAwaitingReadback = true;
                successText = mode == "pos"
                    ? "СМР «по позициям»: цены работ перенесены из сводной"
                    : "СМР «сумма»: работы сохранены как введённая сумма";
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
            _saveError = ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? successText ?? "Сохранено ✓" : "Ошибка записи: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Start();
            }
        }
    }
    /// <summary>Цены работ из справочника — только там, где не заполнены (fill_work_from_catalog).</summary>
    private async void FillWork_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate?.Locked != false) return;
        if (!CanAppendImport(_serverRevision)) { SetStatus("Заверши сохранение и обнови смету.", true); return; }
        var revision = _serverRevision;
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var pending = Interlocked.Increment(ref _pendingWrites);
        InvalidateSparseReadback(); // структурная правка: только полный reload
        SetStatus($"Заполнение… ({pending})", false);
        string? successText = null;
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict || _detached || revision != _serverRevision || _estimate?.Locked != false)
                throw new InvalidOperationException("Смета изменилась до заполнения цен.");
            var result = await Task.Run(() => SqlServerCatalog.FillWorkFromCatalogWithRevision(
                _sqlConnectionString, _contractId, onlyEmpty: true, revision));
            _serverRevision = result.Revision;
            _saveAwaitingReadback = true;
            successText = $"Цены работ: изменено {result.Changed}, обработано {result.Processed}. Выполняется сверка…";
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
            _saveError = "Заполнение цен не подтверждено: " + ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? successText ?? "Готово ✓" : "Ошибка записи: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Start();
            }
        }
    }

}
