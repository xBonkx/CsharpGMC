using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Read-only snapshot for a multi-sheet KS-2 / KS-3 Excel package.
/// A single selected legal contract defines the scope; no arbitrary act IDs
/// may be used to read data from other objects.
/// </summary>
public static partial class SqlServerKsPrintData
{
    public sealed record Line(long ActId, string Section, string Name, string Unit,
        string PositionNumber, decimal Quantity, decimal Price, decimal WorkPrice,
        bool OutsideKp, string Basis = "", string WorkCode = "", long SystemId = 0);

    public sealed record Section(long Id,long ContractId,string Name);
    public sealed record PrintSection(string Name,IReadOnlyList<Line> Lines,decimal Smr,decimal Transport);

    public sealed record Contractor(long DocumentId, string ContractDate,
        string CompanyName, string LegalName, string SignerPosition, string SignerName);

    public sealed record Package(SqlServerKsContractScope.Scope Scope,
        KsGroupReport Report, SqlServerKsRequisites.Values Requisites,
        IReadOnlyList<Line> Lines, IReadOnlyDictionary<long, Contractor> Contractors)
    {
        public IReadOnlyDictionary<long, KsLegalRequisites.Agreement> LegalAgreements { get; init; } = new Dictionary<long, KsLegalRequisites.Agreement>();
        public IReadOnlyList<Section> Sections {get;init;}=Array.Empty<Section>();

        // External cache uses reference identity, so a record 'with' copy with
        // different Lines/Sections/Scope never inherits the original index.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Package, KsPrintPackageIndex>
            Indexes = new();

        private KsPrintPackageIndex Index => Indexes.GetValue(this, static p => new KsPrintPackageIndex(p));

        public IReadOnlyList<Line> LinesFor(long actId) => Index.LinesFor(actId);

        // Resolve by stable ID, not section caption; include sections with no closed positions.
        public IReadOnlyList<PrintSection> SectionsFor(SqlServerActs.ActInfo act)
        {
            return Index.SectionsFor(act);
        }
    }

