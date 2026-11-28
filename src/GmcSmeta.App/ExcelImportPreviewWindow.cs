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
    private sealed record DocumentKind(string Key, string Label);
    private static readonly DocumentKind[] DocumentKinds =
        { new("kp", "КП"), new("main", "Приложение"), new("dop", "Допсоглашение") };

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
        public string Smr => (Block.HasWork ? Block.WorkSum : Block.Smr ?? 0m).ToString("N2");
        public string Total => (Block.DeclaredTotal ?? Block.CalculatedTotal).ToString("N2");
        public string Check => Block.VolumesOnly ? "объёмы, без цен" :
            Block.DeclaredMaterial is null && Block.DeclaredTotal is null ? "— нет итогов" :
            Block.Ok ? "✓ сходится" : "✗ расхождение";
        public string Kind { get; set; } = "kp";
        public string Number { get; set; } = "";
        public string Date { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private readonly ExcelImportResult _result;
    private readonly IReadOnlyDictionary<long, IReadOnlySet<string>> _alreadyLoaded;
    private readonly List<DocRow> _docs;
    private readonly IReadOnlyList<CompanyInfo> _companies;
    private readonly IReadOnlyList<ExcelImportLegalTarget>? _legalTargets;
    private readonly ComboBox _legalAgreement = new() { MinWidth = 210, MinHeight = 32, DisplayMemberPath = nameof(ExcelImportLegalTarget.Caption) };
    private readonly TextBlock _legalLabel = new() { Text = "Договор приложений:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12,0,8,0) };
    private readonly ComboBox _annexCompany = new() { MinWidth = 210, MinHeight = 32, DisplayMemberPath = nameof(CompanyInfo.Name) };
    private readonly Button _agreementDefaults = new() { Content = "Реквизиты договора", MinHeight = 32 };
    private readonly ComboBox _objects = new() { MinWidth = 240, MinHeight = 32 };
    private readonly ComboBox _baseDocs = new() { MinWidth = 220, MinHeight = 32 };
    private readonly TextBox _newName = new() { MinWidth = 240, MinHeight = 32 };
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
        MinHeight = 150, MinRowHeight = 27, AlternationCount = 0,
        SelectionUnit = DataGridSelectionUnit.FullRow
    };
    private readonly List<PreviewRow> _rows = new();
    private readonly Button _confirm = new() { Content = "Импортировать", MinWidth = 145, MinHeight = 32, Margin = new Thickness(8, 0, 0, 0) };

    public long? ObjectId => (_objects.SelectedItem as Target)?.Id;
    public string NewObjectName => _newName.Text.Trim();
    public IReadOnlyList<ExcelImportChoice> Choices
    {
        get
        {
            var baseId = (_baseDocs.SelectedItem as BaseDocument)?.Id;
            var annexCompany = (_annexCompany.SelectedItem as CompanyInfo)?.Id;
            var baseCompany = SelectedBaseCompany();
            return _rows.Where(r => r.Selected)
                .Select(r => new ExcelImportChoice(r.Block, r.Kind, r.Number.Trim(),
                    r.Date.Trim(), r.Name.Trim(), r.Kind == "dop" ? baseId : null,
                    r.Kind == "main" ? annexCompany : r.Kind == "dop" ? baseCompany : null)
                    { AgreementId = r.Kind == "main" ? (_legalAgreement.SelectedItem as ExcelImportLegalTarget)?.Id : null })
                .ToList();
        }
    }

    public ExcelImportPreviewWindow(Window parent, ExcelImportResult result,
        IEnumerable<ObjectRow> objects, IEnumerable<DocRow> docs,
        long? defaultObjectId, IReadOnlyDictionary<long, IReadOnlySet<string>> alreadyLoaded,
        IReadOnlyList<CompanyInfo> companies, IReadOnlyList<ExcelImportLegalTarget>? legalTargets = null)
    {
        Owner = parent;
        _result = result;
        _docs = docs.ToList();
        _companies = companies;
        _legalTargets = legalTargets;
        _annexCompany.ItemsSource = companies;
        _alreadyLoaded = alreadyLoaded;
        Title = "Импорт документов — " + Path.GetFileName(result.FileName);
        Width = 1250; Height = 720; MinWidth = 880; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;

        WorkspaceWindowUi.ApplyChrome(this);
        WorkspaceWindowUi.StyleTable(_table);
        var layout = new Grid { Margin = new Thickness(16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 150 });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 90 });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var target = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        target.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        target.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        target.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        target.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        target.Children.Add(Label("Договор / группа КП:"));
        _objects.Items.Add(new Target(null, "➕ Новая карточка…"));
        foreach (var obj in objects.Where(x => !x.Archived))
            _objects.Items.Add(new Target(obj.Id, obj.Name));
        _objects.SelectedItem = _objects.Items.OfType<Target>().FirstOrDefault(x => x.Id == defaultObjectId) ??
                                _objects.Items[0];
        Grid.SetColumn(_objects, 1);
        target.Children.Add(_objects);
        var nameLabel = Label("Название новой карточки:");
        Grid.SetRow(nameLabel, 1);
        nameLabel.Margin = new Thickness(0, 6, 8, 0);
        target.Children.Add(nameLabel);
        Grid.SetRow(_newName, 1);
        Grid.SetColumn(_newName, 1);
        _newName.Margin = new Thickness(0, 6, 0, 0);
        target.Children.Add(_newName);
        _newName.Text = Path.GetFileNameWithoutExtension(result.FileName).Replace("КП ", "").Trim();
        Grid.SetRow(target, 0); layout.Children.Add(target);

        var meta = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        meta.Children.Add(Label("Найденные документы (отметьте, что загружать):"));
        meta.Children.Add(new TextBlock { Text = "Общее основание выбранных ДС:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) });
        _baseDocs.ToolTip = "Выберите основание явно. Все отмеченные ДС будут связаны с одним выбранным документом. Для разных оснований импортируйте отдельно.";
        meta.Children.Add(_baseDocs);
        meta.Children.Add(new TextBlock { Text = "Организация приложений:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) });
        _annexCompany.ToolTip = "Общая организация для выбранных приложений. ДС наследуют организацию своего основания, КП — прежнее значение по умолчанию.";
        meta.Children.Add(_annexCompany);
        meta.Children.Add(_legalLabel);
        meta.Children.Add(_legalAgreement);
        _legalAgreement.SelectionChanged += (_, _) => UpdateAgreementDefaultsEnabled();
        _agreementDefaults.Margin = new Thickness(8, 0, 0, 0);
        _agreementDefaults.ToolTip = "Подставить номер, дату и организацию договора в отмеченные приложения. Не меняет КП и ДС.";
        WorkspaceWindowUi.StyleButton(_agreementDefaults);
        _agreementDefaults.Click += (_, _) => ApplyAgreementDefaults();
        meta.Children.Add(_agreementDefaults);
        Grid.SetRow(meta, 1); layout.Children.Add(meta);

        _table.Columns.Add(new DataGridCheckBoxColumn { Header = "✓", Binding = new Binding(nameof(PreviewRow.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 37 });
        AddColumn("Лист", nameof(PreviewRow.Sheet), 130, true);
        AddColumn("Строки", nameof(PreviewRow.Span), 75, true);
        AddColumn("Позиций", nameof(PreviewRow.Positions), 74, true);
        AddColumn("Материалы", nameof(PreviewRow.Materials), 108, true);
        AddColumn("СМР", nameof(PreviewRow.Smr), 102, true);
        AddColumn("Итого", nameof(PreviewRow.Total), 112, true);
        AddColumn("Проверка", nameof(PreviewRow.Check), 158, true);
        _table.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Тип", Width = 150, ItemsSource = DocumentKinds,
            DisplayMemberPath = nameof(DocumentKind.Label), SelectedValuePath = nameof(DocumentKind.Key),
            SelectedValueBinding = new Binding(nameof(PreviewRow.Kind)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
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
        var statusColumn = _table.Columns.OfType<DataGridTextColumn>().Single(c => Equals(c.Header, "Проверка"));
        statusColumn.ElementStyle = ExcelPreviewUi.StatusStyle(statusColumn.ElementStyle, "Block.Ok",
            (true, "#2E7D32"), (false, "#C62828"));
        _table.Columns.OfType<DataGridTextColumn>().Single(c => Equals(c.Header, "Название")).MinWidth = 160;
        TableColumnMenu.SetProtect(_table, "✓|Лист|Позиций|Итого|Проверка|Тип|Название");
        TableColumnMenu.SetKey(_table, "excel-import-preview");
        _table.SelectionChanged += (_, _) => ShowReport();
        Grid.SetRow(_table, 2); layout.Children.Add(_table);

        var splitter = ExcelPreviewUi.ReportSplitter();
        Grid.SetRow(splitter, 3); layout.Children.Add(splitter);
        _report.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(_report, 4); layout.Children.Add(_report);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Отмена", Width = 105, Height = 32 };
        cancel.Click += (_, _) => DialogResult = false;
        WorkspaceWindowUi.StyleButton(cancel);
        WorkspaceWindowUi.StyleButton(_confirm, primary: true);
        actions.Children.Add(cancel);
        _confirm.Click += (_, _) => Confirm();
        actions.Children.Add(_confirm);
        Grid.SetRow(actions, 5); layout.Children.Add(actions);

        Content = layout;
        var many = result.Blocks.Count > 1;
        foreach (var b in result.Blocks)
            _rows.Add(new PreviewRow
            {
                Block = b, Selected = b.Ok && (b.MaterialSum > 0 || b.VolumesOnly),
                Kind = b.Kind, Number = b.Number, Date = b.Date,
                Name = b.Title.Length > 0 ? b.Title : many ? b.Sheet : ""
            });
        _table.ItemsSource = _rows;
        _objects.SelectionChanged += (_, _) => { UpdateTarget(); ShowReport(); };
        UpdateTarget();
        if (_rows.Count > 0) _table.SelectedIndex = 0;
        else _report.Text = "В файле не найдены таблицы смет.\n" + string.Join("\n", result.Notes);
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
            _baseDocs.Items.Add(new BaseDocument(d.Id, d.Kind == "main"
                ? AgreementPresentation.FullAnnexTitle(d) + CompanySuffix(d) : d.Title + CompanySuffix(d)));
        // Never attach an imported DS to the first document by list order.
        _baseDocs.SelectedIndex = -1;
        _annexCompany.SelectedIndex = -1;
        var cards = CurrentLegalCards();
        _legalAgreement.ItemsSource = cards.Where(a => !a.Archived).ToArray();
        _legalAgreement.SelectedIndex = -1;
        _legalLabel.Visibility = _legalAgreement.Visibility = cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAgreementDefaultsEnabled();
    }

    private string CompanySuffix(DocRow d) => " · " + (_companies.FirstOrDefault(c => c.Id == d.CompanyId)?.Name ?? "организация не указана");

    private IReadOnlyList<ExcelImportLegalTarget> CurrentLegalCards() =>
        _legalTargets?.Where(a => a.ObjectId == ObjectId).ToArray() ?? Array.Empty<ExcelImportLegalTarget>();

    private AgreementPresentation.Requisites? CurrentAgreement()
    {
        if (_legalTargets is not null)
            return _legalAgreement.SelectedItem is ExcelImportLegalTarget { CompanyId: long company } a
                ? new(a.Number, a.Date, company) : null;
        return AgreementPresentation.Resolve(_docs.Where(d => d.ObjectId == ObjectId));
    }

    private void UpdateAgreementDefaultsEnabled()
    {
        var agreement = CurrentAgreement();
        _agreementDefaults.IsEnabled = agreement is not null && _companies.Any(c => c.Id == agreement.CompanyId);
    }

    private long? SelectedBaseCompany() => _baseDocs.SelectedItem is BaseDocument b
        ? _docs.FirstOrDefault(d => d.Id == b.Id && d.ObjectId == ObjectId)?.CompanyId : null;

    private void ApplyAgreementDefaults()
    {
        if (!_table.CommitEdit(DataGridEditingUnit.Cell, true) || !_table.CommitEdit(DataGridEditingUnit.Row, true)) return;
        if (CurrentAgreement() is not { } agreement || _companies.FirstOrDefault(c => c.Id == agreement.CompanyId) is not { } company) return;
        var rows = _rows.Where(r => r.Selected && r.Kind == "main").ToArray();
        if (rows.Length == 0) return;
        if (MessageBox.Show(this, $"Подставить {agreement.Heading} и организацию «{company.Name}» в {rows.Length} отмеченных приложений?\nНомер и дата в этих строках будут заменены.",
            "Реквизиты договора", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var row in rows) { row.Number = agreement.Number; row.Date = agreement.Date; }
        _annexCompany.SelectedItem = company;
        _table.Items.Refresh();
    }

    private PreviewRow? _reportRow;
    private string _reportBody = "";
    private CancellationTokenSource? _reportCancellation;
    private readonly SemaphoreSlim _reportGate = new(1, 1);
    private bool _reportClosed;

    private async void ShowReport()
    {
        if (_reportClosed) return;
        _reportCancellation?.Cancel();
        if (_table.SelectedItem is not PreviewRow row)
        {
            _report.Text = "";
            return;
        }
        var b = row.Block;
        // Target changes reuse the report for the immutable parsed block.
        if (!ReferenceEquals(_reportRow, row))
        {
            using var cancellation = new CancellationTokenSource();
            _reportCancellation = cancellation;
            var token = cancellation.Token;
            _report.Text = "Расчёт отчёта…";
            try
            {
                await _reportGate.WaitAsync(token);
                string body;
                try { body = await Task.Run(() => BuildReport(row, token), token); }
                finally { _reportGate.Release(); }
                if (_reportClosed || token.IsCancellationRequested ||
                    !ReferenceEquals(_table.SelectedItem, row)) return;
                _reportBody = body;
                _reportRow = row;
            }
            catch (Exception error)
            {
                if (!_reportClosed && !token.IsCancellationRequested)
                    _report.Text = "Не удалось сформировать отчёт: " + error.Message;
                return;
            }
            finally
            {
                if (ReferenceEquals(_reportCancellation, cancellation)) _reportCancellation = null;
            }
        }
        var duplicate = ObjectId is long id && _alreadyLoaded.TryGetValue(id, out var sheets) &&
            sheets.Contains(Path.GetFileName(_result.FileName) + "|" + b.Sheet);
        var text = duplicate
            ? "⚠ Этот файл и лист уже загружены в выбранную карточку. Повторный импорт заблокирован.\n" +
                Environment.NewLine + _reportBody
            : _reportBody;
        if (_report.Text != text) _report.Text = text;
    }

    protected override void OnClosed(EventArgs e)
    {
        _reportClosed = true;
        _reportCancellation?.Cancel();
        base.OnClosed(e);
    }

    private string BuildReport(PreviewRow row, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var b = row.Block;
        var info = new List<string>
        {
            $"Лист «{b.Sheet}», строки {b.FirstRow}–{b.LastRow}; позиций {b.PositionCount}.",
            "",
            "Разделы и системы:"
        };
        foreach (var s in b.Sections)
        {
            token.ThrowIfCancellationRequested();
            info.Add($"  {s.Name} — {s.Positions.Sum(p => { token.ThrowIfCancellationRequested(); return 1 + p.Children.Count; })} поз., {s.MaterialSum:N2} ₽");
        }
        info.Add("");
        info.Add($"Материалы {b.MaterialSum:N2} · СМР {row.Smr} · Транспорт {b.Transport ?? 0m:N2}");
        info.Add($"Итого расчётное {b.CalculatedTotal:N2} · в файле {b.DeclaredTotal?.ToString("N2") ?? "не указано"}");
        info.Add($"НДС {b.VatRate:0.##}% · { (b.Form == "dns" ? "начисляется сверху" : "в том числе") }");
        if (b.HasPurchase) info.Add("Закупочные цены будут сохранены отдельно.");
        if (b.HasWork) info.Add("Работы будут записаны отдельно от материалов, режим СМР — по позициям.");
        info.Add(""); info.Add("Проверки:");
        foreach (var check in b.Checks) info.Add($"  {(check.Passed ? "✓" : "✗")} {check.Description}");
        foreach (var warning in b.Warnings) info.Add("  ⚠ " + warning);
        if (_result.Notes.Count > 0)
        {
            info.Add(""); info.Add("По файлу:");
            info.AddRange(_result.Notes);
        }
        token.ThrowIfCancellationRequested();
        return string.Join(Environment.NewLine, info);
    }

    private void Confirm()
    {
        if (!_table.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !_table.CommitEdit(DataGridEditingUnit.Row, true)) return;
        var selections = Choices;
        if (selections.Count == 0)
        {
            MessageBox.Show(this, "Отметь хотя бы один документ.", "Импорт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ObjectId is null && NewObjectName.Length == 0)
        {
            MessageBox.Show(this, "Укажи название новой карточки.", "Импорт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (selections.Any(c => c.Kind == "dop" && c.BaseContractId is null))
        {
            MessageBox.Show(this, "Для допсоглашений явно выбери документ-основание. Все выбранные ДС получат это основание.", "Импорт",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ObjectId is long id && _alreadyLoaded.TryGetValue(id, out var sheets) &&
            selections.Any(c => sheets.Contains(Path.GetFileName(_result.FileName) + "|" + c.Block.Sheet)))
        {
            MessageBox.Show(this, "Один из отмеченных листов уже был импортирован в выбранную карточку.", "Повторный импорт",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (selections.Any(c => !c.Block.Ok) &&
            MessageBox.Show(this, "Для некоторых документов суммы не сходятся с файлом или нет итоговых строк.\n" +
                "Импортировать выбранные блоки?", "Проверка сумм", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (selections.Any(c => (c.Kind is "main" or "dop") && (c.CompanyId is not > 0 || !_companies.Any(company => company.Id == c.CompanyId))))
        {
            MessageBox.Show(this, "Выберите организацию приложений и основание ДС с указанной организацией.",
                "Реквизиты", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_legalTargets is not null)
        {
            try
            {
                var cards = CurrentLegalCards();
                var annexes = selections.Where(c => c.Kind == "main").ToArray();
                foreach (var annex in annexes) ExcelImportAgreementPolicy.ValidateLegalTarget(annex, cards);
                if (cards.Count == 0) ExcelImportAgreementPolicy.ValidateNewDraftBatch(annexes);
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, ex.Message, "Реквизиты договора", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        if (selections.Any(c => c.Kind is "main" or "dop"))
        {
            var details = selections.Where(c => c.Kind is "main" or "dop").Select(c =>
                $"{c.Name} · № {c.Number} от {c.Date} · {_companies.First(company => company.Id == c.CompanyId).Name}" +
                (c.AgreementId is long legalId ? $" · ID договора {legalId}" : ""));
            if (MessageBox.Show(this, "Проверьте реквизиты (НДС ДНС — из файла, прочие формы — по выбранной организации):\n" +
                string.Join("\n", details) + "\nПродолжить?", "Проверка реквизитов", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        DialogResult = true;
    }
}
