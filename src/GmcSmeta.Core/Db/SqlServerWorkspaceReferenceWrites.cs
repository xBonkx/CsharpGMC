using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Transactional reference updates; no production writes or silent document reassignment.</summary>
public static class SqlServerWorkspaceReferenceWrites
{
    private static readonly string[] RuleKeys =
        ["vat_top", "num_manual", "basis", "ks_dns", "ds_per_act", "round_rows"];

    public static string NormalizeRules(string? json)
    {
        Dictionary<string, bool> parsed;
        try { parsed = JsonSerializer.Deserialize<Dictionary<string, bool>>(json ?? "{}") ?? []; }
        catch (JsonException ex) { throw new ArgumentException("Неверный JSON правил тега.", ex); }
        if (parsed.Keys.Any(key => !RuleKeys.Contains(key, StringComparer.Ordinal)))
            throw new ArgumentException("Неизвестное правило тега.");
        return JsonSerializer.Serialize(RuleKeys.ToDictionary(x => x, x => parsed.GetValueOrDefault(x)));
    }

    public static string TagKey(string name)
    {
        var value = Regex.Replace((name ?? "").Trim().ToLowerInvariant(), "[^a-zа-я0-9]+", "_").Trim('_');
        return value.Length == 0 ? "tag" : value[..Math.Min(24, value.Length)];
    }

