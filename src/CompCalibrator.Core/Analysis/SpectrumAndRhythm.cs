using CompCalibrator.Compression;
using CompCalibrator.Dsp;

namespace CompCalibrator.Analysis;

/// <summary>発話区間の長時間平均スペクトル。</summary>
internal static class LongTermSpectrum
{
    private const int Size = 4096;

    public sealed record Result(
        IReadOnlyList<SpectrumPoint> Points,
        double CentroidHz,
        double Tilt,
        double BoomDb,
        double PresenceDb,
        double SibilanceDb);

    public static Result Measure(float[] samples, int sampleRate, int frameHop, bool[] active)
    {
        var half = Size / 2;
        var power = new double[half];
        var window = Fft.Hann(Size);
        var real = new double[Size];
        var imag = new double[Size];
        var used = 0;

        for (var start = 0; start + Size < samples.Length; start += Size / 2)
        {
            var frame = (start + Size / 2) / frameHop;
            if (frame >= active.Length || !active[frame])
            {
                continue;
            }
            for (var i = 0; i < Size; i++)
            {
                real[i] = samples[start + i] * window[i];
                imag[i] = 0;
            }
            Fft.Forward(real, imag);
            for (var i = 0; i < half; i++)
            {
                power[i] += real[i] * real[i] + imag[i] * imag[i];
            }
            used++;
        }

        if (used == 0)
        {
            return new Result([], 0, 0, 0, 0, 0);
        }

        var binHz = sampleRate / (double)Size;
        double Band(double low, double high)
        {
            double sum = 0;
            var first = Math.Max(1, (int)(low / binHz));
            var last = Math.Min(half - 1, (int)(high / binHz));
            for (var i = first; i <= last; i++)
            {
                sum += power[i];
            }
            return SignalTools.PowerToDb(sum / used / (Size * Size));
        }

        double weighted = 0, total = 0;
        for (var i = (int)(100 / binHz); i < Math.Min(half, (int)(10000 / binHz)); i++)
        {
            weighted += power[i] * i * binHz;
            total += power[i];
        }

        var points = new List<SpectrumPoint>();
        for (var hz = 63.0; hz <= Math.Min(16000, sampleRate / 2.2); hz *= Math.Pow(2, 1 / 6.0))
        {
            var low = hz / Math.Pow(2, 1 / 12.0);
            var high = hz * Math.Pow(2, 1 / 12.0);
            points.Add(new SpectrumPoint(hz, Band(low, high)));
        }

        var body = Band(250, 2000);
        return new Result(
            points,
            total > 0 ? weighted / total : 0,
            Tilt(points),
            Band(80, 250) - body,
            Band(2000, 5000) - body,
            Band(5000, 9000) - body);
    }

    private static double Tilt(List<SpectrumPoint> points)
    {
        double n = 0, sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;
        foreach (var point in points)
        {
            if (point.Hz < 200 || point.Hz > 8000)
            {
                continue;
            }
            var x = Math.Log2(point.Hz);
            n++;
            sumX += x;
            sumY += point.Db;
            sumXy += x * point.Db;
            sumXx += x * x;
        }
        var denominator = n * sumXx - sumX * sumX;
        return denominator > 0 ? (n * sumXy - sumX * sumY) / denominator : 0;
    }
}

/// <summary>話す速さと、音節の立ち上がりの速さ。</summary>
internal static class RhythmAnalyzer
{
    private const double DefaultRate = 5;
    private const double DefaultOnsetMs = 15;
    private const double MinimumProminenceDb = 4;

    public readonly record struct Result(double SyllablesPerSecond, double OnsetMs);

    public static Result Measure(double[] levelDb, bool[] active, LevelEnvelope envelope, double speechSeconds)
    {
        if (levelDb.Length < 10 || speechSeconds < 1)
        {
            return new Result(DefaultRate, DefaultOnsetMs);
        }

        var smooth = new double[levelDb.Length];
        for (var i = 0; i < smooth.Length; i++)
        {
            double sum = 0;
            var count = 0;
            for (var k = Math.Max(0, i - 3); k <= Math.Min(smooth.Length - 1, i + 3); k++)
            {
                sum += levelDb[k];
                count++;
            }
            smooth[i] = sum / count;
        }

        var peaks = new List<int>();
        var trough = smooth[0];
        var climbing = false;
        var candidate = 0;
        for (var i = 1; i < smooth.Length; i++)
        {
            if (!climbing)
            {
                trough = Math.Min(trough, smooth[i]);
                if (smooth[i] - trough >= MinimumProminenceDb && active[i])
                {
                    climbing = true;
                    candidate = i;
                }
            }
            else if (smooth[i] >= smooth[candidate])
            {
                candidate = i;
            }
            else if (smooth[candidate] - smooth[i] >= MinimumProminenceDb)
            {
                peaks.Add(candidate);
                climbing = false;
                trough = smooth[i];
            }
        }

        if (peaks.Count < 3)
        {
            return new Result(DefaultRate, DefaultOnsetMs);
        }

        var rise = new List<double>();
        foreach (var peak in peaks)
        {
            var ms = envelope.RiseTimeMs(peak * 10);
            if (ms > 0)
            {
                rise.Add(ms);
            }
        }

        return new Result(
            Math.Clamp(peaks.Count / speechSeconds, 2, 9),
            rise.Count > 0 ? Math.Clamp(SignalTools.Percentile(rise, 0.5), 2, 80) : DefaultOnsetMs);
    }
}
