using System.Globalization;
using System.Windows;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App;

/// <summary>Contract print actions: isolated from the estimate editor and its SQL writes.</summary>
public partial class MainWindow
{
    private sealed record KpSnapshot(EstimateDocument Estimate, DocRow Header,
        IReadOnlyDictionary<int, decimal> SystemSmr, SqlServerKpRequisites.Values Requisites,
        decimal SmrPercent, bool ManualNumbering);

    private KpSnapshot LoadKpPrintData(int contractId)
    {
        if (_sqlConnectionString is null) throw new InvalidOperationException("Нет подключения к SQL Server.");
        using var conn = SqlServerDatabase.Open(_sqlConnectionString);
        var startRevision = SqlServerDatabase.Revision(conn);
        var header = Workspace.Documents(conn).FirstOrDefault(x => x.Id == contractId)
            ?? throw new InvalidOperationException("Документ для экспорта не найден.");
        var estimate = EstimateReader.Load(conn, contractId);

        // Python Position.cost_unit() prefers the factual procurement cost over
        // the KP's planned purchase_price. Never write either value during export.
        var factual = new Dictionary<int, decimal>();
        using (var command = new SqlCommand(
            "SELECT p.id,pr.cost_price FROM dbo.positions p " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "JOIN dbo.procurement pr ON pr.position_id=p.id " +
            "WHERE sy.contract_id=@id AND pr.cost_price IS NOT NULL", conn))
        {
            command.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = contractId;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                factual[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        }
        if (factual.Count > 0)
        {
            EstimateRow Resolve(EstimateRow row) =>
                !row.IsSystem && factual.TryGetValue(row.Id, out var cost)
                    ? row with { PurchasePrice = cost } : row;
            estimate = estimate with
            {
                Rows = estimate.Rows.Select(Resolve).ToArray(),
                PositionsInOrder = estimate.PositionsInOrder.Select(Resolve).ToArray()
            };
        }
        var systemSmr = new Dictionary<int, decimal>();
        using (var command = new SqlCommand(
            "SELECT id,smr_amount FROM dbo.systems WHERE contract_id=@id", conn))
        {
            command.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = contractId;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                systemSmr[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] = reader.IsDBNull(1) ? 0m
                    : Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        }
        var requisites = SqlServerKpRequisites.Read(conn, contractId);
        var percent = (decimal)SqlServerWorkspace.ReadContract(conn, contractId).SmrPercent;
        var manualNumbering = string.Equals(estimate.Form, "r4", StringComparison.OrdinalIgnoreCase) ||
            TagRules.Load(conn).Has(estimate.Form, "num_manual");
        if (startRevision != SqlServerDatabase.Revision(conn))
            throw new InvalidOperationException("Данные документа изменились во время выгрузки. Повторите экспорт.");
        return new KpSnapshot(estimate, header, systemSmr, requisites, percent, manualNumbering);
    }

    /// <summary>Экспорт КП из редактора сметы (btn_export: «Сохранить в Excel / PDF»).</summary>
    internal void ExportKpDocument(DocRow doc, bool internalPrices = false,
        bool positional = false, bool pdf = false, bool vatLine = false) =>
        ExportKpDocumentCore(doc, internalPrices, positional, pdf, vatLine);

    private async void ExportKpDocumentCore(DocRow doc, bool internalPrices = false,
        bool positional = false, bool pdf = false, bool vatLine = false)
    {
        // Python kp_export._default_file: object-based name, number and date.
        var prefix = doc.Kind switch
        {
            "main" => "Приложение к договору",
            "dop" => "Допсоглашение",
            _ => "КП"
        };
        var objectName = _objects.FirstOrDefault(x => x.Id == doc.ObjectId)?.Name ?? doc.Title;
        var fileName = prefix + " " + objectName
            + (string.IsNullOrWhiteSpace(doc.Name) ? "" : " — " + doc.Name)
            + (doc.Kind != "kp" && !string.IsNullOrWhiteSpace(doc.Number) ? " № " + doc.Number : "")
            + (string.IsNullOrWhiteSpace(doc.Date) ? "" : " " + doc.Date);
        if (internalPrices) fileName += " (закуп-розница)";
        try
        {
            if (pdf)
            {
                await ContractDocumentSave.PdfAsync(this, fileName, () =>
                {
                    var snapshot = LoadKpPrintData(doc.Id);
                    var effectivePositional = positional ||
                        (!internalPrices && snapshot.Estimate.SmrMode == "pos");
                    if (positional && snapshot.Estimate.SmrMode != "pos")
                        throw new InvalidOperationException("Позиционная форма доступна для режима СМР по позициям.");
                    return KpPdfForms.Create(snapshot.Estimate, snapshot.Header,
                        internalPrices, effectivePositional, vatLine,
                        requisites: snapshot.Requisites, manualNumbering: snapshot.ManualNumbering);
                });
            }
            else
            {
                await ContractDocumentSave.XlsxAsync(this, fileName, () =>
                {
                    var snapshot = LoadKpPrintData(doc.Id);
                    var effectivePositional = positional ||
                        (!internalPrices && snapshot.Estimate.SmrMode == "pos");
                    if (positional && snapshot.Estimate.SmrMode != "pos")
                        throw new InvalidOperationException("Позиционная форма доступна для режима СМР по позициям.");
                    return KpExcelForms.Create(snapshot.Estimate, snapshot.Header,
                        internalPrices, effectivePositional, snapshot.SystemSmr,
                        vatLine: vatLine, requisites: snapshot.Requisites,
                        smrPercent: snapshot.SmrPercent, manualNumbering: snapshot.ManualNumbering);
                });
            }
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Экспорт КП",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
