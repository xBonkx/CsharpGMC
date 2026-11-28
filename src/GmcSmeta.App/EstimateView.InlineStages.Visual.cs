using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace GmcSmeta.App;

public partial class EstimateView
{
    // Match ordinary estimate headers, including live Ctrl+wheel zoom,
    // border, padding and resize grippers. Tint only the stage header.
    private Style CreateInlineStageHeaderStyle(int index, bool rest)
    {
        var style = new Style(typeof(DataGridColumnHeader))
        {
            BasedOn = EstimateGrid.ColumnHeaderStyle ??
                (Style)FindResource("ReportGridHeader")
        };
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            StageBrush(rest ? "#F1F4F9" : StageTints[index % StageTints.Length])));
        style.Setters.Add(new Setter(Control.ForegroundProperty,
            StageBrush(rest ? "#334155" : StageInk[index % StageInk.Length])));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new EventSetter(ButtonBase.ClickEvent,
            new RoutedEventHandler(InlineStageHeader_Click)));
        return style;
    }

    private static DataTemplate CreateInlineStageHeaderTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        text.SetBinding(FrameworkElement.ToolTipProperty, new Binding());
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        return new DataTemplate { VisualTree = text };
    }

    private void AddCard(long id, string name, string amount, string status,
        string tint, string ink, bool warning)
    {
        var body = new StackPanel();
        var heading = new DockPanel();
        heading.Children.Add(new Border
        {
            Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
            Background = StageBrush(ink), Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        heading.Children.Add(new TextBlock
        {
            Text = name, FontWeight = FontWeights.SemiBold,
            Foreground = StageBrush("#334155"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        body.Children.Add(heading);
        body.Children.Add(new TextBlock
        {
            Text = amount, FontWeight = FontWeights.SemiBold,
            Foreground = StageBrush("#0F172A"), FontSize = (double)FindResource("UiBodySize"),
            Margin = new Thickness(0, 2, 0, 1),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        body.Children.Add(new TextBlock
        {
            Text = status, Foreground = warning ? StageBrush("#B91C1C") : (Brush)FindResource("UiMutedTextBrush"),
            FontSize = (double)FindResource("UiCaptionSize"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var button = new Button
        {
            Style = (Style)FindResource("PythonToolbarButton"),
            Content = body, Background = StageBrush(tint),
            BorderBrush = StageBrush(warning ? "#FCA5A5" : "#E4E8EF"),
            BorderThickness = new Thickness(1), Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 6, 4), Width = 218,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = $"{name}\n{amount}\n{status}\nМеню этапа"
        };
        var snapshot = _inlineStagesData;
        button.Click += (_, _) =>
        {
            if (snapshot is null || !ReferenceEquals(snapshot, _inlineStagesData)) return;
            var stage = snapshot.Stages.FirstOrDefault(item => item.Id == id) ??
                (snapshot.Rest?.Id == id ? snapshot.Rest : null);
            if (stage is not null) ShowInlineStageMenu(button, stage, snapshot);
        };
        InlineStagesCards.Children.Add(button);
    }
}
