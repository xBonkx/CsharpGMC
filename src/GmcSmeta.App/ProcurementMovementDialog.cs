using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>
/// Python proc_dialogs.MovementDialog: one dated movement for many positions,
/// with editable quantities, optional document/recipient and direct-to-site flag.
/// Does not touch the database until the caller commits the complete batch.
/// </summary>
public sealed class ProcurementMovementDialog : Window
{
    public sealed class Line
    {
        public long PositionId { get; init; }
        public string Name { get; init; } = "";
        public string Unit { get; init; } = "";
        public decimal Planned { get; init; }
        public decimal Received { get; init; }
        public decimal Issued { get; init; }
        public decimal Quantity { get; set; }
    }

    private readonly DataGrid _grid;
    private readonly DatePicker _date;
    private readonly TextBox _document;
    private readonly TextBox _note;
    private readonly CheckBox _direct;
    private readonly ObservableCollection<Line> _lines;
    private readonly string _kind;

    public IReadOnlyList<SqlServerProcurement.MovementInput> Items { get; private set; } =
        Array.Empty<SqlServerProcurement.MovementInput>();
    public DateTime? MovementDate => _date.SelectedDate;
    public string Document => _document.Text.Trim();
    public string Note => _note.Text.Trim();
    public bool DirectToSite => _direct.IsChecked == true;

    public ProcurementMovementDialog(string kind,
        IReadOnlyList<ProcurementPreviewRow> rows,
        SqlServerProcurement.MovementHistoryRow? edit = null, string defaultDocument = "")
    {
        if (rows.Count == 0) throw new ArgumentException("Нет позиций для проведения движения.");
        _kind = kind;
        Title = edit is null ? SqlServerProcurement.MovementKindLabel(kind) :
            "Изменить движение — " + SqlServerProcurement.MovementKindLabel(kind);
        Width = 900;
        Height = 560;
        MinWidth = 650;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ProcurementDialogChrome.Apply(this);
        Background = System.Windows.Media.Brushes.White;

        var root = new Grid { Margin = ProcurementDialogChrome.Outer };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(ProcurementDialogChrome.Heading($"{Title} — позиций: {rows.Count}"));
        head.Children.Add(new TextBlock
        {
            Text = kind switch
            {
                "in" => "По умолчанию — сколько ещё не пришло. Можно провести частичную поставку.",
                "out" => "По умолчанию — сколько ещё не выдано. Выдача попадёт в ведомость объекта.",
                "back" => "Введите количество, возвращённое с объекта.",
                _ => "Введите количество, возвращённое поставщику."
            },
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(head);

        var form = new Grid { Margin = ProcurementDialogChrome.SectionGap };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++)
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        static void At(Grid panel, UIElement control, int row, int col)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, col);
            panel.Children.Add(control);
        }
        _date = new DatePicker
        {
            SelectedDate = edit?.Date ?? DateTime.Today,
            MinHeight = 30,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 180,
            Margin = new Thickness(0, 0, 0, 7)
        };
        _document = new TextBox
        {
            Text = edit?.Document ?? defaultDocument,
            MinHeight = 30,
            Margin = new Thickness(0, 0, 0, 7)
        };
        _note = new TextBox
        {
            Text = edit?.Note ?? "",
            MinHeight = 30,
            Margin = new Thickness(0, 0, 0, 7)
        };
        At(form, new TextBlock { Text = "Дата", VerticalAlignment = VerticalAlignment.Center }, 0, 0);
        At(form, _date, 0, 1);
        At(form, new TextBlock { Text = "Документ", VerticalAlignment = VerticalAlignment.Center }, 1, 0);
        At(form, _document, 1, 1);
        At(form, new TextBlock
        {
            Text = kind == "out" ? "Получатель" : "Примечание",
            VerticalAlignment = VerticalAlignment.Center
        }, 2, 0);
        At(form, _note, 2, 1);
        Grid.SetRow(form, 1);
        root.Children.Add(form);

