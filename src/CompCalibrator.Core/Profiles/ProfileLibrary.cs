using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompCalibrator.Profiles;

/// <summary>
/// 同梱のプロファイルと、ユーザーのフォルダーの JSON を読む。
/// 同じ id のファイルをユーザーのフォルダーに置くと、同梱のものを置き換える。
/// </summary>
public static class ProfileLibrary
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly string[] Order =
    [
        "bridge-cast", "ag-mk2", "goxlr", "rodecaster-pro-2", "vocaster",
        "wave-link", "revelator-io24", "obs-studio", "generic",
    ];

    public static DeviceProfile Parse(string json)
    {
        var profile = JsonSerializer.Deserialize<DeviceProfile>(json, Options)
            ?? throw new InvalidDataException("プロファイルが空です。");
        Validate(profile);
        return profile;
    }

    public static IReadOnlyList<DeviceProfile> LoadBuiltIn()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var profiles = new List<DeviceProfile>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith("profiles.", StringComparison.Ordinal))
            {
                continue;
            }
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            profiles.Add(Parse(reader.ReadToEnd()));
        }
        return Sort(profiles);
    }

    /// <summary>読めなかったファイルは飛ばして、理由を <paramref name="errors"/> に積む。</summary>
    public static IReadOnlyList<DeviceProfile> LoadAll(string folder, List<string> errors)
    {
        var profiles = LoadBuiltIn().ToDictionary(p => p.Id);
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    var profile = Parse(File.ReadAllText(file));
                    profiles[profile.Id] = profile;
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
                {
                    errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        return Sort(profiles.Values);
    }

    private static List<DeviceProfile> Sort(IEnumerable<DeviceProfile> profiles) =>
        [.. profiles.OrderBy(p =>
        {
            var index = Array.IndexOf(Order, p.Id);
            return index < 0 ? Order.Length - 1 : index;
        }).ThenBy(p => p.Name, StringComparer.Ordinal)];

    private static void Validate(DeviceProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new InvalidDataException("id と name は必須です。");
        }
        if (profile.Modes.Count == 0)
        {
            throw new InvalidDataException($"{profile.Id}: modes が空です。");
        }
        foreach (var mode in profile.Modes)
        {
            foreach (var parameter in mode.Parameters)
            {
                var where = $"{profile.Id}/{mode.Id}/{parameter.Label}";
                if (parameter.IsList)
                {
                    if (parameter.Labels is { Length: > 0 } && parameter.Labels.Length != parameter.Values!.Length)
                    {
                        throw new InvalidDataException($"{where}: values と labels の数が違います。");
                    }
                    for (var i = 1; i < parameter.Values!.Length; i++)
                    {
                        if (parameter.Values[i] <= parameter.Values[i - 1])
                        {
                            throw new InvalidDataException($"{where}: values は昇順にしてください。");
                        }
                    }
                }
                else if (parameter.Min is null || parameter.Max is null || parameter.Max <= parameter.Min)
                {
                    throw new InvalidDataException($"{where}: min/max か values が必要です。");
                }
            }
        }
    }
}
