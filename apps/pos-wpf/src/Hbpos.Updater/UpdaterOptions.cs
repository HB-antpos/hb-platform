using System.Globalization;

namespace Hbpos.Updater;

/// <summary>
/// 收银端拉起更新窗口时传入的参数；参数名必须与收银端 AppUpdaterCommandLine 保持一致。
/// </summary>
internal sealed record UpdaterOptions
{
    public const string InstallerOption = "--installer";
    public const string InstallerArgumentsOption = "--installer-args";
    public const string AppExeOption = "--app-exe";
    public const string WaitProcessIdOption = "--wait-pid";
    public const string FromVersionOption = "--from";
    public const string ToVersionOption = "--to";
    public const string CultureOption = "--culture";
    public const string LogOption = "--log";

    public required string InstallerPath { get; init; }

    public string InstallerArguments { get; init; } = string.Empty;

    public string? AppExePath { get; init; }

    public int? WaitProcessId { get; init; }

    public string? FromVersion { get; init; }

    public string? ToVersion { get; init; }

    public string Culture { get; init; } = "en";

    public string? LogPath { get; init; }

    public static bool TryParse(IReadOnlyList<string> args, out UpdaterOptions? options)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index + 1 < args.Count; index += 2)
        {
            // 未识别的参数连同取值一起跳过：更新窗口只要拿到安装包就能继续，不能因为多一个参数就放弃更新。
            values[args[index]] = args[index + 1];
        }

        var installerPath = Normalize(values.GetValueOrDefault(InstallerOption));
        if (installerPath is null)
        {
            options = null;
            return false;
        }

        options = new UpdaterOptions
        {
            InstallerPath = installerPath,
            InstallerArguments = values.GetValueOrDefault(InstallerArgumentsOption)?.Trim() ?? string.Empty,
            AppExePath = Normalize(values.GetValueOrDefault(AppExeOption)),
            WaitProcessId = ParseProcessId(values.GetValueOrDefault(WaitProcessIdOption)),
            FromVersion = Normalize(values.GetValueOrDefault(FromVersionOption)),
            ToVersion = Normalize(values.GetValueOrDefault(ToVersionOption)),
            Culture = Normalize(values.GetValueOrDefault(CultureOption)) ?? "en",
            LogPath = Normalize(values.GetValueOrDefault(LogOption))
        };
        return true;
    }

    private static int? ParseProcessId(string? value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) && processId > 0
            ? processId
            : null;
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
