using System.Diagnostics;
using System.IO;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.AppUpdates;

namespace Hbpos.Client.Wpf.Services;

public sealed record ProcessLaunchResult(
    bool Success,
    string? ErrorMessage = null,
    string? StatusKey = null,
    object[]? StatusArgs = null)
{
    public static ProcessLaunchResult Succeeded() => new(true);

    public static ProcessLaunchResult Fail(
        string? errorMessage,
        string? statusKey = null,
        params object[] statusArgs) => new(false, errorMessage, statusKey, statusArgs);
}

public interface IProcessLauncher
{
    Task<ProcessLaunchResult> StartAsync(string fileName, string arguments);
}

public sealed class ProcessLauncher : IProcessLauncher
{
    public Task<ProcessLaunchResult> StartAsync(string fileName, string arguments)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true
            });

            if (process is null)
            {
                return Task.FromResult(ProcessLaunchResult.Fail("Process.Start returned null."));
            }

            process.Dispose();
            return Task.FromResult(ProcessLaunchResult.Succeeded());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return Task.FromResult(ProcessLaunchResult.Fail(ex.Message));
        }
    }
}

public interface IAppUpdateInstallSafetyGuard
{
    bool CanInstallUpdate(out string statusKey, out object[] args);
}

public sealed class AllowInstallSafetyGuard : IAppUpdateInstallSafetyGuard
{
    public static AllowInstallSafetyGuard Instance { get; } = new();

    public bool CanInstallUpdate(out string statusKey, out object[] args)
    {
        statusKey = string.Empty;
        args = [];
        return true;
    }
}

public sealed class ShellAppUpdateInstallSafetyGuard(
    PosCartService cart,
    MainViewModel mainViewModel) : IAppUpdateInstallSafetyGuard
{
    public bool CanInstallUpdate(out string statusKey, out object[] args)
    {
        if (!cart.IsEmpty)
        {
            statusKey = "appUpdate.install.activeTransaction";
            args = [];
            return false;
        }

        var payment = mainViewModel.CachedCashPaymentScreen;
        if (payment is not null &&
            (payment.IsCardPaymentInProgress ||
                payment.IsPaymentInteractionLocked ||
                payment.PaymentTenders.Count > 0))
        {
            statusKey = "appUpdate.install.activeTransaction";
            args = [];
            return false;
        }

        statusKey = string.Empty;
        args = [];
        return true;
    }
}

/// <summary>
/// 夜间无人值守安装的方式：更新窗口失败时直接重新打开旧版收银端；AllowDirectInstall 为 false 时（本机只能靠管理员更新代理免 UAC）
/// 绝不直接运行安装器，因为那会弹出没人点的 UAC 确认框，把收银机卡到早上。
/// </summary>
public sealed record UnattendedInstallMode(bool AllowDirectInstall)
{
    public static UnattendedInstallMode AgentOnly { get; } = new(false);

    public static UnattendedInstallMode AnyInstaller { get; } = new(true);
}

public interface IAppUpdateInstallerLauncher
{
    Task<ProcessLaunchResult> LaunchAsync(
        string installerPath,
        AppUpdateCheckResponse update,
        CancellationToken cancellationToken = default);

    Task<ProcessLaunchResult> LaunchUnattendedAsync(
        string installerPath,
        AppUpdateCheckResponse update,
        UnattendedInstallMode mode,
        CancellationToken cancellationToken = default) =>
        LaunchAsync(installerPath, update, cancellationToken);
}

