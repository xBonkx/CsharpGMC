using System.IO;

namespace GmcSmeta.Launcher;

internal static class LauncherBinaryPublisher
{
    internal static void Replace(string verifiedFile, string executable)
    {
        var backup = executable + ".old";
        // Same-volume replacement avoids copying bytes into the live EXE.
        // If Windows refuses replacement of an in-use executable, do not
        // fall back to a destructive overwrite.
        try
        {
            File.Replace(verifiedFile, executable, backup);
        }
        catch (IOException)
        {
            // Some filesystem errors can leave the destination moved to backup.
            if (!File.Exists(executable) && File.Exists(backup))
                File.Move(backup, executable);
            throw;
        }
    }
}
