using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace GmcSmeta.App;

/// <summary>Presentation only: shared two-column fields and certificate labels.</summary>
internal static class PtoFormUi
{
    internal static void AddRow(Panel host,string label,Control field,double labelWidth)
    {
        var row=new Grid();
        row.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormFieldMargin");
        row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star),MaxWidth=labelWidth});
        row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(2,GridUnitType.Star)});
        var caption=new TextBlock{Text=label,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
        caption.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormColumnGap");
        row.Children.Add(caption);
        Grid.SetColumn(field,1);
        AutomationProperties.SetName(field,label);
        row.Children.Add(field);host.Children.Add(row);
    }

    internal static Grid WithPlaceholder(TextBox field,string text)
    {
        var grid=new Grid();
        grid.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormFieldMargin");
        grid.Children.Add(field);
        AutomationProperties.SetName(field,text);
        var hint=new TextBlock{Text=text,
            Margin=new Thickness(10,0,10,0),VerticalAlignment=VerticalAlignment.Center,
            TextTrimming=TextTrimming.CharacterEllipsis,IsHitTestVisible=false};
        hint.SetResourceReference(TextBlock.ForegroundProperty,"UiMutedTextBrush");
        var style=new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Collapsed));
        var empty=new DataTrigger{Binding=new Binding(nameof(TextBox.Text)){Source=field},Value=""};
        empty.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Visible));
        style.Triggers.Add(empty);hint.Style=style;grid.Children.Add(hint);
        return grid;
    }

    internal static readonly IValueConverter VerifiedText=new VerifiedConverter();

    internal static MultiBinding ExpiryBinding()
    {
        var binding=new MultiBinding{Converter=new ExpiryConverter(),Mode=BindingMode.OneWay};
        binding.Bindings.Add(new Binding("Expires"));
        binding.Bindings.Add(new Binding("NoExpiry"));
        return binding;
    }

    private sealed class VerifiedConverter:IValueConverter
    {
        public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>
            value is true?"Да":"Нет";
        public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>
            Binding.DoNothing;
    }

    private sealed class ExpiryConverter:IMultiValueConverter
    {
        public object Convert(object[] values,Type targetType,object parameter,CultureInfo culture)=>
            values.Length>0 && values[0] is string date && !string.IsNullOrWhiteSpace(date)
                ?date:values.Length>1 && values[1] is true?"Без срока":"Не указан";
        public object[] ConvertBack(object value,Type[] targetTypes,object parameter,CultureInfo culture)=>
            targetTypes.Select(_=>Binding.DoNothing).ToArray();
    }
}
