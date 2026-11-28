using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GmcSmeta.App;

/// <summary>
/// Выбор года архива крупными кнопками (перенос ArchiveYearDialog): чипы последних восьми лет
/// плюс поле «Другой год»; выбранный год подсвечивается, «Сейчас» — у текущего.
/// </summary>
public partial class ArchiveYearDialog : Window
{
    public int Year { get; private set; }
    private bool _syncing;

    public ArchiveYearDialog(string name, int current, int thisYear)
    {
        InitializeComponent();
        Year = current;
        ObjectName.Text = name;
        var years = Enumerable.Range(thisYear - 7, 8).Append(current).Distinct().OrderDescending();
        foreach (var year in years)
        {
            var chip = new ToggleButton
            {
                Content = year.ToString(CultureInfo.InvariantCulture),
                IsChecked = year == current,
                Style = (Style)Resources["YearChip"],
                ToolTip = year == current ? "Сейчас" : null,
                Tag = year,
                TabIndex = YearGrid.Children.Count,
            };
            chip.Click += (_, _) => Pick(year);
            YearGrid.Children.Add(chip);
        }
        YearBox.Text = current.ToString(CultureInfo.InvariantCulture);
        Loaded += (_, _) => { YearBox.Focus(); YearBox.SelectAll(); };
    }

    private void Pick(int year)
    {
        Year = year;
        _syncing = true;
        YearBox.Text = year.ToString(CultureInfo.InvariantCulture);
        _syncing = false;
        foreach (ToggleButton chip in YearGrid.Children) chip.IsChecked = (int)chip.Tag == year;
    }

    private void YearBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (!int.TryParse(YearBox.Text.Trim(), CultureInfo.InvariantCulture, out var year) || year is < 2000 or > 2100) return;
        Year = year;
        foreach (ToggleButton chip in YearGrid.Children) chip.IsChecked = (int)chip.Tag == year;
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(YearBox.Text.Trim(), CultureInfo.InvariantCulture, out var year) && year is >= 2000 and <= 2100)
        {
            Year = year;
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
