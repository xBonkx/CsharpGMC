using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public class NavigationParityTests
{
    [Fact]
    public void AllThreeTrees_MatchActualPythonWidgets()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "db", "base.db"))) dir = dir.Parent;
        if (dir is null) return;
        var reference = Path.Combine(dir!.FullName, "db", "navigation_python.json");
        Assert.True(File.Exists(reference), "Generate reference: python tools/preview_python.py --render");
        using var conn = Database.Open(Path.Combine(dir.FullName, "db", "base.db"), readOnly: true);
        using var json = JsonDocument.Parse(File.ReadAllText(reference));
        var objects = Workspace.Objects(conn);
        var documents = Workspace.Documents(conn);
        var count = 0;
        foreach (var (name, mode) in new[] { ("contracts", NavigationMode.Contracts), ("kp", NavigationMode.Kp), ("archive", NavigationMode.Archive) })
        {
            var py = FlattenPython(json.RootElement.GetProperty(name), "").ToList();
            var cs = new List<string>();
            foreach (var year in Navigation.Years(objects, documents, mode, DateTime.Today.Year))
            {
                var y = "year:" + year.Year;
                cs.Add(y + "|" + year.Year + "||");
                foreach (var obj in year.Objects)
                {
                    var o = y + "/obj:" + obj.Object.Id;
                    cs.Add(o + "|" + obj.Object.Name + "||");
                    foreach (var doc in Navigation.Documents(obj.Documents)) FlattenCSharp(doc, o, cs);
                }
            }
            Assert.Equal(py, cs);
            count += cs.Count;
        }
        Assert.True(count > documents.Count, $"Compared only {count} nodes");
    }

    private static IEnumerable<string> FlattenPython(JsonElement nodes, string parent)
    {
        foreach (var node in nodes.EnumerateArray())
        {
            var path = (parent.Length == 0 ? "" : parent + "/") + node.GetProperty("kind").GetString() + ":" + node.GetProperty("id");
            yield return path + "|" + node.GetProperty("text").GetString() + "|" + node.GetProperty("date").GetString() + "|" +
                string.Join(",", node.GetProperty("phase").EnumerateArray().Select(x => x.ToString()));
            foreach (var child in FlattenPython(node.GetProperty("children"), path)) yield return child;
        }
    }

    private static void FlattenCSharp(NavigationDocument node, string parent, List<string> rows)
    {
        var doc = node.Document;
        var path = parent + "/contract:" + doc.Id;
        rows.Add(path + "|" + doc.NavigationName + "|" + doc.NavigationDate + "|" +
            (doc.PhaseNumber.Length > 0 ? doc.PhaseNumber + "," + doc.PhaseColor : ""));
        foreach (var child in node.Children) FlattenCSharp(child, path, rows);
    }
}
