using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App;

public partial class ActsEditorView
{
    private sealed record ActMoreSnapshot(ActCommandTarget Target, object Sheet, KsRowVm[] Selection);

    private ActCommandTarget? CaptureMoreCommand(object sender, bool selectionRequired = false)
    {
        if (sender is not MenuItem item) return CaptureActCommand();
        if (!item.IsEnabled || item.Tag is not ActMoreSnapshot snapshot) return null;
        bool SameSnapshot() => IsVisible && IsCurrentActCommand(snapshot.Target) &&
            ReferenceEquals(snapshot.Sheet, _sheet) &&
            (!selectionRequired || ActGrid.SelectedItems.OfType<KsRowVm>().SequenceEqual(snapshot.Selection));
        if (!SameSnapshot() || !ActGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !ActGrid.CommitEdit(DataGridEditingUnit.Row, true) || !SameSnapshot())
        {
            SetStatus("Данные или выделение изменились. Откройте меню заново.", true);
            return null;
        }
        return snapshot.Target;
    }

    /// <summary>«Добавить позицию вне сметы…» (acts.add_extra_line): позиция вне КП +
    /// строка акта одной операцией, сумма строки видна до сохранения.</summary>
    private async void AddExtra_Click(object sender, RoutedEventArgs e)
    {
        var target = CaptureMoreCommand(sender);
        var current = Current;
        if (target is null || current is null || current.Signed || _contractLocked) return;
        var sheet = _sheet;
        var selectedSystem = ActGrid.SelectedItems.OfType<KsRowVm>().FirstOrDefault()?.SystemId;
        List<(long Id, string Label)> systems;
        try
        {
            systems = await Task.Run(() =>
            {
                using var conn = SqlServerDatabase.Open(target.ConnectionString);
                if (SqlServerDatabase.Revision(conn) != target.Revision)
                    throw new InvalidOperationException("Данные изменились. Обновите КС.");
                var rows = new List<(long Id, string Label)>();
                using (var read = new SqlCommand(
                    "SELECT id, name FROM dbo.systems WHERE contract_id=@id ORDER BY [order], id", conn))
                {
                    read.Parameters.AddWithValue("@id", target.ContractId);
                    using var r = read.ExecuteReader();
                    while (r.Read()) rows.Add((SqlInteger.ReadInt64(r, 0), r.GetString(1)));
                }
                if (SqlServerDatabase.Revision(conn) != target.Revision)
                    throw new InvalidOperationException("Данные изменились. Обновите КС.");
                return rows;
            });
            RequireCurrentActCommand(target);
            if (!ReferenceEquals(sheet, _sheet) || _signed || _contractLocked) return;
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка чтения систем: " + ex.Message, true);
            return;
        }
        if (systems.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "В договоре нет систем — сначала создайте систему.",
                "Позиция вне сметы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        long defaultSystem = selectedSystem ?? systems[0].Id;

        var dialog = new Window
        {
            Title = "Добавить позицию вне сметы",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "Работа или материал, которых нет в подписанной смете. Строка войдёт только в этот акт; " +
                   "смета, договор и подписанные акты не изменятся.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
        });
        var systemCombo = new ComboBox
        {
            ItemsSource = systems.Select(s => new ComboPair(s.Id, s.Label)).ToList(),
            DisplayMemberPath = "Label",
            SelectedIndex = Math.Max(0, systems.FindIndex(s => s.Id == defaultSystem)),
            Margin = new Thickness(0, 8, 0, 0),
        };
        var nameBox = new TextBox { Margin = new Thickness(0, 4, 0, 0) };
        var unitBox = new TextBox { Text = "шт", MaxWidth = 120, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0) };
        var qtyBox = new TextBox { Margin = new Thickness(0, 4, 0, 0) };
        var priceBox = new TextBox { Margin = new Thickness(0, 4, 0, 0) };
        var sumLabel = new TextBlock { Text = "0,00 ₽", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Система", Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(systemCombo);
        panel.Children.Add(new TextBlock { Text = "Наименование", Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = "Единица", Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(unitBox);
        panel.Children.Add(new TextBlock { Text = "Фактический объём", Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(qtyBox);
        panel.Children.Add(new TextBlock { Text = "Цена за единицу, ₽", Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(priceBox);
        panel.Children.Add(new TextBlock { Text = "Сумма строки", Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(sumLabel);
        void UpdateSum()
        {
            decimal? q = decimal.TryParse(qtyBox.Text.Trim().Replace(',', '.'), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var qv) ? qv : null;
            decimal? p = decimal.TryParse(priceBox.Text.Trim().Replace(" ", "").Replace(',', '.'),
                NumberStyles.Number, CultureInfo.InvariantCulture, out var pv) ? pv : null;
            sumLabel.Text = q is not null && p is not null
                ? Money.Format(decimal.Round(q.Value * p.Value, 2)) + " ₽" : "—";
        }
        qtyBox.TextChanged += (_, _) => UpdateSum();
        priceBox.TextChanged += (_, _) => UpdateSum();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var ok = new Button
        {
            Content = "Добавить в акт",
            Style = (Style)FindResource("PythonToolbarPrimary"),
            Padding = new Thickness(14, 5, 14, 5),
        };
        var cancel = new Button { Content = "Отмена", Style = (Style)FindResource("PythonToolbarButton"),
            Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        (long SystemId, string Name, string Unit, decimal Qty, decimal Price)? draft = null;
        cancel.IsCancel = true;
        ok.Click += (_, _) =>
        {
            var name = nameBox.Text.Trim();
            var unit = unitBox.Text.Trim();
            decimal? q = decimal.TryParse(qtyBox.Text.Trim().Replace(',', '.'), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var qv) ? qv : null;
            decimal? p = decimal.TryParse(priceBox.Text.Trim().Replace(" ", "").Replace(',', '.'),
                NumberStyles.Number, CultureInfo.InvariantCulture, out var pv) ? pv : null;
            if (name.Length == 0)
            {
                MessageBox.Show(dialog, "Впишите наименование позиции.", "Позиция вне сметы",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (unit.Length == 0)
            {
                MessageBox.Show(dialog, "Впишите единицу измерения.", "Позиция вне сметы",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (q is null || q <= 0m)
            {
                MessageBox.Show(dialog, "Впишите количество числом больше нуля, например 4,11.",
                    "Позиция вне сметы", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (p is null || p < 0m)
            {
                MessageBox.Show(dialog, "Впишите цену числом, например 4 530,50.",
                    "Позиция вне сметы", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (systemCombo.SelectedItem is not ComboPair system || !systems.Any(s => s.Id == system.Id)) return;
            draft = (system.Id, name, unit, q.Value, p.Value);
            dialog.DialogResult = true;
        };
        if (dialog.ShowDialog() != true || draft is not { } value) return;
        try
        {
            RequireCurrentActCommand(target);
            if (!ReferenceEquals(sheet, _sheet) || _signed || _contractLocked)
                throw new InvalidOperationException("Снимок акта изменился. Повторите команду.");
        }
        catch (Exception ex) { SetStatus(ex.Message, true); return; }

        long createdId;
        try
        {
            createdId = await RunActWriteAsync(() => SqlServerActs.AddExtraLine(
                target.ConnectionString, target.ActId, value.SystemId, value.Name,
                value.Unit, value.Qty, value.Price, target.Revision));
        }
        catch (Exception ex)
        {
            if (_connectionString != target.ConnectionString || _contractId != target.ContractId || _actId != target.ActId) return;
            _revision = "";
            SetStatus("Результат добавления не подтверждён: " + ex.Message + ". Перечитайте КС перед повтором.", true);
            await ReloadActAfterSave();
            return;
        }
        if (_connectionString != target.ConnectionString || _contractId != target.ContractId || _actId != target.ActId) return;
        _revision = "";
        SetStatus("Позиция вне сметы добавлена в акт: " + value.Name);
        await ReloadActAfterSave();
        if (_actId == target.ActId && _contractId == target.ContractId && _connectionString == target.ConnectionString &&
            _rows?.FirstOrDefault(r => r.PositionId == createdId && r.OutOfKp && r.InAct) is { } row)
        {
            ActGrid.SelectedItem = row;
            ActGrid.ScrollIntoView(row);
        }
    }

}
