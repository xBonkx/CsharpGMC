using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.ComponentModel;
using GmcSmeta.Core.Domain;
using Microsoft.Win32;

namespace GmcSmeta.App;

public partial class LettersWindow : Window
{
    private readonly string _connectionString;
    private readonly ObjectRow? _objectContext;
    private readonly DocRow? _contractContext;
    private readonly IReadOnlySet<long>? _objectContractIds;
    private LettersArchive? _archive;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _closed, _busy, _reading, _refreshRequested, _ready;
    private long _generation;
    private CancellationTokenSource? _listReadCancellation;
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GMC-Smeta-Next", "letters.settings.json");

    private static string? SavedRoot()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("GMC_SMETA_LETTERS_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;
        try
        {
            if (File.Exists(SettingsFile))
                return JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement
                    .GetProperty("root").GetString();
        }
        catch (Exception) { /* Corrupt user preference must not affect SQL or archive files. */ }
        return null;
    }

    private static void PersistRoot(string root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new { root }));
    }

    public LettersWindow(string connectionString, ObjectRow? objectContext = null,
        IReadOnlySet<long>? objectContractIds = null, DocRow? contractContext = null)
    {
        InitializeComponent();
        _connectionString = connectionString;
        _objectContext = objectContext;
        _objectContractIds = objectContractIds;
        if (contractContext is not null && (objectContext is null || contractContext.ObjectId != objectContext.Id))
            throw new ArgumentException("Договор не принадлежит выбранному объекту.");
        _contractContext = contractContext;
        if (objectContext is not null)
            Title = "Письма · " + objectContext.Name + (contractContext is null ? "" : " — " + contractContext.Title);
        AttachExistingButton.Visibility = objectContext is null ? Visibility.Collapsed : Visibility.Visible;
        StatusFilter.SelectedIndex = 0;
        SetControls();
        ArchivePath.Text = "Подключение к архиву писем…";
        _searchTimer.Tick += SearchTimer_Tick;
        Loaded += InitializeAsync;
    }

    private async void InitializeAsync(object sender, RoutedEventArgs e)
    {
        Loaded -= InitializeAsync;
        _busy = true;
        SetControls();
        try
        {
            _archive = await Task.Run(() => new LettersArchive(_connectionString, SavedRoot()));
        }
        catch (Exception error)
        {
            ArchivePath.Text = "Архив не открыт: " + error.Message;
        }
        finally
        {
            _busy = false;
            SetControls();
        }
        if (_archive is not null) RequestRefresh(immediate: true);
    }

    private void SetControls()
    {
        ActionPanel.IsEnabled = LetterList.IsEnabled = _ready && !_busy;
        SearchBox.IsEnabled = StatusFilter.IsEnabled = RefreshButton.IsEnabled = !_busy && _archive is not null;
    }

    private void RequestRefresh(bool immediate = false)
    {
        if (_closed || _busy || _archive is null) return;
        ++_generation;
        _listReadCancellation?.Cancel();
        _ready = false;
        SetControls();
        ArchivePath.Text = "Чтение списка писем…";
        _searchTimer.Stop();
        if (immediate)
        {
            _refreshRequested = true;
            _ = RefreshAsync();
        }
        else _searchTimer.Start();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RequestRefresh(immediate: true);

    private void SearchTimer_Tick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        _refreshRequested = true;
        _ = RefreshAsync();
    }

    // At most one list read is in flight. New input invalidates its result
    // immediately; a timer coalesces rapid changes before the next request.
    private async Task RefreshAsync()
    {
        if (_closed || _busy || _reading || _archive is null) return;
        _reading = true;
        try
        {
            while (_refreshRequested && !_closed && !_busy)
            {
                _refreshRequested = false;
                var generation = _generation;
                var archive = _archive;
                var selectedId = Selected()?.Id;
                var query = SearchBox.Text.Trim();
                var status = (StatusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Все";
                using var cancellation = new CancellationTokenSource();
                _listReadCancellation = cancellation;
                var token = cancellation.Token;
                try
                {
                    var letters = await Task.Run(() => archive.Sql.ListAsync(query, status, token, Belongs), token);
                    if (_closed || token.IsCancellationRequested || generation != _generation || !ReferenceEquals(archive, _archive)) continue;
                    LetterList.ItemsSource = letters;
                    LetterList.SelectedItem = letters.FirstOrDefault(x => x.Id == selectedId);
                    ArchivePath.Text = "Отдельный архив файлов: " + archive.Root +
                        " · метаданные и номера писем: SQL Server";
                    _ready = true;
                }
                catch (Exception error)
                {
                    if (_closed || token.IsCancellationRequested || generation != _generation) continue;
                    LetterList.ItemsSource = null;
                    ArchivePath.Text = "Не удалось прочитать письма: " + error.Message;
                    // Search/filter remains available for a fresh read.
                    _ready = false;
                }
                finally
                {
                    _listReadCancellation = null;
                    if (!_closed) SetControls();
                }
            }
        }
        finally { _reading = false; }
    }

    private void Search_Changed(object sender, RoutedEventArgs e)
    {
        if (_archive is not null && StatusFilter is not null && SearchBox is not null)
            RequestRefresh();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // A write/restore must finish and report its outcome before closing.
        if (_busy) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        ++_generation;
        _listReadCancellation?.Cancel();
        _searchTimer.Stop();
        _searchTimer.Tick -= SearchTimer_Tick;
        base.OnClosed(e);
    }

    private bool Belongs(LetterDocument letter) => LetterContext.Belongs(letter,
        _objectContext?.Id, _contractContext?.Id, _objectContractIds);

    private LetterDocument? Selected() => LetterList.SelectedItem as LetterDocument;

    private async Task RunActionAsync(Func<LettersArchive, Task> action)
    {
        if (_closed || _busy || !_ready || _archive is not { } archive) return;
        _busy = true;
        _ready = false;
        SetControls();
        try { await action(archive); }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "Письма", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            RequestRefresh(immediate: true);
        }
    }

    private void Edit(LettersArchive archive, LetterDocument letter) =>
        new LetterEditorWindow(archive, letter, _connectionString) { Owner = this }.ShowDialog();

    private async void New_Click(object sender, RoutedEventArgs e) => await RunActionAsync(async archive =>
    {
        var doc = new LetterDocument();
        if (_objectContext is not null)
        {
            LetterContext.AttachDraft(doc, _objectContext.Id, _objectContext.Name,
                _contractContext?.Id, _contractContext?.Title ?? "");
            doc.Recipient = _objectContext.Customer;
        }
        await Task.Run(() => archive.Sql.Save(doc, validateContext: _objectContext is not null,
            useAgreementRecipient: _contractContext is not null));
        Edit(archive, doc);
    });

    private async void Open_Click(object sender, RoutedEventArgs e) => await RunActionAsync(async archive =>
    {
        if (Selected() is { } selected)
        {
            var current = await Task.Run(() => archive.Sql.Get(selected.Id));
            if (!Belongs(current)) return;
            Edit(archive, current);
        }
    });

    private async void Copy_Click(object sender, RoutedEventArgs e) => await RunActionAsync(async archive =>
    {
        if (Selected() is { } source)
        {
            var current = await Task.Run(() => archive.Sql.Get(source.Id));
            if (!Belongs(current)) return;
            var copy = await Task.Run(() => archive.Sql.Create(current));
            Edit(archive, copy);
        }
    });

    private void List_DoubleClick(object sender, MouseButtonEventArgs e) =>
        Open_Click(sender, e);

    private async void AttachExisting_Click(object sender, RoutedEventArgs e) => await RunActionAsync(async archive =>
    {
        if (_objectContext is not { } obj) return;
        var choices = await Task.Run(() => archive.Sql.List().Where(LetterContext.CanAttach).ToArray());
        if (choices.Length == 0)
        {
            MessageBox.Show(this, "Нет черновиков без привязки и опубликованных выпусков.", "Письма");
            return;
        }
        var picker = new Window { Owner = this, Title = "Привязать письмо", Width = 680, Height = 420,
            MinWidth = 450, MinHeight = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(14) };
        var hint = new TextBlock { Text = "Выберите черновик для привязки к " +
            (_contractContext?.Title ?? obj.Name) + ". Отправленные письма и опубликованные выпуски не изменяются.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        var list = new ListBox { ItemsSource = choices.Select(letter => new
        {
            Letter = letter, Text = $"{letter.Number} · {letter.Subject} · {letter.Date}"
        }).ToArray(), DisplayMemberPath = "Text", SelectedIndex = 0 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0) };
        var attach = new Button { Content = "Привязать", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) };
        attach.Click += (_, _) => { if (list.SelectedIndex >= 0) picker.DialogResult = true; };
        buttons.Children.Add(attach); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(list);
        picker.Content = panel;
        if (picker.ShowDialog() != true || list.SelectedIndex < 0) return;
        var chosen = choices[list.SelectedIndex];
        await Task.Run(() =>
        {
            var current = archive.Sql.Get(chosen.Id);
            if (current.Revision != chosen.Revision || !LetterContext.CanAttach(current))
                throw new InvalidOperationException("Письмо изменилось. Повторите выбор.");
            LetterContext.AttachDraft(current, obj.Id, obj.Name, _contractContext?.Id, _contractContext?.Title ?? "");
            archive.Sql.Save(current, validateContext: true);
        });
    });

    private async void Root_Click(object sender, RoutedEventArgs e) => await RunActionAsync(async archive =>
    {
        var browse = new OpenFolderDialog { Title = "Файловый архив писем" };
        if (browse.ShowDialog(this) != true) return;
        var target = Path.GetFullPath(browse.FolderName).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(target, archive.Root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) &&
            await Task.Run(() => archive.Sql.List().Any(letter => letter.Files.Count > 0 || letter.Releases.Count > 0)))
            throw new InvalidOperationException(
                "В архиве уже есть вложения или выпуски. Переключение папки без их переноса " +
                "сделает файлы недоступными. Выберите сетевой архив до добавления писем " +
                "или выполните согласованную миграцию всех файлов.");
        var replacement = await Task.Run(() => new LettersArchive(_connectionString, target));
        _archive = replacement;
        await Task.Run(() => PersistRoot(replacement.Root));
    });

    private async void Backup_Click(object sender, RoutedEventArgs e) => await RunActionAsync(async archive =>
    {
        var dialog = new SaveFileDialog
        {
            Title = "Резервная копия писем (SQL JSON + оригиналы + выпуски)",
            Filter = "ZIP (*.zip)|*.zip", FileName = "Письма.zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        var path = dialog.FileName;
        await Task.Run(() => archive.Backup(path));
        MessageBox.Show(this, "Резервная копия сохранена.", "Письма");
    });

    private async void Restore_Click(object sender, RoutedEventArgs e) => await RunActionAsync(async archive =>
    {
        var backup = new OpenFileDialog { Title = "ZIP-копия архива писем", Filter = "ZIP (*.zip)|*.zip" };
        if (backup.ShowDialog(this) != true) return;
        var backupPath = backup.FileName;
        await Task.Run(() => LettersArchive.ValidateRestoreZip(backupPath));
        var folder = new OpenFolderDialog { Title = "Новый пустой каталог восстановления" };
        if (folder.ShowDialog(this) != true) return;
        // Never replace data already present in SQL Server or an existing file archive.
        var destination = folder.FolderName;
        var restored = await Task.Run(() => LettersArchive.RestoreBackup(_connectionString, backupPath, destination));
        _archive = restored;
        await Task.Run(() => PersistRoot(restored.Root));
        MessageBox.Show(this, "Архив восстановлен в новом каталоге.", "Письма");
    });
}