        _lines = new ObservableCollection<Line>(rows.Select(row => new Line
        {
            PositionId = row.PositionId,
            Name = row.BuyName,
            Unit = row.Unit,
            Planned = row.QuantityValue,
            Received = row.ReceivedValue,
            Issued = row.IssuedValue,
            Quantity = edit is not null ? edit.Qty :
                kind == "in" ? Math.Max(0m, row.QuantityValue - row.ReceivedValue) :
                kind == "out" ? Math.Max(0m, row.QuantityValue - row.IssuedValue) : 0m
        }));
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = false,
            ItemsSource = _lines,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            MinRowHeight = 24,
            GridLinesVisibility = DataGridGridLinesVisibility.All
        };
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Наименование", Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            IsReadOnly = true, Binding = new Binding(nameof(Line.Name))
        });
        foreach (var col in new[]
        {
            ("Ед.", nameof(Line.Unit), 60),
            ("По смете", nameof(Line.Planned), 85),
            ("Пришло", nameof(Line.Received), 85),
            ("Выдано", nameof(Line.Issued), 85)
        })
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = col.Item1, Width = col.Item3, IsReadOnly = true,
                Binding = new Binding(col.Item2)
            });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Количество", Width = 115,
            Binding = new Binding(nameof(Line.Quantity))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
                ValidatesOnExceptions = true
            }
        });
        ProcurementDialogChrome.Table(_grid);
        foreach (var column in _grid.Columns.OfType<DataGridTextColumn>())
            ProcurementDialogChrome.Column(column, column.Header?.ToString() switch
            {
                "Наименование" => TextAlignment.Left,
                "Ед." => TextAlignment.Center,
                _ => TextAlignment.Right
            });
        TableColumnMenu.SetProtect(_grid, "Наименование|Ед.|Количество");
        TableColumnMenu.SetKey(_grid, "procurement-movement-preview");
        Grid.SetRow(_grid, 2);
        root.Children.Add(_grid);

        var footer = new DockPanel { Margin = ProcurementDialogChrome.FooterGap };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var save = new Button
        {
            Content = edit is null ? "Провести" : "Сохранить изменения",
            MinWidth = 135, Height = 32,
            Margin = new Thickness(8, 0, 0, 0)
        };
        save.Click += Accept;
        var cancel = new Button
        {
            Content = "Отмена", MinWidth = 90, Height = 32,
            Margin = new Thickness(8, 0, 0, 0)
        };
        cancel.Click += (_, _) => DialogResult = false;
        ProcurementDialogChrome.Buttons(save, cancel);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);

        _direct = new CheckBox
        {
            Content = "Сразу на объект — приход не требуется",
            IsEnabled = kind == "out" && edit is null,
            Visibility = kind == "out" && edit is null ? Visibility.Visible : Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center
        };
        _direct.Content = new TextBlock { Text = "Сразу на объект — приход не требуется", TextWrapping = TextWrapping.Wrap };
        footer.Children.Add(_direct);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
    }

    private void Accept(object sender, RoutedEventArgs e)
    {
        if (!_grid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !_grid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            MessageBox.Show(this, "Исправьте некорректное количество.", "Движение");
            return;
        }
        if (_date.SelectedDate is null)
        {
            MessageBox.Show(this, "Укажите дату движения.", "Движение");
            return;
        }
        if (_lines.Any(x => x.Quantity < 0m))
        {
            MessageBox.Show(this, "Количество не может быть отрицательным.", "Движение");
            return;
        }
        Items = _lines.Where(x => x.Quantity > 0m)
            .Select(x => new SqlServerProcurement.MovementInput(x.PositionId, x.Quantity))
            .ToArray();
        var validation = SqlServerProcurement.ValidateMovementBatch(Items, _kind);
        if (validation is not null)
        {
            MessageBox.Show(this, validation, "Движение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
