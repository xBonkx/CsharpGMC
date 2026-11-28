using System.Text.RegularExpressions;

namespace GmcSmeta.Core.Tests;

// PERF-007.03 + PERF-014: структурные гарантии. Каждая структурная мутация
// сметы обязана отключать sparse readback; навигация не читает SQL на UI-потоке;
// очередь записи сохраняет single-funnel sparse-регистрацию.
public sealed class SparseAndNavigationSourceTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GmcSmeta.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }

    [Fact]
    public void StructuralEstimateWritesInvalidateSparseReadback()
    {
        // Все partial-файлы EstimateView, кроме воронки одиночных ячеек.
        var files = Directory.GetFiles(Path.Combine(RepoRoot(), "src", "GmcSmeta.App"), "EstimateView*.cs");
        Assert.Contains(files, f => f.EndsWith("EstimateView.QueueSave.cs"));
        foreach (var file in files)
        {
            var text = File.ReadAllText(file).Replace("\r\n", "\n");
            var lines = text.Split('\n');
            var isQueue = file.EndsWith("EstimateView.QueueSave.cs");
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd();
                if (!line.EndsWith("Interlocked.Increment(ref _pendingWrites);") &&
                    !line.EndsWith("System.Threading.Interlocked.Increment(ref _pendingWrites);")) continue;
                if (isQueue)
                {
                    // Воронка ячеек регистрирует sparse-правку после подтверждения.
                    Assert.Contains("NoteSparseCellWrite", text);
                    continue;
                }
                // Структурный маршрут обязан инвалидировать в пределах двух строк.
                var around = string.Join("\n", lines.Skip(Math.Max(0, i - 1)).Take(4));
                Assert.Matches(@"InvalidateSparseReadback\(\)", around);
            }
        }
    }

    [Fact]
    public void NavigationReadsNeverRunOnUiThread()
    {
        var appDir = Path.Combine(RepoRoot(), "src", "GmcSmeta.App");
        foreach (var file in Directory.GetFiles(appDir, "MainWindow*.cs"))
        {
            var text = File.ReadAllText(file).Replace("\r\n", "\n");
            // Тяжёлое чтение навигации — только через revision-fenced API на worker.
            Assert.DoesNotContain("Workspace.ReadNavigation(", text);
            if (file.EndsWith("MainWindow.Navigation.cs"))
            {
                Assert.Contains("Workspace.ReadConsistentNavigation(read", text);
                Assert.Contains("Task.Run(() =>", text);
                // Синхронного post-write reload больше нет.
                Assert.DoesNotContain("private void ReloadNavigationData()", text);
                Assert.Contains("private async Task ReloadNavigationDataAsync()", text);
            }
        }
    }

    [Fact]
    public void EstimateReloadWorkerKeepsSparsePathAndFullFallback()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GmcSmeta.App", "EstimateView.xaml.cs"))
            .Replace("\r\n", "\n");
        Assert.Contains("EstimateReader.ReadSparseSqlAsync(read, _contractId, sparseBase,", text);
        Assert.Contains("StartupTrace.Mark(\"estimate_sparse_fallback\");", text);
        Assert.Contains("EstimateReader.LoadSqlAsync(read, _contractId, token)", text);
        // Ревизионная ограда и гейт публикации не тронуты.
        Assert.Contains("estimate_revision_before_end", text);
        Assert.Contains("_reloadGate.CanApply(request,", text);
        Assert.Contains("AnySparseEditsSinceDrain()", text);
    }

    [Fact]
    public void SaveCellFieldSetMatchesSparseWhitelist()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GmcSmeta.Core", "Db", "SqlServerDatabase.cs"))
            .Replace("\r\n", "\n");
        var numeric = Regex.Match(source, @"numeric = field is ([^;]+);").Groups[1].Value;
        var textual = Regex.Match(source, @"textual = field is ([^;]+);").Groups[1].Value;
        var fields = Regex.Matches(numeric + " " + textual, @"""([a-z_]+)""")
            .Select(m => m.Groups[1].Value).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
        // Whitelist sparse — ровно множество полей SaveCell (не меньше, не больше).
        Assert.Equal(new[] { "name", "note", "price", "purchase_price", "qty", "unit", "work_price" }, fields);
        Assert.All(fields, f => Assert.True(GmcSmeta.Core.Domain.EstimateReader.IsSparseReadbackSafe(f), f));
        var special = Regex.Match(source, @"field is ""area_manual"" or ""basis""").Success;
        Assert.True(special); // специальные колонки существуют и обходятся отдельным маршрутом
        Assert.False(GmcSmeta.Core.Domain.EstimateReader.IsSparseReadbackSafe("area_manual"));
        Assert.False(GmcSmeta.Core.Domain.EstimateReader.IsSparseReadbackSafe("basis"));
    }
}
