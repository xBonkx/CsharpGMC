using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GmcSmeta.ReleaseBuilder;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        new Application().Run(new BuildWindow());
    }
}

internal sealed class BuildWindow : Window
{
    private static readonly Brush Ink = BrushOf("#17263B");
    private static readonly Brush Muted = BrushOf("#64748B");
    private static readonly Brush Blue = BrushOf("#2764D8");
    private static readonly string Config = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GMC-Smeta-ReleaseBuilder", "repo.txt");
    private readonly TextBlock _status = Label("Готов к сборке", 18, Ink, true);
    private readonly TextBlock _version = Label("Версия —", 12, Muted);
    private readonly TextBox _repo = new() { MinHeight = 32, Padding = new Thickness(8), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _log = new() { IsReadOnly = true, FontFamily = new FontFamily("Consolas"),
        FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(8) };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 4, Height = 8, Foreground = Blue };
    private readonly Button _build = MakeButton("Собрать релиз", true);
    private readonly Button _browse = MakeButton("Выбрать…");
    private readonly Button _open = MakeButton("Открыть папку сборки");
    private readonly CheckBox _tests = new() { Content = "Запустить Core-тесты после сборки", Margin = new Thickness(0, 12, 0, 0) };
    private bool _busy;
    private string? _output;

    public BuildWindow()
    {
        Title = "ГМЦ-Смета — сборка релиза";
        Width = 700; Height = 600; MinWidth = 520; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = BrushOf("#F3F6FA"); FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;
        var root = new DockPanel { Margin = new Thickness(24) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(Label("ГМЦ-Смета", 24, Ink, true));
        header.Children.Add(Label("Локальная сборка Release • .NET 10 / WPF", 13, Muted));
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var settings = new StackPanel();
        settings.Children.Add(Label("Репозиторий CsharpGMC", 12, Muted));
        var pathRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(_browse, Dock.Right); _browse.Margin = new Thickness(8, 0, 0, 0);
        pathRow.Children.Add(_browse); pathRow.Children.Add(_repo);
        settings.Children.Add(pathRow); settings.Children.Add(_tests);
        _version.Margin = new Thickness(0, 12, 0, 0); settings.Children.Add(_version);
        var settingsCard = Card(settings);
        DockPanel.SetDock(settingsCard, Dock.Top); root.Children.Add(settingsCard);

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(_build, Dock.Right); footer.Children.Add(_build);
        DockPanel.SetDock(_open, Dock.Left); footer.Children.Add(_open);
        _open.IsEnabled = false;
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var note = Label("Только локальная сборка. Git, GitHub Releases и SQL Server не изменяются.", 12, Muted);
        note.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(note, Dock.Bottom); root.Children.Add(note);

        var progress = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        top.Children.Add(_status);
        _bar.Margin = new Thickness(0, 10, 0, 0); top.Children.Add(_bar);
        DockPanel.SetDock(top, Dock.Top); progress.Children.Add(top);
        progress.Children.Add(_log); root.Children.Add(Card(progress));
        Content = root;

        try { if (File.Exists(Config)) _repo.Text = File.ReadAllText(Config).Trim(); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        _repo.TextChanged += (_, _) => RefreshVersion();
        RefreshVersion();
        _browse.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Выберите папку CsharpGMC" };
            if (Directory.Exists(_repo.Text)) picker.InitialDirectory = _repo.Text;
            if (picker.ShowDialog(this) == true) _repo.Text = picker.FolderName;
        };
        _build.Click += async (_, _) => await BuildAsync();
        _open.Click += (_, _) =>
        {
            if (_output is not null && Directory.Exists(_output))
                try { Process.Start(new ProcessStartInfo(_output) { UseShellExecute = true }); }
                catch (Exception ex) { Append(ex.Message); }
        };
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    private void RefreshVersion()
    {
        try
        {
            var path = Path.Combine(_repo.Text.Trim(), "RELEASE_VERSION");
            _version.Text = File.Exists(path) ? "Версия в RELEASE_VERSION: " + File.ReadAllText(path).Trim() : "Версия —";
        }
        catch { _version.Text = "Версия —"; }
    }

    private async Task BuildAsync()
    {
        if (_busy) return;
        using var mutex = new Mutex(false, @"Local\GmcSmetaReleaseBuilder");
        bool acquired;
        try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { _status.Text = "Сборка уже выполняется в другом окне"; return; }
        _busy = true; _build.IsEnabled = _browse.IsEnabled = _repo.IsEnabled = _tests.IsEnabled = _open.IsEnabled = false;
        _output = null; _log.Clear(); _bar.Value = 0; _bar.Foreground = Blue; _status.Foreground = Ink;
        try
        {
            var repo = Path.GetFullPath(_repo.Text.Trim());
            if (!File.Exists(Path.Combine(repo, "GmcSmeta.slnx")) ||
                !File.Exists(Path.Combine(repo, "tools", "launcher", "GmcSmeta.Launcher", "GmcSmeta.Launcher.csproj")))
                throw new InvalidOperationException("Выберите корневую папку CsharpGMC с GmcSmeta.slnx.");
            Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
            File.WriteAllText(Config, repo);
            var tests = _tests.IsChecked == true;
            _bar.Maximum = tests ? 4 : 3;
            await Stage(repo, "Восстановление NuGet-пакетов", "restore", "GmcSmeta.slnx", "--nologo");
            await Stage(repo, "Сборка приложения Release", "build", "GmcSmeta.slnx", "-c", "Release", "--no-restore", "--nologo");
            await Stage(repo, "Сборка автообновлятора", "build", "tools/launcher/GmcSmeta.Launcher/GmcSmeta.Launcher.csproj", "-c", "Release", "--nologo");
            if (tests) await Stage(repo, "Core-тесты", "test", "tests/GmcSmeta.Core.Tests/GmcSmeta.Core.Tests.csproj", "-c", "Release", "--no-build", "--nologo");
            var output = Path.Combine(repo, "src", "GmcSmeta.App", "bin", "Release", "net10.0-windows");
            if (!File.Exists(Path.Combine(output, "GMC-Smeta-Next.exe")))
                throw new InvalidOperationException("Сборка завершилась, но EXE не найден.");
            _output = output; _status.Text = "Релиз успешно собран"; _bar.Foreground = BrushOf("#20835C");
            Append("Готово: " + output);
        }
        catch (Exception ex)
        {
            _status.Text = "Сборка не завершена"; _status.Foreground = BrushOf("#B42318");
            _bar.Foreground = _status.Foreground; Append(ex.Message);
        }
        finally
        {
            _bar.IsIndeterminate = false;
            _busy = false; _build.IsEnabled = _browse.IsEnabled = _repo.IsEnabled = _tests.IsEnabled = true;
            _open.IsEnabled = _output is not null;
            mutex.ReleaseMutex();
        }
    }

    private async Task Stage(string repo, string label, params string[] args)
    {
        _status.Text = $"{_bar.Value + 1:0}/{_bar.Maximum:0}  {label}…";
        Append(Environment.NewLine + "> dotnet " + string.Join(" ", args));
        var info = new ProcessStartInfo("dotnet") { WorkingDirectory = repo, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить dotnet.");
        // Drain both streams concurrently; await UI dispatch for bounded log updates.
        var stdout = PumpAsync(process.StandardOutput);
        var stderr = PumpAsync(process.StandardError);
        await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{label}: код ошибки {process.ExitCode}. Подробности в журнале выше.");
        _bar.Value++;
    }

    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line) Append(line);
    }

    private void Append(string line)
    {
        _log.AppendText(line + Environment.NewLine);
        if (_log.Text.Length > 160000) _log.Text = _log.Text[^120000..];
        _log.ScrollToEnd();
    }

    private static Border Card(UIElement child) => new() { Child = child, Background = Brushes.White,
        BorderBrush = BrushOf("#E0E6EE"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
        Padding = new Thickness(16) };
    private static Button MakeButton(string text, bool primary = false) => new() { Content = text, MinHeight = 34,
        Padding = new Thickness(14, 6, 14, 6), Background = primary ? Blue : Brushes.White,
        Foreground = primary ? Brushes.White : Ink, BorderBrush = primary ? Blue : BrushOf("#D4DCE7") };
    private static TextBlock Label(string text, double size, Brush brush, bool bold = false) => new() {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, Foreground = brush,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
    private static Brush BrushOf(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush;
    }
}
