using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GmcSmeta.App;

/// <summary>Python catalog/quick-search affordances without changing text or selection handlers.</summary>
internal static class ReferenceWindowUi
{
    internal static Grid Search(TextBox input,string placeholder)
    {
        input.SetResourceReference(FrameworkElement.StyleProperty,"PythonSearch");
        input.Padding=new Thickness(32,5,32,5);
        input.VerticalContentAlignment=VerticalAlignment.Center;
        input.Margin=new Thickness(0);
        AutomationProperties.SetName(input,placeholder);
        var root=new Grid();
        root.Children.Add(input);
        var icon=new System.Windows.Shapes.Path{Data=(Geometry)Application.Current.FindResource("GlyphSearch"),
            Stroke=new SolidColorBrush(Color.FromRgb(0x64,0x74,0x8B))};
        icon.SetResourceReference(FrameworkElement.StyleProperty,"SvgGlyphPath");
        var canvas=new Canvas{Width=24,Height=24};
        canvas.Children.Add(icon);
        root.Children.Add(new Viewbox{Width=18,Height=18,Child=canvas,
            HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,
            Margin=new Thickness(8,0,0,0),IsHitTestVisible=false});
        var hint=new TextBlock{Text=placeholder,TextTrimming=TextTrimming.CharacterEllipsis,
            Foreground=new SolidColorBrush(Color.FromRgb(0x94,0xA3,0xB8)),
            Margin=new Thickness(32,0,32,0),VerticalAlignment=VerticalAlignment.Center,
            IsHitTestVisible=false};
        hint.Style=EmptyVisibility(typeof(TextBlock),input,true);
        root.Children.Add(hint);
        var clear=new Button{Content="Очистить",ToolTip="Очистить поиск",Width=26,Height=26,
            MinWidth=0,MinHeight=0,Padding=new Thickness(0),Margin=new Thickness(0,0,3,0),
            HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
        clear.Style=EmptyVisibility(typeof(Button),input,false,
            (Style)Application.Current.FindResource("PythonToolbarButton"));
        clear.Background=Brushes.Transparent;
        clear.BorderThickness=new Thickness(0);
        clear.SetResourceReference(ContentControl.ContentTemplateProperty,"IconOnlyX");
        AutomationProperties.SetName(clear,"Очистить поиск");
        clear.Click+=(_,_)=>{input.Clear();input.Focus();};
        root.Children.Add(clear);
        return root;
    }

    private static Style EmptyVisibility(Type type,TextBox input,bool visibleWhenEmpty,Style? basedOn=null)
    {
        var style=basedOn is null?new Style(type):new Style(type,basedOn);
        style.Setters.Add(new Setter(UIElement.VisibilityProperty,visibleWhenEmpty?Visibility.Collapsed:Visibility.Visible));
        var empty=new DataTrigger{Binding=new Binding(nameof(TextBox.Text)){Source=input},Value=""};
        empty.Setters.Add(new Setter(UIElement.VisibilityProperty,visibleWhenEmpty?Visibility.Visible:Visibility.Collapsed));
        style.Triggers.Add(empty);
        return style;
    }

    internal static Grid LogoPreview(Image image)
    {
        var grid=new Grid{Width=260,Height=90,HorizontalAlignment=HorizontalAlignment.Left};
        grid.Children.Add(new Rectangle{RadiusX=6,RadiusY=6,Fill=Brushes.White,
            Stroke=new SolidColorBrush(Color.FromRgb(0xCB,0xD3,0xDE)),StrokeThickness=1,
            StrokeDashArray=new DoubleCollection{4,3},IsHitTestVisible=false});
        image.Width=250;image.Height=82;
        grid.Children.Add(image);
        var empty=new TextBlock{Text="нет логотипа",Foreground=Brushes.SlateGray,
            HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,
            IsHitTestVisible=false};
        var style=new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Collapsed));
        var missing=new DataTrigger{Binding=new Binding(nameof(Image.Source)){Source=image},Value=null};
        missing.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Visible));
        style.Triggers.Add(missing);empty.Style=style;grid.Children.Add(empty);
        return grid;
    }

    internal static GridSplitter HistorySplitter()
    {
        return new GridSplitter{Height=6,HorizontalAlignment=HorizontalAlignment.Stretch,
            VerticalAlignment=VerticalAlignment.Center,ResizeDirection=GridResizeDirection.Rows,
            ResizeBehavior=GridResizeBehavior.PreviousAndNext,
            Background=new SolidColorBrush(Color.FromRgb(0xCF,0xD6,0xE3))};
    }
}
