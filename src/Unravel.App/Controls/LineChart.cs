using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Unravel.Core.Pages;

namespace Unravel.App.Controls;

/// <summary>A small line chart on a fixed scale (Reliability Monitor's 1-10 index), in the theme's colours.</summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(ChartModel), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ChartModel? Model
    {
        get => (ChartModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, 110);

    protected override void OnRender(DrawingContext dc)
    {
        if (Model is not { Points.Count: > 1 } model)
        {
            return;
        }

        var accent = (Brush)Application.Current.FindResource("Accent");
        var grid = (Brush)Application.Current.FindResource("Border");
        var muted = (Brush)Application.Current.FindResource("Muted");
        var typeface = new Typeface((FontFamily)Application.Current.FindResource("UiFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        FormattedText Text(string s) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, muted, dpi);

        const double left = 24, bottom = 18, top = 6;
        var width = ActualWidth - left;
        var height = ActualHeight - bottom - top;
        double Y(double v) => top + height * (1 - (Math.Clamp(v, model.Min, model.Max) - model.Min) / (model.Max - model.Min));

        // Grid lines at the scale's ends and middle, with labels.
        var gridPen = new Pen(grid, 1);
        foreach (var v in new[] { model.Max, (model.Min + model.Max) / 2, model.Min })
        {
            var y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(gridPen, new Point(left, y), new Point(ActualWidth, y));
            var label = Text(v.ToString("0", CultureInfo.CurrentCulture));
            dc.DrawText(label, new Point(left - label.Width - 6, y - label.Height / 2));
        }

        var step = width / (model.Points.Count - 1);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(left, Y(model.Points[0])), false, false);
            for (var i = 1; i < model.Points.Count; i++)
            {
                ctx.LineTo(new Point(left + i * step, Y(model.Points[i])), true, true);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(accent, 2) { LineJoin = PenLineJoin.Round }, geometry);

        if (model.StartLabel is { } start)
        {
            dc.DrawText(Text(start), new Point(left, ActualHeight - bottom + 3));
        }

        if (model.EndLabel is { } end)
        {
            var t = Text(end);
            dc.DrawText(t, new Point(ActualWidth - t.Width, ActualHeight - bottom + 3));
        }
    }
}
