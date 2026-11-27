using System.Globalization;
using System.Windows;
using System.Windows.Input;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>
/// «Корзина» (trash_dialog.py): удалённые объекты и документы, хранятся 30 дней.
/// «Вернуть» восстанавливает выделенную запись (двойной щелчок — тоже), «Удалить
/// навсегда» стирает снимок. После успешного возврата окно закрывается и главное
/// окно показывает восстановленное.
/// </summary>
public partial class TrashWindow : Window
{
    private readonly string _connectionString;
    private string _revision = "";
    private bool _commitUncertain;
    private CancellationTokenSource? _loadCancellation;

    /// <summary>("object" | "contract", новый id) для главного окна, как TrashDialog.restored.</summary>
    public SqlServerTrash.RestoredItem? Restored { get; private set; }

    public TrashWindow(string connectionString)
    {
        _connectionString = connectionString;
        InitializeComponent();
        RestoreButton.IsEnabled = ForgetButton.IsEnabled = false;
        EmptyText.Text = "Загрузка корзины…";
        EmptyText.Visibility = Visibility.Visible;
        Loaded += async (_, _) => await SafeReloadAsync();
        Closed += (_, _) => _loadCancellation?.Cancel();
    }

    private async Task SafeReloadAsync()
    {
        if (_loadCancellation is not null) return;
        _revision = "";
        RestoreButton.IsEnabled = ForgetButton.IsEnabled = false;
        TrashGrid.ItemsSource = null;
        EmptyText.Text = "Загрузка корзины…";
        EmptyText.Visibility = Visibility.Visible;
        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var (entries, revision) = SqlServerTrash.ReadSnapshot(_connectionString, token);
                var rows = new List<Row>(entries.Count);
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    rows.Add(new Row(entry));
                }
                return (Rows: rows, Revision: revision);
            }, token);
            if (token.IsCancellationRequested || !IsLoaded) return;
            TrashGrid.ItemsSource = snapshot.Rows;
            var hasData = snapshot.Rows.Count > 0;
            EmptyText.Text = "Корзина пуста — ничего не удаляли.";
            EmptyText.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
            if (hasData) TrashGrid.SelectedIndex = 0;
            if (!_commitUncertain)
            {
                _revision = snapshot.Revision;
                RestoreButton.IsEnabled = ForgetButton.IsEnabled = hasData;
            }
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || !IsLoaded) return;
            var ticket = new ErrorLog().Record(ErrorCategory.SqlFailure, ex);
            EmptyText.Text = "Невозможно перечитать корзину. " + ticket.Notification;
            EmptyText.Visibility = Visibility.Visible;
            MessageBox.Show(this, EmptyText.Text, "Корзина", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _loadCancellation = null;
        }
    }

    private long? CurrentId() => TrashGrid.SelectedItem is Row row ? row.Id : null;

    private async void Restore_Click(object sender, RoutedEventArgs e) => await RestoreAsync();

    private async void Forget_Click(object sender, RoutedEventArgs e) => await ForgetAsync();

    private async Task RestoreAsync()
    {
        if (_commitUncertain || string.IsNullOrWhiteSpace(_revision) || CurrentId() is not long id) return;
        var revision = _revision;
        try
        {
            Restored = SqlServerTrash.Restore(_connectionString, id, revision);
            DialogResult = true;                     // главное окно показывает восстановленное
        }
        catch (Exception ex)
        {
            var ticket = new ErrorLog().Record(
                ex is TrashCommitUnknownException ? ErrorCategory.SqlCommitUnknown : ErrorCategory.TrashRejected,
                ex);
            if (ex is TrashCommitUnknownException) BlockUncertainCommit();
            MessageBox.Show(this, ticket.Notification + (_commitUncertain ?
                "\nНе повторяйте восстановление до проверки объекта на сервере." : ""),
                "Корзина", MessageBoxButton.OK, MessageBoxImage.Warning);
            await SafeReloadAsync();
        }
    }

    private async Task ForgetAsync()
    {
        if (_commitUncertain || string.IsNullOrWhiteSpace(_revision) ||
            TrashGrid.SelectedItem is not Row row) return;
        var id = row.Id;
        var revision = _revision;
        if (MessageBox.Show(this, $"Удалить навсегда «{row.Title}»? Вернуть будет уже нельзя.",
                "Удалить навсегда", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            if (_revision != revision || CurrentId() != id || _commitUncertain) return;
            if (!SqlServerTrash.Forget(_connectionString, id, revision))
                throw new InvalidOperationException("Запись корзины уже отсутствует. Обновите список.");
        }
        catch (Exception ex)
        {
            if (ex is TrashCommitUnknownException) BlockUncertainCommit();
            var ticket = new ErrorLog().Record(
                ex is TrashCommitUnknownException ? ErrorCategory.SqlCommitUnknown : ErrorCategory.TrashRejected, ex);
            MessageBox.Show(this, ticket.Notification + (_commitUncertain ?
                "\nНе повторяйте удаление до проверки состояния сервера." : ""),
                "Корзина", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await SafeReloadAsync();
    }

    private void BlockUncertainCommit()
    {
        _commitUncertain = true;
        _revision = "";
        RestoreButton.IsEnabled = false;
        ForgetButton.IsEnabled = false;
    }

    private async void TrashGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            System.Windows.Controls.ItemsControl.ContainerFromElement(TrashGrid, source)
                is System.Windows.Controls.DataGridRow row &&
            row.Item is Row entry && entry.Id == CurrentId())
            await RestoreAsync();
    }

    /// <summary>Строка таблицы с отображением как в Python (дата · пользователь, «N дн.»).</summary>
    private sealed record Row(SqlServerTrash.TrashEntry Entry)
    {
        public long Id => Entry.Id;
        public string KindText => Entry.Kind == "object" ? "Карточка" : "Документ";
        public string Title => Entry.Title;
        public string ObjectName => Entry.ObjectName;
        public string RowsText => Entry.Rows.ToString(CultureInfo.InvariantCulture);
        public string DeletedText => Entry.DeletedAt is DateTime at
            ? $"{at:dd.MM.yyyy HH:mm} · {Entry.DeletedBy}" : "";
        public string DaysLeftText => $"{Entry.DaysLeft} дн.";
    }
}
