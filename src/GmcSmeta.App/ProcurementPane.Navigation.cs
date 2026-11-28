using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class ProcurementPane
{
    private readonly HashSet<string> _collapsedProcurementGroups = new(StringComparer.Ordinal);

    private static string? ProcurementGroupKey(ToggleButton toggle) =>
        (toggle.DataContext as CollectionViewGroup)?.Items.OfType<ProcurementPreviewRow>().FirstOrDefault()?.GroupIdentity;

    private void RestoreProcurementGroup(ToggleButton toggle)
    {
        var key = ProcurementGroupKey(toggle);
        toggle.IsChecked = key is null || !_collapsedProcurementGroups.Contains(key);
    }

    private void ProcurementGroupToggle_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle) RestoreProcurementGroup(toggle);
    }

    private void ProcurementGroupToggle_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is ToggleButton toggle) RestoreProcurementGroup(toggle);
    }

    private void ProcurementGroupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle || ProcurementGroupKey(toggle) is not string key) return;
        if (toggle.IsChecked == true) _collapsedProcurementGroups.Remove(key);
        else _collapsedProcurementGroups.Add(key);
    }

    private void ClearProcurementSearch_Click(object sender, RoutedEventArgs e)
    {
        ProcurementSearchBox.Clear();
        ProcurementSearchBox.Focus();
    }

    private void ProcurementFilterLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ProcurementChips is null || ProcurementFilterTail is null) return;
        var stacked = e.NewSize.Width < 1170;
        Grid.SetColumnSpan(ProcurementChips, stacked ? 2 : 1);
        Grid.SetRow(ProcurementFilterTail, stacked ? 1 : 0);
        Grid.SetColumn(ProcurementFilterTail, stacked ? 0 : 1);
        Grid.SetColumnSpan(ProcurementFilterTail, stacked ? 2 : 1);
    }

    private static ContextMenu ToolbarMenu(object sender) => new()
    {
        PlacementTarget = sender as UIElement,
        Placement = PlacementMode.Bottom
    };

    private static void MenuAction(ContextMenu menu, string label, RoutedEventHandler action,
        string? glyph = null, bool danger = false, string shortcut = "", bool enabled = true)
    {
        var item = glyph is null ? new MenuItem { Header = label } :
            ContextMenuIcons.Create(label, glyph, shortcut: shortcut, danger: danger);
        item.IsEnabled = enabled;
        item.Click += action;
        menu.Items.Add(item);
    }

    private static void MenuChoice(ContextMenu menu, string label, bool selected, Action action)
    {
        var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = selected };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void ProcurementStatusMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartOperation || SelectedBuyingIds().Count == 0) return;
        var revision = _revision;
        var attachment = _attachmentGeneration;
        var generation = _reloadGeneration;
        var selection = BuyGrid.SelectedItems.OfType<ProcurementPreviewRow>().ToArray();
        var ids = SelectedBuyingIds().OrderBy(id => id).ToArray();
        bool CurrentSelection()
        {
            if (CanStartOperation && _attachmentGeneration == attachment && _revision == revision &&
                _reloadGeneration == generation && CurrentBuyingSelection(selection) &&
                SelectedBuyingIds().OrderBy(id => id).SequenceEqual(ids)) return true;
            SetStatus("Данные или выбор изменились. Откройте меню заново.");
            return false;
        }

        var menu = ToolbarMenu(sender);
        foreach (var option in StageOptions)
        {
            var code = option.Code;
            MenuAction(menu, option.Label, async (_, _) =>
            {
                if (CurrentSelection())
                    await ApplyBulkProcAsync(null, code == "none" ? "" : code, "Статус обновлён");
            });
        }
        menu.Items.Add(new Separator());
        void Movement(string label, string kind, string? glyph = null, string document = "") =>
            MenuAction(menu, label, (_, _) =>
            {
                if (CurrentSelection() && BuyGrid.SelectedItem is ProcurementPreviewRow row &&
                    ids.Contains(row.PositionId))
                    _ = OpenMovementDialogAsync(row, kind, defaultDocument: document);
            }, glyph, enabled: BuyGrid.SelectedItem is ProcurementPreviewRow selected &&
                                ids.Contains(selected.PositionId));

        Movement("На складе — пришло…", "in");
        Movement("Выдано на объект…", "out");
        Movement("Возврат с объекта…", "back");
        Movement("Возврат поставщику…", "back_supplier");
        Movement("Изготовлено → на склад…", "in", document: "из цеха");
        MenuAction(menu, "Ожидаем поставку к…", (s, e) =>
        {
            if (CurrentSelection()) ProcurementBulkDue_Click(s, e);
        }, "History");
        menu.IsOpen = true;
    }

    private async void ProcurementBulkDue_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartOperation || SelectedBuyingIds().Count == 0) return;
        var revision = _revision;
        var attachment = _attachmentGeneration;
        var generation = _reloadGeneration;
        var selection = BuyGrid.SelectedItems.OfType<ProcurementPreviewRow>().ToArray();
        var ids = SelectedBuyingIds().ToArray();
        var prompt = new PromptDialog("Ожидаем поставку", "Дата: 25.11, 25.11.2026 или +7 (пусто — очистить)", "")
        { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true) return;
        if (!CanStartOperation || _revision != revision || attachment != _attachmentGeneration ||
            generation != _reloadGeneration || !CurrentBuyingSelection(selection) ||
            !SelectedBuyingIds().SequenceEqual(ids))
        { SetStatus("Данные или выбор изменились. Повторите назначение срока."); return; }
        if (!ProcurementDisplay.TryParseDue(prompt.Value, DateTime.Today, out var due))
        {
            SetStatus("Некорректная дата поставки.");
            return;
        }
        await ApplyBulkProcAsync(null, null, "Срок поставки обновлён", due, due is null);
    }

    private void ProcurementGroupMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = ToolbarMenu(sender);
        foreach (var option in ViewModes)
        {
            var code = option.Code;
            MenuChoice(menu, option.Label, _viewMode == code, () => { _viewMode = code; ApplyProcurementFilters(); });
        }
        NameWrapPreference.AddMenuItem(menu);
        menu.IsOpen = true;
    }

    private void ProcurementSortMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = ToolbarMenu(sender);
        foreach (var option in ViewSorts)
        {
            var code = option.Code;
            MenuChoice(menu, option.Label, _viewSort == code, () => { _viewSort = code; ApplyProcurementFilters(); });
        }
        menu.IsOpen = true;
    }

    private void ProcurementMore_Click(object sender, RoutedEventArgs e)
    {
        if (_batchBusy) return;
        var menu = ToolbarMenu(sender);
        var canWrite = CanStartOperation;
        var selected = BuyGrid.SelectedItems.OfType<ProcurementPreviewRow>().ToArray();
        var ids = SelectedBuyingIds();
        var extraSelected = ids.Count > 0 && selected.All(row => row.OutOfKp && !row.IsSub);
        var singleExtra = selected.Length == 1 && extraSelected;
        var revision = _revision;
        var attachment = _attachmentGeneration;
        var generation = _reloadGeneration;
        var primary = BuyGrid.SelectedItem;
        var rows = _allRows;
        RoutedEventHandler Guard(RoutedEventHandler action) => (s, args) =>
        {
            if (CanStartOperation && _revision == revision && attachment == _attachmentGeneration &&
                generation == _reloadGeneration && ReferenceEquals(rows, _allRows) &&
                ReferenceEquals(primary, BuyGrid.SelectedItem) &&
                (selected.Length == 0 ? BuyGrid.SelectedItems.Count == 0 : CurrentBuyingSelection(selected)))
                action(s, args);
            else SetStatus("Данные или выбор изменились. Откройте меню заново.");
        };
        MenuAction(menu, "Докупили (нет в смете)…", Guard(ProcurementAddExtra_Click), "Plus",
            enabled: canWrite && (selected.Length == 0 || ids.Count == 1));
        MenuAction(menu, "Изменить докупленное…", Guard(ProcurementEditExtra_Click), "Edit", enabled: canWrite && singleExtra);
        MenuAction(menu, "Удалить докупленное…", Guard(ProcurementDeleteExtra_Click), "Trash", danger: true,
            enabled: canWrite && extraSelected);
        MenuAction(menu, "Допсоглашение из докупленного…", Guard(ProcurementExtraDop_Click), "FilePlus",
            enabled: canWrite && _allRows.Any(row => row.OutOfKp && !row.IsSub));
        MenuAction(menu, "Отдать в цех…", Guard(ProcurementToShop_Click), "Settings", enabled: canWrite && ids.Count > 0);
        MenuAction(menu, "Заявка в цех (печать)…", ProcurementShopOrder_Click, "File");
        menu.Items.Add(new Separator());
        MenuAction(menu, "Заказ / оплата…", Guard(ProcurementOrder_Click), "File", shortcut: "F4", enabled: canWrite && ids.Count > 0);
        MenuAction(menu, "Загрузить контроль оборудования из Excel…", Guard(ImportPlan_Click), "Upload", enabled: canWrite);
        MenuAction(menu, "Сохранить в Excel / PDF…", ProcurementExport_Click, "Download");
        MenuAction(menu, "Обновить", ProcurementRefresh_Click, "Refresh");
        menu.Items.Add(new Separator());
        MenuChoice(menu, "Только незакрытые", _openOnly, () => { _openOnly = !_openOnly; ApplyProcurementFilters(); });
        MenuChoice(menu, "Без закупочной цены", _missingCost, () => { _missingCost = !_missingCost; ApplyProcurementFilters(); });
        MenuChoice(menu, "Показать денежные колонки", _showMoney, () =>
        {
            _showMoney = !_showMoney;
            var visibility = _showMoney ? Visibility.Visible : Visibility.Collapsed;
            ProcCostColumn.Visibility = ProcPriceColumn.Visibility = ProcMarginColumn.Visibility = visibility;
        });
        MenuAction(menu, "Как в КП (сброс вида)", (_, _) =>
        {
            _viewMode = _viewSort = "kp";
            ApplyProcurementFilters();
        });
        menu.IsOpen = true;
    }

    private async void ProcurementSaveDetails_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHistoryRow() is not ProcurementPreviewRow row || !CurrentBuyingRow(row)) return;
        using var operation = TryBeginOperation();
        if (operation is null) return;
        var replacement = ReplacementDetail.Text.Trim();
        var responsible = ResponsibleDetail.Text.Trim();
        var direct = DirectDetail.IsChecked == true;
        if (responsible.Length > 128)
        {
            SetStatus("Ответственный: не более 128 символов.");
            return;
        }

        SaveDetailButton.IsEnabled = false;
        try
        {
            var revision = operation.BeginWrite();
            var result = await Task.Run(() => SqlServerProcurement.SetProc(
                _connectionString, _contractId, new[] { row.PositionId }, null, null, null,
                null, null, null, replacement, null, direct, revision, responsible: responsible));

            if (!result.Saved) { SetStatus("Не сохранено: " + result.Error); await TryReloadAsync(preserveStatus: true); return; }
            await RefreshCommittedBuyRowsAsync(new[] { row.PositionId }, result.Revision);
            SetStatus("Данные позиции сохранены.");
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Сохранение не подтверждено: " + ex.Message);
            await TryReloadAsync(preserveStatus: true);
        }
        finally
        {

            SaveDetailButton.IsEnabled = SelectedHistoryRow() is not null;
        }
    }
}
