using System.IO;
using ClosedXML.Excel;

namespace GmcSmeta.App;

/// <summary>Capture a long-lived editable workbook without rebinding it to an
/// external export path or a temporary stream that will be closed.</summary>
internal static class LiveWorkbookSnapshot
{
    internal static byte[] Capture(XLWorkbook workbook)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        // ClosedXML 0.104.2 SaveAs(Stream) stores the stream as _originalStream.
        // On success the workbook owns a reference to it for later SaveAs calls.
        // Do NOT dispose it here. It is a managed MemoryStream and can be
        // collected once the workbook no longer references it.
        var backing = new MemoryStream();
        workbook.SaveAs(backing);
        return backing.ToArray();
    }
}
