# Read actual PE icon resources without executing the application.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string[]]$ExePaths)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '..\src\GmcSmeta.App\Assets\app.ico'
$ico = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($source))
if (-not ('GmcBrandIconVerifier' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public static class GmcBrandIconVerifier
{
    private delegate bool ResourceCallback(IntPtr module, IntPtr type, IntPtr name, IntPtr param);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, ResourceCallback cb, IntPtr param);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr resource);

    private static byte[] Read(IntPtr module, IntPtr name, int type)
    {
        IntPtr info = FindResource(module, name, new IntPtr(type));
        if (info == IntPtr.Zero) return null;
        int size = checked((int)SizeofResource(module, info));
        IntPtr data = LockResource(LoadResource(module, info));
        if (data == IntPtr.Zero || size == 0) return null;
        byte[] bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, size);
        return bytes;
    }
    private static bool Matches(IntPtr module, byte[] group, byte[] ico)
    {
        if (group == null || group.Length < 6 || ico.Length < 6) return false;
        int count = BitConverter.ToUInt16(ico, 4);
        if (count == 0 || BitConverter.ToUInt16(group, 4) != count ||
            group.Length < 6 + 14 * count || ico.Length < 6 + 16 * count) return false;
        for (int i = 0; i < count; i++)
        {
            int a = 6 + 16 * i;
            int length = checked((int)BitConverter.ToUInt32(ico, a + 8));
            int offset = checked((int)BitConverter.ToUInt32(ico, a + 12));
            if (offset < 0 || length <= 0 || offset > ico.Length - length) return false;
            bool found = false;
            for (int j = 0; j < count && !found; j++)
            {
                int b = 6 + 14 * j;
                if (ico[a] != group[b] || ico[a+1] != group[b+1]) continue;
                byte[] image = Read(module, new IntPtr(BitConverter.ToUInt16(group, b + 12)), 3);
                if (image == null || image.Length != length) continue;
                found = true;
                for (int k = 0; k < length; k++)
                    if (image[k] != ico[offset+k]) { found = false; break; }
            }
            if (!found) return false;
        }
        return true;
    }
    public static bool Verify(string path, byte[] ico)
    {
        // LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE
        IntPtr module = LoadLibraryEx(path, IntPtr.Zero, 0x22);
        if (module == IntPtr.Zero) throw new IOException("Cannot read PE resources: " + path);
        try
        {
            bool matched = false;
            ResourceCallback callback = delegate(IntPtr h, IntPtr type, IntPtr name, IntPtr param)
            {
                if (Matches(h, Read(h, name, 14), ico)) matched = true;
                return !matched;
            };
            EnumResourceNames(module, new IntPtr(14), callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return matched;
        }
        finally { FreeLibrary(module); }
    }
}
'@
}
foreach ($exe in $ExePaths) {
    $path = (Resolve-Path -LiteralPath $exe).Path
    if (-not [GmcBrandIconVerifier]::Verify($path, $ico)) {
        throw "EXE does not contain the original GMC icon frames: $path"
    }
    Write-Host "Original GMC icon verified: $path" -ForegroundColor Green
}
