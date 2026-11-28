using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Own legal entities; edits do not rewrite historical contracts or replace their signatories.</summary>
public sealed class CompanyWindow : Window
{
    private readonly string _connectionString;
    private readonly ListBox _list = new() { Width = 220, DisplayMemberPath = "Name" };
    private readonly Dictionary<string, TextBox> _fields = [];
    private readonly Image _logoView = new() { Height = 86, Width = 250, Stretch = System.Windows.Media.Stretch.Uniform };
    private byte[]? _logo;
    private IReadOnlyList<SqlServerWorkspaceNavigation.CompanyEntry> _companies = [];
    private string _revision = "";
    private long _editingId;
    private bool _loading;
    private bool _dirty;
    private readonly ReferenceWindowOperation _operation = new();

    private static readonly (string Id, string Title, bool Multiline)[] Columns =
    [
        ("Name","Название",false), ("Address","Адрес",true), ("InnKpp","ИНН / КПП",false),
        ("Phone","Телефон",false), ("Email","E-mail",false), ("Vat","НДС, %",false),
        ("LegalFull","Для КС: полное юр. наименование",true),
        ("SignerPost","Для КС: должность подписанта",false),
        ("SignerName","Для КС: ФИО подписанта",false)
    ];

    public CompanyWindow(string connectionString)
    {
        _connectionString = connectionString;
        Title = "Наши организации"; Width = 860; Height = 620;
        MinWidth = 750; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WorkspaceWindowUi.ApplyChrome(this);
        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
        Content = root;
        var heading = WorkspaceWindowUi.Heading(Title);
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        var hint = WorkspaceWindowUi.Hint("Реквизиты выводятся в КП и КС. Существующие документы не переназначаются.");
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);

        var footer = WorkspaceWindowUi.Buttons(
            WorkspaceWindowUi.Button("Сохранить", async (_, _) => await SaveAsync()),
            WorkspaceWindowUi.Button("Обновить", async (_, _) => await LoadAsync()),
            WorkspaceWindowUi.Button("Закрыть", (_, _) => Close()));
        footer.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetColumn(left, 0); layout.Children.Add(left);
        var leftButtons = WorkspaceWindowUi.Buttons(
            WorkspaceWindowUi.Button("Добавить", (_, _) => New()),
            WorkspaceWindowUi.Button("Удалить", async (_, _) => await DeleteAsync()));
        DockPanel.SetDock(leftButtons, Dock.Bottom); left.Children.Add(leftButtons);
        left.Children.Add(_list);

