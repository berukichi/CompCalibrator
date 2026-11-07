using System.Runtime.InteropServices;
using Windows.Storage;

namespace CompCalibrator.App.Services;

/// <summary>
/// 保存先を決める。MSIX では %LOCALAPPDATA% への書き込みが別の場所へ振り替えられ、
/// エクスプローラーで開けなくなるので、パッケージの LocalState を使う。
/// </summary>
internal static class AppPaths
{
    private const int NoPackage = 15700; // APPMODEL_ERROR_NO_PACKAGE

    public static bool IsPackaged { get; } = DetectPackage();

    public static string Data { get; } = IsPackaged
        ? ApplicationData.Current.LocalFolder.Path
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompCalibrator");

    public static string Profiles { get; } = Path.Combine(Data, "Profiles");

    public static string Settings { get; } = Path.Combine(Data, "settings.json");

    private static bool DetectPackage()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, null) != NoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? name);
}
