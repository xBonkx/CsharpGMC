using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace GmcSmeta.Launcher;

internal sealed class ConsoleUi : IDisposable
{
    private readonly bool _silent;
    private readonly bool _commandLine;
    private UpdateCard? _card;
    private string _status = "";
    private string _details = "";

    public ConsoleUi(bool silent = false) => _silent = silent;

    public ConsoleUi(LaunchOptions options)
        : this(options.BackgroundUpdate)
    {
        _commandLine = !options.BackgroundUpdate &&
            (options.Help || options.Check || options.Dev || options.UpdateOnly);
        if (_commandLine)
        {
            AttachConsole(uint.MaxValue);
            Console.OutputEncoding = Encoding.UTF8;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);

    public void ShowUpdate(string status, string details)
    {
        if (_commandLine) return;
        _status = status;
        _details = details;
        try
        {
            if (_card is null)
                _card = new UpdateCard(status, details);
            else
                _card.Report(status, details);
        }
        catch (Exception ex)
        {
            UpdateCard.TraceUiFailure(ex);
        }
    }

    public void UpdatePhase(string status, string details)
    {
        _status = status;
        _details = details;
        _card?.Report(status, details);
    }

    public void DownloadProgress(string details, double? percent)
    {
        _card?.Report(_status, details, percent);
        if (_commandLine) Console.Write("\r  " + details + "   ");
    }

    public void FinishUpdate(string status, string? details = null)
    {
        if (_card is null) return;
        _card.Report(status, details ?? _details, 100);
        Thread.Sleep(2300);
    }

    public void Failure(string message)
    {
        Error("Не удалось: " + message);
        if (_commandLine) return;
        if (_silent)
        {
            if (_card is not null)
            {
                _card.Report("Обновление отложено", "Проверка повторится при следующем запуске.", showProgress: false);
                Thread.Sleep(3000);
            }
            return;
        }
        Dispose();
        MessageBox.Show(message, "ГМЦ-Смета — ошибка обновления", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void Dispose()
    {
        _card?.Dispose();
        _card = null;
    }

    public void Step(string text)
    {
        if (_silent) return;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    public void Info(string text)
    {
        if (_silent) return;
        Console.WriteLine("  " + text);
    }

    public void Warn(string text)
    {
        if (_silent) return;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  " + text);
        Console.ResetColor();
    }

    public void Error(string text)
    {
        if (_silent) return;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    public void Done(string text)
    {
        if (_silent) return;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
