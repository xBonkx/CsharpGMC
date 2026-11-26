using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public class EstimateDisplayTests
{
    [Theory]
    [InlineData("  МОНТАЖ установки", true)]
    [InlineData("Монтаж: работы", true)]
    [InlineData("Монтажная пена", false)]
    [InlineData("Демонтаж", true)]
    [InlineData("Наладка-оборудования", true)]
    [InlineData("Пуско-наладочные работы", true)]
    [InlineData("ПНР шкафа", true)]
    [InlineData("Работы по монтажу", true)]
    [InlineData("Установка", false)]
    [InlineData("Монтаж2", false)]
    [InlineData("монтаж_шкафа", true)]
    [InlineData("Демонтажный", false)]
    [InlineData("ПНР", false)]
    [InlineData("   ", false)]
    public void WorkNames_MatchPythonBoundaries(string name, bool work) =>
        Assert.Equal(work, EstimateStatistics.IsWorkName(name));

    private static EstimateRow Row(int id, int level, string name, bool system = false) =>
        new(system ? RowKind.System : RowKind.Position, level, id, id, false, "", name, "шт", 1, 1, 1, null, false, "");

    private static readonly EstimateRow[] Rows =
    {
        Row(1, 0, "Вентиляция", true), Row(2, 1, "Установка П1"),
        Row(3, 2, "Датчик температуры"), Row(4, 2, "Крепёж"),
        Row(5, 1, "Установка П2"), Row(6, 0, "Отопление", true), Row(7, 1, "Радиатор")
    };

    [Fact]
    public void Search_ComponentKeepsParentAndSystemWithoutSiblingComponents()
    {
        Assert.Equal(new[] { 1, 2, 3 }, EstimateFilter.Apply(Rows, "температуры датчик").Select(r => r.Id));
        Assert.Empty(EstimateFilter.Apply(Rows, "датчик крепёж"));
    }

    [Fact]
    public void Search_MatchingSystemOrParentKeepsWholeBranchAndClearingRestoresOrder()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, EstimateFilter.Apply(Rows, "ВЕНТИЛЯЦИЯ").Select(r => r.Id));
        Assert.Equal(new[] { 1, 2, 3, 4 }, EstimateFilter.Apply(Rows, "установка п1").Select(r => r.Id));
        Assert.Same(Rows, EstimateFilter.Apply(Rows, "  "));
        Assert.Equal(7, Rows.Length);
    }

    [Fact]
    public void DisplayStatistics_MatchPythonOnAll34Documents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "db", "base.db"))) dir = dir.Parent;
        if (dir is null) return;
        var reference = Path.Combine(dir!.FullName, "db", "statistics_python.json");
        Assert.True(File.Exists(reference), "Generate reference: python tools/preview_python.py --render");
        using var conn = Database.Open(Path.Combine(dir.FullName, "db", "base.db"), readOnly: true);
        using var json = JsonDocument.Parse(File.ReadAllText(reference));
        var count = 0;
        foreach (var doc in json.RootElement.EnumerateObject())
        {
            var estimate = EstimateReader.Load(conn, int.Parse(doc.Name));
            foreach (var (field, value) in new[] { ("mat", estimate.Statistics.Materials), ("smr", estimate.Statistics.Smr), ("work_rows", estimate.Statistics.WorkRows) })
                Assert.True(Math.Abs((double)value - doc.Value.GetProperty(field).GetDouble()) <= .005,
                    $"Document {doc.Name}, {field}: C# {value}, Python {doc.Value.GetProperty(field)}");
            Assert.Equal(estimate.Totals.Materials + estimate.Totals.Smr,
                estimate.Statistics.Materials + estimate.Statistics.Smr);
            count++;
        }
        Assert.Equal(34, count);
    }
}
