using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Первичное наполнение базы — как в Python `_open_db`: две свои фирмы, теги заказчиков,
/// логотипы и отметка версии схемы. Все шаги привязаны к прежней версии базы, поэтому
/// на уже заполненной базе ничего не меняется.
/// </summary>
public static class Seeding
{
    private sealed record DefaultCompany(
        string Name, string Address, string InnKpp, string Phone, string Email,
        int Order, double VatRate);

    private static readonly DefaultCompany[] DefaultCompanies =
    {
        new("ООО «КОНСОЛЬ»", "г. Владивосток, ул. Выселковая 118",
            "ИНН  2539076349 / КПП  53901001",
            "тел./факс: 8(423) 2701-306, 277-19-06", "e-mail: konsol@inbox.ru", 0, 5.0),
        new("ООО «ГМЦ СТРОЙ»",
            "690911, Приморский край, г. Владивосток,\nул. Адмирала Горшкова, д. 85, кв. 28",
            "ИНН  2537101261 / КПП  253601001",
            "тел./факс: 8(423) 2701-306, 277-19-06", "e-mail: konsol@inbox.ru", 1, 22.0),
    };

    /// <summary>Правила тегов: те же ключи и тексты, что в Python TAG_RULES / BUILTIN_TAGS.</summary>
    private static readonly (string Key, string Name, Dictionary<string, bool> Rules)[] BuiltinTags =
    {
        ("dns", "ДНС", new Dictionary<string, bool>
        {
            ["vat_top"] = true, ["num_manual"] = true, ["basis"] = true,
            ["ks_dns"] = true, ["ds_per_act"] = true, ["round_rows"] = true,
        }),
        ("r4", "Р4", new Dictionary<string, bool> { ["basis"] = true, ["num_manual"] = true }),
    };

    /// <summary>Заполнить базу так же, как это делает Python при первом открытии.</summary>
    public static void Run(SqliteConnection conn)
    {
        var version = ReadSchemaVersion(conn);   // нет строки — значит база новая, версия 0

        if (version < 2 && RowWriter.Count(conn, "companies") == 0)
            foreach (var c in DefaultCompanies)
                RowWriter.Insert(conn, "companies", new Dictionary<string, object?>
                {
                    ["name"] = c.Name,
                    ["address"] = c.Address,
                    ["inn_kpp"] = c.InnKpp,
                    ["phone"] = c.Phone,
                    ["email"] = c.Email,
                    ["order"] = (long)c.Order,
                    ["vat_rate"] = c.VatRate,
                });

        if (version < 6)
            foreach (var (id, name) in CompanyNames(conn))
                if (name.Contains("гмц", StringComparison.OrdinalIgnoreCase) && !HasSigner(conn, id))
                    RowWriter.Update(conn, "companies", id, new Dictionary<string, object?>
                    {
                        ["signer_post"] = "Генеральный директор",
                        ["signer_name"] = "Цурупий А.В.",
                        ["legal_full"] =
                            "ООО «ГМЦ СТРОЙ» Юр.адрес: 690911, Приморский край, г.о. Владивостокский, " +
                            "г. Владивосток, ул. Адмирала Горшкова, д. 85, кв. 28",
                    });

        if (version < 5)
            foreach (var (id, name, vat) in CompaniesForVat(conn))
            {
                if (name.Contains("консоль", StringComparison.OrdinalIgnoreCase))
                    RowWriter.Update(conn, "companies", id, new Dictionary<string, object?> { ["vat_rate"] = 5.0 });
                else if (vat is null)
                    RowWriter.Update(conn, "companies", id, new Dictionary<string, object?> { ["vat_rate"] = 22.0 });
            }

        if (version < 4)
        {
            var logo = Asset("logo_konsol.jpg");
            if (logo is not null)
                foreach (var (id, hasLogo) in CompaniesForLogo(conn))
                    if (!hasLogo)
                        RowWriter.Update(conn, "companies", id,
                            new Dictionary<string, object?> { ["logo"] = logo });
        }

        if (version < 26 && RowWriter.Count(conn, "tags") == 0)
            foreach (var tag in BuiltinTags)
                RowWriter.Insert(conn, "tags", new Dictionary<string, object?>
                {
                    ["key"] = tag.Key,
                    ["name"] = tag.Name,
                    ["rules"] = JsonSerializer.Serialize(tag.Rules),
                });

        if (!TagExists(conn, "r4"))
        {
            var tag = BuiltinTags.First(t => t.Key == "r4");
            RowWriter.Insert(conn, "tags", new Dictionary<string, object?>
            {
                ["key"] = tag.Key,
                ["name"] = tag.Name,
                ["rules"] = JsonSerializer.Serialize(tag.Rules),
            });
        }

        UpdateGmcLogo(conn);

        // Версия схемы не уменьшается: C#-версия знает 28 и оставляет базу на 28.
        WriteSchemaVersion(conn, Math.Max(version, Schema.Version));
    }

