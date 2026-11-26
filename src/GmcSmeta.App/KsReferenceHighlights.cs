using System.Windows.Media;

namespace GmcSmeta.App;

/// <summary>
/// Maintains only the referenced cells of one stable KS grid snapshot.
/// The owner clears this instance before replacing the row collection.
/// </summary>
internal sealed class KsReferenceHighlights
{
    private static readonly Brush[] Colors =
        new[] { "#2563EB", "#DC2626", "#16803C", "#9333EA", "#C2410C", "#087F8C", "#A21CAF", "#946200" }
        .Select(hex =>
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return (Brush)brush;
        }).ToArray();

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
            var brush = Colors[i % Colors.Length];
            foreach (var row in _byId[references[i]])
            {
                next.Add(row);
                row.ReferenceBrush = brush;
            }
        }
        foreach (var row in _highlighted)
            if (!next.Contains(row)) row.ReferenceBrush = null;
        _highlighted = next;
    }

    public void Clear()
    {
        foreach (var row in _highlighted) row.ReferenceBrush = null;
        _highlighted.Clear();
    }
}
