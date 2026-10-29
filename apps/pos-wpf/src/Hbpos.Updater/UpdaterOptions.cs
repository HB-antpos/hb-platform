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
    public const string DownloadUrlOption = "--download-url";
    public const string Sha256Option = "--sha256";
    public const string FileSizeOption = "--file-size";
    public const string UnattendedOption = "--unattended";
    public const string AgentOnlyOption = "--agent-only";

    public required string InstallerPath { get; init; }

    public string InstallerArguments { get; init; } = string.Empty;

    public string? AppExePath { get; init; }

    public int? WaitProcessId { get; init; }

    public string? FromVersion { get; init; }

    public string? ToVersion { get; init; }

    public string Culture { get; init; } = "en";

    public string? LogPath { get; init; }

    // 中文注释：下面三项是发布合同里的下载地址、SHA256 和大小，供管理员更新代理自己重新下载核对；缺任何一项就不走代理。
    public string? DownloadUrl { get; init; }

    public string? Sha256 { get; init; }

    public long? FileSize { get; init; }

    // 中文注释：夜间无人值守安装：失败时不能停在需要人点的失败页，也不能退回会弹 UAC 的直接安装。
    public bool Unattended { get; init; }

    // 中文注释：只能靠管理员更新代理安装：代理不可用、拒绝请求或没响应时直接失败，绝不退回会弹 UAC 的直接运行安装器。
    public bool AgentOnly { get; init; }

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
            LogPath = Normalize(values.GetValueOrDefault(LogOption)),
            DownloadUrl = Normalize(values.GetValueOrDefault(DownloadUrlOption)),
            Sha256 = Normalize(values.GetValueOrDefault(Sha256Option)),
            FileSize = ParseFileSize(values.GetValueOrDefault(FileSizeOption)),
            Unattended = string.Equals(Normalize(values.GetValueOrDefault(UnattendedOption)), "1", StringComparison.Ordinal),
            AgentOnly = string.Equals(Normalize(values.GetValueOrDefault(AgentOnlyOption)), "1", StringComparison.Ordinal)
        };
        return true;
    }

    private static long? ParseFileSize(string? value)
    {
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var fileSize) && fileSize > 0
            ? fileSize
            : null;
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
