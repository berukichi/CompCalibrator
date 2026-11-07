using CompCalibrator.Analysis;
using CompCalibrator.Profiles;

namespace CompCalibrator.Compression;

public enum TuningStyle
{
    /// <summary>抑揚を残す。雑談・歌向け。</summary>
    Natural,
    /// <summary>配信の標準。</summary>
    Balanced,
    /// <summary>音量を強くそろえる。叫ぶゲーム実況向け。</summary>
    Tight,
}

public sealed record ParameterRecommendation(
    ParameterSpec Spec, double Value, string Display, double Position, string Reason);

public sealed record TuningResult
{
    public required CompressorMode Mode { get; init; }
    public required TuningStyle Style { get; init; }
    public required IReadOnlyList<ParameterRecommendation> Settings { get; init; }
    public required IReadOnlyList<ParameterRecommendation> Related { get; init; }
    public required CompressorParams Params { get; init; }
    public required SimulationResult Simulation { get; init; }
    public required double InputSpreadDb { get; init; }
    public required double InputLoudnessLufs { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    /// <summary>macro のモードで示す、GR メーターの目標。</summary>
    public string? MacroInstruction { get; init; }
}

public static class CompressorSolver
{
    /// <summary>配信の声の目安。ゲームや BGM と混ぜる前提で少し控えめにしている。</summary>
    public const double TargetLufs = -18;
    private const double PeakCeilingDb = -1;

    private sealed record Goal(
        double Spread, double Reduction, double PeakReduction,
        double AttackScale, double ReleaseScale, double RatioPenalty);

    private static Goal GoalFor(TuningStyle style, double inputSpread) => style switch
    {
        TuningStyle.Natural => new Goal(Math.Max(10, inputSpread - 4), 2, 7, 1.3, 1.15, 1.6),
        TuningStyle.Tight => new Goal(Math.Max(6, inputSpread - 9), 5, 13, 0.7, 0.75, 0.5),
        _ => new Goal(Math.Max(8, inputSpread - 6), 3.5, 10, 1.0, 0.95, 1.0),
    };

