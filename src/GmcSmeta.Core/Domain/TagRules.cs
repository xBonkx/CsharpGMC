using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Правила заказчика (тег формы документа) — как db.tag_rules в Python-версии.
/// Хранятся в таблице tags полем rules (JSON), читаются один раз в память.
/// Встроенные: dns (НДС сверху, их КС), r4 (нумерация вручную).
/// </summary>
public sealed class TagRules
{
    private readonly Dictionary<string, HashSet<string>> _rules = new(StringComparer.Ordinal);

    public static TagRules Load(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT [key], rules FROM tags";
        using var r = cmd.ExecuteReader();
        return Read(r);
    }

    public static TagRules Load(Microsoft.Data.SqlClient.SqlConnection conn, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT [key], rules FROM tags";
        using var cancellation = GmcSmeta.Core.Db.SqlReadCancellation.Register(cmd, token);
        using var reader = GmcSmeta.Core.Db.SqlReadCancellation.ExecuteReader(cmd, token);
        var result = new TagRules();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            result.ReadCurrent(reader);
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    // Object cards need names and exactly the same rule snapshot for both
    // estimate and finance projections. Read both in one SQL Server command.
    internal static TagRules LoadWithNames(Microsoft.Data.SqlClient.SqlConnection conn,
        out Dictionary<string, string> names, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT [key], rules, name FROM tags";
        using var registration = GmcSmeta.Core.Db.SqlReadCancellation.Register(cmd, token);
        using var reader = GmcSmeta.Core.Db.SqlReadCancellation.ExecuteReader(cmd, token);
        var result = new TagRules();
        names = new Dictionary<string, string>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            result.ReadCurrent(reader);
            var key = reader.IsDBNull(0) ? "" : reader.GetString(0);
            names[key] = reader.IsDBNull(2) ? "" : reader.GetString(2);
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Читает правила из уже открытого reader'а (батч загрузки сметы).</summary>
    public static TagRules Read(DbDataReader r)
    {
        var result = new TagRules();
        while (r.Read()) result.ReadCurrent(r);
        return result;
    }

    internal static async Task<TagRules> ReadSqlAsync(DbDataReader r, CancellationToken token)
    {
        var result = new TagRules();
        while (await r.ReadAsync(token).ConfigureAwait(false)) result.ReadCurrent(r);
        return result;
    }

    private void ReadCurrent(DbDataReader r)
    {
        var key = r.IsDBNull(0) ? "" : r.GetString(0);
        if (key.Length == 0) return;
        var json = r.IsDBNull(1) ? "" : r.GetString(1);
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (json.Length > 0)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var p in doc.RootElement.EnumerateObject())
                        if (p.Value.ValueKind == System.Text.Json.JsonValueKind.True)
                            set.Add(p.Name);
            }
            catch (System.Text.Json.JsonException)
            {
                // битые правила = нет правил; так же ведёт себя Python-версия
            }
        }
        _rules[key] = set;
    }

    /// <summary>Включено ли правило у формы документа («vat_top», «round_rows», «num_manual»…).</summary>
    public bool Has(string form, string rule) =>
        form.Length > 0 && _rules.TryGetValue(form, out var set) && set.Contains(rule);
}
