using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Import preview, modeled on the Python QDialog: selectable blocks, reconciliations,
/// editable document metadata and target object. Nothing is written before confirmation.
/// </summary>
internal sealed class ExcelImportPreviewWindow : Window
{
    private sealed record Target(long? Id, string Title)
    {
        public override string ToString() => Title;
    }
    private sealed record BaseDocument(long Id, string Title)
    {
        public override string ToString() => Title;
    }

    internal sealed class PreviewRow
    {
        public required ExcelImportBlock Block { get; init; }
        public bool Selected { get; set; }
        public string Sheet => Block.Sheet;
        public string Span => $"{Block.FirstRow}–{Block.LastRow}";
        public int Positions => Block.PositionCount;
        public string Materials => Block.MaterialSum.ToString("N2");
        public string Smr => (Block.Smr ?? 0m).ToString("N2");
        public string Total => (Block.DeclaredTotal ?? Block.CalculatedTotal).ToString("N2");
        public string Check => Block.VolumesOnly ? "объёмы" :
            Block.DeclaredMaterial is null && Block.DeclaredTotal is null ? "нет итогов" :
            Block.Ok ? "✓ сходится" : "✗ расхождение";
        public string Kind { get; set; } = "kp";
        public string Number { get; set; } = "";
        public string Date { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private readonly ExcelImportResult _result;
    private readonly IReadOnlyDictionary<long, IReadOnlySet<string>> _alreadyLoaded;
    private readonly List<DocRow> _docs;
    private readonly ComboBox _objects = new() { MinWidth = 370, Margin = new Thickness(0, 0, 16, 0) };
    private readonly ComboBox _baseDocs = new() { MinWidth = 220 };
    private readonly TextBox _newName = new() { MinWidth = 240 };
    private readonly TextBox _report = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(12)
    };
    private readonly DataGrid _table = new()
    {
        AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single,
        Margin = new Thickness(0, 8, 0, 8), MinHeight = 250
    };
    private readonly ObservableCollection<PreviewRow> _rows = new();
    private readonly Button _confirm = new() { Content = "Импортировать", Width = 145, Height = 32, Margin = new Thickness(8, 0, 0, 0) };

    public long? ObjectId => (_objects.SelectedItem as Target)?.Id;
    public string NewObjectName => _newName.Text.Trim();
    public IReadOnlyList<ExcelImportChoice> Choices => _rows.Where(r => r.Selected)
        .Select(r => new ExcelImportChoice(r.Block, r.Kind, r.Number.Trim(),
            r.Date.Trim(), r.Name.Trim(), r.Kind == "dop" ? (_baseDocs.SelectedItem as BaseDocument)?.Id : null))
        .ToList();