    public static TuningResult Solve(AnalysisResult analysis, CompressorMode mode, TuningStyle style)
    {
        var metrics = analysis.Metrics;
        var envelope = analysis.Envelope;
        var goal = GoalFor(style, metrics.SpreadDb);
        var notes = new List<string>();

        var attackGoal = AttackGoal(metrics, goal);
        var releaseGoal = ReleaseGoal(metrics, goal);

        // macro のモードは数値を返さないので、標準的な比率で GR の目安だけを計算する。
        var thresholdSpec = mode.Find(ParamRole.Threshold) ?? FallbackThreshold;
        var ratioSpec = mode.Find(ParamRole.Ratio) ?? FallbackRatio;
        var attackSpec = mode.Find(ParamRole.Attack);
        var releaseSpec = mode.Find(ParamRole.Release);

        var attack = attackSpec?.Snap(attackGoal) ?? attackGoal;
        var release = releaseSpec?.Snap(releaseGoal) ?? releaseGoal;

        var best = SearchLevels(envelope, metrics, goal, thresholdSpec, ratioSpec, attack, release, attackGoal, releaseGoal);
        best = RefineTimes(envelope, metrics, goal, best, attackSpec, releaseSpec, attackGoal, releaseGoal);
        best = SearchLevels(envelope, metrics, goal, thresholdSpec, ratioSpec, best.AttackMs, best.ReleaseMs, attackGoal, releaseGoal);

        var simulation = CompressorSimulator.Run(envelope, best);
        var makeupGoal = MakeupGoal(simulation);
        var makeupSpec = mode.Find(ParamRole.Makeup);
        var makeup = makeupSpec?.Snap(makeupGoal) ?? 0;
        if (makeupSpec is not null && !makeupSpec.IsList && makeup > makeupGoal + 0.01)
        {
            makeup = makeupSpec.Snap(makeup - (makeupSpec.Step ?? 1));
        }
        if (mode.AutoMakeup)
        {
            makeup = simulation.MedianReductionDb;
        }
        best = best with { MakeupDb = makeup };
        simulation = CompressorSimulator.Run(envelope, best);

        if (makeupSpec is not null && makeupGoal > makeupSpec.Highest + 0.5)
        {
            notes.Add($"{TargetLufs:0} LUFS に届かせるには {makeupGoal:+0.0;-0.0} dB 必要です。{makeupSpec.Label} の上限で止めたので、マイクゲインも上げてください。");
        }
        else if (!mode.AutoMakeup && TargetLufs - simulation.LoudnessLufs > 1.5 && simulation.PeakDb > PeakCeilingDb - 1)
        {
            notes.Add($"ピークが {PeakCeilingDb:0} dBFS に届くので、ゲインをそこで止めました。適用後は {simulation.LoudnessLufs:0.0} LUFS です。");
        }
        if (simulation.PeakDb > PeakCeilingDb + 0.5)
        {
            notes.Add($"適用後のピークが {simulation.PeakDb:0.0} dBFS です。マイクゲインを少し下げて測定し直してください。");
        }
        if (metrics.SibilanceDb > SibilanceHigh)
        {
            notes.Add("歯擦音が強めです。コンプの後に De-Esser があれば、併用してください。");
        }

        var settings = new List<ParameterRecommendation>();
        if (!mode.IsMacro)
        {
            Add(settings, thresholdSpec, best.ThresholdDb, ThresholdReason(metrics, best, simulation));
            Add(settings, ratioSpec, best.Ratio, RatioReason(metrics, simulation));
            if (attackSpec is not null)
            {
                Add(settings, attackSpec, best.AttackMs, AttackReason(metrics, attackGoal));
            }
            if (releaseSpec is not null)
            {
                Add(settings, releaseSpec, best.ReleaseMs, ReleaseReason(metrics, releaseGoal));
            }
            if (makeupSpec is not null)
            {
                Add(settings, makeupSpec, best.MakeupDb,
                    $"適用後の声が {simulation.LoudnessLufs:0.0} LUFS になる量です(目標 {TargetLufs:0})。");
            }
        }

        var related = new List<ParameterRecommendation>();
        foreach (var spec in mode.Parameters)
        {
            if (spec.Role == ParamRole.LowCut)
            {
                var (value, reason) = LowCut(metrics, spec);
                Add(related, spec, value, reason);
            }
            else if (spec.Role == ParamRole.GateThreshold)
            {
                var (value, reason) = Gate(metrics, spec);
                Add(related, spec, value, reason);
            }
        }

        return new TuningResult
        {
            Mode = mode,
            Style = style,
            Settings = settings,
            Related = related,
            Params = best,
            Simulation = simulation,
            InputSpreadDb = metrics.SpreadDb,
            InputLoudnessLufs = metrics.LoudnessLufs,
            Notes = notes,
            MacroInstruction = mode.IsMacro
                ? $"{mode.MacroControl} を、普段の声でメーターが -{simulation.MedianReductionDb:0} dB 前後、盛り上がった声で -{simulation.PeakReductionDb:0} dB 前後になる位置に合わせてください。"
                : null,
        };
    }

    /// <summary>検証の結果などで、計算し直さずに値だけを差し替える。</summary>
    public static TuningResult WithParams(TuningResult previous, AnalysisResult analysis, CompressorParams updated)
    {
        var mode = previous.Mode;
        var snapped = new CompressorParams(
            mode.Find(ParamRole.Threshold)?.Snap(updated.ThresholdDb) ?? updated.ThresholdDb,
            updated.Ratio,
            updated.AttackMs,
            updated.ReleaseMs,
            mode.Find(ParamRole.Makeup)?.Snap(updated.MakeupDb) ?? updated.MakeupDb);
        var simulation = CompressorSimulator.Run(analysis.Envelope, snapped);

        var settings = previous.Settings.Select(item =>
        {
            var value = item.Spec.Role switch
            {
                ParamRole.Threshold => snapped.ThresholdDb,
                ParamRole.Makeup => snapped.MakeupDb,
                _ => item.Value,
            };
            return value == item.Value
                ? item
                : item with
                {
                    Value = value,
                    Display = item.Spec.Format(value),
                    Position = item.Spec.Position(value),
                    Reason = "検証の測定結果で補正しました。",
                };
        }).ToList();

        return previous with { Params = snapped, Simulation = simulation, Settings = settings };
    }

    private const double SibilanceHigh = -14;

    private static readonly ParameterSpec FallbackThreshold = new()
    {
        Role = ParamRole.Threshold, Label = "THRESHOLD", Unit = "dB", Min = -60, Max = 0, Step = 1,
    };

