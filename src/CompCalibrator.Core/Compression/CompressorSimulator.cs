using CompCalibrator.Dsp;

namespace CompCalibrator.Compression;

public readonly record struct CompressorParams(
    double ThresholdDb, double Ratio, double AttackMs, double ReleaseMs, double MakeupDb = 0);

/// <summary>1 ms ごとに縮めた録音。候補を何千通りも試すための下ごしらえ。</summary>
public sealed class LevelEnvelope
{
    public const int WindowMs = 200;
    public const int WindowHopMs = 50;
    public const int LoudnessBlockMs = 400;
    /// <summary>最も低い声(60 Hz 台)の基本周期より長くする。</summary>
    private const int HoldMs = 18;
    /// <summary>候補を試すときの粗い評価の刻み。</summary>
    internal const int CoarseBlockMs = 5;

    internal double[] PeakDb { get; }
    internal double[] Power { get; }
    internal double[] CoarsePower { get; }
    internal double[] WeightedPower { get; }
    internal int[] ActiveWindowStarts { get; }
    internal bool[] Active { get; }

    public int Length => PeakDb.Length;

    private LevelEnvelope(double[] peakDb, double[] power, double[] weightedPower, bool[] active, int[] windows)
    {
        PeakDb = peakDb;
        Power = power;
        WeightedPower = weightedPower;
        Active = active;
        ActiveWindowStarts = windows;
        CoarsePower = new double[peakDb.Length / CoarseBlockMs];
        for (var block = 0; block < CoarsePower.Length; block++)
        {
            double sum = 0;
            for (var i = 0; i < CoarseBlockMs; i++)
            {
                sum += power[block * CoarseBlockMs + i];
            }
            CoarsePower[block] = sum / CoarseBlockMs;
        }
    }

    public static LevelEnvelope Build(float[] samples, int sampleRate, bool[] activeFrames, double frameHopSeconds)
    {
        var blockSize = Math.Max(1, sampleRate / 1000);
        var count = samples.Length / blockSize;
        var peak = new double[count];
        var power = new double[count];
        var weighted = new double[count];
        var filter = new KWeighting(sampleRate);

        for (var ms = 0; ms < count; ms++)
        {
            double top = 0, sum = 0, sumWeighted = 0;
            for (var i = 0; i < blockSize; i++)
            {
                double x = samples[ms * blockSize + i];
                top = Math.Max(top, Math.Abs(x));
                sum += x * x;
                var y = filter.Process(x);
                sumWeighted += y * y;
            }
            peak[ms] = SignalTools.ToDb(top);
            power[ms] = sum / blockSize;
            weighted[ms] = sumWeighted / blockSize;
        }

        var active = new bool[count];
        var framesPerMs = 1 / (frameHopSeconds * 1000);
        for (var ms = 0; ms < count; ms++)
        {
            var frame = (int)(ms * framesPerMs);
            active[ms] = frame < activeFrames.Length && activeFrames[frame];
        }

        var windows = new List<int>();
        for (var start = 0; start + WindowMs <= count; start += WindowHopMs)
        {
            var activeCount = 0;
            for (var i = 0; i < WindowMs; i++)
            {
                if (active[start + i])
                {
                    activeCount++;
                }
            }
            if (activeCount >= WindowMs / 2)
            {
                windows.Add(start);
            }
        }

        return new LevelEnvelope(peak, power, weighted, active, [.. windows]);
    }

    public double BaselineLoudness() => Loudness(new double[Length]);
    public double BaselineSpread() => Spread(new double[Length]);

    /// <summary>BS.1770 のゲート付きラウドネス。gainDb は 1 ms ごとのゲイン。</summary>
    public double Loudness(double[] gainDb)
    {
        var cumulative = new double[Length + 1];
        for (var i = 0; i < Length; i++)
        {
            cumulative[i + 1] = cumulative[i] + WeightedPower[i] * Math.Pow(10, gainDb[i] / 10);
        }

        var blocks = new List<double>();
        for (var start = 0; start + LoudnessBlockMs <= Length; start += 100)
        {
            var mean = (cumulative[start + LoudnessBlockMs] - cumulative[start]) / LoudnessBlockMs;
            if (-0.691 + 10 * Math.Log10(mean + 1e-12) > -70)
            {
                blocks.Add(mean);
            }
        }
        if (blocks.Count == 0)
        {
            return double.NaN;
        }

        var relativeGate = -0.691 + 10 * Math.Log10(blocks.Average()) - 10;
        var gated = blocks.Where(b => -0.691 + 10 * Math.Log10(b) > relativeGate).ToList();
        return gated.Count == 0 ? double.NaN : -0.691 + 10 * Math.Log10(gated.Average());
    }

