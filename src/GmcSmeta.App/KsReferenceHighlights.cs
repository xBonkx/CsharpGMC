using System.Windows;
using System.Windows.Media;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Maintains only the referenced cells of one stable KS grid snapshot.
/// The owner clears this instance before replacing the row collection.
/// </summary>
internal sealed class KsReferenceHighlights
{
    private static readonly Brush[] Colors = KsFormulaReferenceColors.Palette
        .Select(Freeze)
        .ToArray();
    private static readonly Brush[] Fills = BuildFills(KsFormulaReferenceColors.NowCellBackground);
    private static readonly Brush[] SelectedFills = BuildFills(SelectionBackground());

    private readonly ILookup<long, KsRowVm> _byId;
    private HashSet<KsRowVm> _highlighted = new();

    public KsReferenceHighlights(IEnumerable<KsRowVm> rows) =>
        _byId = rows.Where(row => !row.IsSection).ToLookup(row => row.PositionId);

    // References arrive unique and in typing order. Missing IDs still consume
    // a palette slot, exactly as in the original full-grid implementation.
    public void Update(IReadOnlyList<long> references)
    {
        var next = new HashSet<KsRowVm>();
        for (var i = 0; i < references.Count; i++)
        {
            var slot = i % Colors.Length;
            foreach (var row in _byId[references[i]])
            {
                next.Add(row);
                row.ReferenceBrush = Colors[slot];
                row.ReferenceFillBrush = Fills[slot];
                row.ReferenceSelectedFillBrush = SelectedFills[slot];
            }
        }
        foreach (var row in _highlighted)
            if (!next.Contains(row)) ClearRow(row);
        _highlighted = next;
    }

    public void Clear()
    {
        foreach (var row in _highlighted) ClearRow(row);
        _highlighted.Clear();
    }

    private static void ClearRow(KsRowVm row)
    {
        row.ReferenceBrush = null;
        row.ReferenceFillBrush = null;
        row.ReferenceSelectedFillBrush = null;
    }

    private static Brush[] BuildFills(string background) =>
        Enumerable.Range(0, KsFormulaReferenceColors.Palette.Count)
            .Select(i => Freeze(KsFormulaReferenceColors.ShadeOver(i, background)))
            .ToArray();

    // Python paints the alpha-30 shade over the cell, including when it is
    // selected. WPF cannot layer it over the cell background, so the same
    // blend is pre-composited over the real base and selection colours.
    private static string SelectionBackground()
    {
        if (Application.Current?.TryFindResource("GridSelectionBrush") is SolidColorBrush brush)
            return $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
        return "#C9DBFB"; // App.xaml GridSelectionBrush fallback
    }

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
