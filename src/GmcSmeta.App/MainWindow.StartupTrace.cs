using System.Windows.Media;
using GmcSmeta.Core;

namespace GmcSmeta.App;

public partial class MainWindow
{
    // One WPF render callback after the workspace becomes interactive. This
    // is not a measurement of a physical monitor's scan-out or user input.
    private void ObserveReadyWorkspaceRender()
    {
        if (!StartupTrace.Enabled || _startupClosed) return;
        EventHandler? render = null;
        EventHandler? closed = null;
        void Detach()
        {
            CompositionTarget.Rendering -= render;
            Closed -= closed;
        }
        render = (_, _) =>
        {
            Detach();
            if (_ready && !_startupClosed && RootLayout.IsEnabled)
                StartupTrace.Mark("main_workspace_ready_rendering");
        };
        closed = (_, _) => Detach();
        CompositionTarget.Rendering += render;
        Closed += closed;
    }
}
