using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GmcSmeta.Core.Domain;
using Microsoft.Win32;

namespace GmcSmeta.App;

public partial class LettersWindow : Window
{
    private readonly string _connectionString;
    private readonly ObjectRow? _objectContext;
    private readonly DocRow? _contractContext;
    private readonly IReadOnlySet<long>? _objectContractIds;
    private LettersArchive _archive;
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
        _archive = new LettersArchive(connectionString, SavedRoot());
        StatusFilter.SelectedIndex = 0;
        Refresh();
    }

    private void Refresh()
    {
        ArchivePath.Text = "Отдельный архив файлов: " + _archive.Root +
            " · метаданные и номера писем: SQL Server";
        var status = (StatusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Все";
        var letters = _archive.Sql.List(SearchBox.Text.Trim(), status);
        LetterList.ItemsSource = letters.Where(Belongs).ToList();
    }

    private void Search_Changed(object sender, RoutedEventArgs e)
    {
        if (_archive is not null && StatusFilter is not null && SearchBox is not null)
            Refresh();
    }

    private bool Belongs(LetterDocument letter) => LetterContext.Belongs(letter,
        _objectContext?.Id, _contractContext?.Id, _objectContractIds);

    private LetterDocument? Selected() => LetterList.SelectedItem as LetterDocument;

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "Письма", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Edit(LetterDocument letter)
    {
        new LetterEditorWindow(_archive, letter, _connectionString) { Owner = this }.ShowDialog();
        Refresh();
    }

    private void New_Click(object sender, RoutedEventArgs e) => Try(() =>
    {
        var doc = new LetterDocument();
        if (_objectContext is not null)
        {
            LetterContext.AttachDraft(doc, _objectContext.Id, _objectContext.Name,
                _contractContext?.Id, _contractContext?.Title ?? "");
            doc.Recipient = _objectContext.Customer;
        }
        _archive.Sql.Save(doc, validateContext: _objectContext is not null);
        Edit(doc);
    });

    private void Open_Click(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (Selected() is { } selected)
        {
            var current = _archive.Sql.Get(selected.Id);
            if (!Belongs(current)) { Refresh(); return; }
            Edit(current);
        }
    });

    private void Copy_Click(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (Selected() is { } source)
        {
            var current = _archive.Sql.Get(source.Id);
            if (!Belongs(current)) { Refresh(); return; }
            Edit(_archive.Sql.Create(current));
        }
    });

    private void List_DoubleClick(object sender, MouseButtonEventArgs e) =>
        Open_Click(sender, e);

    private void AttachExisting_Click(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (_objectContext is not { } obj) return;
        var choices = _archive.Sql.List().Where(LetterContext.CanAttach).ToArray();
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
        var current = _archive.Sql.Get(chosen.Id);
        if (current.Revision != chosen.Revision || !LetterContext.CanAttach(current))
            throw new InvalidOperationException("Письмо изменилось. Повторите выбор.");
        LetterContext.AttachDraft(current, obj.Id, obj.Name, _contractContext?.Id, _contractContext?.Title ?? "");
        _archive.Sql.Save(current, validateContext: true);
        Refresh();
    });

    private void Root_Click(object sender, RoutedEventArgs e) => Try(() =>
    {
        var browse = new OpenFolderDialog { Title = "Файловый архив писем" };
        if (browse.ShowDialog(this) != true) return;
        var target = Path.GetFullPath(browse.FolderName).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(target, _archive.Root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) &&
            _archive.Sql.List().Any(letter => letter.Files.Count > 0 || letter.Releases.Count > 0))
            throw new InvalidOperationException(
                "В архиве уже есть вложения или выпуски. Переключение папки без их переноса " +
                "сделает файлы недоступными. Выберите сетевой архив до добавления писем " +
                "или выполните согласованную миграцию всех файлов.");
        var replacement = new LettersArchive(_connectionString, target);
        _archive = replacement;
        PersistRoot(replacement.Root);
        Refresh();
    });

    private void Backup_Click(object sender, RoutedEventArgs e) => Try(() =>
    {
        var dialog = new SaveFileDialog
        {
            Title = "Резервная копия писем (SQL JSON + оригиналы + выпуски)",
            Filter = "ZIP (*.zip)|*.zip", FileName = "Письма.zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        _archive.Backup(dialog.FileName);
        MessageBox.Show(this, "Резервная копия сохранена.", "Письма");
    });

    private void Restore_Click(object sender, RoutedEventArgs e) => Try(() =>
    {
        var backup = new OpenFileDialog { Title = "ZIP-копия архива писем", Filter = "ZIP (*.zip)|*.zip" };
        if (backup.ShowDialog(this) != true) return;
        LettersArchive.ValidateRestoreZip(backup.FileName);
        var folder = new OpenFolderDialog { Title = "Новый пустой каталог восстановления" };
        if (folder.ShowDialog(this) != true) return;
        // Never replace data already present in SQL Server or an existing file archive.
        var restored = LettersArchive.RestoreBackup(_connectionString, backup.FileName, folder.FolderName);
        _archive = restored;
        PersistRoot(restored.Root);
        Refresh();
        MessageBox.Show(this, "Архив восстановлен в новом каталоге.", "Письма");
    });
}
