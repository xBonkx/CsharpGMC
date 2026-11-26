using System.Threading;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App;

public partial class ActsEditorView
{
    private HashSet<long> _sharedExtraIds = new();

    // Read once per confirmed act snapshot, not once per context-menu opening.
    private static HashSet<long> ReadSharedExtras(SqlConnection conn, long actId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var ids = new HashSet<long>();
        if (actId == 0) return ids;
        using var cmd = new SqlCommand(
            "SELECT DISTINCT p.id FROM dbo.act_lines al " +
            "JOIN dbo.positions p ON p.id=al.position_id " +
            "WHERE al.act_id=@act AND p.out_of_kp=1 AND p.parent_id IS NULL " +
            "AND EXISTS(SELECT 1 FROM dbo.act_lines other " +
            "WHERE other.position_id=p.id AND other.act_id<>@act)", conn);
        cmd.Parameters.Add("@act", System.Data.SqlDbType.BigInt).Value = actId;
        using var reader = token.CanBeCanceled
            ? cmd.ExecuteReaderAsync(token).GetAwaiter().GetResult() : cmd.ExecuteReader();
        while (token.CanBeCanceled ? reader.ReadAsync(token).GetAwaiter().GetResult() : reader.Read())
            ids.Add(Convert.ToInt64(reader.GetValue(0)));
        token.ThrowIfCancellationRequested();
        return ids;
    }

    private bool CanMoveExtra(KsRowVm row, long actId, string revision) =>
        !_building && !_loadingAct && !_loadFailed && !ActWritePending &&
        !_signed && !_contractLocked && _actId == actId &&
        !string.IsNullOrWhiteSpace(revision) && _revision == revision &&
        _rows?.Contains(row) == true && !row.IsSection && row.OutOfKp &&
        row.InAct && !_sharedExtraIds.Contains(row.PositionId);

    private void AddExtraMoveCommands(ContextMenu menu, KsRowVm row, long actId, string revision)
    {
        if (!CanMoveExtra(row, actId, revision) || _rows is null) return;
        var peers = _rows.Where(r => !r.IsSection && r.SystemId == row.SystemId &&
            (!r.OutOfKp || r.InAct)).ToArray();
        var index = Array.IndexOf(peers, row);
        if (index < 0 || peers.Length < 2) return;

        void Add(string label, int direction, bool enabled = true)
        {
            var item = ContextMenuIcons.Create(label, direction < 0 ? "ArrowUp" : direction > 0 ? "ArrowDown" : "ArrowForward", enabled);
            item.Click += async (_, _) => await MoveExtraFromMenuAsync(row, actId, revision, direction);
            menu.Items.Add(item);
        }
        Add("Выше", -1, index > 0);
        Add("Ниже", 1, index < peers.Length - 1);
        Add("Переместить после позиции…", 0);
    }

    private async Task MoveExtraFromMenuAsync(KsRowVm row, long actId, string revision, int direction)
    {
        if (!CanMoveExtra(row, actId, revision)) return;
        long? anchorId = null;
        if (direction == 0)
        {
            var choices = _rows!.Where(r => !r.IsSection && r.SystemId == row.SystemId &&
                    r.PositionId != row.PositionId && (!r.OutOfKp || r.InAct))
                .Select(r => new ExtraAnchor(r.PositionId, $"{r.Num}  {r.Name}".Trim())).ToArray();
            if (choices.Length == 0) return;
            var picker = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(ExtraAnchor.Label),
                SelectedIndex = 0, MinWidth = 300, Margin = new Thickness(0, 8, 0, 16) };
            var dialog = new Window
            {
                Title = "Переместить после позиции", Width = 560,
                SizeToContent = SizeToContent.Height, Owner = Window.GetWindow(this),
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
            };
            var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
            panel.Children.Add(new TextBlock { Text = "Выберите позицию в этой системе:", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(picker);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "Переместить", Style = (Style)FindResource("PythonToolbarPrimary") };
            var cancel = new Button { Content = "Отмена", IsCancel = true,
                Style = (Style)FindResource("PythonToolbarButton"), Margin = new Thickness(8, 0, 0, 0) };
            ok.Click += (_, _) => { if (picker.SelectedItem is ExtraAnchor) dialog.DialogResult = true; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dialog.Content = panel;
            if (dialog.ShowDialog() != true || picker.SelectedItem is not ExtraAnchor anchor) return;
            anchorId = anchor.Id;
        }

        if (!CanMoveExtra(row, actId, revision) ||
            !ActGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !ActGrid.CommitEdit(DataGridEditingUnit.Row, true) ||
            !CanMoveExtra(row, actId, revision))
        {
            SetStatus("Данные изменились. Повторите команду после обновления КС.", true);
            return;
        }

        var connectionString = _connectionString;
        var contractId = _contractId;
        var positionId = row.PositionId;
        try
        {
            var saved = await RunActWriteAsync(() => SqlServerActs.MoveExtraLine(
                connectionString, actId, positionId, direction, revision, anchorId));
            if (_actId != actId || _contractId != contractId || _connectionString != connectionString) return;
            SetStatus(saved.Saved ? "Порядок позиций сохранён." : "Перемещение отменено: " + saved.Error, !saved.Saved);
            await ReloadAsync(actId, reuseList: false);
            if (_actId == actId && _rows?.FirstOrDefault(r => !r.IsSection && r.PositionId == positionId) is { } restored)
            {
                ActGrid.SelectedItem = restored;
                ActGrid.ScrollIntoView(restored);
            }
        }
        catch (Exception ex)
        {
            if (_actId != actId || _contractId != contractId || _connectionString != connectionString) return;
            _revision = "";
            SetStatus("Результат перемещения не подтверждён: " + ex.Message +
                ". Обновите КС перед повтором.", true);
            await ReloadAsync(actId, reuseList: false);
        }
    }

    private sealed record ExtraAnchor(long Id, string Label);
}
