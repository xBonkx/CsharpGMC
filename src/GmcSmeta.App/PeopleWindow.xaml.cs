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
public partial class PeopleView : UserControl
{
    private SqlServerPeopleAccess _db = null!;
    private CancellationTokenSource? _initialCancellation;
    private CancellationTokenSource? _reloadCancellation;
    private bool _closed;
    private readonly long? _objectId;
    private AccessSite? _site;
    private List<AccessCompany> _companies = new();
    private List<PeoplePerson> _people = new();
    private List<PeopleCar> _cars = new();
    private Dictionary<long, string> _peopleSearch = new();
    private Dictionary<long, string> _carSearch = new();
    private List<HistoryOption> _history = new();
    private readonly HashSet<long> _checkedPeople = new();
    private readonly HashSet<long> _checkedCars = new();
    private string _revision = "";
    private bool _loading;
    private bool _defaultsApplied;
    private bool _registrationUncertain;
    private bool _operationBusy;
    private bool _checkUpdateQueued;
    private bool IsAccess => _objectId.HasValue;

    private sealed record HistoryOption(AccessHistory History, string Label);
    internal string Title => IsAccess ? "Список на допуск" : "Люди и автомобили";
    private Window OwnerWindow => Window.GetWindow(this) ?? throw new InvalidOperationException("Справочник не открыт.");
    internal event Action? CloseRequested;
    internal bool CanRestart => !_operationBusy && !_inlinePending && !_loading &&
        !HasTextEditor(PeopleGrid) && !HasTextEditor(CarsGrid);

    private static bool HasTextEditor(DataGrid grid) =>
        grid.CurrentCell.IsValid && grid.CurrentCell.Column.GetCellContent(grid.CurrentCell.Item) is TextBox;

    public PeopleView(string connectionString, long? objectId = null)
    {
        _loading = true;
        InitializeComponent();
        _objectId = objectId;
        TitleText.Text = Title;
        SubtitleText.Text = IsAccess ?
            "Отметьте людей и автомобили для выбранного объекта. История каждого объекта независима." :
            "Общая база для всех объектов. Неактивные остаются в ранее выданных списках.";
        if (IsAccess)
        {
            HistoryPanel.Visibility = Visibility.Visible;
            AccessOptions.Visibility = Visibility.Visible;
            OrdersButton.Visibility = Visibility.Visible;
            ExportButton.Visibility = Visibility.Visible;
            PeopleAll.Visibility = PeopleNone.Visibility = Visibility.Visible;
            CarsAll.Visibility = CarsNone.Visibility = Visibility.Visible;
        }
        else
        {
            PeopleSelectedColumn.Visibility = Visibility.Collapsed;
            CarsSelectedColumn.Visibility = Visibility.Collapsed;
        }
        // No editing/export before the initial authoritative directory is available.
        var content = (UIElement)Content;
        content.IsEnabled = false;
        HintText.Text = "Загрузка справочника…";
        Loaded += async (_, _) =>
        {
            await InitializeAsync(connectionString, content);
        };
    }

    internal bool TryLeave()
    {
        if (_operationBusy || _inlinePending) return false;
        return _loading || (CommitChecks() && !_inlinePending);
    }

    internal void CloseView()
    {
        if (_closed) return;
        _closed = true;
        _initialCancellation?.Cancel();
        _reloadCancellation?.Cancel();
    }

