using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GmcSmeta.App;

/// <summary>
/// Простой диалог «одно поле + кнопки» — для переименования объекта из дерева (F2
/// и контекстное меню). Python в этом месте редактирует строку прямо в списке;
/// до переноса встроенного редактора используется этот небольшой запрос.
/// </summary>
public sealed class PromptDialog : Window
{
    public string Value => Box.Text.Trim();
    private readonly TextBox Box;

    public PromptDialog(string title, string label, string value)
    {
        Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 420;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 246, 250));
        SetResourceReference(FontFamilyProperty, "UiFontFamily");
        SetResourceReference(FontSizeProperty, "UiBodySize");
        UseLayoutRounding = true;
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
        root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) });
        Box = new TextBox { Text = value, Padding = new Thickness(10, 7, 10, 7) };
        Box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { DialogResult = true; e.Handled = true; }
            else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
        root.Children.Add(Box);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(12, 6, 12, 6) };
        cancel.Click += (_, _) => DialogResult = false;
        var ok = new Button { Content = "Сохранить", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        ok.IsDefault = true;
        cancel.IsCancel = true;
        ok.Click += (_, _) => DialogResult = true;
        row.Children.Add(cancel);
        row.Children.Add(ok);
        root.Children.Add(row);
        Content = root;
        Loaded += (_, _) => { Box.Focus(); Box.SelectAll(); };
    }
}
