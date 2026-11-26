using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Read-only SQL Server queries for the top-level workspace. Each call owns its connection,
/// so it cannot concurrently use the MainWindow reader while estimates are being edited.
/// </summary>
public static class SqlServerWorkspaceNavigation
{
    public sealed record SearchHit(string Kind, long ObjectId, long ContractId, long PositionId,
        string Title, string Context)
    {
        public string KindTitle => Kind switch
        {
            "object" => "Объект", "contract" => "КП / договор", _ => "Позиция"
        };
        public string Display => Title + (Context.Length > 0 ? "   ·   " + Context : "");
    }

    public sealed record CatalogEntry(long Id, string Name, string Unit,
        decimal Price, decimal PurchasePrice, long Uses)
    {
        public string PriceText => Price.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
        public string PurchaseText => PurchasePrice.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
    }

    public sealed record PriceHistoryEntry(long Id, string Object, string Contract,
        string Date, string System, decimal Qty, decimal Price, decimal PurchasePrice)
    {
        public string QtyText => Qty.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"));
        public string PriceText => Price.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
        public string PurchaseText => PurchasePrice.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
    }

    public sealed record CompanyEntry(long Id, string Name, string Address, string InnKpp,
        string Phone, string Email, decimal VatRate, string LegalFull,
        string SignerPost, string SignerName, int Order, long Uses)
    {
        public byte[]? Logo { get; init; }
    }

    public sealed record TagEntry(long Id, string Key, string Name, string Rules,
        string Note, long Documents, long Objects);

    /// <summary>Literal LIKE pattern: %, _, [, and escape marker are not interpreted as wildcards.</summary>
    public static string LikePattern(string word) => "%" +
        (word ?? "").Replace("~", "~~", StringComparison.Ordinal)
            .Replace("%", "~%", StringComparison.Ordinal)
            .Replace("_", "~_", StringComparison.Ordinal)
            .Replace("[", "~[", StringComparison.Ordinal) + "%";

