using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Win32;

namespace GmcSmeta.App;

/// <summary>Python kp_editor.update_from_spec/show_spec_history WPF adapters.</summary>
public partial class EstimateView
{
    private long _seenSpecRevisionId;
    private bool _specRevisionBaselineLoaded;

    /// <summary>Python check_spec_news: только последняя запись, без полного дампа.</summary>
    private void CheckSpecNews(SpecRevisionHead? latest, bool foreignRevision)
    {
        var newest = latest?.Id ?? 0;
        if (_specRevisionBaselineLoaded && foreignRevision &&
            newest > _seenSpecRevisionId && latest is not null &&
            !string.Equals(latest.User, Environment.UserName, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus($"Другой пользователь обновил КП по спецификации: " +
                $"{latest.Title}. Подробности — «Изменения спецификации».", false);
        }
        _seenSpecRevisionId = Math.Max(_seenSpecRevisionId, newest);
        _specRevisionBaselineLoaded = true;
    }

    private async void UpdateSpec_Click(object sender, RoutedEventArgs e)
    {
        if (!_isKp || _doc.IsStage || _estimate is not { Locked: false } snapshot)
        {
            SetStatus("Обновление спецификации доступно только в неподписанном КП.", true);
            return;
        }
        var revision = _serverRevision;
        if (!CanChangeHeader(snapshot, revision, false))
        {
            SetStatus("Прежде чем обновлять спецификацию, дождись записи и обнови КП.", true);
            return;
        }
        var pick = new OpenFileDialog
        {
            Title = "Новая спецификация (изм.)",
            Filter = "Excel (*.xlsx;*.xlsm;*.xls)|*.xlsx;*.xlsm;*.xls",
            Multiselect = false
        };
        if (pick.ShowDialog(Window.GetWindow(this)) != true ||
            !CanChangeHeader(snapshot, revision, false)) return;

        try
        {
            SetStatus("Сравнение спецификации с SQL Server…", false);
            var workbook = await Task.Run(() =>
                ExcelWorkbookReader.Read(pick.FileName, CancellationToken.None));
            var block = workbook.Blocks.FirstOrDefault(b => b.VolumesOnly)
                ?? workbook.Blocks.FirstOrDefault();
            if (block is null)
            {
                SetStatus("В Excel не обнаружены строки спецификации.", true);
                return;
            }
            var incoming = SqlServerSpecUpdate.Incoming(block);
            var preview = await Task.Run(() =>
                SqlServerSpecUpdate.Preview(_sqlConnectionString, _contractId, incoming));
            if (preview.Blocker is not null)
            {
                MessageBox.Show(Window.GetWindow(this), preview.Blocker,
                    "Обновление спецификации", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!CanChangeHeader(snapshot, revision, false) ||
                !string.Equals(preview.Revision, revision, StringComparison.Ordinal))
            {
                _serverConflict = true;
                SetStatus("КП изменился во время чтения Excel. Обнови смету и повтори сравнение.", true);
                return;
            }
            var dialog = new SpecUpdatePreviewWindow(Window.GetWindow(this)!, pick.FileName, preview);
            if (dialog.ShowDialog() != true || !CanChangeHeader(snapshot, revision, false)) return;
            var keys = dialog.SelectedKeys;
            var keepOrder = dialog.KeepOrder;
            if (keys.Count == 0) return;

            _reloadTimer.Stop();
            AttachSaveCloseGuard();
            Interlocked.Increment(ref _pendingWrites);
            await _writeLock.WaitAsync();
            try
            {
                _saveError = null;
                if (_serverConflict || _detached || !ReferenceEquals(snapshot, _estimate) ||
                    !string.Equals(revision, _serverRevision, StringComparison.Ordinal))
                    throw new DBConcurrencyException("Другой пользователь изменил КП.");
                var result = await Task.Run(() => SqlServerSpecUpdate.Apply(
                    _sqlConnectionString, _contractId, pick.FileName,
                    incoming, keys, keepOrder, revision));
                _serverRevision = result.Revision;
                _saveAwaitingReadback = true;
                SetStatus($"Спецификация обновлена: {result.Title}. Итого " +
                    $"{result.TotalBefore:N2} → {result.TotalAfter:N2} ₽. " +
                    "Отчёт — «Изменения спецификации», вернуть — Ctrl+Z.", false);
            }
            catch (Exception ex)
            {
                // Do not replay a transaction after an uncertain SQL COMMIT.
                _serverConflict = true;
                _saveError = ex.Message;
                SetStatus("Обновление не подтверждено SQL Server: " + ex.Message +
                    ". Проверь журнал изменений; повтор без обновления запрещён.", true);
            }
            finally
            {
                _writeLock.Release();
                Interlocked.Decrement(ref _pendingWrites);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка чтения спецификации: " + ex.Message, true);
        }
    }

    private async void SpecHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var revisions = await Task.Run(() =>
                SqlServerSpecUpdate.ReadHistory(_sqlConnectionString, _contractId));
            new SpecUpdateHistoryWindow(Window.GetWindow(this)!, revisions)
                .ShowDialog();
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось прочитать историю спецификаций: " + ex.Message, true);
        }
    }
}

/// <summary>Checked diff with per-change selection and explicit source document.</summary>
internal sealed class SpecUpdatePreviewWindow : Window
{
    private sealed class CheckRow
    {
        public bool Apply { get; set; } = true;
        public string Key { get; init; } = "";
        public string Kind { get; init; } = "";
        public string System { get; init; } = "";
        public string Name { get; init; } = "";
        public string Unit { get; init; } = "";
        public string Old { get; init; } = "";
        public string New { get; init; } = "";
        public string Warning { get; init; } = "";
    }

