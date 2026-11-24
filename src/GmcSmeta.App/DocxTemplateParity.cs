using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Optional fail-closed publication layer for existing PeopleDocuments.FillDocx.
/// Does not replace the orders template engine or write SQL/history.
/// OrdersWindow integration requires a separately reviewed narrow hook.
/// </summary>
public static class DocxTemplateParity
{
    private static readonly XNamespace Word =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly Regex Placeholder = new(@"\{[^{}\r\n]{1,128}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Inspect the OOXML parts used by the existing Word-template renderer.</summary>
    public static void VerifyPackage(string path, bool requireFilled = false)
    {
        using var zip = ZipFile.OpenRead(path);
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in zip.Entries)
        {
            var name = part.FullName.Replace('\\', '/');
            if (name.StartsWith('/') ||
                name.Split('/').Any(segment => segment is "." or "..") ||
                !entries.Add(name))
                throw new InvalidDataException("Unsafe or duplicate DOCX package entry.");

            var isWordText = name.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ||
                (name.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase)) &&
                name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
            var isManifest = name is "[Content_Types].xml" or "_rels/.rels";
            if (!isWordText && !isManifest) continue;

            using var input = part.Open();
            var xml = XDocument.Load(input, LoadOptions.None);
            if (isWordText && requireFilled)
            {
                foreach (var paragraph in xml.Descendants(Word + "p"))
                {
                    var text = string.Concat(paragraph.Descendants(Word + "t")
                        .Select(t => t.Value));
                    if (Placeholder.IsMatch(text))
                        throw new InvalidDataException("DOCX contains an unresolved Word placeholder.");
                }
            }
        }

        if (!entries.Contains("[Content_Types].xml") ||
            !entries.Contains("_rels/.rels") ||
            !entries.Contains("word/document.xml"))
            throw new InvalidDataException("DOCX is missing an essential OOXML part.");
    }

    /// <summary>
    /// Render with the existing engine into a private staging file. Publish by an
    /// atomic same-directory rename (without overwrite), retrying name collisions.
    /// An invalid template never leaves a partially published DOCX.
    /// </summary>
    public static string FillAtomic(string template, string folder, string stem,
        IReadOnlyDictionary<string, string> mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (!File.Exists(template))
            throw new FileNotFoundException("Word template not found.", template);
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("Destination folder is required.", nameof(folder));
        if (!string.Equals(Path.GetExtension(template), ".docx",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected a DOCX Word template.");

        var safe = PeopleRules.SafeFileName(stem ?? "");
        if (string.IsNullOrWhiteSpace(safe))
            throw new ArgumentException("A valid output name is required.", nameof(stem));
        VerifyPackage(template);
        Directory.CreateDirectory(folder);
        var staging = Path.Combine(folder, ".gmc-docx-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            PeopleDocuments.FillDocx(template, staging, mapping);
            VerifyPackage(staging, requireFilled: true);
            for (var index = 1; index < 10000; index++)
            {
                var candidate = Path.Combine(folder,
                    safe + (index == 1 ? "" : $" ({index})") + ".docx");
                if (File.Exists(candidate)) continue;
                try
                {
                    File.Move(staging, candidate); // same-volume atomic, never overwrite
                    return candidate;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    // Another client published this name between the check and rename.
                }
            }
            throw new IOException("Unable to allocate a unique Word document filename.");
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }
}
