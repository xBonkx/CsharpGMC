using System.Windows;
using System.Windows.Controls;

namespace GmcSmeta.App;

/// <summary>OrderDialog equivalent: supplier, source, stage, references and payment date.</summary>
public sealed class ProcurementOrderDialog : Window
{
    private sealed record Choice(string Label, string? Code)
    {
        public override string ToString() => Label;
    }
    private readonly bool _single;
    private readonly ComboBox _source;
    private readonly ComboBox _supplier;
    private readonly ComboBox _stage;
    private readonly TextBox _order;
    private readonly TextBox _invoice;
    private readonly CheckBox _setPayment;
    private readonly CheckBox _clearPayment;
    private readonly DatePicker _payment;

    public string? Source => (_source.SelectedItem as Choice)?.Code;
    public string? Supplier => _single || !string.IsNullOrWhiteSpace(_supplier.Text) ?
        _supplier.Text.Trim() : null;
    public string? Stage => (_stage.SelectedItem as Choice)?.Code;
    public string? OrderRef => _single || !string.IsNullOrWhiteSpace(_order.Text) ? _order.Text.Trim() : null;
    public string? InvoiceRef => _single || !string.IsNullOrWhiteSpace(_invoice.Text) ? _invoice.Text.Trim() : null;
    public DateTime? PaidDate => _setPayment.IsChecked == true ? _payment.SelectedDate : null;
    public bool ClearPaidDate => _clearPayment.IsChecked == true;

    public ProcurementOrderDialog(IReadOnlyList<ProcurementPreviewRow> rows,
        IEnumerable<string> supplierNames)
    {
        if (rows.Count == 0) throw new ArgumentException("Нет строк для заказа.");
        _single = rows.Count == 1;
        var current = _single ? rows[0] : null;
        Title = "Заказ / оплата";
        Width = 620;
        Height = 485;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ProcurementDialogChrome.Apply(this);
        Background = System.Windows.Media.Brushes.White;
        var root = new StackPanel();
        var title = ProcurementDialogChrome.Heading($"Заказ / оплата — позиций: {rows.Count}");
        title.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormGroupHeadingGap");
        root.Children.Add(title);
        root.Children.Add(DynamicFormLayout.Secondary(
            "При массовом изменении пустые поля не изменяются. Цена закупки редактируется в таблице.", "LayoutDialogIntroGap"));

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 145 });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        for (var i = 0; i < 6; i++)
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        static void Place(Grid panel, string title, FrameworkElement editor, int index)
        {
            var label = DynamicFormLayout.Secondary(title, "LayoutFormLabelMargin");
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(label, index);
            panel.Children.Add(label);
            if (editor is Control input) DynamicFormLayout.Field(input);
            editor.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormFieldMargin");
            System.Windows.Automation.AutomationProperties.SetName(editor, title);
            editor.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(editor, index);
            Grid.SetColumn(editor, 1);
            panel.Children.Add(editor);
        }
        _source = new ComboBox();
        if (!_single) _source.Items.Add(new Choice("— не менять —", null));
        _source.Items.Add(new Choice("Закупка у поставщика", "buy"));
        _source.Items.Add(new Choice("Изготовление в нашем цеху", "shop"));
        _source.SelectedItem = _source.Items.OfType<Choice>()
            .FirstOrDefault(x => x.Code == current?.Source) ?? _source.Items[0];
        Place(form, "Источник", _source, 0);

        _supplier = new ComboBox { IsEditable = true, IsTextSearchEnabled = true };
        foreach (var name in supplierNames.Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.CurrentCultureIgnoreCase))
            _supplier.Items.Add(name);
        _supplier.Text = current?.Supplier ?? "";
        Place(form, "Поставщик", _supplier, 1);

        _stage = new ComboBox();
        void FillStage()
        {
            var previous = (_stage.SelectedItem as Choice)?.Code ?? current?.Stage;
            _stage.Items.Clear();
            if (!_single) _stage.Items.Add(new Choice("— не менять —", null));
            _stage.Items.Add(new Choice("—", ""));
            if (Source == "shop")
            {
                _stage.Items.Add(new Choice("Заявка в цех", "shop_queued"));
                _stage.Items.Add(new Choice("В изготовлении", "shop_work"));
                _stage.Items.Add(new Choice("Не делаем", "skip"));
            }
            else
            {
                foreach (var s in new[]
                {
                    new Choice("Заказано", "ordered"),
                    new Choice("Счёт", "invoice"),
                    new Choice("Оплачено", "paid"),
                    new Choice("По факту / монтажники", "fact"),
                    new Choice("Не заказываем", "skip")
                })
                    _stage.Items.Add(s);
            }
            _stage.SelectedItem = _stage.Items.OfType<Choice>()
                .FirstOrDefault(x => x.Code == previous) ?? _stage.Items[0];
        }
        Place(form, "Статус", _stage, 2);
        _source.SelectionChanged += (_, _) =>
        {
            FillStage();
            if (Source == "shop" && string.IsNullOrWhiteSpace(_supplier.Text))
                _supplier.Text = "Цех";
        };
        FillStage();
        _order = new TextBox { Text = current?.OrderRef ?? "", ToolTip = "заявка 146 / заказ № 191 / ЗВК 219" };
        Place(form, "Заявка / заказ", _order, 3);
        _invoice = new TextBox { Text = current?.InvoiceRef ?? "", ToolTip = "№ счёта" };
        Place(form, "Счёт", _invoice, 4);
        var paymentRow = new WrapPanel { Orientation = Orientation.Horizontal };
        _setPayment = new CheckBox
        {
            Content = "Указать дату", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };
        _payment = new DatePicker
        {
            SelectedDate = current?.PaidDate ?? DateTime.Today, Width = 150, IsEnabled = false
        };
        _clearPayment = new CheckBox
        {
            Content = "Очистить дату", IsEnabled = _single,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(13, 0, 0, 0)
        };
        _setPayment.Checked += (_, _) =>
        {
            _payment.IsEnabled = true;
            _clearPayment.IsChecked = false;
        };
        _setPayment.Unchecked += (_, _) => _payment.IsEnabled = false;
        _clearPayment.Checked += (_, _) =>
        {
            _setPayment.IsChecked = false;
            _payment.IsEnabled = false;
        };
        paymentRow.Children.Add(_setPayment);
        paymentRow.Children.Add(_payment);
        paymentRow.Children.Add(_clearPayment);
        Place(form, "Оплата", paymentRow, 5);
        root.Children.Add(form);
        root.Children.Add(new TextBlock
        {
            Text = "Позиции: " + string.Join("; ", rows.Take(6)
                       .Select(r => r.BuyName.Length > 40 ? r.BuyName[..40] + "…" : r.BuyName)) +
                   (rows.Count > 6 ? " …" : ""),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 14)
        });

        var buttons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var save = new Button { Content = "Сохранить", MinWidth = 110 };
        save.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogActionGap");
        save.Click += (_, _) =>
        {
            if (_setPayment.IsChecked == true && _payment.SelectedDate is null)
            {
                MessageBox.Show(this, "Укажите дату оплаты.", "Заказ / оплата");
                return;
            }
            DialogResult = true;
        };
        var cancel = new Button { Content = "Отмена", MinWidth = 90 };
        ProcurementDialogChrome.Buttons(save, cancel);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        Content = DynamicFormLayout.WithFooter(root, buttons);
    }
}
