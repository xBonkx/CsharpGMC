using System.Windows;
using System.Windows.Controls;

namespace GmcSmeta.App;

/// <summary>
/// Estimate footer: equal columns, independent natural row heights and a single
/// gap between cards. Collapsed cards do not occupy cells (Qt kp_editor._layout_totals).
/// </summary>
public sealed class EstimateTotalsCardsPanel : Panel
{
    internal const double Gap = 10;

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(EstimateTotalsCardsPanel),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is int n && n >= 1);

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    private UIElement[] VisibleCards() => InternalChildren.Cast<UIElement>()
        .Where(card => card.Visibility != Visibility.Collapsed).ToArray();

    private static double HorizontalGap(double width, int columns) =>
        columns == 1 ? 0 : Math.Min(Gap, width / (columns - 1));

    private static double CellWidth(double width, int columns, double gap) =>
        Math.Max(0, (width - gap * (columns - 1)) / columns);

    private static double[] RowHeights(UIElement[] cards, int columns)
    {
        var heights = new double[(cards.Length + columns - 1) / columns];
        for (int i = 0; i < cards.Length; i++)
            heights[i / columns] = Math.Max(heights[i / columns], cards[i].DesiredSize.Height);
        return heights;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var cards = VisibleCards();
        if (cards.Length == 0) return new Size();
        var columns = Math.Min(Columns, cards.Length);
        var gap = HorizontalGap(availableSize.Width, columns);
        var width = CellWidth(availableSize.Width, columns, gap);
        foreach (var card in cards)
            card.Measure(new Size(width, double.PositiveInfinity));
        var heights = RowHeights(cards, columns);
        var desiredWidth = double.IsPositiveInfinity(availableSize.Width)
            ? cards.Max(card => card.DesiredSize.Width) * columns + gap * (columns - 1)
            : availableSize.Width;
        return new Size(desiredWidth, heights.Sum() + Gap * (heights.Length - 1));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cards = VisibleCards();
        if (cards.Length == 0) return finalSize;
        var columns = Math.Min(Columns, cards.Length);
        var gap = HorizontalGap(finalSize.Width, columns);
        var width = CellWidth(finalSize.Width, columns, gap);
        var heights = RowHeights(cards, columns);
        double y = 0;
        for (int row = 0; row < heights.Length; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                int index = row * columns + col;
                if (index >= cards.Length) break;
                cards[index].Arrange(new Rect(col * (width + gap), y, width, heights[row]));
            }
            y += heights[row] + Gap;
        }
        return finalSize;
    }
}
