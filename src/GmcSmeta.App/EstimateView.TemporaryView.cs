using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Python kp_editor.py's temporary "Вид" and table zoom. All operations in
/// this file are presentation-only: no SQL writes, no changes to SortKey.
/// </summary>
public partial class EstimateView
{
    private string _temporarySort = EstimateViewProjection.DocumentOrder;
    private bool _flatTemporaryView;
    private bool _noPriceTemporaryView;
    private readonly Dictionary<int, int> _frozenTemporaryOrder = new();

    private void EstimateGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (EstimateGrid is null) return;
        var targetCell = ContextMenuTarget.TargetCell(EstimateGrid, e);
        var hit = ContextMenuTarget.SelectRow<EditableEstimateRow>(EstimateGrid, e);
        if (hit is null && EstimateGrid.Items.Count > 0)
        {
            e.Handled = true;
            return;
        }
        var selected = hit;
        var primary = EstimateGrid.SelectedItem as EditableEstimateRow;
        var sameAnchor = ReferenceEquals(selected, primary);
        var selection = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().ToArray();
        var revision = _serverRevision;
        bool CanWrite() => _estimate?.Locked == false && !_serverConflict &&
            System.Threading.Volatile.Read(ref _pendingWrites) == 0;
        var canWrite = CanWrite();
        var realSelected = selected is { IsVirtual: false };
        var nSel = selection.Count(r => !r.IsVirtual);
        var menu = new ContextMenu();
        MenuItem Add(string title, Action action, bool enabled = true, string shortcut = "",
            bool write = true, bool needsAnchor = true, string? glyph = null, bool danger = false)
        {
            var item = new MenuItem
            {
                Header = title, IsEnabled = enabled && (!needsAnchor || sameAnchor),
                InputGestureText = shortcut
            };
            if (glyph is not null) ContextMenuIcons.Set(item, glyph, danger);
            item.Click += (_, _) =>
            {
                // No action may silently retarget after a refresh or selection change.
                if ((write && (!CanWrite() || revision != _serverRevision)) ||
                    !ReferenceEquals(primary, EstimateGrid.SelectedItem) ||
                    !EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().SequenceEqual(selection))
                {
                    SetStatus("Выбор или данные изменились. Откройте меню снова.", true);
                    return;
                }
                action();
            };
            menu.Items.Add(item);
            return item;
        }
        void PasteAtTarget()
        {
            // A numeric clipboard column writes from CurrentCell, not SelectedItem.
            // Never paste into the old cell if the menu was opened on another one.
            if (!targetCell.IsValid || !ReferenceEquals(targetCell.Item, selected) ||
                !EstimateGrid.Columns.Contains(targetCell.Column))
            {
                SetStatus("Выберите конкретную ячейку для вставки.", true);
                return;
            }
            if (!EstimateGrid.CurrentCell.Equals(targetCell))
            {
                if (selection.Length != 1)
                {
                    SetStatus("Для вставки выберите одну начальную ячейку.", true);
                    return;
                }
                EstimateGrid.CurrentCell = targetCell;
            }
            if (!EstimateGrid.CurrentCell.Equals(targetCell) || !CanWrite() ||
                revision != _serverRevision ||
                !EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().SequenceEqual(selection))
            {
                SetStatus("Ячейка или данные изменились. Повторите вставку.", true);
                return;
            }
            PasteRows_Click(this, new RoutedEventArgs());
        }
        // Python kp_editor._context_menu: тот же порядок и те же подписи.
        Add("Добавить строку", () => AddRow_Click(this, new RoutedEventArgs()), canWrite, "Insert", glyph: "Plus");
        if (realSelected && selected is { IsSystem: false })
            Add("Добавить комплектующее (без цены)",
                () => AddComponent_Click(this, new RoutedEventArgs()),
                canWrite && selected is { IsSystem: false }, "Shift+Insert", glyph: "Plus");
        Add("Из справочника…", () => AddFromCatalog_Click(this, new RoutedEventArgs()), canWrite, "Ctrl+K", glyph: "Book");
        Add("Добавить систему", () => AddSection_Click(this, new RoutedEventArgs()), canWrite,
            "Ctrl+Shift+Insert", needsAnchor: false, glyph: "FolderPlus");
        if (realSelected)
            Add("Добавить подраздел в систему", () => AddSubsection_Click(this, new RoutedEventArgs()),
                canWrite && realSelected, "Ctrl+Alt+Insert", glyph: "FolderPlus");
        menu.Items.Add(new Separator());
        if (realSelected)
            Add(nSel > 1 ? $"Копировать строки ({nSel})" : "Копировать", CopySelectedRows,
                _estimate?.Locked == false, "Ctrl+C", write: false, needsAnchor: false, glyph: "Copy");
        var excelClip = false;
        var ownClip = false;
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            ownClip = EstimateClipboardPolicy.IsOwned(ClipboardIsOurs, ClipboardTextAtCopy, text);
            excelClip = EstimateClipboardPolicy.IsExternalText(ClipboardIsOurs, ClipboardTextAtCopy, text);
        }
        catch (System.Runtime.InteropServices.ExternalException) { /* буфер занят */ }
        if (excelClip)
        {
            Add("Вставить из Excel", PasteAtTarget, canWrite && targetCell.IsValid, "Ctrl+V", glyph: "Download");
            if (realSelected && nSel > 0 && !selected!.IsSystem)
                Add(nSel > 1 ? $"Заменить выделенные ({nSel}) строками из Excel"
                        : "Заменить эту строку строками из Excel",
                    () => ReplaceRows_Click(this, new RoutedEventArgs()), canWrite, "Ctrl+Shift+V", needsAnchor: false, glyph: "Copy");
        }
        else if (ownClip && ClipboardRows.Count > 0)
            Add($"Вставить скопированные ({ClipboardRows.Count})",
                PasteAtTarget, canWrite && targetCell.IsValid, "Ctrl+V", glyph: "Download");
        if (realSelected && selected is { IsSystem: false })
        {
            Add(nSel > 1 ? $"Дублировать выделенные ({nSel})" : "Копия строки",
                () => Duplicate_Click(this, new RoutedEventArgs()),
                canWrite && selected is { IsSystem: false }, "Ctrl+D", needsAnchor: false, glyph: "Duplicate");
            var canIndent = canWrite && CanPlaceEstimateRows && selection.Length == 1 &&
                !selected.IsAreaSummary && !selected.Source.OutOfKp;
            if (selected.Source.ParentPositionId is null)
                Add("Сделать комплектующим строки выше", () => _ = IndentSelectedAsync(true),
                    canIndent && !_rows.Any(r => !r.IsSystem && r.Source.ParentPositionId == selected.Id) &&
                    _rows.Any(r => !r.IsSystem && r.Source.SystemId == selected.Source.SystemId &&
                        r.Source.ParentPositionId is null && r.SortKey < selected.SortKey),
                    "Ctrl+]", glyph: "Indent");
            else
                Add("Сделать обычной строкой", () => _ = IndentSelectedAsync(false),
                    canIndent, "Ctrl+[", glyph: "Outdent");
            Add("Превратить в раздел", () => ToSections_Click(this, new RoutedEventArgs()), canWrite, needsAnchor: false, glyph: "Layers");
            Add("Перенести в другую систему…", () => MoveSection_Click(this, new RoutedEventArgs()),
                canWrite && selected is { IsSystem: false }, "Ctrl+M", needsAnchor: false, glyph: "Layers");
            if (canWrite && sameAnchor && !selected.IsSub &&
                selected.Source.ParentPositionId is null && _estimate is { } snapshot)
            {
                var preview = PriceLikeBatchPlan.Preview(snapshot.PositionsInOrder, selected.Id);
                var culture = CultureInfo.GetCultureInfo("ru-RU");
                if (preview.ClientCount > 0)
                    Add($"Цену {preview.ClientPrice.ToString("N2", culture)} ₽ — всем таким же строкам ({preview.ClientCount})",
                        () => _ = SavePriceLikeAsync(selected.Id, "price"),
                        preview.ClientCount <= PriceLikeBatchPlan.MaxChanges, glyph: "Check");
                // C# extension: only a positive purchase price, never silently
                // clear other rows with an empty/zero source.
                if (preview.PurchaseCount > 0 && preview.PurchasePrice is decimal purchase)
                    Add($"Закупочную цену {purchase.ToString("N2", culture)} ₽ — всем таким же строкам ({preview.PurchaseCount})",
                        () => _ = SavePriceLikeAsync(selected.Id, "purchase_price"),
                        preview.PurchaseCount <= PriceLikeBatchPlan.MaxChanges);
            }
        }
        if (realSelected)
        {
            Add("Копировать систему…", () => CopySection_Click(this, new RoutedEventArgs()),
                canWrite && CopySourceSystem(selected) is not null, glyph: "Copy");
            if (selected is { IsSystem: true, IsVirtual: false })
                Add("Переименовать систему", () => RenameSection_Click(this, new RoutedEventArgs()), canWrite, "F2", glyph: "Edit");
        }
        if (selected is { IsAreaSummary: true, IsVirtual: false })
            Add("Развернуть эту строку м² обратно", () => ExpandArea(selected.Id), canWrite, glyph: "Undo");
        menu.Items.Add(new Separator());
        if (realSelected)
            Add("Удалить",
                selected is { IsSystem: true }
                    ? () => DeleteSection_Click(this, new RoutedEventArgs())
                    : () => DeletePosition_Click(this, new RoutedEventArgs()),
                canWrite && nSel == 1, "Del", glyph: "Trash", danger: true);
        // Python: висячий разделитель в конце меню не рисуется.
        while (menu.Items.Count > 0 && menu.Items[^1] is Separator)
            menu.Items.RemoveAt(menu.Items.Count - 1);
        menu.PlacementTarget = EstimateGrid;
        menu.Placement = e.CursorLeft < 0 && e.CursorTop < 0
            ? PlacementMode.Bottom : PlacementMode.MousePoint;
        e.Handled = true;
        menu.IsOpen = true;
    }

    // Python common.view_button: «Группировка» и «Сортировка» — два отдельных меню
    // у двух кнопок; выбор меняет только вид таблицы, не порядок строк в КП.
    private void GroupMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        void SetGrouping(string mode)
        {
            _temporaryGroupMode = mode;
            _flatTemporaryView = mode == "list";
            if (mode is EstimateGroups.Same or EstimateGroups.Type)
                _flatTemporaryView = false;
            _frozenTemporaryOrder.Clear();
            _frozenGroupedOrder.Clear();
            ApplyFilter();
        }
        AddViewOption(menu, "По системам — как в КП",
            _temporaryGroupMode == "kp" && !_flatTemporaryView,
            () => SetGrouping("kp"));
        AddViewOption(menu, "Одинаковые позиции вместе — редактировать сразу все",
            _temporaryGroupMode == EstimateGroups.Same,
            () => SetGrouping(EstimateGroups.Same));
        AddViewOption(menu, "По типу изделия — воздуховоды, клапаны, решётки…",
            _temporaryGroupMode == EstimateGroups.Type,
            () => SetGrouping(EstimateGroups.Type));
        AddViewOption(menu, "Одним списком, без систем",
            _flatTemporaryView, () => SetGrouping("list"));
        menu.Items.Add(new Separator());
        AddViewOption(menu, "Вернуть как в КП", false, () =>
        {
            ResetTemporaryView();
            ApplyFilter();
        });
        menu.Items.Add(new Separator());
        AddViewOption(menu, "Свернуть вложенные строки (комплектующие)", false, CollapseNestedRows);
        AddViewOption(menu, "Развернуть всё", false, ExpandAllRows);
        NameWrapPreference.AddMenuItem(menu);
        menu.IsOpen = true;
    }

    private void CollapseNestedRows()
    {
        // Python collapse_nested keeps system/type headings open, but folds
        // their nested area summaries and combined duplicate-position groups.
        // ApplyFilter uses these sets, not EditableEstimateRow.IsCollapsed alone.
        _collapsedSectionIds.Clear();
        _collapsedTypeKeys.Clear();
        _expandedGroupKeys.Clear();
        _expandedAreaSummaries.Clear();
        foreach (var row in _rows)
        {
            if (row.IsSystem) row.IsCollapsed = false;
            else if (row.IsAreaSummary) row.IsCollapsed = true;
        }
        ApplyFilter();
    }

    private void ExpandAllRows()
    {
        // Python tree.expandAll: expand real systems, area rows, type headings
        // and every synthetic multi-position group in the current projection.
        _collapsedSectionIds.Clear();
        _collapsedTypeKeys.Clear();
        _expandedGroupKeys.Clear();
        foreach (var row in _rows)
        {
            if (row.IsSystem) row.IsCollapsed = false;
            else if (row.IsAreaSummary)
            {
                _expandedAreaSummaries.Add(row.Id);
                row.IsCollapsed = false;
            }
        }
        ApplyFilter(expandAllGroups: true);
    }

    private void SortMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var (key, label) in new[]
        {
            (EstimateViewProjection.DocumentOrder, "Исходный порядок (как в КП)"),
            (EstimateViewProjection.ByNumber, "По номеру позиции (по возрастанию)"),
            (EstimateViewProjection.ByName, "По названию (А → Я)"),
            (EstimateViewProjection.ByAmount, "По сумме — дорогие сверху"),
            (EstimateViewProjection.ByPrice, "По цене — дорогие сверху"),
            (EstimateViewProjection.ByQuantity, "По количеству — больше сверху")
        })
        {
            var sortKey = key;
            AddViewOption(menu, label, _temporarySort == sortKey, () => SetTemporarySort(sortKey));
        }
        menu.Items.Add(new Separator());
        AddViewOption(menu, "Сбросить сортировку", false,
            () => SetTemporarySort(EstimateViewProjection.DocumentOrder));
        menu.Items.Add(new MenuItem
        {
            Header = "Сортировка только на экране. Нажатия на заголовок: по возрастанию → по убыванию → исходный порядок.",
            IsEnabled = false
        });
        menu.IsOpen = true;
    }

    private static void AddViewOption(ContextMenu parent, string text, bool selected, Action apply)
    {
        var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = selected };
        item.Click += (_, _) => apply();
        parent.Items.Add(item);
    }

    private void NoPriceFilter_Click(object sender, RoutedEventArgs e)
    {
        _noPriceTemporaryView = !_noPriceTemporaryView;
        _frozenTemporaryOrder.Clear();
        _frozenGroupedOrder.Clear();
        ApplyFilter();
    }

    private void ResetView_Click(object sender, RoutedEventArgs e)
    {
        ResetTemporaryView();
        ApplyFilter();
    }

    private void ResetTemporaryView()
    {
        _temporarySort = EstimateViewProjection.DocumentOrder;
        _temporaryGroupMode = EstimateViewProjection.DocumentOrder;
        _flatTemporaryView = false;
        _noPriceTemporaryView = false;
        _frozenTemporaryOrder.Clear();
        _frozenGroupedOrder.Clear();
    }

    private void RefreshTemporaryViewControls()
    {
        if (NoPriceFilterButton is null || ResetViewButton is null ||
            GroupButton is null || SortButton is null) return;

        var missing = _rows.Count(r => r.HasNoPrice);
        NoPriceFilterButton.Content = $"Без цены: {missing}";
        NoPriceFilterButton.Visibility = missing > 0 || _noPriceTemporaryView
            ? Visibility.Visible : Visibility.Collapsed;
        NoPriceFilterButton.Background = _noPriceTemporaryView
            ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE))
            : Brushes.White;
        NoPriceFilterButton.BorderBrush = _noPriceTemporaryView
            ? new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB))
            : new SolidColorBrush(Color.FromRgb(0xD0, 0xD7, 0xE2));
        var groupOn = _flatTemporaryView ||
            _temporaryGroupMode != EstimateViewProjection.DocumentOrder;
        var sortOn = _temporarySort != EstimateViewProjection.DocumentOrder;
        RefreshEstimateSortIndicators();
        var active = groupOn || sortOn || _noPriceTemporaryView;
        ResetViewButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        // Python sync_view_button: подпись кнопки постоянна, активный вид подсвечен синим.
        StyleBlue(GroupButton, groupOn);
        StyleBlue(SortButton, sortOn);
        SortButton.ToolTip = sortOn
            ? "Сортировка временная. Вернуть — «Исходный порядок»"
            : "Порядок строк на экране";
        GroupButton.ToolTip = groupOn
            ? "Временная группировка. Порядок строк в КП, Excel и печати не меняется"
            : "Как показать строки: по системам, одинаковые вместе, по типу, списком";
    }

    private void StyleBlue(Button button, bool on)
    {
        button.FontWeight = FontWeights.Normal;
        if (on)
        {
            button.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE));
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
            button.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x40, 0xAF));
        }
        else
        {
            // Restore the standard white toolbar chrome, not a transparent button.
            button.ClearValue(Control.BackgroundProperty);
            button.ClearValue(Control.BorderBrushProperty);
            button.ClearValue(Control.ForegroundProperty);
        }
    }

    private void EstimateView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control ||
            e.OriginalSource is not DependencyObject source ||
            EstimateGrid is null || !IsInside(source, EstimateGrid))
            return;

        ChangeEstimateZoom(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private bool TryZoomShortcut(KeyEventArgs e, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.Control || EstimateGrid is null ||
            !EstimateGrid.IsKeyboardFocusWithin ||
            Keyboard.FocusedElement is TextBox) return false;
        var delta = e.Key switch
        {
            Key.OemPlus or Key.Add => 1,
            Key.OemMinus or Key.Subtract => -1,
            Key.D0 or Key.NumPad0 => 0,
            _ => int.MinValue
        };
        if (delta == int.MinValue) return false;
        ChangeEstimateZoom(delta);
        e.Handled = true;
        return true;
    }

    private void ChangeEstimateZoom(int step)
    {
        var old = Math.Clamp(_uiSettings.TableZoom, 0.8, 1.8);
        var next = step == 0 ? 1.0
            : Math.Clamp(Math.Round(old + step * 0.1, 2), 0.8, 1.8);
        if (Math.Abs(old - next) < 0.001)
        {
            SetStatus($"Масштаб таблицы {Math.Round(next * 100)} % — предел", false);
            return;
        }
        _uiSettings.TableZoom = next;
        ApplyEstimateZoom();
        SaveEstimateUiSettings();
        SetStatus($"Масштаб таблицы {Math.Round(next * 100)} % · Ctrl + 0 — обычный", false);
    }

    private void ApplyEstimateZoom()
    {
        if (EstimateGrid is null) return;
        var zoom = double.IsFinite(_uiSettings.TableZoom)
            ? Math.Clamp(_uiSettings.TableZoom, 0.8, 1.8) : 1.0;
        var baseSize = (double)FindResource("UiBodySize");
        var headerRatio = (double)FindResource("UiEstimateHeaderZoomRatio");
        EstimateGrid.FontSize = baseSize * zoom;
        if (EstimateColumnsButton is not null)
            EstimateColumnsButton.ToolTip = $"Какие столбцы показывать\nМасштаб таблицы: {Math.Round(zoom * 100)} % · Ctrl+колесо, Ctrl+0 — сброс";
        var baseHeader = TryFindResource(typeof(DataGridColumnHeader)) as Style;
        var header = new Style(typeof(DataGridColumnHeader))
        {
            BasedOn = baseHeader
        };
        header.Setters.Add(new Setter(Control.FontSizeProperty, baseSize * headerRatio * zoom));
        EstimateGrid.ColumnHeaderStyle = header;

        // Python common.set_alignment: headers follow the delegate alignment.
        // Base each column style on the current zoomed header; never override
        // the semantic colours of dynamically added stage columns.
        Style AlignedHeader(HorizontalAlignment alignment)
        {
            var style = new Style(typeof(DataGridColumnHeader)) { BasedOn = header };
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, alignment));
            return style;
        }
        var center = AlignedHeader(HorizontalAlignment.Center);
        var right = AlignedHeader(HorizontalAlignment.Right);
        var left = AlignedHeader(HorizontalAlignment.Left);
        foreach (var column in EstimateGrid.Columns)
        {
            if (_inlineStageColumns.Contains(column)) continue;
            column.HeaderStyle = column.Header?.ToString() switch
            {
                "№" or "Ед." or "Кол-во" => center,
                "Цена" or "Сумма" or "Закупка" or "Работа за ед." or "Работа всего"
                    or "м² за ед." or "м² всего" => right,
                _ => left
            };
        }
    }
}
