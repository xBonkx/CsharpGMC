using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Compact editor for Python's "Докупили (вне КП)" positions.</summary>
public sealed class ProcurementExtraDialog : Window
{
    private readonly TextBox _name;
    private readonly TextBox _unit;
    private readonly TextBox _quantity;
    private readonly TextBox _cost;

    public string PositionName => _name.Text.Trim();
    public string Unit => _unit.Text.Trim();
    public decimal Quantity { get; private set; }
    public decimal? PurchaseCost { get; private set; }

    public ProcurementExtraDialog(ProcurementPreviewRow? item = null)
    {
        Title = item is null ? "Добавить докупленное (вне КП)" :
            "Редактировать дополнительную закупку";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        MinWidth = 340;
        ResizeMode = ResizeMode.CanResize;
        Background = System.Windows.Media.Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ProcurementDialogChrome.Apply(this);
        var pane = new StackPanel();
        var title = ProcurementDialogChrome.Heading(Title);
        title.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormGroupSeparatorGap");
        pane.Children.Add(title);

        static TextBox Field(StackPanel container, string title, string value)
        {
            container.Children.Add(DynamicFormLayout.Secondary(title));
            var box = new TextBox { Text = value };
            DynamicFormLayout.Field(box);
            box.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormStackedFieldGap");
            System.Windows.Automation.AutomationProperties.SetName(box, title);
            container.Children.Add(box);
            return box;
        }
        _name = Field(pane, "Наименование", item?.BuyName ?? "Новая позиция (докупили)");
        _name.TextWrapping = TextWrapping.Wrap;
        _name.MinHeight = 54;
        _name.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var unitWrap = new StackPanel();
        unitWrap.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormColumnGap");
        var qtyWrap = new StackPanel();
        Grid.SetColumn(qtyWrap, 1);
        _unit = Field(unitWrap, "Ед.", item?.Unit ?? "шт");
        _quantity = Field(qtyWrap, "Количество", item?.Qty ?? "1");
        _quantity.TextAlignment = TextAlignment.Right;
        row.Children.Add(unitWrap);
        row.Children.Add(qtyWrap);
        pane.Children.Add(row);
        _cost = Field(pane, "Фактическая цена закупки (можно пусто)", item?.CostPrice ?? "");
        _cost.TextAlignment = TextAlignment.Right;
        if (item is not null)
        {
            _cost.IsEnabled = false;
            _cost.ToolTip = "Закупочная цена редактируется непосредственно в таблице.";
        }
        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right
        };
        var save = new Button { Content = "Сохранить", MinWidth = 110 };
        save.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogActionGap");
        save.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Отмена", MinWidth = 85 };
        ProcurementDialogChrome.Buttons(save, cancel);
        actions.Children.Add(save);
        actions.Children.Add(cancel);
        Content = DynamicFormLayout.WithFooter(pane, actions);
    }

    private void Accept()
    {
        static bool Parse(string text, out decimal value) =>
            GmcSmeta.Core.Domain.ProcurementDisplay.TryParseNumber(text, out value);
        if (!Parse(_quantity.Text, out var qty))
        {
            MessageBox.Show(this, "Некорректное количество.", "Докупленное");
            return;
        }
        decimal? cost = null;
        if (!string.IsNullOrWhiteSpace(_cost.Text))
        {
            if (!Parse(_cost.Text, out var parsed))
            {
                MessageBox.Show(this, "Некорректная закупочная цена.", "Докупленное");
                return;
            }
            cost = parsed;
        }
        var error = SqlServerProcurement.ValidateExtra(PositionName, Unit, qty, cost);
        if (error is not null)
        {
            MessageBox.Show(this, error, "Докупленное", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Quantity = qty;
        PurchaseCost = cost;
        DialogResult = true;
    }
}
