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

    /// <summary>("object" | "contract", новый id) для главного окна, как TrashDialog.restored.</summary>
    public SqlServerTrash.RestoredItem? Restored { get; private set; }

    public TrashWindow(string connectionString)
    {
        _connectionString = connectionString;
        InitializeComponent();
        SafeReload();
    }

    private void Reload()
    {
        _revision = "";
        RestoreButton.IsEnabled = false;
        ForgetButton.IsEnabled = false;
        var (entries, revision) = SqlServerTrash.ReadSnapshot(_connectionString);
        _revision = revision;
        TrashGrid.ItemsSource = entries.Select(e => new Row(e)).ToList();
        var hasData = entries.Count > 0;
        EmptyText.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        RestoreButton.IsEnabled = hasData && !_commitUncertain;
        ForgetButton.IsEnabled = hasData && !_commitUncertain;
        if (TrashGrid.Items.Count > 0) TrashGrid.SelectedIndex = 0;
    }

    private long? CurrentId() => TrashGrid.SelectedItem is Row row ? row.Id : null;

    private void Restore_Click(object sender, RoutedEventArgs e) => Restore();

    private void Forget_Click(object sender, RoutedEventArgs e) => Forget();

    private void Restore()
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
            SafeReload();
        }
    }

    private void Forget()
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
        SafeReload();
    }

    private void SafeReload()
    {
        try { Reload(); }
        catch (Exception ex)
        {
            var ticket = new ErrorLog().Record(ErrorCategory.SqlFailure, ex);
            MessageBox.Show(this, "Невозможно перечитать корзину. " + ticket.Notification,
                "Корзина", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BlockUncertainCommit()
    {
        _commitUncertain = true;
        _revision = "";
        RestoreButton.IsEnabled = false;
        ForgetButton.IsEnabled = false;
    }

    private void TrashGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            System.Windows.Controls.ItemsControl.ContainerFromElement(TrashGrid, source)
                is System.Windows.Controls.DataGridRow row &&
            row.Item is Row entry && entry.Id == CurrentId())
            Restore();
    }

    /// <summary>Строка таблицы с отображением как в Python (дата · пользователь, «N дн.»).</summary>
    private sealed record Row(SqlServerTrash.TrashEntry Entry)
    {
        public long Id => Entry.Id;
        public string KindText => Entry.Kind == "object" ? "Объект" : "Документ";
        public string Title => Entry.Title;
        public string ObjectName => Entry.ObjectName;
        public string RowsText => Entry.Rows.ToString(CultureInfo.InvariantCulture);
        public string DeletedText => Entry.DeletedAt is DateTime at
            ? $"{at:dd.MM.yyyy HH:mm} · {Entry.DeletedBy}" : "";
        public string DaysLeftText => $"{Entry.DaysLeft} дн.";
    }
}
