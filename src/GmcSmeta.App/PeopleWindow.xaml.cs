using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Win32;

namespace GmcSmeta.App;

/// <summary>Python PeopleDialog dual-mode parity. The menu owner only calls the public constructor.</summary>
public partial class PeopleWindow : Window
{
    private readonly SqlServerPeopleAccess _db;
    private readonly long? _objectId;
    private AccessSite? _site;
    private List<AccessCompany> _companies = new();
    private List<PeoplePerson> _people = new();
    private List<PeopleCar> _cars = new();
    private List<HistoryOption> _history = new();
    private readonly HashSet<long> _checkedPeople = new();
    private readonly HashSet<long> _checkedCars = new();
    private string _revision = "";
    private bool _loading;
    private bool _defaultsApplied;
    private bool _registrationUncertain;
    private bool IsAccess => _objectId.HasValue;

    private sealed record HistoryOption(AccessHistory History, string Label);

    public PeopleWindow(string connectionString, long? objectId = null)
    {
        _loading = true;
        InitializeComponent();
        _objectId = objectId;
        _db = new SqlServerPeopleAccess(connectionString);
        Title = IsAccess ? "Список на допуск" : "Люди и автомобили";
        TitleText.Text = Title;
        SubtitleText.Text = IsAccess ?
            "Отметьте людей и автомобили для выбранного объекта. История каждого объекта независима." :
            "Общая база для всех объектов. Неактивные остаются в ранее выданных списках.";
        if (IsAccess)
        {
            _site = _db.Site(objectId!.Value);
            SubtitleText.Text = _site.Name + " · " + SubtitleText.Text;
            HistoryPanel.Visibility = Visibility.Visible;
            AccessOptions.Visibility = Visibility.Visible;
            OrdersButton.Visibility = Visibility.Visible;
            ExportButton.Visibility = Visibility.Visible;
            PeopleAll.Visibility = PeopleNone.Visibility = Visibility.Visible;
            CarsAll.Visibility = CarsNone.Visibility = Visibility.Visible;
            IntroBox.Text = PeopleRules.DefaultIntro(_site);
            _companies = _db.Companies().ToList();
            CompanyBox.ItemsSource = _companies;
            CompanyBox.SelectedItem = _companies.FirstOrDefault(x =>
                x.Name.Contains("ГМЦ", StringComparison.OrdinalIgnoreCase)) ?? _companies.FirstOrDefault();
        }
        else
        {
            PeopleSelectedColumn.Visibility = Visibility.Collapsed;
            CarsSelectedColumn.Visibility = Visibility.Collapsed;
        }
        _loading = false;
        RefreshHistory();
        Reload();
        Loaded += (_, _) => WindowState = WindowState.Maximized;
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (DBConcurrencyException)
        {
            MessageBox.Show(this, "Данные изменились в другом окне. Справочник перечитан; повторите действие.",
                "Конфликт изменений", MessageBoxButton.OK, MessageBoxImage.Warning);
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Люди и автомобили", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void RememberChecks()
    {
        if (!IsAccess || _loading) return;
        foreach (var row in _people)
            if (row.Selected) _checkedPeople.Add(row.Id); else _checkedPeople.Remove(row.Id);
        foreach (var row in _cars)
            if (row.Selected) _checkedCars.Add(row.Id); else _checkedCars.Remove(row.Id);
    }
    private void Reload() => Reload(rememberChecks: true);
    private void Reload(bool rememberChecks)
    {
        if (rememberChecks) RememberChecks();
        var selectedP = (PeopleGrid.SelectedItem as PeoplePerson)?.Id;
        var selectedC = (CarsGrid.SelectedItem as PeopleCar)?.Id;
        _revision = "";
        var data = _db.Load(ShowInactive.IsChecked == true);
        _revision = data.Revision;
        _people = data.People.ToList();
        _cars = data.Cars.ToList();
        if (IsAccess)
        {
            // Python first-ever access list selects all active employees/vehicles.
            if (_history.Count == 0 && !_defaultsApplied)
            {
                foreach (var p in _people.Where(p => p.Active)) _checkedPeople.Add(p.Id);
                foreach (var car in _cars.Where(c => c.Active)) _checkedCars.Add(car.Id);
                _defaultsApplied = true;
            }
            foreach (var p in _people) p.Selected = _checkedPeople.Contains(p.Id);
            foreach (var c in _cars) c.Selected = _checkedCars.Contains(c.Id);
        }
        _loading = true;
        PeopleGrid.ItemsSource = _people;
        CarsGrid.ItemsSource = _cars;
        PeopleGrid.SelectedItem = _people.FirstOrDefault(x => x.Id == selectedP);
        CarsGrid.SelectedItem = _cars.FirstOrDefault(x => x.Id == selectedC);
        _loading = false;
        ApplyFilter();
    }
    private void ApplyFilter()
    {
        var query = PeopleRules.Clean(SearchBox.Text);
        foreach (var (view, person) in new[] { (CollectionViewSource.GetDefaultView(PeopleGrid.ItemsSource), true),
            (CollectionViewSource.GetDefaultView(CarsGrid.ItemsSource), false) })
        {
            if (view is null) continue;
            view.Filter = row => person
                ? row is PeoplePerson p && (p.Fio + " " + p.Post + " " + p.Birth + " " + p.Note)
                    .Contains(query, StringComparison.CurrentCultureIgnoreCase)
                : row is PeopleCar c && (c.Model + " " + c.Plate + " " + c.Note)
                    .Contains(query, StringComparison.CurrentCultureIgnoreCase);
            view.Refresh();
        }
        UpdateCounts();
    }
    private void UpdateCounts()
    {
        PeopleCount.Text = IsAccess
            ? $"в списке {_people.Count(x => x.Selected)} из {_people.Count}"
            : $"работают {_people.Count(x => x.Active)}, показано {_people.Count}";
        CarsCount.Text = IsAccess
            ? $"в списке {_cars.Count(x => x.Selected)} из {_cars.Count}"
            : $"{_cars.Count} авто";
        if (IsAccess)
            ExportButton.Content = $"Сформировать список (Excel) — {_people.Count(x => x.Selected)} чел., " +
                $"{_cars.Count(x => x.Selected)} авто";
    }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loading && PeopleGrid is not null) ApplyFilter();
    }
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading && PeopleGrid is not null) Run(Reload);
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateCounts();
    private void People_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedPerson() is not null) PersonEdit_Click(sender, e);
    }
    private void Cars_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedCar() is not null) CarEdit_Click(sender, e);
    }
    private void People_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || e.Key is not (Key.Up or Key.Down)) return;
        Move(true, e.Key == Key.Up ? -1 : +1);
        e.Handled = true;
    }
    private void Cars_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || e.Key is not (Key.Up or Key.Down)) return;
        Move(false, e.Key == Key.Up ? -1 : +1);
        e.Handled = true;
    }
    private void CheckCell_Changed(object sender, EventArgs e)
    {
        if (_loading || !IsAccess) return;
        // WPF check-box edits commit on focus change; a dispatcher update reflects new selections.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsAccess)
            {
                RememberChecks();
                UpdateCounts();
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
    private PeoplePerson? SelectedPerson() => PeopleGrid.SelectedItem as PeoplePerson;
    private PeopleCar? SelectedCar() => CarsGrid.SelectedItem as PeopleCar;

    private void PersonAdd_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var values = EditFields("Добавить человека", new (string, string)[]
        {
            ("ФИО", ""), ("Должность", "монтажник вент.систем"), ("Дата рождения", ""),
            ("Примечание", ""), ("ФИО — кого (пусто = автоматически)", ""),
            ("Должность — кого (пусто = автоматически)", "")
        });
        if (values is null) return;
        var result = _db.SavePerson(new PeoplePerson(0, values[0], values[1], values[2], values[3],
            values[4], values[5], true, 0), _revision);
        _revision = result.Revision;
        if (IsAccess) _checkedPeople.Add(result.Id);
        Reload();
        PeopleGrid.SelectedItem = _people.FirstOrDefault(x => x.Id == result.Id);
    });
    private void PersonEdit_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var p = SelectedPerson();
        if (p is null) return;
        var values = EditFields("Редактировать сотрудника", new (string, string)[]
        {
            ("ФИО", p.Fio), ("Должность", p.Post), ("Дата рождения", p.Birth),
            ("Примечание", p.Note), ("ФИО — кого (пусто = автоматически)", p.FioGen),
            ("Должность — кого (пусто = автоматически)", p.PostGen)
        });
        if (values is null) return;
        _revision = _db.SavePerson(p with { Fio = values[0], Post = values[1], Birth = values[2],
            Note = values[3], FioGen = values[4], PostGen = values[5] }, _revision).Revision;
        Reload();
    });
    private void CarAdd_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var values = EditFields("Добавить автомобиль", new (string, string)[]
        { ("Марка / модель", ""), ("Госномер", ""), ("Примечание", "") });
        if (values is null) return;
        var result = _db.SaveCar(new PeopleCar(0, values[0], values[1], values[2], true, 0), _revision);
        _revision = result.Revision;
        if (IsAccess) _checkedCars.Add(result.Id);
        Reload();
        CarsGrid.SelectedItem = _cars.FirstOrDefault(x => x.Id == result.Id);
    });
    private void CarEdit_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var c = SelectedCar();
        if (c is null) return;
        var values = EditFields("Редактировать автомобиль", new (string, string)[]
            { ("Марка / модель", c.Model), ("Госномер", c.Plate), ("Примечание", c.Note) });
        if (values is null) return;
        _revision = _db.SaveCar(c with { Model = values[0], Plate = values[1], Note = values[2] },
            _revision).Revision;
        Reload();
    });
    private (long Id, bool Active, string Name)[] SelectedEntries(bool person) =>
        person ? PeopleGrid.SelectedItems.OfType<PeoplePerson>()
            .Select(p => (p.Id, p.Active, p.Fio)).ToArray() :
            CarsGrid.SelectedItems.OfType<PeopleCar>()
            .Select(c => (c.Id, c.Active, c.Model + " " + c.Plate)).ToArray();

    private bool IsMenuSnapshot(bool person, long[] ids, string revision) =>
        !_loading && !string.IsNullOrWhiteSpace(revision) && _revision == revision &&
        SelectedEntries(person).Select(p => p.Id).OrderBy(id => id).SequenceEqual(ids);

    private void People_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        OpenEntryMenu(true, e);
    private void Cars_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        OpenEntryMenu(false, e);

    private void OpenEntryMenu(bool person, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (_loading || string.IsNullOrWhiteSpace(_revision)) return;
        var grid = person ? PeopleGrid : CarsGrid;
        if (person ? ContextMenuTarget.SelectRow<PeoplePerson>(grid, e) is null :
            ContextMenuTarget.SelectRow<PeopleCar>(grid, e) is null) return;
        var entries = SelectedEntries(person);
        if (entries.Length == 0) return;
        var ids = entries.Select(p => p.Id).OrderBy(id => id).ToArray();
        var revision = _revision;
        var menu = new ContextMenu { PlacementTarget = grid,
            Placement = e.CursorLeft < 0 && e.CursorTop < 0
                ? System.Windows.Controls.Primitives.PlacementMode.Bottom
                : System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        void Add(string title, string glyph, Action action, bool enabled = true, bool danger = false)
        {
            var item = ContextMenuIcons.Create(title, glyph, enabled, danger: danger);
            item.Click += (_, _) =>
            {
                if (IsMenuSnapshot(person, ids, revision)) action();
                else MessageBox.Show(this, "Выбор изменился. Откройте меню снова.", Title);
            };
            menu.Items.Add(item);
        }
        Add("Не работает у нас (скрыть из новых списков)", "X",
            () => ChangeActive(person, entries.Where(p => p.Active).Select(p => p.Id).ToArray(), false, revision),
            entries.Any(p => p.Active));
        if (ShowInactive.IsChecked == true)
            Add("Снова работает", "Check",
                () => ChangeActive(person, entries.Where(p => !p.Active).Select(p => p.Id).ToArray(), true, revision),
                entries.Any(p => !p.Active));
        menu.Items.Add(new Separator());
        Add("Удалить из базы…", "Trash", () => DeleteEntries(person, entries, revision), danger: true);
        menu.IsOpen = true;
    }

    private void ChangeActive(bool person, long[] ids, bool active, string revision) => Run(() =>
    {
        if (ids.Length == 0 || _revision != revision || _loading) return;
        _revision = _db.SetActiveBatch(person, ids, active, revision);
        if (!active)
        {
            RememberChecks();
            var checkedIds = person ? _checkedPeople : _checkedCars;
            foreach (var id in ids) checkedIds.Remove(id);
            Reload(rememberChecks: false);
        }
        else Reload();
    });

    private void ToggleSelectedActive(bool person)
    {
        var entries = SelectedEntries(person);
        if (entries.Length == 0) return;
        // Mixed selection: hide active entries, never toggle each independently.
        var active = entries.All(p => !p.Active);
        ChangeActive(person, entries.Where(p => p.Active != active).Select(p => p.Id).ToArray(),
            active, _revision);
    }
    private void PersonActive_Click(object sender, RoutedEventArgs e) => ToggleSelectedActive(true);
    private void CarActive_Click(object sender, RoutedEventArgs e) => ToggleSelectedActive(false);
    private void PersonDelete_Click(object sender, RoutedEventArgs e) =>
        DeleteEntries(true, SelectedEntries(true), _revision);
    private void CarDelete_Click(object sender, RoutedEventArgs e) =>
        DeleteEntries(false, SelectedEntries(false), _revision);

    private void DeleteEntries(bool person, (long Id, bool Active, string Name)[] entries,
        string revision) => Run(() =>
    {
        if (entries.Length == 0 || string.IsNullOrWhiteSpace(revision)) return;
        var ids = entries.Select(p => p.Id).OrderBy(id => id).ToArray();
        if (!IsMenuSnapshot(person, ids, revision)) return;
        var names = string.Join(", ", entries.Take(5).Select(p => p.Name)) +
            (entries.Length > 5 ? "…" : "");
        if (MessageBox.Show(this, $"Удалить из общей базы ({entries.Length}): {names}?\n\n" +
                "Записи, связанные с ранее сформированными списками и приказами, будут отключены, а не удалены. " +
                "Если сотрудник просто ушёл, лучше выбрать «Не работает у нас».",
                "Удаление из базы", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes ||
            !IsMenuSnapshot(person, ids, revision)) return;
        var result = _db.DeleteOrDeactivateBatch(person, ids, revision);
        _revision = result.Revision;
        RememberChecks();
        var checkedIds = person ? _checkedPeople : _checkedCars;
        foreach (var id in ids) checkedIds.Remove(id);
        Reload(rememberChecks: false);
        MessageBox.Show(this, $"Удалено: {result.Deleted}. Отключено (есть история): {result.Deactivated}.",
            Title, MessageBoxButton.OK, MessageBoxImage.Information);
    });
    private void Move(bool person, int delta)
    {
        Run(() =>
        {
            if (person)
            {
                var selected = SelectedPerson();
                if (selected is null) return;
                var i = _people.FindIndex(p => p.Id == selected.Id);
                var j = i + delta;
                if (j < 0 || j >= _people.Count) return;
                (_people[i], _people[j]) = (_people[j], _people[i]);
                _revision = _db.Reorder(true, _people.Select(p => p.Id).ToArray(), _revision);
            }
            else
            {
                var selected = SelectedCar();
                if (selected is null) return;
                var i = _cars.FindIndex(p => p.Id == selected.Id);
                var j = i + delta;
                if (j < 0 || j >= _cars.Count) return;
                (_cars[i], _cars[j]) = (_cars[j], _cars[i]);
                _revision = _db.Reorder(false, _cars.Select(p => p.Id).ToArray(), _revision);
            }
            Reload();
        });
    }
    private void PersonUp_Click(object sender, RoutedEventArgs e) => Move(true, -1);
    private void PersonDown_Click(object sender, RoutedEventArgs e) => Move(true, +1);
    private void CarUp_Click(object sender, RoutedEventArgs e) => Move(false, -1);
    private void CarDown_Click(object sender, RoutedEventArgs e) => Move(false, +1);
    private void CheckAll(bool people, bool selected)
    {
        if (people)
            foreach (var p in _people.Where(p => IsVisible(PeopleGrid, p))) p.Selected = selected;
        else
            foreach (var c in _cars.Where(c => IsVisible(CarsGrid, c))) c.Selected = selected;
        RememberChecks();
        (people ? PeopleGrid : CarsGrid).Items.Refresh();
        UpdateCounts();
    }
    private static bool IsVisible(DataGrid grid, object row) =>
        CollectionViewSource.GetDefaultView(grid.ItemsSource)?.Filter?.Invoke(row) ?? true;
    private void PeopleAll_Click(object sender, RoutedEventArgs e) => CheckAll(true, true);
    private void PeopleNone_Click(object sender, RoutedEventArgs e) => CheckAll(true, false);
    private void CarsAll_Click(object sender, RoutedEventArgs e) => CheckAll(false, true);
    private void CarsNone_Click(object sender, RoutedEventArgs e) => CheckAll(false, false);

    private string[]? EditFields(string title, IReadOnlyList<(string Label, string Value)> fields)
    {
        var dialog = new Window
        {
            Title = title, Owner = this, Width = 660, SizeToContent = SizeToContent.Height,
            MaxHeight = SystemParameters.WorkArea.Height - 80, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White
        };
        var root = new StackPanel { Margin = new Thickness(22) };
        var boxes = new List<TextBox>();
        foreach (var (label, value) in fields)
        {
            root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 3) });
            var box = new TextBox { Text = value, Padding = new Thickness(7, 5, 7, 5),
                MinWidth = 500 };
            root.Children.Add(box);
            boxes.Add(box);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = "Сохранить", Padding = new Thickness(16, 7, 16, 7) };
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(16, 7, 16, 7),
            Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        cancel.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        dialog.Content = new ScrollViewer { Content = root };
        if (dialog.ShowDialog() != true) return null;
        return boxes.Select(x => PeopleRules.Clean(x.Text)).ToArray();
    }

    private void Import_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var picker = new OpenFileDialog { Filter = "Excel (*.xlsx;*.xlsm)|*.xlsx;*.xlsm" };
        if (picker.ShowDialog(this) != true) return;
        var detected = PeopleDocuments.ReadImport(picker.FileName);
        var review = new PeopleImportPreviewWindow(picker.FileName, detected) { Owner = this };
        if (review.ShowDialog() != true) return;
        var preview = review.Preview;
        var resultImport = _db.Import(preview, _revision);
        // Reloading immediately prevents stale revision errors for a subsequent action.
        Reload();
        HintText.Text = $"Импорт: новых {resultImport.NewPeople} чел., {resultImport.NewCars} авто; " +
            $"существующих {resultImport.ExistingPeople} чел., {resultImport.ExistingCars} авто.";
        if (IsAccess)
        {
            // Matching on normalized keys selects the rows from the imported workbook.
            var pkeys = preview.People.Select(p => PeopleRules.PersonKey(p.Fio)).ToHashSet();
            var ckeys = preview.Cars.Select(c => PeopleRules.PlateKey(c.Plate)).ToHashSet();
            _checkedPeople.Clear(); _checkedCars.Clear();
            foreach (var p in _people.Where(p => pkeys.Contains(PeopleRules.PersonKey(p.Fio))))
                _checkedPeople.Add(p.Id);
            foreach (var c in _cars.Where(c => ckeys.Contains(PeopleRules.PlateKey(c.Plate))))
                _checkedCars.Add(c.Id);
            Reload(rememberChecks: false);
        }
    });

    private void RefreshHistory()
    {
        if (!IsAccess) return;
        _history = _db.History(_objectId!.Value, "list")
            .Select(h => new HistoryOption(h, $"{h.CreatedAt:dd.MM.yyyy HH:mm} · {h.User}")).ToList();
        _loading = true;
        HistoryBox.ItemsSource = _history;
        if (_history.Count > 0)
        {
            HistoryBox.SelectedIndex = 0;
            RestoreHistory(_history[0].History);
        }
        _loading = false;
    }
    private void RestoreHistory(AccessHistory history)
    {
        if (string.IsNullOrWhiteSpace(history.Data)) return;
        try
        {
            var pdata = new HashSet<long>(); var cdata = new HashSet<long>();
            using var json = JsonDocument.Parse(history.Data);
            var root = json.RootElement;
            if (root.TryGetProperty("People", out var pp) || root.TryGetProperty("people", out pp))
                if (pp.ValueKind == JsonValueKind.Array)
                    foreach (var p in pp.EnumerateArray())
                        if ((p.TryGetProperty("Id", out var pid) || p.TryGetProperty("id", out pid)) &&
                            pid.TryGetInt64(out var id)) pdata.Add(id);
            if (root.TryGetProperty("Cars", out var cc) || root.TryGetProperty("cars", out cc))
                if (cc.ValueKind == JsonValueKind.Array)
                    foreach (var car in cc.EnumerateArray())
                        if ((car.TryGetProperty("Id", out var cid) || car.TryGetProperty("id", out cid)) &&
                            cid.TryGetInt64(out var id)) cdata.Add(id);
            _checkedPeople.Clear(); _checkedCars.Clear();
            _checkedPeople.UnionWith(pdata); _checkedCars.UnionWith(cdata);
            if ((root.TryGetProperty("Intro", out var intro) || root.TryGetProperty("intro", out intro)) &&
                intro.ValueKind == JsonValueKind.String)
                IntroBox.Text = intro.GetString() ?? IntroBox.Text;
            if (root.TryGetProperty("CompanyId", out var company) &&
                company.ValueKind == JsonValueKind.Number && company.TryGetInt64(out var companyId))
                CompanyBox.SelectedItem = _companies.FirstOrDefault(x => x.Id == companyId);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            HintText.Text = "Запись истории содержит повреждённый JSON. Старый файл сохранён; текущие галочки не изменены.";
        }
    }
    private void History_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HistoryBox.SelectedItem is not HistoryOption option) return;
        Run(() => { RestoreHistory(option.History); Reload(rememberChecks: false); });
    }
    private void OpenHistory_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (HistoryBox.SelectedItem is not HistoryOption option) return;
        if (!File.Exists(option.History.File))
            throw new FileNotFoundException("Файл ранее созданного списка не найден. История осталась в SQL Server.");
        Process.Start(new ProcessStartInfo(option.History.File) { UseShellExecute = true });
    });
    private void Orders_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!_objectId.HasValue) return;
        var child = new OrdersWindow(_db, _objectId.Value) { Owner = this };
        child.ShowDialog();
        _site = _db.Site(_objectId.Value);
        Reload(); // orders update meta.rev; avoid using the stale revision for the next list.
    });
    private void Export_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (_site is null || _objectId is null || _registrationUncertain) return;
        if (!PeopleGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !PeopleGrid.CommitEdit(DataGridEditingUnit.Row, true) ||
            !CarsGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !CarsGrid.CommitEdit(DataGridEditingUnit.Row, true))
            throw new InvalidOperationException("Завершите редактирование списка допуска.");
        var revision = _revision;
        if (string.IsNullOrWhiteSpace(revision))
            throw new InvalidOperationException("Сначала обновите справочник.");
        RememberChecks();
        var people = _people.Where(p => p.Selected)
            .Select(p => new AccessPerson(p.Id, p.Fio, p.Post, p.Birth)).ToArray();
        var cars = _cars.Where(c => c.Selected)
            .Select(c => new AccessCar(c.Id, c.Model, c.Plate)).ToArray();
        if (people.Length == 0) { MessageBox.Show(this, "Отметьте хотя бы одного человека."); return; }
        var company = CompanyBox.SelectedItem as AccessCompany;
        var req = company is null ? "" :
            company.Name.Contains("ГМЦ", StringComparison.OrdinalIgnoreCase)
                ? PeopleDocuments.GmcRequisites
                : string.Join(" ", new[] { company.InnKpp,
                    string.IsNullOrWhiteSpace(company.Address) ? "" : "Юр.адрес: " + company.Address }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
        var signer = company is null ? "" :
            (string.IsNullOrWhiteSpace(company.SignerPost) ? "Генеральный директор" : company.SignerPost) +
            " " + company.Name + " " + company.SignerName;
        var picker = new SaveFileDialog
        {
            Title = "Сохранить список на допуск",
            Filter = "Excel (*.xlsx)|*.xlsx", DefaultExt = ".xlsx",
            FileName = PeopleRules.SafeFileName("Список людей " + _site.Name +
                " " + DateTime.Today.ToString("dd.MM.yyyy")) + ".xlsx",
            InitialDirectory = Directory.Exists(_site.Folder) ? _site.Folder :
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (picker.ShowDialog(this) != true) return;
        if (File.Exists(picker.FileName))
            throw new IOException("Файл уже существует. Выберите другое имя — перезапись не допускается.");
        if (revision != _revision || _db.Load(ShowInactive.IsChecked == true).Revision != revision ||
            _db.Site(_objectId.Value) != _site || !_db.Companies().SequenceEqual(_companies))
            throw new InvalidOperationException("Справочник, объект или реквизиты изменились. Перезапустите окно.");
        var operation = Guid.NewGuid();
        var snapshot = new AccessSnapshot(people, cars, IntroBox.Text, req, company?.Id,
            company?.Name ?? "", signer, operation);
        // No false history record if XLSX was not successfully saved.
        PeopleDocuments.ExportAccessList(picker.FileName, company, req, IntroBox.Text, people, cars, signer);
        try
        {
            _revision = _db.RegisterExport(_objectId.Value, "list", picker.FileName,
                JsonSerializer.Serialize(snapshot), operation, revision).Revision;
        }
        catch (Exception)
        {
            // COMMIT may be uncertain: retain the file and idempotency token, never delete blindly.
            _registrationUncertain = true;
            ExportButton.IsEnabled = false;
            HintText.Text = "Файл создан, но запись в журнал не подтверждена. Повторный экспорт не выполняйте.";
            throw;
        }
        RefreshHistory();
        HintText.Text = "Список создан и зарегистрирован в SQL Server.";
        Process.Start(new ProcessStartInfo(picker.FileName) { UseShellExecute = true });
    });
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
