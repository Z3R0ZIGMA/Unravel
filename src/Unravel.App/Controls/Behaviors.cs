using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Unravel.Core.Pages;

namespace Unravel.App.Controls;

/// <summary>
/// <c>local:Table.Model="{Binding}"</c> on a Grid fills it from a <see cref="TableModel"/>: a header row and one row per
/// item, with selectable values (plan.md section 7). Columns size to their content; the last takes the rest and wraps.
/// A plain Grid instead of a DataGrid: these tables are short, and a DataGrid inside a scrolling page collapses its
/// auto-sized columns and swallows the mouse wheel.
/// </summary>
public static class Table
{
    private const double MaxColumnWidth = 300;

    public static readonly DependencyProperty ModelProperty = DependencyProperty.RegisterAttached(
        "Model", typeof(TableModel), typeof(Table), new PropertyMetadata(null, OnModelChanged));

    public static TableModel? GetModel(DependencyObject d) => (TableModel?)d.GetValue(ModelProperty);

    public static void SetModel(DependencyObject d, TableModel? value) => d.SetValue(ModelProperty, value);

    private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid)
        {
            return;
        }

        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        if (e.NewValue is not TableModel model || model.Columns.Count == 0)
        {
            return;
        }

        var columns = model.Columns.Count;
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = c == columns - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });
        }

        var border = (Brush)Application.Current.FindResource("Border");

        // Header
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < columns; c++)
        {
            var header = new TextBlock
            {
                Text = model.Columns[c].ToUpper(System.Globalization.CultureInfo.CurrentCulture),
                Style = (Style)Application.Current.FindResource("CapsLabel"),
                Margin = new Thickness(0, 0, c == columns - 1 ? 0 : 18, 6),
                TextWrapping = TextWrapping.NoWrap,
            };
            Grid.SetColumn(header, c);
            grid.Children.Add(header);
        }

        var valueStyle = (Style)Application.Current.FindResource("ValueText");
        for (var r = 0; r < model.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = r + 1;

            // A line above each row.
            var line = new Border { BorderBrush = border, BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false };
            Grid.SetRow(line, row);
            Grid.SetColumnSpan(line, columns);
            grid.Children.Add(line);

            var cells = model.Rows[r];
            for (var c = 0; c < columns; c++)
            {
                var last = c == columns - 1;
                var cell = new TextBox
                {
                    Text = c < cells.Count ? cells[c] : "",
                    Style = valueStyle,
                    FontSize = 13,
                    Margin = new Thickness(0, 6, last ? 0 : 18, 6),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = last ? double.PositiveInfinity : MaxColumnWidth,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
    }
}

/// <summary>
/// <c>local:WheelToParent.Enabled="True"</c> passes mouse-wheel scrolling on to the page, so a list inside a
/// scrolling page does not swallow the wheel.
/// </summary>
public static class WheelToParent
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(WheelToParent), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element)
        {
            element.PreviewMouseWheel -= OnWheel;
            if (e.NewValue is true)
            {
                element.PreviewMouseWheel += OnWheel;
            }
        }
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject d)
        {
            return;
        }

        e.Handled = true;
        var parent = VisualTreeHelper.GetParent(d) as UIElement;
        parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
    }
}
