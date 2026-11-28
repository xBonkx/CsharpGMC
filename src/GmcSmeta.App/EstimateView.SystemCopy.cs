using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class EstimateView
{
    private async void CopySection_Click(object sender, RoutedEventArgs e)
    {
        if (_estimate is not { Locked: false } snapshot ||
            Window.GetWindow(this) is not MainWindow main || _doc.StageRest) return;
        var displayedRevision = _serverRevision;
        var selected = EstimateGrid.SelectedItem as EditableEstimateRow;
        var selection = EstimateGrid.SelectedItems.OfType<EditableEstimateRow>().ToArray();
        var source = CopySourceSystem(selected);
        bool Current() => CanChangeHeader(snapshot, displayedRevision, false) &&
            ReferenceEquals(selected, EstimateGrid.SelectedItem) &&
            EstimateGrid.SelectedItems.OfType<EditableEstimateRow>()
                .SequenceEqual(selection, ReferenceEqualityComparer.Instance) &&
            source is not null && ReferenceEquals(CopySourceSystem(selected), source);
        if (!Current() || source is null)
        {
            SetStatus("Выберите систему или её позицию и дождитесь сохранения.", true);
            return;
        }
        var systemId = source.Source.SystemId;
        DocRow[] destinations;
        try
        {
            destinations = await Task.Run(() =>
            {
                using var conn = SqlServerDatabase.Open(_sqlConnectionString);
                if (SqlServerDatabase.Revision(conn) != displayedRevision)
                    throw new InvalidOperationException("Смета изменилась. Обновите её.");
                var docs = Workspace.ReadObjectDocumentMetadata(conn, _objectId);
                if (SqlServerDatabase.Revision(conn) != displayedRevision)
                    throw new InvalidOperationException("Документы изменились при чтении.");
                return docs.Where(d => SystemCopySource.CanReceive(d, _objectId)).ToArray();
            });
        }
        catch (Exception ex) { SetStatus("Копирование недоступно: " + ex.Message, true); return; }
        if (!Current()) return;
        destinations = destinations.Where(d => main.CanReceiveSystemCopy(d, _contractId, _objectId)).ToArray();
        if (destinations.Length == 0) { SetStatus("Нет доступного документа назначения.", true); return; }

        var existing = _rows.Where(r => r.IsSystem).Select(r => r.Name).ToList();
        var dialog = new SystemCopyDialog(main, source.Name,
            SqlServerSystemCopy.SuggestName(source.Name, existing), destinations, _contractId);
        if (dialog.ShowDialog() != true || dialog.Target is not { } target) return;
        if (!Current() || !main.CanReceiveSystemCopy(target, _contractId, _objectId)) return;
        var newName = dialog.NewName;
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        Interlocked.Increment(ref _pendingWrites);
        SetStatus("Копирование системы…", false);
        await _writeLock.WaitAsync();
        var saved = false;
        try
        {
            if (_serverConflict || _detached || !ReferenceEquals(snapshot, _estimate) ||
                displayedRevision != _serverRevision || !ReferenceEquals(selected, EstimateGrid.SelectedItem) ||
                !EstimateGrid.SelectedItems.OfType<EditableEstimateRow>()
                    .SequenceEqual(selection, ReferenceEqualityComparer.Instance) ||
                !main.CanEditEstimateHeader(_contractId, _objectId) ||
                (target.Id != _contractId && !main.CanReceiveSystemCopy(target, _contractId, _objectId)))
                throw new InvalidOperationException("Выбор или документ изменились.");
            var result = await Task.Run(() => SqlServerSystemCopy.CopySystemTo(_sqlConnectionString,
                _contractId, systemId, target.Id, newName, displayedRevision));
            if (!result.Save.Saved || result.SystemId is not long newId)
                throw new InvalidOperationException(result.Save.Error ?? "Копирование не подтверждено.");
            _serverRevision = result.Save.Revision;
            _saveAwaitingReadback = true;
            if (target.Id == _contractId)
            {
                _focusSystemAfterReload = checked((int)newId);
                if (!_detached && !string.IsNullOrEmpty(EstimateSearch.Text)) EstimateSearch.Clear();
            }
            saved = true;
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = "Копирование не подтверждено: " + ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError ?? "Система скопирована; выполняется сверка…", _saveError is not null);
                _reloadTimer.Start();
            }
        }
        if (saved && target.Id != _contractId)
        {
            try { main.RefreshSystemCopyTarget(target.Id); }
            catch (Exception ex) { SetStatus("Система скопирована; обновите список: " + ex.Message, true); }
        }
    }
}

internal sealed class SystemCopyDialog : Window
{
    private readonly TextBox _name = new() { MinHeight = 32, MaxLength = 255 };
    private readonly ComboBox _targets = new() { MinHeight = 32, DisplayMemberPath = nameof(DocRow.Title) };
    public DocRow? Target => _targets.SelectedItem as DocRow;
    public string NewName => _name.Text.Trim();

    internal SystemCopyDialog(Window owner, string sourceName, string suggested,
        IReadOnlyList<DocRow> destinations, int sourceContractId)
    {
        Owner = owner;
        Title = "Копировать систему";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WorkspaceWindowUi.ApplyChrome(this);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(WorkspaceWindowUi.Heading("Копирование системы"));
        panel.Children.Add(WorkspaceWindowUi.Hint("Источник: " + sourceName));
        panel.Children.Add(new TextBlock { Text = "Документ назначения", Margin = new Thickness(0, 12, 0, 4) });
        _targets.ItemsSource = destinations;
        _targets.SelectedItem = destinations.FirstOrDefault(d => d.Id == sourceContractId) ?? destinations.FirstOrDefault();
        panel.Children.Add(_targets);
        panel.Children.Add(new TextBlock { Text = "Название новой системы", Margin = new Thickness(0, 12, 0, 4) });
        _name.Text = suggested;
        panel.Children.Add(_name);
        panel.Children.Add(WorkspaceWindowUi.Hint("Копируются сметные позиции, комплектующие и подразделы. Закупки, выдача, КС и позиции вне сметы не переносятся."));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "Копировать", IsDefault = true, MinWidth = 110 };
        WorkspaceWindowUi.StyleButton(cancel);
        WorkspaceWindowUi.StyleButton(ok, primary: true);
        void Validate() => ok.IsEnabled = Target is not null && NewName.Length is >= 1 and <= 255;
        _name.TextChanged += (_, _) => Validate();
        _targets.SelectionChanged += (_, _) => Validate();
        ok.Click += (_, _) => { if (ok.IsEnabled) DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
        Validate();
    }
}