    public static string[] Words(string? query) =>
        (query ?? "").Trim().Replace(',', '.')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 0).Take(6)
            .Select(w => w.Length <= 80 ? w : w[..80]).ToArray();

    // All column names and generated parameter names below are hard-coded by callers.
    private static string MatchSql(string[] columns, string[] words)
    {
        if (words.Length == 0) return "1=1";
        return string.Join(" AND ", Enumerable.Range(0, words.Length).Select(i =>
            "(" + string.Join(" OR ", columns.Select(col =>
                "LOWER(COALESCE(" + col + ",N'')) LIKE LOWER(@w" + i + ") ESCAPE N'~'")) + ")"));
    }

    private static void AddWords(SqlCommand cmd, string[] words)
    {
        for (var i = 0; i < words.Length; i++)
            cmd.Parameters.Add("@w" + i, SqlDbType.NVarChar, 256).Value = LikePattern(words[i]);
    }

    /// <summary>Bounded, cancellable database search. No materialized all-position cache.</summary>
    public static async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string connectionString, string? query, CancellationToken cancellationToken = default)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        var words = Words(query);
        var hits = new List<SearchHit>(180);
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        using (var cmd = new SqlCommand(
            "SELECT TOP (60) id,name,archived FROM dbo.objects WHERE " +
            (words.Length == 0 ? "archived=0" : MatchSql(["name", "customer", "address"], words)) +
            " ORDER BY archived,name,id", conn) { CommandTimeout = 15 })
        {
            AddWords(cmd, words);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hits.Add(new SearchHit("object", Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), 0, 0,
                    reader.GetString(1), reader.GetBoolean(2) ? "Архив" : ""));
            }
        }

        if (words.Length == 0) return hits;

        using (var cmd = new SqlCommand(
            "SELECT TOP (60) c.id,c.object_id,c.kind,c.name,c.number,c.[date],o.name " +
            "FROM dbo.contracts c JOIN dbo.objects o ON o.id=c.object_id WHERE " +
            MatchSql(["c.name", "c.number", "c.[date]", "o.name"], words) +
            " ORDER BY c.id DESC", conn) { CommandTimeout = 15 })
        {
            AddWords(cmd, words);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var kind = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var caption = kind switch { "main" => "Договор", "dop" => "ДС", _ => "КП" };
                var number = reader.IsDBNull(4) ? "" : reader.GetString(4);
                var date = reader.IsDBNull(5) ? "" : reader.GetString(5);
                var name = reader.IsDBNull(3) ? "" : reader.GetString(3);
                hits.Add(new SearchHit("contract", Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture), Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), 0,
                    caption + (number.Length > 0 ? " № " + number : "") +
                    (date.Length > 0 ? " от " + date : "") +
                    (name.Length > 0 ? " — " + name : ""),
                    reader.GetString(6)));
            }
        }

        using (var cmd = new SqlCommand(
            "SELECT TOP (60) p.id,p.name,s.contract_id,c.object_id,s.name,c.name,o.name " +
            "FROM dbo.positions p JOIN dbo.systems s ON s.id=p.system_id " +
            "JOIN dbo.contracts c ON c.id=s.contract_id JOIN dbo.objects o ON o.id=c.object_id " +
            "WHERE NULLIF(LTRIM(RTRIM(p.name)),N'') IS NOT NULL AND " +
            MatchSql(["p.name"], words) + " ORDER BY p.id DESC", conn) { CommandTimeout = 20 })
        {
            AddWords(cmd, words);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                hits.Add(new SearchHit("position", Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture), Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetString(1),
                    reader.GetString(6) + " › " + reader.GetString(5) + " › " + reader.GetString(4)));
        }
        return hits;
    }

    public static IReadOnlyList<CatalogEntry> Catalog(string connectionString, string? query, int skip = 0, int take = 200)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        if (skip < 0 || take is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(take));
        using var conn = SqlServerDatabase.Open(connectionString);
        var words = Words(query);
        using var cmd = new SqlCommand(
            "SELECT c.id,c.name,c.unit,COALESCE(latest.price,0),COALESCE(latest.purchase_price,0)," +
            "(SELECT COUNT_BIG(*) FROM dbo.positions p WHERE p.catalog_item_id=c.id) " +
            "FROM dbo.catalog_items c OUTER APPLY " +
            "(SELECT TOP (1) p.price,p.purchase_price FROM dbo.positions p " +
            " WHERE p.catalog_item_id=c.id AND p.price>0 ORDER BY p.id DESC) latest " +
            "WHERE " + MatchSql(["c.name"], words) +
            " ORDER BY c.name,c.id OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY", conn) { CommandTimeout = 30 };
        AddWords(cmd, words);
        cmd.Parameters.Add("@skip", SqlDbType.Int).Value = skip;
        cmd.Parameters.Add("@take", SqlDbType.Int).Value = take;
        using var r = cmd.ExecuteReader();
        var rows = new List<CatalogEntry>();
        while (r.Read())
            rows.Add(new CatalogEntry(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? 0m : Convert.ToDecimal(r.GetValue(3), CultureInfo.InvariantCulture),
                r.IsDBNull(4) ? 0m : Convert.ToDecimal(r.GetValue(4), CultureInfo.InvariantCulture),
                Convert.ToInt64(r.GetValue(5), CultureInfo.InvariantCulture)));
        return rows;
    }

    /// <summary>
    /// Python builder.price_history uses currently linked estimate positions, not
    /// the separate price_history cache. This preserves system/quantity and current prices.
    /// </summary>
    public static IReadOnlyList<PriceHistoryEntry> PriceHistory(string connectionString, long catalogId)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var cmd = new SqlCommand(
            "SELECT TOP (500) p.id,COALESCE(o.name,N'')," +
            "CONCAT(CASE c.kind WHEN N'main' THEN N'Договор' WHEN N'dop' THEN N'ДС' ELSE N'КП' END," +
            " CASE WHEN COALESCE(c.number,N'')=N'' THEN N'' ELSE N' № ' + c.number END," +
            " CASE WHEN COALESCE(c.name,N'')=N'' THEN N'' ELSE N' — ' + c.name END)," +
            "COALESCE(NULLIF(c.[date],N''),CONVERT(NVARCHAR(10),c.created_at,104))," +
            "COALESCE(s.name,N''),p.qty,COALESCE(p.price,0),COALESCE(p.purchase_price,0) " +
            "FROM dbo.positions p JOIN dbo.systems s ON s.id=p.system_id " +
            "JOIN dbo.contracts c ON c.id=s.contract_id JOIN dbo.objects o ON o.id=c.object_id " +
            "WHERE p.catalog_item_id=@id ORDER BY p.id DESC", conn) { CommandTimeout = 30 };
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = catalogId;
        using var r = cmd.ExecuteReader();
        var rows = new List<PriceHistoryEntry>();
        while (r.Read())
            rows.Add(new PriceHistoryEntry(
                Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetString(4),
                Convert.ToDecimal(r.GetValue(5), CultureInfo.InvariantCulture),
                Convert.ToDecimal(r.GetValue(6), CultureInfo.InvariantCulture),
                Convert.ToDecimal(r.GetValue(7), CultureInfo.InvariantCulture)));
        return rows;
    }

    public static IReadOnlyList<CompanyEntry> Companies(string connectionString)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var cmd = new SqlCommand(
            "SELECT c.id,c.name,c.address,c.inn_kpp,c.phone,c.email,c.vat_rate," +
            "c.legal_full,c.signer_post,c.signer_name,c.[order]," +
            "(SELECT COUNT_BIG(*) FROM dbo.contracts WHERE company_id=c.id),c.logo " +
            "FROM dbo.companies c ORDER BY c.[order],c.id", conn);
        using var r = cmd.ExecuteReader();
        var rows = new List<CompanyEntry>();
        while (r.Read())
            rows.Add(ReadCompany(r));
        return rows;
    }

    // The existing SQL Server databases may expose [order] as INT or BIGINT.
    internal static CompanyEntry ReadCompany(IDataRecord r)
    {
        string S(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
        return new CompanyEntry(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), S(1), S(2), S(3), S(4), S(5),
            r.IsDBNull(6) ? 0 : Convert.ToDecimal(r.GetValue(6), CultureInfo.InvariantCulture),
            S(7), S(8), S(9), r.IsDBNull(10) ? 0 : Convert.ToInt32(r.GetValue(10), CultureInfo.InvariantCulture),
            Convert.ToInt64(r.GetValue(11), CultureInfo.InvariantCulture))
            { Logo = r.IsDBNull(12) ? null : (byte[])r.GetValue(12) };
    }

    public static IReadOnlyList<TagEntry> Tags(string connectionString)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var cmd = new SqlCommand(
            "SELECT t.id,t.[key],t.name,t.rules,t.note," +
            "(SELECT COUNT_BIG(*) FROM dbo.contracts WHERE form=t.[key])," +
            "(SELECT COUNT_BIG(*) FROM dbo.objects WHERE form=t.[key]) FROM dbo.tags t ORDER BY t.name,t.id", conn);
        using var r = cmd.ExecuteReader();
        var rows = new List<TagEntry>();
        while (r.Read())
        {
            string S(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
            rows.Add(new TagEntry(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), S(1), S(2), S(3), S(4), Convert.ToInt64(r.GetValue(5), CultureInfo.InvariantCulture), Convert.ToInt64(r.GetValue(6), CultureInfo.InvariantCulture)));
        }
        return rows;
    }
}
