using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class MainWindow
{
    private bool _workspaceNavigationAttached;
    private bool _navigationRefreshInProgress;
    private long _navigationDataGeneration;

    internal void InitializeWorkspaceNavigation()
    {
        if (_workspaceNavigationAttached) return;
        _workspaceNavigationAttached = true;

        var top = MainMenuItem;
        if (top is not null)
        {
            MenuItem Item(string title, Action action, string? glyph = null, string gesture = "",
                bool local = false, bool writes = false)
            {
                var item = glyph is null
                    ? new MenuItem { Header = title, InputGestureText = gesture }
                    : ContextMenuIcons.Create(title, glyph, shortcut: gesture);
                item.Click += (_, _) =>
                {
                    if (writes && !SqlWritesEnabled) return;
                    if (local) RunLocalNavigationAction(title, action);
                    else RunNavigationAction(title, action);
                };
                if (writes)
                    top.SubmenuOpened += (_, _) => item.IsEnabled = SqlWritesEnabled;
                return item;
            }

            // Preserve the existing XAML handlers and Pto visibility binding,
            // but use the same top-level order as Python's _build_main_menu.
            top.Items.Clear();
            top.Items.Add(Item("Найти везде…", OpenGlobalSearch, "Search", "Ctrl+P"));
            top.Items.Add(new Separator());
            top.Items.Add(Item("Карточка выбранного договора / группы", () =>
            {
                if (CommandObject() is { } obj) ReloadAndSelect(obj.Id);
                else StatusText.Text = "Сначала выберите карточку";
            }, "Building"));
            top.Items.Add(Item("Себестоимость и маржа (сводка)", ShowHome, "Building"));
            top.Items.Add(Item("Справочник номенклатуры…", () =>
                new CatalogWindow(_sqlConnectionString!) { Owner = this }.ShowDialog(), "Book", writes: true));
            top.Items.Add(Item("Наши организации…", () =>
                new CompanyWindow(_sqlConnectionString!) { Owner = this }.ShowDialog(), "File", writes: true));
            top.Items.Add(Item("Теги заказчиков (правила)…", () =>
                new TagsWindow(_sqlConnectionString!) { Owner = this }.ShowDialog(), "Layers", writes: true));
            top.Items.Add(Item("Люди и автомобили (для допуска)…", OpenPeopleWorkspace, "Users", writes: true));
            top.Items.Add(Item("Журнал изменений…", () => OpenHistory(null), "History"));
            top.Items.Add(Item("Корзина (удалённые карточки и документы)…", OpenTrash, "Trash", writes: true));
            top.Items.Add(new Separator());
            top.Items.Add(Item("Как работать", OpenWorkspaceHelp, "Book", "F1", local: true));
            top.Items.Add(new Separator());
            ContextMenuIcons.Set(PtoMenuItem, "File");
            top.Items.Add(PtoMenuItem);
            // Exact Python order, with the C# metal register retained after warehouse.
            foreach (var tool in new[] { LettersMenuItem, MetalWarehouseMenuItem, MetalRegisterMenuItem })
            {
                ContextMenuIcons.Set(tool, "File");
                top.Items.Add(tool);
            }
            top.SubmenuOpened += (_, _) =>
            {
                foreach (var tool in new[] { PtoMenuItem, LettersMenuItem, MetalWarehouseMenuItem, MetalRegisterMenuItem })
                    tool.IsEnabled = SqlWritesEnabled;
            };
            // SQL Server-specific backup information, not Python's SQLite file actions.
            var settings = ContextMenuIcons.Create("Настройки", "Settings");
            settings.Items.Add(PtoModeToggle);
            var fontMenu = new MenuItem { Header = "Размер шрифта" };
            foreach (var (title, scale) in FontScaleOptions)
            {
                var option = new MenuItem
                {
                    Header = title,
                    IsCheckable = true,
                    IsChecked = Math.Abs(scale - _uiSettings.FontScale) < 0.01,
                    Tag = scale,
                };
                option.Click += FontScaleOption_Click;
                fontMenu.Items.Add(option);
            }
            settings.Items.Add(fontMenu);
            settings.Items.Add(Item("Показать подсказки снова", ShowHintsAgain, local: true));
            settings.Items.Add(new Separator());
            settings.Items.Add(Item("Сведения о подключении SQL Server", ShowConnectionDetails, "Database", local: true));
            settings.Items.Add(Item("Последнее сообщение программы", () =>
                MessageBox.Show(this, StatusText.Text, "Сообщение программы", MessageBoxButton.OK, MessageBoxImage.Information), local: true));
            settings.Items.Add(Item("Обновить", RefreshWorkspaceNavigation, "Refresh", "F5"));
            settings.Items.Add(new Separator());
            settings.Items.Add(Item("Резервные копии SQL Server", ShowServerBackupInfo, "Download", local: true));
            settings.Items.Add(Item("Журнал ошибок (для разработчика)", OpenWorkspaceErrorLog, "File", local: true));
            top.Items.Add(settings);
            top.Items.Add(new Separator());
            top.Items.Add(Item("О программе", ShowAbout, local: true));
        }

        PreviewKeyDown += WorkspaceNavigationKeyDown;
    }

    private void OpenWorkspaceHelp() => new HelpDialog(this).ShowDialog();

    private void ShowConnectionDetails() =>
        MessageBox.Show(this, DatabaseLabel.Text, "Подключение SQL Server",
            MessageBoxButton.OK, MessageBoxImage.Information);

    // SQL Server backups belong to the server administrator, never a local file copy.
    private void ShowServerBackupInfo() =>
        MessageBox.Show(this,
            "Резервные копии SQL Server создаются и хранятся на сервере. " +
            "Уточните расписание и каталог у администратора SQL Server. " +
            "Эта команда не создаёт резервную копию и не изменяет базу.",
            "Резервные копии", MessageBoxButton.OK, MessageBoxImage.Information);

    private void FontScaleOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: double scale, Parent: MenuItem fontMenu }) return;
        foreach (var option in fontMenu.Items.OfType<MenuItem>())
            option.IsChecked = ReferenceEquals(option, sender);
        ApplyFontScale(scale);
        _uiSettings.FontScale = scale;
        _uiSettings.Save();
        StatusText.Text = "Размер шрифта изменён";
    }

    private void WorkspaceNavigationKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || _startupClosed) return;
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            RunNavigationAction("Найти везде", OpenGlobalSearch);
            e.Handled = true;
        }
        else if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            RunLocalNavigationAction("Как работать", OpenWorkspaceHelp);
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            RunNavigationAction("Обновить", RefreshWorkspaceNavigation);
            e.Handled = true;
        }
    }

    private void RunNavigationAction(string title, Action action)
    {
        if (_serverConn is null || _sqlConnectionString is null || _startupClosed) return;
        RunLocalNavigationAction(title, action);
    }

    private void RunLocalNavigationAction(string title, Action action)
    {
        if (_startupClosed) return;
        try { action(); }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenGlobalSearch()
    {
        if (_sqlConnectionString is null) return;
        var dialog = new GlobalSearchWindow(_sqlConnectionString) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedResult is not { } hit) return;
        if (hit.ObjectId is < 1 or > int.MaxValue) return;

        if (hit.Kind == "object")
        {
            ReloadAndSelect((int)hit.ObjectId);
            return;
        }
        ReloadNavigationData();
        if (hit.Kind == "agreement")
        {
            var target = _objects.FirstOrDefault(o => o.Id == hit.ObjectId && !o.Archived);
            if (target is null || hit.AgreementId is not long legalId || legalId <= 0 || !SqlWritesEnabled)
            {
                StatusText.Text = "Договор больше недоступен для открытия реквизитов";
                return;
            }
            OpenAgreementCard(target, legalId);
            return;
        }
        if (hit.ContractId is < 1 or > int.MaxValue ||
            _docs.FirstOrDefault(d => d.Id == hit.ContractId) is not { } doc ||
            doc.ObjectId != hit.ObjectId || !_objects.Any(o => o.Id == doc.ObjectId))
        {
            StatusText.Text = "Документ больше недоступен";
            return;
        }
        ObjectSearch.Clear();
        SynchronizeNavigation(doc, rebuildTree: true);
        OpenEstimate(doc.Id, hit.Kind == "position" && hit.PositionId is > 0 and <= int.MaxValue
            ? (int)hit.PositionId : null);
    }

    // Manual F5 reads on its own guarded SQL connection. Keep the existing
    // synchronous post-write reload path, and never publish an obsolete result.
    private async void RefreshWorkspaceNavigation()
    {
        if (!_ready || _startupClosed || _navigationRefreshInProgress ||
            _serverConn is null || _sqlConnectionString is not { } connection) return;
        if (ObjectRenameBox.Visibility == Visibility.Visible)
        {
            StatusText.Text = "Завершите переименование объекта перед обновлением.";
            return;
        }

        _navigationRefreshInProgress = true;
        var navigationGeneration = _estimateOpenGeneration;
        var dataGeneration = _navigationDataGeneration;
        var selected = CommandObject()?.Id;
        bool IsCurrent() => !_startupClosed && _ready &&
            navigationGeneration == _estimateOpenGeneration &&
            dataGeneration == _navigationDataGeneration &&
            connection == _sqlConnectionString;

        const string loading = "Обновление списка объектов и документов…";
        StatusText.Text = loading;
        try
        {
            var loaded = await Task.Run(() =>
            {
                StartupTrace.Mark("main_navigation_refresh_begin");
                using var read = SqlServerDatabase.Open(connection);
                var result = Workspace.ReadConsistentNavigation(read);
                StartupTrace.Mark("main_navigation_refresh_end");
                return result;
            });

            if (!IsCurrent())
            {
                if (!_startupClosed && StatusText.Text == loading)
                    StatusText.Text = "Обновление списка отменено: нажмите F5 повторно.";
                return;
            }
            if (!IsEnabled || !RootLayout.IsEnabled ||
                System.Windows.Interop.ComponentDispatcher.IsThreadModal ||
                ObjectRenameBox.Visibility == Visibility.Visible)
            {
                StatusText.Text = "Завершите текущую операцию и повторите F5.";
                return;
            }

            // Do not detach a dirty editor if its document disappeared while
            // this independent read was running. Existing save/readback owns it.
            if (_openDocumentId is not null && PageHost.Content is EstimateView editing && !editing.CanTransferDocument &&
                !loaded.Documents.Any(d => d.Id == _openDocumentId &&
                    loaded.Objects.Any(o => o.Id == d.ObjectId)))
            {
                StatusText.Text = "Документ изменён или удалён. Завершите редактирование и повторите F5.";
                return;
            }

            var changed = ApplyNavigationData(loaded.Objects, loaded.Documents);
            if (PageHost.Content is EstimateView estimate &&
                _docs.FirstOrDefault(d => d.Id == _openDocumentId) is { } doc &&
                _objects.Any(o => o.Id == doc.ObjectId))
            {
                if (!estimate.MatchesDocumentIdentity(doc))
                {
                    if (!estimate.CanTransferDocument)
                    {
                        StatusText.Text = "Документ изменился. Дождитесь сверки и повторите F5.";
                        return;
                    }
                    _estimateCache.Remove(doc.Id);
                    _estimateCacheOrder.Remove(doc.Id);
                    _openDocumentId = null;
                    OpenEstimate(doc.Id);
                    return;
                }
                if (changed) SynchronizeNavigation(doc, rebuildTree: true);
                estimate.RequestRefresh();
                StatusText.Text = $"{doc.Title}  ·  список объектов и документов обновлён";
                return;
            }
            if (selected is int id && _objects.FirstOrDefault(x => x.Id == id) is { } obj)
            {
                SelectObjectFromNavigation(obj, preserveSearch: true, rebuildTree: changed);
                return;
            }
            if (changed) RefreshNavigation();
            if (ReferenceEquals(PageHost.Content, _directorView) && _directorView is not null)
                _directorView.RequestRefresh();
            else
                ShowEmptyPage();
            StatusText.Text = "Данные объектов и документов обновлены из SQL Server";
        }
        catch (Exception ex)
        {
            LogError(ex);
            if (IsCurrent())
                StatusText.Text = "Не удалось обновить список: " + ex.Message + " · Повторите F5.";
        }
        finally { _navigationRefreshInProgress = false; }
    }

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
        // Invalidate any manual F5 snapshot before a synchronous post-write refresh.
        ++_navigationDataGeneration;
        var reader = _serverConn
            ?? throw new InvalidOperationException("Нет открытого подключения к SQL Server.");
        var (objects, documents) = Workspace.ReadNavigation(reader);
        ApplyNavigationData(objects, documents);
    }

    private bool ApplyNavigationData(List<ObjectRow> objects, List<DocRow> documents)
    {
        if (_objects.SequenceEqual(objects) && _docs.SequenceEqual(documents)) return false;
        _objects.Clear(); _objects.AddRange(objects);
        _docs.Clear(); _docs.AddRange(documents);
        return true;
    }

    private void SelectObjectFromNavigation(ObjectRow obj, bool preserveSearch = false, bool rebuildTree = true)
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
        if (!preserveSearch) ObjectSearch.Clear();
        _ready = true;
        RememberNavigationMode();
        rebuildTree |= _shownMode != mode;
        if (rebuildTree) RefreshNavigation();
        if (_objectNodes.TryGetValue(obj.Id, out var node) && (rebuildTree || !node.IsSelected))
        {
            if (ItemsControl.ItemsControlFromItemContainer(node) is TreeViewItem year) year.IsExpanded = true;
            _syncingNavigation = true;
            try { node.IsSelected = true; }
            finally { _syncingNavigation = false; }
            if (node.Visibility == Visibility.Visible) node.BringIntoView();
        }
        ShowObject(obj);
    }

    private void OpenWorkspaceErrorLog()
    {
        var path = Path.Combine(Path.GetTempPath(), "gmcsmeta-next.log");
        if (!File.Exists(path))
        {
            StatusText.Text = "Журнал ошибок пуст";
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path, UseShellExecute = true
        });
    }
}
