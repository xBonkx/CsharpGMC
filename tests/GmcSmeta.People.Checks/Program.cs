using System.IO;
using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using GmcSmeta.App;
using GmcSmeta.Core.Domain;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.Json;

var dir = Path.Combine(Path.GetTempPath(), "gmc-people-offline-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    var accessFile = Path.Combine(dir, "access.xlsx");
    PeopleDocuments.ExportAccessList(accessFile, new AccessCompany(1, "ООО «ТЕСТ»",
        "Пример", "ИНН 000", "Генеральный директор", "А.А."),
        "Тестовые реквизиты", "Для выполнения работ",
        [new AccessPerson(1, "Тестовый Сотрудник", "монтажник", "15.11.1978")],
        [new AccessCar(2, "Тестовая модель", "А123ВС")], "Тестовая подпись");
    using (var xlsx = new XLWorkbook(accessFile))
    {
        var sheet = xlsx.Worksheet("Список");
        Require(sheet.Cell("B10").GetString() == "Тестовый Сотрудник", "XLSX: ФИО");
        Require(sheet.Cell("A9").GetString() == "№", "XLSX: заголовок");
        Require(sheet.Cell("B12").GetString() == "Автомобили", "XLSX: автомобили");
    }
    var imported = PeopleDocuments.ReadImport(accessFile);
    Require(imported.People.Count == 1 && imported.Cars.Count == 1, "XLSX roundtrip");
    Require(imported.People[0].Birth == "15.11.1978", "XLSX birthday");
    try
    {
        PeopleDocuments.ExportAccessList(accessFile, null, "", "", imported.People.Select((p, i) =>
            new AccessPerson(i + 1, p.Fio, p.Post, p.Birth)).ToArray(),
            [], "");
        throw new Exception("Overwrite was not blocked.");
    }
    catch (IOException) { /* expected */ }

    var src = Path.Combine(dir, "template.docx");
    var dst = Path.Combine(dir, "filled.docx");
    using (var zip = ZipFile.Open(src, ZipArchiveMode.Create))
    {
        Put(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Put(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships " +
            "xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" " +
            "Target=\"word/document.xml\"/></Relationships>");
        Put(zip, "word/document.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            "<w:body><w:p><w:r><w:rPr><w:b/></w:rPr><w:t>{ФИО_</w:t></w:r>" +
            "<w:r><w:t>КОГО}</w:t></w:r></w:p></w:body></w:document>");
    }
    PeopleDocuments.FillDocx(src, dst, new Dictionary<string, string>
    {
        ["{ФИО_КОГО}"] = "Синтетического Сотрудника"
    });
    using (var result = ZipFile.OpenRead(dst))
    {
        using var reader = new StreamReader(result.GetEntry("word/document.xml")!.Open());
        var xml = reader.ReadToEnd();
        Require(xml.Contains("Синтетического Сотрудника", StringComparison.Ordinal), "DOCX placeholder");
        Require(xml.Contains("<w:b/>", StringComparison.Ordinal), "DOCX run formatting");
        Require(!xml.Contains("{ФИО_", StringComparison.Ordinal), "DOCX split placeholder residue");
    }
    // Render every actual, embedded Python-origin .docx template with synthetic values.
    // This verifies that a stock installation has usable editable Word templates.
    var templates = PeopleDocuments.Templates();
    Require(templates.Count >= 3, "Original Word templates embedded in WPF build");
    var site = new AccessSite(1, "Тестовая стройка", "", "Тестовая стройка", "", "",
        "Синтетический заказчик", "8", "01.01.2026");
    var syntheticPerson = new PeoplePerson(1, "Петров Пётр Петрович", "главный инженер",
        "", "", "", "", true, 0);
    var next = "03/03-01";
    for (var index = 0; index < templates.Count; index++)
    {
        var mapping = PeopleDocuments.OrderMapping(site, syntheticPerson, null,
            new DateTime(2026, 3, 3), next, "№8 от 01 января 2026 г.", site.Name);
        var dest = Path.Combine(dir, "original-template-" + index.ToString("D2") + ".docx");
        PeopleDocuments.FillDocx(templates[index], dest, mapping);
        using var result = ZipFile.OpenRead(dest);
        Require(result.GetEntry("word/document.xml") is not null,
            "Original docx can be opened after text substitution");
        next = PeopleRules.NextNumber(next);
    }
    Console.WriteLine($"PASS {templates.Count} stock Word order templates and sequential numbering");
    Console.WriteLine("PASS people office XLSX/DOCX checks: roundtrip, formatting, immutability");
    // Write tests are opt-in and their connection string MUST name a GmcSmetaPilot_* database.
    var pilotConnection = Environment.GetEnvironmentVariable("GMC_SMETA_PEOPLE_SQL_PILOT");
    if (!string.IsNullOrWhiteSpace(pilotConnection))
        RunPilot(pilotConnection, dir);
    else
        Console.WriteLine("SKIP isolated SQL pilot: GMC_SMETA_PEOPLE_SQL_PILOT not configured.");
}
finally
{
    try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
}

static void Put(ZipArchive zip, string name, string content)
{
    using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
    writer.Write(content);
}
static void Require(bool ok, string what)
{
    if (!ok) throw new InvalidOperationException(what);
}

static void RunPilot(string connectionString, string folder)
{
    SqlServerWorkspace.RequirePilotDatabase(connectionString);
    // This is intentionally limited to a known pilot database. Never mutate prod.
    var store = new SqlServerPeopleAccess(connectionString);
    long[] objects;
    using (var conn = SqlServerDatabase.Open(connectionString))
    using (var cmd = new SqlCommand("SELECT TOP(2) id FROM dbo.objects ORDER BY id", conn))
    using (var r = cmd.ExecuteReader())
    {
        var list = new List<long>();
        while (r.Read()) list.Add(Convert.ToInt64(r.GetValue(0)));
        objects = list.ToArray();
    }
    if (objects.Length < 2)
        throw new InvalidOperationException("Для SQL-проверки допусков требуется два тестовых объекта.");

    // Run both history queries before any writes: catch SQL parser/identifier regressions
    // even on a directory with no prior lists or orders.
    _ = store.History(objects[0], "list");
    _ = store.History(objects[0], "order");

    var id = Guid.NewGuid().ToString("N")[..8];
    var source = new PeopleImportPreview(
        Enumerable.Range(1, 1000).Select(i => new PeopleImportRow(
            "Тестовый" + id + " Сотрудник" + i.ToString("D4"), "монтажник", "01.01.2000")).ToArray(),
        Enumerable.Range(1, 200).Select(i => new CarImportRow(
            "Проверочная модель", "TEST" + id + i.ToString("D4"))).ToArray());
    var old = store.Load(false);
    var first = store.Import(source, old.Revision);
    Require(first.NewPeople == 1000 && first.NewCars == 200, "SQL pilot: bulk insert");
    var after = store.Load(false);
    var repeated = store.Import(source, after.Revision);
    Require(repeated.NewPeople == 0 && repeated.NewCars == 0, "SQL pilot: idempotent import");
    var current = store.Load(false);
    try
    {
        store.SavePerson(new PeoplePerson(0, "Пилотовый Запоздалый", "", "", "", "", "", true, 0),
            old.Revision);
        throw new Exception("SQL optimistic concurrency accepted a stale revision.");
    }
    catch (DBConcurrencyException) { /* expected */ }

    var people = current.People.Where(p => p.Fio.StartsWith("Тестовый" + id, StringComparison.Ordinal))
        .Take(3).Select(p => new AccessPerson(p.Id, p.Fio, p.Post, p.Birth)).ToArray();
    var cars = current.Cars.Where(c => c.Plate.StartsWith("TEST" + id, StringComparison.Ordinal))
        .Take(2).Select(c => new AccessCar(c.Id, c.Model, c.Plate)).ToArray();
    Require(people.Length == 3 && cars.Length == 2, "SQL pilot: imported identifiers");
    var oldOther = store.History(objects[1], "list").Count;
    var number = Guid.NewGuid();
    var access = Path.Combine(folder, "pilot-list-" + number.ToString("N") + ".xlsx");
    PeopleDocuments.ExportAccessList(access, null, "", "Проверка", people, cars, "");
    var snapshot = new AccessSnapshot(people, cars, "Проверка", "", null, "", "", number);
    var reg = store.RegisterExport(objects[0], "list", access,
        JsonSerializer.Serialize(snapshot), number, current.Revision);
    Require(reg.Id > 0, "SQL pilot: history id");
    var registered = store.History(objects[0], "list").Single(h => h.Id == reg.Id);
    Require(registered.File == access && registered.ObjectId == objects[0] &&
        registered.Kind == "list" && registered.Data == JsonSerializer.Serialize(snapshot),
        "SQL pilot: history file and JSON roundtrip");
    Require(store.WasRegistered(objects[0], number), "SQL pilot: registered operation");
    Require(!store.WasRegistered(objects[1], number), "SQL pilot: object isolation");
    Require(store.History(objects[1], "list").Count == oldOther, "SQL pilot: object history unchanged");

    var failedOp = Guid.NewGuid();
    var missing = Path.Combine(folder, "never-created-" + failedOp.ToString("N") + ".xlsx");
    var previous = store.History(objects[0], "list").Count;
    try
    {
        store.RegisterExport(objects[0], "list", missing,
            JsonSerializer.Serialize(snapshot with { OperationId = failedOp }),
            failedOp, reg.Revision);
        throw new Exception("SQL pilot: registered a nonexistent Excel file.");
    }
    catch (IOException) { /* expected */ }
    Require(store.History(objects[0], "list").Count == previous, "SQL pilot: no false journal");
    Console.WriteLine("PASS isolated SQL pilot: 1000 people, 200 cars, 2 objects, dedup, stale revision, file failure");
}
