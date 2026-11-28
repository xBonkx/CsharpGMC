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
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
        root.Children.Add(new TextBlock
        {
            Text = $"Наценка для выделенных строк: {count}",
            FontWeight = FontWeights.SemiBold, FontSize = 17, Margin = new Thickness(0, 0, 0, 12),
        });
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
        root.Children.Add(_toPrice);
        root.Children.Add(_toCost);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _factor = new TextBox { Text = "1,44", Width = 110, HorizontalContentAlignment = HorizontalAlignment.Right };
        _percent = new TextBox { Text = "44", Width = 110, HorizontalContentAlignment = HorizontalAlignment.Right };
        _factor.TextChanged += FactorChanged;
        _percent.TextChanged += PercentChanged;
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock { Text = "Коэффициент   ", VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(_factor);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 0) };
        right.Children.Add(new TextBlock { Text = "или наценка, %   ", VerticalAlignment = VerticalAlignment.Center });
        right.Children.Add(_percent);
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        root.Children.Add(grid);
        _onlyEmpty = new CheckBox
        {
            Content = "Только там, где цена (или закуп) ещё не указана", Margin = new Thickness(0, 0, 0, 6),
        };
        _round = new CheckBox { Content = "Округлить цену до целых рублей", IsChecked = true };
        root.Children.Add(_onlyEmpty);
        root.Children.Add(_round);
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0),
        };
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(12, 6, 12, 6) };
        cancel.Click += (_, _) => DialogResult = false;
        var apply = new Button
        {
            Content = "Применить", Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(8, 0, 0, 0), IsDefault = true,
        };
        apply.Click += (_, _) => Apply();
        row.Children.Add(cancel);
        row.Children.Add(apply);
        root.Children.Add(row);
        Content = root;
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
