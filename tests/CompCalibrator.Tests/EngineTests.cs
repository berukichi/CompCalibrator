using CompCalibrator.Analysis;
using CompCalibrator.Compression;
using CompCalibrator.Profiles;

namespace CompCalibrator.Tests;

public class ProfileTests
{
    private static readonly IReadOnlyList<DeviceProfile> Profiles = ProfileLibrary.LoadBuiltIn();

    private static CompressorMode Mode(string device, string mode) =>
        Profiles.Single(p => p.Id == device).Modes.Single(m => m.Id == mode);

    [Fact]
    public void AllProfilesLoad() => Assert.Equal(9, Profiles.Count);

    [Theory]
    [InlineData(ParamRole.Threshold, -30, 0.375)]
    [InlineData(ParamRole.Ratio, 2.0, 6.0 / 13)]
    [InlineData(ParamRole.Attack, 20, 0.2)]
    [InlineData(ParamRole.Release, 200, 0.3)]
    [InlineData(ParamRole.Makeup, 0, 0)]
    [InlineData(ParamRole.Threshold, -48, 0)]
    [InlineData(ParamRole.Threshold, -21, 0.5625)]
    [InlineData(ParamRole.Ratio, 1.0, 0)]
    [InlineData(ParamRole.Ratio, 2.5, 7.0 / 13)]
    [InlineData(ParamRole.Ratio, 3.2, 8.0 / 13)]
    [InlineData(ParamRole.Attack, 0, 0)]
    [InlineData(ParamRole.Attack, 50, 0.5)]
    [InlineData(ParamRole.Attack, 100, 1)]
    [InlineData(ParamRole.Release, 50, 0)]
    [InlineData(ParamRole.Release, 130, 0.2)]
    [InlineData(ParamRole.Release, 640, 0.55)]
    [InlineData(ParamRole.Makeup, 17, 17.0 / 30)]
    [InlineData(ParamRole.Makeup, 30, 1)]
    [InlineData(ParamRole.LowCut, 31.5, 0.2)]
    [InlineData(ParamRole.GateThreshold, -24, 0.6)]
    public void BridgeCastMatchesTheSliderPositionsOnScreen(ParamRole role, double value, double position)
    {
        var spec = Mode("bridge-cast", "legacy").Find(role)!;
        Assert.Equal(position, spec.Position(value), 3);
    }

    [Fact]
    public void BridgeCastFormatsLikeTheApp()
    {
        var mode = Mode("bridge-cast", "legacy");
        Assert.Equal("-30dB", mode.Find(ParamRole.Threshold)!.Format(-30));
        Assert.Equal("2.00:1", mode.Find(ParamRole.Ratio)!.Format(2));
        Assert.Equal("20ms", mode.Find(ParamRole.Attack)!.Format(20));
        Assert.Equal("200ms", mode.Find(ParamRole.Release)!.Format(200));
        Assert.Equal("+0dB", mode.Find(ParamRole.Makeup)!.Format(0));
        Assert.Equal("31.5Hz", mode.Find(ParamRole.LowCut)!.Format(31.5));
    }

    [Fact]
    public void ManualFactsAreKept()
    {
        var ag = Mode("ag-mk2", "detail");
        Assert.Equal("THRESH", ag.Find(ParamRole.Threshold)!.Label);
        Assert.Equal(1.1, ag.Find(ParamRole.Ratio)!.Lowest);

        var highPass = Mode("revelator-io24", "standard").Find(ParamRole.LowCut)!;
        Assert.Equal(24, highPass.Lowest);
        Assert.Equal(1000, highPass.Highest);

        Assert.True(Mode("rodecaster-pro-2", "voxlab").IsMacro);
        Assert.True(Mode("revelator-io24", "tube").IsMacro);
    }

