using System.Windows;
using System.Windows.Media;

namespace SyncWaveAudio.Views;

/// <summary>
/// Custom WPF control that renders an animated audio waveform visualization.
/// Supports a single waveform with smooth bezier curves, glow, and gradient fill.
/// </summary>
public class WaveformVisualizer : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty =
        DependencyProperty.Register(nameof(Samples), typeof(object), typeof(WaveformVisualizer),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeBrushProperty =
        DependencyProperty.Register(nameof(StrokeBrush), typeof(Brush), typeof(WaveformVisualizer),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0xB3)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelTextProperty =
        DependencyProperty.Register(nameof(LabelText), typeof(string), typeof(WaveformVisualizer),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public object? Samples { get => GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public Brush StrokeBrush { get => (Brush)GetValue(StrokeBrushProperty); set => SetValue(StrokeBrushProperty, value); }
    public string LabelText { get => (string)GetValue(LabelTextProperty); set => SetValue(LabelTextProperty, value); }

    private Color GetStrokeColor() => StrokeBrush is SolidColorBrush scb ? scb.Color : Color.FromRgb(0x3D, 0xD6, 0xB3);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 2 || h < 2) return;

        var samples = Samples as float[];
        var midY = h / 2.0;

        // Draw background grid lines
        DrawGrid(dc, w, h, midY);

        if (samples is null || samples.Length < 2)
        {
            DrawIdleLine(dc, w, h, midY);
            DrawLabel(dc, w);
            return;
        }

        var amplitude = h * 0.42;

        // Glow layer
        var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, GetStrokeColor().R, GetStrokeColor().G, GetStrokeColor().B)), 5.0)
        { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var geo = BuildWaveform(samples, w, midY, amplitude);
        dc.DrawGeometry(null, glowPen, geo);

        // Main waveform
        var mainPen = new Pen(new SolidColorBrush(GetStrokeColor()), 2.0)
        { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, mainPen, geo);

        // Gradient fill
        DrawFill(dc, geo, w, h, GetStrokeColor());

        // Center line
        var centerPen = new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xF4, 0xF7, 0xFB)), 0.5);
        dc.DrawLine(centerPen, new Point(0, midY), new Point(w, midY));

        // Label
        DrawLabel(dc, w);
    }

    private static void DrawGrid(DrawingContext dc, double w, double h, double midY)
    {
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x0C, 0xF4, 0xF7, 0xFB)), 0.5);
        for (var i = 1; i < 4; i++)
        {
            var y = h * i / 4.0;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }
    }

    private void DrawLabel(DrawingContext dc, double w)
    {
        if (string.IsNullOrEmpty(LabelText)) return;
        var tf = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var ft = new FormattedText(LabelText, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, tf, 10, new SolidColorBrush(Color.FromArgb(0x90, GetStrokeColor().R, GetStrokeColor().G, GetStrokeColor().B)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(6, 4));
    }

    private static StreamGeometry BuildWaveform(float[] samples, double w, double midY, double amplitude)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var count = samples.Length;
            var stepX = w / (count - 1);
            ctx.BeginFigure(new Point(0, midY - Math.Clamp(samples[0], -1f, 1f) * amplitude), false, false);

            for (var i = 1; i < count; i++)
            {
                var x0 = (i - 1) * stepX;
                var x1 = i * stepX;
                var y0 = midY - Math.Clamp(samples[i - 1], -1f, 1f) * amplitude;
                var y1 = midY - Math.Clamp(samples[i], -1f, 1f) * amplitude;
                var cpX = (x0 + x1) / 2;
                ctx.BezierTo(new Point(cpX, y0), new Point(cpX, y1), new Point(x1, y1), true, true);
            }
        }
        geo.Freeze();
        return geo;
    }

    private static void DrawFill(DrawingContext dc, StreamGeometry waveGeo, double w, double h, Color color)
    {
        var fillGeo = new StreamGeometry();
        using (var ctx = fillGeo.Open())
        {
            var flat = waveGeo.GetFlattenedPathGeometry(0.5, ToleranceType.Absolute);
            if (flat.Figures.Count == 0) return;
            var fig = flat.Figures[0];
            ctx.BeginFigure(fig.StartPoint, true, true);
            foreach (var seg in fig.Segments)
            {
                if (seg is PolyLineSegment pl) ctx.PolyLineTo(pl.Points, false, false);
                else if (seg is LineSegment ls) ctx.LineTo(ls.Point, false, false);
            }
            ctx.LineTo(new Point(w, h), false, false);
            ctx.LineTo(new Point(0, h), false, false);
        }
        fillGeo.Freeze();

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1),
            GradientStops = { new(Color.FromArgb(0x20, color.R, color.G, color.B), 0), new(Color.FromArgb(0x00, color.R, color.G, color.B), 1) }
        };
        brush.Freeze();
        dc.DrawGeometry(brush, null, fillGeo);
    }

    private void DrawIdleLine(DrawingContext dc, double w, double h, double midY)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, midY), false, false);
            for (var i = 1; i < 64; i++)
            {
                var t = i / 63.0;
                var y = midY + Math.Sin(t * Math.PI * 4) * 2.5 * Math.Sin(t * Math.PI);
                ctx.LineTo(new Point(i * w / 63, y), true, true);
            }
        }
        geo.Freeze();
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x30, GetStrokeColor().R, GetStrokeColor().G, GetStrokeColor().B)), 1.0)
        { LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, pen, geo);
    }
}
