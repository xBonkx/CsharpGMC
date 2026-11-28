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
    private readonly long? _documentId;
    private AccessSite _site = null!;
    private List<PeoplePerson> _people = new();
    private List<AccessCompany> _companies = new();
    private readonly List<OrderTemplateChoice> _templates = new();
    private string _revision = "";
    private string _folder = "";
    private bool _initializing = true;
    private bool _registrationUncertain;
    private bool _issuing;
    private static readonly string TemplateSettings = Path.Combine(
        GmcSmeta.Core.Demo.DemoMode.ApplicationData,
        "GMC-Smeta-Next", "orders-off.json");


    public sealed class OrderTemplateChoice
    {
        public string Path { get; init; } = "";
        public string Title { get; init; } = "";
        public bool Checked { get; set; } = true;
    }
    private sealed record OrderHistoryItem(AccessHistory History, string Label);

    public OrdersWindow(string connectionString, long objectId, long? documentId = null)
        : this(token => new SqlServerPeopleAccess(connectionString, token), objectId, documentId) { }

    public OrdersWindow(SqlServerPeopleAccess database, long objectId)
        : this(_ => database, objectId, null) { }

    private OrdersWindow(Func<CancellationToken, SqlServerPeopleAccess> createDatabase, long objectId, long? documentId)
    {
        _objectId = objectId;
        _documentId = documentId;
        InitializeComponent();
        OrderContent.IsEnabled = false;
        MakeButton.IsEnabled = false;
        ObjectName.Text = "Загрузка данных объекта и приказов…";
        Loaded += async (_, _) => await InitializeAsync(createDatabase);
        Closing += (_, e) => { if (_issuing) e.Cancel = true; };
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
                var site = db.OrderSite(_objectId, _documentId, token);
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
            var blocked = OrderAgreementPolicy.BlockReason(_site, _companies);
            MakeButton.IsEnabled = _people.Count > 0 && blocked is null;
            if (blocked is not null) StatusInfo.Text = blocked;
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
        if (_closed || _initializing || _issuing || _historyOpenCancellation is not null ||
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
    private async void Make_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _initializing || string.IsNullOrWhiteSpace(_revision)) return;
        if (_historyOpenCancellation is not null) return;
        if (_issuing) return;
        if (OrderAgreementPolicy.BlockReason(_site, _companies) is { } blocked) { Error(blocked); return; }
        if (_registrationUncertain) { Error("Проверьте журнал и файлы, затем заново откройте окно."); return; }
        var chosen = _templates.Where(x => x.Checked)
            .Select(x => new OrderIssueService.Template(x.Path, x.Title)).ToArray();
        if (chosen.Length == 0) { Error("Отметьте хотя бы один шаблон приказа."); return; }
        if (PersonBox.SelectedItem is not PeoplePerson person) { Error("Выберите ответственного."); return; }
        if (DayPicker.SelectedDate is not DateTime when) { Error("Выберите дату."); return; }
        var number = PeopleRules.Clean(NumberBox.Text);
        if (!Regex.IsMatch(number, @"\d+\D*$")) { Error("Номер должен заканчиваться числом."); return; }
        var site = PeopleRules.Clean(SiteBox.Text);
        if (site.Length == 0) { Error("Укажите объект в приказе."); return; }
        var request = new OrderIssueService.Request(_objectId, _documentId, _site, _companies.ToArray(),
            person with { }, when, number, NumberBox.Text, ContractBox.Text.Trim(), site,
            _folder, _revision, chosen, Guid.NewGuid());

        _issuing = true;
        OrderContent.IsEnabled = false;
        MakeButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        StatusInfo.Text = "Формирование приказов и регистрация в SQL Server…";
        try
        {
            // No cancellation after dispatch: generated files and SQL registration
            // must reach a known outcome, even on a slow network.
            var result = await Task.Run(() => OrderIssueService.Issue(_db, request));
            if (!result.Registered)
            {
                _registrationUncertain = true;
                _revision = "";
                StatusInfo.Text = "Исход COMMIT не подтверждён. Файлы оставлены на месте; не создавайте их повторно.";
                Error(result.Warning ?? StatusInfo.Text);
                return;
            }

            // Registration succeeded. A later history or shell failure is not an
            // export failure and must never trigger deletion or another registration.
            _revision = result.Revision ?? "";
            StatusInfo.Text = $"Сформировано {result.Count} приказов, история записана в SQL Server.";
            if (result.History is not null) ApplyHistory(result.History);
            if (result.Warning is not null)
            {
                StatusInfo.Text += " " + result.Warning;
                return;
            }
            try { SaveTemplateSettings(); }
            catch (Exception) { /* optional UI preference cannot invalidate issued orders */ }
            try { Process.Start(new ProcessStartInfo(request.Folder) { UseShellExecute = true }); }
            catch (Exception ex) { StatusInfo.Text += " Не удалось открыть папку: " + ex.Message; }
        }
        catch (Exception ex)
        {
            StatusInfo.Text = "Не удалось завершить формирование приказов.";
            Error(ex.Message);
        }
        finally
        {
            _issuing = false;
            OrderContent.IsEnabled = true;
            CloseButton.IsEnabled = true;
            MakeButton.IsEnabled = !_registrationUncertain && _revision.Length > 0 && _people.Count > 0;
        }
    }
    private void Error(string message) =>
        MessageBox.Show(this, message, "Приказы", MessageBoxButton.OK, MessageBoxImage.Warning);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
