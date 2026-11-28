namespace GmcSmeta.Core.Tests;

/// <summary>
/// Приёмка PERF-031/035/036 (кампания perfacc-A, 29.11.2026): STATISTICS IO/TIME
/// следуют языку сессии, поэтому диагностическая сессия обязана закреплять
/// us_english — иначе на локализованном SQL Server сообщения фильтра
/// IsStatisticsMessage не совпадают и диагностика всегда INCOMPLETE.
/// </summary>
public sealed class RemoteSqlBenchDiagnosticsSourceTests
{
    private static (DirectoryInfo Root, string ToolPath) LocateTool()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tools", "RemoteSqlBench", "SqlDiagnostics.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        return (root!, Path.Combine(root!.FullName, "tools", "RemoteSqlBench", "SqlDiagnostics.cs"));
    }

    [Fact]
    public void StatisticsIoTimeCapturePinsEnglishSessionLanguage()
    {
        var (_, path) = LocateTool();
        var code = File.ReadAllText(path);
        Assert.Contains("SET LANGUAGE us_english;", code, StringComparison.Ordinal);
        var ioIndex = code.IndexOf("SET STATISTICS IO ON;", StringComparison.Ordinal);
        var languageIndex = code.IndexOf("SET LANGUAGE us_english;", StringComparison.Ordinal);
        Assert.True(ioIndex >= 0, "STATISTICS IO capture missing.");
        Assert.True(languageIndex >= 0 && languageIndex < ioIndex,
            "Language must be pinned before statistics are enabled.");
        // Сообщения фильтруются по английскому тексту — нельзя молча принять локализованный вывод.
        Assert.Contains("\"logical reads\"", code, StringComparison.Ordinal);
        Assert.Contains("\"CPU time\"", code, StringComparison.Ordinal);
    }
}
