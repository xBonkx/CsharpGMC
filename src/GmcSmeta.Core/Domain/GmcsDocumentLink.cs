using System.Text.Json;

namespace GmcSmeta.Core.Domain;

/// <summary>Version 1 portable document links. No database or UI dependencies.</summary>
public sealed record GmcsDocumentLink(int ContractId, int ObjectId, string ObjectName,
    string Title, string Kind, string Number, string Date)
{
    public static GmcsDocumentLink? Parse(string json)
    {
        using var jd = JsonDocument.Parse(json);
        var root = jd.RootElement;
        if (root.TryGetProperty("type", out var t) && t.GetString() != "gmc-smeta-link") return null;
        int Id(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : 0;
        string Text(string name) => root.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
        return new(Id("contract_id"), Id("object_id"), Text("object"), Text("title"),
            Text("kind"), Text("number"), Text("date"));
    }

    // Preserve the v1 precedence and first-match order of the existing shell.
    public int? Resolve(IReadOnlyList<ObjectRow> objects, IReadOnlyList<DocRow> documents)
    {
        if (ContractId > 0 && documents.FirstOrDefault(d => d.Id == ContractId) is { } direct &&
            (ObjectId == 0 || direct.ObjectId == ObjectId ||
                objects.FirstOrDefault(o => o.Id == direct.ObjectId)?.Name == ObjectName))
            return ContractId;
        var obj = objects.FirstOrDefault(o => o.Name == ObjectName);
        if (obj is null) return null;
        var candidates = documents.Where(d => d.ObjectId == obj.Id);
        if (!string.IsNullOrEmpty(Title) && candidates.FirstOrDefault(d => d.Title == Title) is { } title)
            return title.Id;
        return candidates.FirstOrDefault(d => d.Kind == Kind && d.Number == Number && d.Date == Date)?.Id;
    }

    public static string Serialize(DocRow doc, string objectName, string user, DateTime savedAt)
    {
        var data = new
        {
            type = "gmc-smeta-link",
            version = 1,
            // Links open against the active connection; never embed SQL credentials.
            db = "",
            contract_id = doc.Id,
            object_id = doc.ObjectId,
            @object = objectName,
            title = doc.Title,
            kind = doc.Kind,
            number = doc.Number,
            date = doc.Date,
            saved_by = user,
            saved_at = savedAt.ToString("dd.MM.yyyy HH:mm"),
            hint = "Двойной клик открывает этот документ в GMC-Smeta.",
        };
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }
}
