using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using GmcSmeta.App;
using GmcSmeta.Core.Domain;

var root = Path.Combine(Path.GetTempPath(), "gmc-docx-golden-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var results = new List<(string Name, string Status, string Detail)>();
XNamespace wordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
var template = Path.Combine(root, "synthetic-source.docx");
Fixture(template);

void Run(string name, Action check)
{
    try
    {
        check();
        results.Add((name, "PASS", "synthetic"));
        Console.WriteLine("PASS " + name);
    }
    catch (Exception exception)
    {
        results.Add((name, "FAIL", exception.GetType().Name + ": " + exception.Message));
        Console.Error.WriteLine("FAIL " + name + ": " + exception);
    }
}

Run("OOXML split runs, table, Unicode/XML escaping", () =>
{
    var path = DocxTemplateParity.FillAtomic(template, Folder("split"), "Приказ",
        Values());
    var document = ReadXml(path, "word/document.xml");
    var text = Text(document);
    Require(text.Contains("Иванова Ивана Ивановича & <инженер>", StringComparison.Ordinal),
        "Cyrillic and XML special characters not preserved");
    Require(text.Contains("№ 03/03-01", StringComparison.Ordinal), "Number not rendered");
    Require(document.Descendants(wordNs + "tbl").Any(), "Word table was lost");
    Require(document.Descendants(wordNs + "b").Any(), "Bold run formatting was lost");
    Require(!text.Contains("{ФИО_", StringComparison.Ordinal), "Split placeholder remained");
});
Run("OOXML linked header/footer, dates and signatures", () =>
{
    var path = DocxTemplateParity.FillAtomic(template, Folder("headers"), "Приказ", Values());
    var header = Text(ReadXml(path, "word/header1.xml"));
    var footer = Text(ReadXml(path, "word/footer1.xml"));
    Require(header.Contains("Синтетический заказчик", StringComparison.Ordinal),
        "Header split-runs not filled");
    Require(footer.Contains("«03» марта 2026 г.", StringComparison.Ordinal) &&
            footer.Contains("03/03-01", StringComparison.Ordinal),
        "Footer date/number not filled");
    Require(Text(ReadXml(path, "word/document.xml")).Contains("Подписант А.А.", StringComparison.Ordinal),
        "Signature not filled");
    var rels = ReadXml(path, "word/_rels/document.xml.rels");
    Require(rels.Descendants().Any(x => (string?)x.Attribute("Target") == "header1.xml") &&
            rels.Descendants().Any(x => (string?)x.Attribute("Target") == "footer1.xml"),
        "Word header/footer relationships missing");
});
Run("Repeat generation and no template mutation", () =>
{
    var original = SHA256.HashData(File.ReadAllBytes(template));
    var one = DocxTemplateParity.FillAtomic(template, Folder("repeat"), "Приказ", Values());
    var two = DocxTemplateParity.FillAtomic(template, Folder("repeat"), "Приказ", Values());
    Require(Path.GetFileName(two) == "Приказ (2).docx", "Archive collision not versioned");
    Require(Text(ReadXml(one, "word/document.xml")) == Text(ReadXml(two, "word/document.xml")),
        "Repeat output changes rendered Word content");
    Require(original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(template))),
        "Input template was modified");
    Require(File.Exists(one), "First archived output was replaced");
});
Run("Missing Word template refuses publication", () =>
{
    var folder = Folder("missing");
    Expect<FileNotFoundException>(() =>
        DocxTemplateParity.FillAtomic(Path.Combine(root, "absent.docx"), folder, "Приказ", Values()));
    Require(!Directory.EnumerateFiles(folder).Any(), "Missing input published an output");
});
Run("Unknown placeholders clean staging and fail closed", () =>
{
    var folder = Folder("unknown");
    var incomplete = Values().Where(x => x.Key != "{ДИРЕКТОР}")
        .ToDictionary(x => x.Key, x => x.Value);
    Expect<InvalidDataException>(() =>
        DocxTemplateParity.FillAtomic(template, folder, "Приказ", incomplete));
    Require(!Directory.EnumerateFiles(folder).Any(), "Failed rendering left partial files");
});
Run("Invalid target path preserves sentinel", () =>
{
    var blocked = Path.Combine(root, "blocked-folder");
    File.WriteAllText(blocked, "sentinel", Encoding.UTF8);
    Expect<IOException>(() =>
        DocxTemplateParity.FillAtomic(template, blocked, "Приказ", Values()));
    Require(File.ReadAllText(blocked, Encoding.UTF8) == "sentinel",
        "Unrelated user file was modified");
});
Run("Corrupt and duplicate OOXML reject", () =>
{
    var corrupt = Path.Combine(root, "corrupt.docx");
    File.WriteAllText(corrupt, "not OOXML", Encoding.UTF8);
    Expect<InvalidDataException>(() => DocxTemplateParity.VerifyPackage(corrupt));
    var duplicate = Path.Combine(root, "duplicate.docx");
    using (var zip = ZipFile.Open(duplicate, ZipArchiveMode.Create))
    {
        Part(zip, "word/document.xml", "<root/>");
        Part(zip, "word/document.xml", "<root/>");
    }
    Expect<InvalidDataException>(() => DocxTemplateParity.VerifyPackage(duplicate));
});
Run("OOXML traversal path rejects without extraction", () =>
{
    var malicious = Path.Combine(root, "traversal.docx");
    using (var zip = ZipFile.Open(malicious, ZipArchiveMode.Create))
        Part(zip, "../outside.txt", "poison");
    Expect<InvalidDataException>(() => DocxTemplateParity.VerifyPackage(malicious));
    Require(!File.Exists(Path.Combine(root, "outside.txt")), "Archive entry escaped");
});
Run("Stock Python-origin order templates render synthetic mappings", () =>
{
    var assembly = typeof(PeopleDocuments).Assembly;
    var resources = assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("GmcSmeta.App.OrderTemplates.", StringComparison.Ordinal) &&
                    n.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        .OrderBy(n => n, StringComparer.Ordinal).ToArray();
    Require(resources.Length == 3, "Expected exactly three bundled Python Word templates");
    var site = new AccessSite(1, "Тестовая стройка", "", "Тестовая стройка", "", "",
        "Синтетический заказчик", "8", "01.01.2026");
    var person = new PeoplePerson(1, "Петров Пётр Петрович", "главный инженер",
        "", "", "", "", true, 0);
    var mapping = PeopleDocuments.OrderMapping(site, person, null,
        new DateTime(2026, 3, 3), "03/03-01", "№8 от 01 января 2026 г.", site.Name);
    for (var i = 0; i < resources.Length; i++)
    {
        var input = Path.Combine(Folder("stock"), $"stock-{i}.docx");
        using (var source = assembly.GetManifestResourceStream(resources[i]) ??
               throw new InvalidDataException("Embedded order template not found"))
        using (var target = new FileStream(input, FileMode.CreateNew, FileAccess.Write))
            source.CopyTo(target);
        var output = DocxTemplateParity.FillAtomic(input, Folder("stock-output"),
            $"Приказ {i + 1}", mapping);
        DocxTemplateParity.VerifyPackage(output, requireFilled: true);
        Require(Text(ReadXml(output, "word/document.xml")).Length > 10,
            "Bundled order form is empty");
    }
});

