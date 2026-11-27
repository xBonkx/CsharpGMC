using System.ComponentModel;
using System.IO;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Python common.view_button / enable_wrap: one local preference for estimate,
/// materials and procurement. Only name display changes, never document data.
/// </summary>
public sealed class NameWrapPreference : INotifyPropertyChanged
{
    private static string FilePath => Path.Combine(
        Path.GetDirectoryName(EstimateUiSettings.SettingsFilePath)!, "wrap-names.txt");

    public static NameWrapPreference Current { get; } = new();

    private NameWrapPreference() => IsEnabled = Load();

    public bool IsEnabled { get; private set; }
    // NaN is WPF's automatic row height. The compact mode must constrain
    // every row, including explicit newlines in names or other columns.
    public double RowHeight => IsEnabled ? double.NaN : 27d;
    public TextWrapping EditorWrapping => IsEnabled ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public event PropertyChangedEventHandler? PropertyChanged;

    private static bool Load()
    {
        try
        {
            if (File.Exists(FilePath) && bool.TryParse(File.ReadAllText(FilePath), out var enabled))
                return enabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to the original estimate preference.
        }
        return EstimateUiSettings.Load().WrapNames;
    }

    private void SetEnabled(bool enabled)
    {
        if (IsEnabled == enabled) return;
        IsEnabled = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowHeight)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditorWrapping)));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, enabled.ToString());
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // UI preferences are best effort; never block document editing.
        }
    }

    internal static void AddMenuItem(ContextMenu menu)
    {
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Длинные названия", IsEnabled = false });
        var item = new MenuItem
        {
            Header = "В несколько строк (строка становится выше)",
            IsCheckable = true,
            IsChecked = Current.IsEnabled
        };
        item.Click += (_, _) => Current.SetEnabled(item.IsChecked);
        menu.Items.Add(item);
    }
}

/// <summary>Display only: retain the original value for editing, tooltips and exports.</summary>
public sealed class SingleLineNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        NameDisplayText.SingleLine(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
