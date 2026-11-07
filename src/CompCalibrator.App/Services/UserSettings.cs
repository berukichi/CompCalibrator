using System.Text.Json;

namespace CompCalibrator.App.Services;

/// <summary>次回の起動で選択を復元する。読めなければ既定値で始める。</summary>
public sealed class UserSettings
{
    public string? DeviceId { get; set; }
    public string? ModeId { get; set; }
    public string? InputId { get; set; }
    public string? Style { get; set; }

    public static UserSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(AppPaths.Settings)) ?? new UserSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new UserSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存できなくても測定には影響しない。
        }
    }
}
