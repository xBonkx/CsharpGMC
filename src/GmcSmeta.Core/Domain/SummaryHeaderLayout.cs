namespace GmcSmeta.Core.Domain;

public static class SummaryHeaderLayout
{
    public enum Band { Position, Materials, Work, Total }
    public readonly record struct Column(string Key, double Width);
    public readonly record struct Run(Band Group, double Left, double Width);

    public static Band GroupFor(string key) => key switch
    {
        "name" or "unit" or "qty" => Band.Position,
        "m_ucost" or "m_uprice" or "m_mcost" or "m_msell" => Band.Materials,
        "w_ucost" or "w_usell" or "w_wcost" or "w_wsell" or "markup" => Band.Work,
        "t_cost" or "t_sell" or "mat_margin" or "work_margin" => Band.Total,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown cost summary column")
    };

    // Input is the visible column sequence in DisplayIndex order.
    public static IReadOnlyList<Run> Build(IEnumerable<Column> columns, double origin)
    {
        var result = new List<Run>();
        var left = origin;
        foreach (var column in columns)
        {
            if (column.Width <= 0 || !double.IsFinite(column.Width)) continue;
            var group = GroupFor(column.Key);
            if (result.Count > 0 && result[^1].Group == group)
            {
                var previous = result[^1];
                result[^1] = previous with { Width = previous.Width + column.Width };
            }
            else result.Add(new(group, left, column.Width));
            left += column.Width;
        }
        return result;
    }
}
