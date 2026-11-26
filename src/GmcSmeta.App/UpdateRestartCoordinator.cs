using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using GmcSmeta.Core;

namespace GmcSmeta.App;

/// <summary>Normal launcher sessions may restart when a newer, fully installed
/// version is published. Never terminate the process or bypass Closing guards.</summary>
internal sealed class UpdateRestartCoordinator
{
    private readonly MainWindow _window;
    private readonly string _currentExe, _appsRoot;
    private readonly DispatcherTimer _timer;
    private InstalledUpdateTarget? _pending;
    private int? _document;
    private DateTime _nextAttemptUtc;
    private bool _closed, _closing;

    private UpdateRestartCoordinator(MainWindow window, string currentExe, string appsRoot)
    {
        _window = window;
        _currentExe = currentExe;
        _appsRoot = appsRoot;
        _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, window.Dispatcher)
        { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += Tick;
        window.Closed += Closed;
        _timer.Start();
    }

    internal static void Attach(MainWindow window)
    {
        if (Environment.GetEnvironmentVariable("GMC_SMETA_AUTO_RESTART") != "1" ||
            Environment.GetEnvironmentVariable("GMC_SMETA_NO_UPDATE") == "1" ||
            Environment.ProcessPath is not { } exe) return;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) return;
        var appsRoot = Path.GetDirectoryName(Path.GetDirectoryName(exe));
        if (appsRoot is null || !new[] { "GMC-Smeta-Office", "GMC-Smeta-Next" }.Any(name =>
            string.Equals(appsRoot, Path.Combine(local, name, "apps"), StringComparison.OrdinalIgnoreCase)))
            return; // Local/dev/standalone builds have no versioned updater.
        _ = new UpdateRestartCoordinator(window, exe, appsRoot);
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_closed || _closing || DateTime.UtcNow < _nextAttemptUtc ||
            !_window.CanRestartForUpdate || ComponentDispatcher.IsThreadModal ||
            Mouse.Captured is not null ||
            Application.Current.Windows.Cast<Window>().Any(w => w != _window && w.IsLoaded))
            return;

        var target = InstalledUpdateTarget.TryRead(_currentExe, _appsRoot);
        if (target is null) return;

        _closing = true;
        _pending = target;
        _document = _window.RestartDocumentId;
        try
        {
            // Close() raises every normal Closing handler; it can be cancelled.
            // In particular, do not call Application.Shutdown or Process.Kill.
            _window.Close();
        }
        finally
        {
            _closing = false;
            if (!_closed)
            {
                _pending = null;
                _nextAttemptUtc = DateTime.UtcNow.AddSeconds(30);
            }
        }
    }

    private void Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _timer.Stop();
        _timer.Tick -= Tick;
        _window.Closed -= Closed;
        if (_pending is null) return; // Ordinary exit must not relaunch.
        // Re-read the atomic pointer in case another worker published a newer
        // package. The old version is retained by the existing installer.
        var target = InstalledUpdateTarget.TryRead(_currentExe, _appsRoot);
        try
        {
            if (target is null) throw new InvalidOperationException("Установленная версия недоступна.");
            Start(target.Exe, autoRestart: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            Win32Exception or InvalidOperationException or ArgumentException)
        {
            // Starting the new process failed synchronously. Recover the old
            // session once, without an automatic restart loop.
            try { Start(_currentExe, autoRestart: false); }
            catch (Exception fallback) when (fallback is IOException or UnauthorizedAccessException or
                Win32Exception or InvalidOperationException or ArgumentException)
            {
                MessageBox.Show("Не удалось перезапустить программу. Откройте её с ярлыка.\n" + ex.Message,
                    "ГМЦ-Смета — обновление", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void Start(string exe, bool autoRestart)
    {
        using var previous = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false
        };
        start.Environment["GMC_SMETA_AUTO_RESTART"] = autoRestart ? "1" : "0";
        // Keep the existing connection environment; never put credentials in arguments.
        start.ArgumentList.Add("--restart-after");
        start.ArgumentList.Add(previous.Id.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(previous.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        if (_document is int id && id > 0)
        {
            start.ArgumentList.Add("--open");
            start.ArgumentList.Add(id.ToString(CultureInfo.InvariantCulture));
        }
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить EXE.");
    }

    internal static bool WaitForPreviousProcess(string[] args)
    {
        var index = Array.IndexOf(args, "--restart-after");
        if (index < 0) return true;
        if (index + 2 >= args.Length || !int.TryParse(args[index + 1], out var pid) ||
            !long.TryParse(args[index + 2], out var ticks) || pid <= 0 ||
            pid == Environment.ProcessId) return false;
        try
        {
            using var previous = Process.GetProcessById(pid);
            // PID may have been reused; only wait on the exact previous instance.
            if (previous.StartTime.ToUniversalTime().Ticks != ticks) return true;
            return previous.WaitForExit(15000);
        }
        catch (ArgumentException) { return true; } // Already exited.
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { return false; }
    }
}
