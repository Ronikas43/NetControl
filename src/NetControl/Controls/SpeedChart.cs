using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NetControl.Services;

namespace NetControl.Controls;

/// <summary>
/// Lightweight, dependency-free area chart that draws live upload/download speeds.
/// Data comes exclusively from real TrafficMonitor samples.
/// </summary>
public class SpeedChart : FrameworkElement
{
    private IReadOnlyList<BandwidthSample> _data = Array.Empty<BandwidthSample>();
    private double _maxValue = 1024 * 100;

    // Pen/brush colors (kept independent from theme lookup so OnRender never throws).
    public Color DownloadColor { get; set; } = Color.FromRgb(0x4F, 0x8C, 0xFF);
    public Color UploadColor { get; set; } = Color.FromRgb(0x34, 0xC7, 0x7B);
    public Color GridColor { get; set; } = Color.FromArgb(0x40, 0x99, 0x9C, 0xAF);
    public Color LabelColor { get; set; } = Color.FromRgb(0x9C, 0x9C, 0xAF);
    public Color Background { get; set; } = Color.FromArgb(0x00, 0, 0, 0);

    public SpeedChart()
    {
        ClipToBounds = true;
    }

    /// <summary>Attach a traffic monitor; the chart updates on every sample.</summary>
    public void Attach(TrafficMonitor monitor)
    {
        if (monitor == null) return;
        monitor.SampleTaken += () =>
            Dispatcher.BeginInvoke(new Action(() => Update(monitor.History)));
        Update(monitor.History);
    }

    public void Update(IReadOnlyList<BandwidthSample> data)
    {
        _data = data ?? Array.Empty<BandwidthSample>();
        double max = 0;
        foreach (var s in _data)
            max = Math.Max(max, Math.Max(s.DownloadBps, s.UploadBps));
        _maxValue = max < 1024 ? 1024 : max * 1.15;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;

        dc.DrawRectangle(new SolidColorBrush(Background), null, new Rect(0, 0, w, h));

        // Grid + labels (4 horizontal lines)
        var labelBrush = new SolidColorBrush(LabelColor);
        var gridPen = new Pen(new SolidColorBrush(GridColor), 0.6);
        gridPen.Freeze();
        var typeface = new Typeface("Segoe UI");
        const int lines = 4;
        for (int i = 0; i <= lines; i++)
        {
            double y = h * i / lines;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            double val = _maxValue * (lines - i) / lines;
            string label = FormatSpeedLabel(val);
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, 10, labelBrush, 1.25);
            dc.DrawText(ft, new Point(4, Math.Max(0, y - 12)));
        }

        if (_data.Count < 2) return;

        int maxPoints = (int)Math.Min(_data.Count, Math.Max(60, w));
        int start = _data.Count - maxPoints;
        double stepX = w / Math.Max(1, maxPoints - 1);

        DrawSeries(dc, start, maxPoints, stepX, h, s => s.DownloadBps, DownloadColor);
        DrawSeries(dc, start, maxPoints, stepX, h, s => s.UploadBps, UploadColor);
    }

    private void DrawSeries(DrawingContext dc, int start, int count, double stepX, double h,
        Func<BandwidthSample, double> selector, Color color)
    {
        var pts = new List<Point>(count);
        for (int i = 0; i < count; i++)
        {
            var s = _data[start + i];
            double y = h - (selector(s) / _maxValue) * h;
            pts.Add(new Point(i * stepX, Math.Max(0, y)));
        }

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            foreach (var p in pts) ctx.LineTo(p, true, false);
            ctx.LineTo(new Point((count - 1) * stepX, h), true, false);
        }
        geo.Freeze();

        var fill = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B));
        fill.Freeze();
        var pen = new Pen(new SolidColorBrush(color), 1.6);
        pen.Freeze();

        dc.DrawGeometry(fill, pen, geo);
    }

    private static string FormatSpeedLabel(double bytesPerSec)
    {
        if (bytesPerSec < 1024) return $"{bytesPerSec:0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:0.#} KB/s";
        if (bytesPerSec < 1024 * 1024 * 1024) return $"{bytesPerSec / 1024 / 1024:0.#} MB/s";
        return $"{bytesPerSec / 1024 / 1024 / 1024:0.#} GB/s";
    }
}
