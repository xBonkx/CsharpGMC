
[CmdletBinding()]
param([switch]$InstallCertificate)

$ErrorActionPreference = 'Stop'
$certificateFile = Join-Path $PSScriptRoot 'DIMA-SQL-Office.cer'
$thumbprint = '8DD5A06765203794955F314CEE31C42B070A5BB4'
$publicHash = '2F85A2F52A19A3D0409ECBEA269757992157A442AB76143F2DF1CA8D54295557'

if ($InstallCertificate) {
    if ((Get-FileHash -LiteralPath $certificateFile -Algorithm SHA256).Hash -ne $publicHash) {
        throw 'Контрольная сумма сертификата DIMA не совпала.'
    }
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificateFile)
    if ($certificate.Thumbprint -ne $thumbprint -or $certificate.HasPrivateKey) {
        throw 'Файл не соответствует открытому сертификату SQL Server DIMA.'
    }
    $now = Get-Date
    if ($now -lt $certificate.NotBefore -or $now -gt $certificate.NotAfter) {
        throw 'Проверьте дату на компьютере и срок действия сертификата DIMA.'
    }

    if (-not ((Test-Path -LiteralPath "Cert:\LocalMachine\Root\$thumbprint") -or
              (Test-Path -LiteralPath "Cert:\CurrentUser\Root\$thumbprint"))) {
        Import-Certificate -FilePath $certificateFile -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
    }
}

$windowsAccount = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
Write-Host "Windows: $windowsAccount"
$connection = [System.Data.SqlClient.SqlConnection]::new(
    'Server=tcp:192.168.1.46,1433;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=False;Connect Timeout=20;Application Name=GMC-Smeta Office Connection Check;')
try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = @'
SELECT CONVERT(nvarchar(128), @@SERVERNAME), ORIGINAL_LOGIN(),
       CONVERT(nvarchar(32), CONNECTIONPROPERTY('net_transport')),
       CONVERT(nvarchar(32), CONNECTIONPROPERTY('auth_scheme'));
'@
    $reader = $command.ExecuteReader()
    try {
        if (-not $reader.Read()) { throw 'SQL Server не вернул сведения о сеансе.' }
        if ($reader.GetString(0) -ine 'DIMA\SQLEXPRESS' -or $reader.GetString(2) -ine 'TCP') {
            throw 'Ответил другой экземпляр или использован другой транспорт.'
        }
        Write-Host "SQL: $($reader.GetString(0))"
        Write-Host "Вход SQL: $($reader.GetString(1)) ($($reader.GetString(3)))"
        Write-Host 'TCP и TLS с проверкой сертификата: успешно. Проверена только база master.'
    }
    finally {
        $reader.Dispose()
        $command.Dispose()
    }
}
finally {
    $connection.Dispose()
}
