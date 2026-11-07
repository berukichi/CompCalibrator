using CompCalibrator.Analysis;

namespace CompCalibrator.Compression;

public sealed record VerificationResult(
    double PredictedSpreadDb,
    double MeasuredSpreadDb,
    double PredictedLufs,
    double MeasuredLufs,
    CompressorParams Corrected,
    bool Converged,
    string Verdict);

/// <summary>
/// 実機のコンプを ON にして録り直し、予測との差から値を補正する。
/// 機器のスレッショルドの基準は PC の dBFS と同じとは限らないので、ここで差を吸収する。
/// </summary>
public static class Verifier
{
    private const double SpreadTolerance = 1.5;
    private const double LoudnessTolerance = 1.0;
    private const double MaximumThresholdShift = 8;

    public static VerificationResult Evaluate(TuningResult tuning, VoiceMetrics measured)
    {
        var predictedSpread = tuning.Simulation.SpreadDb;
        var predictedLufs = tuning.Simulation.LoudnessLufs;
        var spreadError = measured.SpreadDb - predictedSpread;
        var loudnessError = measured.LoudnessLufs - predictedLufs;

        var slope = 1 - 1 / Math.Max(tuning.Params.Ratio, 1);
        var thresholdShift = 0.0;
        if (Math.Abs(spreadError) > SpreadTolerance && slope > 0.05)
        {
            // 幅が残っているならスレッショルドを下げ、潰れすぎなら上げる。
            thresholdShift = Math.Clamp(-spreadError / slope, -MaximumThresholdShift, MaximumThresholdShift);
        }

        // スレッショルドを動かすと音量も動くので、その分もメイクアップで打ち消す。
        var makeupShift = 0.0;
        if (Math.Abs(loudnessError) > LoudnessTolerance || thresholdShift != 0)
        {
            makeupShift = -loudnessError - thresholdShift * slope * 0.5;
        }

        var converged = thresholdShift == 0 && makeupShift == 0;
        var corrected = tuning.Params with
        {
            ThresholdDb = tuning.Params.ThresholdDb + thresholdShift,
            MakeupDb = tuning.Params.MakeupDb + makeupShift,
        };

        var verdict = converged
            ? "予測どおりです。この設定で確定できます。"
            : (spreadError, loudnessError) switch
            {
                ( > SpreadTolerance, _) => $"予測より音量の幅が {spreadError:0.0} dB 広く、コンプの効きが足りません。補正値を入れて、もう一度確認してください。",
                ( < -SpreadTolerance, _) => $"予測より音量の幅が {-spreadError:0.0} dB 狭く、効きすぎています。補正値を入れて、もう一度確認してください。",
                _ => $"効き方は予測どおりですが、音量が予測と {loudnessError:+0.0;-0.0} dB 違います。ゲインを補正してください。",
            };

        return new VerificationResult(predictedSpread, measured.SpreadDb, predictedLufs, measured.LoudnessLufs,
            corrected, converged, verdict);
    }
}
