using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Win32;

namespace GmcSmeta.App;

public partial class OrdersWindow : Window
{
    private SqlServerPeopleAccess _db = null!;
    private CancellationTokenSource? _initialCancellation;
    private CancellationTokenSource? _historyOpenCancellation;
    private bool _closed;
    private readonly long _objectId;
    private AccessSite _site = null!;
    private List<PeoplePerson> _people = new();
    private List<AccessCompany> _companies = new();
    private readonly List<OrderTemplateChoice> _templates = new();
    private string _revision = "";
    private string _folder = "";
    private bool _initializing = true;
    private bool _registrationUncertain;
    private static readonly string TemplateSettings = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GMC-Smeta-Next", "orders-off.json");


    public sealed class OrderTemplateChoice
    {
        public string Path { get; init; } = "";
        public string Title { get; init; } = "";
        public bool Checked { get; set; } = true;
    }
    private sealed record OrderHistoryItem(AccessHistory History, string Label);

    public OrdersWindow(string connectionString, long objectId)
        : this(token => new SqlServerPeopleAccess(connectionString, token), objectId) { }

    public OrdersWindow(SqlServerPeopleAccess database, long objectId)
        : this(_ => database, objectId) { }

    private OrdersWindow(Func<CancellationToken, SqlServerPeopleAccess> createDatabase, long objectId)
    {
        _objectId = objectId;
        InitializeComponent();
        OrderContent.IsEnabled = false;
        MakeButton.IsEnabled = false;
        ObjectName.Text = "Загрузка данных объекта и приказов…";
        Loaded += async (_, _) => await InitializeAsync(createDatabase);
        Closed += (_, _) =>
        {
            _closed = true;
            _initialCancellation?.Cancel();
            _historyOpenCancellation?.Cancel();
        };
    }

