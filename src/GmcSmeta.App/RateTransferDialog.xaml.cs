using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App;

/// <summary>Строка предпросмотра переноса расценок (rate_transfer_dialog.PreviewModel).</summary>
public sealed class RatePreviewRow : INotifyPropertyChanged
{
    private bool _included;
    public bool Included { get => _included; set { _included = value; Changed(); IncludedChanged?.Invoke(this, value); } }
    public event Action<RatePreviewRow, bool>? IncludedChanged;
    public string SourceLabel { get; init; } = "";
    public string TargetLabel { get; init; } = "";
    public string CurrentPrice { get; init; } = "";
    public string SourcePrice { get; init; } = "";
    public string ResultText { get; init; } = "";
    public bool NeedsReview { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// «Скопировать расценки» — цены работ/материалов из другого КП в текущее
/// (rate_transfer_dialog.py): источник, совпадения, ручной выбор конфликтов,
/// предпросмотр с защитой цен актов/этапов и подтверждением нулевых цен.
/// </summary>
public partial class RateTransferDialog : Window
{
    private readonly int _targetId;
    private readonly string _connectionString;
    private readonly string _expectedRevision;
    private readonly Func<bool> _canApply;
    private readonly Func<Func<SqlServerRateTransfer.Receipt>, Task<SqlServerRateTransfer.Receipt>> _execute;
    private bool _applying;
    public bool WriteAttempted { get; private set; }
    private readonly List<(int Id, int ObjectId, string Title)> _docs = new();
    private TransferData? _data;
    private List<TransferRow> _rows = new();
    private List<TransferRow> _shown = new();
    private readonly Dictionary<int, int> _manual = new();
    private readonly HashSet<int> _excluded = new();
    private readonly HashSet<int> _allowZero = new();
    private int[] _fields = RateFields.Work;

    /// <summary>Итог переноса: перенесённых цен, позиций, СМР работ после переноса.</summary>
    public SqlServerRateTransfer.Receipt? ResultData { get; private set; }

    public RateTransferDialog(string connectionString, int targetId, string targetTitle,
        string expectedRevision, Func<bool> canApply,
        Func<Func<SqlServerRateTransfer.Receipt>, Task<SqlServerRateTransfer.Receipt>> execute)
    {
        _connectionString = connectionString;
        _targetId = targetId;
        _expectedRevision = expectedRevision;
        _canApply = canApply;
        _execute = execute;
        InitializeComponent();
        Closing += (_, args) => { if (_applying) args.Cancel = true; };
        TargetText.Text = "В: " + targetTitle;
        LoadDocs();
        FillSources();
    }

    private void LoadDocs()
    {
        try
        {
            var targetObject = 0;
            using (var conn = SqlServerDatabase.Open(_connectionString))
            {
                using (var read = new SqlCommand(
                    "SELECT object_id FROM dbo.contracts WHERE id=@id", conn))
                {
                    read.Parameters.AddWithValue("@id", _targetId);
                    targetObject = Convert.ToInt32(read.ExecuteScalar() ?? 0);
                }
                using var read2 = conn.CreateCommand();
                read2.CommandText =
                    "SELECT c.id, c.object_id, o.name, c.number, c.[date], c.name FROM dbo.contracts c " +
                    "JOIN dbo.objects o ON o.id=c.object_id WHERE c.kind=N'kp' AND c.id != @id " +
                    "ORDER BY o.name, c.[date] DESC, c.id DESC";
                read2.Parameters.AddWithValue("@id", _targetId);
                using var r = read2.ExecuteReader();
                while (r.Read())
                    _docs.Add((Convert.ToInt32(r.GetValue(0)),
                        Convert.ToInt32(r.GetValue(1)),
                        $"{r.GetValue(2)} · {Documents.ContractTitleText("kp", ToString2(r, 3), ToString2(r, 4), ToString2(r, 5))}"));
            }
            _targetObject = targetObject;
        }
        catch (Exception ex)
        {
            InfoText.Text = "Не удалось прочитать список КП: " + ex.Message;
        }
    }

    private static string ToString2(SqlDataReader r, int i) =>
        r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i)) ?? "";

    private int _targetObject;

    private void FillSources()
    {
        var keep = (SourceCombo.SelectedItem as ComboItem)?.Id;
        var words = SourceSearch.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        SourceCombo.Items.Clear();
        SourceCombo.Items.Add(new ComboItem(null, "Выберите КП-источник"));
        foreach (var (id, objId, title) in _docs)
        {
            if ((!AllObjects.IsChecked == true || objId == _targetObject) && MatchesWords(words, title))
                SourceCombo.Items.Add(new ComboItem(id, title));
        }
        var index = SourceCombo.Items.OfType<ComboItem>().ToList()
            .FindIndex(x => x.Id is not null && x.Id == keep);
        SourceCombo.SelectedIndex = index > 0 ? index : 0;
        Invalidate();
    }

