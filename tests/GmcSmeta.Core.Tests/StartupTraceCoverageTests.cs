namespace GmcSmeta.Core.Tests;

public sealed class StartupTraceCoverageTests
{
    [Theory]
    [InlineData("src/GmcSmeta.Core/StartupTrace.cs", "trace_build_debug")]
    [InlineData("src/GmcSmeta.Core/StartupTrace.cs", "trace_build_release")]
    [InlineData("tools/launcher/GmcSmeta.Launcher/AppInstaller.cs", "launcher_child_pid")]
    [InlineData("tools/launcher/GmcSmeta.Launcher/AppInstaller.cs", "launcher_archive_extract")]
    [InlineData("tools/launcher/GmcSmeta.Launcher/AppInstaller.cs", "launcher_install_pointer_publish")]
    [InlineData("tools/launcher/GmcSmeta.Launcher/GitHubReleases.cs", "launcher_auth_noninteractive")]
    [InlineData("tools/launcher/GmcSmeta.Launcher/GitHubReleases.cs", "launcher_auth_gcm_timeout")]
    [InlineData("tools/launcher/GmcSmeta.Launcher/GitHubReleases.cs", "launcher_asset_transfer_with_hash")]
    [InlineData("tools/launcher/GmcSmeta.Launcher/UpdateWindow.xaml.cs", "launcher_update_card_rendered")]
    [InlineData("src/GmcSmeta.App/App.xaml.cs", "app_main_content_rendered")]
    [InlineData("src/GmcSmeta.App/GmcSmeta.App.csproj", "<StartupObject>GmcSmeta.App.StartupEntryPoint</StartupObject>")]
    [InlineData("src/GmcSmeta.App/StartupEntryPoint.cs", "app_application_initialize_begin")]
    [InlineData("src/GmcSmeta.App/StartupEntryPoint.cs", "app_application_initialize_end")]
    [InlineData("src/GmcSmeta.App/StartupEntryPoint.cs", "app_resource_dictionary_counts")]
    [InlineData("src/GmcSmeta.App/StartupEntryPoint.cs", "return app.Run();")]
    [InlineData("src/GmcSmeta.App/MainWindow.StartupTrace.cs", "main_workspace_ready_rendering")]
    [InlineData("src/GmcSmeta.App/MainWindow.StartupTrace.cs", "CompositionTarget.Rendering -= render")]
    public void CriticalStartupPhasesRemainInstrumented(string path, string marker)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, path)))
            root = root.Parent;
        Assert.NotNull(root);
        Assert.Contains(marker, File.ReadAllText(Path.Combine(root!.FullName, path)));
    }
}
