using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class ActsEditorView
{
    private IReadOnlyList<KsActSheetProjection.SectionDisplay> ProjectActSections(SqlServerActs.ActSheetData sheet)
    {
        var manual = _details is not null && !string.IsNullOrEmpty(_details.SectionAmountsJson)
            ? KsActAllocation.Resolve(
                sheet.Positions.Where(p => p.LineId is not null)
                    .Select(p => new KsActAllocation.Line(p.SystemId, p.LineQty, p.LinePrice, p.LineWorkPrice)).ToList(),
                sheet.Systems.Select(s => s.SystemId).ToHashSet(),
                _details.Smr, _details.Transport, _isPosSmr, _details.SmrBySystem,
                _details.SectionAmountsJson, _roundRows)
            : null;
        return KsActSheetProjection.Sections(sheet, manual, _isPosSmr, _roundRows);
    }

    private static string SectionAmountText(KsActSheetProjection.SectionDisplay section) =>
        section.Unallocated || section.Amount != 0m ? Money.Format(section.Amount) : "";
}
