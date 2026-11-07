namespace CompCalibrator.Tests;

/// <summary>音源フィルタモデルで作る合成音声。正解がわかっている入力として使う。</summary>
internal static class SyntheticVoice
{
    public const int SampleRate = 48000;

    public sealed record Spec(
        double PitchHz = 120,
        double F1 = 700,
        double F2 = 1200,
        double F3 = 2600,
        double SyllableRate = 5,
        double Level = 0.08,
        double NoiseLevel = 0.0004,
        double DurationSeconds = 16,
        double SilentLeadSeconds = 2,
        double LoudGain = 2.8,
        double QuietGain = 0.35);

    public static float[] Generate(Spec spec, int seed = 1)
    {
        var random = new Random(seed);
        var total = (int)(spec.DurationSeconds * SampleRate);
        var output = new float[total];
        var filters = new[] { new Resonator(spec.F1, 90), new Resonator(spec.F2, 110), new Resonator(spec.F3, 170) };

        var phase = 0.0;
        var syllablePhase = 0.0;
        var syllableGain = 1.0;
        var silent = (int)(spec.SilentLeadSeconds * SampleRate);
        double hold = 0;

        for (var n = 0; n < total; n++)
        {
            var noise = (random.NextDouble() - 0.5) * 2 * spec.NoiseLevel;
            if (n < silent)
            {
                output[n] = (float)noise;
                continue;
            }

            var time = (n - silent) / (double)SampleRate;
            var length = spec.DurationSeconds - spec.SilentLeadSeconds;
            var section = time / length;
            var gain = section > 0.85 ? spec.QuietGain : section > 0.6 ? spec.LoudGain : 1;

            syllablePhase += spec.SyllableRate / SampleRate;
            if (syllablePhase >= 1)
            {
                syllablePhase -= 1;
                syllableGain = 0.6 + 0.8 * random.NextDouble();
            }
            var syllable = Math.Pow(Math.Max(0, Math.Sin(Math.PI * syllablePhase)), 0.7);

            var hz = spec.PitchHz * (1 + 0.06 * Math.Sin(2 * Math.PI * 0.7 * time));
            phase += hz / SampleRate;
            var pulse = 0.0;
            if (phase >= 1)
            {
                phase -= 1;
                pulse = 1;
            }

            hold = 0.2 * hold + pulse;
            var voiced = 0.0;
            foreach (var filter in filters)
            {
                voiced += filter.Process(hold);
            }

            var value = voiced * spec.Level * syllable * syllableGain * gain * 30 + noise;
            output[n] = (float)Math.Clamp(value, -1, 1);
        }
        return output;
    }

    private sealed class Resonator
    {
        private readonly double _b0, _a1, _a2;
        private double _y1, _y2;

        public Resonator(double hz, double bandwidth)
        {
            var r = Math.Exp(-Math.PI * bandwidth / SampleRate);
            _a1 = -2 * r * Math.Cos(2 * Math.PI * hz / SampleRate);
            _a2 = r * r;
            _b0 = 1 - r;
        }

        public double Process(double x)
        {
            var y = _b0 * x - _a1 * _y1 - _a2 * _y2;
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }
}