    /// <summary>Одноразовая замена поставленного логотипа ГМЦ на отдельный знак (как в Python).</summary>
    private static void UpdateGmcLogo(SqliteConnection conn)
    {
        const string key = "gmc_symbol_logo_v1";
        if (Database.GetMeta(conn, key) is not null) return;

        var original = Asset("logo_konsol.jpg");
        var symbol = Asset("logo_gmc.png");
        if (original is null || symbol is null) return;

        foreach (var (id, name, logo) in CompaniesWithLogo(conn))
        {
            var normalized = NormalizeName(name);
            if (normalized is "гмцстрой" or "ооогмцстрой" && logo is not null && logo.SequenceEqual(original))
                RowWriter.Update(conn, "companies", id, new Dictionary<string, object?> { ["logo"] = symbol });
        }
        Database.SetMeta(conn, key, "1");
    }

    private static string NormalizeName(string name) =>
        new string(name.ToLowerInvariant().Where(ch =>
            ch is >= 'а' and <= 'я' or >= 'a' and <= 'z' or >= '0' and <= '9').ToArray());

    private static byte[]? Asset(string name)
    {
        var asm = typeof(Seeding).Assembly;
        using var stream = asm.GetManifestResourceStream("GmcSmeta.Core.Assets." + name);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static int ReadSchemaVersion(SqliteConnection conn)
    {
        var text = Database.GetMeta(conn, "schema_version");
        if (text is null)
        {
            Database.SetMeta(conn, "schema_version", "0");
            return 0;
        }
        return int.TryParse(text, out var v) ? v : 0;
    }

    private static void WriteSchemaVersion(SqliteConnection conn, int version) =>
        Database.SetMeta(conn, "schema_version", version.ToString());

    private static List<(long Id, string Name)> CompanyNames(SqliteConnection conn)
    {
        var list = new List<(long, string)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM companies";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1)));
        return list;
    }

    private static List<(long Id, string Name, double? Vat)> CompaniesForVat(SqliteConnection conn)
    {
        var list = new List<(long, string, double?)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, vat_rate FROM companies";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetDouble(2)));
        return list;
    }

    private static List<(long Id, bool HasLogo)> CompaniesForLogo(SqliteConnection conn)
    {
        var list = new List<(long, bool)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, logo IS NOT NULL FROM companies";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetInt64(1) != 0));
        return list;
    }

    private static List<(long Id, string Name, byte[]? Logo)> CompaniesWithLogo(SqliteConnection conn)
    {
        var list = new List<(long, string, byte[]?)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, logo FROM companies";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? null : (byte[])r.GetValue(2)));
        return list;
    }

    private static bool HasSigner(SqliteConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT signer_name FROM companies WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var value = cmd.ExecuteScalar();
        return value is string s && s.Length > 0;
    }

    private static bool TagExists(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM tags WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() is not null;
    }
}
