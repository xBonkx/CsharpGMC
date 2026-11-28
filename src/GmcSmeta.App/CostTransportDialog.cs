using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>
/// Python cost_view.edit_transport / _transport_calc: edit both contract amounts
/// and calculate our expense as a percentage of selected supplier purchases.
/// </summary>
public sealed class CostTransportDialog : Window
{
    private readonly TextBox _customer;
    private readonly TextBox _cost;
    private readonly TextBox _percent;
    private readonly TextBlock _calculation;
    private readonly List<(SqlServerCostOperations.SupplierCost Source, CheckBox Check)> _suppliers = new();
    private decimal? _putValue;
    private decimal _putBase;
    private decimal _putPercent;
    private string[] _putSuppliers = Array.Empty<string>();

    public decimal? OurCost { get; private set; }
    public decimal CustomerAmount { get; private set; }
    public string CalculationNote { get; private set; } = "";

    private static string Number(decimal value) => value.ToString("0.######", CultureInfo.CurrentCulture);
    private static decimal? Parse(string text)
    {
        var clean = text.Trim().Replace(" ", "").Replace("\u00a0", "");
        return clean.Length == 0 ? null : decimal.TryParse(
            clean.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture,
            out var value) ? value : null;
    }

    public CostTransportDialog((decimal? Cost, decimal Customer, string Note) current,
        IReadOnlyList<SqlServerCostOperations.SupplierCost> suppliers, bool locked)
    {
        Title = "Транспорт — клиенту и нам";
        Width = 660;
        Height = Math.Min(750, 330 + 37 * suppliers.Count);
        MinWidth = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DynamicFormLayout.Apply(this);
        var root = new StackPanel();
        root.Children.Add(new TextBlock {
            Text = "Транспорт учитывается в итогах документа только один раз",
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });
        var customerRow = new DockPanel { Margin = new Thickness(0, 2, 0, 8) };
        customerRow.Children.Add(new TextBlock {
            Text = "Клиенту (в смете), ₽:", Width = 210, VerticalAlignment = VerticalAlignment.Center
        });
        _customer = new TextBox {
            Text = Number(current.Customer), MinWidth = 150,
            IsReadOnly = locked, HorizontalContentAlignment = HorizontalAlignment.Right
        };
        customerRow.Children.Add(_customer);
        root.Children.Add(customerRow);
        var costRow = new DockPanel { Margin = new Thickness(0, 2, 0, 8) };
        costRow.Children.Add(new TextBlock {
            Text = "Нам (закуп), ₽:", Width = 210, VerticalAlignment = VerticalAlignment.Center
        });
        _cost = new TextBox {
            Text = current.Cost is null ? "" : Number(current.Cost.Value),
            MinWidth = 150, HorizontalContentAlignment = HorizontalAlignment.Right
        };
        costRow.Children.Add(_cost);
        root.Children.Add(costRow);
        root.Children.Add(new TextBlock {
            Text = locked
                ? "Смета подписана: сумму клиенту менять нельзя, наш расход разрешено уточнить."
                : "Сумма «Клиенту» совпадает с транспортом в итогах договора.",
            Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 14)
        });
        root.Children.Add(new TextBlock {
            Text = "ПОСЧИТАТЬ ОТ ЗАКУПА У ПОСТАВЩИКА",
            FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
        });
        if (suppliers.Count == 0)
            root.Children.Add(new TextBlock { Text = "В смете нет строк с закупом." });