    /// <summary>発話中の 200 ms レベルの幅(P95 - P10)。</summary>
    public double Spread(double[] gainDb)
    {
        var levels = WindowLevels(gainDb);
        if (levels.Length == 0)
        {
            return 0;
        }
        Array.Sort(levels);
        return SignalTools.PercentileSorted(levels, 0.95) - SignalTools.PercentileSorted(levels, 0.1);
    }

    internal double[] WindowLevels(double[] gainDb)
    {
        var cumulative = new double[Length + 1];
        for (var i = 0; i < Length; i++)
        {
            cumulative[i + 1] = cumulative[i] + Power[i] * Math.Pow(10, gainDb[i] / 10);
        }
        var levels = new double[ActiveWindowStarts.Length];
        for (var i = 0; i < levels.Length; i++)
        {
            var start = ActiveWindowStarts[i];
            levels[i] = SignalTools.PowerToDb((cumulative[start + WindowMs] - cumulative[start]) / WindowMs);
        }
        return levels;
    }

    /// <summary>
    /// 音節の山から見て、-12 dB から -3 dB まで上がるのにかかる時間。
    /// 1 ms のピークは声帯のパルスの谷に落ちるので、基本周期より長いホールドを通して測る。
    /// </summary>
    public double RiseTimeMs(int peakMs)
    {
        const int reach = 150;
        peakMs = Math.Clamp(peakMs, 0, Length - 1);

        var top = -200.0;
        var topAt = peakMs;
        for (var i = Math.Max(0, peakMs - reach / 2); i < Math.Min(Length, peakMs + reach / 2); i++)
        {
            if (PeakDb[i] > top)
            {
                top = PeakDb[i];
                topAt = i;
            }
        }

        var near = -1;
        for (var i = topAt; i > Math.Max(0, topAt - reach); i--)
        {
            var level = Smoothed(i);
            if (near < 0 && level < top - 3)
            {
                near = i;
            }
            if (near >= 0 && level < top - 12)
            {
                return near - i;
            }
        }
        return 0;
    }

    private double Smoothed(int ms)
    {
        var top = -200.0;
        for (var i = Math.Max(0, ms - HoldMs + 1); i <= ms; i++)
        {
            top = Math.Max(top, PeakDb[i]);
        }
        return top;
    }
}

/// <summary>候補の比較に使う軽い評価。ラウドネスは含まない。</summary>
public readonly record struct QuickResult(double MedianReductionDb, double PeakReductionDb, double SpreadDb, double MotionDb);

public sealed record SimulationResult(
    double[] GainDb,
    double MedianReductionDb,
    double PeakReductionDb,
    double MaxReductionDb,
    double SpreadDb,
    double LoudnessLufs,
    double PeakDb,
    double MotionDb);

public static class CompressorSimulator
{
    /// <summary>スレッショルドの前後をなだらかにする幅。実機の多くはソフトニーに近い。</summary>
    public const double KneeDb = 3;

    public static SimulationResult Run(LevelEnvelope envelope, CompressorParams p)
    {
        var length = envelope.Length;
        var gain = new double[length];
        var attack = Coefficient(p.AttackMs);
        var release = Coefficient(p.ReleaseMs);
        var slope = 1 - 1 / Math.Max(p.Ratio, 1);

        double reduction = 0;
        for (var ms = 0; ms < length; ms++)
        {
            var target = StaticReduction(envelope.PeakDb[ms], p.ThresholdDb, slope);
            var coefficient = target > reduction ? attack : release;
            reduction = coefficient * reduction + (1 - coefficient) * target;
            gain[ms] = -reduction + p.MakeupDb;
        }

        var active = new List<double>(length);
        double outputPeak = -200, motion = 0, maxReduction = 0;
        var motionCount = 0;
        const int motionStep = 20;
        for (var ms = 0; ms < length; ms++)
        {
            var applied = p.MakeupDb - gain[ms];
            outputPeak = Math.Max(outputPeak, envelope.PeakDb[ms] + gain[ms]);
            if (!envelope.Active[ms])
            {
                continue;
            }
            active.Add(applied);
            maxReduction = Math.Max(maxReduction, applied);
            if (ms >= motionStep && envelope.Active[ms - motionStep])
            {
                motion += Math.Abs(gain[ms] - gain[ms - motionStep]);
                motionCount++;
            }
        }

        var sorted = active.ToArray();
        Array.Sort(sorted);
        return new SimulationResult(
            gain,
            sorted.Length > 0 ? SignalTools.PercentileSorted(sorted, 0.5) : 0,
            sorted.Length > 0 ? SignalTools.PercentileSorted(sorted, 0.95) : 0,
            maxReduction,
            envelope.Spread(gain),
            envelope.Loudness(gain),
            outputPeak,
            motionCount > 0 ? motion / motionCount : 0);
    }

