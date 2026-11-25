using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GmcSmeta.Launcher;

internal static class OfficeLauncher
{
    public const string FileName = "GMC-Smeta-CSharp-Start.exe";
    private const string OfficeDatabase = "GmcSmetaPilot_Cutover_20261125";

    public static bool IsOfficeExecutable =>
        string.Equals(Path.GetFileName(Environment.ProcessPath), FileName, StringComparison.OrdinalIgnoreCase);

    public static string ConnectionString =>
        string.Equals(Environment.MachineName, "DIMA", StringComparison.OrdinalIgnoreCase)
            ? $@"Server=.\SQLEXPRESS;Database={OfficeDatabase};Integrated Security=true;Encrypt=false;"
            : $"Server=tcp:192.168.1.46,1433;Database={OfficeDatabase};Integrated Security=true;Encrypt=true;TrustServerCertificate=false;";

    public static bool SupportsOfficeRelease(string tag) =>
        Version.TryParse(tag.TrimStart('v'), out var version) && version >= new Version(0, 1, 6);

    public static bool InstallLauncher(string root, string[] arguments)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден запускающий EXE.");
        var local = Path.Combine(root, FileName);
        if (string.Equals(executable, local, StringComparison.OrdinalIgnoreCase))
            return false;

        // A fresh download must not replace a launcher that has already updated itself.
        if (!File.Exists(local))
        {
            var temporary = Path.Combine(root, Guid.NewGuid() + ".tmp");
            try
            {
                File.Copy(executable, temporary);
                try { File.Move(temporary, local); }
                catch (IOException) when (File.Exists(local)) { }
            }
            finally { File.Delete(temporary); }
        }

        CreateDesktopShortcut(local);

        var start = new ProcessStartInfo(local) { UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Локальный запуск не удался.");
        return true;
    }

    private static void CreateDesktopShortcut(string launcher)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!Directory.Exists(desktop)) return;
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(Path.Combine(desktop, "ГМЦ-Смета C# (рабочая).lnk"));
                try
                {
                    shortcut.TargetPath = launcher;
                    shortcut.WorkingDirectory = Path.GetDirectoryName(launcher);
                    shortcut.IconLocation = launcher + ",0";
                    shortcut.Save();
                }
                finally { Marshal.FinalReleaseComObject(shortcut); }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            Console.WriteLine("Ярлык не создан: " + ex.Message);
        }
    }
}