    public static Package Read(string connectionString, long contractId, string groupKey, CancellationToken token = default, string? expectedRevision = null)
    {
        using var conn = SqlReadCancellation.Open(connectionString, token);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = SqlReadCancellation.Revision(conn, token);
            if (expectedRevision is not null && (string.IsNullOrWhiteSpace(before) || before != expectedRevision))
                throw new InvalidOperationException("КС изменились. Обновите акт перед выгрузкой.");
            var scope = SqlServerKsContractScope.Read(conn, contractId, token: token);
            var package = ReadPackage(conn, contractId, scope, KsGroupReport.Calculate(scope, groupKey), groupReport: true, token: token);
            if (!string.IsNullOrWhiteSpace(before) && before == SqlReadCancellation.Revision(conn, token))
                return package;
        }
        throw new InvalidOperationException("КС изменились во время подготовки экспорта. Повторите выгрузку.");
    }

    public static Package ReadSingle(string connectionString, long contractId, long actId, string expectedRevision, CancellationToken token = default)
    {
        using var conn = SqlReadCancellation.Open(connectionString, token);
        var before = SqlReadCancellation.Revision(conn, token);
        if (string.IsNullOrWhiteSpace(before) || before != expectedRevision)
            throw new InvalidOperationException("КС изменились. Обновите акт перед выгрузкой.");
        var scope = SqlServerKsContractScope.Read(conn, contractId, token: token);
        var report = KsGroupReport.CalculateSingle(scope, actId);
        var package = ReadPackage(conn, contractId, scope, report, token: token);
        if (before != SqlReadCancellation.Revision(conn, token))
            throw new InvalidOperationException("КС изменились во время подготовки экспорта. Обновите акт.");
        return package;
    }

    // Export all acts of the selected document, not all annexes of the legal agreement.
    // Read once under the same revision, while keeping the legal scope for cumulative KS-3.
    public static IReadOnlyList<Package> ReadAll(string connectionString, long contractId,
        string expectedRevision, CancellationToken token = default)
    {
        using var conn = SqlReadCancellation.Open(connectionString, token);
        var before = SqlReadCancellation.Revision(conn, token);
        if (string.IsNullOrWhiteSpace(before) || before != expectedRevision)
            throw new InvalidOperationException("КС изменились. Обновите акт перед выгрузкой.");
        var scope = SqlServerKsContractScope.Read(conn, contractId, token: token);
        var local = scope.Acts.Where(a => a.ContractId == contractId)
            .OrderBy(a => a.Date).ThenBy(a => a.Id).ToArray();
        if (local.Length == 0 || local.Length > 500)
            throw new InvalidOperationException("Неверное число актов для экспорта.");
        var prepared = new KsGroupReport.Prepared(scope);
        var snapshot = ReadPackage(conn, contractId, scope,
            prepared.Single(local[0].Id), local, token: token);
        var document = scope.Documents.Single(d => d.Id == contractId);
        var objectName = snapshot.Requisites.ObjectName.Trim().ToLowerInvariant();
        var grouped = document.Form.Trim().Equals("r4", StringComparison.OrdinalIgnoreCase) &&
            objectName is ("р4" or "p4" or "r4");
        var reports = (grouped
            ? local.GroupBy(KsWorkspaceProjection.GroupKey)
                .Select(g => prepared.Group(g.Key, selectedDocumentOnly: true))
            : local.Select(a => prepared.Single(a.Id))).ToArray();
        // The first package's guard is not sufficient for later reports in a
        // batch: their previous groups and opening-balance usage can differ.
        if (snapshot.LegalAgreements.Count != 0)
            foreach (var report in reports)
                KsLegalRequisites.RequirePrintAgreement(scope, report, snapshot.LegalAgreements, grouped);
        token.ThrowIfCancellationRequested();
        var packages = ScopeReports(snapshot, reports, token);
        if (before != SqlReadCancellation.Revision(conn, token))
            throw new InvalidOperationException("КС изменились во время подготовки экспорта. Обновите акт.");
        return packages;
    }

    /// <summary>
    /// Partition an already read export snapshot by report membership in one
    /// pass. Preserve the original line order and full legal/report context.
    /// No SQL reads, monetary calculations, or changes to the input package.
    /// </summary>
    public static IReadOnlyList<Package> ScopeReports(Package snapshot,
        IEnumerable<KsGroupReport> reports, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var ordered = reports.ToArray();
        var reportIndexesByAct = ordered.SelectMany((report, index) =>
            report.Acts.Select(act => act.Id).Distinct().Select(id => (Id: id, Index: index)))
            .ToLookup(x => x.Id, x => x.Index);
        var lines = ordered.Select(_ => new List<Line>()).ToArray();
        foreach (var line in snapshot.Lines)
        {
            token.ThrowIfCancellationRequested();
            foreach (var index in reportIndexesByAct[line.ActId]) lines[index].Add(line);
        }
        token.ThrowIfCancellationRequested();
        return ordered.Select((report, index) =>
            snapshot with { Report = report, Lines = lines[index].AsReadOnly() }).ToArray();
    }

    private static Package ReadPackage(SqlConnection conn, long contractId,
        SqlServerKsContractScope.Scope scope, KsGroupReport report,
        IReadOnlyList<SqlServerActs.ActInfo>? readActs = null, bool groupReport = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var requisites = SqlServerKsRequisites.Read(conn, contractId, token);
        if (requisites.ObjectId != scope.Documents[0].ObjectId)
            throw new InvalidOperationException("Реквизиты не относятся к объекту выбранной КС-3.");
        var selectedActs = readActs ?? report.Acts;
        if (selectedActs.Count == 0 || selectedActs.Count > 500)
            throw new InvalidOperationException("Неверное число актов для экспорта.");

        var contractors = ReadContractors(conn, scope.Documents, token);

        var scopeIds = scope.Documents.Select(d => d.Id).ToHashSet();
        var docIds = selectedActs.Select(a => a.ContractId).Distinct().ToArray();
        if (docIds.Any(id => !scopeIds.Contains(id)))
            throw new InvalidOperationException("Акты не относятся к выбранному договору.");
        var ids = selectedActs.Select(a => a.Id).ToList();
        var parameters = ids.Select((id, i) => "@act" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var lines = new List<Line>();
        using (var command = new SqlCommand(
            "SELECT al.act_id,sy.name,p.name,p.unit,p.num,al.qty,al.price,al.work_price,p.out_of_kp,p.basis,p.extra_json,sy.id " +
            "FROM dbo.act_lines al " +
            "JOIN dbo.acts a ON a.id=al.act_id " +
            "JOIN dbo.positions p ON p.id=al.position_id " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE al.act_id IN (" + string.Join(",",parameters) + ") " +
            "AND sy.contract_id=a.contract_id AND a.contract_id IN (" +
            string.Join(",", docIds.Select((_,i) => "@doc"+i)) + ") " +
            "ORDER BY a.id,sy.[order],sy.id,p.[order],p.id", conn))
        {
            for (var i=0;i<ids.Count;i++)
                command.Parameters.Add(parameters[i], SqlDbType.BigInt).Value = ids[i];
            for (var i=0;i<docIds.Length;i++)
                command.Parameters.Add("@doc"+i,SqlDbType.BigInt).Value = docIds[i];
            using var registration = SqlReadCancellation.Register(command, token);
            using var r = SqlReadCancellation.ExecuteReader(command, token);
            string S(int i) => r.IsDBNull(i) ? "" :
                Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            decimal D(int i) => r.IsDBNull(i) ? 0m :
                Money.FromDouble(Convert.ToDouble(r.GetValue(i),CultureInfo.InvariantCulture));
            while(r.Read())
            {
                token.ThrowIfCancellationRequested();
                var basis=S(9);
                var workCode=basis;
                if(string.IsNullOrWhiteSpace(workCode) && !string.IsNullOrWhiteSpace(S(10)))
                {
                    try
                    {
                        using var json=JsonDocument.Parse(S(10));
                        if(json.RootElement.ValueKind==JsonValueKind.Object &&
                           json.RootElement.TryGetProperty("B",out var code) &&
                           code.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                            workCode=code.ToString();
                    }
                    catch(JsonException) { /* The original basis remains usable. */ }
                }
                lines.Add(new Line(
                    Convert.ToInt64(r.GetValue(0),CultureInfo.InvariantCulture),
                    S(1),S(2),S(3),S(4),D(5),D(6),D(7),
                    !r.IsDBNull(8) && Convert.ToBoolean(r.GetValue(8),CultureInfo.InvariantCulture),
                    basis,workCode,Convert.ToInt64(r.GetValue(11),CultureInfo.InvariantCulture)));
            }
        }

        var sections=new List<Section>();
        using(var cmd=new SqlCommand(
            "SELECT id,contract_id,name FROM dbo.systems WHERE contract_id IN ("+
            string.Join(",",docIds.Select((_,i)=>"@d"+i))+") ORDER BY contract_id,[order],id",conn))
        {
            for(var i=0;i<docIds.Length;i++)cmd.Parameters.Add("@d"+i,SqlDbType.BigInt).Value=docIds[i];
            using var registration = SqlReadCancellation.Register(cmd, token);
            using var reader=SqlReadCancellation.ExecuteReader(cmd, token);
            while(reader.Read())
            {
                token.ThrowIfCancellationRequested();
                sections.Add(new(Convert.ToInt64(reader.GetValue(0)),Convert.ToInt64(reader.GetValue(1)),
                    reader.IsDBNull(2)?"":reader.GetString(2)));
            }
        }
        var legalSources = new Dictionary<long, KsLegalRequisites.Agreement>();
        if (SqlServerAgreements.Enabled(conn, token: token))
        {
            var objectId = scope.Documents[0].ObjectId;
            var cards = SqlServerAgreements.Read(conn, objectId, token: token).ToDictionary(c => c.Id);
            var companies = SqlServerWorkspace.Companies(conn, token).ToDictionary(c => c.Id, c => c.Name);
            // Keep the full object's legal map for the completeness guard.
            // Report rows and money still use the original scope.
            using (var mapping = new SqlCommand("SELECT id,agreement_id FROM dbo.contracts WHERE object_id=@object AND kind IN (N'main',N'dop') AND agreement_id IS NOT NULL", conn))
            {
                mapping.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
                using var registration = SqlReadCancellation.Register(mapping, token);
                using var reader = SqlReadCancellation.ExecuteReader(mapping, token);
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var id = Convert.ToInt64(reader.GetValue(0));
                    // Missing/archived cards are rejected by the guard only when relevant to this report.
                    if (!cards.TryGetValue(Convert.ToInt64(reader.GetValue(1)), out var legal)) continue;
                    var companyName = legal.CompanyId is long companyId ? companies.GetValueOrDefault(companyId) ?? "" : "";
                    var co = new Contractor(id, legal.Date, companyName, legal.ContractorFull, legal.ContractorPosition, legal.ContractorName);
                    legalSources[id] = new(legal.Id, legal.Number, legal.Date, co);
                }
            }
            var legalId = KsLegalRequisites.RequirePrintAgreement(scope, report, legalSources, groupReport);
            requisites = cards[legalId].Print with { SitePart = requisites.SitePart };
        }
        token.ThrowIfCancellationRequested();
        return new Package(scope,report,requisites,lines.AsReadOnly(),contractors)
            {Sections=sections.AsReadOnly(), LegalAgreements=legalSources};
    }
}