    private readonly List<CheckRow> _changes;
    private readonly CheckBox _keepOrder;
    private readonly TextBlock _summary;
    public bool KeepOrder => _keepOrder.IsChecked == true;
    public IReadOnlyList<string> SelectedKeys =>
        _changes.Where(x => x.Apply).Select(x => x.Key).ToArray();

    public SpecUpdatePreviewWindow(Window owner, string source, SpecSqlPreview preview)
    {
        Owner = owner;
        Title = "Обновить КП по новой спецификации — предпросмотр";
        Width = 1160;
        Height = 710;
        MinWidth = 800;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _changes = preview.Diff.Changes.Select(x => new CheckRow
        {
            Key = x.Key, Kind = x.Kind, System = x.System, Name = x.Name,
            Unit = x.Unit, Old = x.OldQuantity?.ToString("G") ?? "—",
            New = x.NewQuantity?.ToString("G") ?? "—", Warning = x.Warning
        }).ToList();
        var root = new DockPanel { Margin = new Thickness(16) };
        Content = root;
        var intro = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);
        intro.Children.Add(new TextBlock
        {
            Text = $"Файл: {Path.GetFileName(source)}. Сопоставление: система + " +
                "наименование из спецификации + ед. измерения. Ручные строки не затрагиваются.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7)
        });
        _summary = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        intro.Children.Add(_summary);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        _keepOrder = new CheckBox
        {
            Content = "Сохранить мой порядок строк (новые — в конец раздела)",
            Margin = new Thickness(0, 0, 0, 12)
        };
        bottom.Children.Add(_keepOrder);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", MinWidth = 100, Margin = new Thickness(0,0,10,0) };
        cancel.Click += (_, _) => DialogResult = false;
        var apply = new Button { Content = "Применить выбранное", MinWidth = 175,
            IsEnabled = _changes.Count > 0 };
        apply.Click += (_, _) =>
        {
            if (SelectedKeys.Count == 0)
            {
                MessageBox.Show(this, "Не выбрано ни одного изменения.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        bottom.Children.Add(buttons);

        if (_changes.Count == 0)
            intro.Children.Add(new TextBlock { Text = "Изменений нет: спецификация совпадает с КП." });
        var grid = new DataGrid
        {
            ItemsSource = _changes,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeaderWidth = 0,
            MinRowHeight = 25,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
        };
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "✓",
            Binding = new Binding(nameof(CheckRow.Apply)) { Mode = BindingMode.TwoWay },
            Width = 40 });
        AddColumn(grid, "Тип", nameof(CheckRow.Kind), 98);
        AddColumn(grid, "Система", nameof(CheckRow.System), 150);
        AddColumn(grid, "Наименование", nameof(CheckRow.Name), 340);
        AddColumn(grid, "Ед.", nameof(CheckRow.Unit), 55);
        AddColumn(grid, "Было", nameof(CheckRow.Old), 75);
        AddColumn(grid, "Стало", nameof(CheckRow.New), 75);
        AddColumn(grid, "Ограничения", nameof(CheckRow.Warning), 265);
        TableColumnMenu.SetProtect(grid, "✓|Тип|Наименование|Ед.|Было|Стало|Ограничения");
        TableColumnMenu.SetKey(grid, "spec-update-preview");
        grid.CurrentCellChanged += (_, _) => Refresh();
        root.Children.Add(grid);
        Refresh();
    }