    private static bool MatchesWords(string[] words, string title)
    {
        var lower = title.ToLowerInvariant();
        return words.All(w => lower.Contains(w, StringComparison.Ordinal));
    }

    private void Invalidate()
    {
        _data = null;
        _revision = "";
        _manual.Clear();
        _excluded.Clear();
        _allowZero.Clear();
        _fields = (SmrCheck.IsChecked == true ? RateFields.Work : Array.Empty<int>())
            .Concat(MaterialsCheck.IsChecked == true ? RateFields.Material : Array.Empty<int>())
            .ToArray();
        CheckButton.IsEnabled = (SourceCombo.SelectedItem as ComboItem)?.Id is not null && _fields.Length > 0;
        RefreshRows();
    }

    private void Source_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        FillSources();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => FillSources();

    private void Invalidate_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Invalidate();
    }

    private void Refresh_Changed(object sender, RoutedEventArgs e) => RefreshRows();

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        Invalidate();
        if (!CheckButton.IsEnabled) return;
        var sourceId = (SourceCombo.SelectedItem as ComboItem)?.Id;
        if (sourceId is null) return;
        try
        {
            using var conn = SqlServerDatabase.Open(_connectionString);
            var revision = SqlServerDatabase.Revision(conn);
            if (string.IsNullOrWhiteSpace(revision) || revision != _expectedRevision || !_canApply())
                throw new InvalidOperationException("Документ изменился. Закройте диалог и обновите смету.");
            var data = SqlServerRateTransfer.ReadTransfer(_connectionString, sourceId.Value, _targetId,
                MaterialsCheck.IsChecked == true);
            if (string.IsNullOrWhiteSpace(revision) || revision != SqlServerDatabase.Revision(conn))
                throw new InvalidOperationException("Данные изменились при проверке. Повторите предпросмотр.");
            _data = data;
            _revision = revision;
        }
        catch (InvalidOperationException error)
        {
            InfoText.Text = error.Message;
            return;
        }
        catch (Exception)
        {
            InfoText.Text = "Не удалось прочитать КП. Повторите проверку.";
            return;
        }
        RefreshRows();
    }

    private void RefreshRows()
    {
        var allRows = _data is null
            ? new List<TransferRow>()
            : RateTransfer.MakePreview(_data, _fields, OverwriteCheck.IsChecked == true,
                _manual, _excluded, _allowZero);
        _rows = allRows;
        var shown = OnlyReviewCheck.IsChecked == true
            ? allRows.Where(r => RateTransfer.NeedsReview(r.Status) || r.ZeroReview).ToList()
            : allRows;
        _shown = shown;
        var items = new ObservableCollection<RatePreviewRow>();
        foreach (var row in shown)
        {
            var sourceId = row.Source.Id;
            var item = new RatePreviewRow
            {
                Included = !_excluded.Contains(sourceId),
                SourceLabel = row.Source.SystemLabel + "\n" + row.Source.Name,
                TargetLabel = row.Target is null ? "—" : row.Target.SystemLabel + "\n" + row.Target.Name,
                CurrentPrice = row.Target is null ? "—" : Prices(row.Target, _fields),
                SourcePrice = Prices(row.Source, _fields),
                ResultText = row.Status,
                NeedsReview = RateTransfer.NeedsReview(row.Status) || row.ZeroReview,
            };
            item.IncludedChanged += (_, included) =>
            {
                if (included) _excluded.Remove(sourceId);
                else _excluded.Add(sourceId);
                RefreshRows();
            };
            items.Add(item);
        }
        PreviewGrid.ItemsSource = items;
        var count = _rows.Sum(r => r.Changes.Length);
        ApplyButton.IsEnabled = count > 0;
        if (_data is null)
            InfoText.Text = "Выберите источник и проверьте совпадения.";
        else
        {
            var all = _rows;
            InfoText.Text =
                $"Точных совпадений: {all.Count(r => r.Exact)} · К переносу цен: {count} · " +
                $"Уже с ценами: {all.Count(r => r.Status == "Уже есть расценки")} · " +
                $"Не найдено: {all.Count(r => r.Status == "Не найдено")} · " +
                $"На проверку: {all.Count(r => RateTransfer.NeedsReview(r.Status) || r.ZeroReview)}";
        }
        ChooseButton.IsEnabled = false;
        ZeroButton.IsEnabled = false;
    }

    private static string Prices(RateRow row, int[] fields) =>
        string.Join("\n", fields.Select(f =>
            $"{RateFields.Short[f]}: {(row.Rate(f) is { } v ? Money.Format(v) : "—")}"));

    private sealed record ComboItem(int? Id, string Title)
    {
        public override string ToString() => Title;
    }

    private void Choose_Click(object sender, RoutedEventArgs e) => ChoosePosition();

    private void Zero_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow();
        if (row is { } selected && selected.ZeroReview)
        {
            _allowZero.Add(selected.Source.Id);
            RefreshRows();
        }
    }

    private TransferRow? SelectedRow()
    {
        var index = PreviewGrid.SelectedIndex;
        return index >= 0 && index < _shown.Count ? _shown[index] : null;
    }

    /// <summary>Ручной выбор: среди совместимых строк сметы — по поиску, с сохранением пары.</summary>
    private void ChoosePosition()
    {
        var row = SelectedRow();
        if (row is null || _excluded.Contains(row.Source.Id) || _data is null) return;
        var candidates = _data.Target.Rows
            .Where(r => RateTransfer.Compatible(row.Source, r))
            .OrderBy(r => r.Key != row.Source.Key)
            .ThenBy(r => string.Join('›', r.System) != string.Join('›', row.Source.System))
            .ThenBy(r => string.Join('›', r.System))
            .ThenBy(r => r.Name)
            .ToList();
        var dialog = new Window
        {
            Title = "Выбрать позицию в смете",
            Width = 680, Height = 165,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Из: {row.Source.SystemLabel} · {row.Source.Name}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        var choice = new ComboBox();
        var ok = new Button { Content = "Выбрать", IsEnabled = false, Padding = new Thickness(12, 4, 12, 4) };
        panel.Children.Add(search);
        panel.Children.Add(choice);
        panel.Children.Add(ok);
        dialog.Content = panel;

        void Fill()
        {
            var words = search.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            choice.Items.Clear();
            choice.Items.Add(new ComboItem(null, "Выберите строку"));
            foreach (var item in candidates)
            {
                var number = item.Num.Length > 0 ? $"№ {item.Num} · " : "";
                var title = $"{item.SystemLabel} · {number}{item.Name} · {item.Qty:0.###} {item.Unit}";
                if (item.Basis.Length > 0) title += $" · {item.Basis}";
                if (MatchesWords(words, title))
                    choice.Items.Add(new ComboItem(item.Id, title));
            }
            ok.IsEnabled = (choice.SelectedItem as ComboItem)?.Id is not null;
        }
        void Apply(object? s, SelectionChangedEventArgs args) => ok.IsEnabled = (choice.SelectedItem as ComboItem)?.Id is not null;
        search.TextChanged += (_, _) => Fill();
        choice.SelectionChanged += Apply;
        ok.Click += (_, _) =>
        {
            if ((choice.SelectedItem as ComboItem)?.Id is int id)
            {
                _manual[row.Source.Id] = id;
                dialog.DialogResult = true;
            }
        };
        Fill();
        if (dialog.ShowDialog() == true) RefreshRows();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_applying || _data is not { } data || string.IsNullOrWhiteSpace(_revision)) return;
        var revision = _revision;
        var replace = OverwriteCheck.IsChecked == true || _allowZero.Count > 0;
        if (replace && MessageBox.Show(this,
                "Заполненные цены, отмеченные в предпросмотре, будут заменены. Продолжить?",
                "Подтвердить замену цен", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!ReferenceEquals(_data, data) || _revision != revision) return;
        if (revision != _expectedRevision || !_canApply())
        {
            Invalidate();
            InfoText.Text = "Документ изменился. Закройте диалог и обновите смету.";
            return;
        }
        // Freeze the exact reviewed inputs before leaving the dispatcher.
        var fields = _fields.ToArray();
        var overwrite = OverwriteCheck.IsChecked == true;
        var manual = new Dictionary<int, int>(_manual);
        var excluded = new HashSet<int>(_excluded);
        var allowZero = new HashSet<int>(_allowZero);
        _applying = true;
        IsEnabled = false;
        Exception? failure = null;
        try
        {
            WriteAttempted = true;
            ResultData = await _execute(() => SqlServerRateTransfer.ApplyTransferWithReceipt(
                _connectionString, data, fields, overwrite, manual, excluded, allowZero, revision));
        }
        catch (Exception error) { failure = error; }
        finally
        {
            _applying = false;
            IsEnabled = true;
        }
        if (failure is not null)
        {
            MessageBox.Show(this, "Перенос не подтверждён: " + failure.Message +
                "\nПроверьте смету перед повтором.", "Перенос расценок",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
            return;
        }
        DialogResult = true;
    }

    private string _revision = "";

 }