    /// <summary>
    /// 探索用の軽い評価。5 ms ごとに読むだけなので、全体の計算より大幅に速い。
    /// 1 回の解析で数千回呼ぶ。
    /// </summary>
    public static QuickResult Evaluate(LevelEnvelope envelope, CompressorParams p)
    {
        const double powerPerDb = 0.2302585092994046; // ln(10) / 10
        var length = envelope.Length;
        var blocks = envelope.CoarsePower.Length;
        var attack = Coefficient(p.AttackMs);
        var release = Coefficient(p.ReleaseMs);
        var slope = 1 - 1 / Math.Max(p.Ratio, 1);

        var cumulative = new double[blocks + 1];
        var active = new double[blocks];
        var activeCount = 0;
        double motion = 0, previous = 0;
        var motionCount = 0;

        double reduction = 0;
        for (var ms = 0; ms < length; ms++)
        {
            var target = StaticReduction(envelope.PeakDb[ms], p.ThresholdDb, slope);
            var coefficient = target > reduction ? attack : release;
            reduction = coefficient * reduction + (1 - coefficient) * target;

            if (ms % LevelEnvelope.CoarseBlockMs != 0)
            {
                continue;
            }
            var block = ms / LevelEnvelope.CoarseBlockMs;
            if (block >= blocks)
            {
                break;
            }
            cumulative[block + 1] = cumulative[block] + envelope.CoarsePower[block] * Math.Exp(-reduction * powerPerDb);
            if (envelope.Active[ms])
            {
                active[activeCount++] = reduction;
                if (block % 4 == 0)
                {
                    motion += Math.Abs(reduction - previous);
                    motionCount++;
                }
            }
            if (block % 4 == 0)
            {
                previous = reduction;
            }
        }

        if (activeCount == 0)
        {
            return default;
        }
        Array.Sort(active, 0, activeCount);

        var windowBlocks = LevelEnvelope.WindowMs / LevelEnvelope.CoarseBlockMs;
        var windows = envelope.ActiveWindowStarts;
        var levels = new double[windows.Length];
        for (var i = 0; i < windows.Length; i++)
        {
            var start = windows[i] / LevelEnvelope.CoarseBlockMs;
            var end = Math.Min(start + windowBlocks, blocks);
            levels[i] = SignalTools.PowerToDb((cumulative[end] - cumulative[start]) / Math.Max(1, end - start));
        }
        Array.Sort(levels);

        return new QuickResult(
            active[activeCount / 2],
            active[(int)(activeCount * 0.95)],
            levels.Length > 0 ? SignalTools.PercentileSorted(levels, 0.95) - SignalTools.PercentileSorted(levels, 0.1) : 0,
            motionCount > 0 ? motion / motionCount : 0);
    }

    /// <summary>静特性のゲインリダクション(dB、正の値)。</summary>
    public static double StaticReduction(double levelDb, double thresholdDb, double slope)
    {
        var over = levelDb - thresholdDb;
        if (over <= -KneeDb / 2)
        {
            return 0;
        }
        if (over >= KneeDb / 2)
        {
            return over * slope;
        }
        var inKnee = over + KneeDb / 2;
        return slope * inKnee * inKnee / (2 * KneeDb);
    }

    /// <summary>静特性の入出力(dB)。</summary>
    public static double StaticOutput(double levelDb, CompressorParams p) =>
        levelDb - StaticReduction(levelDb, p.ThresholdDb, 1 - 1 / Math.Max(p.Ratio, 1)) + p.MakeupDb;

    /// <summary>試聴用に実際の音へゲインをかける。</summary>
    public static float[] Render(float[] samples, int sampleRate, double[] gainDb)
    {
        var blockSize = Math.Max(1, sampleRate / 1000);
        var output = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var position = (double)i / blockSize;
            var index = Math.Min((int)position, gainDb.Length - 1);
            var next = Math.Min(index + 1, gainDb.Length - 1);
            var db = index < 0 ? 0 : gainDb[index] + (gainDb[next] - gainDb[index]) * (position - index);
            output[i] = (float)Math.Clamp(samples[i] * Math.Pow(10, db / 20), -1, 1);
        }
        return output;
    }

    private static double Coefficient(double timeMs) =>
        timeMs < 0.5 ? 0 : Math.Exp(-1 / timeMs);
}
