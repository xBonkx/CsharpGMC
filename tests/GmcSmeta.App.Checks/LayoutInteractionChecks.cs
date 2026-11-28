using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using GmcSmeta.App;
using GmcSmeta.Core.Domain;

/// <summary>Opt-in real WPF render callbacks on a shown window and production
/// estimate grid templates. Synthetic read-only rows; never injects global input
/// or connects to SQL. Not physical monitor scan-out/FPS.</summary>
internal static class LayoutInteractionChecks
{
    private const int Warmup = 12;
    private const int Measured = 120;
    private const int TimerMs = 16;

    private sealed record Sample(int Iteration, double RenderIntervalMs, double WallIntervalMs,
        double CommandToRenderMs, double DispatcherStallMs, int RealizedRows,
        double DpiX, double DpiY, double WindowPixelsX, double WindowPixelsY,
        double ViewportWidth, double ViewportHeight, double Offset,
        double InspectionMs, bool Valid);

    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        using var csv = new StreamWriter(Path.Combine(output, "layout-interaction.csv"));
        csv.WriteLine("requested_pixels_x,requested_pixels_y,rows,phase,iteration,render_interval_ms,wall_interval_ms,command_to_render_ms,dispatcher_stall_ms,realized_rows,dpi_x,dpi_y,window_pixels_x,window_pixels_y,viewport_width_dip,viewport_height_dip,offset,inspection_ms,status");
        var summaries = new List<object>();
        var failures = 0;
        foreach (var (width, height) in new[] { (1366, 768), (1920, 1080) })
        foreach (var count in new[] { 300, 2500, 10000, 25000 })
        {
            var page = BackgroundEstimateChecks.LoadEstimateGrid();
            var grid = (DataGrid)page.FindName("EstimateGrid");
            grid.IsReadOnly = true;
            grid.ItemsSource = Enumerable.Range(1, count).Select(id =>
                new EditableEstimateRow(new EstimateRow(RowKind.Position, 1, id, id,
                    false, id.ToString(), "Синтетическая позиция " + id + " — длинное наименование",
                    "шт", 2m, 123.45m, 246.90m, 50m, false, "Проверка прокрутки")
                { SystemId = 1 })).ToArray();
            var host = new Window
            {
                Content = page, Width = 900, Height = 600, ShowActivated = false,
                ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0
            };
            host.Show();
            try
            {
                var dpi = VisualTreeHelper.GetDpi(host);
                var baseWidth = width / dpi.DpiScaleX;
                var baseHeight = height / dpi.DpiScaleY;
                host.Width = baseWidth;
                host.Height = baseHeight;
                host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                host.UpdateLayout();
                var viewer = grid.Template?.FindName("DG_ScrollViewer", grid) as ScrollViewer ??
                    Descendants(grid).OfType<ScrollViewer>().FirstOrDefault();
                if (viewer is null || viewer.ScrollableHeight <= 0)
                    throw new InvalidOperationException("Estimate fixture has no scrollable viewport.");
                foreach (var phase in new[] { "scroll", "resize" })
                {
                    var (samples, error) = Measure(host, grid, viewer, phase, baseWidth, baseHeight);
                    foreach (var s in samples)
                    {
                        csv.WriteLine(FormattableString.Invariant(
                            $"{width},{height},{count},{phase},{s.Iteration},{s.RenderIntervalMs:F4},{s.WallIntervalMs:F4},{s.CommandToRenderMs:F4},{s.DispatcherStallMs:F4},{s.RealizedRows},{s.DpiX:F2},{s.DpiY:F2},{s.WindowPixelsX:F2},{s.WindowPixelsY:F2},{s.ViewportWidth:F2},{s.ViewportHeight:F2},{s.Offset:F2},{s.InspectionMs:F4},{(s.Valid ? "PASS" : "FAIL")}"));
                    }
                    csv.Flush();
                    var complete = error is null && samples.Count == Measured && samples.All(s => s.Valid);
                    if (!complete) failures++;
                    summaries.Add(new
                    {
                        requestedPixelsX = width, requestedPixelsY = height, rows = count, phase,
                        sampleCount = samples.Count, status = complete ? "PASS" : "FAIL", error,
                        actualDpiX = samples.LastOrDefault()?.DpiX,
                        actualDpiY = samples.LastOrDefault()?.DpiY,
                        maxRealizedRows = samples.Count == 0 ? 0 : samples.Max(s => s.RealizedRows),
                        renderInterval = Stats(samples.Select(s => s.RenderIntervalMs)),
                        wallInterval = Stats(samples.Select(s => s.WallIntervalMs)),
                        commandToRender = Stats(samples.Select(s => s.CommandToRenderMs)),
                        dispatcherStall = Stats(samples.Select(s => s.DispatcherStallMs))
                    });
                }
            }
            finally
            {
                grid.ItemsSource = null;
                host.Close();
            }
        }
        File.WriteAllText(Path.Combine(output, "layout-interaction-summary.json"),
            JsonSerializer.Serialize(summaries, new JsonSerializerOptions { WriteIndented = true }));
        if (failures != 0) throw new InvalidOperationException($"Layout interaction: {failures} failed cases.");
    }

    private static (List<Sample> Samples, string? Error) Measure(Window host, DataGrid grid,
        ScrollViewer viewer, string phase, double width, double height)
    {
        var samples = new List<Sample>(Measured);
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Input, host.Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(TimerMs) };
        var watchdog = new DispatcherTimer(DispatcherPriority.Background, host.Dispatcher)
        { Interval = TimeSpan.FromSeconds(1) };
        var started = Stopwatch.GetTimestamp();
        var lastTick = started;
        long pending = 0, lastWallRender = 0;
        double maxStall = 0;
        TimeSpan? lastWpfRender = null;
        var issued = 0;
        string? error = null;
        double? firstOffset = null, firstWidth = null;
        var movement = false;

        void Stop(string? message)
        {
            error ??= message;
            frame.Continue = false;
        }
        void Tick(object? sender, EventArgs args)
        {
            try
            {
                var now = Stopwatch.GetTimestamp();
                maxStall = Math.Max(maxStall,
                    Math.Max(0, Stopwatch.GetElapsedTime(lastTick, now).TotalMilliseconds - TimerMs));
                lastTick = now;
                if (pending != 0 || issued >= Warmup + Measured) return;
                pending = now;
                var step = issued++;
                if (phase == "scroll")
                    ScrollBar.ScrollToVerticalOffsetCommand.Execute(
                        viewer.ScrollableHeight * ((step * 17 % 97) / 96d), viewer);
                else
                {
                    host.Width = width - (step % 2 == 0 ? Math.Min(120, width / 10) : 0);
                    host.Height = height - (step % 2 == 0 ? Math.Min(80, height / 10) : 0);
                }
            }
            catch (Exception ex) { Stop(ex.Message); }
        }
        void Render(object? sender, EventArgs args)
        {
            try
            {
                if (args is not RenderingEventArgs render ||
                    lastWpfRender is TimeSpan previous && render.RenderingTime <= previous) return;
                var now = Stopwatch.GetTimestamp();
                var wpfInterval = lastWpfRender is TimeSpan before
                    ? (render.RenderingTime - before).TotalMilliseconds : double.NaN;
                var wallInterval = lastWallRender != 0
                    ? Stopwatch.GetElapsedTime(lastWallRender, now).TotalMilliseconds : double.NaN;
                lastWpfRender = render.RenderingTime;
                lastWallRender = now;
                if (pending == 0) return;
                var commandMs = Stopwatch.GetElapsedTime(pending, now).TotalMilliseconds;
                pending = 0;
                var stall = maxStall;
                maxStall = 0;

                // Timestamp first; count actual realized visual containers, never
                // scan all 25k model rows on every frame. Report inspection overhead.
                var inspect = Stopwatch.GetTimestamp();
                var realized = Descendants(grid).OfType<DataGridRow>().Count(r => r.IsVisible);
                var offset = viewer.ContentVerticalOffset;
                var dpi = VisualTreeHelper.GetDpi(host);
                firstOffset ??= offset;
                firstWidth ??= host.ActualWidth;
                movement |= phase == "scroll" ? Math.Abs(offset - firstOffset.Value) >= 1
                    : Math.Abs(host.ActualWidth - firstWidth.Value) >= 1;
                var valid = realized is > 0 and < 200 && viewer.ViewportWidth > 0 &&
                    viewer.ViewportHeight > 0 && !viewer.IsDeferredScrollingEnabled &&
                    grid.EnableRowVirtualization && grid.EnableColumnVirtualization &&
                    (phase != "scroll" || Math.Abs(offset - viewer.VerticalOffset) < 1);
                var inspectionMs = Stopwatch.GetElapsedTime(inspect).TotalMilliseconds;
                if (issued > Warmup)
                {
                    if (!double.IsFinite(wpfInterval) || !double.IsFinite(wallInterval))
                    { Stop("Missing distinct rendering callback."); return; }
                    samples.Add(new Sample(issued - Warmup, wpfInterval, wallInterval, commandMs,
                        stall, realized, dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                        host.ActualWidth * dpi.DpiScaleX, host.ActualHeight * dpi.DpiScaleY,
                        viewer.ViewportWidth, viewer.ViewportHeight, offset, inspectionMs, valid));
                }
                if (issued == Warmup + Measured)
                    Stop(movement ? null : "No actual scroll/resize movement was observed.");
            }
            catch (Exception ex) { Stop(ex.Message); }
        }
        void Timeout(object? sender, EventArgs args)
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(90))
                Stop("Timed out waiting for rendering samples.");
        }
        void Closed(object? sender, EventArgs args) => Stop("Measurement window closed.");
        timer.Tick += Tick;
        watchdog.Tick += Timeout;
        CompositionTarget.Rendering += Render;
        host.Closed += Closed;
        timer.Start();
        watchdog.Start();
        try { Dispatcher.PushFrame(frame); }
        finally
        {
            timer.Stop();
            watchdog.Stop();
            timer.Tick -= Tick;
            watchdog.Tick -= Timeout;
            CompositionTarget.Rendering -= Render;
            host.Closed -= Closed;
        }
        return (samples, error);
    }

    private static object Stats(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        // p99 only when at least 100 actual measured samples exist.
        double? P(double percentile, int minimum) => sorted.Length < minimum ? null :
            sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
        return new { p50Ms = P(.50, 1), p95Ms = P(.95, 30), p99Ms = P(.99, 100) };
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.TryPop(out var child))
        {
            yield return child;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(child); index++)
                stack.Push(VisualTreeHelper.GetChild(child, index));
        }
    }
}