    [Fact]
    public void SnapUsesOnlyValuesTheDeviceHas()
    {
        var ratio = Mode("bridge-cast", "legacy").Find(ParamRole.Ratio)!;
        Assert.Equal(2.5, ratio.Snap(2.3));
        Assert.Equal(16, ratio.Snap(14));
        var threshold = Mode("obs-studio", "filter").Find(ParamRole.Threshold)!;
        Assert.Equal(-18.3, threshold.Snap(-18.27), 6);
        Assert.Equal(-60, threshold.Snap(-80));
    }

    [Fact]
    public void BrokenUserProfileIsReportedNotFatal()
    {
        var folder = Path.Combine(Path.GetTempPath(), "comp-calibrator-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "broken.json"), "{ not json");
            File.WriteAllText(Path.Combine(folder, "mine.json"), """
                {"id":"mine","name":"Mine","modes":[{"id":"m","label":"M","parameters":[
                  {"role":"threshold","label":"T","unit":"dB","min":-30,"max":0,"step":1}]}]}
                """);
            var errors = new List<string>();
            var profiles = ProfileLibrary.LoadAll(folder, errors);
            Assert.Contains(profiles, p => p.Id == "mine");
            Assert.Single(errors);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

public class AnalyzerTests
{
    private static AnalysisResult Analyze(SyntheticVoice.Spec spec) =>
        VoiceAnalyzer.Analyze(SyntheticVoice.Generate(spec), SyntheticVoice.SampleRate);

    [Theory]
    [InlineData(100)]
    [InlineData(130)]
    [InlineData(220)]
    public void FindsThePitch(double hz)
    {
        var metrics = Analyze(new SyntheticVoice.Spec(PitchHz: hz)).Metrics;
        Assert.InRange(metrics.PitchHz, hz * 0.95, hz * 1.05);
    }

    [Fact]
    public void FindsTheFormants()
    {
        var metrics = Analyze(new SyntheticVoice.Spec(F1: 700, F2: 1300, F3: 2600)).Metrics;
        Assert.InRange(metrics.F1Hz, 700 * 0.85, 700 * 1.15);
        Assert.InRange(metrics.F2Hz, 1300 * 0.85, 1300 * 1.15);
        Assert.InRange(metrics.F3Hz, 2600 * 0.85, 2600 * 1.15);
    }

    [Theory]
    [InlineData(3.5)]
    [InlineData(6)]
    public void FollowsTheSpeakingRate(double rate)
    {
        var metrics = Analyze(new SyntheticVoice.Spec(SyllableRate: rate)).Metrics;
        Assert.InRange(metrics.SyllablesPerSecond, rate * 0.7, rate * 1.3);
    }

    [Fact]
    public void MeasuresNoiseAndSpeechSeparately()
    {
        var metrics = Analyze(new SyntheticVoice.Spec()).Metrics;
        Assert.True(metrics.NoiseFloorDb < -65, $"noise {metrics.NoiseFloorDb}");
        Assert.True(metrics.SnrDb > 25, $"snr {metrics.SnrDb}");
        Assert.InRange(metrics.SpeechSeconds, 10, 14.5);
    }

    [Fact]
    public void SilenceProducesWarningsInsteadOfNumbers()
    {
        var result = VoiceAnalyzer.Analyze(new float[SyntheticVoice.SampleRate * 6], SyntheticVoice.SampleRate);
        Assert.Contains("無音", Assert.Single(result.Warnings));
        Assert.False(result.IsUsable);
    }

    [Fact]
    public void RoomNoiseAloneIsNotUsable()
    {
        var random = new Random(3);
        var noise = new float[SyntheticVoice.SampleRate * 6];
        for (var i = 0; i < noise.Length; i++)
        {
            noise[i] = (float)((random.NextDouble() - 0.5) * 0.002);
        }
        Assert.False(VoiceAnalyzer.Analyze(noise, SyntheticVoice.SampleRate).IsUsable);
    }

    [Fact]
    public void SpeechIsUsable() => Assert.True(Analyze(new SyntheticVoice.Spec()).IsUsable);
}

public class SimulatorTests
{
    [Fact]
    public void StaticCurveFollowsTheRatio()
    {
        var p = new CompressorParams(-20, 4, 10, 100);
        Assert.Equal(-30, CompressorSimulator.StaticOutput(-30, p), 6);
        Assert.Equal(-17, CompressorSimulator.StaticOutput(-8, p), 6);
    }

    [Fact]
    public void SteadyToneSettlesAtTheStaticReduction()
    {
        var samples = new float[SyntheticVoice.SampleRate * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * i / SyntheticVoice.SampleRate));
        }
        var active = Enumerable.Repeat(true, 200).ToArray();
        var envelope = LevelEnvelope.Build(samples, SyntheticVoice.SampleRate, active, 0.01);
        var result = CompressorSimulator.Run(envelope, new CompressorParams(-24, 2, 5, 50));
        // ピークは約 -12 dBFS、スレッショルドから 12 dB 上で 2:1 なら約 6 dB 下がる。
        Assert.InRange(result.MaxReductionDb, 5, 6.2);
    }
}

public class SolverTests
{
    private static readonly IReadOnlyList<DeviceProfile> Profiles = ProfileLibrary.LoadBuiltIn();

    private static AnalysisResult Voice(SyntheticVoice.Spec? spec = null) =>
        VoiceAnalyzer.Analyze(SyntheticVoice.Generate(spec ?? new SyntheticVoice.Spec()), SyntheticVoice.SampleRate);

    public static IEnumerable<object[]> ParametricModes() =>
        from profile in Profiles
        from mode in profile.Modes
        where !mode.IsMacro
        select new object[] { profile.Id, mode.Id };

    [Theory]
    [MemberData(nameof(ParametricModes))]
    public void EveryValueIsOneTheDeviceCanTake(string device, string modeId)
    {
        var mode = Profiles.Single(p => p.Id == device).Modes.Single(m => m.Id == modeId);
        var result = CompressorSolver.Solve(Voice(), mode, TuningStyle.Balanced);
        Assert.NotEmpty(result.Settings);
        foreach (var item in result.Settings.Concat(result.Related))
        {
            Assert.Equal(item.Spec.Snap(item.Value), item.Value, 6);
            Assert.True(item.Value >= item.Spec.Lowest - 1e-9 && item.Value <= item.Spec.Highest + 1e-9, item.Spec.Label);
            Assert.InRange(item.Position, 0, 1);
        }
    }

    [Fact]
    public void CompressionNarrowsTheLevelSpread()
    {
        var mode = Profiles.Single(p => p.Id == "bridge-cast").Modes.Single(m => m.Id == "legacy");
        var result = CompressorSolver.Solve(Voice(), mode, TuningStyle.Balanced);
        Assert.True(result.Simulation.SpreadDb < result.InputSpreadDb - 2,
            $"{result.InputSpreadDb} -> {result.Simulation.SpreadDb}");
        Assert.InRange(result.Simulation.MedianReductionDb, 0.5, 8);
    }

    [Fact]
    public void TighterStyleCompressesHarder()
    {
        var mode = Profiles.Single(p => p.Id == "goxlr").Modes[0];
        var voice = Voice();
        var natural = CompressorSolver.Solve(voice, mode, TuningStyle.Natural);
        var tight = CompressorSolver.Solve(voice, mode, TuningStyle.Tight);
        Assert.True(tight.Simulation.SpreadDb < natural.Simulation.SpreadDb,
            $"tight {tight.Simulation.SpreadDb} natural {natural.Simulation.SpreadDb}");
    }

    [Fact]
    public void LowerVoiceGetsLongerTimes()
    {
        var mode = Profiles.Single(p => p.Id == "obs-studio").Modes[0];
        var low = CompressorSolver.Solve(Voice(new SyntheticVoice.Spec(PitchHz: 85)), mode, TuningStyle.Balanced);
        var high = CompressorSolver.Solve(Voice(new SyntheticVoice.Spec(PitchHz: 240)), mode, TuningStyle.Balanced);
        Assert.True(low.Params.AttackMs > high.Params.AttackMs, $"{low.Params.AttackMs} vs {high.Params.AttackMs}");
    }

    [Fact]
    public void FasterSpeechGetsShorterRelease()
    {
        var mode = Profiles.Single(p => p.Id == "obs-studio").Modes[0];
        var slow = CompressorSolver.Solve(Voice(new SyntheticVoice.Spec(SyllableRate: 3)), mode, TuningStyle.Balanced);
        var fast = CompressorSolver.Solve(Voice(new SyntheticVoice.Spec(SyllableRate: 7)), mode, TuningStyle.Balanced);
        Assert.True(fast.Params.ReleaseMs < slow.Params.ReleaseMs, $"{fast.Params.ReleaseMs} vs {slow.Params.ReleaseMs}");
    }

    [Fact]
    public void MacroModeGivesAMeterTargetInsteadOfNumbers()
    {
        var mode = Profiles.Single(p => p.Id == "bridge-cast").Modes.Single(m => m.Id == "modern");
        var result = CompressorSolver.Solve(Voice(), mode, TuningStyle.Balanced);
        Assert.Empty(result.Settings);
        Assert.Contains("PEAK REDUCTION", result.MacroInstruction);
    }

    [Fact]
    public void FixedPresetValuesAreRespected()
    {
        var mode = Profiles.Single(p => p.Id == "vocaster").Modes[0];
        var result = CompressorSolver.Solve(Voice(), mode, TuningStyle.Balanced);
        Assert.Equal(4, result.Params.Ratio);
        Assert.Equal(10, result.Params.AttackMs);
        Assert.Equal(30, result.Params.ReleaseMs);
    }
}

public class VerifierTests
{
    private static readonly CompressorMode Mode =
        ProfileLibrary.LoadBuiltIn().Single(p => p.Id == "bridge-cast").Modes.Single(m => m.Id == "legacy");

    private static (TuningResult Tuning, VoiceMetrics Metrics) Base()
    {
        var analysis = VoiceAnalyzer.Analyze(SyntheticVoice.Generate(new SyntheticVoice.Spec()), SyntheticVoice.SampleRate);
        return (CompressorSolver.Solve(analysis, Mode, TuningStyle.Balanced), analysis.Metrics);
    }

    [Fact]
    public void MatchingTakeConverges()
    {
        var (tuning, metrics) = Base();
        var measured = metrics with { SpreadDb = tuning.Simulation.SpreadDb, LoudnessLufs = tuning.Simulation.LoudnessLufs };
        var result = Verifier.Evaluate(tuning, measured);
        Assert.True(result.Converged);
        Assert.Equal(tuning.Params, result.Corrected);
    }

    [Fact]
    public void WeakCompressionLowersTheThreshold()
    {
        var (tuning, metrics) = Base();
        var measured = metrics with { SpreadDb = tuning.Simulation.SpreadDb + 4, LoudnessLufs = tuning.Simulation.LoudnessLufs };
        var result = Verifier.Evaluate(tuning, measured);
        Assert.False(result.Converged);
        Assert.True(result.Corrected.ThresholdDb < tuning.Params.ThresholdDb);
    }

    [Fact]
    public void QuietTakeRaisesTheGain()
    {
        var (tuning, metrics) = Base();
        var measured = metrics with { SpreadDb = tuning.Simulation.SpreadDb, LoudnessLufs = tuning.Simulation.LoudnessLufs - 3 };
        var result = Verifier.Evaluate(tuning, measured);
        Assert.Equal(tuning.Params.MakeupDb + 3, result.Corrected.MakeupDb, 6);
    }
}
