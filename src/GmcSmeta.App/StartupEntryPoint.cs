using System.Runtime.CompilerServices;
using System.Windows;
using GmcSmeta.Core;
using GmcSmeta.Core.Demo;

namespace GmcSmeta.App;

/// <summary>Same WPF Application startup sequence as the generated entry point,
/// with opt-in measurements around resources loaded before OnStartup.</summary>
internal static class StartupEntryPoint
{
    [STAThread]
    public static int Main()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var demo = DemoMode.IsRequested(args);
        var smoke = DemoMode.IsSmokeRequested(args);
        try
        {
            DemoMode.RequireExplicitLaunch(args,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.ProcessPath);
            if (demo)
            {
                DemoMode.ConfigureProcess(args);
                if (smoke) DemoMode.ResetSmokeReport();
            }
        }
        catch (Exception ex)
        {
            if (smoke) DemoMode.ReportSmokeFailure("configure-demo", ex);
            else MessageBox.Show(ex.Message, "ГМЦ-Смета — ТЕСТ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        return RunApplication();
    }

    // Do not touch StartupTrace before DEMO has removed inherited trace paths.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApplication()
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
