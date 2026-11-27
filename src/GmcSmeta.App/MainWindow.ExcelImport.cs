using System.IO;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App;

public partial class MainWindow
{
    /// <summary>Импорт Excel из главного окна или карточки объекта.</summary>
    private async void ImportExcelFromObject_Click(object sender, RoutedEventArgs e) =>
        await ImportExcelForObjectAsync(CommandObject() is { Archived: false } obj ? obj.Id : null);

    private async Task ImportExcelForObjectAsync(int? preferredObjectId, bool newDocumentsOnly = false)
    {
        if (_sqlConnectionString is null || _serverConn is null || !SqlWritesEnabled) return;
        if (preferredObjectId is int id && !_objects.Any(obj => obj.Id == id && !obj.Archived))
        {
            StatusText.Text = "Объект больше недоступен для импорта. Обновите список.";
            return;
        }
        var fileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Импорт КП и смет из Excel",
            Filter = "Книги Excel (*.xlsx;*.xlsm;*.xls)|*.xlsx;*.xlsm;*.xls",
            CheckFileExists = true
        };
        if (fileDialog.ShowDialog(this) != true) return;

        // Capture exactly the bytes the reader is going to parse.
        string parsedFingerprint;
        try { parsedFingerprint = SqlServerExcelExistingUpdate.FileFingerprint(fileDialog.FileName); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось прочитать Excel",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ExcelImportResult result;
        using (var cancel = new CancellationTokenSource())
        {
            var loading = ImportProgressWindow("Чтение и разбор Excel…", cancel);
            loading.Show();
            try
            {
                result = await Task.Run(() => ExcelWorkbookReader.Read(fileDialog.FileName, cancel.Token), cancel.Token);
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Чтение Excel отменено.";
                return;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка импорта: " + ex.Message;
                MessageBox.Show(this, ex.Message, "Импорт Excel", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally { loading.Close(); }
        }
        if (result.Blocks.Count == 0)
        {
            MessageBox.Show(this, "В книге не найдено таблиц КП.\n" + string.Join("\n", result.Notes),
                "Импорт Excel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (SqlServerExcelExistingUpdate.FileFingerprint(fileDialog.FileName) != parsedFingerprint)
        {
            MessageBox.Show(this, "Файл изменился во время разбора. Импорт отменён.",
                "Excel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The New KP dialog explicitly requests a new document, not an update.
        if (!newDocumentsOnly)
        {
            // Separate opt-in path: the original new-document importer remains untouched.
            var mode = MessageBox.Show(this,
                "Обновить существующую смету из Excel?\n\n" +
                "Да — предпросмотр безопасных изменений существующего КП.\n" +
                "Нет — прежний импорт новых КП/смет.\nОтмена — ничего не делать.",
                "Режим импорта Excel", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (mode == MessageBoxResult.Yes)
            {
                await UpdateExistingEstimateFromExcelAsync(fileDialog.FileName, result, parsedFingerprint, preferredObjectId);
                return;
            }
            if (mode != MessageBoxResult.No) return;
        }

        // Read only metadata once, not one network query for each preview block.
        Dictionary<long, IReadOnlySet<string>> existing;
        List<ObjectRow> objects;
        List<DocRow> documents;
        IReadOnlyList<CompanyInfo> companies;
        string revision;
        try
        {
            (existing, objects, documents, companies, revision) = await Task.Run(() =>
            {
                using var conn = SqlServerDatabase.Open(_sqlConnectionString);
                var before = SqlServerDatabase.Revision(conn);
                var freshObjects = Workspace.Objects(conn);
                var freshDocuments = Workspace.ReadAllDocumentMetadata(conn);
                var freshCompanies = SqlServerWorkspace.Companies(conn);
                using var query = new SqlCommand(
                    "SELECT object_id,source_file,source_sheet FROM dbo.contracts WHERE source_file<>N''", conn);
                using var reader = query.ExecuteReader();
                var keys = new Dictionary<long, HashSet<string>>();
                while (reader.Read())
                {
                    var id = SqlInteger.ReadInt64(reader, 0);
                    var file = reader.IsDBNull(1) ? "" : Path.GetFileName(reader.GetString(1).Replace('\\', '/'));
                    var sheet = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    if (!keys.TryGetValue(id, out var set)) keys[id] = set = new(StringComparer.OrdinalIgnoreCase);
                    set.Add(file + "|" + sheet);
                }
                reader.Close();
                if (string.IsNullOrWhiteSpace(before) || before != SqlServerDatabase.Revision(conn))
                    throw new InvalidOperationException("Данные изменились во время чтения. Повторите импорт.");
                return (keys.ToDictionary(p => p.Key, p => (IReadOnlySet<string>)p.Value), freshObjects, freshDocuments, freshCompanies, before);
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось проверить повторный импорт:\n" + ex.Message,
                "Импорт Excel", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var preview = new ExcelImportPreviewWindow(this, result, objects, documents,
            preferredObjectId, existing, companies);
        if (preview.ShowDialog() != true) return;
        var selections = preview.Choices;
        var target = preview.ObjectId;
        var name = preview.NewObjectName;
        using var writeCancellation = new CancellationTokenSource();
        var progress = ImportProgressWindow($"Запись {selections.Sum(s => s.Block.PositionCount)} строк в SQL Server…",
            writeCancellation);
        progress.Show();
        try
        {
            var saved = await Task.Run(() => SqlServerExcelImport.Import(
                _sqlConnectionString, target, name, fileDialog.FileName, selections,
                revision, writeCancellation.Token), writeCancellation.Token);
            ReloadAndSelect(checked((int)saved.ObjectId));
            StatusText.Text = $"Импортировано документов: {saved.ContractIds.Count}, строк: " +
                selections.Sum(s => s.Block.PositionCount) + ".";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Импорт отменён. SQL-транзакция откатилась.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось выполнить импорт Excel: " + ex.Message;
            MessageBox.Show(this, ex.Message + "\n\nИзменения одного импорта выполняются транзакционно.",
                "Ошибка импорта Excel", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { progress.Close(); }
    }

    private Window ImportProgressWindow(string message, CancellationTokenSource cancellation)
    {
        var progress = new Window
        {
            Title = "Импорт Excel", Width = 450, Height = 155,
            Owner = this, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12) });
        content.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 8 });
        var cancel = new Button { Content = "Отмена", Width = 92,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        cancel.Click += (_, _) => { cancellation.Cancel(); cancel.IsEnabled = false; };
        content.Children.Add(cancel);
        progress.Content = content;
        progress.Closing += (_, e) => { if (!cancellation.IsCancellationRequested) cancellation.Cancel(); };
        return progress;
    }
}
