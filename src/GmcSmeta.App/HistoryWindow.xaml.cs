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
    private CancellationTokenSource? _loadCancellation;

    public HistoryWindow(string connectionString, long? contractId = null)
    {
        InitializeComponent();

        TitleText.Text = Title;
        SubtitleText.Text = "Загрузка истории…";
        Loaded += async (_, _) => await LoadHistoryAsync(connectionString, contractId);
        Closed += (_, _) => _loadCancellation?.Cancel();
    }

    private async Task LoadHistoryAsync(string connectionString, long? contractId)
    {
        if (_historyLoaded || _loadCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var scope = contractId is long id
                    ? SqlServerHistory.ContractScope(connectionString, id, token)
                    : null;
                if (contractId.HasValue && scope is null)
                    return (Scope: scope, Rows: Array.Empty<Row>());
                var prefixes = scope is null ? Array.Empty<string>() : new[]
                {
                    $"{scope.Title} / ",
                    $"{scope.Title}: ",
                    $"{scope.ObjectName} / {scope.Title} / ",
                };
                var entries = SqlServerHistory.List(connectionString, contractId, token: token);
                var rows = new Row[entries.Count];
                for (var i = 0; i < entries.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    rows[i] = new Row(entries[i], prefixes);
                }
                return (Scope: scope, Rows: rows);
            }, token);
            if (token.IsCancellationRequested || !IsLoaded) return;
            var scope = snapshot.Scope;
            if (contractId.HasValue && scope is null)
            {
                Title = "История документа";
                TitleText.Text = Title;
                SubtitleText.Text = "Документ не найден.";
                EmptyText.Text = "Документ не найден или уже удалён.";
                EmptyText.Visibility = Visibility.Visible;
                return;
            }
            if (scope is not null) Title = $"История: {scope.Title}";
            TitleText.Text = Title;
            _all.AddRange(snapshot.Rows);
            _summaryText = scope is null
                ? $"Все действия в базе — последние {SqlServerHistory.Limit}"
                : $"{scope.ObjectName} · всё, что меняли в этом документе (и ведомости объекта), свежее сверху";
            _historyLoaded = true;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            // SQL cancellation may surface as SqlException rather than OperationCanceledException.
            if (token.IsCancellationRequested || !IsLoaded) return;
            SubtitleText.Text = "Не удалось загрузить историю.";
            EmptyText.Text = ex.Message;
            EmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            _loadCancellation = null;
        }
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
            SearchLine = string.Join(" ", When, Who, What, Action, Details);
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
        public string SearchLine { get; }
    }
}