    private static readonly ParameterSpec FallbackRatio = new()
    {
        Role = ParamRole.Ratio, Label = "RATIO", Unit = ":1", Values = [3.0], Fixed = true,
    };

    private static double AttackGoal(VoiceMetrics m, Goal goal)
    {
        // アタックが基本周期より短いと、波形そのものを追って低音が歪む。
        var period = m.HasPitch ? 1000 / m.PitchHz : 8;
        var value = Math.Max(2 * period, 5) + 0.25 * Math.Min(m.OnsetMs, 40);
        return Math.Clamp(value * goal.AttackScale, 3, 60);
    }

    private static double ReleaseGoal(VoiceMetrics m, Goal goal)
    {
        // 音節の間で戻りきらず、かつ音節の中で波打たない長さを狙う。
        var syllable = 1000 / m.SyllablesPerSecond;
        var value = 0.9 * syllable * goal.ReleaseScale;
        if (m.HasPitch)
        {
            value = Math.Max(value, 10 * 1000 / m.PitchHz);
        }
        if (m.SnrDb < 25)
        {
            value *= 1.3;
        }
        if (m.SibilanceDb > SibilanceHigh)
        {
            value *= 1.15;
        }
        return Math.Clamp(value, 40, 800);
    }

    private static double Cost(VoiceMetrics m, Goal goal, CompressorParams p, QuickResult s,
        double attackGoal, double releaseGoal)
    {
        var spreadError = s.SpreadDb - goal.Spread;
        var cost = spreadError > 0 ? 1.0 * spreadError * spreadError : 0.4 * spreadError * spreadError;

        var reductionError = s.MedianReductionDb - goal.Reduction;
        cost += 1.2 * reductionError * reductionError;

        var overPeak = s.PeakReductionDb - goal.PeakReduction;
        if (overPeak > 0)
        {
            cost += 2.0 * overPeak * overPeak;
        }

        cost += goal.RatioPenalty * 6 * Math.Pow(Math.Log(Math.Max(p.Ratio, 1)), 2);
        cost += 2.0 * s.MotionDb;

        var attackError = Math.Log(Math.Max(p.AttackMs, 1) / attackGoal);
        var releaseError = Math.Log(p.ReleaseMs / releaseGoal);
        cost += 8 * attackError * attackError + 8 * releaseError * releaseError;

        // 環境ノイズを圧縮の対象にしない。
        var lowest = m.NoiseFloorDb + 12;
        if (p.ThresholdDb < lowest)
        {
            cost += 4 * Math.Pow(lowest - p.ThresholdDb, 2);
        }
        return cost;
    }

    private static CompressorParams SearchLevels(LevelEnvelope envelope, VoiceMetrics m, Goal goal,
        ParameterSpec thresholdSpec, ParameterSpec ratioSpec, double attack, double release,
        double attackGoal, double releaseGoal)
    {
        // 環境ノイズより下と、録音のピークより上は試すだけ無駄なので外す。
        var all = thresholdSpec.Candidates(40);
        var plausible = all.Where(t => t >= m.NoiseFloorDb + 6 && t <= m.SamplePeakDb).ToArray();
        var thresholds = plausible.Length > 0 ? plausible : all;
        var ratios = ratioSpec.Candidates(24);
        var best = new CompressorParams(thresholds[0], ratios[0], attack, release);
        var bestCost = double.MaxValue;

        foreach (var ratio in ratios)
        {
            foreach (var threshold in thresholds)
            {
                var candidate = new CompressorParams(threshold, ratio, attack, release);
                var cost = Cost(m, goal, candidate, CompressorSimulator.Evaluate(envelope, candidate), attackGoal, releaseGoal);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = candidate;
                }
            }
        }

