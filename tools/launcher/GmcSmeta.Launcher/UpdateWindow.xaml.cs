using System.Windows;
using System.Windows.Threading;
using System.Windows.Media.Imaging;

namespace GmcSmeta.Launcher;

internal partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();
        var icon = BitmapDecoder.Create(new Uri("pack://application:,,,/app.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BrandIcon.Source = icon.Frames.OrderByDescending(frame => frame.PixelWidth).First();
    }

    public void SetStatus(string status, string details, double? percent = null, bool showProgress = true)
    {
        Status.Text = status;
        Details.Text = details;
        Progress.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = percent is null;
        Progress.Value = percent ?? 0;
    }
}

internal sealed class UpdateCard : IDisposable
{
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;
    private UpdateWindow? _window;

    public UpdateCard(string status, string details)
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        _thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                var window = new UpdateWindow();
                _window = window;
                window.SetStatus(status, details);
                window.Closed += (_, _) => _window = null;
                window.Show();
                ready.SetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                if (!ready.TrySetException(ex))
                    TraceUiFailure(ex);
            }
        }) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _dispatcher = ready.Task.GetAwaiter().GetResult();
    }

    public void Report(string status, string details, double? percent = null, bool showProgress = true)
    {
        _dispatcher.BeginInvoke(() => _window?.SetStatus(status, details, percent, showProgress));
    }

    public void Dispose()
    {
        _dispatcher.BeginInvoke(() =>
        {
            _window?.Close();
            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        });
        _thread.Join();
    }

    public static void TraceUiFailure(Exception ex) =>
        System.Diagnostics.Trace.TraceWarning("Update window: " + ex.Message);
}
