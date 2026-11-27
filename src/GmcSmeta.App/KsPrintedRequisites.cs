using ClosedXML.Excel;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Header cells only; financial tables and historical files are untouched.</summary>
internal static class KsPrintedRequisites
{
    internal static void DnsAct(IXLWorksheet ws, SqlServerKsPrintData.Package p, SqlServerActs.ActInfo act)
    {
        var legal = KsLegalRequisites.ForDocument(p, act.ContractId);
        Parts(ws, legal.Date, "F12", "G12", "H12");
        Supplements(ws, KsLegalRequisites.ForActSupplements(p, act),
            "C13", "E13", "F13", "E14", "F14", "G14", "H14");
    }

    internal static void DnsSummary(IXLWorksheet ws, SqlServerKsPrintData.Package p)
    {
        var legal = KsLegalRequisites.ForSummary(p);
        Parts(ws, legal.Date, "G14", "H14", "I14");
        Supplements(ws, KsLegalRequisites.ForSummarySupplements(p),
            "A15", "F15", "G15", "F16", "G16", "H16", "I16");
    }

    internal static void R4Act(IXLWorksheet ws, SqlServerKsPrintData.Package p, SqlServerActs.ActInfo act)
    {
        var legal = KsLegalRequisites.ForDocument(p, act.ContractId);
        var r = p.Requisites;
        ws.Cell("B7").Value = "Заказчик: " + Customer(p) + "\nПодрядчик: " + Contractor(legal) +
            "\nСтройка: " + (string.IsNullOrWhiteSpace(r.SiteFull) ? r.ObjectName : r.SiteFull);
        ws.Cell("B7").Style.Alignment.WrapText = true;
        ws.Cell("F12").Value = legal.Number.TrimStart('№', ' ');
        Parts(ws, legal.Date, "F13", "G13", "H13");
        SingleLine(ws, "A15:H15", string.Join("; ", KsLegalRequisites.ForActSupplements(p, act).Select(ds => ds.Label)));
        foreach (var address in new[] { "F7", "F8" }) ws.Cell(address).Clear(XLClearOptions.Contents);
        ws.Cell("C100").Value = r.SecondRole;
        ws.Cell("C102").Value = Join(r.SecondPosition, r.SecondOrganization);
        ws.Cell("E102").Value = r.SecondName;
        // This second fixed template signer has no corresponding field in the current snapshot.
        ws.Cell("C105").Clear(XLClearOptions.Contents);
        ws.Cell("E105").Clear(XLClearOptions.Contents);
        ws.Cell("C109").Value = Join(r.CustomerPosition, r.CustomerOrganization);
        ws.Cell("E109").Value = r.CustomerName;
        ws.Cell("C112").Value = Join(legal.Contractor.SignerPosition, Contractor(legal));
        ws.Cell("E112").Value = legal.Contractor.SignerName;
    }

    internal static void R4Summary(IXLWorksheet ws, SqlServerKsPrintData.Package p)
    {
        var legal = KsLegalRequisites.ForSummary(p);
        var r = p.Requisites;
        ws.Cell("A7").Value = "Заказчик (Генподрядчик) - " + Customer(p);
        ws.Cell("A8").Value = "Подрядчик (Субподрядчик) - " + Contractor(legal);
        ws.Cell("A9").Value = "Стройка - " + (string.IsNullOrWhiteSpace(r.SiteFull) ? r.ObjectName : r.SiteFull);
        ws.Cell("G12").Value = legal.Number.TrimStart('№', ' ');
        Parts(ws, legal.Date, "G13", "H13", "I13");
        SingleLine(ws, "A15:I15", string.Join("; ", KsLegalRequisites.ForSummarySupplements(p).Select(ds => ds.Label)));
        ws.Cell("B35").Value = Join(r.CustomerPosition, r.CustomerOrganization) + " __________________ " + r.CustomerName;
        ws.Cell("B39").Value = Join(legal.Contractor.SignerPosition, Contractor(legal)) + " __________________ " + legal.Contractor.SignerName;
        // These template codes belong to its original customer, not the selected agreement.
        foreach (var address in new[] { "G7", "G8" }) ws.Cell(address).Clear(XLClearOptions.Contents);
    }

    private static string Customer(SqlServerKsPrintData.Package p) =>
        string.IsNullOrWhiteSpace(p.Requisites.CustomerFull) ? p.Requisites.CustomerOrganization : p.Requisites.CustomerFull;
    private static string Contractor(KsLegalRequisites.Agreement legal) =>
        string.IsNullOrWhiteSpace(legal.Contractor.LegalName) ? legal.Contractor.CompanyName : legal.Contractor.LegalName;
    private static string Join(string left, string right) => (left.Trim() + " " + right.Trim()).Trim();

    private static void Parts(IXLWorksheet ws, string date, string day, string month, string year)
    {
        var values = date.Length == 0 ? new[] { "", "", "" } : date.Split('.');
        ws.Cell(day).Value = values[0];
        ws.Cell(month).Value = values[1];
        ws.Cell(year).Value = values[2];
    }

    private static void Supplements(IXLWorksheet ws, IReadOnlyList<KsLegalRequisites.Supplement> supplements,
        string label, string numberLabel, string number, string dateLabel, string day, string month, string year)
    {
        foreach (var address in new[] { label, numberLabel, number, dateLabel, day, month, year })
            ws.Cell(address).Clear(XLClearOptions.Contents);
        if (supplements.Count == 0) return;
        if (supplements.Count == 1 && supplements[0].FreeText is null)
        {
            ws.Cell(label).Value = "Дополнительное соглашение";
            ws.Cell(numberLabel).Value = "номер";
            ws.Cell(number).Value = supplements[0].Number;
            ws.Cell(dateLabel).Value = "дата";
            Parts(ws, supplements[0].Date, day, month, year);
        }
        else
        {
            ws.Cell(label).Value = "Дополнительные соглашения";
            ws.Cell(number).Value = string.Join("\n", supplements.Select(ds => ds.Label));
            ws.Cell(number).Style.Alignment.WrapText = true;
            ws.Row(ws.Cell(number).Address.RowNumber).Height = Math.Max(30, supplements.Count * 16);
        }
    }

    internal static void SingleLine(IXLWorksheet ws, string range, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var cells = ws.Range(range).Merge();
        cells.FirstCell().Value = "Дополнительное соглашение: " + text;
        cells.Style.Alignment.WrapText = true;
        ws.Row(cells.FirstCell().Address.RowNumber).Height = 30;
    }
}
