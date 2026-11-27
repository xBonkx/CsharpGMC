using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Read-only contractual letterhead/signature values for KP printing.
///
/// Separates printable identities from the editable contract/workspace models.
/// </summary>
public static class SqlServerKpRequisites
{
    public sealed record Values(
        IReadOnlyList<string> CompanyHeader, byte[]? CompanyLogo,
        string CompanyName, string CompanySignerPost, string CompanySignerName,
        string Customer, string CustomerPost, string CustomerName,
        string BaseContractNumber, string BaseContractDate)
    {
        // Preserve the print path's historical unrounded double conversion.
        // EstimateDocument.SmrPercent is intentionally rounded to six places.
        public double? RawSmrPercent { get; init; }
    }

    public static Values Read(SqlConnection connection, long documentId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = new SqlCommand(
            "SELECT co.name,co.address,co.inn_kpp,co.phone,co.email,co.logo," +
            "co.signer_post,co.signer_name,o.customer_full,o.customer,o.cust_org,o.cust_post," +
            "o.cust_name,base.number,base.[date],c.smr_percent " +
            "FROM dbo.contracts c JOIN dbo.objects o ON o.id=c.object_id " +
            "LEFT JOIN dbo.companies co ON co.id=c.company_id " +
            "LEFT JOIN dbo.contracts base ON base.id=c.base_contract_id " +
            "AND base.object_id=c.object_id AND base.kind=N'main' " +
            "WHERE c.id=@id", connection);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = documentId;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Документ больше не существует.");
        string S(int i) => r.IsDBNull(i) ? "" :
            Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? "";
        var company = S(0);
        var header = new List<string>();
        if (company.Length > 0) header.Add(company);
        header.AddRange(S(1).Split(new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        for (var i = 2; i <= 4; i++)
            if (S(i).Length > 0) header.Add(S(i));
        var customer = S(8);
        if (customer.Length == 0) customer = S(10);
        if (customer.Length == 0) customer = S(9);
        return new Values(header, r.IsDBNull(5) ? null : (byte[])r.GetValue(5),
            company, S(6), S(7), customer, S(11), S(12), S(13), S(14))
        {
            RawSmrPercent = r.IsDBNull(15) ? null : Convert.ToDouble(r.GetValue(15), CultureInfo.InvariantCulture)
        };
    }
}