        // The left pane already owns the 12-DIP inter-pane gap.
        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(195) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < Columns.Length; i++)
        {
            var col = Columns[i];
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = col.Title, Margin = new Thickness(0, 8, 9, 8),
                VerticalAlignment = VerticalAlignment.Top, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(label, i); Grid.SetColumn(label, 0); form.Children.Add(label);
            var entry = new TextBox { Margin = new Thickness(0, 4, 0, 4),
                AcceptsReturn = col.Multiline, TextWrapping = col.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MinHeight = col.Multiline ? 60 : 32, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _fields[col.Id] = entry;
            entry.TextChanged += (_, _) => { if (!_loading) _dirty = true; };
            Grid.SetRow(entry, i); Grid.SetColumn(entry, 1); form.Children.Add(entry);
        }
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var logoCaption = new TextBlock { Text = "Логотип КП", Margin = new Thickness(0, 8, 9, 8) };
        Grid.SetColumn(logoCaption, 0); Grid.SetRow(logoCaption, Columns.Length);
        form.Children.Add(logoCaption);
        var logoControls = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        logoControls.Children.Add(ReferenceWindowUi.LogoPreview(_logoView));
        var chooseLogo=WorkspaceWindowUi.Button("Выбрать…", async (_,_)=>await PickLogoAsync());
        WorkspaceWindowUi.StyleButton(chooseLogo,icon:"IconLabelUpload");
        var clearLogo=WorkspaceWindowUi.Button("Убрать", (_,_)=>
        {
            _logo = null; _logoView.Source = null; _dirty = true;
        });
        WorkspaceWindowUi.StyleButton(clearLogo,icon:"IconLabelX");
        var logoButtons=WorkspaceWindowUi.Buttons(chooseLogo,clearLogo);
        logoButtons.HorizontalAlignment=HorizontalAlignment.Left;logoButtons.Margin=new Thickness(0,6,0,0);
        logoControls.Children.Add(logoButtons);
        Grid.SetColumn(logoControls, 1); Grid.SetRow(logoControls, Columns.Length);
        form.Children.Add(logoControls);
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1); layout.Children.Add(scroll);
        root.Children.Add(layout);

        _list.SelectionChanged += (_, _) =>
        {
            if (_loading || _operation.IsBusy || _list.SelectedItem is not SqlServerWorkspaceNavigation.CompanyEntry company) return;
            if (_dirty)
            {
                MessageBox.Show(this, "Сначала сохраните изменения организации или отмените их кнопкой «Обновить».",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
                _loading = true;
                _list.SelectedItem = _companies.FirstOrDefault(c => c.Id == _editingId);
                _loading = false;
                return;
            }
            Show(company);
        };
        Closing += (_, e) =>
        {
            if (_operation.IsBusy) { e.Cancel = true; return; }
            if (_dirty && MessageBox.Show(this, "Закрыть без сохранения изменённых реквизитов?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                e.Cancel = true;
        };
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync(long? selectId = null)
    {
        using var operation = _operation.TryEnter(this);
        if (operation is null) return;
        await LoadCoreAsync(selectId);
    }

    private async Task LoadCoreAsync(long? selectId = null)
    {
        if (_dirty)
        {
            if (MessageBox.Show(this, "Сбросить несохранённые изменения организации?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        _revision = "";
        try
        {
            var tuple = await Task.Run(() =>
            {
                SqlServerWorkspace.RequirePilotDatabase(_connectionString);
                using var conn = SqlServerDatabase.Open(_connectionString);
                var before = SqlServerDatabase.Revision(conn);
                var rows = SqlServerWorkspaceNavigation.ReadCompanies(conn);
                if (string.IsNullOrWhiteSpace(before) || before != SqlServerDatabase.Revision(conn))
                    throw new System.Data.DBConcurrencyException("Организации изменены другим клиентом. Обновите список.");
                return (Rows: rows, Revision: before);
            });
            _loading = true;
            _dirty = false;
            _companies = tuple.Rows;
            _revision = tuple.Revision;
            _list.ItemsSource = _companies;
            _list.SelectedItem = _companies.FirstOrDefault(c => c.Id == (selectId ?? _editingId)) ??
                _companies.FirstOrDefault();
            _loading = false;
            if (_list.SelectedItem is SqlServerWorkspaceNavigation.CompanyEntry selected) Show(selected);
            else New();
        }
        catch (Exception ex) { _loading = false; _revision = ""; WorkspaceWindowUi.Error(this, ex, "Организации"); }
    }

    private void Show(SqlServerWorkspaceNavigation.CompanyEntry entry)
    {
        _loading = true;
        _editingId = entry.Id;
        _fields["Name"].Text = entry.Name; _fields["Address"].Text = entry.Address;
        _fields["InnKpp"].Text = entry.InnKpp; _fields["Phone"].Text = entry.Phone;
        _fields["Email"].Text = entry.Email; _fields["Vat"].Text = entry.VatRate.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));
        _fields["LegalFull"].Text = entry.LegalFull; _fields["SignerPost"].Text = entry.SignerPost;
        _fields["SignerName"].Text = entry.SignerName;
        _logo = entry.Logo?.ToArray();
        RenderLogo();
        _dirty = false;
        _loading = false;
    }

    private void New()
    {
        if (_dirty)
        {
            MessageBox.Show(this, "Сначала сохраните изменения или нажмите «Обновить».",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _loading = true; _list.SelectedIndex = -1;
        _editingId = 0;
        foreach (var field in _fields.Values) field.Clear();
        _fields["Vat"].Text = "22";
        _logo = null; _logoView.Source = null;
        _dirty = false; _loading = false;
        _fields["Name"].Focus();
    }

    private void RenderLogo()
    {
        _logoView.Source = null;
        if (_logo is not { Length: > 0 }) return;
        try
        {
            using var stream = new MemoryStream(_logo, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            _logoView.Source = bitmap;
        }
        catch (Exception)
        {
            // Do not destroy original binary bytes when a legacy logo is malformed.
            _logoView.Source = null;
        }
    }

    private async Task PickLogoAsync()
    {
        using var operation = _operation.TryEnter(this);
        if (operation is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Логотип для КП",
            Filter = "Изображения (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var path = dialog.FileName;
            var imported = await Task.Run(() =>
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length > 5_000_000)
                    throw new ArgumentException("Выберите изображение размером не более 5 МБ.");
                var bytes = new byte[checked((int)file.Length)];
                file.ReadExactly(bytes);
                using var stream = new MemoryStream(bytes, writable: false);
                var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
                    throw new ArgumentException("Файл не является корректным изображением.");
                bitmap.Freeze();
                return (Bytes: bytes, Bitmap: bitmap);
            });
            _logo = imported.Bytes;
            _logoView.Source = imported.Bitmap;
            _dirty = true;
        }
        catch (Exception ex) { WorkspaceWindowUi.Error(this, ex, "Логотип"); }
    }

    private async Task SaveAsync()
    {
        using var operation = _operation.TryEnter(this);
        if (operation is null || !RequireRevision()) return;
        var revision = _revision;
        var rawVat = _fields["Vat"].Text.Trim().Replace('.', ',');
        if (!decimal.TryParse(rawVat, NumberStyles.Number, CultureInfo.GetCultureInfo("ru-RU"), out var vat))
        {
            MessageBox.Show(this, "Укажите корректный НДС, %.", Title);
            return;
        }
        var previous = _companies.FirstOrDefault(c => c.Id == _editingId);
        var company = new SqlServerWorkspaceNavigation.CompanyEntry(
            _editingId, _fields["Name"].Text.Trim(), _fields["Address"].Text,
            _fields["InnKpp"].Text, _fields["Phone"].Text, _fields["Email"].Text, vat,
            _fields["LegalFull"].Text, _fields["SignerPost"].Text, _fields["SignerName"].Text,
            previous?.Order ?? _companies.Count, previous?.Uses ?? 0)
        { Logo = _logo?.ToArray() };
        try
        {
            var saved = await Task.Run(() =>
                SqlServerWorkspaceReferenceWrites.SaveCompany(_connectionString, company, revision));
            _dirty = false;
            _editingId = saved;
            await LoadCoreAsync(saved);
        }
        catch (Exception ex)
        {
            _revision = "";
            WorkspaceWindowUi.Error(this, new InvalidOperationException(ex.Message +
                "\nЗапись не подтверждена. Перед повтором обновите список и проверьте данные.", ex), "Сохранение организации");
        }
    }

    private bool RequireRevision()
    {
        if (!string.IsNullOrWhiteSpace(_revision)) return true;
        MessageBox.Show(this, "Сначала обновите список и проверьте состояние записи на сервере.", Title);
        return false;
    }

    private async Task DeleteAsync()
    {
        using var operation = _operation.TryEnter(this);
        if (operation is null || !RequireRevision()) return;
        var revision = _revision;
        if (_dirty)
        {
            MessageBox.Show(this, "Сначала сохраните или отмените изменения.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_editingId == 0) return;
        var item = _companies.FirstOrDefault(c => c.Id == _editingId);
        if (item is null) return;
        if (item.Uses > 0 || item.AgreementUses > 0)
        {
            MessageBox.Show(this,
                $"«{item.Name}»: документов — {item.Uses}, юридических договоров — {item.AgreementUses}. Удаление запрещено.",
                "Организации", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"Удалить «{item.Name}»?", "Организации",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            await Task.Run(() =>
                SqlServerWorkspaceReferenceWrites.DeleteCompany(_connectionString, item.Id, revision));
            _editingId = 0;
            await LoadCoreAsync();
        }
        catch (Exception ex)
        {
            _revision = "";
            WorkspaceWindowUi.Error(this, new InvalidOperationException(ex.Message +
                "\nУдаление не подтверждено. Перед повтором обновите список и проверьте данные.", ex), "Удаление организации");
        }
    }
}

/// <summary>Customer tags: immutable keys, editable names, six Python behaviour flags, usage checks.</summary>
public sealed class TagsWindow : Window
{
    private readonly string _connectionString;
    private readonly ListBox _list = new() { Width = 230, DisplayMemberPath = "Name" };
    private readonly TextBox _name = new() { MinHeight = 32 };
    private readonly TextBox _note = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        Height = 60, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _usage = WorkspaceWindowUi.Hint("");
    private readonly Dictionary<string, CheckBox> _rules = [];
    private IReadOnlyList<SqlServerWorkspaceNavigation.TagEntry> _tags = [];
    private string _revision = "";
    private long _editingId;
    private bool _loading;
    private bool _dirty;
    private readonly ReferenceWindowOperation _operation = new();

    private static readonly (string Key, string Caption)[] Rules =
    [
        ("vat_top", "НДС сверху — цены без НДС; внизу итог и НДС"),
        ("num_manual", "Нумерация по оригиналу заказчика"),
        ("basis", "Столбец «Обоснование»"),
        ("ks_dns", "Формы КС-2 / КС-3 ДНС"),
        ("ds_per_act", "Дополнительное соглашение в каждом акте"),
        ("round_rows", "Округлять строки до копеек (Excel)")
    ];

    public TagsWindow(string connectionString)
    {
        _connectionString = connectionString;
        Title = "Теги заказчиков"; Width = 830; Height = 560;
        MinWidth = 700; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WorkspaceWindowUi.ApplyChrome(this);
        var root = new DockPanel { Margin = WorkspaceWindowUi.Pad };
        Content = root;
        var heading = WorkspaceWindowUi.Heading(Title);
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        var hint = WorkspaceWindowUi.Hint(
            "Тег задаёт правила формы заказчика. На объекте он применяется к новым документам; уже созданные не меняются.");
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);

        var buttons = WorkspaceWindowUi.Buttons(
            WorkspaceWindowUi.Button("Сохранить", async (_, _) => await SaveAsync()),
            WorkspaceWindowUi.Button("Обновить", async (_, _) => await LoadAsync()),
            WorkspaceWindowUi.Button("Закрыть", (_, _) => Close()));
        buttons.Margin = new Thickness(0, 9, 0, 0);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(242) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetColumn(left, 0); body.Children.Add(left);
        var manage = WorkspaceWindowUi.Buttons(
            WorkspaceWindowUi.Button("Новый тег", (_, _) => New()),
            WorkspaceWindowUi.Button("Удалить", async (_, _) => await DeleteAsync()));
        DockPanel.SetDock(manage, Dock.Bottom); left.Children.Add(manage);
        left.Children.Add(_list);

        var right = new StackPanel();
        right.Children.Add(DynamicFormLayout.Secondary("Название", "LayoutFormGroupHeadingGap"));
        right.Children.Add(_name);
        right.Children.Add(DynamicFormLayout.Secondary("Правила", "LayoutReferenceGroupHeadingGap"));
        foreach (var (key, caption) in Rules)
        {
            var cb = new CheckBox { Content = new TextBlock { Text=caption, TextWrapping=TextWrapping.Wrap }, Margin = new Thickness(0, 0, 0, 7) };
            System.Windows.Automation.AutomationProperties.SetName(cb,caption);
            _rules[key] = cb; right.Children.Add(cb);
        }
        right.Children.Add(DynamicFormLayout.Secondary("Примечание", "LayoutReferenceNoteHeadingGap"));
        right.Children.Add(_note);
        _usage.Margin = new Thickness(0, 8, 0, 0);
        right.Children.Add(_usage);
        _name.TextChanged += (_, _) => { if (!_loading) _dirty = true; };
        _note.TextChanged += (_, _) => { if (!_loading) _dirty = true; };
        foreach (var cb in _rules.Values)
        {
            cb.Checked += (_, _) => { if (!_loading) _dirty = true; };
            cb.Unchecked += (_, _) => { if (!_loading) _dirty = true; };
        }
        var scroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1); body.Children.Add(scroll);
        root.Children.Add(body);

        _list.SelectionChanged += (_, _) =>
        {
            if (_loading || _operation.IsBusy || _list.SelectedItem is not SqlServerWorkspaceNavigation.TagEntry tag) return;
            if (_dirty)
            {
                MessageBox.Show(this, "Сначала сохраните изменения тега или нажмите «Обновить».",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
                _loading = true;
                _list.SelectedItem = _tags.FirstOrDefault(t => t.Id == _editingId);
                _loading = false;
                return;
            }
            Show(tag);
        };
        Closing += (_, e) =>
        {
            if (_operation.IsBusy) { e.Cancel = true; return; }
            if (_dirty && MessageBox.Show(this, "Закрыть без сохранения изменений тега?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                e.Cancel = true;
        };
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync(long? selectId = null)
    {
        using var operation = _operation.TryEnter(this);
        if (operation is null) return;
        await LoadCoreAsync(selectId);
    }

    private async Task LoadCoreAsync(long? selectId = null)
    {
        if (_dirty)
        {
            if (MessageBox.Show(this, "Сбросить несохранённые изменения тега?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        _revision = "";
        try
        {
            var tuple = await Task.Run(() =>
            {
                SqlServerWorkspace.RequirePilotDatabase(_connectionString);
                using var conn = SqlServerDatabase.Open(_connectionString);
                var before = SqlServerDatabase.Revision(conn);
                var rows = SqlServerWorkspaceNavigation.ReadTags(conn);
                if (string.IsNullOrWhiteSpace(before) || before != SqlServerDatabase.Revision(conn))
                    throw new System.Data.DBConcurrencyException("Теги изменены другим клиентом. Обновите список.");
                return (Rows: rows, Revision: before);
            });
            _loading = true;
            _dirty = false;
            _tags = tuple.Rows; _revision = tuple.Revision;
            _list.ItemsSource = _tags;
            _list.SelectedItem = _tags.FirstOrDefault(t => t.Id == (selectId ?? _editingId)) ??
                _tags.FirstOrDefault();
            _loading = false;
            if (_list.SelectedItem is SqlServerWorkspaceNavigation.TagEntry selected) Show(selected);
            else New();
        }
        catch (Exception ex) { _loading = false; _revision = ""; WorkspaceWindowUi.Error(this, ex, "Теги"); }
    }

    private void Show(SqlServerWorkspaceNavigation.TagEntry tag)
    {
        _loading = true;
        _editingId = tag.Id; _name.Text = tag.Name; _note.Text = tag.Note;
        using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(tag.Rules) ? "{}" : tag.Rules);
        foreach (var (key, check) in _rules)
            check.IsChecked = json.RootElement.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.True;
        _usage.Text = $"Документов с тегом: {tag.Documents}; объектов: {tag.Objects}";
        _dirty = false;
        _loading = false;
    }

    private void New()
    {
        if (_dirty)
        {
            MessageBox.Show(this, "Сначала сохраните изменения или нажмите «Обновить».",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _loading = true; _list.SelectedIndex = -1;
        _editingId = 0; _name.Clear(); _note.Clear();
        foreach (var checkbox in _rules.Values) checkbox.IsChecked = false;
        _usage.Text = "Новый тег: сохраните, чтобы назначить его объекту или документу.";
        _dirty = false; _loading = false;
        _name.Focus();
    }

    private async Task SaveAsync()
    {
        using var operation = _operation.TryEnter(this);
        if (operation is null || !RequireRevision()) return;
        var revision = _revision;
        var old = _tags.FirstOrDefault(t => t.Id == _editingId);
        var rules = JsonSerializer.Serialize(_rules.ToDictionary(pair => pair.Key,
            pair => pair.Value.IsChecked == true));
        var entry = new SqlServerWorkspaceNavigation.TagEntry(
            _editingId, old?.Key ?? "", _name.Text.Trim(), rules, _note.Text,
            old?.Documents ?? 0, old?.Objects ?? 0);
        try
        {
            var saved = await Task.Run(() =>
                SqlServerWorkspaceReferenceWrites.SaveTag(_connectionString, entry, revision));
            _dirty = false;
            _editingId = saved;
            await LoadCoreAsync(saved);
        }
        catch (Exception ex)
        {
            _revision = "";
            WorkspaceWindowUi.Error(this, new InvalidOperationException(ex.Message +
                "\nЗапись не подтверждена. Перед повтором обновите список и проверьте данные.", ex), "Сохранение тега");
        }
    }

    private bool RequireRevision()
    {
        if (!string.IsNullOrWhiteSpace(_revision)) return true;
        MessageBox.Show(this, "Сначала обновите список и проверьте состояние записи на сервере.", Title);
        return false;
    }

    private async Task DeleteAsync()
    {
        using var operation = _operation.TryEnter(this);
        if (operation is null || !RequireRevision()) return;
        var revision = _revision;
        if (_dirty)
        {
            MessageBox.Show(this, "Сначала сохраните или отмените изменения.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var item = _tags.FirstOrDefault(t => t.Id == _editingId);
        if (item is null) return;
        if (item.Documents > 0)
        {
            MessageBox.Show(this, $"Тег используется в {item.Documents} документах. Сначала уберите его в свойствах документов.",
                "Удаление тега", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this,
            $"Удалить тег «{item.Name}»? У {item.Objects} объектов будет снято наследование тега.",
            "Удаление тега", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            await Task.Run(() => SqlServerWorkspaceReferenceWrites.DeleteTag(
                _connectionString, item.Id, revision));
            _editingId = 0; await LoadCoreAsync();
        }
        catch (Exception ex)
        {
            _revision = "";
            WorkspaceWindowUi.Error(this, new InvalidOperationException(ex.Message +
                "\nУдаление не подтверждено. Перед повтором обновите список и проверьте данные.", ex), "Удаление тега");
        }
    }
}
