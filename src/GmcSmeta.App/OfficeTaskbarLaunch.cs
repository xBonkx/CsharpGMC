using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GmcSmeta.Core;

namespace GmcSmeta.App;

/// <summary>Taskbar pins must target the stable office launcher, not a versioned
/// application binary that depends on the launcher's connection environment.</summary>
internal static class OfficeTaskbarLaunch
{
    private static string? Launcher() => OfficeLaunchPath.Resolve(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.ProcessPath);

    internal static bool TryRedirect()
    {
        var launcher = Launcher();
        if (launcher is null) return false;
        try
        {
            using var child = Process.Start(new ProcessStartInfo(launcher)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(launcher)!
            });
            return child is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning("Office launcher redirect failed: " + ex.Message);
            return false;
        }
    }

    internal static void Attach(Window window)
    {
        var launcher = Launcher();
        if (launcher is null) return;
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (HwndSource.FromHwnd(handle) is not { } source) return;
            // Windows consumes these properties when pinning or relaunching.
            // Set the explicit window AppID last, after its relaunch metadata.
            try
            {
                WithStore(handle, store =>
                {
                    Set(store, 2, "\"" + launcher + "\"");
                    Set(store, 3, launcher + ",0");
                    Set(store, 4, "ГМЦ-Смета C#");
                    Set(store, 5, App.ShellAppId);
                });
                source.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                {
                    if (msg == 0x0002) Clear(hwnd); // WM_DESTROY, not a cancellable Closing event
                    return IntPtr.Zero;
                });
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or
                DllNotFoundException or EntryPointNotFoundException)
            {
                Clear(handle);
                Trace.TraceWarning("Office taskbar metadata unavailable: " + ex.Message);
            }
        };
    }

    private static void Clear(IntPtr handle)
    {
        try
        {
            WithStore(handle, store =>
            {
                foreach (uint id in new uint[] { 5, 2, 3, 4 })
                {
                    var key = Key(id);
                    var empty = default(PropVariant);
                    _ = store.SetValue(ref key, ref empty);
                }
            });
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or
            DllNotFoundException or EntryPointNotFoundException)
        { Trace.TraceWarning("Office taskbar cleanup unavailable: " + ex.Message); }
    }

    private static void WithStore(IntPtr handle, Action<IPropertyStore> action)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(handle, ref iid, out var store));
        try { action(store); }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static PropertyKey Key(uint id) => new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = id
    };

    private static void Set(IPropertyStore store, uint id, string text)
    {
        var key = Key(id);
        var value = new PropVariant { Type = 31, Data = Marshal.StringToCoTaskMemUni(text) }; // VT_LPWSTR
        try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value)); }
        finally { _ = PropVariantClear(ref value); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey { public Guid FormatId; public uint Id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type, Reserved1, Reserved2, Reserved3;
        public IntPtr Data, Extra;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
