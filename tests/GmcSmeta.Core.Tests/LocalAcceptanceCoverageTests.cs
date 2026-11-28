namespace GmcSmeta.Core.Tests;

public sealed class LocalAcceptanceCoverageTests
{
    [Fact]
    public void OfflineRecorderSeparatesSqlEnvironmentAndRunsAllCheckHosts()
    {
        var script = Source("tools/local-qa/Invoke-LocalAcceptance.ps1");
        foreach (var id in new[] { "wpf-offline", "export-offline", "procurement-export-offline",
            "contract-export-offline", "layout-performance-offline" })
            Assert.Contains("Invoke-OfflineRecorded '" + id + "'", script);
        Assert.Contains("'GMC_SMETA_SQL_CONNECTION', 'GMC_CONTRACT_EXPORT_SQL_PILOT'", script);
        Assert.Contains("SetEnvironmentVariable($name, $null, 'Process')", script);
        Assert.Contains("SetEnvironmentVariable($name, $saved[$name], 'Process')", script);
        Assert.Contains("finally {", script);
        Assert.Contains("--offline-check", script);
        Assert.Contains("--layout-performance-check", script);
        Assert.Contains("Record-OfflineSkips 'Release build failed'", script);
        Assert.Contains("Where-Object status -eq 'FAIL').Count -gt 0) { exit 1 }", script);
        Assert.DoesNotContain("command = $MyInvocation.Line", script);
    }

    [Fact]
    public void StartupTraceSeparatesRealInitializeComponentAndOtherSetup()
    {
        var source = Source("src/GmcSmeta.App/MainWindow.xaml.cs");
        Assert.Contains("InitializeComponent();\n        StartupTrace.Mark(\"main_initialize_component_end\");",
            source.Replace("\r\n", "\n"));
        Assert.Contains("main_settings_apply_begin", source);
        Assert.Contains("main_settings_apply_end", source);
        Assert.Contains("main_workspace_dispatcher_idle", source);
        Assert.Contains("if (StartupTrace.Enabled)", source);
        var app = Source("src/GmcSmeta.App/App.xaml.cs");
        Assert.Contains("app_previous_process_wait_begin", app);
        Assert.Contains("app_previous_process_wait_failed", app);
        Assert.Contains("app_icon_decode_begin", app);
        Assert.Contains("app_icon_decode_failed", app);
    }

    [Fact]
    public void BenchmarkSeparatesNonPooledConnectionWithoutClearingSharedCaches()
    {
        var source = Source("tools/RemoteSqlBench/Program.cs");
        Assert.Contains("Pooling = false", source);
        Assert.Contains("guarded_open_nonpooled", source);
        Assert.Contains("guarded_open_warm_pool", source);
        Assert.DoesNotContain("ClearAllPools(", source);
        Assert.DoesNotContain("DBCC FREEPROCCACHE", source);
    }

    private static string Source(string relative)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, relative)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, relative));
    }
}
