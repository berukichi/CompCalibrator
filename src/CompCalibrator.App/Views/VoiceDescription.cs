using CompCalibrator.Analysis;

namespace CompCalibrator.App.Views;

/// <summary>測定値を、声の印象の言葉に直す。目安であって分類の正解ではない。</summary>
internal static class VoiceDescription
{
    public static string Summarize(VoiceMetrics m)
    {
        var parts = new List<string>();
        if (m.HasPitch)
        {
            parts.Add(m.PitchHz switch
            {
                < 110 => "低めの声",
                < 170 => "中低音の声",
                < 240 => "中高音の声",
                _ => "高めの声",
            });
        }

        parts.Add(m.CentroidHz switch
        {
            < 800 => "こもり気味",
            < 1800 => "明るさは標準",
            _ => "明るめ",
        });

        parts.Add(m.Breathiness switch
        {
            < 0.08 => "息の成分は少なめ",
            < 0.2 => "息の成分は標準",
            _ => "息の成分は多め",
        });

        if (m.BoomDb > -6)
        {
            parts.Add("低域が強い(マイクが近い可能性)");
        }
        if (m.SibilanceDb > -14)
        {
            parts.Add("歯擦音が強い");
        }
        return string.Join(" / ", parts);
    }
}
