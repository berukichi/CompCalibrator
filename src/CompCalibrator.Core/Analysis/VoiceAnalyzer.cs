using CompCalibrator.Compression;
using CompCalibrator.Dsp;

namespace CompCalibrator.Analysis;

public sealed record SpectrumPoint(double Hz, double Db);

/// <summary>声の測定結果。レベルはすべて dBFS(ラウドネスは LUFS)。</summary>
public sealed record VoiceMetrics
{
    public required double DurationSeconds { get; init; }
    public required double SpeechSeconds { get; init; }

    public required double NoiseFloorDb { get; init; }
    public required double SpeechLevelDb { get; init; }
    public required double QuietLevelDb { get; init; }
    public required double LoudLevelDb { get; init; }
    public required double SnrDb { get; init; }
    public required double SamplePeakDb { get; init; }
    public required double CrestDb { get; init; }
    public required double LoudnessLufs { get; init; }
    /// <summary>発話中の短時間レベルの幅(P95 - P10)。</summary>
    public required double SpreadDb { get; init; }
    public required int ClippedSamples { get; init; }

    public required double PitchHz { get; init; }
    public required double PitchLowHz { get; init; }
    public required double PitchHighHz { get; init; }
    /// <summary>0 に近いほど澄んだ声、大きいほど息の成分が多い。</summary>
    public required double Breathiness { get; init; }

    public required double F1Hz { get; init; }
    public required double F2Hz { get; init; }
    public required double F3Hz { get; init; }

    public required double CentroidHz { get; init; }
    public required double TiltDbPerOctave { get; init; }
    /// <summary>80–250 Hz と 250 Hz–2 kHz の差。</summary>
    public required double BoomDb { get; init; }
    /// <summary>2–5 kHz と 250 Hz–2 kHz の差。</summary>
    public required double PresenceDb { get; init; }
    /// <summary>5–9 kHz と 250 Hz–2 kHz の差。</summary>
    public required double SibilanceDb { get; init; }

    public required double SyllablesPerSecond { get; init; }
    public required double OnsetMs { get; init; }

    public required IReadOnlyList<SpectrumPoint> Spectrum { get; init; }

    public bool HasPitch => PitchHz > 0;
    public bool HasFormants => F1Hz > 0 && F2Hz > 0;
}

public sealed record AnalysisResult(VoiceMetrics Metrics, LevelEnvelope Envelope, IReadOnlyList<string> Warnings)
{
    /// <summary>設定を計算できるだけの発話が録れている。</summary>
    public bool IsUsable => Metrics.SpeechSeconds >= VoiceAnalyzer.MinimumSpeechSeconds;
}

public static class VoiceAnalyzer
{
    public const double MinimumSpeechSeconds = 4;
    private const double DigitalSilenceDb = -100;

    public static AnalysisResult Analyze(float[] samples, int sampleRate)
    {
        var frames = BuildFrames(samples, sampleRate);
        var noise = SignalTools.Percentile(frames.LevelDb, 0.05);
        var active = DetectSpeech(frames.LevelDb, noise);

        var activeLevels = frames.LevelDb.Where((_, i) => active[i]).ToArray();
        var speechSeconds = activeLevels.Length * FrameHop;

        var envelope = LevelEnvelope.Build(samples, sampleRate, active, FrameHop);
        var metrics = Measure(samples, sampleRate, frames, active, activeLevels, noise, speechSeconds, envelope);
        return new AnalysisResult(metrics, envelope, Warn(metrics));
    }

    private const double FrameHop = 0.01;

    private sealed record Frames(double[] LevelDb, int Size, int Hop);

    private static Frames BuildFrames(float[] samples, int sampleRate)
    {
        var size = (int)(0.03 * sampleRate);
        var hop = (int)(FrameHop * sampleRate);
        var count = Math.Max(0, (samples.Length - size) / hop);
        var levels = new double[count];
        for (var i = 0; i < count; i++)
        {
            double sum = 0;
            var start = i * hop;
            for (var n = 0; n < size; n++)
            {
                sum += samples[start + n] * samples[start + n];
            }
            levels[i] = SignalTools.PowerToDb(sum / size);
        }
        return new Frames(levels, size, hop);
    }

