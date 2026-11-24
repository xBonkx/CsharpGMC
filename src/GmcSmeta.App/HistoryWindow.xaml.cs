using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// «История изменений» (history_dialog.py): только чтение dbo.change_log. Для
/// документа — его строки и строки его объекта (ведомости, выдача, сам объект),
/// для null — последние 3000 записей всей базы. Поиск идёт по уже загруженным
/// строкам, без повторного запроса в SQL.
/// </summary>
public partial class HistoryWindow : Window
{
    private static readonly Brush DeleteTint = Frozen(0xFE, 0xEC, 0xEC);
    private static readonly Brush UndoTint = Frozen(0xFF, 0xF7, 0xE6);
    private static readonly Brush CreateTint = Frozen(0xEC, 0xFD, 0xF3);

    private readonly List<Row> _all = [];
    private string _summaryText = "";
    private bool _historyLoaded;

    public HistoryWindow(string connectionString, long? contractId = null)
    {
        InitializeComponent();

        SqlServerHistory.HistoryScope? scope = null;
        if (contractId is long id)
        {
            scope = SqlServerHistory.ContractScope(connectionString, id);
            if (scope is null)
            {
                Title = "История документа";
                TitleText.Text = Title;
                SubtitleText.Text = "Документ не найден.";
                EmptyText.Text = "Документ не найден или уже удалён.";
                EmptyText.Visibility = Visibility.Visible;
                return;
            }
            Title = $"История: {scope.Title}";
        }

        TitleText.Text = Title;
        var prefixes = scope is null
            ? Array.Empty<string>()
            : new[]
            {
                $"{scope.Title} / ",
                $"{scope.Title}: ",
                $"{scope.ObjectName} / {scope.Title} / ",
            };

        var entries = SqlServerHistory.List(connectionString, contractId);
        _all.AddRange(entries.Select(entry => new Row(entry, prefixes)));
        _summaryText = scope is null
            ? $"Все действия в базе — последние {SqlServerHistory.Limit}"
            : $"{scope.ObjectName} · всё, что меняли в этом документе (и ведомости объекта), свежее сверху";
        _historyLoaded = true;
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        // Если договор не найден, не затираем сообщение о причине пустой истории.
        if (!_historyLoaded) return;

        var shown = _all
            .Where(row => HistoryPresentation.MatchesWords(SearchBox.Text, row.SearchLine))
            .ToList();
        HistoryGrid.ItemsSource = shown;
        SubtitleText.Text = $"{_summaryText}. Показано: {shown.Count} из {_all.Count}";
        EmptyText.Text = _all.Count == 0
            ? "Пока изменений нет. Всё, что поменяют в документе, появится здесь."
            : "По запросу ничего не найдено.";
        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HistoryWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
        e.Handled = true;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Строка таблицы: пять видимых колонок, цвет по action и склеенный поиск.</summary>
    private sealed class Row
    {
        public Row(SqlServerHistory.HistoryEntry entry, IReadOnlyList<string> prefixes)
        {
            When = HistoryPresentation.FormatTimestamp(entry.Ts);
            Who = entry.User;
            What = HistoryPresentation.EntityLabel(entry.Entity);
            Action = HistoryPresentation.ActionLabel(entry.Action);
            Details = HistoryPresentation.ShortDetails(entry.Details, prefixes);
            RowBrush = HistoryPresentation.TintKey(entry.Action) switch
            {
                "delete" => DeleteTint,
                "undo" => UndoTint,
                "create" => CreateTint,
                _ => Brushes.White,
            };
        }

        public string When { get; }
        public string Who { get; }
        public string What { get; }
        public string Action { get; }
        public string Details { get; }
        public Brush RowBrush { get; }

        /// <summary>history_dialog.apply склеивает ровно пять показанных колонок.</summary>
        public string SearchLine => string.Join(" ", When, Who, What, Action, Details);
    }
}
