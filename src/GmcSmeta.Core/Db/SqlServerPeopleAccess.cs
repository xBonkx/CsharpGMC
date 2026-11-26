using System.Data;
using System.Text.Json;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Uses the EXISTING Python/SQL Server tables dbo.people, dbo.cars and dbo.access_lists.
/// No parallel employee database. All mutations require a verified pilot, serialized revision
/// lock and one SQL transaction. Data from other objects is never returned in object history.
/// </summary>
public sealed class SqlServerPeopleAccess
{
    private readonly string _connectionString;
    public SqlServerPeopleAccess(string connectionString, CancellationToken token = default)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        token.ThrowIfCancellationRequested();
        _connectionString = connectionString;
        using var conn = Open(token);
        foreach (var table in new[] { "people", "cars", "access_lists" })
        {
            using var check = new SqlCommand("SELECT OBJECT_ID(@name, N'U')", conn);
            check.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = "dbo." + table;
            token.ThrowIfCancellationRequested();
            var value = token.CanBeCanceled
                ? check.ExecuteScalarAsync(token).GetAwaiter().GetResult()
                : check.ExecuteScalar();
            token.ThrowIfCancellationRequested();
            if (value is DBNull or null)
                throw new InvalidOperationException("В тестовой базе отсутствует таблица SQL Server: " + table);
        }
    }

    private SqlConnection Open(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var conn = token.CanBeCanceled
            ? SqlServerDatabase.OpenAsync(_connectionString, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Open(_connectionString);
        if (!conn.Database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
        {
            conn.Dispose();
            throw new InvalidOperationException("Операции с персоналом разрешены только на GmcSmetaPilot_*.");
        }
        return conn;
    }
    private static void Execute(SqlConnection conn, SqlTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(conn, tx, sql, args);
        cmd.ExecuteNonQuery();
    }
    private static SqlCommand Command(SqlConnection conn, SqlTransaction? tx, string sql,
        params (string, object?)[] args)
    {
        var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 30 };
        foreach (var (key, value) in args)
            cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private static void AppLock(SqlConnection conn, SqlTransaction tx)
    {
        using var cmd = Command(conn, tx, """
            DECLARE @code int;
            EXEC @code = sys.sp_getapplock @Resource=N'GMC.PeopleAccess',
              @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000;
            SELECT @code;
            """);
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 0)
            throw new TimeoutException("Изменения справочника заблокированы другим пользователем.");
    }
    private static void CheckRevision(SqlConnection conn, SqlTransaction tx, string expected)
    {
        using var cmd = Command(conn, tx,
            "SELECT [value] FROM dbo.meta WITH (UPDLOCK,HOLDLOCK) WHERE [key]=N'rev'");
        var rev = Convert.ToString(cmd.ExecuteScalar()) ?? "";
        if (!string.Equals(rev, expected, StringComparison.Ordinal))
            throw new DBConcurrencyException("Данные изменились в другом окне. Обновите справочник перед сохранением.");
    }
    private static string Bump(SqlConnection conn, SqlTransaction tx)
    {
        var rev = Environment.UserName + "@" + Environment.MachineName + "|people|" + Guid.NewGuid().ToString("N");
        using var cmd = Command(conn, tx,
            "UPDATE dbo.meta SET [value]=@rev WHERE [key]=N'rev'", ("@rev", rev));
        if (cmd.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Отсутствует ревизия meta.rev.");
        return rev;
    }
    private static void CheckObject(SqlConnection conn, SqlTransaction tx, long objectId)
    {
        using var cmd = Command(conn, tx, "SELECT COUNT_BIG(*) FROM dbo.objects WITH (HOLDLOCK) WHERE id=@id",
            ("@id", objectId));
        if (Convert.ToInt64(cmd.ExecuteScalar()) != 1)
            throw new InvalidOperationException("Объект не найден в этой базе.");
    }

    public (IReadOnlyList<PeoplePerson> People, IReadOnlyList<PeopleCar> Cars, string Revision) Load(
        bool showInactive, CancellationToken token = default)
    {
        // A second pass when a different client writes during the read gives a consistent snapshot.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var conn = Open(token);
            string Revision() => token.CanBeCanceled
                ? SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult()
                : SqlServerDatabase.Revision(conn);
            var before = Revision();
            var people = new List<PeoplePerson>();
            var cars = new List<PeopleCar>();
            using (var cmd = Command(conn, null,
                "SELECT id,fio,post,birth,note,fio_gen,post_gen,active,sort_order FROM dbo.people " +
                (showInactive ? "" : "WHERE active=1 ") + "ORDER BY sort_order,id"))
            using (var registration = SqlReadCancellation.Register(cmd, token))
            using (var reader = SqlReadCancellation.ExecuteReader(cmd, token))
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    people.Add(ReadPerson(reader));
                }
            using (var cmd = Command(conn, null,
                "SELECT id,model,plate,note,active,sort_order FROM dbo.cars " +
                (showInactive ? "" : "WHERE active=1 ") + "ORDER BY sort_order,id"))
            using (var registration = SqlReadCancellation.Register(cmd, token))
            using (var reader = SqlReadCancellation.ExecuteReader(cmd, token))
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    cars.Add(ReadCar(reader));
                }
            token.ThrowIfCancellationRequested();
            var after = Revision();
            token.ThrowIfCancellationRequested();
            if (before == after)
            {
                var sortedPeople = PeopleRules.SortPeople(people);
                var sortedCars = cars.OrderBy(x => x.SortOrder == 0 ? int.MaxValue : x.SortOrder)
                    .ThenBy(x => x.Model).ThenBy(x => x.Plate).ToArray();
                token.ThrowIfCancellationRequested();
                return (sortedPeople, sortedCars, after);
            }
        }
        throw new DBConcurrencyException("Справочник меняется в другом окне. Повторите загрузку.");
    }

    // SQL Server copies may store sort_order as INT or BIGINT.
    // Convert checks the Int32 range instead of requiring an exact provider type.
    internal static PeoplePerson ReadPerson(IDataRecord reader) =>
        new(Convert.ToInt64(reader.GetValue(0)), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            reader.GetBoolean(7), Convert.ToInt32(reader.GetValue(8)));

    internal static PeopleCar ReadCar(IDataRecord reader) =>
        new(Convert.ToInt64(reader.GetValue(0)), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetBoolean(4), Convert.ToInt32(reader.GetValue(5)));

    public (long Id, string Revision) SavePerson(PeoplePerson row, string expectedRevision)
    {
        var fio = PeopleRules.Clean(row.Fio);
        if (fio.Length == 0 || fio.Length > 255 || row.Post.Length > 128 ||
            row.FioGen.Length > 255 || row.PostGen.Length > 128 || row.Birth.Length > 32)
            throw new ArgumentException("Проверьте ФИО, должность и допустимую длину полей.");
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        AppLock(conn, tx);
        CheckRevision(conn, tx, expectedRevision);
        var key = PeopleRules.PersonKey(fio);
        using (var check = Command(conn, tx, "SELECT id,fio FROM dbo.people WITH (UPDLOCK,HOLDLOCK)"))
        using (var reader = check.ExecuteReader())
            while (reader.Read())
                if (Convert.ToInt64(reader.GetValue(0)) != row.Id && PeopleRules.PersonKey(reader.GetString(1)) == key)
                    throw new InvalidOperationException("Сотрудник с таким ФИО уже существует.");
        long id = row.Id;
        if (id == 0)
        {
            using var cmd = Command(conn, tx, """
                INSERT dbo.people(fio,fio_gen,post,post_gen,birth,note,active,created_at,sort_order)
                OUTPUT INSERTED.id VALUES(@fio,@fg,@post,@pg,@birth,@note,@active,SYSUTCDATETIME(),@ord)
                """, ("@fio", fio), ("@fg", row.FioGen), ("@post", PeopleRules.Clean(row.Post)),
                ("@pg", row.PostGen), ("@birth", PeopleRules.BirthNorm(row.Birth)),
                ("@note", row.Note), ("@active", row.Active), ("@ord", row.SortOrder));
            id = Convert.ToInt64(cmd.ExecuteScalar());
        }
        else
        {
            using var cmd = Command(conn, tx, """
                UPDATE dbo.people SET fio=@fio,fio_gen=@fg,post=@post,post_gen=@pg,
                birth=@birth,note=@note,active=@active,sort_order=@ord WHERE id=@id
                """, ("@fio", fio), ("@fg", row.FioGen), ("@post", PeopleRules.Clean(row.Post)),
                ("@pg", row.PostGen), ("@birth", PeopleRules.BirthNorm(row.Birth)),
                ("@note", row.Note), ("@active", row.Active), ("@ord", row.SortOrder), ("@id", row.Id));
            if (cmd.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Сотрудник удалён другим пользователем.");
        }
        var rev = Bump(conn, tx);
        tx.Commit();
        return (id, rev);
    }

    public (long Id, string Revision) SaveCar(PeopleCar row, string expectedRevision)
    {
        var model = PeopleRules.Clean(row.Model);
        var plate = PeopleRules.Clean(row.Plate).ToUpperInvariant();
        if (model.Length > 128 || plate.Length > 32 || (model.Length == 0 && plate.Length == 0))
            throw new ArgumentException("Укажите марку или госномер (не более 128/32 символов).");
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        AppLock(conn, tx);
        CheckRevision(conn, tx, expectedRevision);
        var key = PeopleRules.PlateKey(plate);
        if (key.Length != 0)
        {
            using var check = Command(conn, tx, "SELECT id,plate FROM dbo.cars WITH (UPDLOCK,HOLDLOCK)");
            using var reader = check.ExecuteReader();
            while (reader.Read())
                if (Convert.ToInt64(reader.GetValue(0)) != row.Id && PeopleRules.PlateKey(reader.GetString(1)) == key)
                    throw new InvalidOperationException("Автомобиль с таким госномером уже существует.");
        }
        long id = row.Id;
        if (id == 0)
        {
            using var cmd = Command(conn, tx, """
                INSERT dbo.cars(model,plate,note,active,created_at,sort_order)
                OUTPUT INSERTED.id VALUES(@model,@plate,@note,@active,SYSUTCDATETIME(),@ord)
                """, ("@model", model), ("@plate", plate), ("@note", row.Note),
                ("@active", row.Active), ("@ord", row.SortOrder));
            id = Convert.ToInt64(cmd.ExecuteScalar());
        }
        else
        {
            using var cmd = Command(conn, tx,
                "UPDATE dbo.cars SET model=@model,plate=@plate,note=@note,active=@active,sort_order=@ord WHERE id=@id",
                ("@model", model), ("@plate", plate), ("@note", row.Note), ("@active", row.Active),
                ("@ord", row.SortOrder), ("@id", row.Id));
            if (cmd.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Автомобиль удалён другим пользователем.");
        }
        var rev = Bump(conn, tx);
        tx.Commit();
        return (id, rev);
    }

    public string SetActive(bool person, long id, bool active, string expectedRevision) =>
        SetActiveBatch(person, new[] { id }, active, expectedRevision);

    private static long[] ValidSelection(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0 || ids.Count > 50000 || ids.Any(id => id <= 0) ||
            ids.Count != ids.Distinct().Count())
            throw new ArgumentException("Выберите существующие записи без повторений.");
        return ids.ToArray();
    }

    public string SetActiveBatch(bool person, IReadOnlyCollection<long> ids, bool active, string expectedRevision)
    {
        var selected = ValidSelection(ids);
        SqlServerWorkspace.RequirePilotDatabase(_connectionString);
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        AppLock(conn, tx);
        CheckRevision(conn, tx, expectedRevision);
        foreach (var id in selected)
        {
            using var cmd = Command(conn, tx,
                "UPDATE dbo." + (person ? "people" : "cars") + " SET active=@active WHERE id=@id",
                ("@active", active), ("@id", id));
            if (cmd.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Запись больше не существует.");
        }
        var rev = Bump(conn, tx);
        tx.Commit();
        return rev;
    }

    public string Reorder(bool person, IReadOnlyList<long> ids, string expectedRevision)
    {
        if (ids.Count != ids.Distinct().Count() || ids.Any(id => id <= 0))
            throw new ArgumentException("Невозможно применить порядок: повторяющиеся или пустые ID.");
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        AppLock(conn, tx);
        CheckRevision(conn, tx, expectedRevision);
        var table = person ? "people" : "cars";
        for (int i = 0; i < ids.Count; i++)
        {
            using var cmd = Command(conn, tx, "UPDATE dbo." + table + " SET sort_order=@n WHERE id=@id",
                ("@n", i + 1), ("@id", ids[i]));
            if (cmd.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Порядок изменился в другом окне.");
        }
        var rev = Bump(conn, tx);
        tx.Commit();
        return rev;
    }

    public string DeleteOrDeactivate(bool person, long id, string expectedRevision) =>
        DeleteOrDeactivateBatch(person, new[] { id }, expectedRevision).Revision;

    public (string Revision, int Deleted, int Deactivated) DeleteOrDeactivateBatch(
        bool person, IReadOnlyCollection<long> ids, string expectedRevision)
    {
        var selected = ValidSelection(ids);
        SqlServerWorkspace.RequirePilotDatabase(_connectionString);
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        AppLock(conn, tx);
        CheckRevision(conn, tx, expectedRevision);
        var deleted = 0;
        var deactivated = 0;
        foreach (var id in selected)
        {
            if (DeleteOrDeactivateCore(conn, tx, person, id)) deactivated++;
            else deleted++;
        }
        var rev = Bump(conn, tx);
        tx.Commit();
        return (rev, deleted, deactivated);
    }

    private static bool DeleteOrDeactivateCore(SqlConnection conn, SqlTransaction tx, bool person, long id)
    {
        // JSON snapshots in legacy access_lists are immutable. A reference means deactivate,
        // not delete: existing generated orders must retain the source employee/vehicle.
        // History contains both Python snake_case snapshots and new C# PascalCase JSON.
        // Both must block physical deletion, including already-issued Word orders.
        var lowerPath = person ? "$.people" : "$.cars";
        var upperPath = person ? "$.People" : "$.Cars";
        using var check = Command(conn, tx, """
            SELECT COUNT_BIG(*) FROM dbo.access_lists a
            CROSS APPLY (
                SELECT person_id FROM OPENJSON(
                    CASE WHEN ISJSON(a.data)=1 THEN a.data ELSE N'{}' END,@lower)
                    WITH (person_id BIGINT '$.id')
                UNION ALL
                SELECT person_id FROM OPENJSON(
                    CASE WHEN ISJSON(a.data)=1 THEN a.data ELSE N'{}' END,@upper)
                    WITH (person_id BIGINT '$.Id')
            ) x WHERE x.person_id=@id
            """, ("@lower", lowerPath), ("@upper", upperPath), ("@id", id));
        var used = Convert.ToInt64(check.ExecuteScalar()) > 0;
        if (person)
        {
            using var order = Command(conn, tx, """
                SELECT COUNT_BIG(*) FROM dbo.access_lists a
                WHERE kind=N'order' AND (
                  TRY_CONVERT(BIGINT, JSON_VALUE(
                    CASE WHEN ISJSON(a.data)=1 THEN a.data ELSE N'{}' END,'$.person_id'))=@id
                  OR TRY_CONVERT(BIGINT, JSON_VALUE(
                    CASE WHEN ISJSON(a.data)=1 THEN a.data ELSE N'{}' END,'$.PersonId'))=@id)
                """, ("@id", id));
            used |= Convert.ToInt64(order.ExecuteScalar()) > 0;
        }
        using var cmd = Command(conn, tx,
            (used ? "UPDATE dbo." : "DELETE FROM dbo.") + (person ? "people" : "cars") +
            (used ? " SET active=0 WHERE id=@id" : " WHERE id=@id"),
            ("@id", id));
        if (cmd.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Запись не найдена.");
        return used;
    }

    public PeopleImportResult Import(PeopleImportPreview preview, string expectedRevision)
    {
        if (preview.People.Count + preview.Cars.Count > 50000)
            throw new ArgumentException("Слишком большой импорт: разделите файл на части.");
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        AppLock(conn, tx);
        CheckRevision(conn, tx, expectedRevision);
        var keys = new Dictionary<string, (long Id, string Post, string Birth)>();
        using (var cmd = Command(conn, tx, "SELECT id,fio,post,birth FROM dbo.people WITH (UPDLOCK,HOLDLOCK)"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                keys.TryAdd(PeopleRules.PersonKey(r.GetString(1)),
                    (Convert.ToInt64(r.GetValue(0)), r.GetString(2), r.GetString(3)));
        var plates = new Dictionary<string, long>();
        using (var cmd = Command(conn, tx, "SELECT id,plate FROM dbo.cars WITH (UPDLOCK,HOLDLOCK)"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var k = PeopleRules.PlateKey(r.GetString(1));
                if (k.Length > 0) plates.TryAdd(k, Convert.ToInt64(r.GetValue(0)));
            }

        var addPeople = new DataTable();
        foreach (var (name, type) in new[] { ("fio", typeof(string)), ("fio_gen", typeof(string)),
            ("post", typeof(string)), ("post_gen", typeof(string)), ("birth", typeof(string)),
            ("note", typeof(string)), ("active", typeof(bool)), ("created_at", typeof(DateTime)),
            ("sort_order", typeof(int)) })
            addPeople.Columns.Add(name, type);
        var addCars = new DataTable();
        foreach (var (name, type) in new[] { ("model", typeof(string)), ("plate", typeof(string)),
            ("note", typeof(string)), ("active", typeof(bool)), ("created_at", typeof(DateTime)),
            ("sort_order", typeof(int)) })
            addCars.Columns.Add(name, type);
        var existingP = 0;
        var existingC = 0;
        var uniquePeople = new HashSet<string>();
        foreach (var row in preview.People)
        {
            var fio = PeopleRules.Clean(row.Fio);
            if (fio.Length == 0 || fio.Length > 255) continue;
            var key = PeopleRules.PersonKey(fio);
            if (!uniquePeople.Add(key)) continue;
            var post = PeopleRules.Clean(row.Post);
            var birth = PeopleRules.BirthNorm(row.Birth);
            if (post.Length > 128 || birth.Length > 32)
                throw new ArgumentException("Слишком длинное поле в Excel.");
            if (keys.TryGetValue(key, out var found))
            {
                // Never overwrite an existing manual value.
                Execute(conn, tx, """
                    UPDATE dbo.people SET active=1,
                    post=CASE WHEN post=N'' THEN @post ELSE post END,
                    birth=CASE WHEN birth=N'' THEN @birth ELSE birth END WHERE id=@id
                    """, ("@post", post), ("@birth", birth), ("@id", found.Id));
                existingP++;
            }
            else
                addPeople.Rows.Add(fio, "", post, "", birth, "", true, DateTime.UtcNow, 0);
        }
        var uniqueCars = new HashSet<string>();
        foreach (var row in preview.Cars)
        {
            var model = PeopleRules.Clean(row.Model);
            var plate = PeopleRules.Clean(row.Plate).ToUpperInvariant();
            if (model.Length > 128 || plate.Length > 32 || (model.Length == 0 && plate.Length == 0))
                continue;
            var key = PeopleRules.PlateKey(plate);
            // Empty plates cannot be reliably matched by identity; don't create duplicates
            // from repeated inputs with no unique identifier.
            if (key.Length == 0 || !uniqueCars.Add(key)) continue;
            if (plates.TryGetValue(key, out var found))
            {
                Execute(conn, tx, "UPDATE dbo.cars SET active=1 WHERE id=@id", ("@id", found));
                existingC++;
            }
            else
                addCars.Rows.Add(model, plate, "", true, DateTime.UtcNow, 0);
        }
        Bulk(conn, tx, "dbo.people", addPeople);
        Bulk(conn, tx, "dbo.cars", addCars);
        if (addPeople.Rows.Count + addCars.Rows.Count + existingP + existingC > 0)
            Bump(conn, tx);
        tx.Commit();
        return new PeopleImportResult(addPeople.Rows.Count, existingP, addCars.Rows.Count, existingC);
    }

    private static void Bulk(SqlConnection conn, SqlTransaction tx, string table, DataTable data)
    {
        if (data.Rows.Count == 0) return;
        using var copy = new SqlBulkCopy(conn, SqlBulkCopyOptions.CheckConstraints, tx)
        {
            DestinationTableName = table, BatchSize = 500, BulkCopyTimeout = 120
        };
        foreach (DataColumn column in data.Columns)
            copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        copy.WriteToServer(data);
    }

    public AccessSite Site(long objectId, CancellationToken token = default)
    {
        using var conn = Open(token);
        using var cmd = Command(conn, null, """
            SELECT o.id,o.name,o.address,o.site_full,o.work_title,o.folder,
            COALESCE(NULLIF(o.cust_org,N''),o.customer),
            ISNULL((SELECT TOP(1) c.number FROM dbo.contracts c
                WHERE c.object_id=o.id AND c.kind IN (N'main',N'dop',N'kp')
                ORDER BY CASE c.kind WHEN N'main' THEN 0 WHEN N'dop' THEN 1 ELSE 2 END,c.id),N''),
            ISNULL((SELECT TOP(1) c.date FROM dbo.contracts c
                WHERE c.object_id=o.id AND c.kind IN (N'main',N'dop',N'kp')
                ORDER BY CASE c.kind WHEN N'main' THEN 0 WHEN N'dop' THEN 1 ELSE 2 END,c.id),N'')
            FROM dbo.objects o WHERE o.id=@id
            """, ("@id", objectId));
        using var registration = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        var found = r.Read();
        token.ThrowIfCancellationRequested();
        if (!found) throw new InvalidOperationException("Объект не найден.");
        return new AccessSite(Convert.ToInt64(r.GetValue(0)), r.GetString(1), r.GetString(2), r.GetString(3),
            r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8));
    }

    public IReadOnlyList<AccessCompany> Companies(CancellationToken token = default)
    {
        using var conn = Open(token);
        using var cmd = Command(conn, null,
            "SELECT id,name,address,inn_kpp,signer_post,signer_name FROM dbo.companies ORDER BY [order],id");
        using var registration = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        var list = new List<AccessCompany>();
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            list.Add(new AccessCompany(Convert.ToInt64(r.GetValue(0)), r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetString(4), r.GetString(5)));
        }
        token.ThrowIfCancellationRequested();
        return list;
    }

    public IReadOnlyList<AccessHistory> History(long objectId, string kind, CancellationToken token = default)
    {
        if (kind is not ("list" or "order")) throw new ArgumentException("Недопустимый тип истории.");
        using var conn = Open(token);
        using var cmd = Command(conn, null, """
            SELECT a.id,a.object_id,a.kind,a.created_at,a.file,a.[user],a.data
            FROM dbo.access_lists a INNER JOIN dbo.objects o ON o.id=a.object_id
            WHERE a.object_id=@oid AND a.kind=@kind ORDER BY a.id DESC
            """, ("@oid", objectId), ("@kind", kind));
        using var registration = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        var list = new List<AccessHistory>();
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            list.Add(new AccessHistory(Convert.ToInt64(r.GetValue(0)), Convert.ToInt64(r.GetValue(1)), r.GetString(2), r.GetDateTime(3),
                r.GetString(4), r.GetString(5), r.GetString(6)));
        }
        token.ThrowIfCancellationRequested();
        return list;
    }

    /// <summary>
    /// Called only AFTER file creation and validation. OperationId makes a retry detectable
    /// after an uncertain COMMIT; callers must not blindly run the export a second time.
    /// </summary>
    public (long Id, string Revision) RegisterExport(long objectId, string kind, string file,
        string payloadJson, Guid operationId, string expectedRevision, string? rememberSite = null)
    {
        if (kind is not ("list" or "order") || operationId == Guid.Empty)
            throw new ArgumentException("Неизвестный тип экспорта или пустой идентификатор операции.");
        if (kind == "list" && !File.Exists(file))
            throw new IOException("Файл списка не создан; запись в историю запрещена.");
        if (kind == "order" && !Directory.Exists(file))
            throw new IOException("Папка приказов отсутствует; запись в историю запрещена.");
        using var doc = JsonDocument.Parse(payloadJson);
        if (kind == "order")
        {
            if (!doc.RootElement.TryGetProperty("Files", out var files) ||
                files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
                throw new ArgumentException("В документе приказов отсутствует перечень файлов.");
            foreach (var name in files.EnumerateArray())
            {
                var generated = name.GetString();
                if (string.IsNullOrWhiteSpace(generated) || !File.Exists(generated) ||
                    !string.Equals(Path.GetExtension(generated), ".docx", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Не все Word-файлы созданы; журнал приказов не изменён.");
            }
        }
        if (!doc.RootElement.TryGetProperty("OperationId", out var op) ||
            !Guid.TryParse(op.GetString(), out var parsed) || parsed != operationId)
            throw new ArgumentException("Операция не соответствует неизменяемому snapshot.");
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        AppLock(conn, tx);
        CheckRevision(conn, tx, expectedRevision);
        CheckObject(conn, tx, objectId);
        using (var find = Command(conn, tx, """
            SELECT id FROM dbo.access_lists WHERE object_id=@oid AND kind=@kind AND
            JSON_VALUE(CASE WHEN ISJSON(data)=1 THEN data ELSE N'{}' END, '$.OperationId')=@operation
            """, ("@oid", objectId), ("@kind", kind), ("@operation", operationId.ToString())))
        {
            var already = find.ExecuteScalar();
            if (already is not null and not DBNull)
                return (Convert.ToInt64(already), expectedRevision);
        }
        if (kind == "order" && !string.IsNullOrWhiteSpace(rememberSite))
        {
            var siteName = PeopleRules.Clean(rememberSite);
            if (siteName.Length > 4000)
                throw new ArgumentException("Полное название стройки слишком длинное.");
            Execute(conn, tx, """
                UPDATE dbo.objects SET site_full=@site
                WHERE id=@id AND NULLIF(LTRIM(RTRIM(site_full)),N'') IS NULL
                """, ("@site", siteName), ("@id", objectId));
        }
        using var insert = Command(conn, tx, """
            INSERT dbo.access_lists(object_id,kind,created_at,[user],[file],[data])
            OUTPUT INSERTED.id VALUES(@oid,@kind,SYSUTCDATETIME(),@user,@path,@json)
            """, ("@oid", objectId), ("@kind", kind), ("@user", Environment.UserName),
            ("@path", file), ("@json", payloadJson));
        var id = Convert.ToInt64(insert.ExecuteScalar());
        var rev = Bump(conn, tx);
        tx.Commit();
        return (id, rev);
    }

    public bool WasRegistered(long objectId, Guid operationId)
    {
        using var conn = Open();
        using var cmd = Command(conn, null, """
            SELECT COUNT_BIG(*) FROM dbo.access_lists
            WHERE object_id=@oid AND JSON_VALUE(CASE WHEN ISJSON([data])=1 THEN [data] ELSE N'{}' END,
                '$.OperationId')=@operation
            """, ("@oid", objectId), ("@operation", operationId.ToString()));
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
}
