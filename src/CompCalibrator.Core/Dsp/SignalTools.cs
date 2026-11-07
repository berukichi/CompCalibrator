namespace CompCalibrator.Dsp;

public static class SignalTools
{
    public const double SilenceDb = -120;

    public static double ToDb(double amplitude) =>
        amplitude > 1e-6 ? 20 * Math.Log10(amplitude) : SilenceDb;

    public static double PowerToDb(double power) =>
        power > 1e-12 ? 10 * Math.Log10(power) : SilenceDb;

    /// <summary>線形補間のパーセンタイル。p は 0〜1。入力は変更しない。</summary>
    public static double Percentile(IReadOnlyCollection<double> values, double p)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return PercentileSorted(sorted, p);
    }

    public static double PercentileSorted(double[] sorted, double p)
    {
        var position = Math.Clamp(p, 0, 1) * (sorted.Length - 1);
        var low = (int)position;
        var high = Math.Min(low + 1, sorted.Length - 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (position - low);
    }

    /// <summary>窓関数付き sinc のローパスを通してから間引く。</summary>
    public static float[] Decimate(float[] input, int factor)
    {
        if (factor <= 1)
        {
            return input;
        }

        var halfLength = 8 * factor;
        var kernel = new double[2 * halfLength + 1];
        var cutoff = 0.45 / factor;
        double sum = 0;
        for (var i = -halfLength; i <= halfLength; i++)
        {
            var sinc = i == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * i) / (Math.PI * i);
            var window = 0.54 + 0.46 * Math.Cos(Math.PI * i / halfLength);
            kernel[i + halfLength] = sinc * window;
            sum += kernel[i + halfLength];
        }
        for (var i = 0; i < kernel.Length; i++)
        {
            kernel[i] /= sum;
        }

        var output = new float[input.Length / factor];
        for (var n = 0; n < output.Length; n++)
        {
            var center = n * factor;
            double value = 0;
            for (var k = -halfLength; k <= halfLength; k++)
            {
                var index = center + k;
                if (index >= 0 && index < input.Length)
                {
                    value += input[index] * kernel[k + halfLength];
                }
            }
            output[n] = (float)value;
        }
        return output;
    }
}

/// <summary>ITU-R BS.1770 の K 特性。任意のサンプリング周波数で係数を求める。</summary>
public sealed class KWeighting
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private readonly double _hpA1, _hpA2;
    private double _x1, _x2, _y1, _y2, _hx1, _hx2, _hy1, _hy2;

    public KWeighting(int sampleRate)
    {
        var f0 = 1681.974450955533;
        var gain = 3.999843853973347;
        var q = 0.7071752369554196;
        var k = Math.Tan(Math.PI * f0 / sampleRate);
        var vh = Math.Pow(10, gain / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + k / q + k * k;
        _b0 = (vh + vb * k / q + k * k) / a0;
        _b1 = 2 * (k * k - vh) / a0;
        _b2 = (vh - vb * k / q + k * k) / a0;
        _a1 = 2 * (k * k - 1) / a0;
        _a2 = (1 - k / q + k * k) / a0;

        f0 = 38.13547087602444;
        q = 0.5003270373238773;
        k = Math.Tan(Math.PI * f0 / sampleRate);
        a0 = 1 + k / q + k * k;
        _hpA1 = 2 * (k * k - 1) / a0;
        _hpA2 = (1 - k / q + k * k) / a0;
    }

    public double Process(double x)
    {
        var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1;
        _x1 = x;
        _y2 = _y1;
        _y1 = y;

        var z = y - 2 * _hx1 + _hx2 - _hpA1 * _hy1 - _hpA2 * _hy2;
        _hx2 = _hx1;
        _hx1 = y;
        _hy2 = _hy1;
        _hy1 = z;
        return z;
    }
}