    private static bool[] DetectSpeech(double[] levels, double noise)
    {
        var loud = SignalTools.Percentile(levels, 0.97);
        var gate = Math.Max(noise + 10, loud - 40);
        var active = new bool[levels.Length];
        const int hangover = 15;
        var remaining = 0;
        for (var i = 0; i < levels.Length; i++)
        {
            if (levels[i] > gate)
            {
                remaining = hangover;
            }
            active[i] = remaining > 0;
            remaining = Math.Max(0, remaining - 1);
        }
        return active;
    }

    private static VoiceMetrics Measure(float[] samples, int sampleRate, Frames frames, bool[] active,
        double[] activeLevels, double noise, double speechSeconds, LevelEnvelope envelope)
    {
        double peak = 0;
        var clipped = 0;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs(sample);
            peak = Math.Max(peak, magnitude);
            if (magnitude >= 0.99f)
            {
                clipped++;
            }
        }

        double speechLevel = double.NaN, quiet = double.NaN, loud = double.NaN;
        double crest = 0;
        if (activeLevels.Length > 0)
        {
            speechLevel = SignalTools.Percentile(activeLevels, 0.5);
            quiet = SignalTools.Percentile(activeLevels, 0.1);
            loud = SignalTools.Percentile(activeLevels, 0.95);
            double power = 0;
            foreach (var level in activeLevels)
            {
                power += Math.Pow(10, level / 10);
            }
            crest = SignalTools.ToDb(peak) - SignalTools.PowerToDb(power / activeLevels.Length);
        }

        var pitch = TrackPitch(samples, sampleRate, frames, active);
        var formants = TrackFormants(samples, sampleRate, frames, active, pitch.Voiced);
        var spectrum = LongTermSpectrum.Measure(samples, sampleRate, frames.Hop, active);
        var rhythm = RhythmAnalyzer.Measure(frames.LevelDb, active, envelope, speechSeconds);

