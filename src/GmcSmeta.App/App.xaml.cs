using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using GmcSmeta.Core;

namespace GmcSmeta.App;

public partial class App : Application
{
    // Stable across versioned install paths, separate from the Python production app.
    internal const string ShellAppId = "GMC.Smeta.Next";
    private static ImageSource? _applicationIcon;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    private static void InstallWindowBranding()
    {
        if (OperatingSystem.IsWindows())
        {
            try { _ = SetCurrentProcessExplicitAppUserModelID(ShellAppId); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(ApplyWindowIcon));
    }

    private static void ApplyWindowIcon(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || !ReferenceEquals(e.OriginalSource, window) || window.Icon is not null)
            return;
        if (_applicationIcon is null)
        {
            try
            {
                var uri = new Uri("pack://application:,,,/GMC-Smeta-Next;component/Assets/app.ico", UriKind.Absolute);
                var decoder = new IconBitmapDecoder(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 32)).First();
                if (frame.CanFreeze) frame.Freeze();
                _applicationIcon = frame;
            }
            catch (Exception ex) when (ex is System.IO.IOException or FormatException or
                NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // Optional visual branding must never prevent the main window opening.
                // Windows can still use the icon embedded in the executable.
                return;
            }
        }
        window.Icon = _applicationIcon;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!UpdateRestartCoordinator.WaitForPreviousProcess(e.Args))
        {
            MessageBox.Show("Предыдущий процесс не завершился. Откройте программу с ярлыка после его закрытия.",
                "ГМЦ-Смета — обновление", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        StartupTrace.Mark("app_startup_enter");
        InstallWindowBranding();
        ContextMenuIcons.InstallSeparatorNormalization();
        // Русская локаль для всех чисел и дат в окнах: 1 234 567,89 (а не 1,234,567.89).
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("ru-RU")));
        // Smoke-test in SQL-only mode needs a separate SQL Server test database.
        if (e.Args.Length == 1 && e.Args[0] == "--smoke-test")
        {
            try
            {
                var cs = Environment.GetEnvironmentVariable("GMC_SMETA_SQL_CONNECTION")
                    ?? throw new InvalidOperationException("GMC_SMETA_SQL_CONNECTION is required.");
                GmcSmeta.Core.Db.SqlServerWorkspace.RequirePilotDatabase(cs);
                using var conn = GmcSmeta.Core.Db.SqlServerDatabase.Open(cs);
                var doc = GmcSmeta.Core.Domain.Workspace.Documents(conn).First();
                _ = new EstimateView(conn, doc, cs);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Shutdown(1);
            }
            return;
        }
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GMC_SMETA_SQL_CONNECTION")))
        {
            StartupTrace.Mark("app_sql_connection_missing");
            MessageBox.Show("Откройте ГМЦ-Смету с рабочего ярлыка.", "ГМЦ-Смета",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        StartupTrace.Mark("app_main_window_ctor_begin");
        var window = new MainWindow();
        StartupTrace.Mark("app_main_window_ctor_end");
        window.InitializeWorkspaceNavigation();
        StartupTrace.Mark("app_workspace_navigation_attached");
        MainWindow = window;
        EventHandler? rendered = null;
        rendered = async (_, _) =>
        {
            window.ContentRendered -= rendered;
            StartupTrace.Mark("app_main_content_rendered");
            await window.InitializeWorkspaceAsync();
        };
        window.ContentRendered += rendered;
        window.Show();
        StartupTrace.Mark("app_main_show_returned");
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        UpdateRestartCoordinator.Attach(window);
    }
}
