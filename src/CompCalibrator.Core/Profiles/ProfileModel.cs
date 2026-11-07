using System.Globalization;

namespace CompCalibrator.Profiles;

public enum ParamRole { Threshold, Ratio, Attack, Release, Makeup, LowCut, GateThreshold }

/// <summary>数値がどこまで確かめられているか。画面にそのまま出す。</summary>
public enum Confidence
{
    /// <summary>公開された資料やソースコードで確認した。</summary>
    Verified,
    /// <summary>実機のアプリの表示で確認した。</summary>
    Device,
    /// <summary>画面の位置などからの推定。</summary>
    Inferred,
    /// <summary>未確認の仮置き。</summary>
    Unverified,
    /// <summary>特定の機器を指さない汎用の範囲。</summary>
    Any,
}

public sealed class DeviceProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Maker { get; set; } = "";
    public string Software { get; set; } = "";
    public string CaptureHint { get; set; } = "";
    public List<CompressorMode> Modes { get; set; } = [];
    public List<SourceRef> Sources { get; set; } = [];

    public override string ToString() => Name;
}

public sealed class SourceRef
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class CompressorMode
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>"parametric" は数値を計算する。"macro" は GR の目標だけを示す。</summary>
    public string Kind { get; set; } = "parametric";
    public string Note { get; set; } = "";
    public string MacroControl { get; set; } = "";
    /// <summary>メイクアップが閾値と連動していて、個別に設定できない。</summary>
    public bool AutoMakeup { get; set; }
    public List<ParameterSpec> Parameters { get; set; } = [];

    public bool IsMacro => string.Equals(Kind, "macro", StringComparison.OrdinalIgnoreCase);

    public ParameterSpec? Find(ParamRole role) => Parameters.FirstOrDefault(p => p.Role == role);

    public override string ToString() => Label;
}

/// <summary>機器の 1 項目。連続値(min/max/step)か、離散値のリスト(values)のどちらか。</summary>
public sealed class ParameterSpec
{
    public ParamRole Role { get; set; }
    public string Label { get; set; } = "";
    public string Unit { get; set; } = "";
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    public double[]? Values { get; set; }
    public string[]? Labels { get; set; }
    public bool Plus { get; set; }
    public bool Fixed { get; set; }
    public Confidence Confidence { get; set; } = Confidence.Unverified;
    public string Note { get; set; } = "";

    public bool IsList => Values is { Length: > 0 };

    public double Lowest => IsList ? Values![0] : Min ?? 0;
    public double Highest => IsList ? Values![^1] : Max ?? 0;

    /// <summary>実機で設定できる最も近い値にする。比率と時間は対数で近さを測る。</summary>
    public double Snap(double value)
    {
        if (IsList)
        {
            var useLog = Role is ParamRole.Ratio or ParamRole.Attack or ParamRole.Release or ParamRole.LowCut;
            var best = Values![0];
            var bestDistance = double.MaxValue;
            foreach (var candidate in Values)
            {
                var distance = useLog
                    ? Math.Abs(Math.Log(Math.Max(candidate, 0.5)) - Math.Log(Math.Max(value, 0.5)))
                    : Math.Abs(candidate - value);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            return best;
        }

        var min = Min ?? value;
        var max = Max ?? value;
        var step = Step is > 0 ? Step.Value : 1;
        var snapped = Math.Round((value - min) / step) * step + min;
        snapped = Math.Round(snapped, Decimals(step) + 2);
        return Math.Clamp(snapped, min, max);
    }

    /// <summary>実機で設定できる値を、多すぎるときは間引いて返す。</summary>
    public double[] Candidates(int limit)
    {
        if (IsList)
        {
            return Values!;
        }
        var min = Min ?? 0;
        var max = Max ?? 0;
        var step = Step is > 0 ? Step.Value : 1;
        var count = (int)Math.Round((max - min) / step) + 1;
        var stride = Math.Max(1, (int)Math.Ceiling(count / (double)limit));
        var list = new List<double>();
        for (var i = 0; i < count; i += stride)
        {
            list.Add(Snap(min + i * step));
        }
        return [.. list];
    }

    /// <summary>実機のスライダーのどこに来るか(0〜1)。リストは段の位置で返す。</summary>
    public double Position(double value)
    {
        if (IsList)
        {
            if (Values!.Length < 2)
            {
                return 0.5;
            }
            var index = Array.IndexOf(Values, Snap(value));
            return index / (double)(Values.Length - 1);
        }
        var min = Min ?? 0;
        var max = Max ?? 1;
        return max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;
    }

    /// <summary>実機の画面と同じ書き方にする。</summary>
    public string Format(double value)
    {
        if (IsList && Labels is { Length: > 0 })
        {
            var index = Array.IndexOf(Values!, Snap(value));
            if (index >= 0 && index < Labels.Length)
            {
                return Labels[index];
            }
        }

        var decimals = IsList ? DecimalsOf(value) : Decimals(Step ?? 1);
        var text = value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        var sign = Plus && value >= 0 ? "+" : "";
        return sign + text + Unit;
    }

    public string RangeText()
    {
        if (Fixed)
        {
            return "固定";
        }
        return IsList ? $"{Format(Lowest)} … {Format(Highest)} / {Values!.Length} 段"
                      : $"{Format(Lowest)} … {Format(Highest)}";
    }

    private static int DecimalsOf(double value)
    {
        var text = Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
        var dot = text.IndexOf('.');
        return dot < 0 ? 0 : text.Length - dot - 1;
    }

    private static int Decimals(double step) => DecimalsOf(step);
}
