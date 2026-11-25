namespace GmcSmeta.Core.Domain;

/// <summary>Pure policy for restoring a pixel-scrolled table after replacing rows.
/// Row keys are stable identities, not display indices.</summary>
public static class SummaryViewportAnchor
{
    public readonly record struct Row(string Key, double Y);

    public static Row? Choose(Row? current, Row? top, IReadOnlySet<string> available)
    {
        if (current is { } c && double.IsFinite(c.Y) && available.Contains(c.Key)) return c;
        if (top is { } t && double.IsFinite(t.Y) && available.Contains(t.Key)) return t;
        return null;
    }

    public static double Offset(double offset, double actualY, double desiredY, double maximum)
    {
        if (!double.IsFinite(offset) || !double.IsFinite(actualY) ||
            !double.IsFinite(desiredY) || !double.IsFinite(maximum) || maximum < 0) return 0;
        return Math.Clamp(offset + actualY - desiredY, 0, maximum);
    }
}
