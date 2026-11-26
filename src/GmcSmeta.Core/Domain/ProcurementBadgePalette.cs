using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>Python GridDelegate pill: border is foreground with alpha 70 (0x46).</summary>
public sealed record ProcurementBadgeLook(string Background, string Foreground, string Border);

public static class ProcurementBadgePalette
{
    private static ProcurementBadgeLook Look(string background, string foreground) =>
        new(background, foreground, "#46" + foreground[1..]);

    private static readonly Dictionary<string, ProcurementBadgeLook> StatusLooks =
        SqlServerProcurement.StatusTable.ToDictionary(
            status => status.Code,
            status => Look(status.Bg == "#FFFFFF" ? "#FEE2E2" : status.Bg, status.Fg),
            StringComparer.Ordinal);

    private static readonly Dictionary<string, ProcurementBadgeLook> DueLooks = new(StringComparer.Ordinal)
    {
        ["late"] = Look("#FEE2E2", "#B91C1C"),
        ["soon"] = Look("#FEF3C7", "#B45309"),
        ["ok"] = Look("#EEF2F7", "#334155"),
        ["done"] = Look("#F4F4F5", "#94A3B8")
    };

    private static readonly ProcurementBadgeLook Empty = new("#00000000", "#0F172A", "#00000000");

    public static ProcurementBadgeLook Status(string code) =>
        StatusLooks.GetValueOrDefault(code, StatusLooks["none"]);

    public static ProcurementBadgeLook Due(string state) =>
        DueLooks.GetValueOrDefault(state, Empty);
}
