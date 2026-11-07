using CompCalibrator.Analysis;
using CompCalibrator.Compression;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace CompCalibrator.App.Views;

/// <summary>Canvas に図を描く。線の数は少ないので Shape で足りる。</summary>
internal static class Charts
{
    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    private static FontFamily Mono => (FontFamily)Application.Current.Resources["MonoFont"];

    private static void Line(Canvas canvas, double x1, double y1, double x2, double y2, string brush, double thickness = 1)
    {
        canvas.Children.Add(new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = Res(brush), StrokeThickness = thickness,
        });
    }

    private static void Text(Canvas canvas, string text, double x, double y, string brush = "MutedBrush")
    {
        var block = new TextBlock { Text = text, FontFamily = Mono, FontSize = 10, Foreground = Res(brush) };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        canvas.Children.Add(block);
    }

    private static void Curve(Canvas canvas, IEnumerable<Point> points, string brush, double thickness = 1.5)
    {
        var line = new Polyline { Stroke = Res(brush), StrokeThickness = thickness };
        foreach (var point in points)
        {
            line.Points.Add(point);
        }
        canvas.Children.Add(line);
    }

    public static void Spectrum(Canvas canvas, VoiceMetrics metrics)
    {
        canvas.Children.Clear();
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width < 40 || height < 40 || metrics.Spectrum.Count == 0)
        {
            return;
        }

        const double left = 8, right = 8, top = 10, bottom = 22;
        const double lowHz = 63, highHz = 16000;
        var top_db = metrics.Spectrum.Max(p => p.Db);
        const double range = 60;

        double X(double hz) => left + (width - left - right) * Math.Log(hz / lowHz) / Math.Log(highHz / lowHz);
        double Y(double db) => top + (height - top - bottom) * Math.Clamp((top_db - db) / range, 0, 1);

        foreach (var (hz, label) in new[] { (100.0, "100"), (1000.0, "1k"), (10000.0, "10k") })
        {
            Line(canvas, X(hz), top, X(hz), height - bottom, "GridBrush");
            Text(canvas, label, X(hz) + 3, height - bottom + 5);
        }

        foreach (var (hz, label) in new[] { (metrics.F1Hz, "F1"), (metrics.F2Hz, "F2"), (metrics.F3Hz, "F3") })
        {
            if (hz > 0)
            {
                Line(canvas, X(hz), top, X(hz), height - bottom, "MutedBrush");
                Text(canvas, label, X(hz) + 3, top);
            }
        }

        if (metrics.PitchHz > 0)
        {
            Line(canvas, X(metrics.PitchHz), top, X(metrics.PitchHz), height - bottom, "MutedBrush");
            Text(canvas, "F0", X(metrics.PitchHz) + 3, top);
        }

        Curve(canvas, metrics.Spectrum.Where(p => p.Hz <= highHz).Select(p => new Point(X(p.Hz), Y(p.Db))), "LimeBrush");
    }

    public static void Transfer(Canvas canvas, CompressorParams p, VoiceMetrics metrics)
    {
        canvas.Children.Clear();
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width < 40 || height < 40)
        {
            return;
        }

        const double low = -60, high = 0, pad = 8;
        double X(double db) => pad + (width - 2 * pad) * (db - low) / (high - low);
        double Y(double db) => height - pad - (height - 2 * pad) * (Math.Clamp(db, low, high) - low) / (high - low);

        for (var db = -50.0; db < 0; db += 10)
        {
            Line(canvas, X(db), pad, X(db), height - pad, "GridBrush");
            Line(canvas, pad, Y(db), width - pad, Y(db), "GridBrush");
        }

        Line(canvas, X(low), Y(low), X(high), Y(high), "MutedBrush");

        if (!double.IsNaN(metrics.SpeechLevelDb))
        {
            Line(canvas, X(metrics.SpeechLevelDb), pad, X(metrics.SpeechLevelDb), height - pad, "MutedBrush");
            Text(canvas, "声", X(metrics.SpeechLevelDb) + 3, height - pad - 16);
        }

        var points = new List<Point>();
        for (var db = low; db <= high; db += 1)
        {
            points.Add(new Point(X(db), Y(CompressorSimulator.StaticOutput(db, p))));
        }
        Curve(canvas, points, "LimeBrush", 2);
        Text(canvas, $"T {p.ThresholdDb:0.#} dB", pad + 4, pad + 2, "InkBrush");
    }

    public static void Reduction(Canvas canvas, SimulationResult simulation, double makeupDb)
    {
        canvas.Children.Clear();
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        var gain = simulation.GainDb;
        if (width < 40 || height < 40 || gain.Length == 0)
        {
            return;
        }

        var floor = Math.Max(12, Math.Ceiling(simulation.MaxReductionDb / 3) * 3);
        const double pad = 8;
        double Y(double reduction) => pad + (height - 2 * pad) * Math.Clamp(reduction / floor, 0, 1);

        for (var db = 3.0; db < floor; db += 3)
        {
            Line(canvas, pad, Y(db), width - pad, Y(db), "GridBrush");
            Text(canvas, $"-{db:0}", pad + 2, Y(db) + 1);
        }

        var columns = (int)Math.Max(100, width - 2 * pad);
        var points = new List<Point>(columns);
        for (var column = 0; column < columns; column++)
        {
            var start = column * gain.Length / columns;
            var end = Math.Max(start + 1, (column + 1) * gain.Length / columns);
            double most = 0;
            for (var i = start; i < end; i++)
            {
                most = Math.Max(most, makeupDb - gain[i]);
            }
            points.Add(new Point(pad + column * (width - 2 * pad) / columns, Y(most)));
        }
        Curve(canvas, points, "LimeBrush", 1);
    }
}
