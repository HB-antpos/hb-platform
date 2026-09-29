using System.IO;

namespace Hbpos.Client.Wpf.Services;

public sealed record AppLaunchVersionNotice(string Version, bool IsRollback);

/// <summary>
/// 记录本机上次启动的版本，用于启动页提示"已更新到 X"；首次启动和本地开发版不提示。
/// </summary>
public sealed class AppLaunchVersionTracker(string markerFilePath)
{
    private const string UnpublishedVersion = "0.0.0";

    public static AppLaunchVersionTracker CreateDefault()
    {
        return new AppLaunchVersionTracker(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hbpos.Client",
            "last-launched-version.txt"));
    }

    public AppLaunchVersionNotice? RecordLaunch(string currentVersion)
    {
        var version = currentVersion.Trim();
        if (version.Length == 0 || string.Equals(version, UnpublishedVersion, StringComparison.Ordinal))
        {
            // 本地调试版与已安装的正式版共用数据目录，不能让它们互相覆盖版本记录。
            return null;
        }

        var previousVersion = TryReadPreviousVersion();
        if (string.Equals(previousVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        TryWriteVersion(version);
        return previousVersion is null
            ? null
            : new AppLaunchVersionNotice(version, IsLowerVersion(version, previousVersion));
    }

    private string? TryReadPreviousVersion()
    {
        try
        {
            if (!File.Exists(markerFilePath))
            {
                return null;
            }

            var value = File.ReadAllText(markerFilePath).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TryWriteVersion(string version)
    {
        try
        {
            var directory = Path.GetDirectoryName(markerFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(markerFilePath, version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 记录失败只影响下次启动的"已更新"提示，不能挡住启动。
        }
    }

    private static bool IsLowerVersion(string version, string previousVersion)
    {
        return Version.TryParse(version, out var current) &&
            Version.TryParse(previousVersion, out var previous) &&
            current < previous;
    }
}
