// P0 #55: deterministic, synthetic, OFFLINE workflow fixture builder.
// This utility never connects to a database. Import to a disposable Pilot DB
// only through a separately reviewed, provenance-guarded integration harness.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GmcSmeta.Workflow.Acceptance;

internal static class Program
{
    private static readonly int[] Sizes = [300, 2500, 10000];
    private static readonly string[] Files =
        ["objects.csv", "contracts.csv", "systems.csv", "positions.csv", "acts.csv", "shipments.csv"];

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2 || args[0] is not ("generate" or "verify"))
            {
                Console.Error.WriteLine("Usage: dotnet run --project tests/GmcSmeta.Workflow.Acceptance -- [generate|verify] <directory>");
                return 2;
            }
            var root = Path.GetFullPath(args[1]);
            if (args[0] == "generate")
            {
                if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                    throw new InvalidOperationException("Refusing to overwrite a non-empty fixture directory.");
                Directory.CreateDirectory(root);
                foreach (var size in Sizes) Generate(root, size);
            }
            Verify(root);
            Console.WriteLine("PASS: 3 synthetic fixture sets; 300, 2500, 10000 positions; integrity and SHA-256 verified.");
            Console.WriteLine("SQL and Windows UI: SKIP — this offline command never runs them.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.Message);
            return 1;
        }
    }

    private static string N(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Key(int size) => "QA" + size.ToString(CultureInfo.InvariantCulture);
    private static string Position(int size, int row) => Key(size) + "-P" + row.ToString("D6", CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value ? "1" : "0";

    private static void Generate(string root, int size)
    {
        string k = Key(size);
        var path = Path.Combine(root, k);
        Directory.CreateDirectory(path);
        var datasets = new Dictionary<string, (string[] Header, List<string[]> Rows)>(StringComparer.Ordinal)
        {
            ["objects.csv"] = (["object_key", "name", "synthetic_only"],
                [[k + "-OBJECT", "SYNTHETIC WORKFLOW " + size, "1"]]),
            ["contracts.csv"] = (["contract_key", "object_key", "kind", "parent_contract_key", "locked", "scenario"],
                [
                    [k + "-MAIN", k + "-OBJECT", "main", "", "0", "editable base"],
                    [k + "-DS", k + "-OBJECT", "dop", k + "-MAIN", "1", "locked synthetic amendment"],
                    [k + "-REST", k + "-OBJECT", "rest", k + "-MAIN", "0", "unposted synthetic remainder"]
                ]),
            ["systems.csv"] = (["system_key", "contract_key", "name", "sort_order"], []),
            ["positions.csv"] = (["row_key", "contract_key", "system_key", "parent_row_key",
                "name", "unit", "qty", "retail_unit", "purchase_unit", "work_unit", "supplier",
                "manual_retail", "manual_purchase", "component", "ds_reference",
                "signed_ks_reference", "posted_shipment_reference"], []),
            ["acts.csv"] = (["act_key", "contract_key", "row_key", "qty", "signed"], []),
            ["shipments.csv"] = (["shipment_key", "contract_key", "row_key", "qty", "posted"], [])
        };

        for (int s = 1; s <= 8; s++)
            datasets["systems.csv"].Rows.Add([k + "-S" + s.ToString("D2"),
                k + "-MAIN", "Synthetic HVAC system " + s, s.ToString(CultureInfo.InvariantCulture)]);

        decimal expectedRetail = 0, expectedPurchase = 0;
        int manualRetail = 0, manualPurchase = 0, components = 0, signed = 0, posted = 0, ds = 0;
        for (int i = 1; i <= size; i++)
        {
            var quantity = 1m + (i % 13) * 0.5m;
            var retail = 75m + i % 97;
            var purchase = 40m + i % 41;
            var work = 10m + i % 13;
            bool isComponent = i % 19 == 0;
            bool isManualRetail = i % 11 == 0;
            bool isManualPurchase = i % 7 == 0;
            bool isSigned = i % 113 == 0;
            bool isPosted = i % 79 == 0;
            bool isDs = i % 37 == 0;
            var row = Position(size, i);
            var parent = isComponent ? Position(size, i - 1) : "";
            datasets["positions.csv"].Rows.Add([
                row, k + "-MAIN", k + "-S" + (1 + i % 8).ToString("D2"), parent,
                "Synthetic component / material " + i, "шт", N(quantity), N(retail), N(purchase),
                N(work), "SYNTHETIC SUPPLIER " + i % 5,
                Bool(isManualRetail), Bool(isManualPurchase), Bool(isComponent),
                isDs ? k + "-DS" : "", Bool(isSigned), Bool(isPosted)
            ]);
            if (isSigned)
                datasets["acts.csv"].Rows.Add([k + "-KS-SIGNED", k + "-MAIN", row, N(quantity / 2m), "1"]);
            if (isPosted)
                datasets["shipments.csv"].Rows.Add([k + "-SHIP-POSTED", k + "-MAIN", row, N(quantity / 2m), "1"]);
            expectedRetail += quantity * retail;
            expectedPurchase += quantity * purchase;
            manualRetail += isManualRetail ? 1 : 0;
            manualPurchase += isManualPurchase ? 1 : 0;
            components += isComponent ? 1 : 0;
            signed += isSigned ? 1 : 0;
            posted += isPosted ? 1 : 0;
            ds += isDs ? 1 : 0;
        }
        var fileManifest = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            var (headers, rows) = datasets[file];
            var csvPath = Path.Combine(path, file);
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", headers.Select(EscapeCsv)));
            foreach (var row in rows)
                sb.AppendLine(string.Join(",", row.Select(EscapeCsv)));
            File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(false));
            fileManifest.Add(file, new { rows = rows.Count, sha256 = Hash(csvPath) });
        }
        var metadata = new
        {
            schema_version = 1,
            description = "OFFLINE SYNTHETIC QA INTENT — NOT DATABASE SEED OR SQL ACCEPTANCE",
            fixture_key = k,
            position_count = size,
            system_count = 8,
            contract_count = 3,
            manual_retail_count = manualRetail,
            manual_purchase_count = manualPurchase,
            component_count = components,
            signed_act_line_count = signed,
            posted_shipment_line_count = posted,
            ds_reference_count = ds,
            expected_retail_total = N(expectedRetail),
            expected_purchase_total = N(expectedPurchase),
            files = fileManifest
        };
        File.WriteAllText(Path.Combine(path, "manifest.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
    }

    private static string EscapeCsv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void Verify(string root)
    {
        foreach (int size in Sizes)
        {
            var path = Path.Combine(root, Key(size));
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")));
            var top = json.RootElement;
            Check(top.GetProperty("schema_version").GetInt32() == 1, "Schema version");
            Check(top.GetProperty("fixture_key").GetString() == Key(size), "Wrong fixture key");
            Check(top.GetProperty("position_count").GetInt32() == size, "Wrong position count");
            Check(top.GetProperty("system_count").GetInt32() == 8, "Missing synthetic systems");
            Check(top.GetProperty("contract_count").GetInt32() == 3, "Missing DS/rest scenario");
            Check(top.GetProperty("manual_retail_count").GetInt32() > 0, "Missing manual retail");
            Check(top.GetProperty("manual_purchase_count").GetInt32() > 0, "Missing manual purchase");
            Check(top.GetProperty("signed_act_line_count").GetInt32() > 0, "Missing signed act");
            Check(top.GetProperty("posted_shipment_line_count").GetInt32() > 0, "Missing posted qty");
            Check(top.GetProperty("ds_reference_count").GetInt32() > 0, "Missing DS references");
            var manifest = top.GetProperty("files");
            foreach (var f in Files)
            {
                var file = manifest.GetProperty(f);
                var csv = Path.Combine(path, f);
                Check(Hash(csv) == file.GetProperty("sha256").GetString(), "SHA-256 mismatch: " + csv);
                int actual = File.ReadLines(csv).Skip(1).Count();
                Check(actual == file.GetProperty("rows").GetInt32(), "Row count mismatch: " + csv);
                if (f == "positions.csv")
                {
                    Check(actual == size, "Wrong number of positions");
                    var ids = File.ReadLines(csv).Skip(1)
                        .Select(line => line.Split(',')[0].Trim('"')).ToArray();
                    Check(ids.Distinct(StringComparer.Ordinal).Count() == size, "Duplicate row IDs");
                }
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
