using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace GmcSmeta.App;

public partial class LetterEditorWindow : Window
{
    private readonly LettersArchive _archive;
    private readonly string _connectionString;
    private readonly LetterDocument _letter;
    private bool _loading;
    private bool _commitUncertain, _busy, _closed, _closeApproved, _revisionConflict;
    private long _numberRequest;

    private void MarkCommitUncertain(LetterCommitUncertainException ex)
    {
        _commitUncertain = true;
        MessageBox.Show(this, ex.Message + "\nЗакройте письмо и проверьте историю перед повторным открытием.",
            "Запись не подтверждена", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public LetterEditorWindow(LettersArchive archive, LetterDocument letter, string connectionString)
    {
        InitializeComponent();
        _archive = archive;
        _letter = letter;
        _connectionString = connectionString;
        LoadLetter();
        Closed += (_, _) => { _closed = true; _numberRequest++; };
        PreviewKeyDown += async (_, args) =>
        {
            if (args.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            { args.Handled = true; await RunAsync(async () => { await TrySaveAsync(); }); }
            if (args.Key == Key.Escape) { Close(); args.Handled = true; }
        };
    }

    private void LoadLetter()
    {
        _loading = true;
        try
        {
            Title = "Письмо — " + (_letter.Number.Length > 0 ? _letter.Number : "новое");
            NumberBox.Text = _letter.Number;
            DateBox.Text = _letter.Date;
            ObjectBox.Text = _letter.Object;
            SubjectBox.Text = _letter.Subject;
            SenderBox.Text = _letter.Sender;
            RecipientBox.Text = _letter.Recipient;
            GreetingBox.Text = _letter.Greeting;
            SignerBox.Text = _letter.Signer;
            ScanMode.IsChecked = _letter.UseScan;
            BodyBox.Document = LettersPdf.ReadBody(_letter.Body);
            ContextNote.Text = _letter.ContractId is not null ? "Договор: " + _letter.ContractLabel : "";
            StatusText.Text = _letter.Sent is { } s
                ? $"Отправлено {s.Date} · {s.Channel} · {s.Destination}"
                : "Черновик · отправка учитывается вручную, программа ничего не отправляет";
            var sent = _letter.Sent is not null;
            foreach (var box in new[] { NumberBox, DateBox, ObjectBox, SubjectBox,
                         SenderBox, RecipientBox, GreetingBox, SignerBox })
                box.IsReadOnly = sent;
            BodyBox.IsReadOnly = sent;
            ScanMode.IsEnabled = !sent;
            CompanyButton.IsEnabled = !sent;
            ObjectButton.IsEnabled = !sent && _letter.ObjectId is null && _letter.ContractId is null;
            BuildButton.IsEnabled = SendButton.IsEnabled = !sent;
            if (sent)
            {
                RoleBox.SelectedIndex = 2;
                RoleBox.IsEnabled = false;
            }
            RefreshFiles();
        }
        finally { _loading = false; }
    }

    private void RefreshFiles()
    {
        var selected = (FilesGrid.SelectedItem as LetterAttachment)?.Id;
        FilesGrid.ItemsSource = null;
        FilesGrid.ItemsSource = _letter.Files.ToArray();
        if (selected is not null) FilesGrid.SelectedItem = _letter.Files.FirstOrDefault(x => x.Id == selected);
        ReleasesBox.ItemsSource = null;
        ReleasesBox.ItemsSource = _letter.Releases.Select(x => x.Split('/').Last()).ToArray();
    }

    private async Task<bool> TrySaveAsync()
    {
        if (_commitUncertain || _revisionConflict) return false;
        try
        {
            if (_letter.Sent is null)
            {
                _letter.Date = DateBox.Text.Trim();
                _letter.Number = NumberBox.Text.Trim();
                _letter.Object = ObjectBox.Text.Trim();
                _letter.Subject = SubjectBox.Text.Trim();
                _letter.Sender = SenderBox.Text.Trim();
                _letter.Recipient = RecipientBox.Text.Trim();
                _letter.Greeting = GreetingBox.Text.Trim();
                _letter.Signer = SignerBox.Text.Trim();
                _letter.UseScan = ScanMode.IsChecked == true;
                _letter.Body = LettersPdf.SaveBody(BodyBox.Document);
            }
            await Task.Run(() => _archive.Sql.Save(_letter));
            _loading = true;
            NumberBox.Text = _letter.Number;
            Title = "Письмо — " + _letter.Number;
            _loading = false;
            return true;
        }
        catch (LetterCommitUncertainException ex)
        {
            _loading = false;
            MarkCommitUncertain(ex);
            return false;
        }
        catch (Exception exception)
        {
            if (exception is DBConcurrencyException) _revisionConflict = true;
            _loading = false;
            MessageBox.Show(this, exception.Message, "Письмо", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void Number_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loading && _letter.Sent is null) { _numberRequest++; _letter.NumberAuto = false; }
    }

    private void Date_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _busy || _closed || _letter.Sent is not null || !_letter.NumberAuto || _letter.Date == DateBox.Text.Trim()) return;
        AutoNumber_Click(sender, e);
    }

    private async void AutoNumber_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _busy || _closed || _letter.Sent is not null || _commitUncertain || _revisionConflict) return;
        var date = DateBox.Text.Trim();
        var id = _letter.Id;
        var request = ++_numberRequest;
        try
        {
            var value = await Task.Run(() => _archive.Sql.SuggestNumber(date, id));
            if (_closed || _busy || request != _numberRequest || DateBox.Text.Trim() != date) return;
            _loading = true;
            try
            {
                NumberBox.Text = value;
                _letter.NumberAuto = true;
            }
            finally { _loading = false; }
        }
        catch (Exception ex)
        {
            if (!_closed && !_busy && request == _numberRequest && DateBox.Text.Trim() == date)
                MessageBox.Show(this, ex.Message, "Номер по дате", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static (long Id, string Name)? Select(Window owner, string title, IReadOnlyList<(long Id, string Name)> items)
    {
        if (items.Count == 0)
        {
            MessageBox.Show(owner, "В справочнике пока нет записей.", title);
            return null;
        }
        var dlg = new Window
        {
            Title = title, Owner = owner, Width = 520, Height = 410,
            MinWidth = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White
        };
        var root = new DockPanel { Margin = new Thickness(16) };
        var ok = new Button { Content = "Выбрать", Padding = new Thickness(14, 7, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(ok, Dock.Bottom);
        root.Children.Add(ok);
        var list = new ListBox { ItemsSource = items.Select(x => $"{x.Id}: {x.Name}").ToArray() };
        list.SelectedIndex = 0;
        root.Children.Add(list);
        ok.Click += (_, _) => { if (list.SelectedIndex >= 0) dlg.DialogResult = true; };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedIndex >= 0) dlg.DialogResult = true; };
        dlg.Content = root;
        return dlg.ShowDialog() == true ? items[list.SelectedIndex] : null;
    }

    private async void Company_Click(object sender, RoutedEventArgs e)
    => await RunAsync(async () =>
    {
        if (_letter.Sent is not null) return;
        try
        {
            var values = await Task.Run(() =>
            {
                using var conn = SqlServerDatabase.Open(_connectionString);
                using var command = new SqlCommand(
                    "SELECT id,name,legal_full,signer_post,signer_name FROM dbo.companies ORDER BY name,id", conn);
                using var reader = command.ExecuteReader();
                var rows = new List<(long Id, string Name, string Legal, string Position, string Signer)>();
                while (reader.Read())
                    rows.Add((SqlInteger.ReadInt64(reader, 0), reader.GetString(1),
                        reader.IsDBNull(2) ? "" : reader.GetString(2),
                        reader.IsDBNull(3) ? "" : reader.GetString(3),
                        reader.IsDBNull(4) ? "" : reader.GetString(4)));
                return rows;
            });
            var choice = Select(this, "Наши организации", values.Select(v => (v.Id, v.Name)).ToArray());
            if (choice is null) return;
            var co = values.First(v => v.Id == choice.Value.Id);
            SenderBox.Text = string.IsNullOrWhiteSpace(co.Legal) ? co.Name : co.Legal;
            SignerBox.Text = string.Join(' ', new[] { co.Position, co.Signer }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Организация"); }
    });

    private async void Object_Click(object sender, RoutedEventArgs e)
    => await RunAsync(async () =>
    {
        if (_letter.Sent is not null || _letter.ObjectId is not null || _letter.ContractId is not null) return;
        try
        {
            var values = await Task.Run(() =>
            {
                using var conn = SqlServerDatabase.Open(_connectionString);
                using var cmd = new SqlCommand(
                    "SELECT id,name,customer,customer_full,cust_post,cust_name FROM dbo.objects " +
                    "WHERE archived=0 ORDER BY name,id", conn);
                using var r = cmd.ExecuteReader();
                var rows = new List<(long Id, string Name, string Customer, string Full, string Post, string Person)>();
                while (r.Read())
                    rows.Add((SqlInteger.ReadInt64(r, 0), r.GetString(1),
                        r.IsDBNull(2) ? "" : r.GetString(2),
                        r.IsDBNull(3) ? "" : r.GetString(3),
                        r.IsDBNull(4) ? "" : r.GetString(4),
                        r.IsDBNull(5) ? "" : r.GetString(5)));
                return rows;
            });
            var choice = Select(this, "Выбрать объект", values.Select(v => (v.Id, v.Name)).ToArray());
            if (choice is null) return;
            var obj = values.First(v => v.Id == choice.Value.Id);
            ObjectBox.Text = obj.Name;
            _letter.ObjectId = obj.Id;
            if (string.IsNullOrWhiteSpace(RecipientBox.Text))
                RecipientBox.Text = string.Join("\n", new[] {
                    string.IsNullOrWhiteSpace(obj.Full) ? obj.Customer : obj.Full,
                    obj.Post, obj.Person }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Объект"); }
    });

    private void Bold_Click(object sender, RoutedEventArgs e) =>
        EditingCommands.ToggleBold.Execute(null, BodyBox);
    private void Italic_Click(object sender, RoutedEventArgs e) =>
        EditingCommands.ToggleItalic.Execute(null, BodyBox);
    private void List_Click(object sender, RoutedEventArgs e) =>
        EditingCommands.ToggleNumbering.Execute(null, BodyBox);
    private void AlignLeft_Click(object sender, RoutedEventArgs e) =>
        EditingCommands.AlignLeft.Execute(null, BodyBox);
    private void AlignCenter_Click(object sender, RoutedEventArgs e) =>
        EditingCommands.AlignCenter.Execute(null, BodyBox);
    private void AlignJustify_Click(object sender, RoutedEventArgs e) =>
        EditingCommands.AlignJustify.Execute(null, BodyBox);

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private LetterAttachment? SelectedFile => FilesGrid.SelectedItem as LetterAttachment;

    private async void FileAdd_Click(object sender, RoutedEventArgs e)
    => await RunAsync(async () =>
    {
        if (!await TrySaveAsync()) return;
        var kind = (RoleBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Приложение";
        if (_letter.Sent is not null) kind = "Подтверждение отправки";
        var dlg = new OpenFileDialog
        {
            Title = "Добавить PDF / изображения", Filter = "PDF и фото|*.pdf;*.png;*.jpg;*.jpeg",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var path in dlg.FileNames)
        {
            try { await RunStaAsync(() => _archive.AddFile(_letter, path, kind)); }
            catch (LetterCommitUncertainException ex) { MarkCommitUncertain(ex); break; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Вложение"); if (ex is DBConcurrencyException) { _revisionConflict = true; break; } }
        }
        RefreshFiles();
    });

    private async void FileOpen_Click(object sender, RoutedEventArgs e)
    => await RunAsync(async () =>
    {
        if (SelectedFile is not { } f) return;
        try { OpenPath(await Task.Run(() => _archive.FilePath(f))); }
        catch (Exception ex) { if (ex is DBConcurrencyException) _revisionConflict = true; MessageBox.Show(this, ex.Message, "Вложение"); }
    }, allowReadOnly: true);

    private async void FileRename_Click(object sender, RoutedEventArgs e)
    => await RunAsync(async () =>
    {
        if (_letter.Sent is not null || SelectedFile is not { } file) return;
        var dialog = new PromptDialog("Название файла", "Название в письме и выдаче", file.Title) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Value.Length == 0) return;
        var before = file.Title;
        file.Title = dialog.Value;
        if (!await TrySaveAsync()) file.Title = before;
        RefreshFiles();
    });

    private async void FileToggle_Click(object sender, RoutedEventArgs e)
    => await RunAsync(async () =>
    {
        if (_letter.Sent is not null || SelectedFile is not { } file) return;
        file.Included = !file.Included;
        if (!await TrySaveAsync()) file.Included = !file.Included;
        RefreshFiles();
    });

    private async void FileUp_Click(object sender, RoutedEventArgs e) => await MoveAsync(-1);
    private async void FileDown_Click(object sender, RoutedEventArgs e) => await MoveAsync(1);
    private Task MoveAsync(int offset)
    => RunAsync(async () =>
    {
        var i = FilesGrid.SelectedIndex;
        var j = i + offset;
        if (_letter.Sent is not null || i < 0 || j < 0 || j >= _letter.Files.Count) return;
        (_letter.Files[i], _letter.Files[j]) = (_letter.Files[j], _letter.Files[i]);
        var saved = await TrySaveAsync();
        if (!saved) (_letter.Files[i], _letter.Files[j]) = (_letter.Files[j], _letter.Files[i]);
        RefreshFiles();
        FilesGrid.SelectedIndex = saved ? j : i;
    });

    private async void Releases_DoubleClick(object sender, MouseButtonEventArgs e)
    => await RunAsync(async () =>
    {
        var index = ReleasesBox.SelectedIndex;
        if (index < 0 || index >= _letter.Releases.Count) return;
        var relative = _letter.Releases[index];
        try { OpenPath(await Task.Run(() => _archive.ReleasePath(relative))); }
        catch (Exception ex) { if (ex is DBConcurrencyException) _revisionConflict = true; MessageBox.Show(this, ex.Message, "Архивный выпуск"); }
    }, allowReadOnly: true);

    private async void Save_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { await TrySaveAsync(); });

    private async void Preview_Click(object sender, RoutedEventArgs e)
    => await RunAsync(async () =>
    {
        if (!await TrySaveAsync()) return;
        try { OpenPath(await RunStaAsync(() => _archive.Preview(_letter))); }
        catch (LetterCommitUncertainException ex) { MarkCommitUncertain(ex); }
        catch (Exception ex) { if (ex is DBConcurrencyException) _revisionConflict = true; MessageBox.Show(this, ex.Message, "Предпросмотр PDF"); }
    });

    private async Task<string?> BuildAsync()
    {
        if (!await TrySaveAsync()) return null;
        try
        {
            var combine = CombineBox.IsChecked == true;
            var folder = await RunStaAsync(() => _archive.Build(_letter, combine));
            RefreshFiles();
            OpenPath(folder);
            return _letter.Releases.Last();
        }
        catch (LetterCommitUncertainException ex)
        {
            MarkCommitUncertain(ex);
            return null;
        }
        catch (Exception ex)
        {
            if (ex is DBConcurrencyException) _revisionConflict = true;
            MessageBox.Show(this, ex.Message, "Собрать выдачу");
            return null;
        }
    }

    private async void Build_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { await BuildAsync(); });

    private async void Sent_Click(object sender, RoutedEventArgs e)
    => await RunAsync(async () =>
    {
        if (_letter.Sent is not null) return;
        // A fresh immutable release prevents sending a stale snapshot.
        var release = await BuildAsync();
        if (release is null) return;
        var dialog = new Window
        {
            Owner = this, Title = "Учёт отправки", Width = 540, Height = 315,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Дата отправки" });
        var date = new TextBox { Text = _letter.Date, Margin = new Thickness(0, 4, 0, 10) };
        panel.Children.Add(date);
        panel.Children.Add(new TextBlock { Text = "Способ" });
        var channel = new ComboBox { Margin = new Thickness(0, 4, 0, 10) };
        foreach (var item in new[] { "Электронная почта", "ЭДО", "Бумажное письмо", "Мессенджер", "Иное" })
            channel.Items.Add(item);
        channel.SelectedIndex = 0;
        panel.Children.Add(channel);
        panel.Children.Add(new TextBlock { Text = "Получатель / адрес отправки" });
        var destination = new TextBox { Text = _letter.Recipient, Margin = new Thickness(0, 4, 0, 12) };
        panel.Children.Add(destination);
        var confirm = new Button { Content = "Отметить отправленным", Padding = new Thickness(10, 7, 10, 7),
            HorizontalAlignment = HorizontalAlignment.Right };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(confirm);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        try
        {
            var sentDate = date.Text.Trim();
            var sentChannel = channel.SelectedItem?.ToString() ?? "";
            var sentDestination = destination.Text.Trim();
            await Task.Run(() => _archive.Sql.MarkSent(_letter, sentDate, sentChannel, sentDestination, release));
            LoadLetter();
        }
        catch (LetterCommitUncertainException ex) { MarkCommitUncertain(ex); }
        catch (Exception ex) { if (ex is DBConcurrencyException) _revisionConflict = true; MessageBox.Show(this, ex.Message, "Учёт отправки"); }
    });


    private async void History_Click(object sender, RoutedEventArgs e)
    => await RunAsync(async () =>
    {
        try
        {
            var history = await Task.Run(() => _archive.Sql.ReadHistory(_letter.Id));
            var dialog = new Window
            {
                Owner = this, Title = "История изменений — " + _letter.Number,
                Width = 870, Height = 570, MinWidth = 650, MinHeight = 380,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = System.Windows.Media.Brushes.White
            };
            var dock = new DockPanel { Margin = new Thickness(14) };
            var heading = new TextBlock
            {
                Text = "Сохранённые версии · только для просмотра · время UTC",
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(heading, Dock.Top);
            dock.Children.Add(heading);
            var list = new ListBox
            {
                Width = 245, Margin = new Thickness(0, 0, 12, 0),
                ItemsSource = history.Select(h => $"Версия {h.Revision} — {h.SavedUtc:dd.MM.yyyy HH:mm:ss}").ToArray()
            };
            DockPanel.SetDock(list, Dock.Left);
            dock.Children.Add(list);
            var details = new TextBox
            {
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13
            };
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedIndex < 0 || list.SelectedIndex >= history.Count) return;
                var h = history[list.SelectedIndex];
                var d = h.Snapshot;
                details.Text = $"Версия: {h.Revision} · {h.SavedUtc:dd.MM.yyyy HH:mm:ss} UTC\n" +
                    $"Исходящий: {d.Number} от {d.Date}\nСтатус: {d.Status}\n" +
                    $"Объект: {d.Object}\nТема: {d.Subject}\nКому: {d.Recipient}\n" +
                    $"Отправитель: {d.Sender}\nПодписант: {d.Signer}\n\n" +
                    $"Текст письма:\n{LettersPdf.BodyText(d.Body)}\n\n" +
                    "Файлы:\n" + string.Join("\n", d.Files.Select(file =>
                        $"{file.Kind}: {file.Title} ({file.Pages} стр., SHA-256 {file.Sha256})")) +
                    "\n\nВыпуски:\n" + string.Join("\n", d.Releases);
            };
            dock.Children.Add(details);
            dialog.Content = dock;
            if (history.Count > 0) list.SelectedIndex = 0;
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "История писем", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }, allowReadOnly: true);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Editor_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; return; }
        if (_closeApproved) return;
        e.Cancel = true;
        if (_commitUncertain || _revisionConflict)
        {
            if (MessageBox.Show(this, "Закрыть без повторной записи? Проверьте историю письма.",
                "Письмо", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        else
        {
            var saved = false;
            await RunAsync(async () => { saved = await TrySaveAsync(); });
            if (!saved) return;
        }
        _closeApproved = true;
        Dispatcher.BeginInvoke(new Action(Close));
    }

    private async Task RunAsync(Func<Task> action, bool allowReadOnly = false)
    {
        if (_busy || _closed) return;
        if (!allowReadOnly && (_commitUncertain || _revisionConflict))
        {
            MessageBox.Show(this, "Запись заблокирована. Откройте письмо заново после проверки истории.", "Письмо");
            return;
        }
        _numberRequest++;
        _busy = true;
        LetterTabs.IsEnabled = EditorActions.IsEnabled = false;
        try { await action(); }
        catch (LetterCommitUncertainException ex) { MarkCommitUncertain(ex); }
        catch (DBConcurrencyException ex)
        {
            _revisionConflict = true;
            MessageBox.Show(this, ex.Message, "Письмо", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Письмо", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            _busy = false;
            if (!_closed) LetterTabs.IsEnabled = EditorActions.IsEnabled = true;
        }
    }

    // PDF rendering and image decoding construct WPF objects on a private
    // STA thread. The live RichTextBox document is never passed to it.
    private static Task<T> RunStaAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}