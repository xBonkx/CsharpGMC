using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace GmcSmeta.App;

/// <summary>Presentation only: shared form metrics and a scrolling body with independent actions.</summary>
internal static class DynamicFormLayout
{
    internal static void Apply(Window window)
    {
        WorkspaceWindowUi.ApplyChrome(window);
        var requestedMaxHeight = window.MaxHeight;
        var requestedMaxWidth = window.MaxWidth;
        var lastAppliedHeight = requestedMaxHeight;
        var lastAppliedWidth = requestedMaxWidth;
        var fitting = false;
        void FitWorkArea()
        {
            if (fitting) return;
            // Callers may set an intentional local cap after Apply.
            if (window.MaxHeight != lastAppliedHeight) requestedMaxHeight = window.MaxHeight;
            if (window.MaxWidth != lastAppliedWidth) requestedMaxWidth = window.MaxWidth;
            var workArea = SystemParameters.WorkArea.Size;
            // SystemParameters.WorkArea refers to the primary display. Once a
            // HWND exists, constrain the form to the actual monitor instead.
            var handle = new WindowInteropHelper(window).Handle;
            var monitor = handle == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(handle, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var dpi = VisualTreeHelper.GetDpi(window);
                    var width = (info.Work.Right - info.Work.Left) / dpi.DpiScaleX;
                    var height = (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY;
                    if (double.IsFinite(width) && width > 0 && double.IsFinite(height) && height > 0)
                        workArea = new Size(width, height);
                }
            }
            var maxHeight = Math.Max(window.MinHeight, Math.Min(requestedMaxHeight, workArea.Height));
            var maxWidth = Math.Max(window.MinWidth, Math.Min(requestedMaxWidth, workArea.Width));
            fitting = true;
            try
            {
                if (window.MaxHeight != maxHeight) window.MaxHeight = maxHeight;
                if (window.MaxWidth != maxWidth) window.MaxWidth = maxWidth;
                lastAppliedHeight = maxHeight;
                lastAppliedWidth = maxWidth;
            }
            finally { fitting = false; }
        }
        FitWorkArea();
        window.SourceInitialized += (_, _) => FitWorkArea();
        window.Loaded += (_, _) => FitWorkArea();
        window.LocationChanged += (_, _) => FitWorkArea();
        window.DpiChanged += (_, _) => FitWorkArea();
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    internal static void WrapCaption(ContentControl control)
    {
        if (control.Content is not string caption) return;
        AutomationProperties.SetName(control, caption);
        control.Content = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, LineStackingStrategy = LineStackingStrategy.MaxHeight };
    }

    internal static TextBlock Secondary(string text, string? marginKey = null)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(FrameworkElement.StyleProperty, "UiSecondaryText");
        if (marginKey is not null) block.SetResourceReference(FrameworkElement.MarginProperty, marginKey);
        return block;
    }

    internal static void Field(Control control)
    {
        control.SetResourceReference(FrameworkElement.MinHeightProperty, "LayoutFieldMinHeight");
    }

    internal static DockPanel WithFooter(FrameworkElement body, FrameworkElement footer,
        string marginKey = "LayoutFormDialogMargin")
    {
        body.SetResourceReference(FrameworkElement.MarginProperty, "LayoutNoInset");
        var root = new DockPanel { LastChildFill = true };
        root.SetResourceReference(FrameworkElement.MarginProperty, marginKey);
        footer.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormFooterGap");
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        return root;
    }

    internal static void StyleActions(Button primary, Button cancel)
    {
        WorkspaceWindowUi.StyleButton(primary, primary: true);
        WorkspaceWindowUi.StyleButton(cancel);
    }
}
