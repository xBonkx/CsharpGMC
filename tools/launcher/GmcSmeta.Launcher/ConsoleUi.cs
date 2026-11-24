namespace GmcSmeta.Launcher;

/// <summary>Минимальный вывод в консоль: шаги, предупреждения, итог.</summary>
internal sealed class ConsoleUi
{
    private readonly bool _silent;
    public ConsoleUi(bool silent = false) => _silent = silent;

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