    private async Task InitializeAsync(Func<CancellationToken, SqlServerPeopleAccess> createDatabase)
    {
        if (_closed || !_initializing || _initialCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _initialCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var db = createDatabase(token);
                var site = db.Site(_objectId, token);
                var directory = db.Load(false, token);
                var people = directory.People.Where(p => p.Active).ToList();
                var companies = db.Companies(token).ToList();
                var history = db.History(_objectId, "order", token);
                token.ThrowIfCancellationRequested();
                var folder = Directory.Exists(site.Folder) ? Path.Combine(site.Folder, "допуск") :
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "Допуск", PeopleRules.SafeFileName(site.Name), "допуск");
                token.ThrowIfCancellationRequested();
                return (Db: db, Site: site, People: people, Revision: directory.Revision,
                    Companies: companies, History: history, Folder: folder);
            }, token);
            if (_closed || token.IsCancellationRequested) return;
            _db = snapshot.Db;
            _site = snapshot.Site;
            _people = snapshot.People;
            _revision = snapshot.Revision;
            _companies = snapshot.Companies;
            _folder = snapshot.Folder;
            ObjectName.Text = _site.Name;
            PersonBox.ItemsSource = _people;
            var lastResponsible = LastResponsible(snapshot.History);
            PersonBox.SelectedItem = _people.FirstOrDefault(p => p.Id == lastResponsible) ??
                _people.FirstOrDefault(p => p.Post.Contains("прораб",
                    StringComparison.OrdinalIgnoreCase)) ?? _people.FirstOrDefault();
            ContractBox.Text = ContractReference(_site);
            SiteBox.Text = PeopleRules.Clean(string.IsNullOrWhiteSpace(_site.SiteFull) ?
                (string.IsNullOrWhiteSpace(_site.Address) ? _site.Name : _site.Address) : _site.SiteFull)
                .Trim('«', '»', '"', ' ');
            DayPicker.SelectedDate = DateTime.Today;
            NumberBox.Text = DateTime.Today.ToString("dd/MM", CultureInfo.InvariantCulture) + "-01";
            FolderInfo.Text = _folder;
            var uncheckedNames = DisabledTemplates();
            foreach (var path in PeopleDocuments.Templates())
                _templates.Add(new OrderTemplateChoice { Path = path, Title = PeopleDocuments.TemplateTitle(path),
                    Checked = !uncheckedNames.Contains(Path.GetFileName(path)) });
            TemplateList.ItemsSource = _templates;
            if (_templates.Count == 0)
                StatusInfo.Text = "Нет Word-шаблонов. Положите .docx в папку шаблонов.";
            if (_people.Count == 0)
            {
                MakeButton.IsEnabled = false;
                StatusInfo.Text = "Нет активных сотрудников. Добавьте человека в общий справочник.";
            }
            ApplyHistory(snapshot.History);
            _initializing = false;
            UpdateInflection();
            OrderContent.IsEnabled = true;
            MakeButton.IsEnabled = _people.Count > 0;
        }
        catch (Exception ex)
        {
            if (_closed || token.IsCancellationRequested) return;
            _revision = "";
            Error(ex.Message);
            Close();
        }
        finally
        {
            _initialCancellation = null;
        }
    }

    private static long? LastResponsible(IReadOnlyList<AccessHistory> history)
    {
        foreach (var record in history)
        {
            try
            {
                using var doc = JsonDocument.Parse(record.Data);
                var root = doc.RootElement;
                if ((root.TryGetProperty("PersonId", out var value) ||
                    root.TryGetProperty("person_id", out value)) &&
                    value.TryGetInt64(out var id)) return id;
            }
            catch (JsonException) { /* legacy invalid history must not block order UI */ }
        }
        return null;
    }
    private static HashSet<string> DisabledTemplates()
    {
        try
        {
            return File.Exists(TemplateSettings)
                ? (JsonSerializer.Deserialize<string[]>(File.ReadAllText(TemplateSettings)) ??
                    Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }
    private void SaveTemplateSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TemplateSettings)!);
        var disabled = _templates.Where(x => !x.Checked).Select(x => Path.GetFileName(x.Path)).ToArray();
        File.WriteAllText(TemplateSettings, JsonSerializer.Serialize(disabled));
    }

    private static string ContractReference(AccessSite site)
    {
        var when = site.ContractDate;
        if (DateTime.TryParseExact(when, "dd.MM.yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date))
        {
            var withDay = PeopleRules.DateLong(date).Replace("«", "").Replace("» ", " ");
            when = withDay;
        }
        return string.Join(" ", new[]
        {
            string.IsNullOrWhiteSpace(site.ContractNumber) ? "" : "№" + site.ContractNumber.Trim(),
            string.IsNullOrWhiteSpace(when) ? "" : "от " + when
        }.Where(x => x.Length > 0));
    }
    private void RefreshHistory() => ApplyHistory(_db.History(_objectId, "order"));

    private void ApplyHistory(IReadOnlyList<AccessHistory> history)
    {
        OrderHistory.ItemsSource = history.Select(x => new OrderHistoryItem(x,
            $"{x.CreatedAt:dd.MM.yyyy HH:mm} · {x.User}")).ToArray();
    }
    private void UpdateInflection()
    {
        if (PersonBox.SelectedItem is not PeoplePerson p) { InflectionPreview.Text = ""; return; }
        InflectionPreview.Text = "В приказе: «назначить " + p.DisplayPostGenitive +
            " " + p.DisplayGenitive + "». Исправить склонения можно в общем справочнике.";
    }
    private void Person_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing) UpdateInflection();
    }
    private void Date_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (NumberBox is null || DayPicker?.SelectedDate is not DateTime day) return;
        NumberBox.Text = day.ToString("dd/MM", CultureInfo.InvariantCulture) + "-01";
    }
    private void Templates_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(PeopleDocuments.TemplatesFolder);
            Process.Start(new ProcessStartInfo(PeopleDocuments.TemplatesFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { Error(ex.Message); }
    }
    private void OutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var choose = new OpenFolderDialog
        {
            Title = "Папка для выданных приказов",
            InitialDirectory = Directory.Exists(_folder) ? _folder :
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (choose.ShowDialog(this) == true)
        {
            _folder = choose.FolderName;
            FolderInfo.Text = _folder;
        }
    }
    private void History_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _historyOpenCancellation?.Cancel();
    }

    private async void OpenHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _initializing || _historyOpenCancellation is not null ||
            OrderHistory.SelectedItem is not OrderHistoryItem selected) return;
        using var cancellation = new CancellationTokenSource();
        _historyOpenCancellation = cancellation;
        var token = cancellation.Token;
        var wasMakeEnabled = MakeButton.IsEnabled;
        var originalLabel = OpenHistoryButton.Content;
        OpenHistoryButton.IsEnabled = false;
        OpenHistoryButton.Content = "Поиск файлов…";
        MakeButton.IsEnabled = false;
        try
        {
            var path = await Task.Run(() => OrderHistoryFileResolver.Resolve(selected.History, token), token);
            if (_closed || token.IsCancellationRequested ||
                !ReferenceEquals(OrderHistory.SelectedItem, selected)) return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            if (!_closed && !token.IsCancellationRequested &&
                ReferenceEquals(OrderHistory.SelectedItem, selected)) Error(ex.Message);
        }
        finally
        {
            _historyOpenCancellation = null;
            if (!_closed)
            {
                OpenHistoryButton.Content = originalLabel;
                OpenHistoryButton.IsEnabled = true;
                MakeButton.IsEnabled = wasMakeEnabled && !_registrationUncertain;
            }
        }
    }
    private void Make_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _initializing || string.IsNullOrWhiteSpace(_revision)) return;
        if (_historyOpenCancellation is not null) return;
        if (_registrationUncertain) { Error("Проверьте журнал и файлы, затем заново откройте окно."); return; }
        var chosen = _templates.Where(x => x.Checked).ToArray();
        if (chosen.Length == 0) { Error("Отметьте хотя бы один шаблон приказа."); return; }
        if (PersonBox.SelectedItem is not PeoplePerson person) { Error("Выберите ответственного."); return; }
        if (DayPicker.SelectedDate is not DateTime when) { Error("Выберите дату."); return; }
        var number = PeopleRules.Clean(NumberBox.Text);
        if (!Regex.IsMatch(number, @"\d+\D*$")) { Error("Номер должен заканчиваться числом."); return; }
        var operation = Guid.NewGuid();
        var generated = new List<string>();
        string? current = null;
        bool registrationStarted = false;
        try
        {
            Directory.CreateDirectory(_folder);
            var company = _companies.FirstOrDefault(c =>
                c.Name.Contains("ГМЦ", StringComparison.OrdinalIgnoreCase)) ?? _companies.FirstOrDefault();
            var site = PeopleRules.Clean(SiteBox.Text);
            if (site.Length == 0) throw new ArgumentException("Укажите объект в приказе.");
            // A new revision is required whenever another client edited the people dictionary.
            var latest = _db.Load(false);
            if (latest.Revision != _revision || _db.Site(_objectId) != _site ||
                !_db.Companies().SequenceEqual(_companies))
                throw new System.Data.DBConcurrencyException("Сотрудники, объект или реквизиты изменились. Перезапустите окно.");
            foreach (var choice in chosen)
            {
                if (!File.Exists(choice.Path))
                    throw new FileNotFoundException("Не найден Word-шаблон.", choice.Path);
                var name = PeopleRules.SafeFileName("Приказ №" +
                    number.Replace('/', '-') + " " + choice.Title);
                current = PeopleDocuments.UniquePath(_folder, name, ".docx");
                var mapping = PeopleDocuments.OrderMapping(_site, person, company,
                    when, number, ContractBox.Text.Trim(), site);
                PeopleDocuments.FillDocx(choice.Path, current, mapping);
                generated.Add(current); // only a successfully published file belongs to this operation
                using (var docx = System.IO.Compression.ZipFile.OpenRead(current))
                    if (docx.GetEntry("word/document.xml") is null)
                        throw new InvalidDataException("Word-документ повреждён.");
                current = null;
                number = PeopleRules.NextNumber(number);
            }
            var snapshot = new OrderSnapshot(person.Id, person.Fio, generated, NumberBox.Text, operation);
            registrationStarted = true;
            _revision = _db.RegisterExport(_objectId, "order", _folder,
                JsonSerializer.Serialize(snapshot), operation, _revision, site).Revision;
            try { SaveTemplateSettings(); }
            catch (IOException) { /* optional UI preference cannot invalidate issued orders */ }
            StatusInfo.Text = $"Сформировано {generated.Count} приказов, история записана в SQL Server.";
            RefreshHistory();
            Process.Start(new ProcessStartInfo(_folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            bool recorded = false;
            if (registrationStarted)
            {
                try { recorded = _db.WasRegistered(_objectId, operation); }
                catch { /* Connection interrupted: do not assume that the commit failed. */ }
            }
            if (recorded)
            {
                StatusInfo.Text = "Приказы созданы; регистрация подтверждена повторным чтением SQL Server.";
                RefreshHistory();
                return;
            }
            if (!registrationStarted)
            {
                // No SQL write has begun: partial batch output is safe to remove.
                foreach (var path in generated)
                {
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch { /* report original export failure; don't overwrite existing files */ }
                }
            }
            else
            {
                _registrationUncertain = true;
                MakeButton.IsEnabled = false;
                StatusInfo.Text = "Исход COMMIT не подтверждён. Файлы оставлены на месте; не создавайте их повторно.";
            }
            Error(ex.Message);
        }
    }
    private void Error(string message) =>
        MessageBox.Show(this, message, "Приказы", MessageBoxButton.OK, MessageBoxImage.Warning);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
