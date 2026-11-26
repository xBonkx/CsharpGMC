using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;
using PdfSharp.Pdf.IO;
using Microsoft.Win32;

namespace GmcSmeta.App;

/// <summary>Строка вложения договора (таблица attachments).</summary>
public sealed class AttachmentRow
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public string AddedBy { get; init; } = "";
    public DateTime AddedAt { get; init; }
    public string Note { get; init; } = "";
    public string Role { get; init; } = "";
    public bool IsPrimary { get; init; }
    public string SignedText => Role != "signed_contract" ? "" : IsPrimary ? "Основной PDF" : "Подписанный PDF";
    public string SizeText => Size < 1024 ? $"{Size} Б" :
        Size < 1024 * 1024 ? $"{Size / 1024.0:0.0} КБ" : $"{Size / (1024.0 * 1024.0):0.00} МБ";
    public string AddedAtText => AddedAt == DateTime.MinValue ? "" :
        AddedAt.ToString("dd.MM.yyyy HH:mm");
}

/// <summary>
/// Вложения договора (attach_dialog.py): список, добавление файлов, открытие, удаление.
/// Новые файлы сохраняются в отдельной общей папке Pilot; старые пути не мигрируются автоматически.
/// </summary>
public sealed class AttachmentWindow : Window
{
    private readonly string _connectionString;
    private readonly long _contractId;
    private readonly bool _signedOnly;
    private bool _loading;
    private bool _closed;
    private bool _allowCloseDuringRead;
    private CancellationTokenSource? _readCancellation;
    private CancellationTokenSource? _openCancellation;
    // Accessed only from the owning WPF dispatcher; one direct-open request per window.
    private static readonly HashSet<Window> DirectOpenOwners = new();
    private string _revision = "";
    private string _objectFolder = "";
    private readonly List<(Button Button, Func<bool> Available)> _commands = new();
    private bool CanWrite => !_loading && !string.IsNullOrWhiteSpace(_revision) &&
        SqlSchemaCompatibility.IsWritablePilot(new SqlConnectionStringBuilder(_connectionString).InitialCatalog,
            new SqlConnectionStringBuilder(_connectionString).DataSource);
    private IReadOnlyList<AttachmentRow> _rows = Array.Empty<AttachmentRow>();
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
        CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Extended,
        SelectionUnit = DataGridSelectionUnit.FullRow, MinRowHeight = 27, AlternationCount = 0,
        HeadersVisibility = DataGridHeadersVisibility.Column,
    };

    public AttachmentWindow(string connectionString, long contractId, string contractTitle, bool signedOnly = false)
    {
        _connectionString = connectionString;
        _contractId = contractId;
        _signedOnly = signedOnly;
        Title = signedOnly ? $"Подписанные PDF: {contractTitle}" : $"Файлы документа: {contractTitle}"; Width = 980; Height = 560;
        MinWidth = 560; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;

        WorkspaceWindowUi.ApplyChrome(this);
        WorkspaceWindowUi.StyleTable(_grid);
        _grid.Columns.Add(new DataGridTextColumn { Header = "Файл", Binding = new System.Windows.Data.Binding(nameof(AttachmentRow.Name)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Размер", Binding = new System.Windows.Data.Binding(nameof(AttachmentRow.SizeText)), Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Кто добавил", Binding = new System.Windows.Data.Binding(nameof(AttachmentRow.AddedBy)), Width = 120 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Когда", Binding = new System.Windows.Data.Binding(nameof(AttachmentRow.AddedAtText)), Width = 130 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Отметка", Binding = new System.Windows.Data.Binding(nameof(AttachmentRow.SignedText)), Width = 145 });
        foreach (var column in _grid.Columns.OfType<DataGridTextColumn>())
            WorkspaceWindowUi.StyleColumn(column, Equals(column.Header, "Размер") ? TextAlignment.Right : TextAlignment.Left);
        TableColumnMenu.SetKey(_grid, "attachments");
        _grid.ItemsSource = _rows;
        _grid.ContextMenu = new ContextMenu();
        _grid.ContextMenuOpening += AttachmentMenu_Opening;
        _grid.MouseDoubleClick += async (_, _) => await OpenSelectedAsync();

        var root = new DockPanel { Margin = new Thickness(22,18,22,18) };
        Content = root;
        var heading = WorkspaceWindowUi.Heading(Title);
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var hint = new TextBlock { Text = "Новые файлы — в общей UNC-папке объекта (отдельный каталог тестовой базы). Отметка PDF не проверяет электронную подпись. Старые относительные пути требуют переноса. Удаление из списка не удаляет файл.", Foreground = System.Windows.Media.Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);

        var footer = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var add = new Button { Content = "Добавить файл…", Width = 110, Height = 30, Margin = new Thickness(0, 0, 6, 0) };
        add.Click += (_, _) => AddFiles();
        var open = new Button { Content = "Открыть", Width = 100, Height = 30, Margin = new Thickness(0, 0, 6, 0) };
        open.Click += async (_, _) => await OpenSelectedAsync();
        var del = new Button { Content = "Убрать из списка", Width = 100, Height = 30, Margin = new Thickness(0, 0, 6, 0) };
        del.Click += (_, _) => DeleteSelected();
        var close = new Button { Content = "Закрыть", Width = 100, Height = 30, IsCancel = true };
        close.Click += (_, _) => DialogResult = true;
        footer.Children.Add(add);
        footer.Children.Add(open);
        if (_signedOnly)
        {
            var existing = new Button { Content = "Из прикреплённых…", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0) };
            existing.Click += async (_, _) =>
            {
                if (_loading) return;
                new AttachmentWindow(_connectionString, _contractId, Title) { Owner = this }.ShowDialog();
                await ReloadAsync(allowClose: true);
            };
            footer.Children.Add(existing);
        }
        else footer.Children.Add(del);
        footer.Children.Add(close);
        void StyleButton(Button button, string icon, bool primary = false)
        {
            button.Style = (Style)FindResource(primary ? "PythonToolbarPrimary" : "PythonToolbarButton");
            button.ContentTemplate = (DataTemplate)FindResource("IconLabel" + icon);
            button.Width = double.NaN;
            button.Height = double.NaN;
            button.Margin = new Thickness(0, 0, 6, 4);
        }
        StyleButton(add, "Plus", primary: true);
        StyleButton(open, "File");
        StyleButton(del, "Trash");
        WorkspaceWindowUi.StyleButton(close);
        _commands.Add((add, () => CanWrite));
        _commands.Add((open, () => !_loading && _grid.SelectedItems.Count == 1));
        _commands.Add((del, () => CanWrite && !_signedOnly && _grid.SelectedItems.Count > 0));
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var roleBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void AddRole(string label, string icon, bool signed, bool primary, Func<AttachmentRow, bool> available)
        {
            var button = new Button { Content = label };
            StyleButton(button, icon);
            bool Eligible() => CanWrite && _grid.SelectedItems.Count == 1 &&
                _grid.SelectedItem is AttachmentRow row && available(row);
            _commands.Add((button, Eligible));
            button.Click += async (_, _) =>
            {
                if (Eligible() && _grid.SelectedItem is AttachmentRow row)
                    await ChangeRoleAsync(row, signed, primary, _revision);
            };
            roleBar.Children.Add(button);
        }
        bool IsPdf(AttachmentRow row) => Path.GetExtension(row.Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        AddRole("Отметить как подписанный PDF", "Check", true, false, row => IsPdf(row) && row.Role != "signed_contract");
        AddRole("Сделать основным PDF", "File", true, true, row => IsPdf(row) && (row.Role != "signed_contract" || !row.IsPrimary));
        AddRole("Снять отметку", "X", false, false, row => row.Role == "signed_contract");
        DockPanel.SetDock(roleBar, Dock.Top); root.Children.Add(roleBar);
        _grid.SelectionChanged += (_, _) => UpdateCommands();
        UpdateCommands();

        DockPanel.SetDock(_grid, Dock.Top);
        root.Children.Add(_grid);

        Loaded += async (_, _) => await ReloadAsync(allowClose: true);
        Closing += (_, e) => { if (_loading && !_allowCloseDuringRead) e.Cancel = true; };
        Closed += (_, _) =>
        {
            _closed = true;
            _readCancellation?.Cancel();
            _openCancellation?.Cancel();
        };
    }

    private void UpdateCommands()
    {
        foreach (var command in _commands) command.Button.IsEnabled = command.Available();
    }

    private async Task ReloadAsync(bool allowClose = false)
    {
        if (_closed || _readCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _readCancellation = cancellation;
        var token = cancellation.Token;
        _loading = true;
        _allowCloseDuringRead = allowClose;
        _revision = "";
        _objectFolder = "";
        UpdateCommands();
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var data = SqlServerAttachments.Read(_connectionString, _contractId, token);
                var rows = new List<AttachmentRow>(data.Entries.Count);
                foreach (var entry in data.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (!_signedOnly || entry.Role == "signed_contract") rows.Add(ToRow(entry));
                }
                return (Rows: rows.AsReadOnly(), data.Folder, data.Revision);
            }, token);
            if (_closed || token.IsCancellationRequested) return;
            var selected = (_grid.SelectedItem as AttachmentRow)?.Id;
            _rows = snapshot.Rows;
            _grid.ItemsSource = _rows;
            _grid.SelectedItem = _rows.FirstOrDefault(row => row.Id == selected);
            _objectFolder = snapshot.Folder;
            _revision = snapshot.Revision;
        }
        catch (Exception ex)
        {
            if (_closed || token.IsCancellationRequested) return;
            _rows = Array.Empty<AttachmentRow>();
            _grid.ItemsSource = _rows;
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _readCancellation = null;
            _allowCloseDuringRead = false;
            _loading = false;
            if (!_closed) UpdateCommands();
        }
    }

    private static AttachmentRow ToRow(SqlServerAttachments.Entry item) => new()
    {
        Id = item.Id, Name = item.Name, Path = item.Path, Size = item.Size, AddedBy = item.AddedBy,
        AddedAt = item.AddedAt, Note = item.Note, Role = item.Role, IsPrimary = item.IsPrimary
    };

    internal static async Task OpenSignedAsync(Window owner, string connectionString, long contractId,
        string title, Func<bool> validTarget)
    {
        if (!owner.IsLoaded || !validTarget() || !DirectOpenOwners.Add(owner)) return;
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        EventHandler closed = (_, _) => cancellation.Cancel();
        owner.Closed += closed;
        bool CanPublish() => !token.IsCancellationRequested && owner.IsLoaded && validTarget();
        try
        {
            var row = await Task.Run(() =>
            {
                var entries = SqlServerAttachments.Read(connectionString, contractId, token).Entries;
                var id = SignedPdfSelection.DirectId(entries.Select(entry =>
                    new SignedPdfSelection.Entry(entry.Id, entry.Role, entry.IsPrimary)));
                if (id is null) return null;
                var selected = ToRow(entries.Single(entry => entry.Id == id));
                ValidateOpenFile(selected, signedOnly: true);
                token.ThrowIfCancellationRequested();
                return selected;
            }, token);
            if (!CanPublish()) return;
            if (row is null)
            {
                new AttachmentWindow(connectionString, contractId, title, signedOnly: true) { Owner = owner }.ShowDialog();
                return;
            }
            LaunchFile(row);
        }
        catch (Exception ex)
        {
            if (CanPublish()) MessageBox.Show(owner, ex.Message, "Подписанный PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            owner.Closed -= closed;
            DirectOpenOwners.Remove(owner);
        }
    }

    private static void ValidateOpenFile(AttachmentRow row, bool signedOnly)
    {
        if (!Path.IsPathFullyQualified(row.Path))
            throw new InvalidOperationException("Относительный путь требует настройки общей папки: " + row.Path);
        if (!File.Exists(row.Path))
            throw new FileNotFoundException("Файл отсутствует или серверная папка недоступна.", row.Path);
        if (signedOnly && (row.Role != "signed_contract" ||
            !Path.GetExtension(row.Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Файл не отмечен как подписанный PDF.");
    }

    // ShellExecute stays on the STA dispatcher, only after the current target is checked.
    private static void LaunchFile(AttachmentRow row)
    {
        using var process = Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
    }

    private void AttachmentMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (_loading || ContextMenuTarget.SelectRow<AttachmentRow>(_grid, e) is not { } row) return;
        var menu = new ContextMenu { PlacementTarget = _grid,
            Placement = e.CursorLeft < 0 && e.CursorTop < 0
                ? System.Windows.Controls.Primitives.PlacementMode.Bottom
                : System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var open = ContextMenuIcons.Create("Открыть файл", "File", _grid.SelectedItems.Count == 1);
        open.Click += async (_, _) =>
        {
            if (_rows.Contains(row) && ReferenceEquals(_grid.SelectedItem, row)) await OpenSelectedAsync();
        };
        menu.Items.Add(open);
        var revision = _revision;
        var singlePdf = _grid.SelectedItems.Count == 1 && Path.GetExtension(row.Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        bool SameSelection() => CanWrite && revision == _revision && _rows.Contains(row) &&
            _grid.SelectedItems.Count == 1 && ReferenceEquals(_grid.SelectedItem, row);
        void RoleAction(string label, string glyph, bool signed, bool primary, bool enabled)
        {
            var item = ContextMenuIcons.Create(label, glyph, CanWrite && enabled);
            item.Click += async (_, _) => { if (SameSelection()) await ChangeRoleAsync(row, signed, primary, revision); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        RoleAction("Отметить как подписанный PDF", "Check", true, false, singlePdf && row.Role != "signed_contract");
        RoleAction("Сделать основным PDF", "File", true, true, singlePdf && (row.Role != "signed_contract" || !row.IsPrimary));
        RoleAction("Снять отметку", "X", false, false, _grid.SelectedItems.Count == 1 && row.Role == "signed_contract");
        if (!_signedOnly)
        {
            var ids = _grid.SelectedItems.OfType<AttachmentRow>().Select(item => item.Id).OrderBy(id => id).ToArray();
            menu.Items.Add(new Separator());
            var delete = ContextMenuIcons.Create("Удалить из списка…", "Trash", CanWrite, danger: true);
            delete.Click += (_, _) =>
            {
                if (CanWrite && revision == _revision && _grid.SelectedItems.OfType<AttachmentRow>().Select(item => item.Id).OrderBy(id => id).SequenceEqual(ids))
                    DeleteSelected();
            };
            menu.Items.Add(delete);
        }
        menu.IsOpen = true;
    }

    private static void ValidatePdf(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выберите существующий PDF с доступным абсолютным путём.");
        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        if (pdf.PageCount == 0) throw new InvalidDataException("PDF не содержит страниц.");
    }

    private async Task ChangeRoleAsync(AttachmentRow row, bool signed, bool primary, string revision)
    {
        if (!CanWrite || _revision != revision) return;
        _loading = true;
        UpdateCommands();
        try
        {
            await Task.Run(() =>
            {
                if (signed) ValidatePdf(row.Path);
                SqlServerAttachments.SetSigned(_connectionString, _contractId, row.Id, row.Path, signed, primary, revision);
            });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { await ReloadAsync(); }
    }

    private async void AddFiles()
    {
        if (!CanWrite) return;
        var revision = _revision;
        var objectFolder = _objectFolder;
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Добавить вложения",
            Filter = _signedOnly ? "PDF (*.pdf)|*.pdf" : "Все файлы (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true || !CanWrite || revision != _revision) return;
        var selected = dlg.FileNames.ToArray();
        _loading = true;
        UpdateCommands();
        try
        {
            await Task.Run(() =>
            {
                var folder = SqlServerAttachments.StorageFolder(_connectionString, _contractId, objectFolder);
                if (!Directory.Exists(objectFolder)) throw new DirectoryNotFoundException("Общая папка объекта недоступна.");
                // Validate all PDFs before copying anything.
                if (_signedOnly) foreach (var file in selected) ValidatePdf(file);
                Directory.CreateDirectory(folder);
                var files = new List<SqlServerAttachments.NewFile>();
                foreach (var src in selected)
                {
                    var name = Path.GetFileName(src);
                    var stem = Path.GetFileNameWithoutExtension(name);
                    if (stem.Length > 70) stem = stem[..70];
                    var dst = Path.Combine(folder, stem + "_" + Guid.NewGuid().ToString("N")[..12] + Path.GetExtension(name));
                    var temp = dst + ".copying";
                    try
                    {
                        File.Copy(src, temp, overwrite: false);
                        File.Move(temp, dst);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    if (_signedOnly) ValidatePdf(dst);
                    files.Add(new SqlServerAttachments.NewFile(name, dst, new FileInfo(dst).Length));
                }
                // Do not delete completed files after an uncertain SQL COMMIT.
                // Registration is all-or-nothing; preserved files allow reconciliation.
                SqlServerAttachments.Register(_connectionString, _contractId, files, _signedOnly, revision);
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message + "\n\nСкопированные файлы могут остаться в тестовой папке. Проверьте список перед повтором.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { await ReloadAsync(); }
    }

    private async Task OpenSelectedAsync()
    {
        if (_loading || _closed || _grid.SelectedItems.Count != 1 || _grid.SelectedItem is not AttachmentRow row) return;
        using var cancellation = new CancellationTokenSource();
        _openCancellation = cancellation;
        var token = cancellation.Token;
        _loading = true;
        _allowCloseDuringRead = true;
        UpdateCommands();
        bool CanPublish() => !_closed && !token.IsCancellationRequested &&
            _grid.SelectedItems.Count == 1 && ReferenceEquals(_grid.SelectedItem, row) && _rows.Contains(row);
        try
        {
            await Task.Run(() => ValidateOpenFile(row, _signedOnly), token);
            if (CanPublish()) LaunchFile(row);
        }
        catch (Exception ex)
        {
            if (CanPublish()) MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _openCancellation = null;
            _allowCloseDuringRead = false;
            _loading = false;
            if (!_closed) UpdateCommands();
        }
    }

    private async void DeleteSelected()
    {
        if (!CanWrite || _signedOnly || _grid.SelectedItems.Count == 0) return;
        var revision = _revision;
        var ids = _grid.SelectedItems.OfType<AttachmentRow>().Select(row => row.Id).OrderBy(id => id).ToArray();
        if (MessageBox.Show(this, $"Удалить из списка {ids.Length} вложений? Файлы останутся на диске.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes ||
            !CanWrite || revision != _revision ||
            !_grid.SelectedItems.OfType<AttachmentRow>().Select(row => row.Id).OrderBy(id => id).SequenceEqual(ids)) return;
        _loading = true;
        UpdateCommands();
        try { await Task.Run(() => SqlServerAttachments.Delete(_connectionString, _contractId, ids, revision)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { await ReloadAsync(); }
    }
}
