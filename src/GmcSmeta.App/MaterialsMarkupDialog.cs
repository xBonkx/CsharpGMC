using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GmcSmeta.Core;

namespace GmcSmeta.App;

/// <summary>Диалог наценки (cost_view.MarkupDialog): направление, коэффициент/%, только пустые, округление.</summary>
public sealed class MaterialsMarkupDialog : Window
{
    private readonly RadioButton _toPrice;
    private readonly RadioButton _toCost;
    private readonly TextBox _factor;
    private readonly TextBox _percent;
    private readonly CheckBox _onlyEmpty;
    private readonly CheckBox _round;
    private bool _syncing;

    public decimal Factor { get; private set; } = 1.44m;
    public bool PriceFromCost { get; private set; } = true;
    public bool OnlyEmpty { get; private set; }
    public bool WholeRubles { get; private set; } = true;

    public MaterialsMarkupDialog(int count)
    {
        Title = "Наценка";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brushes.White;
        DynamicFormLayout.Apply(this);
        var root = new StackPanel();
        var heading = WorkspaceWindowUi.Heading($"Наценка для выделенных строк: {count}");
        heading.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogIntroGap");
        root.Children.Add(heading);
        _toPrice = new RadioButton
        {
            Content = "Посчитать цену клиенту из закупа:  цена = закуп × коэффициент",
            IsChecked = true, Margin = new Thickness(0, 0, 0, 6),
        };
        _toCost = new RadioButton
        {
            Content = "Посчитать закуп из цены клиенту:  закуп = цена ÷ коэффициент",
            Margin = new Thickness(0, 0, 0, 10),
        };
        DynamicFormLayout.WrapCaption(_toPrice);
        DynamicFormLayout.WrapCaption(_toCost);
        root.Children.Add(_toPrice);
        root.Children.Add(_toCost);
        // Python MarkupDialog uses two QFormLayout rows.
        _factor = new TextBox { Text = "1,44", HorizontalContentAlignment = HorizontalAlignment.Right,
            ToolTip = "Например: 1,44 = закуп × 1,2 × 1,2; 2 = закуп × 2" };
        _percent = new TextBox { Text = "44", HorizontalContentAlignment = HorizontalAlignment.Right };
        _factor.TextChanged += FactorChanged;
        _percent.TextChanged += PercentChanged;
        void AddField(string caption, TextBox input)
        {
            var field = new Grid();
            field.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormFieldMargin");
            field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            var label = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormColumnGap");
            field.Children.Add(label);
            DynamicFormLayout.Field(input);
            System.Windows.Automation.AutomationProperties.SetName(input, caption);
            Grid.SetColumn(input, 1);
            field.Children.Add(input);
            root.Children.Add(field);
        }
        AddField("Коэффициент", _factor);
        AddField("или наценка, %", _percent);
        _onlyEmpty = new CheckBox
        {
            Content = "Только там, где цена (или закуп) ещё не указана", Margin = new Thickness(0, 0, 0, 6),
        };
        _round = new CheckBox { Content = "Округлить цену до целых рублей", IsChecked = true };
        DynamicFormLayout.WrapCaption(_onlyEmpty);
        DynamicFormLayout.WrapCaption(_round);
        root.Children.Add(_onlyEmpty);
        root.Children.Add(_round);
        var row = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        cancel.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogActionGap");
        var apply = new Button { Content = "Применить", IsDefault = true };
        apply.Click += (_, _) => Apply();
        row.Children.Add(cancel);
        row.Children.Add(apply);
        DynamicFormLayout.StyleActions(apply, cancel);
        Content = DynamicFormLayout.WithFooter(root, row);
        Loaded += (_, _) => _factor.Focus();
    }

    private static decimal? Parse(string text) =>
        NumberText.Parse(text);

    private void FactorChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        var v = Parse(_factor.Text);
        if (v is decimal value)
            _percent.Text = ((value - 1m) * 100m).ToString("0.###", CultureInfo.CurrentCulture);
        _syncing = false;
    }

    private void PercentChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        var v = Parse(_percent.Text);
        if (v is decimal value)
            _factor.Text = (1m + value / 100m).ToString("0.###", CultureInfo.CurrentCulture);
        _syncing = false;
    }

    private void Apply()
    {
        var factor = Parse(_factor.Text);
        if (factor is not decimal value || value <= 0.01m || value > 100m)
        {
            MessageBox.Show(this, "Коэффициент должен быть от 0,01 до 100.", "Наценка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Factor = value;
        PriceFromCost = _toPrice.IsChecked == true;
        OnlyEmpty = _onlyEmpty.IsChecked == true;
        WholeRubles = _round.IsChecked == true;
        DialogResult = true;
    }
}