    private void Refresh()
    {
        _summary.Text = $"Всего изменений: {_changes.Count}; выбрано: {SelectedKeys.Count}. " +
            "Существующие цены не перезаписываются; новые материалы без цены справочника " +
            "получают 0 ₽. Привязанные к КС/закупке строки защищены проверками SQL.";
    }

    private static void AddColumn(DataGrid grid, string title, string path, double width) =>
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = title, Binding = new Binding(path), Width = new DataGridLength(width),
            IsReadOnly = true
        });
}

internal sealed class SpecUpdateHistoryWindow : Window
{
    public SpecUpdateHistoryWindow(Window owner, IReadOnlyList<SpecSqlHistory> revisions)
    {
        Owner = owner;
        Title = "Изменения спецификации — история КП";
        Width = 1000; Height = 680;
        MinWidth = 700; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(14) };
        Content = root;
        var intro = new TextBlock
        {
            Text = revisions.Count == 0
                ? "Обновлений по спецификации пока нет."
                : $"Зафиксировано обновлений: {revisions.Count}. " +
                    "Выбери изменение, чтобы просмотреть полный перечень.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,10)
        };
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0,8,0,0)
        };
        var export = new Button { Content = "Экспорт XLSX…", MinWidth = 135,
            Margin = new Thickness(0,0,12,0), IsEnabled = revisions.Count > 0 };
        bool exporting = false, closed = false;
        Closed += (_, _) => closed = true;
        export.Click += async (_, _) =>
        {
            if (exporting || closed) return;
            var snapshot = revisions.ToArray();
            var dialog = new SaveFileDialog
            {
                Title = "Экспорт изменений спецификаций",
                Filter = "Excel (*.xlsx)|*.xlsx",
                FileName = "Изменения_спецификации.xlsx",
                DefaultExt = ".xlsx"
            };
            if (dialog.ShowDialog(this) != true || closed) return;
            var destination = dialog.FileName;
            exporting = true;
            export.IsEnabled = false;
            try
            {
                // A captured report may finish even if the history window closes.
                await Task.Run(() => SpecUpdateExcelReport.Export(destination, snapshot));
                if (!closed)
                    MessageBox.Show(this, "Отчёт Excel сформирован.", Title,
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                if (!closed)
                    MessageBox.Show(this, "Ошибка экспорта: " + ex.Message, Title,
                        MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                exporting = false;
                if (!closed) export.IsEnabled = revisions.Count > 0;
            }
        };
        var close = new Button { Content = "Закрыть", MinWidth = 100 };
        close.Click += (_, _) => Close();
        buttons.Children.Add(export);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var panels = new Grid();
        panels.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panels.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(panels);
        var table = new DataGrid
        {
            ItemsSource = revisions, AutoGenerateColumns = false,
            IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single
        };
        table.Columns.Add(new DataGridTextColumn { Header = "Изм.", Binding = new Binding("Title"), Width = 90 });
        table.Columns.Add(new DataGridTextColumn { Header = "Когда", Binding = new Binding("CreatedAt") { StringFormat="dd.MM.yyyy HH:mm" }, Width = 155 });
        table.Columns.Add(new DataGridTextColumn { Header = "Пользователь", Binding = new Binding("User"), Width = 135 });
        table.Columns.Add(new DataGridTextColumn { Header = "Файл", Binding = new Binding("File"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        table.Columns.Add(new DataGridTextColumn { Header = "Было", Binding = new Binding("TotalBefore") { StringFormat = "N2" }, Width = 120 });
        table.Columns.Add(new DataGridTextColumn { Header = "Стало", Binding = new Binding("TotalAfter") { StringFormat = "N2" }, Width = 120 });
        TableColumnMenu.SetProtect(table, "Изм.|Файл");
        TableColumnMenu.SetKey(table, "spec-revision-history");
        Grid.SetRow(table,0);
        panels.Children.Add(table);
        var changes = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0,9,0,0)
        };
        Grid.SetRow(changes,1);
        panels.Children.Add(changes);
        table.SelectionChanged += (_, _) =>
        {
            if (table.SelectedItem is not SpecSqlHistory item) return;
            try
            {
                using var json = JsonDocument.Parse(item.Data);
                changes.Text = JsonSerializer.Serialize(json.RootElement,
                    new JsonSerializerOptions { WriteIndented = true });
            }
            catch (JsonException) { changes.Text = item.Data; }
        };
        if (revisions.Count > 0) table.SelectedIndex = 0;
    }
}