        HashSet<string> oldNames = new(StringComparer.Ordinal);
        decimal oldPct = 0m;
        try
        {
            using var json = JsonDocument.Parse(current.Note);
            if (json.RootElement.TryGetProperty("sup", out var array) &&
                array.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in array.EnumerateArray())
                    if (name.ValueKind == JsonValueKind.String && name.GetString() is { } text)
                        oldNames.Add(text);
            }
            if (json.RootElement.TryGetProperty("pct", out var pct) &&
                pct.ValueKind == JsonValueKind.Number && pct.TryGetDecimal(out var p))
                oldPct = p;
        }
        catch (JsonException) { /* manual calculation, no remembered inputs */ }

        foreach (var supplier in suppliers)
        {
            var check = new CheckBox {
                Content = supplier.Name + " — " + supplier.Total.ToString("N2", CultureInfo.CurrentCulture) +
                    " ₽ (" + supplier.Rows + " поз." +
                    (supplier.Missing > 0 ? ", без закупа " + supplier.Missing : "") + ")",
                IsChecked = oldNames.Contains(supplier.Name),
                Margin = new Thickness(0, 2, 0, 4),
            };
            DynamicFormLayout.WrapCaption(check);
            check.Checked += (_, _) => Recalculate();
            check.Unchecked += (_, _) => Recalculate();
            _suppliers.Add((supplier, check));
            root.Children.Add(check);
        }
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 5) };
        bottom.Children.Add(new TextBlock {
            Text = "× процент:", Width = 100, VerticalAlignment = VerticalAlignment.Center
        });
        _percent = new TextBox {
            Text = oldPct == 0m ? "" : Number(oldPct), Width = 85,
            HorizontalContentAlignment = HorizontalAlignment.Right
        };
        bottom.Children.Add(_percent);
        _percent.TextChanged += (_, _) => Recalculate();
        _calculation = new TextBlock {
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0),
            TextWrapping = TextWrapping.Wrap
        };
        bottom.Children.Add(_calculation);
        root.Children.Add(bottom);
        var put = new Button {
            Content = "Вписать расчёт в «Нам»", Margin = new Thickness(0, 6, 0, 12),
            Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left
        };
        put.Click += (_, _) =>
        {
            var checkedSuppliers = _suppliers.Where(x => x.Check.IsChecked == true).ToArray();
            var pct = Parse(_percent.Text);
            if (pct is null || pct < 0m || pct > 10000m || checkedSuppliers.Length == 0)
            {
                MessageBox.Show(this, "Выберите поставщиков и впишите корректный процент.",
                    "Транспорт", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _putSuppliers = checkedSuppliers.Select(x => x.Source.Name).ToArray();
            _putBase = decimal.Round(checkedSuppliers.Sum(x => x.Source.Total), 2);
            _putPercent = pct.Value;
            _putValue = SqlServerCostOperations.TransportFromSuppliers(
                checkedSuppliers.Select(x => x.Source.Total), pct.Value);
            _cost.Text = Number(_putValue.Value);
        };
        root.Children.Add(put);
        var buttons = new StackPanel {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button { Content = "Отмена", MinWidth = 100,
            Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button { Content = "Сохранить", MinWidth = 120,
            Padding = new Thickness(12, 6, 12, 6), FontWeight = FontWeights.SemiBold };
        save.Click += (_, _) =>
        {
            var sell = Parse(_customer.Text);
            var cost = Parse(_cost.Text);
            if ((_customer.Text.Trim().Length != 0 && sell is null) ||
                (_cost.Text.Trim().Length != 0 && cost is null) ||
                cost < 0m || sell < 0m)
            {
                MessageBox.Show(this, "Проверьте суммы транспорта.", "Транспорт",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            CustomerAmount = sell ?? 0m;
            OurCost = cost;
            if (cost is not null && _putValue == cost && _putSuppliers.Length > 0)
                CalculationNote = JsonSerializer.Serialize(new {
                    sup = _putSuppliers, pct = _putPercent, @base = _putBase, sum = _putValue
                });
            DialogResult = true;
        };
        DynamicFormLayout.StyleActions(save, cancel);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Content = DynamicFormLayout.WithFooter(root, buttons, "LayoutCompareDialogMargin");
        Recalculate();
    }

    private void Recalculate()
    {
        if (_calculation is null || _percent is null) return;
        var selected = _suppliers.Where(x => x.Check.IsChecked == true).ToArray();
        var pct = Parse(_percent.Text);
        var sum = selected.Sum(x => x.Source.Total);
        var missing = selected.Sum(x => x.Source.Missing);
        _calculation.Text = selected.Length == 0 ? "Отметьте поставщика" :
            "Выбрано " + sum.ToString("N2", CultureInfo.CurrentCulture) +
            (pct is >= 0m and <= 10000m ?
                " → " + SqlServerCostOperations.TransportFromSuppliers(
                    selected.Select(x => x.Source.Total), pct.Value).ToString("N2",
                    CultureInfo.CurrentCulture) + " ₽" : " (укажите процент)") +
            (missing != 0 ? " · без закупа " + missing : "");
    }
}
