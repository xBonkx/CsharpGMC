using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class EstimateView
{
    private bool _estimateFitQueued;
    private bool _estimateHeaderResizeActive;
    private bool _applyingEstimateOrder;
    private Dictionary<DataGridColumn, double>? _estimateResizeStart;

    private void InitializeEstimateColumnLayout()
    {
        EstimateGrid.SizeChanged += (_, e) => { if (e.WidthChanged) RequestEstimateColumnFit(); };
        EstimateGrid.IsVisibleChanged += (_, _) => RequestEstimateColumnFit();
        EstimateGrid.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(EstimateGridViewportChanged), true);
        EstimateGrid.AddHandler(Thumb.DragStartedEvent,
            new DragStartedEventHandler(EstimateHeaderDragStarted), true);
        EstimateGrid.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(EstimateHeaderDragCompleted), true);
        EstimateGrid.PreviewMouseLeftButtonDown += EstimateHeaderDoubleClick;
        EstimateGrid.ColumnReordered += (_, _) =>
        {
            if (_applyingEstimateOrder) return;
            ApplyEstimateOrder(_defaultEstimateColumns.OrderBy(c => c.DisplayIndex)
                .Select(ColumnPreferenceKey).ToArray());
            SaveEstimateUiSettings();
        };
        EstimateGrid.Loaded += (_, _) => RequestEstimateColumnFit();
        EstimateGrid.Unloaded += (_, _) =>
        {
            _estimateHeaderResizeActive = false;
            _estimateResizeStart = null;
        };
        RequestEstimateColumnFit();
    }

    private void EstimateGridViewportChanged(object sender, ScrollChangedEventArgs e)
    {
        // Ignore nested TextBox scroll viewers.
        if (e.OriginalSource is not ScrollViewer scroll ||
            !ReferenceEquals(scroll, EstimateScrollViewer())) return;
        if (Math.Abs(e.ViewportWidthChange) > 0.1) RequestEstimateColumnFit();
        if (e.VerticalChange != 0 || e.HorizontalChange != 0 ||
            e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
        {
            RefreshDragMarkerAfterScroll();
            RefreshEstimateFormulaHighlightsAfterScroll();
        }
    }

    private ScrollViewer? EstimateScrollViewer()
    {
        if (EstimateGrid.Template?.FindName("DG_ScrollViewer", EstimateGrid) is ScrollViewer named)
            return named;
        // Custom themes may use a different template part name. Stop at the
        // outer viewer, not an editor's nested scroll viewer.
        return FindOuterScrollViewer(EstimateGrid);
    }

    private static ScrollViewer? FindOuterScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindOuterScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private void RequestEstimateColumnFit()
    {
        if (!_uiSettingsReady || EstimateGrid is null || !EstimateGrid.IsVisible ||
            _estimateFitQueued || _estimateHeaderResizeActive) return;
        _estimateFitQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _estimateFitQueued = false;
            if (EstimateGrid.IsLoaded && EstimateGrid.IsVisible && !_estimateHeaderResizeActive)
                FitEstimateColumns();
        }), DispatcherPriority.Loaded);
    }

    private void FitEstimateColumns()
    {
        var scroll = EstimateScrollViewer();
        // WPF's horizontal viewport is measured in DIPs for the DataGrid.
        // Keep 2 DIP for rounding/grid chrome, as in Python common.setup_columns.
        var viewport = scroll?.ViewportWidth ?? 0;
        if (!double.IsFinite(viewport) || viewport < 300) return;
        var visible = _defaultEstimateColumns.Where(c => c.Visibility == Visibility.Visible).ToList();
        if (visible.Count == 0) return;

        var available = viewport - 2 - _inlineStageColumns
            .Where(c => c.Visibility == Visibility.Visible).Sum(c => c.ActualWidth);
        var widths = _uiSettings.ColumnWidths;
        var manual = widths.Count > 0;
        var inputs = visible.Select(column =>
        {
            var key = ColumnPreferenceKey(column);
            var fallback = _defaultEstimateWidths[column].Value;
            var preferred = widths.TryGetValue(key, out var stored) ? stored : fallback;
            var role = column == NameColumn ? EstimateColumnLayout.Role.Name :
                column == NoteColumn ? EstimateColumnLayout.Role.Note : EstimateColumnLayout.Role.Fixed;
            var minimum = role == EstimateColumnLayout.Role.Name ? 190 :
                role == EstimateColumnLayout.Role.Note ? 36 : Math.Floor(fallback * 0.75);
            minimum = manual ? Math.Max(36, column.MinWidth) :
                Math.Max(Math.Max(36, column.MinWidth), minimum);
            return new EstimateColumnLayout.Column(key, preferred, minimum, role);
        }).ToList();
        var fitted = EstimateColumnLayout.Fit(inputs, available, manual);
        foreach (var column in visible)
        {
            var width = fitted[ColumnPreferenceKey(column)];
            if (!column.Width.IsAbsolute || Math.Abs(column.Width.Value - width) > 0.1)
                column.Width = new DataGridLength(width);
        }
    }

    private void ApplyEstimateOrder(IEnumerable<string> preferred)
    {
        var defaults = _defaultEstimateColumns
            .Select((column, index) => (Column: column, Index: _defaultDisplayIndexes[index]))
            .OrderBy(x => x.Index).Select(x => x.Column).ToArray();
        var byId = defaults.ToDictionary(ColumnPreferenceKey, StringComparer.Ordinal);
        var fixedIds = defaults.Where(c => !c.CanUserReorder || c == NumberColumn || c == BasisColumn)
            .Select(ColumnPreferenceKey).ToHashSet(StringComparer.Ordinal);
        var ordered = ColumnOrderPolicy.Normalize(defaults.Select(ColumnPreferenceKey).ToArray(), preferred, fixedIds);
        _applyingEstimateOrder = true;
        try
        {
            // Base columns stay before the temporary stage block. The basis is
            // always the last base column, independent of form-specific header.
            for (int i = 0; i < ordered.Length; i++)
                if (byId[ordered[i]].DisplayIndex != i) byId[ordered[i]].DisplayIndex = i;
            for (int i = 0; i < _inlineStageColumns.Count; i++)
                if (_inlineStageColumns[i].DisplayIndex != ordered.Length + i)
                    _inlineStageColumns[i].DisplayIndex = ordered.Length + i;
        }
        finally { _applyingEstimateOrder = false; }
    }

    // Keep the form-specific basis header from creating a second preference key.
    private string ColumnPreferenceKey(DataGridColumn column) =>
        column == BasisColumn ? "Обоснование" : column.Header?.ToString() ?? "";

    private bool IsEstimateHeaderThumb(object? source) =>
        source is DependencyObject hit && VisualAncestor<Thumb>(hit) is not null &&
        VisualAncestor<DataGridColumnHeader>(hit) is not null;

    private Dictionary<DataGridColumn, double> SnapshotEstimateWidths() =>
        _defaultEstimateColumns.Where(c => c.Visibility == Visibility.Visible)
            .ToDictionary(c => c, c => c.ActualWidth);

    private void EstimateHeaderDragStarted(object sender, DragStartedEventArgs e)
    {
        if (!IsEstimateHeaderThumb(e.OriginalSource)) return;
        _estimateHeaderResizeActive = true;
        _estimateResizeStart = SnapshotEstimateWidths();
    }

    private void EstimateHeaderDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_estimateHeaderResizeActive) return;
        var before = _estimateResizeStart;
        _estimateResizeStart = null;
        // Wait for the header to finish applying the final drag width.
        CompleteEstimateHeaderResize(e.Canceled ? null : before);
    }

    private void EstimateHeaderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || !IsEstimateHeaderThumb(e.OriginalSource)) return;
        // WPF may set Width=Auto after a divider double click. Capture measured
        // width after layout, then return it to the same fitting policy.
        _estimateHeaderResizeActive = true;
        CompleteEstimateHeaderResize(SnapshotEstimateWidths());
    }

    private void CompleteEstimateHeaderResize(Dictionary<DataGridColumn, double>? before)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (before is not null && EstimateGrid.IsLoaded)
                {
                    var current = SnapshotEstimateWidths();
                    if (current.Any(pair => before.TryGetValue(pair.Key, out var previous) &&
                        double.IsFinite(pair.Value) && Math.Abs(pair.Value - previous) > 0.5))
                    {
                        // Freeze the visible layout, not just the dragged column:
                        // the next fit must not redistribute space to its neighbours.
                        foreach (var (column, actual) in current)
                            if (double.IsFinite(actual) && actual >= 36)
                                _uiSettings.ColumnWidths[ColumnPreferenceKey(column)] = Math.Clamp(actual, 36, 1600);
                        SaveEstimateUiSettings();
                    }
                }
            }
            finally
            {
                _estimateHeaderResizeActive = false;
                RequestEstimateColumnFit();
            }
        }), DispatcherPriority.Loaded);
    }
}
