using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// SQL Server pilot. Unlike the SQLite writer, updates here lock meta.rev and the position
/// inside one SERIALIZABLE transaction. qty/price/purchase_price/work_price (with formulas and catalog) plus name/unit/note are enabled;
/// other edit commands must be ported together with their related SQL Server tables.
/// </summary>
public static partial class SqlServerDatabase
{
    private static readonly JsonSerializerOptions FormulaOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public sealed record SaveResult(bool Saved, string? Error, string Revision)
    {
        public static SaveResult Fail(string error, string revision) => new(false, error, revision);
    }

    // Revalidate schema on each logical open. A release can change it during the lifetime of a client.

    public static SqlConnection Open(string connectionString)
    {
        var builder = BuildGuardedConnectionString(connectionString);
        var conn = new SqlConnection(builder.ConnectionString);
        try
        {
            // Open itself may throw (login, timeout, network). Own the connection
            // until all validation has succeeded and ownership passes to the caller.
            conn.Open();
            GmcSmeta.Core.Demo.DemoMode.VerifyDatabase(conn);
            SqlSchemaCompatibility.RejectProduction(conn.Database, builder.DataSource);
            if (SqlSchemaCompatibility.IsLocalTestCutover(conn.Database, builder.DataSource))
            {
                using var identity = conn.CreateCommand();
                identity.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ComputerNamePhysicalNetBIOS'))";
                var machine = Convert.ToString(identity.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (!string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Исключение Cutover действует только на локальном SQL Server этого компьютера.");
            }
            var approvedCutover = SqlSchemaCompatibility.IsApprovedCutover(conn.Database, builder.DataSource);
            if (approvedCutover)
            {
                using var identity = conn.CreateCommand();
                identity.CommandText = CutoverIdentitySql;
                identity.Parameters.Add("@transport", SqlDbType.NVarChar, 30).Value =
                    SqlSchemaCompatibility.IsNetworkCutover(conn.Database, builder.DataSource) ? "TCP" : "Shared memory";
                if (Convert.ToInt32(identity.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                    throw new InvalidOperationException("Подключение открыло другой экземпляр SQL Server или транспорт.");
            }
            // Preview must be verified on EVERY connection; never cache a read-only check.
            var preview = SqlSchemaCompatibility.IsPreviewDatabase(conn.Database)
                || SqlSchemaCompatibility.IsOrdinarySchema31TestDatabase(conn.Database, builder.DataSource)
                || SqlSchemaCompatibility.IsAgreementTestDatabase(conn.Database, builder.DataSource);
            using var cmd = conn.CreateCommand();
            if (preview || approvedCutover || SqlSchemaCompatibility.OrdinarySchema31Enabled)
            {
                cmd.CommandText = PreviewValidationSql;
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) throw new InvalidOperationException("Не удалось проверить тестовую базу.");
                ValidatePreviewResult(reader, conn.Database, builder.DataSource);
            }
            else
            {
                cmd.CommandText = SchemaVersionSql;
                var version = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                SqlSchemaCompatibility.Validate(version, conn.Database, null, false, builder.DataSource);
            }
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    public static string Revision(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT [value] FROM dbo.meta WHERE [key] = N'rev'";
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>Read the same global revision, with cancellation of the SQL command.</summary>
    public static async Task<string> RevisionAsync(DbConnection conn, System.Threading.CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT [value] FROM dbo.meta WHERE [key] = N'rev'";
        var value = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>Open a guarded SQL connection with cancellation through login and schema checks.</summary>
    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var builder = BuildGuardedConnectionString(connectionString);
        var conn = new SqlConnection(builder.ConnectionString);
        try
        {
            // Open itself may throw (login, timeout, network). Own the connection
            // until all validation has succeeded and ownership passes to the caller.
            await conn.OpenAsync(token).ConfigureAwait(false);
            await GmcSmeta.Core.Demo.DemoMode.VerifyDatabaseAsync(conn, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            SqlSchemaCompatibility.RejectProduction(conn.Database, builder.DataSource);
            if (SqlSchemaCompatibility.IsLocalTestCutover(conn.Database, builder.DataSource))
            {
                using var identity = conn.CreateCommand();
                identity.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ComputerNamePhysicalNetBIOS'))";
                var machine = Convert.ToString(await identity.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
                if (!string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Исключение Cutover действует только на локальном SQL Server этого компьютера.");
            }
            var approvedCutover = SqlSchemaCompatibility.IsApprovedCutover(conn.Database, builder.DataSource);
            if (approvedCutover)
            {
                using var identity = conn.CreateCommand();
                identity.CommandText = CutoverIdentitySql;
                identity.Parameters.Add("@transport", SqlDbType.NVarChar, 30).Value =
                    SqlSchemaCompatibility.IsNetworkCutover(conn.Database, builder.DataSource) ? "TCP" : "Shared memory";
                if (Convert.ToInt32(await identity.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
                    throw new InvalidOperationException("Подключение открыло другой экземпляр SQL Server или транспорт.");
            }
            // Preview must be verified on EVERY connection; never cache a read-only check.
            var preview = SqlSchemaCompatibility.IsPreviewDatabase(conn.Database)
                || SqlSchemaCompatibility.IsOrdinarySchema31TestDatabase(conn.Database, builder.DataSource)
                || SqlSchemaCompatibility.IsAgreementTestDatabase(conn.Database, builder.DataSource);
            token.ThrowIfCancellationRequested();
            using var cmd = conn.CreateCommand();
            if (preview || approvedCutover || SqlSchemaCompatibility.OrdinarySchema31Enabled)
            {
                cmd.CommandText = PreviewValidationSql;
                using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Не удалось проверить тестовую базу.");
                token.ThrowIfCancellationRequested();
                ValidatePreviewResult(reader, conn.Database, builder.DataSource);
            }
            else
            {
                cmd.CommandText = SchemaVersionSql;
                var version = Convert.ToString(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
                token.ThrowIfCancellationRequested();
                SqlSchemaCompatibility.Validate(version, conn.Database, null, false, builder.DataSource);
            }
            token.ThrowIfCancellationRequested();
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private static SqlConnectionStringBuilder BuildGuardedConnectionString(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "GMC-Smeta C# Pilot",
            ConnectTimeout = 5,
        };
        GmcSmeta.Core.Demo.DemoMode.ValidateConnection(builder, Environment.GetCommandLineArgs().Skip(1));
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog) ||
            builder.InitialCatalog.Equals("master", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выбери конкретную тестовую базу SQL Server, не master.");
        SqlSchemaCompatibility.RejectProduction(builder.InitialCatalog, builder.DataSource);
        if (SqlSchemaCompatibility.IsNetworkCutover(builder.InitialCatalog, builder.DataSource))
        {
            if (!builder.IntegratedSecurity || builder.TrustServerCertificate ||
                !string.IsNullOrWhiteSpace(builder.ServerCertificate) ||
                !string.IsNullOrWhiteSpace(builder.HostNameInCertificate) ||
                builder.Encrypt != SqlConnectionEncryptOption.Mandatory)
                throw new InvalidOperationException("Рабочий SQL Server по сети требует вход Windows и проверяемый TLS.");
            builder.ConnectTimeout = 20;
        }
        if (SqlSchemaCompatibility.IsWriteTestDatabase(builder.InitialCatalog))
            throw new InvalidOperationException("Специальная база теста записи v31 недоступна обычному приложению.");
        return builder;
    }

    private const string CutoverIdentitySql = """
        SELECT CASE WHEN CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')) = N'DIMA\SQLEXPRESS'
                    AND CONVERT(nvarchar(30), CONNECTIONPROPERTY('net_transport')) = @transport
                    THEN 1 ELSE 0 END
        """;

    private const string SchemaVersionSql =
        "SET QUOTED_IDENTIFIER ON; SELECT [value] FROM dbo.meta WHERE [key] = N'schema_version'";

    // Read the version and the existing mandatory compatibility checks in
    // one statement on every logical open. Never cache approval or identity.
    private const string PreviewValidationSql = """
                    SET QUOTED_IDENTIFIER ON;
                    SELECT (SELECT [value] FROM dbo.meta WHERE [key]=N'schema_version'),
                    CONVERT(nvarchar(32), DATABASEPROPERTYEX(DB_NAME(), 'Updateability')),
                    CASE WHEN COL_LENGTH(N'dbo.contracts', N'discount_percent') IS NOT NULL
                          AND COL_LENGTH(N'dbo.acts', N'discount_percent') IS NOT NULL
                          AND COL_LENGTH(N'dbo.acts', N'section_amounts_json') IS NOT NULL
                          AND COL_LENGTH(N'dbo.acts', N'discount_override') IS NOT NULL
                          AND COL_LENGTH(N'dbo.acts', N'manual_total_cents') IS NOT NULL
                          AND ((SELECT CONVERT(nvarchar(32),[value]) FROM dbo.meta WHERE [key]=N'schema_version')<>N'32'
                            OR (OBJECT_ID(N'dbo.agreements',N'U') IS NOT NULL AND OBJECT_ID(N'dbo.agreement_files',N'U') IS NOT NULL
                                AND COL_LENGTH(N'dbo.contracts',N'agreement_id') IS NOT NULL AND COL_LENGTH(N'dbo.agreements',N'contractor_full') IS NOT NULL
                                AND EXISTS(SELECT 1 FROM dbo.meta WHERE [key]=N'agr10_schema_manifest' AND [value]=N'AGR10-expand-1')))
                         THEN 1 ELSE 0 END
                    """
        ;

    internal static void ValidatePreviewResult(IDataRecord reader, string database, string dataSource) =>
        SqlSchemaCompatibility.Validate(
            Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture), database,
            Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture),
            Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) == 1, dataSource);

    /// <summary>Save a single cell, including text and catalog bookkeeping, in one SQL transaction.</summary>
    public static SaveResult SaveCell(string connectionString, long positionId, string field,
                                      string input, string expectedRevision)
    {
        if (field is "area_manual" or "basis")
            return SaveSpecialCell(connectionString, positionId, field, input, expectedRevision);
        var problem = ValidateCellInput(field, input, out var numeric, out var trimmed, out var value);
        if (problem is not null) return SaveResult.Fail(problem, expectedRevision);

        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        using var revCmd = new SqlCommand(
            "SELECT [value] FROM dbo.meta WITH (UPDLOCK, HOLDLOCK) WHERE [key] = N'rev'", conn, tx);
        var currentRev = Convert.ToString(revCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (currentRev is null)
            return SaveResult.Fail("Нет метки ревизии в SQL Server.", expectedRevision);
        if (!string.Equals(currentRev, expectedRevision, StringComparison.Ordinal))
            return SaveResult.Fail("Данные на сервере изменились. Смета будет перечитана; повтори правку.", currentRev);

        var saved = SaveCellCore(conn, tx, positionId, null, field, input, numeric, trimmed, value,
            currentRev, out var changed);
        if (!saved.Saved) return saved;
        if (!changed)
        {
            tx.Commit();
            return saved;
        }
        var identity = $"{Environment.UserName}@{Environment.MachineName}#{Environment.ProcessId}";
        var newRev = $"{identity}|{Guid.NewGuid():N}|{DateTime.Now:HH:mm:ss}";
        using (var updateRev = new SqlCommand(
            "UPDATE dbo.meta SET [value]=@revision WHERE [key]=N'rev'", conn, tx))
        {
            updateRev.Parameters.Add("@revision", SqlDbType.NVarChar, -1).Value = newRev;
            if (updateRev.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Пропала строка ревизии meta.rev.");
        }
        tx.Commit();
        return new SaveResult(true, null, newRev);
    }


    internal static string? ValidateCellInput(string field, string input,
        out bool numeric, out string trimmed, out decimal? value)
    {
        numeric = field is "qty" or "price" or "purchase_price" or "work_price";
        trimmed = (input ?? "").Trim();
        value = null;
        var textual = field is "name" or "unit" or "note";
        if (!numeric && !textual)
            return "Эта колонка пока не перенесена для SQL Server.";

        if (numeric)
        {
            value = trimmed.Length == 0 ? null : NumberText.Parse(trimmed);
            if (trimmed.Length > 0 && value is null)
                return "Недопустимое число или формула: " + trimmed;
            if (value < 0)
                return "Отрицательное значение недопустимо.";
            if (value is null && field != "purchase_price") value = 0m;
        }
        if (field == "unit" && trimmed.Length > 32)
            return "Единица измерения длиннее 32 символов.";

        return null;
    }

    // Caller owns transaction and revision. No COMMIT or revision bump inside this
    // method: multi-row cost operations can reuse the exact single-cell semantics.
    internal static SaveResult SaveCellCore(SqlConnection conn, SqlTransaction tx,
        long positionId, long? expectedContractId, string field, string input,
        bool numeric, string trimmed, decimal? value, string currentRev, out bool changed,
        ICollection<long>? linkedPriceSources = null)
    {
        changed = false;
        using var read = new SqlCommand(
            "SELECT p.qty, p.price, p.purchase_price, p.formulas, c.locked, c.id, c.object_id, " +
            "       c.name, sy.name, p.name, p.unit, p.note, p.work_price, c.smr_mode " +
            "FROM dbo.positions p WITH (UPDLOCK, HOLDLOCK) " +
            "JOIN dbo.systems sy ON sy.id = p.system_id JOIN dbo.contracts c ON c.id = sy.contract_id " +
            "WHERE p.id = @id", conn, tx);
        read.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
        decimal? oldNumber;
        string? oldFormulas;
        bool locked;
        long contractId, objectId;
        string title, systemName, positionName, oldUnit, oldNote;
        using (var r = read.ExecuteReader())
        {
            if (!r.Read()) return SaveResult.Fail("Позиция удалена на сервере.", currentRev);
            var index = field switch { "qty" => 0, "price" => 1, "purchase_price" => 2, _ => 12 };
            oldNumber = !numeric || r.IsDBNull(index) ? null :
                Convert.ToDecimal(r.GetValue(index), CultureInfo.InvariantCulture);
            oldFormulas = r.IsDBNull(3) ? null : r.GetString(3);
            locked = !r.IsDBNull(4) && Convert.ToBoolean(r.GetValue(4), CultureInfo.InvariantCulture);
            contractId = Convert.ToInt64(r.GetValue(5), CultureInfo.InvariantCulture);
            objectId = Convert.ToInt64(r.GetValue(6), CultureInfo.InvariantCulture);
            title = r.IsDBNull(7) ? "" : r.GetString(7);
            systemName = r.IsDBNull(8) ? "" : r.GetString(8);
            positionName = r.IsDBNull(9) ? "" : r.GetString(9);
            oldUnit = r.IsDBNull(10) ? "" : r.GetString(10);
            oldNote = r.IsDBNull(11) ? "" : r.GetString(11);
        }
        if (expectedContractId.HasValue && contractId != expectedContractId.Value)
            return SaveResult.Fail("Позиция не принадлежит этому договору.", currentRev);
        if (locked && field != "purchase_price")
            return SaveResult.Fail("Документ подписан; менять цены и количество нельзя.", currentRev);

        var oldText = field switch { "name" => positionName, "unit" => oldUnit, "note" => oldNote, _ => "" };
        JsonObject? formulas = null;
        string? newFormulas = null;
        if (numeric)
        {
            try
            {
                formulas = string.IsNullOrWhiteSpace(oldFormulas) ? new JsonObject() :
                    JsonNode.Parse(oldFormulas) as JsonObject ?? throw new FormatException("Некорректный JSON формул.");
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                return SaveResult.Fail("Нельзя безопасно обновить сохранённые формулы: " + ex.Message, currentRev);
            }
            var formulaText = NumberText.FormulaText(input);
            if (formulaText is null) formulas.Remove(field);
            else formulas[field] = formulaText;
            newFormulas = formulas.ToJsonString(FormulaOptions);
            if (oldNumber == value && JsonEquivalent(oldFormulas, newFormulas))
            {
                return new SaveResult(true, null, currentRev);
            }
        }
        else if (string.Equals(oldText, trimmed, StringComparison.Ordinal))
        {
            return new SaveResult(true, null, currentRev);
        }

        long? catalogId = null;
        if (field is "name" or "unit")
        {
            var newName = field == "name" ? trimmed : positionName;
            var newUnit = field == "unit" ? trimmed : oldUnit;
            if (!string.IsNullOrWhiteSpace(newName))
            {
                var key = Catalog.Key(newName, newUnit);
                if (key.Length > 600)
                    return SaveResult.Fail("Слишком длинное название для справочника.", currentRev);
                using (var catalog = new SqlCommand(
                    "SELECT id FROM dbo.catalog_items WITH (UPDLOCK, HOLDLOCK) WHERE [key]=@key", conn, tx))
                {
                    catalog.Parameters.Add("@key", SqlDbType.NVarChar, 600).Value = key;
                    var found = catalog.ExecuteScalar();
                    if (found is not null and not DBNull)
                        catalogId = Convert.ToInt64(found, CultureInfo.InvariantCulture);
                }
                if (catalogId is null)
                {
                    using var catalog = new SqlCommand(
                        "INSERT INTO dbo.catalog_items ([key],name,unit) OUTPUT INSERTED.id " +
                        "VALUES (@key,@name,@unit)", conn, tx);
                    catalog.Parameters.Add("@key", SqlDbType.NVarChar, 600).Value = key;
                    catalog.Parameters.Add("@name", SqlDbType.NVarChar, -1).Value = newName;
                    catalog.Parameters.Add("@unit", SqlDbType.NVarChar, 32).Value = newUnit;
                    catalogId = Convert.ToInt64(catalog.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
            }
        }

        // Field is hard-coded by the whitelist above; never interpolate arbitrary user input.
        using (var update = new SqlCommand(numeric
            ? $"UPDATE dbo.positions SET [{field}]=@v, formulas=@formulas WHERE id=@id"
            : field == "note"
                ? "UPDATE dbo.positions SET note=@v WHERE id=@id"
                : $"UPDATE dbo.positions SET [{field}]=@v, catalog_item_id=@catalog WHERE id=@id", conn, tx))
        {
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
            if (numeric)
            {
                update.Parameters.Add("@v", SqlDbType.Float).Value =
                    value is null ? DBNull.Value : (object)(double)value.Value;
                update.Parameters.Add("@formulas", SqlDbType.NVarChar, -1).Value = newFormulas!;
            }
            else
            {
                update.Parameters.Add("@v", SqlDbType.NVarChar, field == "unit" ? 32 : -1).Value = trimmed;
                if (field != "note")
                    update.Parameters.Add("@catalog", SqlDbType.BigInt).Value =
                        catalogId is null ? DBNull.Value : (object)catalogId.Value;
            }
            if (update.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Позиция исчезла во время записи.");
        }

        if (field == "price")
        {
            using var acts = new SqlCommand(
                "UPDATE dbo.act_lines SET price=@v WHERE position_id=@id " +
                "AND act_id IN (SELECT id FROM dbo.acts WHERE signed=0)", conn, tx);
            acts.Parameters.Add("@v", SqlDbType.Float).Value = (double)(value ?? 0m);
            acts.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
            acts.ExecuteNonQuery();
            if (oldNumber != value)
            {
                if (linkedPriceSources is null)
                    SqlServerStages.RefreshLinkedPrices(conn, tx, contractId, [positionId]);
                else
                    linkedPriceSources.Add(positionId);
            }
        }

        if (field is "qty" or "name" or "unit")
            RefreshAreaParent(conn, tx, positionId, contractId, objectId,
                title, systemName);

        if (field == "work_price")
        {
            using var acts = new SqlCommand(
                "UPDATE dbo.act_lines SET work_price=@v WHERE position_id=@id " +
                "AND act_id IN (SELECT id FROM dbo.acts WHERE signed=0)", conn, tx);
            acts.Parameters.Add("@v", SqlDbType.Float).Value = (double)(value ?? 0m);
            acts.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
            acts.ExecuteNonQuery();
            SyncActWork(conn, tx, TouchedUnsignedActs(conn, tx, positionId, contractId));
        }

        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,N'position',@id,N'update',@details,@contract,@object)", conn, tx))
        {
            log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
            log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
            var before = numeric ? oldNumber?.ToString(CultureInfo.InvariantCulture) ?? "" : oldText;
            var after = numeric ? value?.ToString(CultureInfo.InvariantCulture) ?? "" : trimmed;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                $"{title} / {systemName} / {positionName}: {field}: «{before}» → «{after}»";
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            log.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
            log.ExecuteNonQuery();
        }
        changed = true;
        return new SaveResult(true, null, currentRev);
    }

    /// <summary>Неподписанные акты, содержащие строку (для пересчёта СМР «по поз.»).</summary>
    internal static List<long> TouchedUnsignedActs(SqlConnection conn, SqlTransaction tx,
        long positionId, long contractId)
    {
        var ids = new List<long>();
        using var cmd = new SqlCommand(
            "SELECT DISTINCT act_id FROM dbo.act_lines WHERE position_id=@id " +
            "AND act_id IN (SELECT id FROM dbo.acts WHERE contract_id=@cid AND signed=0)", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
        cmd.Parameters.Add("@cid", SqlDbType.BigInt).Value = contractId;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            ids.Add(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture));
        return ids;
    }

    /// <summary>
    /// СМР «по позициям» (acts.sync_work): у неподписанных актов смр-сумма — работы их строк.
    /// Округление каждой строки и суммы — как Python round (half-even).
    /// </summary>
    internal static void SyncActWork(SqlConnection conn, SqlTransaction tx, IEnumerable<long> actIds)
    {
        foreach (var actId in actIds.Distinct())
        {
            string? mode;
            using (var modeCmd = new SqlCommand(
                "SELECT c.smr_mode FROM dbo.acts a JOIN dbo.contracts c ON c.id=a.contract_id WHERE a.id=@id",
                conn, tx))
            {
                modeCmd.Parameters.Add("@id", SqlDbType.BigInt).Value = actId;
                mode = Convert.ToString(modeCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            if (mode != "pos") continue;

            double sum = 0;
            using (var lines = new SqlCommand(
                "SELECT qty, work_price FROM dbo.act_lines WHERE act_id=@id", conn, tx))
            {
                lines.Parameters.Add("@id", SqlDbType.BigInt).Value = actId;
                using var r = lines.ExecuteReader();
                while (r.Read())
                {
                    var qty = r.IsDBNull(0) ? 0d : Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture);
                    var work = r.IsDBNull(1) ? 0d : Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture);
                    sum += FinanceReader.PythonRound(qty * work);
                }
            }
            using var update = new SqlCommand(
                "UPDATE dbo.acts SET smr_amount=@v WHERE id=@id", conn, tx);
            update.Parameters.Add("@v", SqlDbType.Float).Value = FinanceReader.PythonRound(sum);
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = actId;
            update.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Одна цена работы для выделенных строк (kp_editor.set_work_selected): сброс формулы
    /// work_price, запись цены, синхронизация строк неподписанных актов и их СМР. Меняются
    /// только верхние строки; подписанные документы пропускаются; журнал — по записи на
    /// позицию, как update_position. Возвращает число изменённых позиций.
    /// </summary>
    public sealed record WorkPriceBatchResult(int Updated, string Revision);

    /// <summary>
    /// Set a batch of per-position work prices atomically in one SQL Server
    /// contract. Reject foreign, missing, locked or component positions instead
    /// of silently skipping them. Return the revision from the committed
    /// transaction so the WPF editor can immediately queue its next edit.
    /// </summary>
    public static WorkPriceBatchResult SetWorkPrice(string connectionString, long contractId,
        IReadOnlyList<long> positionIds, decimal value, string expectedRevision)
    {
        WorkPriceBatchPolicy.ValidateSelection(contractId, positionIds, value);

        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = LockRevision(conn, tx);
        if (revision is null)
            throw new InvalidOperationException("Нет метки ревизии в SQL Server.");
        if (!string.Equals(revision, expectedRevision, StringComparison.Ordinal))
            throw new DBConcurrencyException("Данные на сервере изменились. Обнови смету и повтори.");

        var touchedActs = new HashSet<long>();
        var changed = 0;
        foreach (var id in positionIds)
        {
            string oldFormulas, title, systemName, positionName;
            decimal oldPrice;
            long actualContractId, objectId;
            bool isTop, locked;
            string smrMode;
            using (var read = new SqlCommand(
                "SELECT p.formulas,p.work_price,p.parent_id,c.locked,c.id,c.name," +
                "sy.name,p.name,c.object_id,c.smr_mode " +
                "FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
                "JOIN dbo.systems sy ON sy.id=p.system_id " +
                "JOIN dbo.contracts c ON c.id=sy.contract_id WHERE p.id=@id", conn, tx))
            {
                read.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                using var row = read.ExecuteReader();
                if (!row.Read())
                    throw new DBConcurrencyException("Одна из выделенных позиций удалена. Вся операция отменена.");
                oldFormulas = row.IsDBNull(0) ? "" : row.GetString(0);
                oldPrice = row.IsDBNull(1) ? 0m :
                    Convert.ToDecimal(row.GetValue(1), CultureInfo.InvariantCulture);
                isTop = row.IsDBNull(2);
                locked = !row.IsDBNull(3) &&
                    Convert.ToBoolean(row.GetValue(3), CultureInfo.InvariantCulture);
                actualContractId = Convert.ToInt64(row.GetValue(4), CultureInfo.InvariantCulture);
                title = row.IsDBNull(5) ? "" : row.GetString(5);
                systemName = row.IsDBNull(6) ? "" : row.GetString(6);
                positionName = row.IsDBNull(7) ? "" : row.GetString(7);
                objectId = Convert.ToInt64(row.GetValue(8), CultureInfo.InvariantCulture);
                smrMode = row.IsDBNull(9) ? "" : row.GetString(9);
            }
            WorkPriceBatchPolicy.EnsureWritable(contractId, actualContractId,
                isTop, locked, smrMode);

            JsonObject formulas;
            try
            {
                formulas = string.IsNullOrWhiteSpace(oldFormulas) ? new JsonObject() :
                    JsonNode.Parse(oldFormulas) as JsonObject ??
                    throw new FormatException("Некорректный JSON формул.");
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                throw new InvalidOperationException(
                    "Нельзя безопасно обновить формулы позиции: " + ex.Message, ex);
            }
            formulas.Remove("work_price");
            using (var update = new SqlCommand(
                "UPDATE dbo.positions SET work_price=@v,formulas=@formulas WHERE id=@id",
                conn, tx))
            {
                update.Parameters.Add("@v", SqlDbType.Float).Value = (double)value;
                update.Parameters.Add("@formulas", SqlDbType.NVarChar, -1).Value =
                    formulas.ToJsonString(FormulaOptions);
                update.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                if (update.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Позиция исчезла во время записи. Вся операция отменена.");
            }
            using (var acts = new SqlCommand(
                "UPDATE dbo.act_lines SET work_price=@v WHERE position_id=@id " +
                "AND act_id IN (SELECT id FROM dbo.acts WHERE signed=0)", conn, tx))
            {
                acts.Parameters.Add("@v", SqlDbType.Float).Value = (double)value;
                acts.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                acts.ExecuteNonQuery();
            }
            foreach (var actId in TouchedUnsignedActs(conn, tx, id, contractId))
                touchedActs.Add(actId);

            using (var log = new SqlCommand(
                "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
                "VALUES (@ts,@user,N'position',@id,N'update',@details,@contract,@object)", conn, tx))
            {
                log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
                log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
                log.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                    $"{title} / {systemName} / {positionName}: work_price: " +
                    $"«{oldPrice.ToString(CultureInfo.InvariantCulture)}» → «{value.ToString(CultureInfo.InvariantCulture)}»";
                log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
                log.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
                log.ExecuteNonQuery();
            }
            changed++;
        }

        SyncActWork(conn, tx, touchedActs);
        var committedRevision = SqlServerTransactionFinalizer.BumpAndCommit(
            () => BumpPositionRevision(conn, tx), tx.Commit);
        return new WorkPriceBatchResult(changed, committedRevision);
    }

    public sealed record InsertPositionResult(bool Saved, long? PositionId, string? Error, string Revision)
    {
        public static InsertPositionResult Fail(string error, string revision) =>
            new(false, null, error, revision);
    }

    /// <summary>
    /// Port of builder.add_position for the SQL Server pilot. A blank new row has no catalog entry;
    /// the user edits its name afterward. Includes revision guard, signed-document guard, history,
    /// order shift and revision update in one transaction.
    /// </summary>
    public static InsertPositionResult AddPosition(string connectionString, long contractId, long systemId,
                                                   long? afterId, long? parentId, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        if (contractId <= 0 || systemId <= 0 || afterId is <= 0 || parentId is <= 0 ||
            string.IsNullOrWhiteSpace(expectedRevision))
            return InsertPositionResult.Fail("Некорректная цель вставки или ревизия.", expectedRevision);
        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);

        // Keep the same lock order as SaveCell and Python: meta.rev -> document -> positions.
        using var rev = new SqlCommand(
            "SELECT [value] FROM dbo.meta WITH (UPDLOCK, HOLDLOCK) WHERE [key] = N'rev'", conn, tx);
        var currentRev = Convert.ToString(rev.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (currentRev is null)
            return InsertPositionResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(currentRev, expectedRevision, StringComparison.Ordinal))
            return InsertPositionResult.Fail("Смета на сервере изменилась, обнови её.", currentRev);

        long objectId;
        string documentName;
        using (var system = new SqlCommand(
            "SELECT c.locked, c.object_id, c.name FROM dbo.systems sy WITH (UPDLOCK, HOLDLOCK) " +
            "JOIN dbo.contracts c WITH (UPDLOCK,HOLDLOCK) ON c.id = sy.contract_id " +
            "JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id " +
            "WHERE sy.id = @system AND c.id = @contract AND ISNULL(o.archived,0)=0 " +
            "AND ISNULL(c.stage_rest,0)=0", conn, tx))
        {
            system.Parameters.Add("@system", SqlDbType.BigInt).Value = systemId;
            system.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            using var reader = system.ExecuteReader();
            if (!reader.Read())
                return InsertPositionResult.Fail("Раздел больше не принадлежит этому документу.", currentRev);
            if (Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture))
                return InsertPositionResult.Fail("Подписанная смета заблокирована.", currentRev);
            objectId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            documentName = reader.IsDBNull(2) ? "" : reader.GetString(2);
        }

        var positions = new List<(long Id, long? Parent, int Order, bool OutOfKp)>();
        using (var list = new SqlCommand(
            "SELECT id, parent_id, [order], out_of_kp FROM dbo.positions WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE system_id = @system ORDER BY [order],id", conn, tx))
        {
            list.Parameters.Add("@system", SqlDbType.BigInt).Value = systemId;
            using var reader = list.ExecuteReader();
            while (reader.Read())
                positions.Add((Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                    !reader.IsDBNull(3) && Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture)));
        }

        var nextOrder = positions.Count == 0 ? 0 : checked(positions[^1].Order + 1);
        if (parentId is long parent)
        {
            var parentRow = positions.FindIndex(p => p.Id == parent);
            if (parentRow < 0 || positions[parentRow].Parent is not null || positions[parentRow].OutOfKp)
                return InsertPositionResult.Fail("Родительская позиция изменилась.", currentRev);
            var last = positions.Where(p => p.Id == parent || p.Parent == parent).Last();
            nextOrder = checked(last.Order + 1);
        }
        else if (afterId is long anchorId)
        {
            var anchorIndex = positions.FindIndex(p => p.Id == anchorId);
            if (anchorIndex < 0)
                return InsertPositionResult.Fail("Позиция вставки уже отсутствует.", currentRev);
            var root = positions[anchorIndex].Parent ?? anchorId;
            var last = positions.Where(p => p.Id == root || p.Parent == root).Last();
            nextOrder = checked(last.Order + 1);
        }

        // Shift with one SQL statement instead of one UPDATE per existing position (important for 2500+ rows).
        using (var shift = new SqlCommand(
            "UPDATE dbo.positions SET [order] = [order] + 1 " +
            "WHERE system_id = @system AND [order] >= @at", conn, tx))
        {
            shift.Parameters.Add("@system", SqlDbType.BigInt).Value = systemId;
            shift.Parameters.Add("@at", SqlDbType.Int).Value = nextOrder;
            shift.ExecuteNonQuery();
        }

        long id;
        using (var insert = new SqlCommand(
            "INSERT INTO dbo.positions (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price," +
            "purchase_price,note,extra_json,source_row,[order],out_of_kp,formulas,stage_src,area_manual," +
            "work_price,cost_note,cost_color,work_sell,work_cost) " +
            "OUTPUT INSERTED.id " +
            "VALUES (@system,@parent,NULL,N'',N'',N'',N'шт',1,0,NULL,N'',N'',NULL,@at,0,N''," +
            "NULL,NULL,NULL,N'',N'',NULL,NULL)", conn, tx))
        {
            insert.Parameters.Add("@system", SqlDbType.BigInt).Value = systemId;
            insert.Parameters.Add("@parent", SqlDbType.BigInt).Value =
                parentId is null ? DBNull.Value : (object)parentId.Value;
            insert.Parameters.Add("@at", SqlDbType.Int).Value = nextOrder;
            id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@time,@user,N'position',@id,N'create',@details,@contract,@object)", conn, tx))
        {
            log.Parameters.Add("@time", SqlDbType.DateTime2).Value = DateTime.Now;
            log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                $"{documentName}: добавлена {(parentId is null ? "позиция" : "комплектующая")}";
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            log.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
            log.ExecuteNonQuery();
        }

        var identity = $"{Environment.UserName}@{Environment.MachineName}#{Environment.ProcessId}";
        var newRev = $"{identity}|{Guid.NewGuid():N}|{DateTime.Now:HH:mm:ss}";
        using (var update = new SqlCommand(
            "UPDATE dbo.meta SET [value]=@revision WHERE [key]=N'rev'", conn, tx))
        {
            update.Parameters.Add("@revision", SqlDbType.NVarChar, -1).Value = newRev;
            if (update.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Не удалось обновить meta.rev.");
        }
        tx.Commit();
        return new InsertPositionResult(true, id, null, newRev);
    }

    private static bool JsonEquivalent(string? oldJson, string normalized)
    {
        try
        {
            var old = string.IsNullOrWhiteSpace(oldJson) ? new JsonObject() : JsonNode.Parse(oldJson);
            return JsonNode.DeepEquals(old, JsonNode.Parse(normalized));
        }
        catch (JsonException) { return false; }
    }
}
