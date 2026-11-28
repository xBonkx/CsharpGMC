using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Read-only document dependency map for a full КП and its stages.</summary>
public static partial class SqlServerStages
{
    public sealed record StageDocumentLink(long Id, string Kind, string Name,
        long? ParentId, string ParentName, bool Protected, string State);

    public static IReadOnlyList<StageDocumentLink> ReadStageDocumentLinks(
        string connectionString, long fullId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        var docs = new List<(long Id, string Kind, string Name, long? Parent,
            bool Locked, bool IsRest)>();
        using (var cmd = new SqlCommand(
            "SELECT c.id,c.kind,c.name,c.stage_of,c.base_contract_id,c.locked,c.stage_rest " +
            "FROM dbo.contracts c WHERE c.object_id= " +
            "(SELECT object_id FROM dbo.contracts WHERE id=@full) AND " +
            "(c.id=@full OR c.stage_of=@full OR c.base_contract_id=@full OR " +
            "c.base_contract_id IN (SELECT id FROM dbo.contracts WHERE stage_of=@full)) " +
            "ORDER BY c.id", conn))
        {
            cmd.Parameters.Add("@full", SqlDbType.BigInt).Value = fullId;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                docs.Add((
                    SqlInteger.ReadInt64(r, 0),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? r.IsDBNull(4) ? null
                        : SqlInteger.ReadInt64(r, 4)
                        : SqlInteger.ReadInt64(r, 3),
                    !r.IsDBNull(5) && SqlFlag.Read(r, 5),
                    !r.IsDBNull(6) && SqlFlag.Read(r, 6)));
        }
        var names = docs.ToDictionary(x => x.Id, x => x.Name);
        var result = docs.Select(d => new StageDocumentLink(d.Id,
            d.IsRest ? "Остаток" : d.Kind == "dop" ? "ДС" :
                d.Id == fullId ? "Полное КП" : d.Parent == fullId ? "Этап" : d.Kind.ToUpperInvariant(),
            d.Name, d.Parent, d.Parent is long parent
                ? names.GetValueOrDefault(parent, $"ID {parent}") : "—",
            d.Locked, d.Locked ? "Заблокирован" : "Редактируемый")).ToList();

        using (var cmd = new SqlCommand(
            "SELECT a.id,a.number,a.signed,a.contract_id FROM dbo.acts a " +
            "WHERE a.contract_id=@full OR a.contract_id IN " +
            "(SELECT id FROM dbo.contracts WHERE stage_of=@full) ORDER BY a.id", conn))
        {
            cmd.Parameters.Add("@full", SqlDbType.BigInt).Value = fullId;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var owner = SqlInteger.ReadInt64(r, 3);
                var signed = !r.IsDBNull(2) && SqlFlag.Read(r, 2);
                result.Add(new StageDocumentLink(
                    SqlInteger.ReadInt64(r, 0),
                    "КС-2", "КС-2 № " + (r.IsDBNull(1) ? "" :
                        Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture)),
                    owner, names.GetValueOrDefault(owner, $"ID {owner}"),
                    signed, signed ? "Подписан" : "Черновик"));
            }
        }
        return result;
    }
}
