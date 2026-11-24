using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Win32;

namespace GmcSmeta.App;

public partial class PassportsWindow : Window
{
    private readonly SqlServerPassports _sql;
    private readonly long _objectId;
    private readonly string _objectName;
    private readonly string _objectAddress;
    private readonly List<VentPassport> _rows;
    private readonly ObservableCollection<PassportListRow> _summaries = new();
    private ObservableCollection<VentParameterRow> _parameters = new();
    private ObservableCollection<VentFlowPoint> _points = new();
    private List<VentEquipment> _equipment = VentPassports.NewEquipment();
    private List<VentEquipmentTemplate> _catalogue = new();
    private int _current = -1, _sectionIndex;
    private bool _loading;
    private string _root = "";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GMC-Smeta-Next", "passports.settings.json");

    public sealed class PassportListRow
    {
        public Guid Id { get; init; }
        public string System { get; init; } = "";
        public string Purpose { get; init; } = "";
        public int PointsCount { get; init; }
        public string MeasurementStatus { get; init; } = "";
    }

    public sealed class VentParameterRow
    {
        public string Parameter { get; set; } = "";
        public string Project { get; set; } = "";
        public string Actual { get; set; } = "";
    }

    public PassportsWindow(string connectionString, long objectId, string objectName, string address)
    {
        _loading = true;
        InitializeComponent();
        _objectId = objectId;
        _objectName = objectName;
        _objectAddress = address;
        _sql = new SqlServerPassports(connectionString);
        _rows = _sql.List(objectId).ToList();
        Title = "Паспорта вентиляции · " + objectName;
        _root = LoadRoot();
        ArchiveLabel.Text = "Архив PDF (рекомендуется общий сетевой каталог): " + _root;
        PassportList.ItemsSource = _summaries;
        SectionCombo.ItemsSource = VentPassports.Sections
            .Select((section, i) => $"Е.1.{i + 1} · {section.Title}").ToArray();
        SectionCombo.SelectedIndex = 0;
        RefreshCatalogue();
        RefreshList();
        _loading = false;
        if (_rows.Count == 0) AddNew();
        else PassportList.SelectedIndex = 0;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SaveCurrent();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    private static string LoadRoot()
    {
        var env = Environment.GetEnvironmentVariable("GMC_SMETA_PASSPORTS_ROOT");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                var value = settings.RootElement.GetProperty("root").GetString();
                if (!string.IsNullOrWhiteSpace(value)) return Path.GetFullPath(value);
            }
        }
        catch (Exception) { /* A corrupted local UI setting cannot modify passport data. */ }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GMC-Smeta-Next", "Passports");
    }

    private void RefreshCatalogue()
    {
        _catalogue = _sql.Catalogue().ToList();
        var selected = CatalogueBox.SelectedItem as string;
        CatalogueBox.ItemsSource = _catalogue.Select(x => x.Key).ToArray();
        if (selected is not null) CatalogueBox.SelectedItem = selected;
    }

    private void RefreshList()
    {
        _loading = true;
        var selectedId = _current >= 0 && _current < _rows.Count ? _rows[_current].Id : Guid.Empty;
        _summaries.Clear();
        foreach (var p in _rows)
        {
            bool done;
            try { done = !VentPassports.IsDraft(p); }
            catch (ArgumentException) { done = false; }
            _summaries.Add(new PassportListRow
            {
                Id = p.Id, System = p.System,
                Purpose = string.IsNullOrWhiteSpace(p.Purpose)
                    ? VentPassports.DetectPurpose(p.System) : p.Purpose,
                PointsCount = p.Points.Count,
                MeasurementStatus = done ? "Заполнены" : "Не заполнены"
            });
        }
        if (selectedId != Guid.Empty)
        {
            var index = _summaries.ToList().FindIndex(x => x.Id == selectedId);
            if (index >= 0) PassportList.SelectedIndex = index;
        }
        _loading = false;
    }

    private void LoadPassport(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        _loading = true;
        try
        {
            _current = index;
            var p = _rows[index];
            SystemBox.Text = p.System;
            PurposeBox.Text = p.Purpose;
            ObjectBox.Text = p.Object;
            AddressBox.Text = p.Address;
            OrganizationBox.Text = p.Organization;
            ZoneBox.Text = p.Zone;
            LocationBox.Text = p.Location;
            DateBox.Text = p.Date;
            FilterCheck.IsChecked = p.Filter;
            _equipment = VentPassports.DeepCopyEquipment(p.Equipment);
            _points = new ObservableCollection<VentFlowPoint>(
                p.Points.Select(x => new VentFlowPoint
                {
                    Section = x.Section, Room = x.Room, Project = x.Project, Actual = x.Actual
                }));
            PointsGrid.ItemsSource = _points;
            _sectionIndex = Math.Max(0, SectionCombo.SelectedIndex);
            ShowSection();
            UpdateTotals();
            EditorTabs.IsEnabled = true;
        }
        finally { _loading = false; }
    }

    private void CollectSection()
    {
        if (_sectionIndex < 0 || _sectionIndex >= _equipment.Count || _sectionIndex == 6) return;
        if (!EquipmentGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EquipmentGrid.CommitEdit(DataGridEditingUnit.Row, true))
            throw new InvalidOperationException("Завершите редактирование оборудования.");
        var spec = VentPassports.Sections[_sectionIndex].Parameters;
        var item = _equipment[_sectionIndex];
        var rows = _parameters.ToList();
        if (rows.Count != spec.Length) return;
        item.Project = rows.Select(r => r.Project ?? "").ToList();
        item.Actual = rows.Select(r => r.Actual ?? "").ToList();
        item.Notes = EquipmentNotes.Text;
        if (_sectionIndex == 0)
        {
            item.Project[3] = ""; // E.1.1 fan flow always belongs to E.2, never to a catalog.
            item.Actual[3] = "";
        }
    }

    private void ShowSection()
    {
        if (_sectionIndex < 0 || _sectionIndex >= VentPassports.Sections.Length) return;
        var section = _equipment[_sectionIndex];
        var names = VentPassports.Sections[_sectionIndex].Parameters;
        _parameters = new ObservableCollection<VentParameterRow>(
            names.Select((name, i) => new VentParameterRow
            {
                Parameter = name, Project = section.Project[i], Actual = section.Actual[i]
            }));
        EquipmentGrid.ItemsSource = _parameters;
        EquipmentGrid.IsReadOnly = _sectionIndex == 6;
        EquipmentNotes.Text = section.Notes;
        EquipmentNotes.IsEnabled = _sectionIndex is not (6 or 7);
        if (_sectionIndex == 0) UpdateTotals();
    }

    private void Collect()
    {
        if (_current < 0 || _current >= _rows.Count) return;
        CollectSection();
        if (!PointsGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !PointsGrid.CommitEdit(DataGridEditingUnit.Row, true))
            throw new InvalidOperationException("Завершите редактирование измерений.");
        var p = _rows[_current];
        p.System = SystemBox.Text.Trim();
        p.Purpose = PurposeBox.Text.Trim();
        p.Object = ObjectBox.Text;
        p.Address = AddressBox.Text;
        p.Organization = OrganizationBox.Text;
        p.Zone = ZoneBox.Text;
        p.Location = LocationBox.Text;
        p.Date = DateBox.Text;
        p.Filter = FilterCheck.IsChecked == true;
        p.Equipment = VentPassports.DeepCopyEquipment(_equipment);
        p.Points = _points.Where(x => new[] { x.Section, x.Room, x.Project, x.Actual }
                .Any(s => !string.IsNullOrWhiteSpace(s)))
            .Select(x => new VentFlowPoint
            {
                Section = x.Section.Trim(), Room = x.Room.Trim(),
                Project = x.Project.Trim(), Actual = x.Actual.Trim()
            }).ToList();
        VentPassports.Validate(p);
    }

    private bool SaveCurrent(bool refresh = true)
    {
        if (_current < 0 || _current >= _rows.Count) return true;
        try
        {
            Collect();
            _sql.Save(_rows[_current]);
            if (refresh) RefreshList();
            RefreshCatalogue();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Паспорта — сохранение",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void PassportList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PassportList.SelectedItem is not PassportListRow row) return;
        var next = _rows.FindIndex(x => x.Id == row.Id);
        if (next < 0 || next == _current) return;
        var previous = _current;
        if (previous >= 0 && !SaveCurrent(refresh: false))
        {
            _loading = true;
            PassportList.SelectedIndex = previous;
            _loading = false;
            return;
        }
        LoadPassport(next);
        RefreshList();
    }

    private void AddNew()
    {
        try
        {
            if (_current >= 0 && !SaveCurrent()) return;
            var p = VentPassports.Create(_objectId, _objectName, _objectAddress);
            _sql.Save(p);
            _rows.Add(p);
            RefreshList();
            PassportList.SelectedIndex = _rows.Count - 1;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Добавить паспорт"); }
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddNew();

    private void Batch_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrent()) return;
        var window = new Window
        {
            Title = "Добавить системы", Owner = this, Width = 550, Height = 420,
            MinWidth = 380, MinHeight = 260, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var label = new TextBlock { Text = "По одной системе на строку (можно вставить столбец Excel)",
            Margin = new Thickness(0, 0, 0, 9) };
        DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
        var ok = new Button { Content = "Добавить", Padding = new Thickness(12, 7, 12, 7),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(ok, Dock.Bottom); panel.Children.Add(ok);
        var multiline = new TextBox { AcceptsReturn = true, AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(multiline);
        ok.Click += (_, _) => window.DialogResult = true;
        window.Content = panel;
        if (window.ShowDialog() != true || _current < 0) return;
        var batch = VentPassports.BatchCreate(_rows[_current], multiline.Text).ToList();
        if (batch.Count == 0) return;
        try
        {
            _sql.SaveBatch(_objectId, batch);
            _rows.AddRange(batch);
            RefreshList();
            PassportList.SelectedIndex = _rows.Count - 1;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Добавить системы"); }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_current < 0 || !SaveCurrent()) return;
        try
        {
            var p = VentPassports.Duplicate(_rows[_current]);
            _sql.Save(p);
            _rows.Add(p);
            RefreshList();
            PassportList.SelectedIndex = _rows.Count - 1;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Копирование паспорта"); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current < 0) return;
        if (MessageBox.Show(this, "Удалить выбранный паспорт? Ранее выпущенные PDF сохранятся в архиве.",
                "Удаление паспорта", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            var p = _rows[_current];
            _sql.Delete(_objectId, p.Id, p.Revision);
            _rows.RemoveAt(_current);
            _current = -1;
            RefreshList();
            if (_rows.Count > 0) PassportList.SelectedIndex = 0;
            else AddNew();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Удаление паспорта"); }
    }

    private void System_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        var suggested = VentPassports.DetectPurpose(SystemBox.Text);
        if (suggested.Length > 0) PurposeBox.Text = suggested;
    }

    private void Section_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _current < 0) return;
        try { CollectSection(); }
        catch (InvalidOperationException ex)
        {
            _loading = true;
            try { SectionCombo.SelectedIndex = _sectionIndex; }
            finally { _loading = false; }
            MessageBox.Show(this, ex.Message, "Оборудование");
            return;
        }
        _sectionIndex = SectionCombo.SelectedIndex;
        ShowSection();
    }

    private void Equipment_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (_sectionIndex == 6 || (_sectionIndex == 0 &&
            _parameters.IndexOf((VentParameterRow)e.Row.Item) == 3))
            e.Cancel = true;
    }

    private void Catalogue_Click(object sender, RoutedEventArgs e)
    {
        if (_current < 0 || CatalogueBox.SelectedItem is not string key) return;
        var catalog = _catalogue.FirstOrDefault(c => c.Key == key);
        if (catalog is null) return;
        try { CollectSection(); }
        catch (InvalidOperationException ex) { MessageBox.Show(this, ex.Message, "Оборудование"); return; }
        var data = _rows[_current];
        VentPassports.ApplyTemplate(data, catalog);
        _equipment = VentPassports.DeepCopyEquipment(data.Equipment);
        FilterCheck.IsChecked = data.Filter;
        ShowSection();
        UpdateTotals();
    }

    private void ApplyCommon_Click(object sender, RoutedEventArgs e)
    {
        if (_current < 0) return;
        try
        {
            Collect();
            VentPassports.ApplyCommon(_rows[_current], _rows);
            _sql.SaveBatch(_objectId, _rows);
            RefreshList();
            MessageBox.Show(this, $"Общие реквизиты применены к {_rows.Count} системам.", "Паспорта");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Общие реквизиты"); }
    }

    private void UpdateTotals()
    {
        if (_loading && _current == -1) return;
        try
        {
            var points = _points.Where(x => new[] { x.Section, x.Room, x.Project, x.Actual }
                .Any(s => !string.IsNullOrWhiteSpace(s))).ToArray();
            var (project, actual) = VentPassports.Totals(points);
            TotalsLabel.Text = "Итого: проект " + (VentPassports.Format(project) is { Length: > 0 } pr ? pr : "—") +
                               " · факт " + (VentPassports.Format(actual) is { Length: > 0 } ac ? ac : "—") +
                               (actual is null ? " · ЧЕРНОВИК" : "");
            if (_sectionIndex == 0 && _parameters.Count > 3)
            {
                _parameters[3].Project = VentPassports.Format(project);
                _parameters[3].Actual = VentPassports.Format(actual);
                EquipmentGrid.Items.Refresh();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            TotalsLabel.Text = "Проверьте числа в столбцах расходов. Фактический итог не вычислен.";
            if (_sectionIndex == 0 && _parameters.Count > 3)
            {
                _parameters[3].Project = "";
                _parameters[3].Actual = "";
                EquipmentGrid.Items.Refresh();
            }
        }
    }

    private void AddPoint_Click(object sender, RoutedEventArgs e)
    {
        _points.Add(new VentFlowPoint());
        PointsGrid.SelectedIndex = _points.Count - 1;
    }

    private void DeletePoint_Click(object sender, RoutedEventArgs e)
    {
        if (PointsGrid.SelectedItem is VentFlowPoint point)
        {
            _points.Remove(point);
            UpdateTotals();
        }
    }

    private void Points_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(UpdateTotals), DispatcherPriority.Background);
    }

    private void Points_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (!Clipboard.ContainsText()) return;
        e.Handled = true;
        if (!PointsGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !PointsGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            MessageBox.Show(this, "Завершите редактирование измерений.", "Вставка");
            return;
        }
        var content = Clipboard.GetText().Replace("\r\n", "\n").TrimEnd('\r', '\n');
        var startRow = Math.Max(0, PointsGrid.Items.IndexOf(PointsGrid.CurrentCell.Item));
        var startColumn = Math.Max(0, PointsGrid.CurrentCell.Column?.DisplayIndex ?? 0);
        foreach (var (line, lineIndex) in content.Split('\n').Select((s, i) => (s, i)))
        {
            var rowIndex = startRow + lineIndex;
            while (_points.Count <= rowIndex) _points.Add(new VentFlowPoint());
            var row = _points[rowIndex];
            foreach (var (value, offset) in line.Split('\t').Select((s, i) => (s, i)))
            {
                switch (startColumn + offset)
                {
                    case 0: row.Section = value; break;
                    case 1: row.Room = value; break;
                    case 2: row.Project = value; break;
                    case 3: row.Actual = value; break;
                }
            }
        }
        PointsGrid.Items.Refresh();
        UpdateTotals();
    }

    private void Root_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrent()) return;
        var dlg = new OpenFolderDialog { Title = "Общий каталог PDF паспортов" };
        if (dlg.ShowDialog(this) != true) return;
        var root = Path.GetFullPath(dlg.FolderName);
        try
        {
            if (!string.Equals(root, _root, StringComparison.OrdinalIgnoreCase) &&
                _rows.Any(p => _sql.Exports(_objectId, p.Id).Count > 0))
                throw new InvalidOperationException(
                    "Экспортированные PDF уже привязаны к предыдущему архиву. " +
                    "Переключение каталога без согласованного переноса файлов запрещено.");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { root }));
            _root = root;
            ArchiveLabel.Text = "Архив PDF (рекомендуется общий сетевой каталог): " + root;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Архив паспортов"); }
    }

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (_current < 0) return;
        try
        {
            var entries = _sql.Exports(_objectId, _rows[_current].Id);
            if (entries.Count == 0)
            {
                MessageBox.Show(this, "Ранее созданных PDF для паспорта нет.", "Архив");
                return;
            }
            var picker = new Window
            {
                Owner = this, Title = "Архив выпусков", Width = 710, Height = 370,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var list = new ListBox
            {
                ItemsSource = entries.Select(x => $"{x.RelativePath}  ·  " +
                    (x.Draft ? "ЧЕРНОВИК" : "Заполнено") + $" · {x.Pages} стр.").ToArray(),
                Margin = new Thickness(12)
            };
            picker.Content = list;
            list.MouseDoubleClick += (_, _) =>
            {
                if (list.SelectedIndex < 0) return;
                var selected = entries[list.SelectedIndex];
                var file = PdfFilePath(selected.RelativePath);
                using (var stream = File.OpenRead(file))
                {
                    var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                    if (!digest.Equals(selected.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Контрольная сумма архивного PDF не совпадает.");
                }
                OpenPath(file);
            };
            picker.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Архив паспортов"); }
    }

    private string PdfFilePath(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length < 3 || parts[0] != "passports" ||
            parts.Any(x => x is "" or "." or "..") || relative.Contains('\\') ||
            relative.Contains(':') || Path.IsPathRooted(relative))
            throw new InvalidDataException("Недопустимый путь архива паспортов.");
        var basePath = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(basePath,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PDF вне каталога архива.");
        return path;
    }

    private void Export(IReadOnlyList<VentPassport> selected)
    {
        if (selected.Count == 0) { MessageBox.Show(this, "Выберите паспорта.", "PDF"); return; }
        if (!SaveCurrent()) return;
        var errors = new List<string>();
        var count = 0;
        foreach (var p in selected)
        {
            try
            {
                // Save each individual passport before export, including newly edited fields.
                var result = VentPassportPdf.Create(p);
                var relative = $"passports/{_objectId}/{Sanitize(p.System)}-{p.Id:N}-{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}.pdf";
                var path = PdfFilePath(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                var published = false;
                try
                {
                    using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        fs.Write(result.Bytes);
                        fs.Flush(true);
                    }
                    File.Move(temp, path, overwrite: false);
                    published = true;
                    var digest = Convert.ToHexString(SHA256.HashData(result.Bytes)).ToLowerInvariant();
                    _sql.RegisterExport(_objectId, new SqlServerPassports.ExportInfo(
                        p.Id, relative, digest, result.Draft, result.Pages), p.Revision);
                    count++;
                }
                catch (PassportExportCommitUncertainException ex)
                {
                    throw new PassportExportCommitUncertainException(ex.Message + "\n" + path, ex);
                }
                catch
                {
                    if (published && File.Exists(path)) File.Delete(path);
                    throw;
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            catch (PassportExportCommitUncertainException ex)
            {
                errors.Add(p.System + ": " + ex.Message);
                break; // do not continue the batch after an unknown registration
            }
            catch (Exception ex) { errors.Add(p.System + ": " + ex.Message); }
        }
        MessageBox.Show(this, $"Создано PDF: {count}" +
            (errors.Count == 0 ? "" : "\n" + string.Join("\n", errors)),
            "Паспорта — экспорт", MessageBoxButton.OK,
            errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*".ToCharArray()).ToHashSet();
        var safe = new string((value ?? "").Select(x => invalid.Contains(x) || char.IsControl(x) ? '_' : x).ToArray())
            .Trim('.', ' ');
        if (safe.Length > 80) safe = safe[..80];
        return safe.Length > 0 ? safe : "Без обозначения";
    }

    private void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = PassportList.SelectedItems.OfType<PassportListRow>().Select(x => x.Id).ToHashSet();
        if (selected.Count == 0 && _current >= 0) selected.Add(_rows[_current].Id);
        Export(_rows.Where(x => selected.Contains(x.Id)).ToArray());
    }

    private void ExportAll_Click(object sender, RoutedEventArgs e) => Export(_rows.ToArray());
    private void Save_Click(object sender, RoutedEventArgs e) => SaveCurrent();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!SaveCurrent()) e.Cancel = true;
    }
}
