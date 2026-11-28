using System.IO;
using System.Reflection;
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
        StartupTrace.Mark("main_initialize_component_end");
        StartupTrace.Mark("main_shell_setup_begin");
        Closing += (_, e) => { if (_peopleWorkspace is { } view && !view.TryLeave()) e.Cancel = true; };
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
        InitializeEstimateCacheTrim();
        StartupTrace.Mark("main_shell_setup_end");
        StartupTrace.Mark("main_settings_apply_begin");
        ApplyFontScale(_uiSettings.FontScale);
        RestoreWindowGeometry();
        StartupTrace.Mark("main_settings_apply_end");
        StartupTrace.Mark("main_pto_menu_begin");
        InitializePtoMenu();
        StartupTrace.Mark("main_pto_menu_end");
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
                    // Dispatcher idle is a queue/layout milestone, not proof of a physical monitor frame.
                    if (StartupTrace.Enabled)
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
                        {
                            if (_ready && !_startupClosed) StartupTrace.Mark("main_workspace_dispatcher_idle");
                        }));
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
        UpdateNavigationCaptions();
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
        var shortTitle = CurrentMode != NavigationMode.Kp &&
            AgreementPresentation.Resolve(pending.Documents) is not null;
        var children = Navigation.Documents(pending.Documents)
            .Select(doc => DocumentNode(doc, opened, 2, shortTitle)).ToList();
        _pendingNavigationDocuments.Remove(obj.Id);
        node.Expanded -= ObjectNavigation_Expanded;
        node.Items.Clear();
        foreach (var child in children) node.Items.Add(child);
    }

    private TreeViewItem DocumentNode(NavigationDocument entry, HashSet<string>? opened, int depth, bool shortTitle = false)
    {
        var header = new NavigationDocumentHeader(entry.Document, depth > 2, entry.Children.Count > 0)
            { ShortAgreementTitle = shortTitle };
        var node = new TreeViewItem
        {
            Header = header, Tag = entry.Document,
            Padding = NavigationRowPadding(depth),
            HeaderTemplate = (DataTemplate)FindResource("DocumentNavigationTemplate"),
            ToolTip = entry.Document.NavigationTip,
            FontStyle = header.IsItalic ? FontStyles.Italic : FontStyles.Normal
        };
        System.Windows.Automation.AutomationProperties.SetName(node, header.DisplayName);
        node.IsExpanded = entry.Children.Count > 0 || opened?.Contains(NodeKey(node)) == true;
        _documentNodes[entry.Document.Id] = node;
        foreach (var child in entry.Children) node.Items.Add(DocumentNode(child, opened, depth + 1, shortTitle));
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
            var yearVisible = false;
            foreach (TreeViewItem node in year.Items)
            {
                var matched = node.Tag is ObjectRow obj && visible.Contains(obj.Id);
                yearVisible |= matched;
                var visibility = matched ? Visibility.Visible : Visibility.Collapsed;
                if (node.Visibility != visibility) node.Visibility = visibility;
                // Совпадение в названии документа тоже должно быть видно под объектом.
                if (searching && matched)
                {
                    EnsureObjectDocuments(node);
                    if (!node.IsExpanded) node.IsExpanded = true;
                }
            }
            var yearVisibility = yearVisible ? Visibility.Visible : Visibility.Collapsed;
            if (year.Visibility != yearVisibility) year.Visibility = yearVisibility;
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
        ActivateDocumentsWorkspace();
        if (NavigationTree.SelectedItem is TreeViewItem node) node.IsSelected = false;
        _directorView ??= CreateDirectorView();
        ResetPageSelection();
        ShowPage(_directorView);
        StatusText.Text = "Сводка по объектам · SQL Server";
    }

    private void ShowEmptyPage()
    {
        ActivateDocumentsWorkspace();
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
        CancelObjectCardRead();
        _objectCardTransitionPending = false;
        PageHost.Content = page;
        PageHost.IsEnabled = true;
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
            if (_objectCardTransitionPending || !ReferenceEquals(PageHost.Content, ObjectPage) || _selectedObject?.Id != obj.Id ||
                connection != _sqlConnectionString || mode != CurrentMode ||
                fresh is null || fresh.Archived != obj.Archived || fresh.ArchivedOn != obj.ArchivedOn ||
                !Equals(fresh, obj) ||
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
            var passports = ContextMenuIcons.Create("Паспорта вентиляционных систем…", "File", SqlWritesEnabled);
            passports.Click += (_, _) => Run(current => OpenObjectTool(current, "Паспорта вентиляции",
                (conn, target) => new PassportsWindow(conn, target.Id, target.Name, target.Address)));
            menu.Items.Add(passports);
        }
        menu.Items.Add(new Separator());
        var finish = ContextMenuIcons.Create(obj.Archived ? "Вернуть в работу" : "Завершить карточку (в архив)", "Check", SqlWritesEnabled);
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
        var delete = ContextMenuIcons.Create("Удалить карточку и все документы…", "Trash", SqlWritesEnabled, danger: true);
        delete.Click += (_, _) => Run(DeleteSqlServerObjectFlow);
        menu.Items.Add(delete);
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // Confirm the exact object before displaying a mutation dialog; keep this
    // revision until the writer commits, rather than accepting a new revision
    // after a colleague has changed the record while the dialog is open.
    private (string Connection, string Revision, NavigationMode Mode)? CaptureObjectMutation(ObjectRow obj)
    {
        if (!SqlWritesEnabled || _serverConn is null || _sqlConnectionString is not { } connection)
            return null;
        try
        {
            var revision = SqlServerDatabase.Revision(_serverConn);
            var fresh = Workspace.Objects(_serverConn).FirstOrDefault(row => row.Id == obj.Id);
            if (string.IsNullOrWhiteSpace(revision) || !Equals(fresh, obj) ||
                revision != SqlServerDatabase.Revision(_serverConn))
                throw new InvalidOperationException("Объект изменился. Обновите список перед операцией.");
            return (connection, revision, CurrentMode);
        }
        catch (Exception ex)
        {
            LogError(ex);
            StatusText.Text = "Операция отменена: " + ex.Message;
            return null;
        }
    }

    private bool ObjectMutationCurrent(ObjectRow obj,
        (string Connection, string Revision, NavigationMode Mode) target)
    {
        if (SqlWritesEnabled && target.Connection == _sqlConnectionString &&
            target.Mode == CurrentMode && _objects.Any(row => Equals(row, obj))) return true;
        StatusText.Text = "Объект или режим изменились. Повторите операцию.";
        return false;
    }

    /// <summary>Завершить объект (в архив) или вернуть в работу — finish_object эталона.</summary>
    private void FinishSqlServerObject(ObjectRow obj)
    {
        if (!SqlWritesEnabled)
        {
            StatusText.Text = "Изменения разрешены только в GmcSmetaPilot_*.";
            return;
        }
        if (CaptureObjectMutation(obj) is not { } target) return;
        if (!obj.Archived)
        {
            var question = $"Завершить карточку «{obj.Name}» и отправить все её документы в архив?\n\nОн уйдёт в «Архив» (вкладка над списком объектов), " +
                $"в {DateTime.Today.Year} год. Ничего не удаляется, вернуть можно в любой момент.";
            if (MessageBox.Show(this, question, "Завершить карточку", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        if (!ObjectMutationCurrent(obj, target)) return;
        try
        {
            SqlServerWorkspace.SetObjectArchived(
                target.Connection, obj.Id, !obj.Archived, target.Revision);
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
        if (CaptureObjectMutation(obj) is not { } target) return;
        var current = obj.ArchivedOn.Length >= 4 && int.TryParse(obj.ArchivedOn[..4], out var year)
            ? year : DateTime.Today.Year;
        var dialog = new ArchiveYearDialog(obj.Name, current, DateTime.Today.Year) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Year == current ||
            !ObjectMutationCurrent(obj, target)) return;
        try
        {
            SqlServerWorkspace.ChangeArchiveYear(
                target.Connection, obj.Id, dialog.Year, target.Revision);
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
        if (CaptureObjectMutation(obj) is not { } target) return;
        SqlServerWorkspace.ObjectDeleteInfo info;
        var confirmedRevision = target.Revision;
        try
        {
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
        var question = $"Удалить карточку «{info.Name}» и все её документы?\n\n" + (bullets.Length > 0 ? bullets : "Карточка пуста.") +
            "\n\nЕсли работы завершены — лучше «Завершить карточку (в архив)».";
        if (MessageBox.Show(this, question, "Удаление карточки", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (new ConfirmDeleteDialog(info.Name, info.Details, "карточка") { Owner = this }.ShowDialog() != true ||
            !ObjectMutationCurrent(obj, target)) return;
        try
        {
            SqlServerWorkspace.DeleteObject(
                target.Connection, obj.Id, confirmedRevision);
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
        if (doc.IsStage || doc.StageRest)
        {
            StatusText.Text = "Удаление этапов выполняется в режиме распределения этапов.";
            return;
        }
        if (_objects.FirstOrDefault(row => row.Id == doc.ObjectId) is not { } parent ||
            CaptureObjectMutation(parent) is not { } captured || !CanStartDocumentMutation(doc)) return;
        SqlServerWorkspace.ContractDeleteInfo info;
        var confirmedRevision = captured.Revision;
        try
        {
            if (!DocumentMatchesServer(doc, confirmedRevision))
                throw new InvalidOperationException("Документ изменился. Обновите список.");
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
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (info.Details.Count > 0 &&
            new ConfirmDeleteDialog(info.Title, info.Details, "документ") { Owner = this }.ShowDialog() != true) return;
        if (!ObjectMutationCurrent(parent, captured) || !DocumentLocallyCurrent(doc) ||
            !CanStartDocumentMutation(doc)) return;
        try
        {
            SqlServerWorkspace.DeleteContract(
                captured.Connection, doc.Id, confirmedRevision);
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
    private void SaveObjectRename(int objectId, string name, string expectedRevision)
    {
        try
        {
            SaveSqlServerObjectRename(objectId, name, expectedRevision);
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

    private (ObjectRow Object, string Connection, string Revision, NavigationMode Mode)? _pageRenameTarget;

    private void BeginPageRename()
    {
        if (_selectedObject is not { } obj || obj.Archived ||
            CaptureObjectMutation(obj) is not { } target) return;
        _pageRenameTarget = (obj, target.Connection, target.Revision, target.Mode);
        ObjectRenameBox.Text = obj.Name;
        ObjectTitle.Visibility = ObjectRenameButton.Visibility = Visibility.Collapsed;
        ObjectRenameBox.Visibility = Visibility.Visible;
        ObjectRenameBox.Focus();
        ObjectRenameBox.SelectAll();
    }

    private void EndPageRename()
    {
        _pageRenameTarget = null;
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
        if (ObjectRenameBox.Visibility != Visibility.Visible) return;
        var target = _pageRenameTarget;
        var name = ObjectRenameBox.Text.Trim();
        EndPageRename();
        if (target is not { } captured || _selectedObject?.Id != captured.Object.Id ||
            !ObjectMutationCurrent(captured.Object, (captured.Connection, captured.Revision, captured.Mode))) return;
        if (name.Length > 0 && name != captured.Object.Name)
            SaveObjectRename(captured.Object.Id, name, captured.Revision);
    }

    private void DocumentCard_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: int id } button ||
            _docs.FirstOrDefault(doc => doc.Id == id) is not { } target ||
            !CardDocumentCurrent(target)) return;
        var menu = new ContextMenu { PlacementTarget = button,
            Placement = e.CursorLeft < 0 && e.CursorTop < 0
                ? System.Windows.Controls.Primitives.PlacementMode.Bottom
                : System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var mode = CurrentMode;
        AddDocumentFileMenu(menu, target, () => button.IsLoaded && mode == CurrentMode && CardDocumentCurrent(target));
        menu.IsOpen = true;
    }

    private void AddDocumentFileMenu(ContextMenu menu, DocRow doc, Func<bool>? menuTarget = null)
    {
        var connection = _sqlConnectionString;
        bool ValidTarget() => connection is not null && connection == _sqlConnectionString &&
            menuTarget?.Invoke() != false &&
            _docs.Any(current => current.Id == doc.Id && Equals(current, doc));
        if (doc.Kind is "main" or "dop")
        {
            var signed = ContextMenuIcons.Create("Подписанный PDF", "Check");
            signed.Click += async (_, _) =>
            {
                if (ValidTarget()) await AttachmentWindow.OpenSignedAsync(this, connection!, doc.Id, doc.Title, ValidTarget);
            };
            menu.Items.Add(signed);
            var select = ContextMenuIcons.Create("Добавить / выбрать PDF документа…", "File");
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
            var letters = ContextMenuIcons.Create("Письма документа…", "File", SqlWritesEnabled);
            letters.Click += (_, _) => { if (ValidTarget()) OpenContractDocuments(doc, false); };
            menu.Items.Add(letters);
            if (PtoUserSettings.Enabled)
            {
                var pto = ContextMenuIcons.Create("АОСР документа…", "File", SqlWritesEnabled);
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
        RunNavigationAction(pto ? "АОСР документа" : "Письма документа", () =>
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

    private bool CardDocumentCurrent(DocRow doc) =>
        !_objectCardTransitionPending && ReferenceEquals(PageHost.Content, ObjectPage) &&
        _selectedObject?.Id == doc.ObjectId &&
        _objects.Any(row => Equals(row, _selectedObject)) &&
        _docs.Any(current => Equals(current, doc));

    private DocRow? CardFileTarget(object sender) =>
        sender is Button { Tag: int id, IsLoaded: true } ?
            _docs.FirstOrDefault(doc => doc.Id == id && (doc.Kind is "main" or "dop") &&
                CardDocumentCurrent(doc)) : null;

    private async void CardSignedPdf_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (CardFileTarget(sender) is { } doc && _sqlConnectionString is { } connection)
        {
            var mode = CurrentMode;
            await AttachmentWindow.OpenSignedAsync(this, connection, doc.Id, doc.Title, () =>
                connection == _sqlConnectionString && mode == CurrentMode && CardDocumentCurrent(doc));
        }
    }

    private void CardSignedPdfMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || CardFileTarget(sender) is not { } doc ||
            _sqlConnectionString is not { } connection) return;
        var menu = new ContextMenu { PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var mode = CurrentMode;
        // Python object_view.py: signed PDF split-button menu has no icon.
        var select = new MenuItem { Header = "Добавить / выбрать подписанный PDF…" };
        select.Click += (_, _) =>
        {
            if (button.IsLoaded && connection == _sqlConnectionString && mode == CurrentMode && CardDocumentCurrent(doc))
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
        var cardWidth = ObjectCardLayout.Measure(ObjectCardsScroll.ViewportWidth, ObjectCardsScroll.ActualWidth).CardWidth;
        if (CardWidth != cardWidth) CardWidth = cardWidth;
        LayoutObjectTotals();
    }

    private void LayoutObjectTotals()
    {
        if (ObjectTotals.DataContext is not ObjectDocuments documents) return;
        var state = (Wide: PageHost.ActualWidth >= 1100, Progress: documents.ShowProgress);
        if (_objectTileLayout == state) return;
        _objectTileLayout = state;
        var gap = (double)FindResource("LayoutObjectTileGap");
        ObjectTotalsLayout.Apply(ObjectTotals, ObjectTotalCard, ObjectProgressCard,
            ObjectMaterialsCard, ObjectSmrCard, ObjectEarnedCard, state.Wide, state.Progress, gap);
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
        if (CaptureObjectMutation(obj) is not { } captured) return;
        var dialog = new PromptDialog("Переименовать объект", "Название объекта", obj.Name) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Value.Trim().Length > 0 &&
            dialog.Value.Trim() != obj.Name && ObjectMutationCurrent(obj, captured))
            SaveObjectRename(obj.Id, dialog.Value.Trim(), captured.Revision);
    }

    // ---------------------------------------------------------------- контекстное меню дерева

    /// <summary>Меню дерева — как _tree_menu в Python, пока только команды, перенесённые на SQL Server.</summary>
    private void NavigationTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // A readable SQL connection may export/view documents, but mutations
        // and legacy editing windows are still Pilot-only.
        if (_serverConn is null || _sqlConnectionString is null || _startupClosed)
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
        var menuMode = CurrentMode;
        var menuConnection = _sqlConnectionString;
        bool MenuCurrent() => !_startupClosed && _serverConn is not null &&
            CurrentMode == menuMode && menuConnection == _sqlConnectionString;
        bool CanWriteMenu() => MenuCurrent() && SqlWritesEnabled;
        var create = ContextMenuIcons.Create(NavigationModeCaptions.For(CurrentMode).Create + "…", "FolderPlus", SqlWritesEnabled);
        create.Click += (_, _) => { if (CanWriteMenu()) EditObjectProperties(null); };
        menu.Items.Add(create);
        // Global history and trash already live in the main menu (Python).
        // Keep only target-specific commands here.

        if (target is { Tag: ObjectRow selected })
        {
            menu.Items.Add(new Separator());
            // Bind each command to the node under the cursor, never CommandObject()
            // or the previously opened object card.
            void AddObjectCommand(string title, Action<ObjectRow> action, string glyph, bool enabled = true, string shortcut = "", bool danger = false)
            {
                var item = ContextMenuIcons.Create(title, glyph, enabled && SqlWritesEnabled, shortcut, danger);
                item.Click += (_, _) =>
                {
                    if (!CanWriteMenu() || !enabled ||
                        !_objectNodes.TryGetValue(selected.Id, out var currentNode) ||
                        !ReferenceEquals(currentNode, target) ||
                        _objects.FirstOrDefault(obj => obj.Id == selected.Id) is not { } fresh ||
                        !Equals(fresh, selected))
                    {
                        StatusText.Text = "Объект изменился. Обновите список и повторите команду.";
                        return;
                    }
                    action(fresh);
                };
                menu.Items.Add(item);
            }
            AddObjectCommand(CurrentMode == NavigationMode.Kp ? "Новое КП…" : "Добавить документ…", obj => NewContractFlow(obj.Id), "FilePlus", !selected.Archived);
            AddObjectCommand("Импорт КП в эту группу…", obj => _ = ImportExcelForObjectAsync(obj.Id), "Upload", !selected.Archived);
            menu.Items.Add(new Separator());
            AddObjectCommand("Переименовать", RenameObject, "Edit", !selected.Archived, "F2");
            AddObjectCommand("Свойства карточки…", obj => EditObjectProperties(obj.Id), "Edit", !selected.Archived);
            AddObjectCommand("Список людей на допуск…", obj =>
                OpenObjectTool(obj, "Допуск", (connection, current) => new PeopleWindow(connection, current.Id)), "Users");
            AddObjectCommand("Приказы об ответственных…", obj =>
                OpenObjectTool(obj, "Приказы", (connection, current) =>
                    new OrdersWindow(connection, current.Id)), "File");
            menu.Items.Add(new Separator());
            AddObjectCommand(selected.Archived ? "Вернуть в работу" : "Завершить карточку (в архив)…",
                FinishSqlServerObject, "Check");
            if (selected.Archived)
                AddObjectCommand("Изменить год в архиве…", ChangeSqlServerObjectArchiveYear, "History");
            menu.Items.Add(new Separator());
            AddObjectCommand("Удалить карточку и все документы…", DeleteSqlServerObjectFlow, "Trash", danger: true);
        }
        else if (target is { Tag: DocRow doc })
        {
            var parent = _objects.FirstOrDefault(row => row.Id == doc.ObjectId);
            bool ValidDocumentTarget() => MenuCurrent() && parent is not null &&
                _objects.Any(row => Equals(row, parent)) &&
                _documentNodes.TryGetValue(doc.Id, out var currentNode) &&
                ReferenceEquals(currentNode, target) &&
                _docs.Any(current => current.Id == doc.Id && Equals(current, doc));
            void RunDocumentCommand(Action action)
            {
                if (ValidDocumentTarget()) action();
                else StatusText.Text = "Документ изменился. Обновите список и откройте меню снова.";
            }
            void RunDocumentWrite(Action action)
            {
                if (CanWriteMenu()) RunDocumentCommand(action);
            }
            var activeWrite = SqlWritesEnabled && parent is { Archived: false };
            var contractWorkspace = activeWrite && (doc.Kind is "main" or "dop") && !doc.IsStage && !doc.StageRest;
            menu.Items.Add(new Separator());
            if (doc.Kind == "kp")
            {
                var editable = activeWrite && !doc.Locked && !doc.IsStage && !doc.StageRest;
                var make = ContextMenuIcons.Create("Оформить как приложение…", "Check", editable);
                make.Click += (_, _) => RunDocumentWrite(() => EditContractProperties(doc, makeContract: true));
                menu.Items.Add(make);
                var copy = ContextMenuIcons.Create("Создать копию КП (вариант)…", "Duplicate",
                    activeWrite && !doc.StageRest);
                copy.Click += (_, _) => RunDocumentWrite(() => NewContractFlow(doc.ObjectId, doc.Kind, copyFrom: doc.Id));
                menu.Items.Add(copy);
                var rates = ContextMenuIcons.Create("Скопировать расценки…", "Copy", editable);
                rates.Click += (_, _) => RunDocumentWrite(() => TransferRates(doc));
                menu.Items.Add(rates);
                var stages = ContextMenuIcons.Create("Этапы (разбивка)…", "Stage", activeWrite);
                stages.Click += (_, _) => RunDocumentWrite(() => OpenStages(doc));
                menu.Items.Add(stages);
            }
            else if (doc.Kind == "main")
            {
                var dop = ContextMenuIcons.Create("Создать допсоглашение…", "FilePlus", activeWrite && !doc.IsStage);
                dop.Click += (_, _) => RunDocumentWrite(() => NewContractFlow(doc.ObjectId, "dop", baseId: doc.Id));
                menu.Items.Add(dop);
                var move = ContextMenuIcons.Create("Перенести в другой договор / группу…", "FilePlus", activeWrite && !doc.IsStage);
                move.Click += (_, _) => RunDocumentWrite(() => MoveCurrentContract(doc));
                menu.Items.Add(move);
            }
            menu.Items.Add(new Separator());
            var exportKp = ContextMenuIcons.Create("Сохранить в Excel / PDF…", "Download");
            var exportEstimate = new MenuItem { Header = "Сохранить смету в Excel…" };
            exportEstimate.Click += (_, _) => RunDocumentCommand(() => ExportEstimate(doc));
            exportKp.Items.Add(exportEstimate);
            exportKp.Items.Add(new Separator());
            void AddKpExport(string label, bool pdf, bool internalPrices = false,
                bool positional = false, bool vatLine = false)
            {
                var command = new MenuItem { Header = label };
                command.Click += (_, _) => RunDocumentCommand(() => ExportKpDocument(doc, internalPrices, positional, pdf, vatLine));
                exportKp.Items.Add(command);
            }
            AddKpExport("КП / смета — Excel (как в Python)", pdf: false);
            AddKpExport("КП / смета — PDF", pdf: true);
            AddKpExport("КП — Excel, с выделением НДС", pdf: false, vatLine: true);
            AddKpExport("КП — PDF, с выделением НДС", pdf: true, vatLine: true);
            exportKp.Items.Add(new Separator());
            AddKpExport("Внутренние расценки — Excel", pdf: false, internalPrices: true);
            AddKpExport("Внутренние расценки — PDF", pdf: true, internalPrices: true);
            exportKp.Items.Add(new Separator());
            AddKpExport("Позиционная смета — Excel", pdf: false, positional: true);
            AddKpExport("Позиционная смета — PDF", pdf: true, positional: true);
            menu.Items.Add(exportKp);
            var compare = ContextMenuIcons.Create("Сравнить с другим документом…", "List");
            compare.Click += (_, _) => RunDocumentCommand(() => OpenCompare(doc));
            menu.Items.Add(compare);
            var documentHistory = ContextMenuIcons.Create("История изменений…", "History");
            documentHistory.Click += (_, _) => RunDocumentCommand(() => OpenHistory(doc.Id));
            menu.Items.Add(documentHistory);
            menu.Items.Add(new Separator());
            var procurement = ContextMenuIcons.Create("Закупки (контроль оборудования)…", "List", contractWorkspace);
            procurement.Click += (_, _) => RunDocumentWrite(() => OpenProcurement(doc));
            menu.Items.Add(procurement);
            var shipping = ContextMenuIcons.Create("Выдача на объект…", "Upload", contractWorkspace);
            shipping.Click += (_, _) => RunDocumentWrite(() => OpenShipments(doc));
            menu.Items.Add(shipping);
            var acts = ContextMenuIcons.Create("КС-2 (акты)…", "File", contractWorkspace);
            acts.Click += (_, _) => RunDocumentWrite(() => OpenActs(doc));
            menu.Items.Add(acts);
            menu.Items.Add(new Separator());
            AddDocumentFileMenu(menu, doc, ValidDocumentTarget);
            var link = ContextMenuIcons.Create("Создать ссылку .gmcs…", "FilePlus");
            link.Click += (_, _) => RunDocumentCommand(() => CreateGmcsLink(doc));
            menu.Items.Add(link);
            // C# also exposes properties directly from the navigation tree.
            var props = ContextMenuIcons.Create("Свойства документа…", "Edit",
                activeWrite && !doc.Locked && !doc.IsStage && !doc.StageRest);
            props.Click += (_, _) => RunDocumentWrite(() => EditContractProperties(doc));
            menu.Items.Add(props);
            menu.Items.Add(new Separator());
            var delete = ContextMenuIcons.Create(
                doc.Kind switch { "main" => "Удалить приложение…", "dop" => "Удалить допсоглашение…", _ => "Удалить КП…" },
                "Trash", SqlWritesEnabled && !doc.IsStage && !doc.StageRest, danger: true);
            delete.Click += (_, _) => RunDocumentWrite(() => DeleteSqlServerContractFlow(doc));
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
            if (node is Border { Name: "HeaderBorder", TemplatedParent: TreeViewItem item }) return item;
            // A container also owns the expanded children area. Only its header is a row target.
            if (node is TreeViewItem) return null;
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

    private async void OpenEstimate(int contractId, int? focusPosition = null, bool showStages = false)
    {
        if (_startupClosed || _serverConn is null || _sqlConnectionString is null) return;
        ActivateDocumentsWorkspace();
        if (_openDocumentId == contractId)
        {
            if (showStages && PageHost.Content is EstimateView stagePage)
                stagePage.RequestInlineStages();
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
                        if (showStages) cached.RequestInlineStages();
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
            if (showStages) page.RequestInlineStages();
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
                retry.Click += (_, _) => OpenEstimate(contractId, retryFocus, showStages);
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
        _peopleWorkspace?.CloseView();
        CancelEstimateCacheValidation();
        CancelObjectCardRead();
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
            var geometry = ObjectCardLayout.RestoreGeometry(settings.WindowWidth, settings.WindowHeight,
                SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height, MinWidth, MinHeight);
            if (geometry is { } size)
            {
                Width = size.Width;
                Height = size.Height;
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
