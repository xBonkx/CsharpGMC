using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App;

/// <summary>WPF port of Python smr_view: works per position + separate non-position expenses.</summary>
public partial class CostSummaryPane
{
    public sealed class SmrExpensePreviewRow
    {
        public long Id { get; init; }
        public string Date { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Who { get; set; } = "";
        public string Amount { get; set; } = "";
        public string Note { get; set; } = "";
    }

    private void ApplySmrTab(List<SmrExpensePreviewRow> expenses, string mode)
    {
        SmrWorkGrid.ItemsSource = new ObservableCollection<CostSummaryPreviewRow>(
            _allRows.Where(r => r.Id > 0));
        SmrExpenseGrid.ItemsSource = new ObservableCollection<SmrExpensePreviewRow>(expenses);
        SmrModeCaption.Text = "Режим СМР: " + mode;
        foreach (var item in SmrModePicker.Items.OfType<ComboBoxItem>())
            if ((string?)item.Tag == mode) SmrModePicker.SelectedItem = item;
    }

    private IReadOnlyList<long> SelectedWorkIds(bool allWhenEmpty = false)
    {
        var ids = SmrWorkGrid.SelectedItems.OfType<CostSummaryPreviewRow>()
            .Where(x => x.Id > 0).Select(x => x.Id).Distinct().ToArray();
        if (ids.Length == 0 && allWhenEmpty)
            return _summaryRows.Select(r => r.Id).ToArray();
        return ids;
    }

    private async Task ApplyWorkPercent(bool onlyEmpty)
    {
        using var operation = BeginCostOperation();
        if (operation is null) return;
        var ids = SelectedWorkIds(onlyEmpty);
        if (onlyEmpty)
        {
            var empty = _summaryRows.Where(r => r.UWsell is null)
                .Select(r => r.Id).ToHashSet();
            ids = ids.Where(empty.Contains).ToArray();
        }
        if (ids.Count == 0)
        {
            SetStatus(onlyEmpty
                ? "Нет строк СМР с пустой розницей."
                : "Выделите позиции СМР для наценки.");
            return;
        }
        var dialog = new PromptDialog("Наценка на СМР",
            $"Выбрано {ids.Count} строк. Розница = закуп × (1 + наценка / 100).\nНаценка, %",
            "50") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        if (!decimal.TryParse(dialog.Value.Trim().Replace(',', '.'),
                NumberStyles.Number, CultureInfo.InvariantCulture, out var percent) ||
            percent < -100m || percent > 10000m)
        {
            SetStatus("Наценка СМР должна быть от −100 до 10000 %.");
            return;
        }
        try
        {
            var result = await Task.Run(() => SqlServerCostSummary.ApplySmrMarkup(
                _connectionString, _contractId, ids, percent, ServerRevision()));
            _revision = "";
            await ReloadAsync();
            SetStatus($"Наценка СМР: изменено {result.Changed}, без закупа пропущено {result.Skipped}.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка наценки СМР: " + ex.Message);
            await ReloadAsync();
        }
    }

    private async void WorkMarkup_Click(object sender, RoutedEventArgs e) =>
        await ApplyWorkPercent(false);

    private async void WorkFillEmpty_Click(object sender, RoutedEventArgs e) =>
        await ApplyWorkPercent(true);

