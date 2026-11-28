using GmcSmeta.Core;

namespace GmcSmeta.App;

/// <summary>Same WPF Application startup sequence as the generated entry point,
/// with opt-in measurements around resources loaded before OnStartup.</summary>
internal static class StartupEntryPoint
{
    [STAThread]
    public static int Main()
    {
        StartupTrace.Mark("app_process_entry");
        StartupTrace.Mark("app_application_ctor_begin");
        App app;
        using (StartupTrace.Measure("app_application_ctor"))
            app = new App();
        StartupTrace.Mark("app_application_ctor_end");
        StartupTrace.Mark("app_application_initialize_begin");
        using (StartupTrace.Measure("app_application_initialize"))
            app.InitializeComponent();
        StartupTrace.Mark("app_application_initialize_end");
        if (StartupTrace.Enabled)
            StartupTrace.Sample("app_resource_dictionary_counts",
                app.Resources.Count, app.Resources.MergedDictionaries.Count);
        StartupTrace.Mark("app_run_begin");
        return app.Run();
    }
}
