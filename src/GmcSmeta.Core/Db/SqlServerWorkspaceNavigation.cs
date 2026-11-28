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
        public long? AgreementId { get; init; }
        public string KindTitle => Kind switch
        {
            "object" => "Объект", "agreement" => "Юридический договор",
            "contract" => "КП / приложение / ДС", _ => "Позиция"
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
        public long AgreementUses { get; init; }
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
        var hits = new List<SearchHit>(240);
        using var conn = await SqlServerDatabase.OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);

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
                    reader.GetString(1), SqlFlag.Read(reader, 2) ? "Архив" : ""));
            }
        }

        if (words.Length == 0) return hits;

        // The independent legal card is searchable even before its first annex.
        // Legacy schemas never reference agreement tables or annex_number.
        var legalEnabled = await AgreementSearchEnabledAsync(conn, cancellationToken).ConfigureAwait(false);
        if (legalEnabled)
            await ReadAgreementHitsAsync(conn, words, hits, cancellationToken).ConfigureAwait(false);

        using (var cmd = new SqlCommand(
            "SELECT TOP (60) c.id,c.object_id,c.kind,c.name,c.number,c.[date],o.name," +
            (legalEnabled ? "COALESCE(c.annex_number,N'') " : "N'' ") +
            "FROM dbo.contracts c JOIN dbo.objects o ON o.id=c.object_id WHERE " +
            MatchSql(legalEnabled ? ["c.name", "c.number", "c.[date]", "o.name", "c.annex_number"] :
                ["c.name", "c.number", "c.[date]", "o.name"], words) +
            " ORDER BY c.id DESC", conn) { CommandTimeout = 15 })
        {
            AddWords(cmd, words);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var kind = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var caption = kind switch { "main" => legalEnabled ? "Приложение" : "Договор", "dop" => "ДС", _ => "КП" };
                var number = reader.IsDBNull(4) ? "" : reader.GetString(4);
                var date = reader.IsDBNull(5) ? "" : reader.GetString(5);
                var name = reader.IsDBNull(3) ? "" : reader.GetString(3);
                var annex = reader.GetString(7);
                hits.Add(new SearchHit("contract", Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture), Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), 0,
                    caption + (kind == "main" && legalEnabled
                        ? (annex.Length > 0 ? " № " + annex : "") + (number.Length > 0 ? " · прежний № " + number : "")
                        : (number.Length > 0 ? " № " + number : "")) +
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

    private static async Task<bool> AgreementSearchEnabledAsync(SqlConnection conn, CancellationToken token)
    {
        using var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'dbo.agreements',N'U') IS NOT NULL " +
            "AND COL_LENGTH(N'dbo.contracts',N'agreement_id') IS NOT NULL " +
            "AND COL_LENGTH(N'dbo.contracts',N'annex_number') IS NOT NULL THEN 1 ELSE 0 END", conn)
            { CommandTimeout = 15 };
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task ReadAgreementHitsAsync(SqlConnection conn, string[] words,
        List<SearchHit> hits, CancellationToken token)
    {
        using var cmd = new SqlCommand(
            "SELECT TOP (60) a.id,a.object_id,a.number,CONVERT(nvarchar(10),a.agreement_date,104),a.subject,o.name " +
            "FROM dbo.agreements a JOIN dbo.objects o ON o.id=a.object_id " +
            "WHERE a.archived=0 AND o.archived=0 AND " +
            MatchSql(["a.number", "CONVERT(nvarchar(10),a.agreement_date,104)", "a.subject",
                "a.customer_full", "a.cust_org", "a.contractor_full", "o.name"], words) +
            " ORDER BY a.id DESC", conn) { CommandTimeout = 15 };
        AddWords(cmd, words);
        using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            string S(int i) => reader.IsDBNull(i) ? "" : reader.GetString(i);
            var id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            hits.Add(new SearchHit("agreement", Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                0, 0, AgreementSearchTitle(id, S(2), S(3), S(4)), S(5)) { AgreementId = id });
        }
    }

    public static string AgreementSearchTitle(long id, string number, string date, string subject) =>
        (string.IsNullOrWhiteSpace(number) ? $"Договор без номера (ID {id})" : "Договор № " + number) +
        (string.IsNullOrWhiteSpace(date) ? "" : " от " + date) +
        (string.IsNullOrWhiteSpace(subject) ? "" : " — " + subject);

    public static IReadOnlyList<CatalogEntry> Catalog(string connectionString, string? query, int skip = 0, int take = 200)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        if (skip < 0 || take is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(take));
        using var conn = SqlServerDatabase.Open(connectionString);
        return ReadCatalogPage(conn, query, skip, take, includeUsageCount: true, token: default);
    }

    /// <summary>One guarded connection for the page and both revision checks.
    /// The picker does not need the usage count; management still displays it.</summary>
    public static (IReadOnlyList<CatalogEntry> Rows, string Revision) CatalogSnapshot(
        string connectionString, string? query, int skip, int take,
        string? expectedRevision = null, bool includeUsageCount = true, CancellationToken token = default)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        if (skip < 0 || take is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(take));
        token.ThrowIfCancellationRequested();
        using var conn = token.CanBeCanceled
            ? SqlServerDatabase.OpenAsync(connectionString, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Open(connectionString);
        string Revision() => token.CanBeCanceled
            ? SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Revision(conn);
        var before = Revision();
        if (string.IsNullOrWhiteSpace(before) || (expectedRevision is not null && before != expectedRevision))
            throw new DBConcurrencyException("Смета или каталог изменились. Обновите данные и повторите поиск.");
        var rows = ReadCatalogPage(conn, query, skip, take, includeUsageCount, token);
        if (before != Revision())
            throw new DBConcurrencyException("Каталог обновлён другим сотрудником. Повторите поиск.");
        token.ThrowIfCancellationRequested();
        return (rows, before);
    }

    private static IReadOnlyList<CatalogEntry> ReadCatalogPage(SqlConnection conn, string? query,
        int skip, int take, bool includeUsageCount, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var words = Words(query);
        using var cmd = new SqlCommand(
            "SELECT c.id,c.name,c.unit,COALESCE(latest.price,0),COALESCE(latest.purchase_price,0)," +
            (includeUsageCount ? "(SELECT COUNT_BIG(*) FROM dbo.positions p WHERE p.catalog_item_id=c.id) " : "CAST(0 AS bigint) ") +
            "FROM dbo.catalog_items c OUTER APPLY " +
            "(SELECT TOP (1) p.price,p.purchase_price FROM dbo.positions p " +
            " WHERE p.catalog_item_id=c.id AND p.price>0 ORDER BY p.id DESC) latest " +
            "WHERE " + MatchSql(["c.name"], words) +
            " ORDER BY c.name,c.id OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY", conn) { CommandTimeout = 30 };
        AddWords(cmd, words);
        cmd.Parameters.Add("@skip", SqlDbType.Int).Value = skip;
        cmd.Parameters.Add("@take", SqlDbType.Int).Value = take;
        using var registration = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        var rows = new List<CatalogEntry>();
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            rows.Add(new CatalogEntry(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? 0m : Convert.ToDecimal(r.GetValue(3), CultureInfo.InvariantCulture),
                r.IsDBNull(4) ? 0m : Convert.ToDecimal(r.GetValue(4), CultureInfo.InvariantCulture),
                Convert.ToInt64(r.GetValue(5), CultureInfo.InvariantCulture)));
        }
        token.ThrowIfCancellationRequested();
        return rows;
    }

    /// <summary>
    /// Python builder.price_history uses currently linked estimate positions, not
    /// the separate price_history cache. This preserves system/quantity and current prices.
    /// </summary>
    public static IReadOnlyList<PriceHistoryEntry> PriceHistory(string connectionString, long catalogId,
        CancellationToken token = default)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        token.ThrowIfCancellationRequested();
        using var conn = token.CanBeCanceled
            ? SqlServerDatabase.OpenAsync(connectionString, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Open(connectionString);
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
        using var registration = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        var rows = new List<PriceHistoryEntry>();
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            rows.Add(new PriceHistoryEntry(
                Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetString(4),
                Convert.ToDecimal(r.GetValue(5), CultureInfo.InvariantCulture),
                Convert.ToDecimal(r.GetValue(6), CultureInfo.InvariantCulture),
                Convert.ToDecimal(r.GetValue(7), CultureInfo.InvariantCulture)));
        }
        token.ThrowIfCancellationRequested();
        return rows;
    }

    public static IReadOnlyList<CompanyEntry> Companies(string connectionString)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        return ReadCompanies(conn);
    }

    // Caller owns the already guarded connection and revision fence.
    public static IReadOnlyList<CompanyEntry> ReadCompanies(SqlConnection conn)
    {
        var legalUses = SqlServerAgreements.Enabled(conn)
            ? "(SELECT COUNT_BIG(*) FROM dbo.agreements WHERE company_id=c.id)"
            : "CAST(0 AS bigint)";
        using var cmd = new SqlCommand(
            "SELECT c.id,c.name,c.address,c.inn_kpp,c.phone,c.email,c.vat_rate," +
            "c.legal_full,c.signer_post,c.signer_name,c.[order]," +
            "(SELECT COUNT_BIG(*) FROM dbo.contracts WHERE company_id=c.id),c.logo," + legalUses + " " +
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
            {
                Logo = r.IsDBNull(12) ? null : (byte[])r.GetValue(12),
                AgreementUses = r.FieldCount > 13 ? SqlInteger.ReadInt64(r, 13) : 0
            };
    }

    public static IReadOnlyList<TagEntry> Tags(string connectionString)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        return ReadTags(conn);
    }

    public static IReadOnlyList<TagEntry> ReadTags(SqlConnection conn)
    {
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
