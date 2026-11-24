using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace GmcSmeta.Launcher;

internal sealed class UpdateWindow : Window
{
    private static readonly Brush Ink = Paint("#17263B");
    private static readonly Brush Muted = Paint("#617187");
    private static readonly Brush Accent = Paint("#2764D8");
    private readonly TextBlock _status = Text("Подготовка обновления…", 19, Ink, true);
    private readonly TextBlock _current = Text("—", 17, Ink, true);
    private readonly TextBlock _available = Text("—", 17, Ink, true);
    private readonly TextBlock _progressLabel = Text("Ожидание", 12, Muted);
    private readonly ProgressBar _progress = new() { Height = 8, Minimum = 0, Maximum = 100, IsIndeterminate = true };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontSize = 12, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        Foreground = Muted, Padding = new Thickness(2), MinHeight = 105, MaxHeight = 180
    };
    private readonly Button _action = new() { Content = "Отмена", MinWidth = 110, Padding = new Thickness(16, 8, 16, 8) };
    private readonly Expander _details = new() { Header = "Подробности", Margin = new Thickness(0, 14, 0, 0) };
    private readonly Action _cancel;
    private bool _finished, _locked, _ownerClosing, _cancelled;

    public UpdateWindow(Action cancel)
    {
        _cancel = cancel;
        Title = "ГМЦ-Смета • Центр обновлений";
        Width = 536; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Paint("#F3F6FA"); FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;
        var root = new StackPanel { Margin = new Thickness(24) };
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 20) };
        var mark = new Border { Background = Accent, CornerRadius = new CornerRadius(12), Width = 48, Height = 48, Margin = new Thickness(0, 0, 14, 0) };
        mark.Child = new TextBlock { Text = "ГМЦ", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(mark, Dock.Left); header.Children.Add(mark);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Text("ГМЦ-Смета", 22, Ink, true));
        names.Children.Add(Text("Центр обновлений • C# / Windows", 12, Muted));
        header.Children.Add(names); root.Children.Add(header);

        var panel = new StackPanel();
        var versions = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        versions.ColumnDefinitions.Add(new ColumnDefinition());
        versions.ColumnDefinitions.Add(new ColumnDefinition());
        var old = VersionBlock("Установленная версия", _current);
        var next = VersionBlock("Версия на GitHub", _available);
        Grid.SetColumn(next, 1); versions.Children.Add(old); versions.Children.Add(next);
        panel.Children.Add(versions);
        panel.Children.Add(_status);
        _progress.Foreground = Accent; _progress.Background = Paint("#E5EBF4");
        _progress.Margin = new Thickness(0, 15, 0, 8);
        panel.Children.Add(_progress); panel.Children.Add(_progressLabel);
        _details.Content = _log; panel.Children.Add(_details);
        root.Children.Add(new Border { Background = Brushes.White, BorderBrush = Paint("#E0E6EE"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(20), Child = panel });

        var footer = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 18, 0, 0) };
        _action.Background = Brushes.White; _action.Foreground = Ink; _action.BorderBrush = Paint("#D4DCE7");
        _action.Click += (_, _) => { if (_finished) Close(); else RequestCancel(); };
        DockPanel.SetDock(_action, Dock.Right); footer.Children.Add(_action);
        var hint = Text("Рабочая база и настройки не изменяются.", 11, Muted);
        hint.VerticalAlignment = VerticalAlignment.Center; hint.Margin = new Thickness(0, 0, 10, 0);
        footer.Children.Add(hint); root.Children.Add(footer);
        Content = root;
        Closing += (_, e) =>
        {
            if (_finished || _ownerClosing) return;
            e.Cancel = true;
            if (!_locked) RequestCancel();
        };
    }

    public void SetVersions(string current, string available) { _current.Text = current; _available.Text = available; }
    public void SetStep(string text)
    {
        if (!_cancelled) _status.Text = text;
        _progress.IsIndeterminate = true; _progressLabel.Text = "Выполняется…";
        Append(text);
    }
    public void Append(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _log.AppendText(text + Environment.NewLine);
        if (_log.Text.Length > 24000) _log.Text = _log.Text[^18000..];
        _log.ScrollToEnd();
    }
    public void SetProgress(long done, long total)
    {
        if (!_cancelled) _status.Text = "Загрузка обновления…";
        _progress.IsIndeterminate = total <= 0;
        if (total > 0) _progress.Value = Math.Clamp(100.0 * done / total, 0, 100);
        _progressLabel.Text = total > 0
            ? $"{done / 1048576.0:0.0} из {total / 1048576.0:0.0} МБ  •  {_progress.Value:0}%"
            : $"{done / 1048576.0:0.0} МБ";
    }
    public void LockCancellation() { _locked = true; _action.IsEnabled = false; }
    public void Finish(string text, bool error, string? detail = null)
    {
        _finished = true; _status.Text = text; _status.Foreground = error ? Paint("#B42318") : Ink;
        _progress.IsIndeterminate = false; _progress.Value = error || _cancelled ? 0 : 100;
        _progress.Foreground = error ? Paint("#B42318") : _cancelled ? Muted : Paint("#20835C");
        _progressLabel.Text = error ? "Подробности доступны ниже" : _cancelled ? "Установка не начата" : "Готово";
        if (detail is not null) { Append(detail); _details.IsExpanded = true; }
        _action.Content = "Закрыть"; _action.IsEnabled = true;
        _action.Background = Accent; _action.Foreground = Brushes.White; _action.BorderBrush = Accent;
    }
    public void CloseFromOwner() { _ownerClosing = true; Close(); }
    private void RequestCancel()
    {
        if (_locked || _cancelled) return;
        _cancelled = true; _action.IsEnabled = false; _status.Text = "Отменяю загрузку…";
        _cancel();
    }
    private static StackPanel VersionBlock(string label, TextBlock value)
    {
        var result = new StackPanel();
        result.Children.Add(Text(label, 12, Muted));
        value.Margin = new Thickness(0, 4, 0, 0); result.Children.Add(value);
        return result;
    }
    private static TextBlock Text(string value, double size, Brush color, bool bold = false) =>
        new() { Text = value, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
    private static Brush Paint(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze(); return brush;
    }
}
