namespace CompCalibrator.Dsp;

public static class Fft
{
    /// <summary>基数 2 の複素 FFT。長さは 2 のべき乗。結果は引数に上書きする。</summary>
    public static void Forward(double[] real, double[] imag)
    {
        var n = real.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var stepRe = Math.Cos(angle);
            var stepIm = Math.Sin(angle);
            for (var start = 0; start < n; start += length)
            {
                double wRe = 1, wIm = 0;
                for (var k = 0; k < length / 2; k++)
                {
                    var a = start + k;
                    var b = a + length / 2;
                    var tRe = real[b] * wRe - imag[b] * wIm;
                    var tIm = real[b] * wIm + imag[b] * wRe;
                    real[b] = real[a] - tRe;
                    imag[b] = imag[a] - tIm;
                    real[a] += tRe;
                    imag[a] += tIm;
                    var next = wRe * stepRe - wIm * stepIm;
                    wIm = wRe * stepIm + wIm * stepRe;
                    wRe = next;
                }
            }
        }
    }

    public static double[] Hann(int length)
    {
        var window = new double[length];
        for (var i = 0; i < length; i++)
        {
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (length - 1));
        }
        return window;
    }

    public static double[] Hamming(int length)
    {
        var window = new double[length];
        for (var i = 0; i < length; i++)
        {
            window[i] = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (length - 1));
        }
        return window;
    }
}