        // 間引いて探したときは、実機の刻みで周辺を詰める。
        if (!thresholdSpec.IsList && thresholdSpec.Step is > 0)
        {
            var step = thresholdSpec.Step.Value;
            var span = thresholds.Length > 1 ? thresholds[1] - thresholds[0] : step;
            for (var offset = -span; offset <= span + 1e-9; offset += step)
            {
                var candidate = best with { ThresholdDb = thresholdSpec.Snap(best.ThresholdDb + offset) };
                var cost = Cost(m, goal, candidate, CompressorSimulator.Evaluate(envelope, candidate), attackGoal, releaseGoal);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = candidate;
                }
            }
        }
        return best;
    }

    private static CompressorParams RefineTimes(LevelEnvelope envelope, VoiceMetrics m, Goal goal, CompressorParams start,
        ParameterSpec? attackSpec, ParameterSpec? releaseSpec, double attackGoal, double releaseGoal)
    {
        var best = start;
        var bestCost = Cost(m, goal, best, CompressorSimulator.Evaluate(envelope, best), attackGoal, releaseGoal);

        foreach (var attack in Neighbors(attackSpec, start.AttackMs))
        {
            foreach (var release in Neighbors(releaseSpec, start.ReleaseMs))
            {
                var candidate = start with { AttackMs = attack, ReleaseMs = release };
                var cost = Cost(m, goal, candidate, CompressorSimulator.Evaluate(envelope, candidate), attackGoal, releaseGoal);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = candidate;
                }
            }
        }
        return best;
    }

    private static IEnumerable<double> Neighbors(ParameterSpec? spec, double value)
    {
        if (spec is null || spec.Fixed)
        {
            return [value];
        }
        if (spec.IsList)
        {
            var index = Array.IndexOf(spec.Values!, value);
            return spec.Values!.Skip(Math.Max(0, index - 2)).Take(5);
        }
        return new[] { 0.7, 0.85, 1, 1.2, 1.4 }.Select(scale => spec.Snap(value * scale)).Distinct();
    }

    private static double MakeupGoal(SimulationResult s)
    {
        var toTarget = double.IsNaN(s.LoudnessLufs) ? 0 : TargetLufs - s.LoudnessLufs;
        var toCeiling = PeakCeilingDb - s.PeakDb;
        return Math.Min(toTarget, toCeiling);
    }

    private static void Add(List<ParameterRecommendation> list, ParameterSpec spec, double value, string reason) =>
        list.Add(new ParameterRecommendation(spec, value, spec.Format(value), spec.Position(value), reason));

    private static string ThresholdReason(VoiceMetrics m, CompressorParams p, SimulationResult s) =>
        $"普段の声で約 {s.MedianReductionDb:0.0} dB、盛り上がった声で約 {s.PeakReductionDb:0.0} dB 効く位置です。ノイズ床 {m.NoiseFloorDb:0} dB には反応しません。";

    private static string RatioReason(VoiceMetrics m, SimulationResult s) =>
        $"音量の幅を {m.SpreadDb:0.0} dB から {s.SpreadDb:0.0} dB に縮めます。";

    private static string AttackReason(VoiceMetrics m, double goal)
    {
        var why = m.HasPitch
            ? $"声の高さ {m.PitchHz:0} Hz で波形を追わない長さと、立ち上がり {m.OnsetMs:0} ms を残す長さの両方から"
            : $"立ち上がり {m.OnsetMs:0} ms を残す長さから";
        return $"{why}求めました(理論値 {goal:0} ms)。";
    }

    private static string ReleaseReason(VoiceMetrics m, double goal) =>
        $"話す速さ {m.SyllablesPerSecond:0.0} 音節/秒に合わせ、音節の間で戻る長さです(理論値 {goal:0} ms)。";

    private static (double Value, string Reason) LowCut(VoiceMetrics m, ParameterSpec spec)
    {
        if (!m.HasPitch)
        {
            return (spec.Lowest, "声の高さが測れなかったので、切らない設定にしました。");
        }
        // 声の下端より十分低いところまでにとどめる。こもりが強ければ少し上げる。
        var limit = m.PitchLowHz * (m.BoomDb > -6 ? 0.85 : 0.65);
        var value = spec.Lowest;
        foreach (var candidate in spec.IsList ? spec.Values! : spec.Candidates(64))
        {
            if (candidate <= limit)
            {
                value = candidate;
            }
        }
        return (value, $"声の下端 {m.PitchLowHz:0} Hz を削らない範囲で、低域の不要な音を落とします。");
    }

    private static (double Value, string Reason) Gate(VoiceMetrics m, ParameterSpec spec)
    {
        var gap = m.QuietLevelDb - m.NoiseFloorDb;
        var goal = m.NoiseFloorDb + Math.Clamp(gap * 0.35, 4, 12);
        return (spec.Snap(goal), $"ノイズ床 {m.NoiseFloorDb:0} dB と小声 {m.QuietLevelDb:0} dB の間に置きました。");
    }
}
