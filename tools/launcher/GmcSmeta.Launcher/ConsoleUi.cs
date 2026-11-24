using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace GmcSmeta.Launcher;

/// <summary>UI adapter for the existing synchronous launcher. Only the window runs on the STA thread.</summary>
internal sealed class ConsoleUi : IDisposable
{
    private readonly bool _silent;
    private readonly bool _console;
    private readonly CancellationTokenSource _cancel = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _closed = new(false);
    private UpdateWindow? _window;
    private Thread? _thread;
    private Exception? _startupError;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    public ConsoleUi(bool silent = false, bool commandLine = false)
    {
        _silent = silent;
        // WinExe never opens a console for a desktop shortcut. Preserve diagnostics
        // for --check/--help invoked from an existing terminal.
        _console = !_silent && commandLine && AttachConsole(uint.MaxValue);
        if (_console) Console.OutputEncoding = System.Text.Encoding.UTF8;
    }

    public CancellationToken Token => _cancel.Token;
    public void Show()
    {
        if (_silent || _console || _thread is not null) return;
        _thread = new Thread(() =>
        {
            try
            {
                _window = new UpdateWindow(() => _cancel.Cancel());
                _window.Closed += (_, _) =>
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                _window.Show();
                _ready.Set();
                Dispatcher.Run();
            }
            catch (Exception ex) { _startupError = ex; }
            finally { _ready.Set(); _closed.Set(); }
        }) { IsBackground = true, Name = "GMC updater UI" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startupError is not null)
            throw new InvalidOperationException("Не удалось открыть окно обновления.", _startupError);
    }

    private void Send(Action<UpdateWindow> action)
    {
        if (_window is { } window && !window.Dispatcher.HasShutdownStarted)
        {
            try { window.Dispatcher.Invoke(() => action(window)); }
            catch (OperationCanceledException) { }
        }
    }

    public void Versions(string current, string available) => Send(w => w.SetVersions(current, available));
    public void Step(string text)
    {
        if (_console) Console.WriteLine(text);
        Send(w => w.SetStep(text));
    }

    public void Info(string text)
    {
        if (_console) Console.WriteLine(text);
        Send(w => w.Append(text));
    }

    public void Warn(string text) => Info("Внимание: " + text);

    public void Progress(long done, long total) => Send(w => w.SetProgress(done, total));

    public void BeginCommit(string text)
    {
        // Synchronously disable cancellation on the dispatcher before writing
        // installed.txt or modifying the running launcher.
        Send(w => w.LockCancellation());
        Token.ThrowIfCancellationRequested();
        Step(text);
    }

    public void Done(string text)
    {
        if (_console) Console.WriteLine(text);
        Send(w => w.Finish(text, false));
    }

    public void Error(string text)
    {
        if (_console) Console.Error.WriteLine(text);
        Send(w => w.Finish("Не удалось завершить обновление", true, text));
        if (!_silent && !_console && (_window is null || _startupError is not null))
            MessageBox.Show(text, "ГМЦ-Смета", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public void FinishCancelled() => Send(w => w.Finish("Обновление отменено", false));
    public void WaitForClose() { if (_thread is not null) _closed.Wait(); }

    public void Dispose()
    {
        Send(w => w.CloseFromOwner());
        if (_thread is not null) _closed.Wait();
        _cancel.Dispose();
        _ready.Dispose();
        _closed.Dispose();
    }
}
