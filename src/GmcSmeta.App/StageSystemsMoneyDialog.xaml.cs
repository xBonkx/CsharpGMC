using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class StageSystemsMoneyDialog : Window
{
    public sealed class SystemRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string AmountText { get; set; } = "";
    }

    private readonly ObservableCollection<SystemRow> _rows = new();
    public IReadOnlyDictionary<long, decimal> Amounts { get; private set; } =
        new Dictionary<long, decimal>();

    public StageSystemsMoneyDialog(string title,
        IReadOnlyList<SqlServerStages.StageSystemMoney> systems)
    {
        InitializeComponent();
        CaptionText.Text = $"СМР по системам — {title}";
        foreach (var system in systems)
            _rows.Add(new SystemRow
            {
                Id = system.Id, Name = system.Name,
                AmountText = system.Amount.ToString("0.##", CultureInfo.CurrentCulture)
            });
        SystemsGrid.ItemsSource = _rows;
        SummaryText.Text = $"Систем: {_rows.Count}. После сохранения сумма систем станет СМР этапа.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SystemsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        SystemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var values = new Dictionary<long, decimal>();
        foreach (var row in _rows)
        {
            if (!StageAllocationPresentation.TryParseNonnegative(row.AmountText, out var sum))
            {
                SummaryText.Text = $"Проверьте сумму в системе «{row.Name}». Ничего не сохранено.";
                return;
            }
            values[row.Id] = Money.Round2(sum);
        }
        if (values.Count == 0)
        {
            SummaryText.Text = "В этапе нет систем с позициями.";
            return;
        }
        Amounts = values;
        DialogResult = true;
    }
}