public sealed class AppUpdateInstallerLauncher(
    IProcessLauncher processLauncher,
    IApplicationExitService exitService,
    IAppUpdateInstallSafetyGuard safetyGuard,
    IAppUpdateProgressWindowLauncher? progressWindowLauncher = null) : IAppUpdateInstallerLauncher
{
    public Task<ProcessLaunchResult> LaunchAsync(
        string installerPath,
        AppUpdateCheckResponse update,
        CancellationToken cancellationToken = default) =>
        LaunchCoreAsync(installerPath, update, unattended: null, cancellationToken);

    public Task<ProcessLaunchResult> LaunchUnattendedAsync(
        string installerPath,
        AppUpdateCheckResponse update,
        UnattendedInstallMode mode,
        CancellationToken cancellationToken = default) =>
        LaunchCoreAsync(installerPath, update, mode, cancellationToken);

    private async Task<ProcessLaunchResult> LaunchCoreAsync(
        string installerPath,
        AppUpdateCheckResponse update,
        UnattendedInstallMode? unattended,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!safetyGuard.CanInstallUpdate(out var statusKey, out var statusArgs))
        {
            return ProcessLaunchResult.Fail(
                FormatStatus(statusKey, statusArgs),
                statusKey,
                statusArgs);
        }

        if (!TryResolveInstallerType(installerPath, update, out var installerType, out var installerTypeError))
        {
            return ProcessLaunchResult.Fail(
                installerTypeError,
                "appUpdate.install.failed",
                installerTypeError);
        }

        var configuredArguments = update.InstallerArguments?.Trim() ?? string.Empty;

        // 中文注释：无人值守且只能靠管理员更新代理时，任何“直接运行安装器”的路径都会弹 UAC，一律不走。
        var directInstallAllowed = unattended?.AllowDirectInstall ?? true;

        ProcessLaunchResult launchResult;
        if (installerType.Equals("msi", StringComparison.OrdinalIgnoreCase))
        {
            if (!directInstallAllowed)
            {
                return ProcessLaunchResult.Fail(
                    "MSI packages cannot be installed unattended without user confirmation.",
                    "appUpdate.install.failed",
                    "MSI packages cannot be installed unattended without user confirmation.");
            }

            var arguments = string.IsNullOrWhiteSpace(configuredArguments)
                ? $"/i \"{installerPath}\""
                : $"/i \"{installerPath}\" {configuredArguments}";
            launchResult = await processLauncher.StartAsync("msiexec.exe", arguments);
        }
        else
        {
            // 中文注释：exe 安装包优先交给独立更新窗口接管并显示进度；窗口不可用时退回直接启动安装器，更新不能因此卡住。
            var windowResult = await TryLaunchProgressWindowAsync(installerPath, configuredArguments, update, unattended);
            if (windowResult is null && !directInstallAllowed)
            {
                return ProcessLaunchResult.Fail(
                    "The update window is unavailable and installing without it would show an elevation prompt.",
                    "appUpdate.install.failed",
                    "The update window is unavailable and installing without it would show an elevation prompt.");
            }

            launchResult = windowResult ?? await processLauncher.StartAsync(installerPath, configuredArguments);
        }

        if (!launchResult.Success)
        {
            return launchResult;
        }

        // 中文注释：安装器已交给 Windows 后才退出 WPF，避免启动失败时误关收银端。
        exitService.Exit();
        return launchResult;
    }

    private async Task<ProcessLaunchResult?> TryLaunchProgressWindowAsync(
        string installerPath,
        string installerArguments,
        AppUpdateCheckResponse update,
        UnattendedInstallMode? unattended)
    {
        if (progressWindowLauncher is null)
        {
            return null;
        }

        try
        {
            return await progressWindowLauncher.TryLaunchAsync(installerPath, installerArguments, update, unattended);
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError(
                "AppUpdate",
                $"update progress window failed, falling back to installer error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
            return null;
        }
    }

    private static bool TryResolveInstallerType(
        string installerPath,
        AppUpdateCheckResponse update,
        out string installerType,
        out string errorMessage)
    {
        installerType = Path.GetExtension(installerPath).TrimStart('.').ToLowerInvariant();
        errorMessage = string.Empty;

        if (installerType is not ("exe" or "msi"))
        {
            errorMessage = "Downloaded update package must be an .exe or .msi installer.";
            return false;
        }

        var declaredInstallerType = update.InstallerType?.Trim();
        if (!string.IsNullOrWhiteSpace(declaredInstallerType) &&
            !string.Equals(installerType, declaredInstallerType, StringComparison.OrdinalIgnoreCase))
        {
            // 启动安装器前再次校验类型，防止绕过下载校验后执行错误安装命令。
            errorMessage = "Downloaded installer type does not match the release contract.";
            return false;
        }

        return true;
    }

    private static string FormatStatus(string statusKey, params object[] args)
    {
        var template = LocalizationResourceProvider.Instance[statusKey];
        return args.Length == 0
            ? template
            : string.Format(LocalizationResourceProvider.Instance.CurrentCulture, template, args);
    }
}
