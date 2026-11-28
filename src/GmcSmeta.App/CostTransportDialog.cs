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
        SizeToContent = SizeToContent.Height;
        MinWidth = 360;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DynamicFormLayout.Apply(this);
        var root = new StackPanel();
        var intro = WorkspaceWindowUi.Heading("Транспорт учитывается в итогах документа только один раз");
        intro.TextWrapping = TextWrapping.Wrap;
        intro.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogIntroGap");
        root.Children.Add(intro);
        void AddField(string caption, TextBox input)
        {
            var row = new Grid();
            row.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormFieldMargin");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            var label = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormColumnGap");
            DynamicFormLayout.Field(input);
            System.Windows.Automation.AutomationProperties.SetName(input, caption);
            Grid.SetColumn(input, 1);
            row.Children.Add(label);
            row.Children.Add(input);
            root.Children.Add(row);
        }
        _customer = new TextBox {
            Text = Number(current.Customer), IsReadOnly = locked,
            HorizontalContentAlignment = HorizontalAlignment.Right
        };
        AddField("Клиенту (в смете), ₽:", _customer);
        _cost = new TextBox {
            Text = current.Cost is null ? "" : Number(current.Cost.Value),
            HorizontalContentAlignment = HorizontalAlignment.Right
        };
        AddField("Нам (закуп), ₽:", _cost);
        root.Children.Add(DynamicFormLayout.Secondary(locked
            ? "Смета подписана: сумму клиенту менять нельзя, наш расход разрешено уточнить."
            : "Сумма «Клиенту» совпадает с транспортом в итогах договора.", "LayoutDialogIntroGap"));
        var supplierHeading = WorkspaceWindowUi.Heading("Посчитать от закупа у поставщика");
        supplierHeading.TextWrapping = TextWrapping.Wrap;
        supplierHeading.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogIntroGap");
        root.Children.Add(supplierHeading);
        if (suppliers.Count == 0)
            root.Children.Add(DynamicFormLayout.Secondary("В смете нет строк с закупом."));

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
            };
            check.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogListItemGap");
            DynamicFormLayout.WrapCaption(check);
            check.Checked += (_, _) => Recalculate();
            check.Unchecked += (_, _) => Recalculate();
            _suppliers.Add((supplier, check));
            root.Children.Add(check);
        }
        _percent = new TextBox {
            Text = oldPct == 0m ? "" : Number(oldPct),
            HorizontalContentAlignment = HorizontalAlignment.Right
        };
        AddField("× процент:", _percent);
        _percent.TextChanged += (_, _) => Recalculate();
        _calculation = DynamicFormLayout.Secondary("", "LayoutDialogIntroGap");
        root.Children.Add(_calculation);
        var put = new Button {
            Content = "Вписать расчёт в «Нам»", HorizontalAlignment = HorizontalAlignment.Left
        };
        WorkspaceWindowUi.StyleButton(put);
        put.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogIntroGap");
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
        var buttons = new WrapPanel {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        cancel.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogActionGap");
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button { Content = "Сохранить" };
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
        Loaded += (_, _) =>
        {
            var initial = locked ? _cost : _customer;
            initial.Focus();
            initial.SelectAll();
        };
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
