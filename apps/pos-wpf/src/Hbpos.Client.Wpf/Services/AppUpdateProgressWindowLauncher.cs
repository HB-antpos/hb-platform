using System.Globalization;
using System.IO;
using System.Text;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Contracts.AppUpdates;

namespace Hbpos.Client.Wpf.Services;

public sealed record AppUpdaterLaunchRequest(
    string InstallerPath,
    string InstallerArguments,
    string AppExePath,
    int WaitProcessId,
    string FromVersion,
    string ToVersion,
    string Culture,
    string? LogPath);

/// <summary>
/// 生成更新窗口（Hbpos.Updater）的命令行；参数名必须与更新窗口的 UpdaterOptions 保持一致。
/// </summary>
public static class AppUpdaterCommandLine
{
    public static string Build(AppUpdaterLaunchRequest request)
    {
        var arguments = new List<string>
        {
            "--installer", request.InstallerPath,
            "--installer-args", request.InstallerArguments,
            "--app-exe", request.AppExePath,
            "--wait-pid", request.WaitProcessId.ToString(CultureInfo.InvariantCulture),
            "--from", request.FromVersion,
            "--to", request.ToVersion,
            "--culture", request.Culture
        };
        if (!string.IsNullOrWhiteSpace(request.LogPath))
        {
            arguments.Add("--log");
            arguments.Add(request.LogPath);
        }

        return string.Join(' ', arguments.Select(QuoteArgument));
    }

    internal static string QuoteArgument(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return value;
        }

        // 按 Windows 命令行规则转义：引号前的反斜杠翻倍再加一个，结尾反斜杠翻倍，保证更新窗口原样取回。
        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashes);
                builder.Append(character);
            }

            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }
}

public sealed record AppUpdateProgressWindowOptions(
    string UpdaterSourceDirectory,
    string StagingRootDirectory,
    string LogDirectory,
    string AppExePath,
    int ProcessId)
{
    public const string UpdaterDirectoryName = "updater";
    public const string UpdaterExecutableName = "Hbpos.Updater.exe";

    public static AppUpdateProgressWindowOptions CreateDefault()
    {
        return new AppUpdateProgressWindowOptions(
            Path.Combine(AppContext.BaseDirectory, UpdaterDirectoryName),
            Path.Combine(Path.GetTempPath(), "HbposUpdater"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hbpos.Client",
                "update-logs"),
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Hbpos.Client.Wpf.exe"),
            Environment.ProcessId);
    }
}

public interface IAppUpdateProgressWindowLauncher
{
    /// <summary>
    /// 把安装交给独立的更新进度窗口；返回 null 表示本机没有可用的更新窗口，调用方应直接启动安装器。
    /// </summary>
    Task<ProcessLaunchResult?> TryLaunchAsync(
        string installerPath,
        string installerArguments,
        AppUpdateCheckResponse update);
}

public sealed class AppUpdateProgressWindowLauncher(
    IProcessLauncher processLauncher,
    IAppVersionProvider versionProvider,
    AppUpdateProgressWindowOptions options,
    TimeProvider? timeProvider = null,
    Func<string>? cultureNameProvider = null) : IAppUpdateProgressWindowLauncher
{
    internal const int RetainedInstallLogCount = 10;
    private const string InstallLogPattern = "install-*.log";
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<string> _cultureNameProvider = cultureNameProvider ??
        (() => LocalizationResourceProvider.Instance.CurrentCulture.Name);

    public async Task<ProcessLaunchResult?> TryLaunchAsync(
        string installerPath,
        string installerArguments,
        AppUpdateCheckResponse update)
    {
        var updaterPath = TryStageUpdater();
        if (updaterPath is null)
        {
            return null;
        }

        var targetVersion = AppVersionProvider.NormalizeVersionText(update.TargetVersion);
        var request = new AppUpdaterLaunchRequest(
            installerPath,
            installerArguments,
            options.AppExePath,
            options.ProcessId,
            versionProvider.CurrentVersion,
            targetVersion,
            _cultureNameProvider(),
            TryPrepareLogPath(targetVersion));
        var result = await processLauncher.StartAsync(updaterPath, AppUpdaterCommandLine.Build(request));
        if (!result.Success)
        {
            ConsoleLog.Write("AppUpdate", $"update progress window launch failed error={result.ErrorMessage ?? "<null>"}");
            return null;
        }

        return result;
    }

    internal string? TryStageUpdater()
    {
        var sourceExe = Path.Combine(options.UpdaterSourceDirectory, AppUpdateProgressWindowOptions.UpdaterExecutableName);
        if (!File.Exists(sourceExe))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(options.StagingRootDirectory);
            DeleteStaleStagingDirectories();
            var now = _timeProvider.GetLocalNow();
            var targetDirectory = Path.Combine(
                options.StagingRootDirectory,
                $"{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
            // 更新窗口必须从临时目录运行，否则安装器替换 Program Files 下的文件时会被它占用。
            CopyDirectory(options.UpdaterSourceDirectory, targetDirectory);
            return Path.Combine(targetDirectory, AppUpdateProgressWindowOptions.UpdaterExecutableName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConsoleLog.Write("AppUpdate", $"update progress window staging failed error={ex.GetType().Name} message={ex.Message}");
            return null;
        }
    }

    internal string? TryPrepareLogPath(string? targetVersion)
    {
        try
        {
            Directory.CreateDirectory(options.LogDirectory);
            // 为本次日志腾出一个名额，只保留最近几次更新的安装日志。
            var staleLogs = Directory.GetFiles(options.LogDirectory, InstallLogPattern)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(RetainedInstallLogCount - 1);
            foreach (var staleLog in staleLogs)
            {
                TryDeleteFile(staleLog);
            }

            var now = _timeProvider.GetLocalNow();
            return Path.Combine(
                options.LogDirectory,
                $"install-{SanitizeFileNamePart(targetVersion)}-{now:yyyyMMdd-HHmmss}.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConsoleLog.Write("AppUpdate", $"update install log preparation failed error={ex.GetType().Name} message={ex.Message}");
            return null;
        }
    }

    private void DeleteStaleStagingDirectories()
    {
        foreach (var directory in Directory.GetDirectories(options.StagingRootDirectory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 上一次的更新窗口可能还开着，删不掉就留到下次。
            }
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            CopyDirectory(directory, Path.Combine(targetDirectory, Path.GetFileName(directory)));
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string SanitizeFileNamePart(string? value)
    {
        var sanitized = new string((value ?? string.Empty)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')
            .ToArray());
        return sanitized.Length == 0 ? "unknown" : sanitized;
    }
}