var report = new List<string>
{
    "# DOCX golden — synthetic data only",
    "",
    "No production SQL, user templates, or archive folders were accessed.",
    "| Test | Status | Detail |",
    "|---|---|---|"
};
report.AddRange(results.Select(x => $"| {x.Name} | {x.Status} | {x.Detail.Replace("|", "/").Replace("\n", " ")} |"));
report.Add("");
report.Add($"Executed: {results.Count}; PASS: {results.Count(x => x.Status == "PASS")}; FAIL: {results.Count(x => x.Status == "FAIL")}; SKIP: 0.");
report.Add("SKIP: actual Word/LibreOffice layout and SQL Pilot integration (manual environment required).");
File.WriteAllLines(Path.Combine(root, "RESULTS.md"), report, Encoding.UTF8);
Console.WriteLine("Synthetic artifacts and report: " + root);
Console.WriteLine($"Executed {results.Count}, PASS {results.Count(x => x.Status == "PASS")}, FAIL {results.Count(x => x.Status == "FAIL")}");
if (results.Any(x => x.Status == "FAIL")) Environment.ExitCode = 1;

string Folder(string name)
{
    var path = Path.Combine(root, name);
    Directory.CreateDirectory(path);
    return path;
}
static Dictionary<string, string> Values() => new()
{
    ["{ФИО_КОГО}"] = "Иванова Ивана Ивановича & <инженер>",
    ["{ЗАКАЗЧИК}"] = "Синтетический заказчик",
    ["{ДАТА}"] = "«03» марта 2026 г.",
    ["{НОМЕР}"] = "03/03-01",
    ["{ДИРЕКТОР}"] = "Подписант А.А."
};
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
static void Expect<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidDataException("Expected " + typeof(T).Name + " was not thrown.");
}
static void Part(ZipArchive zip, string name, string content)
{
    using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
    writer.Write(content);
}
static XDocument ReadXml(string path, string name)
{
    using var zip = ZipFile.OpenRead(path);
    using var stream = (zip.GetEntry(name) ??
        throw new InvalidDataException("Missing OOXML part: " + name)).Open();
    return XDocument.Load(stream);
}
static string Text(XDocument doc)
{
    XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    return string.Concat(doc.Descendants(w + "t").Select(x => x.Value));
}
static void Fixture(string path)
{
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    Part(zip, "[Content_Types].xml", """
        <?xml version="1.0" encoding="UTF-8"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
        <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
        <Default Extension="xml" ContentType="application/xml"/>
        <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
        <Override PartName="/word/header1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml"/>
        <Override PartName="/word/footer1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml"/>
        </Types>
        """);
    Part(zip, "_rels/.rels", """
        <?xml version="1.0" encoding="UTF-8"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
        <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
        </Relationships>
        """);
    Part(zip, "word/_rels/document.xml.rels", """
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
        <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/header" Target="header1.xml"/>
        <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer" Target="footer1.xml"/>
        </Relationships>
        """);
    Part(zip, "word/document.xml", """
        <?xml version="1.0" encoding="UTF-8"?>
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                    xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
        <w:body>
          <w:p><w:r><w:rPr><w:b/></w:rPr><w:t>№ {НО</w:t></w:r><w:r><w:t>МЕР}</w:t></w:r></w:p>
          <w:tbl><w:tr><w:tc><w:p><w:r><w:t>{ФИО_</w:t></w:r>
              <w:r><w:t>КОГО}</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
          <w:p><w:r><w:t>{ДИРЕКТОР}</w:t></w:r></w:p>
          <w:sectPr><w:headerReference w:type="default" r:id="rId2"/>
                     <w:footerReference w:type="default" r:id="rId3"/></w:sectPr>
        </w:body>
        </w:document>
        """);
    Part(zip, "word/header1.xml", """
        <w:hdr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
        <w:p><w:r><w:t>{ЗАК</w:t></w:r><w:r><w:t>АЗЧИК}</w:t></w:r></w:p>
        </w:hdr>
        """);
    Part(zip, "word/footer1.xml", """
        <w:ftr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
        <w:p><w:r><w:t>{ДАТА}</w:t></w:r><w:r><w:t> {НОМЕР}</w:t></w:r></w:p>
        </w:ftr>
        """);
}
