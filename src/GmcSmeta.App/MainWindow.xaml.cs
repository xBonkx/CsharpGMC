using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App;

public partial class MainWindow : Window
{
    private static readonly string WindowTitle = "ГМЦ-Смета v" +
        (typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3));

    private SqlConnection? _serverConn;
    private volatile bool _startupClosed;
    private bool _startupInProgress;
    private readonly string? _sqlConnectionString;

    /// <summary>
    /// Отмена/повтор всех правок сметы одной сессии (smeta/undo.py): стеки живут
    /// в памяти процесса и переживают переключение документов, снимки и запись —
    /// на SQL Server, одним Serializable-транзакцией с верификацией.
    /// </summary>
    internal static UndoManager Undo { get; private set; } = null!;

    private readonly List<DocRow> _docs = new();
    private readonly List<ObjectRow> _objects = new();
    private int _pendingOpen;
    private string? _pendingGmcs;

    // LRU-кэш открытых смет (S-03): не более 3 документов.
    private readonly Dictionary<int, EstimateView> _estimateCache = new();
    private readonly LinkedList<int> _estimateCacheOrder = new();
    private const int EstimateCacheCapacity = 3;
    private System.Windows.Threading.DispatcherTimer? _estimateCacheTrimTimer;
    private int _estimateCacheTrimAttempts;
    private bool _ready;
    private double _sidebarWidth = 300;
    private int? _openDocumentId;
    private long _estimateOpenGeneration;
    private System.Threading.CancellationTokenSource? _estimateCacheValidation;
    private int? _pendingEstimateFocus;
    private NavigationMode? _shownMode;
    private readonly WpfSettingsStore _navigationSettings = new();
    private readonly EstimateUiSettings _uiSettings = EstimateUiSettings.Load();
    private NavigationMode _preferredNavigationMode = NavigationMode.Contracts;
    private readonly Dictionary<NavigationMode, HashSet<string>> _expanded = new();
    private readonly Dictionary<int, TreeViewItem> _documentNodes = new();
    private readonly Dictionary<int, TreeViewItem> _objectNodes = new();
    private readonly Dictionary<int, PendingNavigationDocuments> _pendingNavigationDocuments = new();
    private static readonly object NavigationPlaceholder = new();
    private sealed record PendingNavigationDocuments(IReadOnlyList<DocRow> Documents, HashSet<string>? Opened);
    // На время поиска временно раскрываем совпадения, затем возвращаем состояние пользователя.
    private Dictionary<TreeViewItem, bool>? _searchExpansionSnapshot;
    private System.Windows.Threading.DispatcherTimer? _navigationSearchTimer;
    private NavigationSearchIndex _navigationSearchIndex = new(Array.Empty<NavigationObject>());
    private bool _syncingNavigation;
    private (bool Wide, bool Progress)? _objectTileLayout;
    private ObjectRow? _selectedObject;
    private DirectorView? _directorView;
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(340d));
    public double CardWidth { get => (double)GetValue(CardWidthProperty); set => SetValue(CardWidthProperty, value); }

    public MainWindow()
    {
        StartupTrace.Mark("main_initialize_component_begin");
        InitializeComponent();
        Title = WindowTitle + " — C#";
        _navigationSearchTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        _navigationSearchTimer.Tick += (_, _) =>
        {
            _navigationSearchTimer.Stop();
            if (_ready && !_startupClosed) ApplyNavigationFilter();
        };
        _estimateCacheTrimTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.ContextIdle)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _estimateCacheTrimTimer.Tick += (_, _) =>
        {
            _estimateCacheTrimTimer.Stop();
            if (_startupClosed) return;
            TrimEstimateCache();
            // Bounded deferred attempts after pending writes/readback settle.
            // Permanent drafts can pin an entry until the next navigation.
            if (_estimateCache.Count > EstimateCacheCapacity && --_estimateCacheTrimAttempts > 0)
                _estimateCacheTrimTimer.Start();
        };
        StartupTrace.Mark("main_initialize_component_end");
        ApplyFontScale(_uiSettings.FontScale);
        RestoreWindowGeometry();
        InitializePtoMenu();
        _sqlConnectionString = Environment.GetEnvironmentVariable("GMC_SMETA_SQL_CONNECTION");
        var argv = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(argv, "--open");
        if (at >= 0 && at + 1 < argv.Length && int.TryParse(argv[at + 1], out var openId))
            _pendingOpen = openId;
        _pendingGmcs = argv.FirstOrDefault(a => a.EndsWith(".gmcs", StringComparison.OrdinalIgnoreCase));
        RootLayout.IsEnabled = false;
        DatabaseLabel.Text = "SQL Server · подключение…";
        ShowStartupMessage("Загрузка объектов и документов…");
    }

    private void ShowStartupMessage(string text)
    {
        StatusText.Text = text;
        ShowPage(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
            FontSize = 16,
        });
    }

    // No SQL in the constructor; the background reader must not touch WPF objects.
    // The connection is handed to the UI only after all startup reads finish.
    private (SqlConnection Connection, List<ObjectRow> Objects, List<DocRow> Documents) ReadStartupData()
    {
        if (_startupClosed) throw new OperationCanceledException();
        var cs = _sqlConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("Укажи GMC_SMETA_SQL_CONNECTION — подключение к отдельной тестовой базе SQL Server.");
        SqlServerWorkspace.RequirePilotDatabase(cs);
        StartupTrace.Mark("main_sql_open_begin");
        var connection = SqlServerDatabase.Open(cs);
        try
        {
            StartupTrace.Mark("main_sql_open_end");
            if (_startupClosed) throw new OperationCanceledException();
            StartupTrace.Mark("main_navigation_read_begin");
            var (objects, documents) = Workspace.ReadNavigation(connection);
            StartupTrace.Mark("main_navigation_read_end");
            if (_startupClosed) throw new OperationCanceledException();
            return (connection, objects, documents);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal async Task<bool> InitializeWorkspaceAsync(bool offerRetry = true)
    {
        Dispatcher.VerifyAccess();
        if (_ready) return true;
        if (_startupInProgress || _startupClosed) return false;
        _startupInProgress = true;
        try
        {
            while (!_startupClosed)
            {
                RootLayout.IsEnabled = false;
                ShowStartupMessage("Загрузка объектов и документов…");
                try
                {
                    var loaded = await Task.Run(ReadStartupData);
                    if (_startupClosed)
                    {
                        loaded.Connection.Dispose();
                        return false;
                    }
                    _serverConn = loaded.Connection;
                    _objects.AddRange(loaded.Objects);
                    _docs.AddRange(loaded.Documents);
                    Undo = new UndoManager(
                        id => { using var conn = SqlServerDatabase.Open(_sqlConnectionString!); return SqlServerUndo.Snapshot(conn, null, id); },
                        (id, target, expected, undo) => SqlServerUndo.Apply(_sqlConnectionString!, id, target, expected, undo));
                    var schema31Preview = SqlSchemaCompatibility.IsPreviewDatabase(_serverConn.Database);
                    Title = schema31Preview
                        ? WindowTitle + " — C# · схема 31 · ТОЛЬКО ЧТЕНИЕ · суммы не проверены"
                        : WindowTitle + " — C# · SQL Server";
                    DatabaseLabel.Text = SqlServerWorkspace.DescribeConnection(_sqlConnectionString!)
                        + (schema31Preview ? " · ПРОСМОТР v31 (не для расчётов и экспорта)" : "");
                    NewObjectButton.IsEnabled = SqlWritesEnabled;
                    ObjectNewKpButton.IsEnabled = SqlWritesEnabled;
                    ObjectPropertiesButton.IsEnabled = SqlWritesEnabled;
                    ObjectMoreButton.IsEnabled = SqlWritesEnabled;
                    ObjectRenameButton.IsEnabled = SqlWritesEnabled;
                    RestoreNavigationMode();
                    StartupTrace.Mark("main_tree_build_begin");
                    RefreshNavigation();
                    StartupTrace.Mark("main_tree_build_end");
                    ShowEmptyPage();
                    _ready = true;
                    RootLayout.IsEnabled = true;
                    StartupTrace.Mark("main_workspace_data_ready");
                    // CLI and .gmcs navigation must run only after the document list is available.
                    if (_pendingOpen != 0) OpenEstimate(_pendingOpen);
                    if (_pendingGmcs is not null) OpenGmcsLink(_pendingGmcs);
                    return true;
                }
                catch (Exception e)
                {
                    _serverConn?.Dispose();
                    _serverConn = null;
                    _ready = false;
                    _objects.Clear();
                    _docs.Clear();
                    if (_startupClosed) return false;
                    RootLayout.IsEnabled = false;
                    NavigationTree.Items.Clear();
                    _documentNodes.Clear();
                    _objectNodes.Clear();
                    _pendingNavigationDocuments.Clear();
                    StartupTrace.Mark("main_initialization_error");
                    try
                    {
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "GmcSmeta-Next-startup-error.txt"),
                            $"{DateTime.UtcNow:O}: {e.GetType().Name}");
                    }
                    catch { /* лог не критичен */ }
                    DatabaseLabel.Text = "SQL Server · не подключено";
                    ShowStartupMessage("Не удалось загрузить данные SQL Server.");
                    if (!offerRetry) return false;
                    if (MessageBox.Show(this, e.Message + "\n\nПовторить подключение?",
                            "Не удалось открыть базу", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                            MessageBoxResult.No) != MessageBoxResult.Yes)
                    {
                        Close();
                        return false;
                    }
                }
            }
            return false;
        }
        finally { _startupInProgress = false; }
    }

    private NavigationMode CurrentMode => ArchiveMode.IsChecked == true ? NavigationMode.Archive :
        KpMode.IsChecked == true ? NavigationMode.Kp : NavigationMode.Contracts;

    private void RestoreNavigationMode()
    {
        try
        {
            if (_navigationSettings.Load().Settings.NavigationMode == "kp")
            {
                _preferredNavigationMode = NavigationMode.Kp;
                KpMode.IsChecked = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LogError(ex); }
    }

    private void RememberNavigationMode()
    {
        var mode = CurrentMode;
        if (mode == NavigationMode.Archive || mode == _preferredNavigationMode) return;
        try
        {
            var current = _navigationSettings.Load().Settings;
            _navigationSettings.Save(current with { NavigationMode = mode == NavigationMode.Kp ? "kp" : "contracts" });
            _preferredNavigationMode = mode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LogError(ex); }
    }

    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        RememberNavigationMode();
        RefreshNavigation();
        ShowEmptyPage();
    }

    private void ObjectSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _navigationSearchTimer is null) return;
        _navigationSearchTimer.Stop();
        if (string.IsNullOrWhiteSpace(ObjectSearch.Text))
            ApplyNavigationFilter(); // programmatic Clear and Escape remain immediate
        else
            _navigationSearchTimer.Start();
    }

    private void ClearObjectSearch_Click(object sender, RoutedEventArgs e)
    {
        ObjectSearch.Clear();
        ObjectSearch.Focus();
    }

    private void ObjectSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || string.IsNullOrEmpty(ObjectSearch.Text)) return;
        ObjectSearch.Clear();
        e.Handled = true;
    }

    private void RestoreNavigationSearchExpansion()
    {
        if (_searchExpansionSnapshot is null) return;
        foreach (var (node, wasExpanded) in _searchExpansionSnapshot)
            node.IsExpanded = wasExpanded;
        _searchExpansionSnapshot = null;
    }

    // Python Nav: 16 DIP per level, 7 DIP vertical padding.
    // Indent only the row content, not its ItemsPresenter.
    private static Thickness NavigationRowPadding(int depth) => new(16 * Math.Max(0, depth), 7, 4, 7);

    private void RefreshNavigation()
    {
        _navigationSearchTimer?.Stop();
        // Сохраняем реальное состояние пользователя, а не раскрытые поиском узлы.
        RestoreNavigationSearchExpansion();
        if (_shownMode is { } previous)
            _expanded[previous] = Descendants(NavigationTree.Items).Where(n => n.IsExpanded)
                .Select(NodeKey).ToHashSet();
        NavigationTree.Items.Clear();
        _documentNodes.Clear();
        _objectNodes.Clear();
        _pendingNavigationDocuments.Clear();
        var years = Navigation.Years(_objects, _docs, CurrentMode, DateTime.Today.Year);
        _navigationSearchIndex = new NavigationSearchIndex(years.SelectMany(y => y.Objects));
        _expanded.TryGetValue(CurrentMode, out var opened);
        foreach (var year in years)
        {
            var yearNode = new TreeViewItem
            {
                Header = year.Year, Tag = year.Year, FontWeight = FontWeights.SemiBold,
                Padding = NavigationRowPadding(0),
                HeaderTemplate = (DataTemplate)FindResource("YearNavigationTemplate")
            };
            yearNode.IsExpanded = opened?.Contains(NodeKey(yearNode)) ??
                (CurrentMode == NavigationMode.Archive || year == years[0]);
            foreach (var group in year.Objects)
            {
                var node = new TreeViewItem
                {
                    Header = group.Object.Name, Tag = group.Object,
                    Padding = NavigationRowPadding(1),
                    HeaderTemplate = (DataTemplate)FindResource("ObjectNavigationTemplate"),
                    FontWeight = FontWeights.SemiBold, ToolTip = group.Object.Name
                };
                _objectNodes[group.Object.Id] = node;
                if (group.Documents.Count > 0)
                {
                    _pendingNavigationDocuments[group.Object.Id] = new(group.Documents, opened);
                    // Keep the usual WPF expand arrow without creating any
                    // real document containers for a collapsed object.
                    node.Items.Add(new TreeViewItem
                    {
                        Tag = NavigationPlaceholder,
                        Visibility = Visibility.Collapsed,
                        Focusable = false,
                        IsEnabled = false,
                    });
                    node.Expanded += ObjectNavigation_Expanded;
                }
                yearNode.Items.Add(node);
                node.IsExpanded = opened?.Contains(NodeKey(node)) ?? false;
                if (node.IsExpanded) EnsureObjectDocuments(node);
            }
            NavigationTree.Items.Add(yearNode);
        }
        _shownMode = CurrentMode;
        ApplyNavigationFilter();
    }

    private void ObjectNavigation_Expanded(object sender, RoutedEventArgs e)
    {
        // Expanded is a bubbling event; child document expansion is unrelated.
        if (sender is TreeViewItem node && ReferenceEquals(e.OriginalSource, node))
            EnsureObjectDocuments(node);
    }

    private void EnsureObjectDocuments(TreeViewItem node)
    {
        if (node.Tag is not ObjectRow obj ||
            !_pendingNavigationDocuments.TryGetValue(obj.Id, out var pending) ||
            !_objectNodes.TryGetValue(obj.Id, out var current) || !ReferenceEquals(node, current))
            return;

        // Build locally before replacing the placeholder. The existing
        // Navigation.Documents projection retains stage hierarchy and order.
        var opened = pending.Opened;
        var children = Navigation.Documents(pending.Documents)
            .Select(doc => DocumentNode(doc, opened, 2)).ToList();
        _pendingNavigationDocuments.Remove(obj.Id);
        node.Expanded -= ObjectNavigation_Expanded;
        node.Items.Clear();
        foreach (var child in children) node.Items.Add(child);
    }

    private TreeViewItem DocumentNode(NavigationDocument entry, HashSet<string>? opened, int depth)
    {
        var node = new TreeViewItem
        {
            Header = entry.Document, Tag = entry.Document,
            Padding = NavigationRowPadding(depth),
            HeaderTemplate = (DataTemplate)FindResource("DocumentNavigationTemplate"),
            ToolTip = entry.Document.NavigationTip,
            FontStyle = entry.Document.StageRest ? FontStyles.Italic : FontStyles.Normal
        };
        System.Windows.Automation.AutomationProperties.SetName(node, entry.Document.NavigationName);
        node.IsExpanded = entry.Children.Count > 0 || opened?.Contains(NodeKey(node)) == true;
        _documentNodes[entry.Document.Id] = node;
        foreach (var child in entry.Children) node.Items.Add(DocumentNode(child, opened, depth + 1));
        return node;
    }

    private void ApplyNavigationFilter()
    {
        var searching = !string.IsNullOrWhiteSpace(ObjectSearch.Text);
        if (searching)
            _searchExpansionSnapshot ??= Descendants(NavigationTree.Items)
                .ToDictionary(node => node, node => node.IsExpanded);

        _navigationSearchTimer?.Stop();
        var visible = _navigationSearchIndex.Match(ObjectSearch.Text);
        foreach (TreeViewItem year in NavigationTree.Items)
        {
            foreach (TreeViewItem node in year.Items)
            {
                var matched = node.Tag is ObjectRow obj && visible.Contains(obj.Id);
                var visibility = matched ? Visibility.Visible : Visibility.Collapsed;
                if (node.Visibility != visibility) node.Visibility = visibility;
                // Совпадение в названии документа тоже должно быть видно под объектом.
                if (searching && matched)
                {
                    EnsureObjectDocuments(node);
                    if (!node.IsExpanded) node.IsExpanded = true;
                }
            }
            year.Visibility = year.Items.Cast<TreeViewItem>().Any(n => n.Visibility == Visibility.Visible)
                ? Visibility.Visible : Visibility.Collapsed;
            if (searching && year.Visibility == Visibility.Visible)
                year.IsExpanded = true;
        }
        if (!searching) RestoreNavigationSearchExpansion();
    }

    private static string NodeKey(TreeViewItem node) => node.Tag switch
    {
        ObjectRow o => "obj:" + o.Id, DocRow d => "doc:" + d.Id, _ => "year:" + node.Tag
    };

    private static IEnumerable<TreeViewItem> Descendants(ItemCollection items)
    {
        foreach (TreeViewItem node in items)
        {
            if (ReferenceEquals(node.Tag, NavigationPlaceholder)) continue;
            yield return node;
            foreach (var child in Descendants(node.Items)) yield return child;
        }
    }

    private void NavigationTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!_ready || _syncingNavigation || e.NewValue is not TreeViewItem node ||
            ReferenceEquals(node.Tag, NavigationPlaceholder)) return;
        if (node.Tag is DocRow doc) OpenEstimate(doc.Id);
        else if (node.Tag is ObjectRow obj) ShowObject(obj);
        else ShowEmptyPage();
    }

    // UI-only sequence; superseded object-card results never replace the
    // currently open page when a user navigates quickly.
    private int _objectCardLoadRequest;
    private readonly Dictionary<(int ObjectId, NavigationMode Mode), ObjectDocuments> _objectCardCache = new();
    private string? _objectCardCacheRevision;

    private async void ShowObject(ObjectRow o)
    {
        if (_startupClosed || _serverConn is null || _sqlConnectionString is not { } connectionString) return;

        // Только команды, перенесённые на SQL Server; вид страницы объекта — как в Python.
        if (ObjectRenameBox.Visibility == Visibility.Visible) EndPageRename();
        if (_selectedObject?.Id != o.Id) ObjectCardsScroll.ScrollToTop();
        ObjectTotals.Visibility = ObjectCardsScroll.Visibility = Visibility.Collapsed;
        ObjectCardsStatus.Text = "Загружаются документы объекта…";
        ObjectCardsStatus.Visibility = Visibility.Visible;
        _selectedObject = o;
        _openDocumentId = null;
        ShowPage(ObjectPage);

        var truncated = o.Name.Length > 110;
        var name = truncated ? o.Name[..105].TrimEnd(' ', ',', '.') + "…" : o.Name;
        ObjectTitle.Text = name + (o.Archived ? "   (архив" + (o.ArchivedOn.Length >= 4 ? " " + o.ArchivedOn[..4] : "") + ")" : "");
        ObjectTitle.ToolTip = truncated ? o.Name : "Двойной щелчок — переименовать объект";
        ObjectNewKpButton.Visibility = ObjectPropertiesButton.Visibility =
            ObjectMoreButton.Visibility = ObjectRenameButton.Visibility = ObjectCrumb.Visibility = Visibility.Visible;
        ObjectNewKpButton.IsEnabled = SqlWritesEnabled && !o.Archived;
        ObjectPropertiesButton.IsEnabled = SqlWritesEnabled && !o.Archived;
        ObjectMoreButton.IsEnabled = SqlWritesEnabled;
        ObjectRenameButton.IsEnabled = SqlWritesEnabled && !o.Archived;
        ObjectTitle.TextWrapping = TextWrapping.Wrap;
        var parts = new[] { o.Customer.Length > 0 ? "заказчик: " + o.Customer : "", o.Address,
            o.Folder.Length > 0 ? "папка: " + o.Folder : "" }.Where(p => p.Length > 0).ToList();
        ObjectSubtitle.Text = parts.Count > 0 ? string.Join(" · ", parts) : "Заказчик, адрес и папку документов можно указать в «Свойствах».";
        ObjectSubtitle.TextWrapping = TextWrapping.Wrap;
        // Never execute contract scans, totals, finance and card projections
        // on the WPF UI thread. Keep input order and current navigation mode
        // stable while the background read runs on its own SQL connection.
        var request = ++_objectCardLoadRequest;
        var documentsSnapshot = _docs.ToArray();
        var mode = CurrentMode;
        var navigationGeneration = _estimateOpenGeneration;
        bool IsCurrent() => !_startupClosed &&
            request == _objectCardLoadRequest && navigationGeneration == _estimateOpenGeneration &&
            mode == CurrentMode && connectionString == _sqlConnectionString &&
            ReferenceEquals(PageHost.Content, ObjectPage) && _selectedObject?.Id == o.Id;
        ObjectDocumentTools.Visibility = mode == NavigationMode.Contracts ? Visibility.Visible : Visibility.Collapsed;
        ObjectTotals.DataContext = null;
        DocumentCards.ItemsSource = null;
        StatusText.Text = $"{o.Name} · загрузка карточек SQL Server…";
        var cacheKey = (o.Id, mode);
        var cached = _objectCardCache.GetValueOrDefault(cacheKey);
        var cachedRevision = _objectCardCacheRevision;
        (ObjectDocuments Documents, string Revision) loaded;
        try
        {
            loaded = await Task.Run(() =>
            {
                using var read = SqlServerDatabase.Open(connectionString);
                var before = SqlServerDatabase.Revision(read);
                if (string.IsNullOrWhiteSpace(before) ||
                    !Workspace.ObjectCardMetadataMatches(read, o, documentsSnapshot))
                    throw new InvalidOperationException("Состав документов или реквизиты объекта изменились. Нажмите F5.");
                var result = cached is not null && cachedRevision == before
                    ? cached
                    : Workspace.ReadObjectDocuments(read, o, documentsSnapshot, mode);
                if (before != SqlServerDatabase.Revision(read))
                    throw new InvalidOperationException("Данные изменились во время загрузки карточек. Нажмите F5.");
                return (result, before);
            });
        }
        catch (Exception ex)
        {
            if (IsCurrent())
            {
                ObjectCardsStatus.Text = "Не удалось загрузить документы. Нажмите F5, чтобы повторить.";
                StatusText.Text = "Не удалось загрузить документы: " + ex.Message;
                LogError(ex);
            }
            return;
        }
        // A different object/estimate may have been selected before SQL replied.
        if (!IsCurrent()) return;
        if (_objectCardCacheRevision != loaded.Revision || _objectCardCache.Count >= 24)
            _objectCardCache.Clear();
        _objectCardCacheRevision = loaded.Revision;
        var documents = loaded.Documents;
        _objectCardCache[cacheKey] = documents;
        ObjectTotals.DataContext = documents;
        _objectTileLayout = null;
        ObjectProgressCard.Visibility = documents.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        // The final tile is the real Python "Новое КП" action. Financial
        // totals and document count intentionally use documents.Cards only.
        DocumentCards.ItemsSource = ObjectCardLayout.AppendNewContract(
            documents.Cards, ObjectNewKpButton.IsEnabled);
        ObjectCardsStatus.Visibility = Visibility.Collapsed;
        ObjectTotals.Visibility = ObjectCardsScroll.Visibility = Visibility.Visible;
        ObjectTotalValue.Text = Money.Format(documents.Total);
        ObjectTotalSub.Visibility = documents.VatTop > .005m ? Visibility.Visible : Visibility.Collapsed;
        ObjectTotalSub.Text = $"материалы + СМР {Money.Format(documents.Total - documents.VatTop)} + НДС сверху {Money.Format(documents.VatTop)}";
        ObjectMaterialsValue.Text = Money.Format(documents.Materials);
        ObjectSmrValue.Text = Money.Format(documents.Smr);
        ObjectMaterialsCaption.Text = documents.VatTop > .005m ? "Материалы (без НДС)" : "Материалы";
        ObjectSmrCaption.Text = documents.VatTop > .005m ? "СМР (без НДС)" : "СМР";
        ResizeCards();
        StatusText.Text = $"{o.Name}  ·  документов: {documents.Cards.Count}  ·  SQL Server";
    }

    private DirectorView CreateDirectorView()
    {
        var view = new DirectorView(_sqlConnectionString!);
        view.ObjectOpenRequested += id =>
        {
            var obj = _objects.FirstOrDefault(o => o.Id == id);
            if (obj is not null) ShowObject(obj);
        };
        return view;
    }

    private void ShowHome()
    {
        if (_serverConn is null) return;
        if (NavigationTree.SelectedItem is TreeViewItem node) node.IsSelected = false;
        _directorView ??= CreateDirectorView();
        ResetPageSelection();
        ShowPage(_directorView);
        StatusText.Text = "Сводка по объектам · SQL Server";
    }

    private void ShowEmptyPage()
    {
        ResetPageSelection();
        ShowPage(null);
        StatusText.Text = "SQL Server · выберите объект или документ слева";
    }

    private void ResetPageSelection()
    {
        EndPageRename();
        _openDocumentId = null;
        _selectedObject = null;
        DocumentCards.ItemsSource = null;
        ObjectTotals.DataContext = null;
    }

    private void ShowPage(FrameworkElement? page)
    {
        ++_estimateOpenGeneration; // Invalidate background navigation, including object/home switches.
        CancelEstimateCacheValidation();
        PageHost.Content = page;
        EmptyPage.Visibility = page is null ? Visibility.Visible : Visibility.Collapsed;
        ScheduleEstimateCacheTrim();
    }

    private void Home_Click(object sender, RoutedEventArgs e) => ShowHome();

    private void NewObject_Click(object sender, RoutedEventArgs e) => EditObjectProperties(null);
    private void ObjectProperties_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedObject is { } obj) EditObjectProperties(obj.Id);
    }
    private void EditObjectProperties(int? id)
    {
        try
        {
            EditSqlServerObjectProperties(id);
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Не удалось сохранить объект", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------------------------------------------------------- документы объекта

    private void ObjectNewContract_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedObject is { } obj) NewContractFlow(obj.Id);
    }

    /// <summary>
    /// Новое КП / договор / допсоглашение или копия документа — NewContractDialog Python.
    /// Пустое содержимое создаёт документ, «копия» повторяет выбранный документ со сметой.
    /// </summary>
    private void NewContractFlow(int objectId, string kind = "kp", long? baseId = null, long? copyFrom = null) =>
        NewSqlServerContractFlow(objectId, kind, baseId, copyFrom);

    /// <summary>
    /// Свойства документа — ContractDialog Python, с кнопки «Сделать договором» тип принудительно
    /// становится договором. Подписанные документы и этапы не меняются.
    /// </summary>
    private void EditContractProperties(DocRow doc, bool makeContract = false) =>
        EditSqlServerContractProperties(doc, makeContract);

    // Обёртки для редактора сметы (kp_editor «Действия» → документ целиком).
    internal void OpenContractProperties(DocRow doc) => EditContractProperties(doc, false);

    internal void MakeCurrentDocumentContract(DocRow doc) => EditContractProperties(doc, true);

    internal void CopyCurrentDocument(DocRow doc) =>
        NewContractFlow(doc.ObjectId, doc.Kind, copyFrom: doc.Id);

    internal void CreateDopAgreement(DocRow doc) =>
        NewContractFlow(doc.ObjectId, "dop", baseId: doc.Id);

    internal void DeleteCurrentDocument(DocRow doc) => DeleteSqlServerContractFlow(doc);

    internal void CompareCurrentDocument(DocRow doc) => OpenCompare(doc);

    /// <summary>Перечитать объекты и документы, перестроить дерево и выбрать объект — как refresh(("obj", id)) в Python.</summary>
    private void ReloadAndSelect(int objectId)
    {
        ReloadNavigationData();
        if (_objects.FirstOrDefault(o => o.Id == objectId) is not { } obj)
        {
            RefreshNavigation();
            ShowEmptyPage();
            StatusText.Text = "Объект больше недоступен";
            return;
        }
        SelectObjectFromNavigation(obj);
    }

    private void ReloadNavigationData()
    {
        var reader = _serverConn
            ?? throw new InvalidOperationException("Нет открытого подключения к SQL Server.");
        var (objects, documents) = Workspace.ReadNavigation(reader);
        _objects.Clear(); _objects.AddRange(objects);
        _docs.Clear(); _docs.AddRange(documents);
    }

    private void SelectObjectFromNavigation(ObjectRow obj)
    {
        var docsHere = _docs.Where(d => d.ObjectId == obj.Id).ToList();
        var hasKp = docsHere.Any(d => d.Kind == "kp");
        var hasContract = docsHere.Any(d => d.Kind is "main" or "dop");
        var mode = obj.Archived ? NavigationMode.Archive
            : CurrentMode == NavigationMode.Kp && hasKp ? NavigationMode.Kp
            : CurrentMode == NavigationMode.Contracts && hasContract ? NavigationMode.Contracts
            : hasKp || docsHere.Count == 0 ? NavigationMode.Kp : NavigationMode.Contracts;
        _ready = false;
        (mode == NavigationMode.Archive ? ArchiveMode : mode == NavigationMode.Kp ? KpMode : ContractsMode).IsChecked = true;
        ObjectSearch.Clear();
        _ready = true;
        RememberNavigationMode();
        RefreshNavigation();
        if (_objectNodes.TryGetValue(obj.Id, out var node))
        {
            if (ItemsControl.ItemsControlFromItemContainer(node) is TreeViewItem year) year.IsExpanded = true;
            _syncingNavigation = true;
            try { node.IsSelected = true; }
            finally { _syncingNavigation = false; }
            node.BringIntoView();
        }
        ShowObject(obj);
    }

    internal int SaveObjectProperties(ObjectProperties properties, int? id = null)
    {
        var result = SaveSqlServerObjectProperties(properties, id);
        ReloadAndSelect(result);
        return result;
    }

    // ---------------------------------------------------------------- действия объекта

    /// <summary>Меню «Ещё» страницы объекта.</summary>
    private void ObjectMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not UIElement anchor || _selectedObject is not { } obj) return;
        var connection = _sqlConnectionString;
        var mode = CurrentMode;
        var menu = new ContextMenu();

        void Run(Action<ObjectRow> action, bool writes = true)
        {
            var fresh = _objects.FirstOrDefault(o => o.Id == obj.Id);
            if (!ReferenceEquals(PageHost.Content, ObjectPage) || _selectedObject?.Id != obj.Id ||
                connection != _sqlConnectionString || mode != CurrentMode ||
                fresh is null || fresh.Archived != obj.Archived || fresh.ArchivedOn != obj.ArchivedOn ||
                (writes && !SqlWritesEnabled))
            {
                StatusText.Text = "Объект или режим изменились. Откройте меню заново.";
                return;
            }
            action(fresh);
        }

        var import = ContextMenuIcons.Create("Загрузить КП / смету из Excel…", "Upload", SqlWritesEnabled && !obj.Archived);
        import.Click += (_, _) => Run(current => _ = ImportExcelForObjectAsync(current.Id));
        menu.Items.Add(import);
        if (mode != NavigationMode.Contracts)
        {
            var passports = ContextMenuIcons.Create("Паспорта вентиляционных систем…", "File");
            passports.Click += (s, args) => Run(_ => ObjectPassports_Click(s, args), writes: false);
            menu.Items.Add(passports);
        }
        menu.Items.Add(new Separator());
        var finish = ContextMenuIcons.Create(obj.Archived ? "Вернуть в работу" : "Завершить объект (в архив)", "Check", SqlWritesEnabled);
        finish.Click += (_, _) => Run(FinishSqlServerObject);
        menu.Items.Add(finish);
        // Python object_view.act_year is visible only for archived objects.
        if (obj.Archived)
        {
            var year = ContextMenuIcons.Create("Изменить год в архиве…", "History", SqlWritesEnabled);
            year.Click += (_, _) => Run(ChangeSqlServerObjectArchiveYear);
            menu.Items.Add(year);
        }
        menu.Items.Add(new Separator());
        var delete = ContextMenuIcons.Create("Удалить объект…", "Trash", SqlWritesEnabled, danger: true);
        delete.Click += (_, _) => Run(DeleteSqlServerObjectFlow);
        menu.Items.Add(delete);
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Завершить объект (в архив) или вернуть в работу — finish_object эталона.</summary>
    private void FinishSqlServerObject(ObjectRow obj)
    {
        if (!SqlWritesEnabled)
        {
            StatusText.Text = "Изменения разрешены только в GmcSmetaPilot_*.";
            return;
        }
        if (!obj.Archived)
        {
            var question = $"Объект «{obj.Name}» закончен?\n\nОн уйдёт в «Архив» (вкладка над списком объектов), " +
                $"в {DateTime.Today.Year} год. Ничего не удаляется, вернуть можно в любой момент.";
            if (MessageBox.Show(this, question, "Завершить объект", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }
        try
        {
            SqlServerWorkspace.SetObjectArchived(
                _sqlConnectionString!, obj.Id, !obj.Archived, SqlServerDatabase.Revision(_serverConn!));
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Не удалось изменить объект", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ReloadAndSelect(obj.Id);
        StatusText.Text = obj.Archived ? "Объект возвращён в работу" : "Объект в архиве — вкладка «Архив» над списком";
    }

    /// <summary>Изменить год в архиве — change_archive_year эталона.</summary>
    private void ChangeSqlServerObjectArchiveYear(ObjectRow obj)
    {
        if (!obj.Archived) return;
        if (!SqlWritesEnabled)
        {
            StatusText.Text = "Изменения разрешены только в GmcSmetaPilot_*.";
            return;
        }
        var current = obj.ArchivedOn.Length >= 4 && int.TryParse(obj.ArchivedOn[..4], out var year)
            ? year : DateTime.Today.Year;
        var dialog = new ArchiveYearDialog(obj.Name, current, DateTime.Today.Year) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Year == current) return;
        try
        {
            SqlServerWorkspace.ChangeArchiveYear(
                _sqlConnectionString!, obj.Id, dialog.Year, SqlServerDatabase.Revision(_serverConn!));
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Не удалось изменить год", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ReloadAndSelect(obj.Id);
        StatusText.Text = $"Объект перенесён в архив {dialog.Year} года";
    }

    /// <summary>Удалить объект: два подтверждения, копия в корзину — main_window.delete_object эталона.</summary>
    private void DeleteSqlServerObjectFlow(ObjectRow obj)
    {
        if (!SqlWritesEnabled)
        {
            StatusText.Text = "Изменения разрешены только в GmcSmetaPilot_*.";
            return;
        }
        SqlServerWorkspace.ObjectDeleteInfo info;
        string confirmedRevision;
        try
        {
            confirmedRevision = SqlServerDatabase.Revision(_serverConn!);
            info = SqlServerWorkspace.DescribeObjectForDelete(_serverConn!, null, obj.Id);
            if (string.IsNullOrWhiteSpace(confirmedRevision) ||
                confirmedRevision != SqlServerDatabase.Revision(_serverConn!))
                throw new InvalidOperationException("Данные изменились во время проверки. Повторите операцию после обновления.");
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Не удалось открыть объект", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var bullets = string.Join("\n", info.Details.Select(d => "• " + d));
        var question = $"Удалить объект «{info.Name}»?\n\n" + (bullets.Length > 0 ? bullets : "Объект пустой.") +
            "\n\nЕсли объект просто закончен — лучше «Завершить объект (в архив)».";
        if (MessageBox.Show(this, question, "Удаление объекта", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (new ConfirmDeleteDialog(info.Name, info.Details) { Owner = this }.ShowDialog() != true) return;
        try
        {
            SqlServerWorkspace.DeleteObject(
                _sqlConnectionString!, obj.Id, confirmedRevision);
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Не удалось удалить объект", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Python: tree.setCurrentItem(None); refresh(None) — пустой список и главная страница.
        _syncingNavigation = true;
        try { if (NavigationTree.SelectedItem is TreeViewItem selected) selected.IsSelected = false; }
        finally { _syncingNavigation = false; }
        _selectedObject = null;
        ReloadNavigationData();
        RefreshNavigation();
        ShowEmptyPage();
    }

    /// <summary>Удалить документ: вопросы и копия в корзину — kp_editor.delete_contract эталона.</summary>
    private void DeleteSqlServerContractFlow(DocRow doc)
    {
        if (!SqlWritesEnabled)
        {
            StatusText.Text = "Изменения разрешены только в GmcSmetaPilot_*.";
            return;
        }
        SqlServerWorkspace.ContractDeleteInfo info;
        string confirmedRevision;
        try
        {
            confirmedRevision = SqlServerDatabase.Revision(_serverConn!);
            info = SqlServerWorkspace.DescribeContractForDelete(_serverConn!, null, doc.Id);
            if (string.IsNullOrWhiteSpace(confirmedRevision) ||
                confirmedRevision != SqlServerDatabase.Revision(_serverConn!))
                throw new InvalidOperationException("Данные изменились во время проверки. Повторите операцию после обновления.");
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Не удалось открыть документ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var question = $"Удалить «{info.Title}» из объекта «{info.ObjectName}»?\n" +
            "Все системы и позиции документа будут удалены.";
        if (MessageBox.Show(this, question, "Удаление", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (info.Details.Count > 0 &&
            new ConfirmDeleteDialog(info.Title, info.Details, "документ") { Owner = this }.ShowDialog() != true) return;
        try
        {
            SqlServerWorkspace.DeleteContract(
                _sqlConnectionString!, doc.Id, confirmedRevision);
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Не удалось удалить документ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Python: structure_changed.emit(("obj", oid)) — страница объекта без удалённого документа.
        ReloadAndSelect(doc.ObjectId);
        StatusText.Text = $"Документ удалён: «{info.Title}». Копия хранится 30 дней в корзине";
    }

    // ---------------------------------------------------------------- переименование на месте

    /// <summary>Переименовать объект через новую запись и обновить списки — Python rename_object.</summary>
    private void SaveObjectRename(int objectId, string name)
    {
        try
        {
            SaveSqlServerObjectRename(objectId, name);
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Не удалось переименовать объект", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ReloadAndSelect(objectId);
        StatusText.Text = $"Объект переименован: «{name.Trim()}»";
    }

    /// <summary>Двойной щелчок по заголовку объекта — как eventFilter в Python ObjectPage.</summary>
    private void ObjectTitle_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && _selectedObject is not null)
        {
            BeginPageRename();
            e.Handled = true;
        }
    }

    private void ObjectRename_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedObject is not null) BeginPageRename();
    }

    private void BeginPageRename()
    {
        if (_selectedObject is not { } obj) return;
        if (_serverConn is not null && (!SqlWritesEnabled || obj.Archived)) return;
        ObjectRenameBox.Text = obj.Name;
        ObjectTitle.Visibility = ObjectRenameButton.Visibility = Visibility.Collapsed;
        ObjectRenameBox.Visibility = Visibility.Visible;
        ObjectRenameBox.Focus();
        ObjectRenameBox.SelectAll();
    }

    private void EndPageRename()
    {
        if (ObjectRenameBox.Visibility != Visibility.Visible) return;
        ObjectRenameBox.Visibility = Visibility.Collapsed;
        ObjectTitle.Visibility = ObjectRenameButton.Visibility = Visibility.Visible;
    }

    private void ObjectRenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Escape) return;
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            EndPageRename();        // Escape — отмена, как в Python
            return;
        }
        SavePageRename();
    }

    private void ObjectRenameBox_LostFocus(object sender, RoutedEventArgs e) => SavePageRename();

    /// <summary>returnPressed/editingFinished: пустое или совпадающее имя ничего не пишет.</summary>
    private void SavePageRename()
    {
        if (ObjectRenameBox.Visibility != Visibility.Visible || _selectedObject is not { } obj) return;
        var name = ObjectRenameBox.Text.Trim();
        EndPageRename();
        if (name.Length > 0 && name != obj.Name) SaveObjectRename(obj.Id, name);
    }

    private void DocumentCard_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: int id } button ||
            _docs.FirstOrDefault(doc => doc.Id == id) is not { } target) return;
        var menu = new ContextMenu { PlacementTarget = button,
            Placement = e.CursorLeft < 0 && e.CursorTop < 0
                ? System.Windows.Controls.Primitives.PlacementMode.Bottom
                : System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        AddDocumentFileMenu(menu, target);
        menu.IsOpen = true;
    }

    private void AddDocumentFileMenu(ContextMenu menu, DocRow doc)
    {
        var connection = _sqlConnectionString;
        bool ValidTarget() => connection is not null && connection == _sqlConnectionString &&
            _docs.Any(current => current.Id == doc.Id && current.Kind == doc.Kind);
        if (doc.Kind is "main" or "dop")
        {
            var signed = ContextMenuIcons.Create("Подписанный PDF", "Check");
            signed.Click += (_, _) =>
            {
                if (ValidTarget()) AttachmentWindow.OpenSigned(this, connection!, doc.Id, doc.Title);
            };
            menu.Items.Add(signed);
            var select = ContextMenuIcons.Create("Добавить / выбрать подписанный PDF…", "File");
            select.Click += (_, _) =>
            {
                if (ValidTarget()) new AttachmentWindow(connection!, doc.Id, doc.Title, signedOnly: true)
                    { Owner = this }.ShowDialog();
            };
            menu.Items.Add(select);
        }
        var attachments = ContextMenuIcons.Create("Файлы документа…", "Folder");
        attachments.Click += (_, _) =>
        {
            if (ValidTarget()) new AttachmentWindow(connection!, doc.Id, doc.Title) { Owner = this }.ShowDialog();
        };
        menu.Items.Add(attachments);
        if (doc.Kind is "main" or "dop")
        {
            var letters = ContextMenuIcons.Create("Письма договора…", "File", SqlWritesEnabled);
            letters.Click += (_, _) => { if (ValidTarget()) OpenContractDocuments(doc, false); };
            menu.Items.Add(letters);
            if (PtoUserSettings.Enabled)
            {
                var pto = ContextMenuIcons.Create("АОСР договора…", "File", SqlWritesEnabled);
                pto.Click += (_, _) => { if (ValidTarget()) OpenContractDocuments(doc, true); };
                menu.Items.Add(pto);
            }
        }
    }

    private void OpenContractDocuments(DocRow target, bool pto)
    {
        if (!SqlWritesEnabled || _sqlConnectionString is not { } connection ||
            (pto && !PtoUserSettings.Enabled)) return;
        var doc = _docs.FirstOrDefault(d => d.Id == target.Id && d.ObjectId == target.ObjectId &&
            d.Kind == target.Kind && (d.Kind is "main" or "dop"));
        if (doc is null || _objects.FirstOrDefault(o => o.Id == doc.ObjectId) is not { } obj) return;
        RunNavigationAction(pto ? "АОСР договора" : "Письма договора", () =>
        {
            Window dialog = pto ? new PtoWindow(connection, obj.Id, doc.Id) :
                new LettersWindow(connection, obj, contractContext: doc);
            dialog.Owner = this;
            dialog.ShowDialog();
        });
    }

    private void CardLetters_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (CardFileTarget(sender) is { } doc) OpenContractDocuments(doc, false);
    }

    private void CardPto_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (CardFileTarget(sender) is { } doc) OpenContractDocuments(doc, true);
    }

    private DocRow? CardFileTarget(object sender) =>
        sender is Button { Tag: int id } ? _docs.FirstOrDefault(doc => doc.Id == id && doc.Kind is "main" or "dop") : null;

    private void CardSignedPdf_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (CardFileTarget(sender) is { } doc && _sqlConnectionString is { } connection)
            AttachmentWindow.OpenSigned(this, connection, doc.Id, doc.Title);
    }

    private void CardSignedPdfMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || CardFileTarget(sender) is not { } doc ||
            _sqlConnectionString is not { } connection) return;
        var menu = new ContextMenu { PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var select = ContextMenuIcons.Create("Добавить / выбрать подписанный PDF…", "File");
        select.Click += (_, _) =>
        {
            if (connection == _sqlConnectionString && _docs.Any(current => current.Id == doc.Id && current.Kind == doc.Kind))
                new AttachmentWindow(connection, doc.Id, doc.Title, signedOnly: true) { Owner = this }.ShowDialog();
        };
        menu.Items.Add(select);
        menu.IsOpen = true;
    }

    private void CardFiles_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (CardFileTarget(sender) is { } doc && _sqlConnectionString is { } connection)
            new AttachmentWindow(connection, doc.Id, doc.Title) { Owner = this }.ShowDialog();
    }

    private void DocumentCard_Click(object sender, RoutedEventArgs e)
    {
        if (!e.Handled && ReferenceEquals(e.OriginalSource, sender) && sender is Button { Tag: int id }) OpenEstimate(id);
    }

    private void ObjectCards_SizeChanged(object sender, SizeChangedEventArgs e) => ResizeCards();
    private void ObjectCards_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0) ResizeCards();
    }

    private void ResizeCards()
    {
        if (ObjectCardsScroll is null) return;
        var width = Math.Max(300, ObjectCardsScroll.ViewportWidth > 0 ? ObjectCardsScroll.ViewportWidth : ObjectCardsScroll.ActualWidth - 18);
        var columns = Math.Clamp((int)((width + 12) / 352), 1, 3);
        // WrapPanel учитывает завершающий отступ каждой карточки, включая последнюю.
        CardWidth = Math.Floor(width / columns) - 12;
        LayoutObjectTotals();
    }

    private void LayoutObjectTotals()
    {
        if (ObjectTotals.DataContext is not ObjectDocuments documents) return;
        var state = (Wide: PageHost.ActualWidth >= 1100, Progress: documents.ShowProgress);
        if (_objectTileLayout == state) return;
        _objectTileLayout = state;
        ObjectTotals.RowDefinitions.Clear();
        ObjectTotals.ColumnDefinitions.Clear();
        var cards = new[] { ObjectTotalCard, ObjectProgressCard, ObjectMaterialsCard, ObjectSmrCard, ObjectEarnedCard };
        if (state.Wide)
        {
            ObjectTotals.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var visible = cards.Where(c => c != ObjectProgressCard || state.Progress).ToList();
            for (var i = 0; i < visible.Count; i++)
            {
                ObjectTotals.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(visible[i] == ObjectProgressCard ? 4 : 2, GridUnitType.Star) });
                Position(visible[i], 0, i);
                visible[i].Margin = new Thickness(0, 0, i == visible.Count - 1 ? 0 : 10, 0);
            }
        }
        else
        {
            for (var i = 0; i < 3; i++) ObjectTotals.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < 2; i++) ObjectTotals.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Position(ObjectTotalCard, 0, 0, state.Progress ? 1 : 3);
            Position(ObjectProgressCard, 0, 1, 2);
            Position(ObjectMaterialsCard, 1, 0); Position(ObjectSmrCard, 1, 1); Position(ObjectEarnedCard, 1, 2);
            ObjectTotalCard.Margin = new Thickness(0, 0, state.Progress ? 10 : 0, 10);
            ObjectProgressCard.Margin = new Thickness(0, 0, 0, 10);
            ObjectMaterialsCard.Margin = ObjectSmrCard.Margin = new Thickness(0, 0, 10, 0);
            ObjectEarnedCard.Margin = new Thickness(0);
        }
    }

    private static void Position(FrameworkElement element, int row, int column, int span = 1)
    {
        Grid.SetRow(element, row); Grid.SetColumn(element, column); Grid.SetColumnSpan(element, span);
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar()
    {
        var hide = Sidebar.Visibility == Visibility.Visible;
        if (hide) _sidebarWidth = SidebarColumn.ActualWidth;
        SidebarColumn.MinWidth = hide ? 0 : 270;
        SidebarColumn.Width = new GridLength(hide ? 0 : _sidebarWidth);
        SplitterColumn.Width = new GridLength(hide ? 0 : 1);
        Sidebar.Visibility = SidebarSplitter.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        ShowSidebarButton.Visibility = hide ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_ready) return;
        if (SqlWritesEnabled && e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            EditObjectProperties(null); e.Handled = true; return;
        }
        if (SqlWritesEnabled && e.Key == Key.I && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ImportExcelFromObject_Click(this, e);
            return;
        }
        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleSidebar();
            e.Handled = true; return;
        }
        if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None)
        {
            RenameSelectedObject();
            e.Handled = true;
        }
    }

    /// <summary>Объект, над которым выполняются команды: выделенный в дереве или открытая страница объекта.</summary>
    private ObjectRow? CommandObject()
    {
        if (NavigationTree.SelectedItem is TreeViewItem { Tag: ObjectRow o }) return o;
        if (NavigationTree.SelectedItem is TreeViewItem { Tag: DocRow d })
            return _objects.FirstOrDefault(x => x.Id == d.ObjectId);
        return _selectedObject;
    }

    /// <summary>F2 / «Переименовать» — Python редактирует имя прямо в списке, здесь короткий запрос.</summary>
    private void RenameSelectedObject()
    {
        RenameObject(CommandObject());
    }

    private void RenameObject(ObjectRow? target)
    {
        if (!SqlWritesEnabled || target is null ||
            _objects.FirstOrDefault(obj => obj.Id == target.Id && !obj.Archived) is not { } obj) return;
        var dialog = new PromptDialog("Переименовать объект", "Название объекта", obj.Name) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Value.Length > 0 && dialog.Value != obj.Name)
            SaveObjectRename(obj.Id, dialog.Value);
    }

    // ---------------------------------------------------------------- контекстное меню дерева

    /// <summary>Меню дерева — как _tree_menu в Python, пока только команды, перенесённые на SQL Server.</summary>
    private void NavigationTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Do not expose legacy archive, deletion or other not-yet-ported operations.
        if (!SqlWritesEnabled)
        {
            NavigationTree.ContextMenu = null;
            e.Handled = true;
            return;
        }
        // ПКМ по пустой области не должен использовать ранее выбранный объект.
        // При клавиатурном вызове целью остаётся выбранный узел.
        TreeViewItem? target = e.CursorLeft < 0 && e.CursorTop < 0
            ? NavigationTree.SelectedItem as TreeViewItem
            : NavigationNodeUnderMouse();
        var menu = new ContextMenu();
        var create = ContextMenuIcons.Create("Новый объект…", "FolderPlus");
        create.Click += (_, _) => EditObjectProperties(null);
        menu.Items.Add(create);
        // В Python «Корзина» лежит в главном меню ☰; пока оно не перенесено — здесь.
        var trash = ContextMenuIcons.Create("Корзина (удалённые объекты и документы)…", "Trash");
        trash.Click += (_, _) => OpenTrash();
        menu.Items.Add(trash);
        var history = ContextMenuIcons.Create("Журнал изменений…", "History");
        history.Click += (_, _) => OpenHistory(null);
        menu.Items.Add(history);

        if (target is { Tag: ObjectRow selected })
        {
            menu.Items.Add(new Separator());
            // Bind each command to the node under the cursor, never CommandObject()
            // or the previously opened object card.
            void AddObjectCommand(string title, Action<ObjectRow> action, string glyph, bool enabled = true, string shortcut = "", bool danger = false)
            {
                var item = ContextMenuIcons.Create(title, glyph, enabled, shortcut, danger);
                item.Click += (_, _) =>
                {
                    if (!SqlWritesEnabled || _objects.FirstOrDefault(obj => obj.Id == selected.Id) is not { } fresh ||
                        fresh.Archived != selected.Archived)
                    {
                        StatusText.Text = "Объект изменился. Обновите список и повторите команду.";
                        return;
                    }
                    action(fresh);
                };
                menu.Items.Add(item);
            }
            AddObjectCommand("Новое КП…", obj => NewContractFlow(obj.Id), "FilePlus", !selected.Archived);
            AddObjectCommand("Импорт КП в этот объект…", obj => _ = ImportExcelForObjectAsync(obj.Id), "Upload", !selected.Archived);
            menu.Items.Add(new Separator());
            AddObjectCommand("Переименовать", RenameObject, "Edit", !selected.Archived, "F2");
            AddObjectCommand("Свойства объекта…", obj => EditObjectProperties(obj.Id), "Edit", !selected.Archived);
            AddObjectCommand("Список людей на допуск…", obj =>
                OpenObjectTool(obj, "Допуск", (connection, current) => new PeopleWindow(connection, current.Id)), "Users");
            AddObjectCommand("Приказы об ответственных…", obj =>
                OpenObjectTool(obj, "Приказы", (connection, current) =>
                    new OrdersWindow(new SqlServerPeopleAccess(connection), current.Id)), "File");
            menu.Items.Add(new Separator());
            AddObjectCommand(selected.Archived ? "Вернуть в работу" : "Завершить объект (в архив)…",
                FinishSqlServerObject, "Check");
            if (selected.Archived)
                AddObjectCommand("Изменить год в архиве…", ChangeSqlServerObjectArchiveYear, "History");
            menu.Items.Add(new Separator());
            AddObjectCommand("Удалить объект…", DeleteSqlServerObjectFlow, "Trash", danger: true);
        }
        else if (target is { Tag: DocRow doc })
        {
            menu.Items.Add(new Separator());
            if (doc.Kind == "kp")
            {
                var make = ContextMenuIcons.Create("Сделать договором…", "Check");
                make.Click += (_, _) => EditContractProperties(doc, makeContract: true);
                menu.Items.Add(make);
                var copy = ContextMenuIcons.Create("Создать копию КП (вариант)…", "Duplicate");
                copy.Click += (_, _) => NewContractFlow(doc.ObjectId, doc.Kind, copyFrom: doc.Id);
                menu.Items.Add(copy);
                var rates = ContextMenuIcons.Create("Скопировать расценки…", "Copy");
                rates.Click += (_, _) => TransferRates(doc);
                menu.Items.Add(rates);
                var stages = ContextMenuIcons.Create("Этапы (разбивка)…", "Stage");
                stages.Click += (_, _) => OpenStages(doc);
                menu.Items.Add(stages);
            }
            else if (doc.Kind == "main")
            {
                var dop = ContextMenuIcons.Create("Создать допсоглашение…", "FilePlus");
                dop.Click += (_, _) => NewContractFlow(doc.ObjectId, "dop", baseId: doc.Id);
                menu.Items.Add(dop);
                var move = ContextMenuIcons.Create("Перенести в другой объект…", "FilePlus", SqlWritesEnabled);
                move.Click += (_, _) => MoveCurrentContract(doc);
                menu.Items.Add(move);
            }
            var exportEstimate = ContextMenuIcons.Create("Сохранить смету в Excel…", "Download");
            exportEstimate.Click += (_, _) => ExportEstimate(doc);
            menu.Items.Add(exportEstimate);
            var exportKp = ContextMenuIcons.Create("Экспорт КП / сметы…", "Download");
            void AddKpExport(string label, bool internalPrices = false, bool positional = false,
                bool pdf = false, bool vatLine = false)
            {
                var command = new MenuItem { Header = label };
                command.Click += (_, _) => ExportKpDocument(doc, internalPrices, positional, pdf, vatLine);
                exportKp.Items.Add(command);
            }
            AddKpExport("КП / смета — Excel (как в Python)");
            AddKpExport("КП / смета — PDF", pdf: true);
            AddKpExport("КП — Excel, с выделением НДС", vatLine: true);
            AddKpExport("КП — PDF, с выделением НДС", pdf: true, vatLine: true);
            exportKp.Items.Add(new Separator());
            AddKpExport("Внутренние расценки — Excel", internalPrices: true);
            AddKpExport("Внутренние расценки — PDF", internalPrices: true, pdf: true);
            exportKp.Items.Add(new Separator());
            AddKpExport("Позиционная смета — Excel", positional: true);
            AddKpExport("Позиционная смета — PDF", positional: true, pdf: true);
            menu.Items.Add(exportKp);
            var compare = ContextMenuIcons.Create("Сравнить с другим документом…", "List");
            compare.Click += (_, _) => OpenCompare(doc);
            menu.Items.Add(compare);
            var procurement = ContextMenuIcons.Create("Закупки (контроль оборудования)…", "List");
            procurement.Click += (_, _) => OpenProcurement(doc);
            menu.Items.Add(procurement);
            var shipping = ContextMenuIcons.Create("Выдача на объект…", "Upload");
            shipping.Click += (_, _) => OpenShipments(doc);
            menu.Items.Add(shipping);
            var acts = ContextMenuIcons.Create("КС-2 (акты)…", "File");
            acts.Click += (_, _) => OpenActs(doc);
            menu.Items.Add(acts);
            AddDocumentFileMenu(menu, doc);
            var link = ContextMenuIcons.Create("Создать ссылку .gmcs…", "FilePlus");
            link.Click += (_, _) => CreateGmcsLink(doc);
            menu.Items.Add(link);
            var documentHistory = ContextMenuIcons.Create("История изменений…", "History");
            documentHistory.Click += (_, _) => OpenHistory(doc.Id);
            menu.Items.Add(documentHistory);
            // В Python «Свойства» лежат в меню редактора; пока редактора нет — здесь.
            var props = ContextMenuIcons.Create("Свойства документа…", "Edit");
            props.Click += (_, _) => EditContractProperties(doc);
            menu.Items.Add(props);
            menu.Items.Add(new Separator());
            var delete = ContextMenuIcons.Create(
                doc.Kind switch { "main" => "Удалить договор…", "dop" => "Удалить допсоглашение…", _ => "Удалить КП…" },
                "Trash", danger: true);
            delete.Click += (_, _) => DeleteSqlServerContractFlow(doc);
            menu.Items.Add(delete);
        }

        menu.PlacementTarget = NavigationTree;
        menu.Placement = e.CursorLeft < 0 && e.CursorTop < 0
            ? System.Windows.Controls.Primitives.PlacementMode.Bottom
            : System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        e.Handled = true;
        menu.IsOpen = true;
    }

    private TreeViewItem? NavigationNodeUnderMouse()
    {
        var point = Mouse.GetPosition(NavigationTree);
        for (DependencyObject? node = NavigationTree.InputHitTest(point) as DependencyObject;
             node is not null && node != NavigationTree;)
        {
            if (node is TreeViewItem item) return item;
            node = node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>
    /// Окно «Корзина» (main_window.show_trash): вернуть объект или документ;
    /// после возврата дерево перечитывается и восстановленное открывается.
    /// </summary>
    private void OpenTrash()
    {
        if (_serverConn is null || _sqlConnectionString is null) return;
        try
        {
            var dialog = new TrashWindow(_sqlConnectionString) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Restored is not { } restored) return;
            if (restored.Kind == "object")
            {
                ReloadAndSelect(checked((int)restored.Id));
            }
            else
            {
                var objectId = SqlServerWorkspace.ReadContract(_serverConn, restored.Id).ObjectId;
                ReloadAndSelect(checked((int)objectId));
                OpenEstimate(checked((int)restored.Id));
            }
            StatusText.Text = "Возвращено из корзины";
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Корзина", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Окно «История изменений» (main_window.py → history_dialog.show_history).
    /// contractId — документ и его объект; null — последние 3000 записей всей базы.
    /// </summary>
    private void OpenHistory(long? contractId)
    {
        if (_sqlConnectionString is null) return;
        try
        {
            new HistoryWindow(_sqlConnectionString, contractId) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Журнал изменений",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OpenEstimate(int contractId, int? focusPosition = null)
    {
        if (_startupClosed || _serverConn is null || _sqlConnectionString is null) return;
        if (_openDocumentId == contractId)
        {
            if (focusPosition is int position && PageHost.Content is EstimateView current)
            {
                if (current.IsEnabled) current.FocusPositionFromSearch(position);
                else _pendingEstimateFocus = position;
            }
            return;
        }
        var doc = _docs.FirstOrDefault(d => d.Id == contractId);
        if (doc is null) return;
        _pendingEstimateFocus = focusPosition;
        long request = -1;
        EstimateView? cached = null;
        System.Threading.CancellationTokenSource? validation = null;
        try
        {
            if (_estimateCache.TryGetValue(contractId, out cached))
            {
                // Show cached rows immediately, but block ALL commands, including
                // exports and nested workspace editors, until revision validation.
                cached.IsEnabled = false;
                ShowPage(cached);
                request = _estimateOpenGeneration;
                validation = new System.Threading.CancellationTokenSource();
                _estimateCacheValidation = validation;
                var token = validation.Token;
                _selectedObject = null;
                _openDocumentId = contractId;
                SynchronizeNavigation(doc);
                SetEstimateStatus($"{doc.Title} · проверка актуальности данных…", false);
                StartupTrace.Mark("estimate_cache_validation_begin");

                // Never discard an editor which still owns local writes/readback.
                // A failed reconciliation can be retried without replaying writes.
                if (cached.HasUnsettledCacheWrites && !cached.CacheReadOrWriteInProgress)
                    cached.RequestRefresh();
                var waiting = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    while (cached.CacheReadOrWriteInProgress || cached.HasUnsettledCacheWrites)
                    {
                        if (!IsCurrentEstimateOpen(request, contractId, cached)) return;
                        if (waiting.Elapsed > TimeSpan.FromSeconds(15))
                            throw new InvalidOperationException("Сохранение или сверка предыдущих правок ещё не завершены.");
                        await Task.Delay(50, token);
                    }
                    if (!IsCurrentEstimateOpen(request, contractId, cached)) return;
                    var revision = await Task.Run(async () =>
                    {
                        token.ThrowIfCancellationRequested();
                        using var conn = await SqlServerDatabase.OpenAsync(_sqlConnectionString!, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        return await SqlServerDatabase.RevisionAsync(conn, token).ConfigureAwait(false);
                    }, token);
                    if (!IsCurrentEstimateOpen(request, contractId, cached)) return;
                    // A scheduled readback may have started during revision I/O.
                    if (cached.CacheReadOrWriteInProgress || cached.HasUnsettledCacheWrites)
                        continue;
                    StartupTrace.Mark("estimate_cache_validation_end");
                    if (cached.CanReuseCacheRevision(revision))
                    {
                        _estimateCacheOrder.Remove(contractId);
                        _estimateCacheOrder.AddFirst(contractId);
                        cached.IsEnabled = true;
                        CompleteEstimateOpen(doc, cached);
                        return;
                    }
                    break;
                }
                // The cached snapshot is obsolete. Keep existing revision/undo
                // guards: only a new authoritative load may replace its rows.
                _estimateCache.Remove(contractId);
                _estimateCacheOrder.Remove(contractId);
            }

            var page = new EstimateView(_serverConn, doc, _sqlConnectionString!);
            CacheEstimate(contractId, page);
            ShowPage(page);
            _selectedObject = null;
            _openDocumentId = contractId;
            SynchronizeNavigation(doc);
            CompleteEstimateOpen(doc, page, loading: true);
        }
        catch (OperationCanceledException) when (validation?.IsCancellationRequested == true)
        {
            // Superseded by navigation or window close; no error on the new page.
        }
        catch (Exception ex)
        {
            if (_startupClosed || (request >= 0 &&
                (request != _estimateOpenGeneration || _openDocumentId != contractId))) return;
            LogError(ex);
            if (cached is not null && request >= 0)
            {
                // Fail closed: stale rows cannot be edited or exported.
                // Leave the old cache/queue intact and offer explicit retry.
                var retryFocus = _pendingEstimateFocus;
                _openDocumentId = null;
                var panel = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(24),
                };
                panel.Children.Add(new TextBlock
                {
                    Text = "Не удалось проверить актуальность сметы.\\n" + ex.Message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 520,
                    TextAlignment = TextAlignment.Center,
                });
                var retry = new Button
                {
                    Content = "Повторить загрузку",
                    Style = (Style)FindResource("PrimaryButton"),
                    Margin = new Thickness(0, 12, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                retry.Click += (_, _) => OpenEstimate(contractId, retryFocus);
                panel.Children.Add(retry);
                ShowPage(panel);
                SetEstimateStatus("Смета не проверена · редактирование недоступно", true);
                StartupTrace.Mark("estimate_cache_validation_failed");
                return;
            }
            MessageBox.Show(this, ex.Message, "Не удалось открыть смету",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            // The owning request disposes after SQL completes; never clear a newer CTS.
            if (ReferenceEquals(_estimateCacheValidation, validation))
                _estimateCacheValidation = null;
            validation?.Dispose();
        }
    }

    private void CancelEstimateCacheValidation()
    {
        var validation = _estimateCacheValidation;
        _estimateCacheValidation = null;
        validation?.Cancel();
    }

    private bool IsCurrentEstimateOpen(long request, int contractId, EstimateView cached) =>
        !_startupClosed && request == _estimateOpenGeneration &&
        _openDocumentId == contractId && ReferenceEquals(PageHost.Content, cached);

    // Both navigation and the editor use the same foreground, so a previous
    // error does not colour an unrelated document's normal status red.
    internal void SetEstimateStatus(string message, bool error)
    {
        StatusText.Text = message;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(error
            ? System.Windows.Media.Color.FromRgb(0xC5, 0x30, 0x30)
            : System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x8B));
    }

    private void CompleteEstimateOpen(DocRow doc, EstimateView page, bool loading = false)
    {
        SetEstimateStatus(loading ? $"{doc.Title} · загрузка сметы…" : doc.Title, false);
        if (_pendingEstimateFocus is int position)
        {
            _pendingEstimateFocus = null;
            page.FocusPositionFromSearch(position);
        }
    }

    /// <summary>Открытие документа по ссылке .gmcs (links.read_link / find_contract).</summary>
    private void OpenGmcsLink(string path)
    {
        try
        {
            using var jd = JsonDocument.Parse(File.ReadAllText(path));
            var root = jd.RootElement;
            if (root.TryGetProperty("type", out var t) && t.GetString() != "gmc-smeta-link") return;
            var contractId = root.TryGetProperty("contract_id", out var cid) && cid.ValueKind == JsonValueKind.Number ? cid.GetInt32() : 0;
            var objectId = root.TryGetProperty("object_id", out var oid) && oid.ValueKind == JsonValueKind.Number ? oid.GetInt32() : 0;
            var objectName = root.TryGetProperty("object", out var on) ? on.GetString() ?? "" : "";
            var title = root.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
            var kind = root.TryGetProperty("kind", out var kk) ? kk.GetString() ?? "" : "";
            var number = root.TryGetProperty("number", out var nn) ? nn.GetString() ?? "" : "";
            var date = root.TryGetProperty("date", out var dd) ? dd.GetString() ?? "" : "";

            int? targetId = null;
            if (contractId > 0 && _docs.Any(d => d.Id == contractId))
            {
                var d0 = _docs.First(d => d.Id == contractId);
                if (objectId == 0 || d0.ObjectId == objectId ||
                    _objects.FirstOrDefault(o => o.Id == d0.ObjectId)?.Name == objectName)
                    targetId = contractId;
            }
            if (targetId is null)
            {
                var obj = _objects.FirstOrDefault(o => o.Name == objectName);
                if (obj is not null)
                {
                    foreach (var x in _docs.Where(d => d.ObjectId == obj.Id))
                    {
                        if (!string.IsNullOrEmpty(title) && x.Title == title) { targetId = x.Id; break; }
                    }
                    if (targetId is null)
                    {
                        foreach (var x in _docs.Where(d => d.ObjectId == obj.Id))
                        {
                            if (x.Kind == kind && x.Number == number && x.Date == date) { targetId = x.Id; break; }
                        }
                    }
                }
            }
            if (targetId is int id && id > 0) OpenEstimate(id);
            else MessageBox.Show(this, "Документ из ссылки не найден в текущей базе.", "Ссылка .gmcs", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ссылка .gmcs", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal void CreateGmcsLink(DocRow doc)
    {
        try
        {
            var dlg = new SaveFileDialog
            {
                Title = "Создать ссылку .gmcs",
                FileName = doc.Title + ".gmcs",
                Filter = "Ссылка GMC-Smeta (*.gmcs)|*.gmcs",
            };
            if (dlg.ShowDialog(this) != true) return;
            var path = dlg.FileName;
            if (!path.EndsWith(".gmcs", StringComparison.OrdinalIgnoreCase)) path += ".gmcs";
            var obj = _objects.FirstOrDefault(o => o.Id == doc.ObjectId);
            var data = new
            {
                type = "gmc-smeta-link",
                version = 1,
                // Links open against the active connection; never embed SQL credentials.
                db = "",
                contract_id = doc.Id,
                object_id = doc.ObjectId,
                @object = obj?.Name ?? "",
                title = doc.Title,
                kind = doc.Kind,
                number = doc.Number,
                date = doc.Date,
                saved_by = Environment.UserName,
                saved_at = DateTime.Now.ToString("dd.MM.yyyy HH:mm"),
                hint = "Двойной клик открывает этот документ в GMC-Smeta.",
            };
            File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }), System.Text.Encoding.UTF8);
            MessageBox.Show(this, "Ссылка создана:\n" + path, "Ссылка .gmcs", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ссылка .gmcs", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CacheEstimate(int contractId, EstimateView view)
    {
        _estimateCache[contractId] = view;
        _estimateCacheOrder.Remove(contractId);
        _estimateCacheOrder.AddFirst(contractId);
        TrimEstimateCache(protectedId: contractId);
    }

    private void ScheduleEstimateCacheTrim()
    {
        if (_startupClosed || _estimateCacheTrimTimer is null ||
            _estimateCache.Count <= EstimateCacheCapacity) return;
        _estimateCacheTrimAttempts = 15;
        _estimateCacheTrimTimer.Stop();
        _estimateCacheTrimTimer.Start();
    }

    private void TrimEstimateCache(int? protectedId = null)
    {
        while (_estimateCacheOrder.Count > EstimateCacheCapacity)
        {
            // Never remove the displayed view or a live write queue, even
            // temporarily. HasUnsettledCacheWrites includes pending readback.
            var candidate = _estimateCacheOrder.Last;
            while (candidate is not null)
            {
                var view = _estimateCache[candidate.Value];
                if (candidate.Value != protectedId &&
                    !ReferenceEquals(PageHost.Content, view) &&
                    !view.HasUnsettledCacheWrites && view.CanReleaseCachedView)
                    break;
                candidate = candidate.Previous;
            }
            if (candidate is null) break;
            _estimateCache.Remove(candidate.Value);
            _estimateCacheOrder.Remove(candidate);
            StartupTrace.Mark("estimate_cache_evicted");
        }
    }

    private void SynchronizeNavigation(DocRow doc, bool rebuildTree = false)
    {
        _syncingNavigation = true;
        try
        {
            var obj = _objects.First(o => o.Id == doc.ObjectId);
            var mode = obj.Archived ? NavigationMode.Archive : doc.Kind == "kp" ? NavigationMode.Kp : NavigationMode.Contracts;
            if (mode != CurrentMode)
            {
                _ready = false;
                (mode == NavigationMode.Archive ? ArchiveMode : mode == NavigationMode.Kp ? KpMode : ContractsMode).IsChecked = true;
                _ready = true;
                RememberNavigationMode();
                rebuildTree = true;
            }
            if (rebuildTree) RefreshNavigation();
            // A direct open, .gmcs link or global search may target a document
            // in a branch that has never been expanded in this session.
            if (_objectNodes.TryGetValue(doc.ObjectId, out var objectNode))
                EnsureObjectDocuments(objectNode);
            if (_documentNodes.TryGetValue(doc.Id, out var node))
            {
                for (var parent = ItemsControl.ItemsControlFromItemContainer(node) as TreeViewItem;
                     parent is not null; parent = ItemsControl.ItemsControlFromItemContainer(parent) as TreeViewItem)
                    parent.IsExpanded = true;
                node.IsSelected = true;
                node.BringIntoView();
            }
        }
        finally { _ready = true; _syncingNavigation = false; }
    }

    protected override void OnClosed(EventArgs e)
    {
        _startupClosed = true;
        CancelEstimateCacheValidation();
        _navigationSearchTimer?.Stop();
        _estimateCacheTrimTimer?.Stop();
        if (_ready) RememberWindowGeometry();
        _serverConn?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Размер главного окна переживает перезапуск (WPF-специфичное удобство, в Python не было).</summary>
    private void RestoreWindowGeometry()
    {
        try
        {
            var settings = _navigationSettings.Load().Settings;
            // Если монитор сменился/стал меньше, окно не должно вылезти за рабочую область.
            var width = Math.Min(settings.WindowWidth, SystemParameters.WorkArea.Width);
            var height = Math.Min(settings.WindowHeight, SystemParameters.WorkArea.Height);
            if (width >= MinWidth && height >= MinHeight)
            {
                Width = width;
                Height = height;
            }
            if (settings.WindowMaximized) WindowState = WindowState.Maximized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LogError(ex); }
    }

    private void RememberWindowGeometry()
    {
        try
        {
            // В развёрнутом виде ActualWidth — весь экран, а нужно «нормальное» окно до разворота.
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth, ActualHeight)
                : RestoreBounds;
            if (bounds.Width < MinWidth || bounds.Height < MinHeight) return;
            var current = _navigationSettings.Load().Settings;
            _navigationSettings.Save(current with
            {
                WindowWidth = Math.Round(bounds.Width),
                WindowHeight = Math.Round(bounds.Height),
                WindowMaximized = WindowState == WindowState.Maximized,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LogError(ex); }
    }

    // ---------------------------------------------------------------- настройки (main_window.py:1262-1287)

    /// <summary>theme.FONT_SCALES эталона: пункты подменю «Размер шрифта».</summary>
    internal static readonly (string Title, double Scale)[] FontScaleOptions =
    {
        ("Обычный", 1.0), ("Чуть крупнее", 1.1), ("Крупный", 1.2),
    };

    /// <summary>set_font_scale эталона: масштаб всего интерфейса, сохраняется между запусками.</summary>
    internal void ApplyFontScale(double scale)
    {
        if (RootLayout is null) return;
        scale = Math.Clamp(scale, 0.9, 1.3);
        RootLayout.LayoutTransform = Math.Abs(scale - 1.0) < 0.01
            ? null : new System.Windows.Media.ScaleTransform(scale, scale);
    }

    /// <summary>reset_hints эталона: все закрытые подсказки возвращаются на место.</summary>
    private void ShowHintsAgain()
    {
        _uiSettings.EstimateHintHidden = false;
        _uiSettings.MaterialsHintHidden = false;
        _uiSettings.KsHintHidden = false;
        _uiSettings.Save();
        (PageHost.Content as EstimateView)?.ShowHintsAgain();
        StatusText.Text = "Подсказки снова включены";
    }

    /// <summary>about() эталона: название, версия, база, пользователь и путь к настройкам.</summary>
    private void ShowAbout()
    {
        var database = "—";
        try
        {
            if (_sqlConnectionString is not null)
                database = new SqlConnectionStringBuilder(_sqlConnectionString).InitialCatalog;
        }
        catch { /* строка подключения может быть неполной — не повод падать */ }
        MessageBox.Show(this,
            "ГМЦ-Смета 0.66.4 — порт C#/WPF под SQL Server.\n" +
            "КП → КС-2/КС-3 → сводная → контроль оборудования.\n\n" +
            $"База: {database}\n" +
            $"Пользователь: {Environment.UserName}\n" +
            $"Настройки: {EstimateUiSettings.SettingsFilePath}",
            "О программе", MessageBoxButton.OK, MessageBoxImage.Information);
    }


    /// <summary>Технический лог ошибок: %TEMP%\gmcsmeta-next.log (для разбора, пользователю не нужен).</summary>
    private static void LogError(Exception ex)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "gmcsmeta-next.log");
            File.AppendAllText(path, $"{DateTime.Now:dd.MM.yyyy HH:mm:ss}  {ex}\n\n");
        }
        catch
        {
            // лог — не повод падать
        }
    }

}