    public ExcelImportPreviewWindow(Window parent, ExcelImportResult result,
        IEnumerable<ObjectRow> objects, IEnumerable<DocRow> docs,
        long? defaultObjectId, IReadOnlyDictionary<long, IReadOnlySet<string>> alreadyLoaded)
    {
        Owner = parent;
        _result = result;
        _docs = docs.ToList();
        _alreadyLoaded = alreadyLoaded;
        Title = "Импорт КП — " + Path.GetFileName(result.FileName);
        Width = 1250; Height = 720; MinWidth = 880; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;

        WorkspaceWindowUi.ApplyChrome(this);
        WorkspaceWindowUi.StyleTable(_table);
        var layout = new Grid { Margin = new Thickness(16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var target = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        target.Children.Add(Label("Объект:"));
        _objects.Items.Add(new Target(null, "➕ Новый объект…"));
        foreach (var obj in objects.Where(x => !x.Archived))
            _objects.Items.Add(new Target(obj.Id, obj.Name));
        _objects.SelectedItem = _objects.Items.OfType<Target>().FirstOrDefault(x => x.Id == defaultObjectId) ??
                                _objects.Items[0];
        target.Children.Add(_objects);
        target.Children.Add(Label("Новый объект:"));
        target.Children.Add(_newName);
        _newName.Text = Path.GetFileNameWithoutExtension(result.FileName).Replace("КП ", "").Trim();
        Grid.SetRow(target, 0); layout.Children.Add(target);

        var meta = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        meta.Children.Add(Label("Найденные КП (отметьте нужные):"));
        meta.Children.Add(new TextBlock { Text = "Основание ДС:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) });
        meta.Children.Add(_baseDocs);
        Grid.SetRow(meta, 1); layout.Children.Add(meta);

        _table.Columns.Add(new DataGridCheckBoxColumn { Header = "✓", Binding = new Binding(nameof(PreviewRow.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 37 });
        AddColumn("Лист", nameof(PreviewRow.Sheet), 130, true);
        AddColumn("Строки", nameof(PreviewRow.Span), 75, true);
        AddColumn("Позиций", nameof(PreviewRow.Positions), 74, true);
        AddColumn("Материалы", nameof(PreviewRow.Materials), 108, true);
        AddColumn("СМР", nameof(PreviewRow.Smr), 102, true);
        AddColumn("Итого", nameof(PreviewRow.Total), 112, true);
        AddColumn("Проверка", nameof(PreviewRow.Check), 122, true);
        _table.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Тип", Width = 90, ItemsSource = new[] { "kp", "main", "dop" },
            SelectedItemBinding = new Binding(nameof(PreviewRow.Kind)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
        });
        AddColumn("№", nameof(PreviewRow.Number), 85, false);
        AddColumn("Дата", nameof(PreviewRow.Date), 100, false);
        AddColumn("Название", nameof(PreviewRow.Name), new DataGridLength(1, DataGridLengthUnitType.Star), false);
        foreach (var column in _table.Columns.OfType<DataGridTextColumn>())
            WorkspaceWindowUi.StyleColumn(column, column.Header?.ToString() switch
            {
                "Строки" or "Позиций" or "Материалы" or "СМР" or "Итого" => TextAlignment.Right,
                "№" or "Дата" or "Проверка" => TextAlignment.Center,
                _ => TextAlignment.Left
            });
        TableColumnMenu.SetProtect(_table, "✓|Лист|Позиций|Итого|Проверка|Тип|Название");
        TableColumnMenu.SetKey(_table, "excel-import-preview");
        _table.ItemsSource = _rows;
        _table.SelectionChanged += (_, _) => ShowReport();
        Grid.SetRow(_table, 2); layout.Children.Add(_table);

        _report.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(_report, 3); layout.Children.Add(_report);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Отмена", Width = 105, Height = 32 };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        WorkspaceWindowUi.StyleButton(cancel);
        WorkspaceWindowUi.StyleButton(_confirm, primary: true, icon: "IconLabelUpload");
        actions.Children.Add(cancel);
        _confirm.Click += (_, _) => Confirm();
        actions.Children.Add(_confirm);
        Grid.SetRow(actions, 4); layout.Children.Add(actions);

        Content = layout;
        var many = result.Blocks.Count > 1;
        foreach (var b in result.Blocks)
            _rows.Add(new PreviewRow
            {
                Block = b, Selected = b.Ok && (b.MaterialSum > 0 || b.VolumesOnly),
                Kind = b.Kind, Number = b.Number, Date = b.Date,
                Name = b.Title.Length > 0 ? b.Title : many ? b.Sheet : ""
            });
        _objects.SelectionChanged += (_, _) => { UpdateTarget(); ShowReport(); };
        UpdateTarget();
        if (_rows.Count > 0) _table.SelectedIndex = 0;
        else _report.Text = "В файле не найдены таблицы КП.\n" + string.Join("\n", result.Notes);
        _confirm.IsEnabled = _rows.Count > 0;
    }

    private static TextBlock Label(string title) => new()
    { Text = title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };

    private void AddColumn(string title, string binding, double width, bool readOnly) =>
        AddColumn(title, binding, new DataGridLength(width), readOnly);

    private void AddColumn(string title, string binding, DataGridLength width, bool readOnly) =>
        _table.Columns.Add(new DataGridTextColumn
        {
            Header = title, Binding = new Binding(binding)
            {
                Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
            },
            IsReadOnly = readOnly, Width = width
        });

    private void UpdateTarget()
    {
        _newName.IsEnabled = ObjectId is null;
        _baseDocs.Items.Clear();
        foreach (var d in _docs.Where(d => d.ObjectId == ObjectId && d.Kind is "main" or "kp" && !d.IsStage))
            _baseDocs.Items.Add(new BaseDocument(d.Id, d.Title));
        if (_baseDocs.Items.Count > 0) _baseDocs.SelectedIndex = 0;
    }

    private void ShowReport()
    {
        if (_table.SelectedItem is not PreviewRow row) return;
        var b = row.Block;
        var info = new List<string>
        {
            $"Лист «{b.Sheet}», строки {b.FirstRow}–{b.LastRow}; позиций {b.PositionCount}.",
            "",
            "Разделы и системы:"
        };
        foreach (var s in b.Sections)
            info.Add($"  {s.Name} — {s.Positions.Sum(p => 1 + p.Children.Count)} поз., {s.MaterialSum:N2} ₽");
        info.Add("");
        info.Add($"Материалы {b.MaterialSum:N2} · СМР {b.Smr ?? 0m:N2} · Транспорт {b.Transport ?? 0m:N2}");
        info.Add($"Итого расчётное {b.CalculatedTotal:N2} · в файле {b.DeclaredTotal?.ToString("N2") ?? "не указано"}");
        info.Add($"НДС {b.VatRate:0.##}% · { (b.Form == "dns" ? "начисляется сверху" : "в том числе") }");
        if (b.HasPurchase) info.Add("Закупочные цены будут сохранены отдельно.");
        if (b.HasWork) info.Add("Работы будут записаны отдельно от материалов, режим СМР — по позициям.");
        info.Add(""); info.Add("Проверки:");
        foreach (var check in b.Checks) info.Add($"  {(check.Passed ? "✓" : "✗")} {check.Description}");
        foreach (var warning in b.Warnings) info.Add("  ⚠ " + warning);
        if (ObjectId is long id && _alreadyLoaded.TryGetValue(id, out var sheets) &&
            sheets.Contains(Path.GetFileName(_result.FileName) + "|" + b.Sheet))
            info.Insert(0, "⚠ Этот файл и лист уже загружены в объект. Повторный импорт заблокирован.\n");
        if (_result.Notes.Count > 0)
        {
            info.Add(""); info.Add("По файлу:");
            info.AddRange(_result.Notes);
        }
        _report.Text = string.Join(Environment.NewLine, info);
    }

    private void Confirm()
    {
        _table.CommitEdit(DataGridEditingUnit.Cell, true);
        _table.CommitEdit(DataGridEditingUnit.Row, true);
        var selections = Choices;
        if (selections.Count == 0)
        {
            MessageBox.Show(this, "Отметь хотя бы один КП.", "Импорт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ObjectId is null && NewObjectName.Length == 0)
        {
            MessageBox.Show(this, "Укажи название нового объекта.", "Импорт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (selections.Any(c => c.Kind == "dop" && c.BaseContractId is null))
        {
            MessageBox.Show(this, "Для допсоглашения выбери существующий договор-основание.", "Импорт",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ObjectId is long id && _alreadyLoaded.TryGetValue(id, out var sheets) &&
            selections.Any(c => sheets.Contains(Path.GetFileName(_result.FileName) + "|" + c.Block.Sheet)))
        {
            MessageBox.Show(this, "Один из отмеченных листов уже был импортирован в объект.", "Повторный импорт",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (selections.Any(c => !c.Block.Ok) &&
            MessageBox.Show(this, "Для некоторых КП суммы не сходятся с файлом или нет итоговых строк.\n" +
                "Импортировать выбранные блоки?", "Проверка сумм", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        DialogResult = true;
        Close();
    }
}
