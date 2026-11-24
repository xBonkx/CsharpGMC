using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;

namespace GmcSmeta.App;

/// <summary>Справка по работе с программой, по эталону Python HelpDialog.</summary>
public sealed class HelpDialog : Window
{
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single };
    private readonly FlowDocumentScrollViewer _view = new()
    {
        IsToolBarVisible = false,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    public HelpDialog(Window owner, string topic = "Начало работы")
    {
        Owner = owner;
        Title = "Как работать в программе";
        Width = 940;
        Height = 640;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        WorkspaceWindowUi.ApplyChrome(this);

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _list.ItemsSource = HelpTopics.All.Keys.ToArray();
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is string key && HelpTopics.All.TryGetValue(key, out var html))
                _view.Document = BuildDocument(html);
        };
        grid.Children.Add(_list);

        var right = new DockPanel { Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        var close = new Button { Content = "Закрыть", MinWidth = 110,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0),
            IsDefault = true, IsCancel = true };
        WorkspaceWindowUi.StyleButton(close, primary: true);
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        right.Children.Add(close);

        var hint = WorkspaceWindowUi.Hint("Справка перенесена из Python; отдельные команды C# могут отличаться.");
        hint.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(hint, Dock.Top);
        right.Children.Add(hint);

        var border = new Border { BorderThickness = new Thickness(1), BorderBrush = Brushes.LightGray,
            Background = Brushes.White, Child = _view };
        right.Children.Add(border);
        Content = grid;

        _list.SelectedItem = HelpTopics.All.ContainsKey(topic) ? topic : HelpTopics.All.Keys.First();
    }

    // HELP содержит только локальный, доверенный текст с h2/p/ol/ul/li/b/code.
    // Отображаем его как WPF-документ, без HTML-движка, скриптов и внешних ссылок.
    private static FlowDocument BuildDocument(string html)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(16, 10, 16, 10),
            FontSize = 11.5 * 4.0 / 3.0,
            Foreground = Brushes.Black
        };
        foreach (var element in XElement.Parse("<help>" + html + "</help>").Elements())
        {
            switch (element.Name.LocalName)
            {
                case "h2":
                    var heading = CreateParagraph(element);
                    heading.FontSize = 20;
                    heading.FontWeight = FontWeights.SemiBold;
                    heading.Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175));
                    document.Blocks.Add(heading);
                    break;
                case "p":
                    document.Blocks.Add(CreateParagraph(element));
                    break;
                case "ol":
                case "ul":
                    var list = new System.Windows.Documents.List
                    {
                        MarkerStyle = element.Name.LocalName == "ol" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    foreach (var item in element.Elements("li"))
                    {
                        var paragraph = CreateParagraph(item);
                        paragraph.Margin = new Thickness(0, 0, 0, 8);
                        list.ListItems.Add(new ListItem(paragraph));
                    }
                    document.Blocks.Add(list);
                    break;
            }
        }
        return document;
    }

    private static Paragraph CreateParagraph(XElement element)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        AppendInlines(paragraph.Inlines, element.Nodes());
        return paragraph;
    }

    private static void AppendInlines(InlineCollection target, IEnumerable<XNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is XText text)
                target.Add(new Run(Regex.Replace(text.Value, @"\s+", " ")));
            else if (node is XElement element)
            {
                Span span = element.Name.LocalName switch
                {
                    "b" => new Bold(),
                    "code" => new Span { FontFamily = new FontFamily("Consolas") },
                    _ => new Span()
                };
                AppendInlines(span.Inlines, element.Nodes());
                target.Add(span);
            }
        }
    }
}