    private async Task InitializeAsync(string connectionString, UIElement content)
    {
        if (_closed || !_loading || _initialCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _initialCancellation = cancellation;
        var token = cancellation.Token;
        var showInactive = ShowInactive.IsChecked == true;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var db = new SqlServerPeopleAccess(connectionString, token);
                token.ThrowIfCancellationRequested();
                var data = db.ReadInitialSnapshot(_objectId, showInactive, token);
                token.ThrowIfCancellationRequested();
                return (Db: db, Site: data.Site, Companies: data.Companies.ToList(),
                    History: data.History, Directory: data.Directory);
            }, token);
            if (_closed || token.IsCancellationRequested) return;
            HintText.Text = "Двойной щелчок или F2 — редактировать ячейку; Enter — сохранить, Esc — отменить.";
            _db = snapshot.Db;
            _site = snapshot.Site;
            if (_site is not null)
            {
                SubtitleText.Text = _site.Name + " · " + SubtitleText.Text;
                IntroBox.Text = PeopleRules.DefaultIntro(_site);
                _companies = snapshot.Companies;
                CompanyBox.ItemsSource = _companies;
                CompanyBox.SelectedItem = _companies.FirstOrDefault(x =>
                    x.Name.Contains("ГМЦ", StringComparison.OrdinalIgnoreCase)) ?? _companies.FirstOrDefault();
                ApplyHistory(snapshot.History);
            }
            ApplyDirectory(snapshot.Directory.People, snapshot.Directory.Cars, snapshot.Directory.Revision, null, null);
            content.IsEnabled = true;
        }
        catch (Exception ex)
        {
            if (_closed || token.IsCancellationRequested) return;
            _revision = "";
            MessageBox.Show(OwnerWindow, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            CloseRequested?.Invoke();
        }
        finally
        {
            _initialCancellation = null;
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_closed || _loading || _operationBusy) return;
        if (_inlinePending) return;
        if (string.IsNullOrWhiteSpace(_revision))
        {
            HintText.Text = "Нет актуальной ревизии справочника. Измените фильтр или откройте окно заново.";
            return;
        }
        if (!CommitChecks())
        {
            HintText.Text = "Завершите редактирование списка.";
            return;
        }
        if (_inlinePending) return; // Cell commit may have queued a directory write.
        RememberChecks();
        _operationBusy = true;
        var content = (UIElement)Content;
        content.IsEnabled = false;
        try { await action(); }
        catch (DBConcurrencyException)
        {
            try
            {
                await ReloadMutationAsync();
                MessageBox.Show(OwnerWindow, _registrationUncertain
                    ? "Файл списка уже создан. Проверьте запись журнала; не повторяйте экспорт."
                    : "Данные изменились в другом окне. Справочник перечитан; повторите действие.",
                    "Конфликт изменений", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(OwnerWindow, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(OwnerWindow, ex.Message, "Люди и автомобили", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationBusy = false;
            if (!_closed) content.IsEnabled = true;
            if (_registrationUncertain)
                HintText.Text = "Файл создан, но запись в журнал не подтверждена. Повторный экспорт не выполняйте.";
            else if (string.IsNullOrWhiteSpace(_revision))
                HintText.Text = "Запись или чтение не подтверждены. Проверьте данные и обновите справочник перед повтором.";
        }
    }

    private bool CommitChecks() =>
        PeopleGrid.CommitEdit(DataGridEditingUnit.Cell, true) &&
        PeopleGrid.CommitEdit(DataGridEditingUnit.Row, true) &&
        CarsGrid.CommitEdit(DataGridEditingUnit.Cell, true) &&
        CarsGrid.CommitEdit(DataGridEditingUnit.Row, true);

    private void RememberChecks()
    {
        if (!IsAccess || _loading) return;
        foreach (var row in _people)
            if (row.Selected) _checkedPeople.Add(row.Id); else _checkedPeople.Remove(row.Id);
        foreach (var row in _cars)
            if (row.Selected) _checkedCars.Add(row.Id); else _checkedCars.Remove(row.Id);
    }

    // Only the captured revision reaches SQL. No stale writes after an uncertain result.
    private async Task<T> MutateAsync<T>(Func<string,T> mutation)
    {
        var revision = _revision;
        _revision = "";
        return await Task.Run(() => mutation(revision));
    }

    private async Task ReloadMutationAsync(long? personId = null, long? carId = null)
    {
        var selectedP = personId ?? (PeopleGrid.SelectedItem as PeoplePerson)?.Id;
        var selectedC = carId ?? (CarsGrid.SelectedItem as PeopleCar)?.Id;
        var showInactive = ShowInactive.IsChecked == true;
        _revision = "";
        var data = await Task.Run(() => _db.Load(showInactive));
        if (!_closed) ApplyDirectory(data.People, data.Cars, data.Revision, selectedP, selectedC);
    }

    // Read-only refreshes may be cancelled; mutation readback is guarded until completion.
    private async Task ReloadReadOnlyAsync(AccessHistory? history = null)
    {
        if (_closed || _loading || _operationBusy || _reloadCancellation is not null || _inlinePending) return;
        if (!CommitChecks() || _inlinePending) return;
        RememberChecks();
        var selectedP = (PeopleGrid.SelectedItem as PeoplePerson)?.Id;
        var selectedC = (CarsGrid.SelectedItem as PeopleCar)?.Id;
        var showInactive = ShowInactive.IsChecked == true;
        var content = (UIElement)Content;
        using var cancellation = new CancellationTokenSource();
        _reloadCancellation = cancellation;
        var token = cancellation.Token;
        _revision = "";
        _loading = true;
        content.IsEnabled = false;
        HintText.Text = "Обновление справочника…";
        try
        {
            var data = await Task.Run(() => _db.Load(showInactive, token), token);
            if (_closed || token.IsCancellationRequested) return;
            HintText.Text = _registrationUncertain
                ? "Файл создан, но запись в журнал не подтверждена. Повторный экспорт не выполняйте."
                : "Двойной щелчок или F2 — редактировать ячейку; Enter — сохранить, Esc — отменить.";
            if (history is not null) RestoreHistory(history);
            ApplyDirectory(data.People, data.Cars, data.Revision, selectedP, selectedC);
        }
        catch (Exception ex)
        {
            if (_closed || token.IsCancellationRequested) return;
            _revision = "";
            HintText.Text = "Не удалось обновить справочник. Сохранение заблокировано; измените фильтр или откройте окно заново.";
            MessageBox.Show(OwnerWindow, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _reloadCancellation = null;
            _loading = false;
            if (!_closed) content.IsEnabled = true;
        }
    }

    private void ApplyDirectory(IReadOnlyList<PeoplePerson> people, IReadOnlyList<PeopleCar> cars,
        string revision, long? selectedP, long? selectedC)
    {
        if (string.IsNullOrWhiteSpace(revision))
            throw new DBConcurrencyException("Справочник получен без ревизии. Повторите загрузку.");
        _people = people.ToList();
        _cars = cars.ToList();
        // Text fields are immutable until the next authoritative directory readback.
        _peopleSearch = _people.ToDictionary(p => p.Id, p => p.Fio + " " + p.Post + " " + p.Birth + " " + p.Note);
        _carSearch = _cars.ToDictionary(c => c.Id, c => c.Model + " " + c.Plate + " " + c.Note);
        _revision = revision;
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
                ? row is PeoplePerson p && _peopleSearch.TryGetValue(p.Id, out var personText) &&
                    personText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                : row is PeopleCar c && _carSearch.TryGetValue(c.Id, out var carText) &&
                    carText.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        }
        UpdateCounts();
    }
    private void UpdateCounts()
    {
        if (_loading) return;
        var selectedPeople = IsAccess ? _people.Count(x => x.Selected) : 0;
        var selectedCars = IsAccess ? _cars.Count(x => x.Selected) : 0;
        PeopleCount.Text = IsAccess
            ? $"в списке {selectedPeople} из {_people.Count}"
            : $"работают {_people.Count(x => x.Active)}, показано {_people.Count}";
        CarsCount.Text = IsAccess
            ? $"в списке {selectedCars} из {_cars.Count}"
            : $"{_cars.Count} авто";
        if (IsAccess)
            ExportButton.Content = $"Сформировать список (Excel) — {selectedPeople} чел., " +
                $"{selectedCars} авто";
    }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loading && !_operationBusy && !_inlinePending && PeopleGrid is not null &&
            CommitChecks() && !_inlinePending) ApplyFilter();
    }
    private async void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading && PeopleGrid is not null) await ReloadReadOnlyAsync();
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateCounts();
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
        if (_loading || _operationBusy || !IsAccess || _checkUpdateQueued) return;
        _checkUpdateQueued = true;
        // Coalesce notifications from multiple realized checkboxes.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _checkUpdateQueued = false;
            if (!_closed && !_loading && !_operationBusy && IsAccess)
            {
                RememberChecks();
                UpdateCounts();
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
    private PeoplePerson? SelectedPerson() => PeopleGrid.SelectedItem as PeoplePerson;
    private PeopleCar? SelectedCar() => CarsGrid.SelectedItem as PeopleCar;

    private async void PersonAdd_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var values = EditFields("Добавить человека", new (string, string)[]
        {
            ("ФИО", ""), ("Должность", "монтажник вент.систем"), ("Дата рождения", ""),
            ("Примечание", ""), ("ФИО — кого (пусто = автоматически)", ""),
            ("Должность — кого (пусто = автоматически)", "")
        });
        if (values is null) return;
        var row = new PeoplePerson(0, values[0], values[1], values[2], values[3],
            values[4], values[5], true, 0);
        var result = await MutateAsync(revision => _db.SavePerson(row, revision));
        if (IsAccess) _checkedPeople.Add(result.Id);
        await ReloadMutationAsync(personId: result.Id);
    });
    private async void CarAdd_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var values = EditFields("Добавить автомобиль", new (string, string)[]
        { ("Марка / модель", ""), ("Госномер", ""), ("Примечание", "") });
        if (values is null) return;
        var row = new PeopleCar(0, values[0], values[1], values[2], true, 0);
        var result = await MutateAsync(revision => _db.SaveCar(row, revision));
        if (IsAccess) _checkedCars.Add(result.Id);
        await ReloadMutationAsync(carId: result.Id);
    });
    private (long Id, bool Active, string Name)[] SelectedEntries(bool person) =>
        person ? PeopleGrid.SelectedItems.OfType<PeoplePerson>()
            .Select(p => (p.Id, p.Active, p.Fio)).ToArray() :
            CarsGrid.SelectedItems.OfType<PeopleCar>()
            .Select(c => (c.Id, c.Active, c.Model + " " + c.Plate)).ToArray();

    private bool IsMenuSnapshot(bool person, (long Id, bool Active, string Name)[] entries, string revision) =>
        !_closed && !_loading && !_inlinePending &&
        PeopleEntrySelection.Matches(entries, SelectedEntries(person), revision, _revision);

    private void People_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        OpenEntryMenu(true, e);
    private void Cars_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        OpenEntryMenu(false, e);

    private void OpenEntryMenu(bool person, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (_closed || _loading || _operationBusy || _inlinePending || string.IsNullOrWhiteSpace(_revision)) return;
        var grid = person ? PeopleGrid : CarsGrid;
        if (person ? ContextMenuTarget.SelectRow<PeoplePerson>(grid, e) is null :
            ContextMenuTarget.SelectRow<PeopleCar>(grid, e) is null) return;
        var entries = SelectedEntries(person);
        if (entries.Length == 0) return;
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
                if (!_operationBusy && IsMenuSnapshot(person, entries, revision)) action();
                else MessageBox.Show(OwnerWindow, "Выбор изменился. Откройте меню снова.", Title);
            };
            menu.Items.Add(item);
        }
        Add("Не работает у нас (скрыть из новых списков)", "X",
            () => ChangeActive(person, entries, false, revision),
            entries.Any(p => p.Active));
        if (ShowInactive.IsChecked == true)
            Add("Снова работает", "Check",
                () => ChangeActive(person, entries, true, revision),
                entries.Any(p => !p.Active));
        menu.Items.Add(new Separator());
        Add("Удалить из базы…", "Trash", () => DeleteEntries(person, entries, revision), danger: true);
        menu.IsOpen = true;
    }

    private async void ChangeActive(bool person, (long Id, bool Active, string Name)[] entries,
        bool active, string revision) => await RunAsync(async () =>
    {
        if (!IsMenuSnapshot(person, entries, revision)) return;
        var ids = PeopleEntrySelection.ChangedIds(entries, active);
        if (ids.Length == 0) return;
        await MutateAsync(expected => _db.SetActiveBatch(person, ids, active, expected));
        if (!active)
        {
            var checkedIds = person ? _checkedPeople : _checkedCars;
            foreach (var id in ids) checkedIds.Remove(id);
        }
        await ReloadMutationAsync();
    });

    private void ToggleSelectedActive(bool person)
    {
        var entries = SelectedEntries(person);
        if (entries.Length == 0) return;
        // Mixed selection: hide active entries, never toggle each independently.
        var active = entries.All(p => !p.Active);
        ChangeActive(person, entries, active, _revision);
    }
    private void PersonActive_Click(object sender, RoutedEventArgs e) => ToggleSelectedActive(true);
    private void CarActive_Click(object sender, RoutedEventArgs e) => ToggleSelectedActive(false);
    private void PersonDelete_Click(object sender, RoutedEventArgs e) =>
        DeleteEntries(true, SelectedEntries(true), _revision);
    private void CarDelete_Click(object sender, RoutedEventArgs e) =>
        DeleteEntries(false, SelectedEntries(false), _revision);

    private async void DeleteEntries(bool person, (long Id, bool Active, string Name)[] entries,
        string revision) => await RunAsync(async () =>
    {
        if (entries.Length == 0 || string.IsNullOrWhiteSpace(revision)) return;
        var ids = entries.Select(p => p.Id).OrderBy(id => id).ToArray();
        if (!IsMenuSnapshot(person, entries, revision)) return;
        var names = string.Join(", ", entries.Take(5).Select(p => p.Name)) +
            (entries.Length > 5 ? "…" : "");
        if (MessageBox.Show(OwnerWindow, $"Удалить из общей базы ({entries.Length}): {names}?\n\n" +
                "Записи, связанные с ранее сформированными списками и приказами, будут отключены, а не удалены. " +
                "Если сотрудник просто ушёл, лучше выбрать «Не работает у нас».",
                "Удаление из базы", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes ||
            !IsMenuSnapshot(person, entries, revision)) return;
        var result = await MutateAsync(expected => _db.DeleteOrDeactivateBatch(person, ids, expected));
        var checkedIds = person ? _checkedPeople : _checkedCars;
        foreach (var id in ids) checkedIds.Remove(id);
        await ReloadMutationAsync();
        MessageBox.Show(OwnerWindow, $"Удалено: {result.Deleted}. Отключено (есть история): {result.Deactivated}.",
            Title, MessageBoxButton.OK, MessageBoxImage.Information);
    });
    private async void Move(bool person, int delta)
    {
        await RunAsync(async () =>
        {
            var selected = person ? SelectedPerson()?.Id : SelectedCar()?.Id;
            if (selected is null) return;
            var ids = person ? _people.Select(p => p.Id).ToArray() : _cars.Select(c => c.Id).ToArray();
            var i = Array.IndexOf(ids, selected.Value);
            var j = i + delta;
            if (i < 0 || j < 0 || j >= ids.Length) return;
            (ids[i], ids[j]) = (ids[j], ids[i]);
            await MutateAsync(revision => _db.Reorder(person, ids, revision));
            await ReloadMutationAsync();
        });
    }
    private void PersonUp_Click(object sender, RoutedEventArgs e) => Move(true, -1);
    private void PersonDown_Click(object sender, RoutedEventArgs e) => Move(true, +1);
    private void CarUp_Click(object sender, RoutedEventArgs e) => Move(false, -1);
    private void CarDown_Click(object sender, RoutedEventArgs e) => Move(false, +1);
    private void CheckAll(bool people, bool selected)
    {
        if (_loading || _operationBusy || _inlinePending || !CommitChecks() || _inlinePending) return;
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
            Title = title, Owner = OwnerWindow, Width = 660, SizeToContent = SizeToContent.Height,
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
        ok.Click += (_, _) => dialog.DialogResult = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        dialog.Content = new ScrollViewer { Content = root };
        if (dialog.ShowDialog() != true) return null;
        return boxes.Select(x => PeopleRules.Clean(x.Text)).ToArray();
    }

    private async void Import_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var picker = new OpenFileDialog { Filter = "Excel (*.xlsx;*.xlsm)|*.xlsx;*.xlsm" };
        if (picker.ShowDialog(OwnerWindow) != true) return;
        var path = picker.FileName;
        var detected = await Task.Run(() => PeopleDocuments.ReadImport(path));
        var review = new PeopleImportPreviewWindow(picker.FileName, detected) { Owner = OwnerWindow };
        if (review.ShowDialog() != true) return;
        var preview = review.Preview;
        var resultImport = await MutateAsync(revision => _db.Import(preview, revision));
        // One authoritative readback; apply imported checkmarks locally below.
        await ReloadMutationAsync();
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
            foreach (var p in _people) p.Selected = _checkedPeople.Contains(p.Id);
            foreach (var c in _cars) c.Selected = _checkedCars.Contains(c.Id);
            PeopleGrid.Items.Refresh();
            CarsGrid.Items.Refresh();
            UpdateCounts();
        }
    });

    private async Task RefreshHistoryAsync(long registeredId)
    {
        if (!IsAccess) return;
        var id = _objectId!.Value;
        var history = await Task.Run(() => _db.History(id, "list"));
        if (_closed) return;
        if (!history.Any(h => h.Id == registeredId))
            throw new DBConcurrencyException("Созданный список отсутствует в обновлённой истории.");
        ApplyHistory(history, registeredId);
    }

    private void ApplyHistory(IReadOnlyList<AccessHistory> history, long? registeredId = null)
    {
        _history = history.Select(h => new HistoryOption(h, $"{h.CreatedAt:dd.MM.yyyy HH:mm} · {h.User}")).ToList();
        var wasLoading = _loading;
        _loading = true;
        try
        {
            HistoryBox.ItemsSource = _history;
            if (registeredId.HasValue)
            {
                // A concurrent export may precede ours in the list. Keep the current
                // draft/checkmarks, and select exactly the registered export receipt.
                HistoryBox.SelectedItem = _history.FirstOrDefault(h => h.History.Id == registeredId.Value);
            }
            else if (_history.Count > 0)
            {
                HistoryBox.SelectedIndex = 0;
                RestoreHistory(_history[0].History);
            }
        }
        finally { _loading = wasLoading; }
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
    private async void History_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HistoryBox.SelectedItem is not HistoryOption option) return;
        await ReloadReadOnlyAsync(option.History);
    }
    private async void OpenHistory_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (HistoryBox.SelectedItem is not HistoryOption option) return;
        var path = option.History.File;
        if (!await Task.Run(() => File.Exists(path)))
            throw new FileNotFoundException("Файл ранее созданного списка не найден. История осталась в SQL Server.");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    });
    private async void Orders_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!_objectId.HasValue) return;
        var child = new OrdersWindow(_db, _objectId.Value) { Owner = OwnerWindow };
        child.ShowDialog();
        _revision = "";
        var id = _objectId.Value;
        _site = await Task.Run(() => _db.Site(id));
        await ReloadMutationAsync(); // orders update meta.rev; avoid stale revision for the next list.
    });
    private async void Export_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_site is null || _objectId is null || _registrationUncertain) return;
        var revision = _revision;
        if (string.IsNullOrWhiteSpace(revision))
            throw new InvalidOperationException("Сначала обновите справочник.");
        RememberChecks();
        var people = _people.Where(p => p.Selected)
            .Select(p => new AccessPerson(p.Id, p.Fio, p.Post, p.Birth)).ToArray();
        var cars = _cars.Where(c => c.Selected)
            .Select(c => new AccessCar(c.Id, c.Model, c.Plate)).ToArray();
        if (people.Length == 0) { MessageBox.Show(OwnerWindow, "Отметьте хотя бы одного человека."); return; }
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
        var site = _site;
        var objectId = _objectId.Value;
        var showInactive = ShowInactive.IsChecked == true;
        var companies = _companies.ToArray();
        var intro = IntroBox.Text;
        var initialFolder = await Task.Run(() => Directory.Exists(site.Folder) ? site.Folder :
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        var picker = new SaveFileDialog
        {
            Title = "Сохранить список на допуск",
            Filter = "Excel (*.xlsx)|*.xlsx", DefaultExt = ".xlsx",
            FileName = PeopleRules.SafeFileName("Список людей " + _site.Name +
                " " + DateTime.Today.ToString("dd.MM.yyyy")) + ".xlsx",
            InitialDirectory = initialFolder
        };
        if (picker.ShowDialog(OwnerWindow) != true) return;
        var path = picker.FileName;
        var operation = Guid.NewGuid();
        var snapshot = new AccessSnapshot(people, cars, intro, req, company?.Id,
            company?.Name ?? "", signer, operation);
        await Task.Run(() =>
        {
            if (File.Exists(path))
                throw new IOException("Файл уже существует. Выберите другое имя — перезапись не допускается.");
            if (_db.Load(showInactive).Revision != revision ||
                _db.Site(objectId) != site || !_db.Companies().SequenceEqual(companies))
                throw new InvalidOperationException("Справочник, объект или реквизиты изменились. Перезапустите окно.");
            // No history record if XLSX was not successfully saved.
            PeopleDocuments.ExportAccessList(path, company, req, intro, people, cars, signer);
        });
        long registeredId;
        try
        {
            var registered = await MutateAsync(expected => _db.RegisterExport(objectId, "list", path,
                JsonSerializer.Serialize(snapshot), operation, expected));
            registeredId = registered.Id;
            _revision = registered.Revision;
        }
        catch (Exception)
        {
            // COMMIT may be uncertain: retain the file and idempotency token, never delete blindly.
            _registrationUncertain = true;
            ExportButton.IsEnabled = false;
            HintText.Text = "Файл создан, но запись в журнал не подтверждена. Повторный экспорт не выполняйте.";
            throw;
        }
        HintText.Text = "Список создан и зарегистрирован в SQL Server.";
        try { await RefreshHistoryAsync(registeredId); }
        catch (Exception ex)
        {
            HintText.Text = "Список зарегистрирован. Не удалось обновить историю.";
            MessageBox.Show(OwnerWindow, "Файл сохранён: " + path + "\n" + ex.Message,
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    });
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