    private static void ValidateCompany(SqlServerWorkspaceNavigation.CompanyEntry c)
    {
        if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 255)
            throw new ArgumentException("Название организации обязательно (до 255 символов).");
        if (c.InnKpp.Length > 255 || c.Phone.Length > 255 || c.Email.Length > 255 ||
            c.SignerName.Length > 128 || c.SignerPost.Length > 128)
            throw new ArgumentException("Слишком длинное поле реквизитов.");
        if (c.VatRate is < 0 or > 100)
            throw new ArgumentException("НДС должен быть в диапазоне от 0 до 100 %.");
        if (c.Logo is { Length: > 5_000_000 })
            throw new ArgumentException("Размер логотипа не должен превышать 5 МБ.");
    }

    public static long SaveCompany(string connectionString,
        SqlServerWorkspaceNavigation.CompanyEntry company, string expectedRevision)
    {
        ValidateCompany(company);
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        long id;
        using (var cmd = new SqlCommand(company.Id == 0
            ? "INSERT dbo.companies(name,address,inn_kpp,phone,email,vat_rate,legal_full,signer_post,signer_name,[order],logo) " +
              "OUTPUT INSERTED.id VALUES(@name,@address,@inn,@phone,@email,@vat,@legal,@post,@signer,@order,@logo)"
            : "UPDATE dbo.companies SET name=@name,address=@address,inn_kpp=@inn,phone=@phone,email=@email," +
              "vat_rate=@vat,legal_full=@legal,signer_post=@post,signer_name=@signer,[order]=@order,logo=@logo WHERE id=@id", conn, tx))
        {
            cmd.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = company.Name.Trim();
            cmd.Parameters.Add("@address", SqlDbType.NVarChar, -1).Value = company.Address;
            cmd.Parameters.Add("@inn", SqlDbType.NVarChar, 255).Value = company.InnKpp;
            cmd.Parameters.Add("@phone", SqlDbType.NVarChar, 255).Value = company.Phone;
            cmd.Parameters.Add("@email", SqlDbType.NVarChar, 255).Value = company.Email;
            cmd.Parameters.Add("@vat", SqlDbType.Float).Value = (double)company.VatRate;
            cmd.Parameters.Add("@legal", SqlDbType.NVarChar, -1).Value = company.LegalFull;
            cmd.Parameters.Add("@post", SqlDbType.NVarChar, 128).Value = company.SignerPost;
            cmd.Parameters.Add("@signer", SqlDbType.NVarChar, 128).Value = company.SignerName;
            cmd.Parameters.Add("@order", SqlDbType.Int).Value = company.Order;
            cmd.Parameters.Add("@logo", SqlDbType.VarBinary, -1).Value =
                company.Logo is null ? DBNull.Value : company.Logo;
            if (company.Id == 0)
                id = Convert.ToInt64(cmd.ExecuteScalar());
            else
            {
                cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = company.Id;
                if (cmd.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Организация больше не существует.");
                id = company.Id;
            }
        }
        Log(conn, tx, "company", id, company.Id == 0 ? "create" : "update", company.Name);
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
        return id;
    }

    public static void DeleteCompany(string connectionString, long id, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        string name;
        using (var cmd = new SqlCommand(
            "SELECT name FROM dbo.companies WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            name = Convert.ToString(cmd.ExecuteScalar()) ??
                throw new ArgumentException("Организация не найдена.");
        }
        using (var check = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE company_id=@id", conn, tx))
        {
            check.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            if (Convert.ToInt64(check.ExecuteScalar()) > 0)
                throw new InvalidOperationException("Организация используется документами. Сначала переназначьте их вручную.");
        }
        using (var cmd = new SqlCommand("DELETE dbo.companies WHERE id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            if (cmd.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Организация уже удалена.");
        }
        Log(conn, tx, "company", id, "delete", name);
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
    }

    public static long SaveTag(string connectionString,
        SqlServerWorkspaceNavigation.TagEntry tag, string expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(tag.Name) || tag.Name.Length > 120 ||
            tag.Note.Length > 16000)
            throw new ArgumentException("Название тега обязательно (до 120 символов); примечание — до 16000.");
        var rules = NormalizeRules(tag.Rules);
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        long id;
        if (tag.Id == 0)
        {
            var basis = TagKey(tag.Name);
            var key = basis;
            for (var n = 2; ; n++)
            {
                using var check = new SqlCommand(
                    "SELECT COUNT_BIG(*) FROM dbo.tags WITH (UPDLOCK,HOLDLOCK) WHERE [key]=@key", conn, tx);
                check.Parameters.Add("@key", SqlDbType.NVarChar, 32).Value = key;
                if (Convert.ToInt64(check.ExecuteScalar()) == 0) break;
                var suffix = "_" + n;
                key = basis[..Math.Min(basis.Length, 32 - suffix.Length)] + suffix;
            }
            using var cmd = new SqlCommand(
                "INSERT dbo.tags([key],name,rules,note) OUTPUT INSERTED.id VALUES(@key,@name,@rules,@note)", conn, tx);
            cmd.Parameters.Add("@key", SqlDbType.NVarChar, 32).Value = key;
            cmd.Parameters.Add("@name", SqlDbType.NVarChar, 120).Value = tag.Name.Trim();
            cmd.Parameters.Add("@rules", SqlDbType.NVarChar, -1).Value = rules;
            cmd.Parameters.Add("@note", SqlDbType.NVarChar, -1).Value = tag.Note;
            id = Convert.ToInt64(cmd.ExecuteScalar());
        }
        else
        {
            // Tag rules drive estimate calculations. Never silently change rules of a
            // signed document: its accepted financial totals must remain immutable.
            using (var current = new SqlCommand(
                "SELECT [key],rules FROM dbo.tags WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
            {
                current.Parameters.Add("@id", SqlDbType.BigInt).Value = tag.Id;
                using var reader = current.ExecuteReader();
                if (!reader.Read()) throw new DBConcurrencyException("Тег больше не существует.");
                var key = reader.GetString(0);
                var currentRules = NormalizeRules(reader.IsDBNull(1) ? "{}" : reader.GetString(1));
                reader.Close();
                if (!string.Equals(currentRules, rules, StringComparison.Ordinal))
                {
                    using var check = new SqlCommand(
                        "SELECT COUNT_BIG(*) FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) " +
                        "WHERE form=@key AND locked=1", conn, tx);
                    check.Parameters.Add("@key", SqlDbType.NVarChar, 32).Value = key;
                    if (Convert.ToInt64(check.ExecuteScalar()) > 0)
                        throw new InvalidOperationException(
                            "Правила тега используются подписанными документами. Нельзя менять расчёты подписанного КП.");
                }
            }
            using var cmd = new SqlCommand(
                "UPDATE dbo.tags SET name=@name,rules=@rules,note=@note WHERE id=@id", conn, tx);
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = tag.Id;
            cmd.Parameters.Add("@name", SqlDbType.NVarChar, 120).Value = tag.Name.Trim();
            cmd.Parameters.Add("@rules", SqlDbType.NVarChar, -1).Value = rules;
            cmd.Parameters.Add("@note", SqlDbType.NVarChar, -1).Value = tag.Note;
            if (cmd.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Тег больше не существует.");
            id = tag.Id;
        }
        Log(conn, tx, "tag", id, tag.Id == 0 ? "create" : "update", tag.Name);
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
        return id;
    }

    public static void DeleteTag(string connectionString, long id, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        string key;
        string name;
        using (var cmd = new SqlCommand(
            "SELECT [key],name FROM dbo.tags WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Тег не найден.");
            key = r.GetString(0);
            name = r.GetString(1);
        }
        using (var cmd = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE form=@key", conn, tx))
        {
            cmd.Parameters.Add("@key", SqlDbType.NVarChar, 32).Value = key;
            if (Convert.ToInt64(cmd.ExecuteScalar()) != 0)
                throw new InvalidOperationException("Тег используется документами. Сначала измените свойства этих документов.");
        }
        using (var cmd = new SqlCommand("UPDATE dbo.objects SET form=N'' WHERE form=@key", conn, tx))
        {
            cmd.Parameters.Add("@key", SqlDbType.NVarChar, 32).Value = key;
            cmd.ExecuteNonQuery();
        }
        using (var cmd = new SqlCommand("DELETE dbo.tags WHERE id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            if (cmd.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Тег уже удалён.");
        }
        Log(conn, tx, "tag", id, "delete", name);
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
    }

    private static void Log(SqlConnection conn, SqlTransaction tx, string entity,
        long id, string action, string details)
    {
        using var cmd = new SqlCommand(
            "INSERT dbo.change_log(ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES(SYSDATETIME(),@user,@entity,@id,@action,@details,NULL,NULL)", conn, tx);
        cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value =
            Environment.UserName[..Math.Min(Environment.UserName.Length, 64)];
        cmd.Parameters.Add("@entity", SqlDbType.NVarChar, 32).Value = entity;
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        cmd.Parameters.Add("@action", SqlDbType.NVarChar, 32).Value = action;
        cmd.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = details;
        cmd.ExecuteNonQuery();
    }
}
