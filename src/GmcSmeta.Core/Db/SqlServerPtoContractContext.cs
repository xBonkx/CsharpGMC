using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Read-only projection of estimate documents and their explicit
/// same-object active legal agreement, without modifying legacy data.</summary>
public static class SqlServerPtoContractContext
{
    public static IReadOnlyList<PtoContractContext> Read(SqlConnection conn,long? objectId=null)
    {
        var legal=SqlServerAgreements.Enabled(conn);
        var sql="SELECT c.id,c.object_id,c.number,c.[date],c.name,c.kind,co.legal_full "+
            (legal?",c.annex_number,a.id,a.number,a.agreement_date,a.customer_full,a.cust_org,a.contractor_full,ac.legal_full,ac.name ":" ")+
            "FROM dbo.contracts c LEFT JOIN dbo.companies co ON co.id=c.company_id "+
            (legal?"LEFT JOIN dbo.agreements a ON a.id=c.agreement_id AND a.object_id=c.object_id AND a.archived=0 "+
                "LEFT JOIN dbo.companies ac ON ac.id=a.company_id ":"")+
            "WHERE c.kind IN(N'main',N'dop') AND (@obj IS NULL OR c.object_id=@obj) "+
            "ORDER BY c.object_id,c.id";
        using var cmd=new SqlCommand(sql,conn);
        cmd.Parameters.Add("@obj",SqlDbType.BigInt).Value=objectId.HasValue?objectId.Value:DBNull.Value;
        using var r=cmd.ExecuteReader();
        var result=new List<PtoContractContext>();
        string S(int i)=>r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)??"";
        string First(params string[] values)=>values.FirstOrDefault(v=>!string.IsNullOrWhiteSpace(v))??"";
        while(r.Read())
        {
            var row=new PtoContractContext(SqlInteger.ReadInt64(r,0),SqlInteger.ReadInt64(r,1),
                S(2),S(3),S(4),S(5),S(6));
            if(legal)
            {
                var agreement=r.IsDBNull(8)?null:new PtoContractContext.Legal(SqlInteger.ReadInt64(r,8),S(9),
                    r.IsDBNull(10)?"":Convert.ToDateTime(r.GetValue(10),CultureInfo.InvariantCulture)
                        .ToString("dd.MM.yyyy",CultureInfo.InvariantCulture),
                    First(S(11),S(12)),First(S(13),S(14),S(15)));
                row=row with {LegalRequired=true,AnnexNumber=S(7),Agreement=agreement};
            }
            result.Add(row);
        }
        return result;
    }
}
