namespace GmcSmeta.Core.Domain;

public enum NavigationMode { Contracts, Kp, Archive }

public sealed record NavigationObject(ObjectRow Object, IReadOnlyList<DocRow> Documents);
public sealed record NavigationYear(string Year, IReadOnlyList<NavigationObject> Objects);
public sealed record NavigationDocument(DocRow Document, List<NavigationDocument> Children);

// Presentation follows the actual visible hierarchy, not just StageOf in storage.
// A stage whose parent is absent from this navigation mode is displayed as a file.
public sealed record NavigationDocumentHeader(DocRow Document, bool IsNested, bool HasChildren)
{
    public string IconRole => IsNested
        ? (Document.StageRest ? "Remainder" : "Stage")
        : (HasChildren ? "Parent" : "File");
    public bool IsItalic => IsNested && Document.StageRest;
    public bool ShortAgreementTitle { get; init; }
    public string DisplayName => AgreementPresentation.NavigationName(Document, ShortAgreementTitle);
}

/// <summary>Списки боковой панели как MainWindow._object_lists / _docs_for_mode в Python.</summary>
public static class Navigation
{
    public static IReadOnlyList<NavigationObject> Select(
        IEnumerable<ObjectRow> objects, IEnumerable<DocRow> documents,
        NavigationMode mode, string search = "")
    {
        var byObject = documents.ToLookup(d => d.ObjectId);
        // Несколько слов ищутся независимо от порядка и количества пробелов.
        var words = string.IsNullOrWhiteSpace(search)
            ? Array.Empty<string>()
            : search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<NavigationObject>();
        foreach (var obj in objects)
        {
            if (obj.Archived != (mode == NavigationMode.Archive)) continue;
            var all = byObject[obj.Id];
            var visible = all.Where(d => mode == NavigationMode.Archive ||
                (mode == NavigationMode.Kp ? d.Kind == "kp" : d.Kind is "main" or "dop")).ToList();
            if (visible.Count == 0 && !(mode == NavigationMode.Kp && !all.Any()) &&
                mode != NavigationMode.Archive) continue;
            // Слова могут быть распределены между названием объекта и ОДНИМ видимым
            // документом, но не между разными документами и не между режимами.
            if (words.Length > 0 &&
                !words.All(word => obj.Name.Contains(word, StringComparison.OrdinalIgnoreCase)) &&
                !visible.Any(d => words.All(word =>
                    obj.Name.Contains(word, StringComparison.OrdinalIgnoreCase) ||
                    d.NavigationTip.Contains(word, StringComparison.OrdinalIgnoreCase)))) continue;
            result.Add(new NavigationObject(obj, visible));
        }
        return result;
    }

    public static IReadOnlyList<NavigationYear> Years(IEnumerable<ObjectRow> objects,
        IEnumerable<DocRow> documents, NavigationMode mode, int currentYear, string search = "") =>
        Select(objects, documents, mode, search)
            .GroupBy(g => Year(mode == NavigationMode.Archive ? g.Object.ArchivedOn : g.Object.CreatedAt,
                mode == NavigationMode.Archive ? "Без года" : currentYear.ToString()))
            .OrderByDescending(g => g.Key != "Без года").ThenByDescending(g => g.Key, StringComparer.Ordinal)
            .Select(g => new NavigationYear(g.Key, g.ToList())).ToList();

    private static string Year(string date, string fallback) =>
        date.Length >= 4 && int.TryParse(date[..4], out var year) && year is >= 1 and <= 9999
            ? year.ToString("D4") : fallback;

    // Python MainWindow.refresh: independent documents first, stages in stage_no order,
    // remainder last. A stage whose parent isn't on this list remains an independent document.
    public static IReadOnlyList<NavigationDocument> Documents(IReadOnlyList<DocRow> documents)
    {
        var ids = documents.Select(d => d.Id).ToHashSet();
        bool Nested(DocRow d) => d.StageOf is int parent && ids.Contains(parent);
        var ordered = documents.Where(d => !Nested(d)).Concat(documents.Where(Nested)
            .OrderBy(d => d.StageRest).ThenBy(d => d.StageNo).ThenBy(d => d.Id));
        var roots = new List<NavigationDocument>();
        var nodes = new Dictionary<int, NavigationDocument>();
        foreach (var doc in ordered)
        {
            var node = new NavigationDocument(doc, new List<NavigationDocument>());
            if (doc.StageOf is int parent && nodes.TryGetValue(parent, out var parentNode))
                parentNode.Children.Add(node);
            else roots.Add(node);
            nodes[doc.Id] = node;
        }
        return roots;
    }

    public static string DocumentName(DocRow doc)
    {
        var number = string.IsNullOrWhiteSpace(doc.Number) ? "" : "№ " + doc.Number;
        var name = doc.Name.Trim();
        if (doc.Kind == "dop") number = number.Length == 0 ? "ДС" : "ДС " + number;
        var title = string.Join(" · ", new[] { number, name }.Where(x => x.Length > 0));
        return title.Length > 0 ? title : doc.Kind switch { "main" => "Договор", "dop" => "ДС", _ => "КП" };
    }

    public static string ShortDate(string date, int currentYear)
    {
        var text = date.Trim();
        var parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return text;
        var year = parts[2].Length == 4 ? parts[2] : "20" + parts[2][Math.Max(0, parts[2].Length - 2)..];
        return parts[0] + "." + parts[1] + (year == currentYear.ToString() ? "" : "." + year[^2..]);
    }

    public static (string Number, string Title, string Color) Phase(string key) => key switch
    {
        "volumes" => ("1", "Объёмы · Дима", "#64748B"),
        "prices" => ("2", "Цены · Наташа", "#2563EB"),
        "review" => ("3", "СМР и проверка · Андрей Викторович", "#D97706"),
        "ready" => ("4", "Готово к отправке", "#0891B2"),
        "sent" => ("5", "Отправлено заказчику", "#16A34A"),
        _ => ("?", "Этап подготовки не указан", "#94A3B8")
    };
}


// Immutable index for the currently displayed navigation mode. It is rebuilt
// whenever the tree is rebuilt, never while a user is typing.
public sealed class NavigationSearchIndex
{
    private sealed record Entry(int Id, string Name, string[] DocumentTips);
    private readonly Entry[] _entries;

    public NavigationSearchIndex(IEnumerable<NavigationObject> groups)
    {
        _entries = groups.Select(g => new Entry(g.Object.Id, g.Object.Name,
            g.Documents.Select(d => d.NavigationTip).ToArray())).ToArray();
    }

    public HashSet<int> Match(string? search)
    {
        var words = string.IsNullOrWhiteSpace(search) ? Array.Empty<string>() :
            search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new HashSet<int>();
        // Reuse a single scratch buffer for the whole query, not one array
        // and closures per object. Preserve one-document matching semantics.
        var unmatched = new string[words.Length];
        foreach (var entry in _entries)
        {
            var count = 0;
            foreach (var word in words)
                if (!entry.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
                    unmatched[count++] = word;
            if (count == 0) { result.Add(entry.Id); continue; }
            foreach (var tip in entry.DocumentTips)
            {
                var i = 0;
                while (i < count && tip.Contains(unmatched[i], StringComparison.OrdinalIgnoreCase)) i++;
                if (i != count) continue;
                result.Add(entry.Id);
                break;
            }
        }
        return result;
    }
}
