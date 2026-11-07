namespace CompCalibrator.Dsp;

/// <summary>YIN による基本周波数の推定。</summary>
public static class PitchTracker
{
    public readonly record struct Result(double Hz, double Aperiodicity);

    /// <summary>有声と判断できないフレームは Hz が 0 になる。</summary>
    public static Result Estimate(float[] signal, int start, int window, int sampleRate,
        double minHz = 60, double maxHz = 500)
    {
        var minLag = (int)(sampleRate / maxHz);
        var maxLag = Math.Min((int)(sampleRate / minHz), window - 1);
        if (start + window + maxLag >= signal.Length)
        {
            return default;
        }

        var difference = new double[maxLag + 2];
        for (var lag = 1; lag <= maxLag + 1; lag++)
        {
            double sum = 0;
            for (var i = 0; i < window; i++)
            {
                double delta = signal[start + i] - signal[start + i + lag];
                sum += delta * delta;
            }
            difference[lag] = sum;
        }

        var normalized = new double[maxLag + 2];
        normalized[0] = 1;
        double running = 0;
        for (var lag = 1; lag <= maxLag + 1; lag++)
        {
            running += difference[lag];
            normalized[lag] = running > 0 ? difference[lag] * lag / running : 1;
        }

        const double threshold = 0.15;
        var best = -1;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            if (normalized[lag] < threshold)
            {
                while (lag < maxLag && normalized[lag + 1] < normalized[lag])
                {
                    lag++;
                }
                best = lag;
                break;
            }
        }

        if (best < 0)
        {
            var lowest = double.MaxValue;
            for (var lag = minLag; lag <= maxLag; lag++)
            {
                if (normalized[lag] < lowest)
                {
                    lowest = normalized[lag];
                    best = lag;
                }
            }
            if (lowest > 0.35)
            {
                return new Result(0, lowest);
            }
        }

        var left = normalized[best - 1];
        var middle = normalized[best];
        var right = normalized[best + 1];
        var curve = left - 2 * middle + right;
        var offset = Math.Abs(curve) > 1e-12 ? 0.5 * (left - right) / curve : 0;
        return new Result(sampleRate / (best + Math.Clamp(offset, -1, 1)), middle);
    }
}

/// <summary>線形予測の包絡からフォルマントを読む。</summary>
public static class FormantTracker
{
    private const int SpectrumSize = 1024;

    public readonly record struct Result(double F1, double F2, double F3);

    /// <summary>取れなかったフォルマントは 0 になる。</summary>
    public static Result Estimate(float[] signal, int start, int window, int sampleRate, int order)
    {
        if (start + window >= signal.Length || start < 1)
        {
            return default;
        }

        var hamming = Fft.Hamming(window);
        var frame = new double[window];
        for (var i = 0; i < window; i++)
        {
            frame[i] = (signal[start + i] - 0.97 * signal[start + i - 1]) * hamming[i];
        }

        var autocorrelation = new double[order + 1];
        for (var lag = 0; lag <= order; lag++)
        {
            double sum = 0;
            for (var i = lag; i < window; i++)
            {
                sum += frame[i] * frame[i - lag];
            }
            autocorrelation[lag] = sum;
        }
        if (autocorrelation[0] <= 1e-12)
        {
            return default;
        }
        autocorrelation[0] *= 1.0001;

        var lpc = LevinsonDurbin(autocorrelation, order);

        var real = new double[SpectrumSize];
        var imag = new double[SpectrumSize];
        Array.Copy(lpc, real, lpc.Length);
        Fft.Forward(real, imag);

        var half = SpectrumSize / 2;
        var envelope = new double[half];
        for (var i = 0; i < half; i++)
        {
            envelope[i] = -10 * Math.Log10(real[i] * real[i] + imag[i] * imag[i] + 1e-12);
        }

        var binHz = sampleRate / (double)SpectrumSize;
        var peaks = new List<double>();
        for (var i = 2; i < half - 2; i++)
        {
            if (envelope[i] <= envelope[i - 1] || envelope[i] < envelope[i + 1])
            {
                continue;
            }
            var hz = i * binHz;
            if (hz < 180 || hz > 4200 || Bandwidth(envelope, i) * binHz > 700)
            {
                continue;
            }
            peaks.Add(hz);
        }

        double f1 = 0, f2 = 0, f3 = 0;
        foreach (var peak in peaks)
        {
            if (f1 == 0 && peak <= 1100)
            {
                f1 = peak;
            }
            else if (f1 > 0 && f2 == 0 && peak >= f1 + 150 && peak <= 3000)
            {
                f2 = peak;
            }
            else if (f2 > 0 && f3 == 0 && peak >= f2 + 150)
            {
                f3 = peak;
            }
        }
        return new Result(f1, f2, f3);
    }

    private static double[] LevinsonDurbin(double[] r, int order)
    {
        var a = new double[order + 1];
        var previous = new double[order + 1];
        a[0] = 1;
        var error = r[0];
        for (var i = 1; i <= order; i++)
        {
            var acc = r[i];
            for (var j = 1; j < i; j++)
            {
                acc += a[j] * r[i - j];
            }
            var reflection = -acc / error;
            Array.Copy(a, previous, order + 1);
            a[i] = reflection;
            for (var j = 1; j < i; j++)
            {
                a[j] = previous[j] + reflection * previous[i - j];
            }
            error *= 1 - reflection * reflection;
            if (error <= 0)
            {
                break;
            }
        }
        return a;
    }

    private static int Bandwidth(double[] envelope, int peak)
    {
        var level = envelope[peak] - 3;
        var low = peak;
        while (low > 0 && envelope[low] > level)
        {
            low--;
        }
        var high = peak;
        while (high < envelope.Length - 1 && envelope[high] > level)
        {
            high++;
        }
        return high - low;
    }
}
