using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Explicit, non-destructive diff approval. The window never writes to SQL Server.</summary>
internal sealed class ExcelExistingUpdatePreviewWindow : Window
{
    private sealed record ContractChoice(long Id, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record BlockChoice(ExcelImportBlock Block)
    {
        public override string ToString() =>
            Block.Sheet + " / " + Block.FirstRow + "–" + Block.LastRow +
            " (" + Block.PositionCount + " поз.)";
    }

    private sealed record MappingChoice(ExcelExistingDbRow Row)
    {
        public override string ToString() =>
            "SQL ID " + Row.Id + " | " + Row.SectionPath +
            " | родитель: " + (Row.ParentName.Length == 0 ? "—" : Row.ParentName) +
            " | " + Row.Name + " / " + Row.Unit +
            " | объём " + Row.Quantity + " | сохранённая цена " + Row.Price;
    }

    private readonly string _connectionString;
    private readonly string _filePath;
    private readonly string _parsedFingerprint;
    private readonly bool _hasMissingFormulaCache;
    private readonly ComboBox _contracts = new() { MinWidth = 300 };
    private readonly ComboBox _blocks = new() { MinWidth = 300 };
    private readonly Button _compare = new() { Content = "Сравнить", MinWidth = 100 };
    private readonly Button _resolve = new() { Content = "Сопоставить 1:1…", MinWidth = 155, IsEnabled = false };
    private readonly Button _confirm = new()
    {
        Content = "Применить выбранные изменения", MinWidth = 220,
        IsEnabled = false
    };
    private readonly TextBlock _summary = new()
    {
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8)
    };
    private bool _busy;
    private bool _closed;
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = false, CanUserAddRows = false,
        CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single,
        EnableRowVirtualization = true, EnableColumnVirtualization = true,
        MinRowHeight = 27, AlternationCount = 0, HeadersVisibility = DataGridHeadersVisibility.Column
    };

    public ExcelExistingUpdatePlan? Plan { get; private set; }

    public ExcelExistingUpdatePreviewWindow(Window owner, ExcelImportResult imported,
        IEnumerable<DocRow> docs, long? objectId, string connectionString, string filePath, string parsedFingerprint)
    {
        Owner = owner;
        _connectionString = connectionString;
        _filePath = filePath;
        _parsedFingerprint = parsedFingerprint;
        _hasMissingFormulaCache = HasMissingFormulaCache(imported);
        Title = "Безопасное обновление существующей сметы из Excel";
        Width = 1330;
        Height = 760;
        MinWidth = 960;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var doc in docs.Where(x => !objectId.HasValue || x.ObjectId == objectId.Value))
            _contracts.Items.Add(new ContractChoice(doc.Id, doc.Title));
        if (_contracts.Items.Count > 0) _contracts.SelectedIndex = 0;
        foreach (var block in imported.Blocks)
            _blocks.Items.Add(new BlockChoice(block));
        if (_blocks.Items.Count > 0) _blocks.SelectedIndex = 0;

        WorkspaceWindowUi.ApplyChrome(this);
        WorkspaceWindowUi.StyleTable(_grid);
        WorkspaceWindowUi.StyleButton(_compare, icon: "IconLabelSearch");
        WorkspaceWindowUi.StyleButton(_resolve, icon: "IconLabelEdit");
        WorkspaceWindowUi.StyleButton(_confirm, primary: true, icon: "IconLabelCheck");
        var layout = new DockPanel { Margin = new Thickness(22, 18, 22, 16) };
        Content = layout;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        header.Children.Add(WorkspaceWindowUi.Heading("Обновить из спецификации"));
        header.Children.Add(WorkspaceWindowUi.Hint("Файл: " + Path.GetFileName(filePath)));
        header.Children.Add(new TextBlock
        {
            Text = "Excel → существующее КП. Ручные цены, формулы, примечания, КС и ДС не перезаписываются.",
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
        });

        var controls = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        controls.Children.Add(Caption("Документ:"));
        controls.Children.Add(_contracts);
        controls.Children.Add(Caption("Блок Excel:"));
        controls.Children.Add(_blocks);
        _compare.Margin = new Thickness(10, 0, 0, 0);
        controls.Children.Add(_compare);
        header.Children.Add(controls);

        if (imported.Notes.Count != 0)
            header.Children.Add(new TextBlock
            {
                Text = string.Join("; ", imported.Notes),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DarkGoldenrod, Margin = new Thickness(0, 8, 0, 0)
            });
        header.Children.Add(_summary);
        _summary.Text = "Выбери документ и блок Excel, затем нажми «Сравнить». Никаких изменений до подтверждения.";

        var footer = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        var cancel = new Button { Content = "Отмена", MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => { DialogResult = false; };
        WorkspaceWindowUi.StyleButton(cancel);
        _confirm.Click += Confirm;
        var safe = new Button { Content = "Выбрать безопасные", MinWidth = 155 };
        safe.Click += (_, _) => ChooseSafe(true);
        footer.Children.Add(safe);
        var clear = new Button
        {
            Content = "Снять выбор", MinWidth = 110, Margin = new Thickness(8, 0, 0, 0)
        };
        clear.Click += (_, _) => ChooseSafe(false);
        footer.Children.Add(clear);
        _resolve.Margin = new Thickness(8, 0, 0, 0);
        _resolve.Click += ResolveAmbiguous;
        WorkspaceWindowUi.StyleButton(safe, icon: "IconLabelCheck");
        WorkspaceWindowUi.StyleButton(clear);
        footer.Children.Add(_resolve);
        _confirm.Margin = new Thickness(8, 0, 0, 0);
        footer.Children.Add(_confirm);
        footer.Children.Add(cancel);

        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Статус", Binding = new Binding(nameof(ExcelExistingDifference.Kind))
            { Converter = new ExcelPreviewUi.ChangeLabelConverter() },
            IsReadOnly = true, Width = 160
        });
        var template = new DataTemplate();
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        check.SetBinding(CheckBox.IsCheckedProperty, new Binding("Apply")
        {
            Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        check.SetBinding(CheckBox.IsEnabledProperty,
            new Binding(nameof(ExcelExistingDifference.SafeToApply)));
        template.VisualTree = check;
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Применить", CellTemplate = template, Width = 90
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Excel / SQL ID", Binding = new Binding(nameof(ExcelExistingDifference.DisplayIdentity)),
            IsReadOnly = true, Width = 190
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Система", Binding = new Binding(nameof(ExcelExistingDifference.DisplaySystem)),
            IsReadOnly = true, Width = 220
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Наименование", Binding = new Binding(nameof(ExcelExistingDifference.DisplayName)),
            IsReadOnly = true, Width = 250
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Объём до → после", Binding = new Binding(nameof(ExcelExistingDifference.DisplayQuantity)),
            IsReadOnly = true, Width = 145
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Цена сохранена / в Excel", Binding = new Binding(nameof(ExcelExistingDifference.DisplayPrice)),
            IsReadOnly = true, Width = 155
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Причина / последствия", Binding = new Binding(nameof(ExcelExistingDifference.Explanation)),
            IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        foreach (var column in _grid.Columns.OfType<DataGridTextColumn>())
            WorkspaceWindowUi.StyleColumn(column, column.Header?.ToString() switch
            {
                "Статус" or "Excel / SQL ID" => TextAlignment.Center,
                "Объём до → после" or "Цена сохранена / в Excel" => TextAlignment.Right,
                _ => TextAlignment.Left
            });
        var statusColumn = _grid.Columns.OfType<DataGridTextColumn>().Single(c => Equals(c.Header, "Статус"));
        statusColumn.ElementStyle = ExcelPreviewUi.StatusStyle(statusColumn.ElementStyle, nameof(ExcelExistingDifference.Kind),
            (ExcelExistingChangeKind.Added, "#047857"), (ExcelExistingChangeKind.Changed, "#B45309"),
            (ExcelExistingChangeKind.DeletedInSource, "#B91C1C"), (ExcelExistingChangeKind.Conflict, "#B91C1C"),
            (ExcelExistingChangeKind.Ambiguous, "#B45309"));
        _grid.Columns.OfType<DataGridTextColumn>().Single(c => Equals(c.Header, "Причина / последствия")).MinWidth = 230;
        TableColumnMenu.SetProtect(_grid, "Статус|Применить|Excel / SQL ID|Наименование|Объём до → после|Причина / последствия");
        TableColumnMenu.SetKey(_grid, "excel-update-preview");
        layout.Children.Add(_grid);

        _contracts.SelectionChanged += (_, _) => ResetPlan();
        _blocks.SelectionChanged += (_, _) => ResetPlan();
        _compare.Click += async (_, _) =>
        {
            if (_closed || _busy || _contracts.SelectedItem is not ContractChoice contract ||
                _blocks.SelectedItem is not BlockChoice block) return;
            ResetPlan();
            SetBusy(true);
            _summary.Text = "Сравнение…";
            try
            {
                var result = await Task.Run(() =>
                {
                    var plan = SqlServerExcelExistingUpdate.Prepare(
                        _connectionString, contract.Id, _filePath, new[] { block.Block });
                    if (!string.Equals(plan.FileSha256, _parsedFingerprint, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Файл Excel изменён после первоначального разбора.");
                    var groups = plan.Differences.GroupBy(x => x.Kind)
                        .Select(g => g.Key + ": " + g.Count());
                    return (Plan: plan, Summary: string.Join("  |  ", groups),
                        HasSafe: plan.Differences.Any(x => x.SafeToApply));
                });
                if (_closed) return;
                PublishPlan(result.Plan, result.HasSafe);
                _summary.Text = result.Summary +
                    ". Отсутствующие в Excel строки останутся на месте. " +
                    "Конфликты пропускаются; без подтверждённого исходного объёма нужен выбор каждой строки вручную.";
                if (_hasMissingFormulaCache)
                    _summary.Text += " Запись заблокирована: есть формулы без сохранённых результатов.";
            }
            catch (Exception ex)
            {
                if (_closed) return;
                MessageBox.Show(this, ex.Message, "Не удалось сравнить смету",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                _summary.Text = "Сравнение не выполнено. SQL не изменён.";
            }
            finally
            {
                if (!_closed) SetBusy(false);
            }
        };
    }

    private static bool HasMissingFormulaCache(ExcelImportResult result) =>
        result.Notes.Any(s => s.Contains("Формулы без сохранённого результата",
            StringComparison.OrdinalIgnoreCase));

    private static TextBlock Caption(string value) => new()
    {
        Text = value, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(10, 0, 6, 0)
    };

    private bool _hasSafeChanges;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _compare.IsEnabled = _contracts.IsEnabled = _blocks.IsEnabled = _grid.IsEnabled = !busy;
        _resolve.IsEnabled = !busy && Plan is not null;
        _confirm.IsEnabled = !busy && _hasSafeChanges && !_hasMissingFormulaCache;
    }

    private void PublishPlan(ExcelExistingUpdatePlan plan, bool hasSafe)
    {
        Plan = plan;
        _hasSafeChanges = hasSafe;
        // One binding update, not a notification for every difference.
        _grid.ItemsSource = plan.Differences;
    }

    private void ResetPlan()
    {
        Plan = null;
        _hasSafeChanges = false;
        _grid.ItemsSource = null;
        _confirm.IsEnabled = false;
        _resolve.IsEnabled = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    private async void ResolveAmbiguous(object sender, RoutedEventArgs e)
    {
        if (_closed || _busy) return;
        if (Plan is not { } plan || _grid.SelectedItem is not ExcelExistingDifference target ||
            target.Kind != ExcelExistingChangeKind.Ambiguous || target.Source is null)
        {
            MessageBox.Show(this, "Выбери неоднозначную строку Excel в таблице.",
                "Сопоставление 1:1", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!CommitGrid()) return;
        var source = target.Source;
        SetBusy(true);
        try
        {
            var candidates = await Task.Run(() => plan.MappingCandidates(source.Ordinal));
            if (_closed || !ReferenceEquals(Plan, plan)) return;
            ShowMappingDialog(plan, source, candidates);
        }
        catch (Exception error)
        {
            if (!_closed)
                MessageBox.Show(this, error.Message, "Соответствие отклонено",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (!_closed) SetBusy(false);
        }
    }

    private void ShowMappingDialog(ExcelExistingUpdatePlan plan, ExcelExistingSourceRow source,
        IReadOnlyList<ExcelExistingDbRow> candidates)
    {
        if (candidates.Count == 0)
        {
            MessageBox.Show(this,
                "Нет кандидатов с доказанным совпадением раздела, родителя, исходного имени и единицы. " +
                "Изменение stable ID, перенос системы и m:n здесь запрещены. Строка останется SKIP.",
                "Нет безопасного соответствия", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new Window
        {
            Owner = this, Title = "Подтверждение соответствия Excel → SQL",
            Width = 860, Height = 420, MinWidth = 650, MinHeight = 330,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        WorkspaceWindowUi.ApplyChrome(dialog);
        var panel = new DockPanel { Margin = new Thickness(14) };
        dialog.Content = panel;
        var info = new TextBlock
        {
            Text = "EXCEL: стр. " + source.SourceRow +
                " | stable ID " + (source.StableId?.ToString() ?? "нет") +
                " | " + source.SectionPath + " | родитель: " + source.ParentName +
                " | " + source.Name + " / " + source.Unit +
                " | количество " + source.Quantity + " | цена " + source.Price +
                "\nВыбери ровно одну SQL-позицию. Ручные цены, формулы и связанные документы не изменятся. " +
                "После сопоставления все галочки сбрасываются.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(info, Dock.Top);
        panel.Children.Add(info);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var accept = new Button { Content = "Подтвердить 1:1", MinWidth = 150 };
        var cancel = new Button { Content = "Оставить SKIP", MinWidth = 130, Margin = new Thickness(8, 0, 0, 0) };
        WorkspaceWindowUi.StyleButton(accept, primary: true, icon: "IconLabelCheck");
        WorkspaceWindowUi.StyleButton(cancel);
        buttons.Children.Add(accept);
        buttons.Children.Add(cancel);
        cancel.Click += (_, _) => dialog.DialogResult = false;

        var choices = new ListBox { ItemsSource = candidates.Select(row => new MappingChoice(row)).ToArray() };
        ScrollViewer.SetHorizontalScrollBarVisibility(choices, ScrollBarVisibility.Disabled);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        choices.ItemContainerStyle = itemStyle;
        var itemTemplate = new DataTemplate();
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        itemTemplate.VisualTree = text;
        choices.ItemTemplate = itemTemplate;
        choices.SelectedIndex = -1;
        panel.Children.Add(choices);
        bool dialogClosed = false;
        bool mapping = false;
        dialog.Closed += (_, _) => dialogClosed = true;
        accept.Click += async (_, _) =>
        {
            if (mapping || dialogClosed || _closed || choices.SelectedItem is not MappingChoice chosen) return;
            var old = chosen.Row;
            if (MessageBox.Show(dialog,
                    "Excel: " + source.Name + " / " + source.Unit + " / " + source.Quantity +
                    "\nSQL ID " + old.Id + ": " + old.Name + " / " + old.Unit + " / " + old.Quantity +
                    "\n\nЗафиксировать исключительно это соответствие 1:1? " +
                    "Все действия останутся SKIP до отдельного подтверждения.",
                    "Ручное соответствие", MessageBoxButton.YesNo, MessageBoxImage.Warning) !=
                MessageBoxResult.Yes) return;
            mapping = true;
            accept.IsEnabled = choices.IsEnabled = false;
            try
            {
                var result = await Task.Run(() =>
                {
                    var mapped = plan.WithExplicitMapping(source.Ordinal, old.Id);
                    return (Plan: mapped, HasSafe: mapped.Differences.Any(x => x.SafeToApply));
                });
                if (dialogClosed || _closed || !ReferenceEquals(Plan, plan)) return;
                PublishPlan(result.Plan, result.HasSafe);
                _summary.Text = "Ручное 1:1 сопоставление сохранено только в плане предпросмотра. " +
                    "Выбор изменений сброшен: отметь нужную строку отдельно. " +
                    "Ни одна SQL-запись ещё не изменена.";
                dialog.DialogResult = true;
            }
            catch (Exception error)
            {
                if (!dialogClosed && !_closed)
                    MessageBox.Show(dialog, error.Message, "Соответствие отклонено",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                mapping = false;
                if (!dialogClosed && !_closed) accept.IsEnabled = choices.IsEnabled = true;
            }
        };
        dialog.ShowDialog();
    }

    private void ChooseSafe(bool selected)
    {
        if (_closed || _busy || Plan is not { } plan || !CommitGrid()) return;
        var changed = false;
        foreach (var row in plan.Differences)
        {
            var apply = selected && row.SafeForBulk;
            if (row.Apply == apply) continue;
            row.Apply = apply;
            changed = true;
        }
        if (changed) _grid.Items.Refresh();
    }

    private bool CommitGrid() =>
        _grid.CommitEdit(DataGridEditingUnit.Cell, true) &&
        _grid.CommitEdit(DataGridEditingUnit.Row, true);

    private void Confirm(object sender, RoutedEventArgs e)
    {
        if (_closed || _busy || _hasMissingFormulaCache || Plan is not { } plan || !CommitGrid()) return;
        try
        {
            var selected = plan.Selected();
            if (selected.Any(x => !x.SafeToApply)) throw new InvalidOperationException("Есть неразрешённый конфликт.");
            var confirmed = MessageBox.Show(this,
                "Применить " + selected.Count + " изменений в существующее КП?\n" +
                "Цены и примечания существующих строк не изменятся. " +
                "Удаление отсутствующих в Excel позиций не выполняется.",
                "Подтверждение Excel", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirmed == MessageBoxResult.Yes) DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Нельзя применить", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
