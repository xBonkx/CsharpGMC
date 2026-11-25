using System;
using System.Data.SqlClient;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

internal static class OfficeSqlCheck
{
    private const string Title = "ГМЦ-Смета — проверка SQL";
    private const string CertificateName = "DIMA-SQL-Office.cer";
    private const string CertificateSha256 = "2F85A2F52A19A3D0409ECBEA269757992157A442AB76143F2DF1CA8D54295557";

    [STAThread]
    private static int Main(string[] args)
    {
        var reportOnly = args.Length == 1 && args[0] == "--report";
        string account;
        using (var identity = WindowsIdentity.GetCurrent())
            account = identity.Name;
        try
        {
            if (!EnsureCertificate(reportOnly))
                return 2;

            ShowResult("Windows: " + account + "\r\n" + CheckSql(), reportOnly, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception error)
        {
            ShowResult("Windows: " + account + "\r\nПроверка не прошла.\r\n\r\n" + error.Message,
                reportOnly, MessageBoxIcon.Warning);
            return 1;
        }
    }

    private static bool EnsureCertificate(bool reportOnly)
    {
        var data = File.ReadAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CertificateName));
        using (var sha256 = SHA256.Create())
        {
            var hash = BitConverter.ToString(sha256.ComputeHash(data)).Replace("-", "");
            if (!string.Equals(hash, CertificateSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Контрольная сумма сертификата DIMA не совпала.");
        }

        using (var certificate = new X509Certificate2(data))
        {
            var now = DateTime.Now;
            if (certificate.HasPrivateKey || now < certificate.NotBefore || now > certificate.NotAfter)
                throw new InvalidOperationException("Проверьте дату на компьютере и срок действия сертификата DIMA.");

            using (var machine = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
            using (var current = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
            {
                machine.Open(OpenFlags.ReadOnly);
                current.Open(OpenFlags.ReadOnly);
                if (HasCertificate(machine, certificate) || HasCertificate(current, certificate))
                    return true;
                if (reportOnly)
                    throw new InvalidOperationException("Сертификат DIMA ещё не установлен.");
                if (MessageBox.Show("Добавить проверенный сертификат SQL Server DIMA для текущего пользователя?",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return false;

                current.Close();
                current.Open(OpenFlags.ReadWrite);
                current.Add(certificate);
                if (!HasCertificate(current, certificate))
                    throw new InvalidOperationException("Windows не добавила сертификат DIMA.");
            }
        }
        return true;
    }

    private static bool HasCertificate(X509Store store, X509Certificate2 certificate)
    {
        return store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count > 0;
    }

    private static string CheckSql()
    {
        using (var connection = new SqlConnection("Server=tcp:192.168.1.46,1433;Database=master;" +
            "Integrated Security=True;Encrypt=True;TrustServerCertificate=False;Connect Timeout=20;" +
            "Application Name=GMC-Smeta Office Connection Check;"))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT CONVERT(nvarchar(128), @@SERVERNAME), ORIGINAL_LOGIN(), " +
                "CONVERT(nvarchar(32), CONNECTIONPROPERTY('net_transport')), " +
                "CONVERT(nvarchar(32), CONNECTIONPROPERTY('auth_scheme'));";
            connection.Open();
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read() || !string.Equals(reader.GetString(0), @"DIMA\SQLEXPRESS", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(reader.GetString(2), "TCP", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Ответил другой экземпляр SQL Server или использован другой транспорт.");

                return "Вход SQL: " + reader.GetString(1) + " (" + reader.GetString(3) + ")" +
                    "\r\nСервер: " + reader.GetString(0) + "\r\nTLS проверен. Проверена только база master.";
            }
        }
    }

    private static void ShowResult(string text, bool reportOnly, MessageBoxIcon icon)
    {
        if (reportOnly)
        {
            using (var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
                output.WriteLine(text);
        }
        else
        {
            MessageBox.Show(text, Title, MessageBoxButtons.OK, icon);
        }
    }
}