        return new VoiceMetrics
        {
            DurationSeconds = samples.Length / (double)sampleRate,
            SpeechSeconds = speechSeconds,
            NoiseFloorDb = noise,
            SpeechLevelDb = speechLevel,
            QuietLevelDb = quiet,
            LoudLevelDb = loud,
            SnrDb = speechLevel - noise,
            SamplePeakDb = SignalTools.ToDb(peak),
            CrestDb = crest,
            LoudnessLufs = envelope.BaselineLoudness(),
            SpreadDb = envelope.BaselineSpread(),
            ClippedSamples = clipped,
            PitchHz = pitch.Median,
            PitchLowHz = pitch.Low,
            PitchHighHz = pitch.High,
            Breathiness = pitch.Aperiodicity,
            F1Hz = formants.F1,
            F2Hz = formants.F2,
            F3Hz = formants.F3,
            CentroidHz = spectrum.CentroidHz,
            TiltDbPerOctave = spectrum.Tilt,
            BoomDb = spectrum.BoomDb,
            PresenceDb = spectrum.PresenceDb,
            SibilanceDb = spectrum.SibilanceDb,
            SyllablesPerSecond = rhythm.SyllablesPerSecond,
            OnsetMs = rhythm.OnsetMs,
            Spectrum = spectrum.Points,
        };
    }

    private sealed record PitchSummary(double Median, double Low, double High, double Aperiodicity, bool[] Voiced);

    private static PitchSummary TrackPitch(float[] samples, int sampleRate, Frames frames, bool[] active)
    {
        var factor = Math.Max(1, (int)Math.Round(sampleRate / 16000.0));
        var reduced = SignalTools.Decimate(samples, factor);
        var reducedRate = sampleRate / factor;
        var window = (int)(0.04 * reducedRate);

        var voiced = new bool[active.Length];
        var pitches = new List<double>();
        var aperiodicity = new List<double>();
        // 10 ms ごとの判定を 20 ms おきに読む。隣のフレームは同じ判定で埋める。
        for (var i = 0; i < active.Length; i += 2)
        {
            if (!active[i])
            {
                continue;
            }
            var start = i * frames.Hop / factor;
            var result = PitchTracker.Estimate(reduced, start, window, reducedRate);
            if (result.Hz > 0)
            {
                pitches.Add(result.Hz);
                aperiodicity.Add(result.Aperiodicity);
                voiced[i] = true;
                if (i + 1 < voiced.Length)
                {
                    voiced[i + 1] = true;
                }
            }
        }

        if (pitches.Count < 10)
        {
            return new PitchSummary(0, 0, 0, 0, voiced);
        }
        return new PitchSummary(
            SignalTools.Percentile(pitches, 0.5),
            SignalTools.Percentile(pitches, 0.05),
            SignalTools.Percentile(pitches, 0.95),
            SignalTools.Percentile(aperiodicity, 0.5),
            voiced);
    }

    private static FormantTracker.Result TrackFormants(float[] samples, int sampleRate, Frames frames,
        bool[] active, bool[] voiced)
    {
        var factor = Math.Max(1, (int)Math.Round(sampleRate / 11025.0));
        var reduced = SignalTools.Decimate(samples, factor);
        var reducedRate = sampleRate / factor;
        var window = (int)(0.025 * reducedRate);
        var order = 2 + reducedRate / 1000;

        var levels = frames.LevelDb.Where((_, i) => voiced[i] && active[i]).ToArray();
        if (levels.Length == 0)
        {
            return default;
        }
        var gate = SignalTools.Percentile(levels, 0.5);

        List<double> f1 = [], f2 = [], f3 = [];
        for (var i = 0; i < voiced.Length; i += 2)
        {
            if (!voiced[i] || frames.LevelDb[i] < gate)
            {
                continue;
            }
            var result = FormantTracker.Estimate(reduced, i * frames.Hop / factor, window, reducedRate, order);
            if (result.F1 > 0)
            {
                f1.Add(result.F1);
            }
            if (result.F2 > 0)
            {
                f2.Add(result.F2);
            }
            if (result.F3 > 0)
            {
                f3.Add(result.F3);
            }
        }

        const int minimum = 8;
        return new FormantTracker.Result(
            f1.Count >= minimum ? SignalTools.Percentile(f1, 0.5) : 0,
            f2.Count >= minimum ? SignalTools.Percentile(f2, 0.5) : 0,
            f3.Count >= minimum ? SignalTools.Percentile(f3, 0.5) : 0);
    }

    private static List<string> Warn(VoiceMetrics m)
    {
        var warnings = new List<string>();
        if (m.SamplePeakDb < DigitalSilenceDb)
        {
            warnings.Add("入力が完全な無音です。マイクのミュート、機器側の出力設定、選んだ入力デバイスを確認してください。");
            return warnings;
        }
        if (m.SpeechSeconds < MinimumSpeechSeconds)
        {
            warnings.Add($"発話が {m.SpeechSeconds:0.0} 秒しか検出できませんでした。もう一度測定してください。");
        }
        if (m.ClippedSamples > 0)
        {
            warnings.Add("入力が飽和しています。インターフェース側のマイクゲインを下げて、測定し直してください。");
        }
        else if (m.SamplePeakDb < -24)
        {
            warnings.Add("入力が小さすぎます。マイクゲインを上げて、測定し直してください。");
        }
        if (m.SpeechSeconds >= MinimumSpeechSeconds && m.SnrDb < 20)
        {
            warnings.Add($"声と環境ノイズの差が {m.SnrDb:0} dB しかありません。ノイズも一緒に持ち上がります。");
        }
        if (m.SpeechSeconds >= MinimumSpeechSeconds && !m.HasPitch)
        {
            warnings.Add("声の高さを検出できませんでした。アタックとリリースは標準的な値から計算します。");
        }
        return warnings;
    }
}
