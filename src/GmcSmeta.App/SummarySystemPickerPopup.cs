using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Local view-only equivalent of Python common._PickerPanel.
/// Search hides choices without clearing them; families apply all members,
/// not just the currently visible search results.</summary>
internal sealed class SummarySystemPickerPopup
{
    private readonly Popup _popup;
    private readonly TextBox _search;
    private readonly TextBlock _count;
    private readonly HashSet<long> _selected;
    private readonly Func<HashSet<long>, bool> _changed;
    private readonly List<(long Id, string Label, CheckBox Box)> _choices = new();
    private readonly List<(long[] Ids, CheckBox Box)> _families = new();
    private bool _syncing;

    internal SummarySystemPickerPopup(Button anchor, IReadOnlyList<(long Id, string Label)> options,
        IReadOnlyDictionary<long, long> parents, IEnumerable<long> selected, Func<HashSet<long>, bool> changed)
    {
        _selected = selected.ToHashSet();
        _changed = changed;
        _popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom,
            VerticalOffset = 4, StaysOpen = false, AllowsTransparency = true };
        var root = new DockPanel { Width = Math.Min(440, Math.Max(240, SystemParameters.WorkArea.Width - 40)),
            MaxHeight = Math.Min(560, Math.Max(240, SystemParameters.WorkArea.Height - 80)), LastChildFill = true };
        var frame = new Border { Background = Brushes.White, BorderBrush = Brush("#DAE3EF"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10), Child = root };
        _popup.Child = frame;
        frame.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };

        var searchBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        DockPanel.SetDock(searchBar, Dock.Top);
        var clear = new Button { Content = "Очистить", Width = 30, MinHeight = 34, Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Очистить поиск" };
        StyleFor(clear, "PythonToolbarButton");
        clear.SetResourceReference(ContentControl.ContentTemplateProperty, "IconOnlyX");
        System.Windows.Automation.AutomationProperties.SetName(clear, "Очистить поиск");
        DockPanel.SetDock(clear, Dock.Right);
        searchBar.Children.Add(clear);
        _search = new TextBox { MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Найти систему: В1, ВЕ, ДП…" };
        StyleFor(_search, "PythonSearch");
        var searchGrid = new Grid();
        searchGrid.Children.Add(_search);
        var hint = new TextBlock { Text = "Найти систему: В1, ВЕ, ДП…", TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "UiMutedTextBrush");
        System.Windows.Automation.AutomationProperties.SetName(_search, "Найти систему");
        searchGrid.Children.Add(hint);
        searchBar.Children.Add(searchGrid);
        root.Children.Add(searchBar);
        clear.Click += (_, _) => { _search.Clear(); _search.Focus(); };

        var footer = new StackPanel { Orientation = Orientation.Vertical };
        footer.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormFooterGap");
        DockPanel.SetDock(footer, Dock.Bottom);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var all = new Button { Content = "Все системы", MinHeight = 34,
            Margin = new Thickness(0, 0, 6, 0), ToolTip = "Снять все отметки — показать все системы" };
        StyleFor(all, "PythonToolbarButton");
        var done = new Button { Content = "Готово", MinHeight = 34 };
        StyleFor(done, "PythonToolbarPrimary");
        actions.Children.Add(all);
        actions.Children.Add(done);
        _count = DynamicFormLayout.Secondary("", "LayoutFormGroupHeadingGap");
        footer.Children.Add(_count);
        footer.Children.Add(actions);
        root.Children.Add(footer);
        done.Click += (_, _) => Close();
        all.Click += (_, _) => { _selected.Clear(); SyncAndPublish(); };

        var familyPanel = new WrapPanel();
        foreach (var family in options.GroupBy(o => SummarySystemSelection.Family(o.Label))
                     .Where(g => g.Key.Length != 0 && g.Count() >= 2))
        {
            var ids = family.Select(o => o.Id).ToArray();
            var chip = new CheckBox { Content = $"{family.Key} · {ids.Length}",
                Margin = new Thickness(0, 0, 6, 6), ToolTip = $"Отметить / снять все системы {family.Key}" };
            StyleFor(chip, "PythonFilterChip");
            _families.Add((ids, chip));
            familyPanel.Children.Add(chip);
            chip.Click += (_, _) =>
            {
                if (_syncing) return;
                if (chip.IsChecked == true) _selected.UnionWith(ids);
                else _selected.ExceptWith(ids);
                SyncAndPublish();
            };
        }
        if (_families.Count > 0)
        {
            var familyScroll = new ScrollViewer { Content = familyPanel, MaxHeight = 100,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            DockPanel.SetDock(familyScroll, Dock.Top);
            root.Children.Add(familyScroll);
        }

        var list = new StackPanel();
        foreach (var (id, label) in options)
        {
            var choice = new CheckBox { Content = label, MinHeight = 28,
                Margin = new Thickness(parents.ContainsKey(id) ? 20 : 4, 0, 4, 0),
                ToolTip = label, VerticalContentAlignment = VerticalAlignment.Center };
            DynamicFormLayout.WrapCaption(choice);
            _choices.Add((id, label, choice));
            list.Children.Add(choice);
            choice.Click += (_, _) =>
            {
                if (_syncing) return;
                if (choice.IsChecked == true) _selected.Add(id);
                else _selected.Remove(id);
                SyncAndPublish();
            };
        }
        root.Children.Add(new Border { BorderBrush = Brush("#DAE3EF"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7), Child = new ScrollViewer
            {
                Content = list, MaxHeight = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            } });
        _search.TextChanged += (_, _) =>
        {
            hint.Visibility = string.IsNullOrEmpty(_search.Text) ? Visibility.Visible : Visibility.Collapsed;
            foreach (var choice in _choices)
                choice.Box.Visibility = SummarySystemSelection.MatchesSearch(choice.Label, _search.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
        };
        Sync();
        _popup.Opened += (_, _) => _search.Dispatcher.BeginInvoke(DispatcherPriority.Input,
            new Action(() => { if (_popup.IsOpen) _search.Focus(); }));
    }

    internal void Open() => _popup.IsOpen = true;

    private void Close() => _popup.IsOpen = false;

    private void SyncAndPublish()
    {
        Sync();
        if (!_changed(new HashSet<long>(_selected))) Close();
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            foreach (var (id, _, box) in _choices) box.IsChecked = _selected.Contains(id);
            foreach (var (ids, box) in _families) box.IsChecked = ids.All(_selected.Contains);
            _count.Text = _selected.Count == 0 ? "Показаны все системы" : $"Отмечено: {_selected.Count}";
        }
        finally { _syncing = false; }
    }

    private static void StyleFor(FrameworkElement element, string key)
    {
        if (Application.Current?.TryFindResource(key) is Style style) element.Style = style;
    }

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));
}