    private async void SmrWorkGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Delete or Key.Back) || Keyboard.FocusedElement is TextBox) return;
        // Python clears the selected editable work-price column only.
        var field = SmrWorkGrid.CurrentCell.Column?.Header?.ToString() switch
        {
            "Работы: закуп за ед." => "work_cost",
            "Работы: розн. за ед." => "work_sell",
            _ => null,
        };
        if (field is null) return;
        e.Handled = true;
        await ClearWork(field); // asks for confirmation and enforces revision/SQL guard
    }

    private void SmrExpenseGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Keyboard.FocusedElement is TextBox) return;
        e.Handled = true;
        ExpenseDelete_Click(sender, e); // same confirmed delete path as toolbar
    }

    private async Task ClearWork(string field)
    {
        using var operation = BeginCostOperation();
        if (operation is null) return;
        var ids = SelectedWorkIds();
        if (ids.Count == 0)
        {
            SetStatus("Выделите позиции, у которых очистить цену СМР.");
            return;
        }
        if (MessageBox.Show(Window.GetWindow(this),
            $"Очистить {field} у {ids.Count} позиций?",
            "Очистка СМР", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            var result = await Task.Run(() => SqlServerSmrExpenses.SetWorkUnitPrices(
                _connectionString, _contractId, ids, field, null, false, ServerRevision()));
            _revision = result.Revision;
            await ReloadAsync();
            SetStatus($"Цены СМР очищены у {result.Changed} позиций.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка очистки СМР: " + ex.Message);
            await ReloadAsync();
        }
    }

    private async void WorkClearCost_Click(object sender, RoutedEventArgs e) =>
        await ClearWork("work_cost");

    private async void WorkClearSell_Click(object sender, RoutedEventArgs e) =>
        await ClearWork("work_sell");

    private async void ExpenseAdd_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginCostOperation();
        if (operation is null) return;
        try
        {
            var added = await Task.Run(() => SqlServerSmrExpenses.Add(
                _connectionString, _contractId, ServerRevision()));
            _revision = added.Revision;
            await ReloadAsync();
            var entry = SmrExpenseGrid.Items.OfType<SmrExpensePreviewRow>()
                .FirstOrDefault(x => x.Id == added.Id);
            if (entry is not null)
            {
                SmrExpenseGrid.SelectedItem = entry;
                SmrExpenseGrid.ScrollIntoView(entry);
            }
            SetStatus("Затрата СМР добавлена — заполните сумму, статью и исполнителя.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка добавления затрат: " + ex.Message);
        }
    }

    private async void ExpenseDelete_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginCostOperation();
        if (operation is null) return;
        var ids = SmrExpenseGrid.SelectedItems.OfType<SmrExpensePreviewRow>()
            .Select(x => x.Id).Distinct().ToArray();
        if (ids.Length == 0) { SetStatus("Выделите затраты для удаления."); return; }
        if (MessageBox.Show(Window.GetWindow(this), $"Удалить затраты: {ids.Length} строк?",
            "Удаление СМР", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            var result = await Task.Run(() => SqlServerSmrExpenses.Delete(
                _connectionString, _contractId, ids, ServerRevision()));
            if (!result.Saved) { SetStatus("Удаление: " + result.Error); return; }
            _revision = result.Revision;
            await ReloadAsync();
            SetStatus($"Удалено затрат на монтаж: {ids.Length}.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка удаления затрат: " + ex.Message);
        }
    }

    private void ExpenseGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (_readOnly || !CanUseSnapshot || e.Row.Item is not SmrExpensePreviewRow)
            e.Cancel = true;
    }

    private async void ExpenseGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (_readOnly || !CanUseSnapshot || e.EditAction != DataGridEditAction.Commit ||
            e.Row.Item is not SmrExpensePreviewRow row || e.EditingElement is not TextBox editor)
            return;
        var field = e.Column.Header?.ToString() switch
        {
            "Дата" => "date",
            "Статья" => "kind",
            "Кому" => "who",
            "Сумма" => "amount",
            "Пометка" => "note",
            _ => null,
        };
        if (field is null) return;
        using var operation = BeginCostOperation();
        if (operation is null) return;
        var input = editor.Text;
        try
        {
            var result = await Task.Run(() => SqlServerSmrExpenses.Save(
                _connectionString, _contractId, row.Id, field, input, ServerRevision()));
            if (!result.Saved) { SetStatus("Затраты СМР: " + result.Error); await ReloadAsync(); return; }
            _revision = result.Revision;
            await ReloadAsync();
            SetStatus("Затраты СМР сохранены; итог монтажа обновлён.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка сохранения СМР: " + ex.Message);
            await ReloadAsync();
        }
    }

    private async void SmrModeSwitch_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginCostOperation();
        if (operation is null) return;
        var target = (SmrModePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (target is null) return;

        try
        {
            // The preview never writes. Save repeats every guard and recalculates
            // the plan under a new SERIALIZABLE transaction after user approval.
            var plan = await Task.Run(() => SqlServerSmrMode.PreviewSmrMode(
                _connectionString, _contractId, target));
            var diff = plan.Differences.Count == 0 ? "Хранимые денежные поля не меняются." :
                string.Join("\n", plan.Differences.Select(d =>
                    d.Field + ": " + d.Before + " → " + d.After));
            var money = plan.BeforeSmr is null || plan.AfterSmr is null
                ? "Для percent/systems денежный предпросмотр пока не подтверждён."
                : "СМР до: " + plan.BeforeSmr.Value.ToString("N2", CultureInfo.CurrentCulture) +
                  " ₽; после: " + plan.AfterSmr.Value.ToString("N2", CultureInfo.CurrentCulture) + " ₽";
            if (!plan.Allowed)
            {
                var reason = plan.RefusalReason ?? "Операция заблокирована.";
                MessageBox.Show(Window.GetWindow(this),
                    "REFUSE — конвертация СМР не выполнялась.\n\n" + reason +
                    "\n\n" + money + "\n" + diff,
                    "Предпросмотр СМР", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("Режим не изменён: " + reason);
                await ReloadAsync();
                return;
            }
            if (plan.IsNoOp)
            {
                SetStatus("Режим СМР не менялся: пересчёт цен и формул не выполнялся.");
                return;
            }
            if (MessageBox.Show(Window.GetWindow(this),
                "SAFE — смена режима без изменения денежных величин.\n\n" +
                money + "\n" + diff + "\n\nПродолжить?",
                "Предпросмотр СМР", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes)
            {
                await ReloadAsync();
                return;
            }
            var expectedRevision = ServerRevision();
            if (!string.Equals(plan.ExpectedRevision, expectedRevision, StringComparison.Ordinal))
            {
                await ReloadAsync();
                SetStatus("Данные изменились. Проверьте новый предпросмотр.");
                return;
            }
            var result = await Task.Run(() => SqlServerSmrMode.SetSmrMode(
                _connectionString, _contractId, target, expectedRevision));
            if (!result.Saved)
            {
                SetStatus("Режим не изменён: " + result.Error);
                await ReloadAsync();
                return;
            }
            _revision = result.Revision;
            await ReloadAsync();
            SetStatus("Режим СМР изменён без перезаписи ручных цен и формул.");
        }
        catch (Exception ex)
        {
            SetStatus("Смена режима не подтверждена (readback/reconciliation требуется при unknown COMMIT): " +
                ex.Message);
            await ReloadAsync();
        }
    }

}
