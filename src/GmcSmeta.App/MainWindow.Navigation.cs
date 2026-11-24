using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class MainWindow
{
    private bool _workspaceNavigationAttached;

    internal void InitializeWorkspaceNavigation()
    {
        if (_workspaceNavigationAttached) return;
        _workspaceNavigationAttached = true;

        var top = MainMenuItem;
        if (top is not null)
        {
            MenuItem Item(string title, Action action, string? glyph = null, string gesture = "")
            {
                var item = glyph is null
                    ? new MenuItem { Header = title, InputGestureText = gesture }
                    : ContextMenuIcons.Create(title, glyph, shortcut: gesture);
                item.Click += (_, _) => RunNavigationAction(title, action);
                return item;
            }

            // Preserve the existing XAML handlers and Pto visibility binding,
            // but use the same top-level order as Python's _build_main_menu.
            var tools = top.Items.OfType<MenuItem>()
                .Where(item => !ReferenceEquals(item, PtoModeToggle) && !ReferenceEquals(item, PtoMenuItem))
                .ToArray();
            top.Items.Clear();
            top.Items.Add(Item("Найти везде…", OpenGlobalSearch, "Search", "Ctrl+P"));
            top.Items.Add(new Separator());
            top.Items.Add(Item("Карточка выбранного объекта", () =>
            {
                if (CommandObject() is { } obj) ReloadAndSelect(obj.Id);
                else StatusText.Text = "Сначала выберите объект";
            }, "Building"));
            top.Items.Add(Item("Себестоимость и маржа (сводка)", ShowHome, "Building"));
            top.Items.Add(Item("Справочник номенклатуры…", () =>
                new CatalogWindow(_sqlConnectionString!) { Owner = this }.ShowDialog(), "Book"));
            top.Items.Add(Item("Наши организации…", () =>
                new CompanyWindow(_sqlConnectionString!) { Owner = this }.ShowDialog(), "File"));
            top.Items.Add(Item("Теги заказчиков (правила)…", () =>
                new TagsWindow(_sqlConnectionString!) { Owner = this }.ShowDialog(), "Layers"));
            top.Items.Add(Item("Люди и автомобили (для допуска)…", () =>
                new PeopleWindow(_sqlConnectionString!) { Owner = this }.ShowDialog(), "Users"));
            top.Items.Add(Item("Журнал изменений…", () => OpenHistory(null), "History"));
            top.Items.Add(Item("Корзина (удалённые объекты и документы)…", OpenTrash, "Trash"));
            top.Items.Add(new Separator());
            top.Items.Add(Item("Как работать", OpenWorkspaceHelp, "Book", "F1"));
            top.Items.Add(new Separator());
            ContextMenuIcons.Set(PtoMenuItem, "File");
            top.Items.Add(PtoMenuItem);
            foreach (var tool in tools)
            {
                ContextMenuIcons.Set(tool, "File");
                top.Items.Add(tool);
            }
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
            settings.Items.Add(Item("Показать подсказки снова", ShowHintsAgain));
            settings.Items.Add(new Separator());
            settings.Items.Add(Item("Сведения о подключении SQL Server", ShowConnectionDetails, "Database"));
            settings.Items.Add(Item("Обновить", RefreshWorkspaceNavigation, "Refresh", "F5"));
            settings.Items.Add(new Separator());
            settings.Items.Add(Item("Резервные копии SQL Server", ShowServerBackupInfo, "Download"));
            settings.Items.Add(Item("Журнал ошибок (для разработчика)", OpenWorkspaceErrorLog, "File"));
            top.Items.Add(settings);
            top.Items.Add(new Separator());
            top.Items.Add(Item("О программе", ShowAbout));
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
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            RunNavigationAction("Найти везде", OpenGlobalSearch);
            e.Handled = true;
        }
        else if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            RunNavigationAction("Как работать", OpenWorkspaceHelp);
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
        if (_serverConn is null || _sqlConnectionString is null) return;
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
        if (hit.ContractId is < 1 or > int.MaxValue ||
            _docs.FirstOrDefault(d => d.Id == hit.ContractId) is not { } doc ||
            !_objects.Any(o => o.Id == doc.ObjectId))
        {
            StatusText.Text = "Документ больше недоступен";
            return;
        }
        ObjectSearch.Clear();
        SynchronizeNavigation(doc, rebuildTree: true);
        OpenEstimate(doc.Id, hit.Kind == "position" && hit.PositionId is > 0 and <= int.MaxValue
            ? (int)hit.PositionId : null);
    }

    private void RefreshWorkspaceNavigation()
    {
        if (_serverConn is null) return;
        var selected = CommandObject()?.Id;
        ReloadNavigationData();
        if (PageHost.Content is EstimateView estimate &&
            _docs.FirstOrDefault(d => d.Id == _openDocumentId) is { } doc &&
            _objects.Any(o => o.Id == doc.ObjectId))
        {
            SynchronizeNavigation(doc, rebuildTree: true);
            estimate.RequestRefresh();
            StatusText.Text = $"{doc.Title}  ·  список объектов и документов обновлён";
            return;
        }
        if (selected is int id && _objects.FirstOrDefault(x => x.Id == id) is { } obj)
        {
            SelectObjectFromNavigation(obj);
            return;
        }
        RefreshNavigation();
        if (ReferenceEquals(PageHost.Content, _directorView) && _directorView is not null)
            ShowHome();
        else
            ShowEmptyPage();
        StatusText.Text = "Данные объектов и документов обновлены из SQL Server";
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
