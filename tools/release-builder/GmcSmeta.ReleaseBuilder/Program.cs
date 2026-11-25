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
    private readonly TextBlock _status = Label("Готов к выпуску релиза", 18, Ink, true);
    private readonly TextBlock _version = Label("Версия —", 12, Muted);
    private readonly TextBox _repo = new() { MinHeight = 32, Padding = new Thickness(8), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _log = new() { IsReadOnly = true, FontFamily = new FontFamily("Consolas"),
        FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(8) };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 4, Height = 8, Foreground = Blue };
    private readonly Button _build = MakeButton("Собрать и выпустить релиз", true);
    private readonly Button _browse = MakeButton("Выбрать…");
    private readonly Button _open = MakeButton("Открыть GitHub Releases");
    private readonly Button _rehearse = MakeButton("Проверить миграцию на копии");
    private bool _busy;
    private string? _output;
    private string? _rehearsalLog;

    public BuildWindow()
    {
        Title = "ГМЦ-Смета — выпуск релиза";
        Width = 700; Height = 600; MinWidth = 520; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = BrushOf("#F3F6FA"); FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;
        var root = new DockPanel { Margin = new Thickness(24) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(Label("ГМЦ-Смета", 24, Ink, true));
        header.Children.Add(Label("Сборка и публикация • GitHub Releases", 13, Muted));
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var settings = new StackPanel();
        settings.Children.Add(Label("Репозиторий CsharpGMC", 12, Muted));
        var pathRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(_browse, Dock.Right); _browse.Margin = new Thickness(8, 0, 0, 0);
        pathRow.Children.Add(_browse); pathRow.Children.Add(_repo);
        settings.Children.Add(pathRow);
        _version.Margin = new Thickness(0, 12, 0, 0); settings.Children.Add(_version);
        _rehearse.Margin = new Thickness(0, 12, 0, 0);
        _rehearse.HorizontalAlignment = HorizontalAlignment.Left;
        settings.Children.Add(_rehearse);
        var settingsCard = Card(settings);
        DockPanel.SetDock(settingsCard, Dock.Top); root.Children.Add(settingsCard);

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(_build, Dock.Right); footer.Children.Add(_build);
        DockPanel.SetDock(_open, Dock.Left); footer.Children.Add(_open);
        _open.IsEnabled = false;
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var note = Label("Чистый main → сборка → тесты → GitHub Releases. Рабочая БД не затрагивается.", 12, Muted);
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
        _rehearse.Click += async (_, _) =>
        {
            var selected = SelectRehearsal();
            if (selected is not null) await BuildAsync(selected);
        };
        _open.Click += (_, _) =>
        {
            if (_output is not null)
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
            _version.Text = File.Exists(path) ? "Базовая версия: " + File.ReadAllText(path).Trim() : "Версия —";
        }
        catch { _version.Text = "Версия —"; }
    }

    private sealed record RehearsalSelection(string Catalog,string Connection);
    private RehearsalSelection? SelectRehearsal()
    {
        var window=new Window{Owner=this,Title="Репетиция SQL 28 → 31",Width=510,
            SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner,
            ResizeMode=ResizeMode.NoResize};
        var panel=new StackPanel{Margin=new Thickness(20)};
        panel.Children.Add(Label("Только отдельная тестовая база. Будут созданы .bak и новая восстановленная копия. Исходная база не мигрируется, релиз не публикуется.",13,Ink));
        var server=new TextBox{MinHeight=30,Margin=new Thickness(0,4,0,10)};
        var catalog=new TextBox{MinHeight=30,Margin=new Thickness(0,4,0,10)};
        panel.Children.Add(Label("SQL Server (Windows-аутентификация)",12,Muted));panel.Children.Add(server);
        panel.Children.Add(Label("Точное имя базы GmcSmetaPilot_Test_…",12,Muted));panel.Children.Add(catalog);
        var trust=new CheckBox{Content="Доверять сертификату тестового SQL Server",Margin=new Thickness(0,0,0,10)};
        panel.Children.Add(trust);
        var message=Label("",12,BrushOf("#B42318"));panel.Children.Add(message);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=MakeButton("Отмена");cancel.IsCancel=true;
        var run=MakeButton("Создать и проверить копию",true);run.IsDefault=true;run.Margin=new Thickness(8,0,0,0);
        buttons.Children.Add(cancel);buttons.Children.Add(run);panel.Children.Add(buttons);window.Content=panel;
        RehearsalSelection? result=null;
        run.Click+=(_,_)=>{
            var name=catalog.Text.Trim();var host=server.Text.Trim();
            if(host.Length==0||!System.Text.RegularExpressions.Regex.IsMatch(name,
                @"\AGmcSmetaPilot_(?:Test|ReleaseCheck)_[A-Za-z0-9_]{1,80}\z"))
            {message.Text="Укажите сервер и точное имя отдельной тестовой базы.";return;}
            // Use a builder rather than concatenating user-supplied connection string fragments.
            var cs=new System.Data.Common.DbConnectionStringBuilder();
            cs["Data Source"]=host;cs["Initial Catalog"]=name;cs["Integrated Security"]=true;
            cs["Encrypt"]=true;cs["TrustServerCertificate"]=trust.IsChecked==true;cs["Connect Timeout"]=15;
            result=new(name,cs.ConnectionString);window.DialogResult=true;
        };
        return window.ShowDialog()==true?result:null;
    }

    private async Task BuildAsync(RehearsalSelection? rehearsal=null)
    {
        if (_busy) return;
        using var mutex = new Mutex(false, @"Local\GmcSmetaReleaseBuilder");
        bool acquired;
        try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { _status.Text = "Выпуск уже выполняется в другом окне"; return; }
        _busy = true; _build.IsEnabled = _browse.IsEnabled = _repo.IsEnabled = _open.IsEnabled = _rehearse.IsEnabled = false;
        _output = null; _log.Clear(); _bar.Value = 0; _bar.Maximum = 7; _bar.Foreground = Blue; _status.Foreground = Ink;
        try
        {
            var repo = Path.GetFullPath(_repo.Text.Trim());
            if (!File.Exists(Path.Combine(repo, "GmcSmeta.slnx")))
                throw new InvalidOperationException("Выберите корневую папку CsharpGMC с GmcSmeta.slnx.");
            Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
            File.WriteAllText(Config, repo);
            _status.Text = "Проверяю репозиторий и обновляю main…";
            var remote = await Git(repo, "remote", "get-url", "origin");
            if (!new[] { "https://github.com/xBonkx/CsharpGMC", "https://github.com/xBonkx/CsharpGMC.git",
                "git@github.com:xBonkx/CsharpGMC.git", "git@github.com:xBonkx/CsharpGMC",
                "ssh://git@github.com/xBonkx/CsharpGMC.git", "ssh://git@github.com/xBonkx/CsharpGMC" }.Contains(remote, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("origin не указывает на xBonkx/CsharpGMC.");
            if (await Git(repo, "branch", "--show-current") != "main")
                throw new InvalidOperationException("Переключитесь на main; ветка не изменяется автоматически.");
            if (!string.IsNullOrWhiteSpace(await Git(repo, "status", "--porcelain")))
                throw new InvalidOperationException("Есть незакоммиченные изменения. Сохраните их перед выпуском.");
            await Git(repo, "fetch", "origin", "main", "--tags");
            await Git(repo, "merge", "--ff-only", "origin/main");
            var head = await Git(repo, "rev-parse", "HEAD");
            if (head != await Git(repo, "rev-parse", "origin/main"))
                throw new InvalidOperationException("Локальный main отличается от origin/main. Выпуск отменён.");
            if(rehearsal is not null)
            {
                await RehearseAsync(repo,head,rehearsal);
                return;
            }
            var versions = (await Git(repo, "tag", "--list", "v*")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim().TrimStart('v')).Where(v => Version.TryParse(v, out var x) && x.Build >= 0 && x.Revision < 0)
                .Select(Version.Parse).ToList();
            var raw = File.ReadAllText(Path.Combine(repo, "RELEASE_VERSION")).Trim().TrimStart('v');
            if (Version.TryParse(raw, out var baseline) && baseline.Build >= 0 && baseline.Revision < 0)
                versions.Add(baseline);
            var last = versions.OrderByDescending(v => v).FirstOrDefault() ?? new Version(0, 0, 0);
            var version = $"{last.Major}.{last.Minor}.{checked(last.Build + 1)}";
            _version.Text = $"Выпуск v{version} · {head[..8]}";
            Append("Новая версия: " + version);
            _bar.Value = 1;
            var script = Path.Combine(repo, "tools", "publish-release.ps1");
            if (!File.Exists(script)) throw new InvalidOperationException("Скрипт публикации не найден.");
            _status.Text = "Сборка и публикация v" + version + "…";
            var info = new ProcessStartInfo("powershell.exe") { WorkingDirectory = repo, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                "-Version", version, "-NewReleaseOnly" }) info.ArgumentList.Add(arg);
            info.Environment["GIT_TERMINAL_PROMPT"] = "0";
            info.Environment["GCM_INTERACTIVE"] = "never";
            info.Environment.Remove("GMC_SMETA_SQL_CONNECTION");
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить публикацию.");
            var stdout = PumpAsync(process.StandardOutput);
            var stderr = PumpAsync(process.StandardError);
            await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Выпуск не завершён (код {process.ExitCode}). Подробности в журнале.");
            _bar.Value = 7; _output = "https://github.com/xBonkx/CsharpGMC/releases/tag/v" + version;
            _status.Text = "Релиз v" + version + " опубликован"; _bar.Foreground = BrushOf("#20835C");
            Append(_output);
        }
        catch (Exception ex)
        {
            _status.Text = rehearsal is null?"Выпуск не завершён":"Репетиция не подтверждена"; _status.Foreground = BrushOf("#B42318");
            _bar.Foreground = _status.Foreground; Append(ex.Message);
        }
        finally
        {
            _bar.IsIndeterminate = false;
            _busy = false; _build.IsEnabled = _browse.IsEnabled = _repo.IsEnabled = _rehearse.IsEnabled = true;
            _rehearsalLog=null;
            _open.IsEnabled = _output is not null;
            mutex.ReleaseMutex();
        }
    }

    private async Task RehearseAsync(string repo,string head,RehearsalSelection selection)
    {
        var folder=Path.Combine(Path.GetDirectoryName(Config)!,"rehearsals");
        Directory.CreateDirectory(folder);
        _rehearsalLog=Path.Combine(folder,DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N")+".log");
        Append("Репетиция, HEAD "+head+", исходная тестовая база "+selection.Catalog+", цель 31");
        Append("Локальный журнал: "+_rehearsalLog);
        _status.Text="Резервная копия и проверка миграции…";_bar.IsIndeterminate=true;
        var info=new ProcessStartInfo("dotnet"){WorkingDirectory=repo,UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8};
        foreach(var arg in new[]{"run","--project","tools/sql-migrations/GmcSmeta.SchemaRehearsal",
            "--configuration","Release","--","--test-only",selection.Catalog,"--target","31"})
            info.ArgumentList.Add(arg);
        info.Environment.Remove("GMC_SMETA_SQL_CONNECTION");
        info.Environment["GMC_SMETA_MIGRATION_CONNECTION"]=selection.Connection;
        using var process=Process.Start(info)??throw new InvalidOperationException("Не удалось запустить репетицию.");
        await Task.WhenAll(process.WaitForExitAsync(),PumpAsync(process.StandardOutput),PumpAsync(process.StandardError));
        Append("Код завершения: "+process.ExitCode);
        if(process.ExitCode!=0)throw new InvalidOperationException("Проверка не пройдена. Сохраните журнал и проверьте созданные артефакты; не повторяйте рабочую миграцию.");
        _bar.IsIndeterminate=false;_bar.Value=7;
        _status.Text="Репетиция завершена — см. результат в журнале";
        Append("Публикация не выполнялась. Этот результат не разрешает штатную запись v31.");
    }

    private async Task<string> Git(string repo, params string[] args)
    {
        Append("> git " + string.Join(" ", args));
        var info = new ProcessStartInfo("git") { WorkingDirectory = repo, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8 };
        info.Environment["GIT_TERMINAL_PROMPT"] = "0"; info.Environment["GCM_INTERACTIVE"] = "never";
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Git не найден.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr);
        var errors = await stderr;
        if (!string.IsNullOrWhiteSpace(errors)) Append(errors.Trim());
        if (process.ExitCode != 0) throw new InvalidOperationException("Git: код ошибки " + process.ExitCode);
        return (await stdout).Trim();
    }

    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            Append(line);
            if (line.Length > 4 && line[0] >= '1' && line[0] <= '6' && line[1] == '/' && line[2] == '6')
            {
                _bar.Value = Math.Max(_bar.Value, line[0] - '0');
                _status.Text = line.Trim();
            }
        }
    }

    private void Append(string line)
    {
        if(_rehearsalLog is not null)
            File.AppendAllText(_rehearsalLog,line+Environment.NewLine);
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
