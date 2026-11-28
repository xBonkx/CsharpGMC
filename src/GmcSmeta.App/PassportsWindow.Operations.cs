using System.Data;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class PassportsWindow
{
    // All callers collect WPF input before disabling the editor. The worker
    // sees only domain objects; publication and selection stay on Dispatcher.
    private async Task<bool> RunWriteAsync(Func<Task> action, string title, bool collect = true)
    {
        if (!_ready || _busy || _closed) return false;
        if (_writeBlocked)
        {
            MessageBox.Show(this, "Запись заблокирована. Сверьте данные и откройте окно заново.", title);
            return false;
        }
        try
        {
            if (collect) Collect();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        SetBusy(true);
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            if (ex is PassportWriteCommitUncertainException or DBConcurrencyException)
                _writeBlocked = true;
            MessageBox.Show(this, ex.Message + (_writeBlocked
                ? "\nПовторная запись заблокирована. Сверьте данные и откройте окно заново." : ""),
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { SetBusy(false); }
    }

    private async Task RefreshCatalogueAfterSaveAsync()
    {
        // A catalogue read failure must not turn a confirmed write into a
        // failed save and invite another create/delete attempt.
        try
        {
            var catalogue = await Task.Run(() => _sql.Catalogue());
            SetCatalogue(catalogue);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Паспорт сохранён, но каталог оборудования не обновлён.\n" + ex.Message,
                "Каталог оборудования", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task SaveCurrentCoreAsync(bool refresh = true)
    {
        if (_current < 0 || _current >= _rows.Count) return;
        var p = _rows[_current];
        await Task.Run(() => _sql.Save(p));
        if (refresh) RefreshList();
        await RefreshCatalogueAfterSaveAsync();
    }

    private Task<bool> SaveAsync() => RunWriteAsync(() => SaveCurrentCoreAsync(), "Паспорта — сохранение");

    private void SelectPassport(int index, IReadOnlySet<Guid>? selected = null)
    {
        LoadPassport(index);
        RefreshList(selected ?? new HashSet<Guid> { _rows[index].Id });
    }

    private async void PassportList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _busy || _loading || PassportList.SelectedItem is not PassportListRow row) return;
        var next = _rows.FindIndex(x => x.Id == row.Id);
        if (next < 0 || next == _current) return;
        var previous = _current;
        var selected = PassportList.SelectedItems.OfType<PassportListRow>().Select(x => x.Id).ToHashSet();
        if (previous < 0) { SelectPassport(next, selected); return; }
        if (!await RunWriteAsync(async () =>
        {
            await SaveCurrentCoreAsync(refresh: false);
            SelectPassport(next, selected);
        }, "Паспорта — сохранение"))
        {
            _loading = true;
            try { PassportList.SelectedIndex = previous; }
            finally { _loading = false; }
        }
    }

    private async Task AddEmptyCoreAsync()
    {
        var p = VentPassports.Create(_objectId, _objectName, _objectAddress);
        await Task.Run(() => _sql.Save(p));
        _rows.Add(p);
        SelectPassport(_rows.Count - 1);
    }

    private Task<bool> AddNewAsync() => RunWriteAsync(async () =>
    {
        await SaveCurrentCoreAsync();
        await AddEmptyCoreAsync();
    }, "Добавить паспорт");

    private async void Add_Click(object sender, RoutedEventArgs e) => await AddNewAsync();

    private async void Batch_Click(object sender, RoutedEventArgs e) => await RunWriteAsync(async () =>
    {
        await SaveCurrentCoreAsync();
        if (_current < 0) return;
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
        var template = _rows[_current];
        var text = multiline.Text;
        var batch = await Task.Run(() =>
        {
            var created = VentPassports.BatchCreate(template, text).ToList();
            if (created.Count > 0) _sql.SaveBatch(_objectId, created);
            return created;
        });
        if (batch.Count == 0) return;
        _rows.AddRange(batch);
        SelectPassport(_rows.Count - 1);
        await RefreshCatalogueAfterSaveAsync();
    }, "Добавить системы");

    private async void Duplicate_Click(object sender, RoutedEventArgs e) => await RunWriteAsync(async () =>
    {
        if (_current < 0) return;
        await SaveCurrentCoreAsync();
        var original = _rows[_current];
        var copy = await Task.Run(() =>
        {
            var p = VentPassports.Duplicate(original);
            _sql.Save(p);
            return p;
        });
        _rows.Add(copy);
        SelectPassport(_rows.Count - 1);
        await RefreshCatalogueAfterSaveAsync();
    }, "Копирование паспорта");

    private async void Delete_Click(object sender, RoutedEventArgs e) => await RunWriteAsync(async () =>
    {
        if (_current < 0 || _current >= _rows.Count) return;
        var index = _current;
        var p = _rows[index];
        var id = p.Id;
        var revision = p.Revision;
        if (MessageBox.Show(this, "Удалить выбранный паспорт? Ранее выпущенные PDF сохранятся в архиве.",
                "Удаление паспорта", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        if (_closed || _current != index || index >= _rows.Count ||
            !ReferenceEquals(_rows[index], p) || p.Id != id || p.Revision != revision) return;
        await Task.Run(() => _sql.Delete(_objectId, id, revision));
        _rows.RemoveAt(index);
        _current = -1;
        if (_rows.Count > 0) SelectPassport(0);
        else
        {
            EditorTabs.IsEnabled = false;
            RefreshList();
            await AddEmptyCoreAsync();
        }
    }, "Удаление паспорта", collect: false);

    private async void ApplyCommon_Click(object sender, RoutedEventArgs e) => await RunWriteAsync(async () =>
    {
        if (_current < 0) return;
        var current = _current;
        var selected = PassportList.SelectedItems.OfType<PassportListRow>().Select(x => x.Id).ToHashSet();
        var batch = await Task.Run(() =>
        {
            // Apply common fields to detached snapshots. A failed atomic
            // batch leaves other local passports unchanged.
            var copies = _rows.Select(p =>
            {
                var copy = JsonSerializer.Deserialize<VentPassport>(
                    JsonSerializer.Serialize(p, VentPassports.Json), VentPassports.Json)!;
                copy.Revision = p.Revision;
                return copy;
            }).ToArray();
            VentPassports.ApplyCommon(copies[current], copies);
            _sql.SaveBatch(_objectId, copies);
            return copies;
        });
        _rows.Clear();
        _rows.AddRange(batch);
        SelectPassport(current, selected);
        await RefreshCatalogueAfterSaveAsync();
        MessageBox.Show(this, $"Общие реквизиты применены к {_rows.Count} системам.", "Паспорта");
    }, "Общие реквизиты");

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; return; }
        if (!_ready || _closeApproved) return;
        e.Cancel = true; // cancel synchronously, before the first await
        if (_writeBlocked)
        {
            if (MessageBox.Show(this,
                "Результат сохранения требует проверки. Закрыть без повторной записи? Несохранённые изменения могут быть потеряны.",
                "Паспорта", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }
        else if (!await SaveAsync()) return;
        _closeApproved = true;
        Dispatcher.BeginInvoke(new Action(Close));
    }
}
