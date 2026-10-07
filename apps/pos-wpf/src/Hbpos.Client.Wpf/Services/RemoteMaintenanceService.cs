using System.IO;
using Hbpos.Client.Wpf.Models;
using Hbpos.RemoteMaintenance.Setup;

namespace Hbpos.Client.Wpf.Services;

public enum RemoteMaintenanceStage
{
    Preparing,
    DownloadingRustDesk,
    DownloadingStatusAgent,
    DownloadedInstalling,
    Registering,
    Configuring
}

public sealed record RemoteMaintenanceProvisionResult(
    bool Succeeded,
    // 返回稳定的资源键，由界面按当前语言渲染；语言切换时无需重跑安装。
    string Message,
    RemoteMaintenanceStatus Status);

public interface IRemoteMaintenanceService
{
    Task<RemoteMaintenanceStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<RemoteMaintenanceProvisionResult> InstallAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default,
        IProgress<RemoteMaintenanceStage>? progress = null);
}

/// <summary>
/// WPF 仅调用这个业务服务；HTTP、下载、DPAPI journal 和安装事务均不进入 ViewModel。
/// </summary>
public sealed class RemoteMaintenanceService(
    IRemoteMaintenanceApiClient apiClient,
    IRemoteMaintenanceArtifactDownloader artifactDownloader,
    IRemoteMaintenanceInstaller installer,
    IRemoteMaintenanceUacHelperLauncher uacHelperLauncher,
    RemoteMaintenanceJournal journal,
    IRemoteMaintenanceSecretProtector secretProtector) : IRemoteMaintenanceService
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly string _helperPath = Path.Combine(AppContext.BaseDirectory, "Hbpos.RemoteMaintenance.Setup.exe");
    private readonly string _journalPath = journal.FilePath;

    public Task<RemoteMaintenanceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        installer.GetStatusAsync(cancellationToken);

    public async Task<RemoteMaintenanceProvisionResult> InstallAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default,
        IProgress<RemoteMaintenanceStage>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(session.StoreCode) || string.IsNullOrWhiteSpace(session.DeviceCode))
        {
            return new RemoteMaintenanceProvisionResult(
                false,
                "settings.remoteMaintenance.result.activationRequired",
                await GetStatusAsync(cancellationToken));
        }

        await _operationGate.WaitAsync(cancellationToken);
        RemoteMaintenanceInstallationResult? installation = null;
        var operationId = Guid.Empty;
        var stage = RemoteMaintenanceStage.Preparing;
        void ReportStage(RemoteMaintenanceStage value)
        {
            stage = value;
            progress?.Report(value);
        }
        try
        {
            ReportStage(RemoteMaintenanceStage.Preparing);
            var existing = await journal.ReadAsync(cancellationToken);
            // “补登记 / 恢复状态服务”捷径的前提是本机 RustDesk 仍在。RustDesk 被卸载后
            // （服务不存在）只重跑 configure 会因启动不存在的服务而永远失败，必须完整重装。
            // 重装沿用原操作号和密码：中心对同一操作号的 prepare/commit 幂等，换新操作号
            // 会被“设备已有已提交的操作”拒绝。状态读取失败时保持原捷径，不贸然重装。
            var reinstallRegisteredOperation =
                existing?.State is RemoteMaintenanceOperationState.InstalledPendingCommit or RemoteMaintenanceOperationState.Committed &&
                (await GetSafeStatusAsync()).ServiceStatus == "notInstalled";
            if (!reinstallRegisteredOperation && existing?.State == RemoteMaintenanceOperationState.InstalledPendingCommit)
            {
                return await ResumePendingCommitAsync(existing, cancellationToken, ReportStage);
            }

            if (!reinstallRegisteredOperation && existing?.State == RemoteMaintenanceOperationState.Committed)
            {
                ReportStage(RemoteMaintenanceStage.Configuring);
                var existingConfigureCode = await uacHelperLauncher.RunAsync(
                    _helperPath,
                    _journalPath,
                    existing.OperationId,
                    "configure",
                    cancellationToken);
                return new RemoteMaintenanceProvisionResult(
                    existingConfigureCode == 0,
                    existingConfigureCode == 0 ? "settings.remoteMaintenance.result.serviceRestored" : "settings.remoteMaintenance.result.serviceRestoreFailed",
                    await GetStatusAsync(cancellationToken));
            }

            // 先核验正式安装目录和随 POS 发布的组件，避免下载完成后才发现无法安装。
            // 本机 RustDesk 仍在的恢复流程已在上方按原 journal 返回，不重新下载安装包。
            uacHelperLauncher.ValidateInstallation(_helperPath);

            operationId = existing?.State is RemoteMaintenanceOperationState.Prepared or
                RemoteMaintenanceOperationState.InstalledPendingCommit or RemoteMaintenanceOperationState.Committed
                ? existing.OperationId
                : Guid.NewGuid();
            var password = existing is null
                ? RemoteMaintenancePasswordGenerator.Create()
                : secretProtector.Unprotect(existing.ProtectedPassword);
            if (string.IsNullOrWhiteSpace(password))
            {
                // Prepared 记录代表同一项已向中心登记的操作；无法解密原密码时
                // 不能生成新密码/新 operationId，否则会留下中心孤儿操作。
                return new RemoteMaintenanceProvisionResult(
                    false,
                    "settings.remoteMaintenance.result.journalDecryptFailed",
                    await GetSafeStatusAsync());
            }

            var prepare = await apiClient.PrepareAsync(
                new RemoteMaintenancePrepareRequest(operationId, Environment.MachineName),
                cancellationToken);
            if (prepare.OperationId != operationId)
            {
                throw new RemoteMaintenanceApiException("远程维护操作标识不一致。", 409, "REMOTE_MAINTENANCE_OPERATION_CONFLICT");
            }

            await journal.WriteAsync(
                new RemoteMaintenanceJournalState(
                    operationId,
                    prepare.DeviceId,
                    RemoteMaintenanceOperationState.Prepared,
                    string.Empty,
                    prepare.ArtifactManifest.RustDesk.Version,
                    secretProtector.Protect(password),
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    prepare.Config,
                    null,
                    null,
                    null,
                    prepare.ArtifactManifest),
                cancellationToken);

            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HBPOS",
                "RemoteMaintenance",
                operationId.ToString("N"));
            ReportStage(RemoteMaintenanceStage.DownloadingRustDesk);
            var rustDeskPath = await artifactDownloader.DownloadAndVerifyAsync(
                prepare.ArtifactManifest.RustDesk,
                dataDirectory,
                cancellationToken);
            ReportStage(RemoteMaintenanceStage.DownloadingStatusAgent);
            var agentPath = await artifactDownloader.DownloadAndVerifyAsync(
                prepare.ArtifactManifest.StatusAgent,
                dataDirectory,
                cancellationToken);

            await journal.WriteAsync(
                new RemoteMaintenanceJournalState(
                    operationId,
                    prepare.DeviceId,
                    RemoteMaintenanceOperationState.Prepared,
                    string.Empty,
                    prepare.ArtifactManifest.RustDesk.Version,
                    secretProtector.Protect(password),
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    prepare.Config,
                    rustDeskPath,
                    agentPath,
                    dataDirectory,
                    prepare.ArtifactManifest),
                cancellationToken);

            // helper 会把实际触及的服务写回 journal；预检查拒绝时两个标志保持 false，
            // 部分安装失败时则由 helper 在第一次修改前写入对应的清理标志。
            installation = new RemoteMaintenanceInstallationResult(false, false, string.Empty, prepare.ArtifactManifest.RustDesk.Version, dataDirectory);
            // 两个 EXE 均通过大小与哈希校验后才提示下载完成；随后进入 Windows 授权与安装。
            ReportStage(RemoteMaintenanceStage.DownloadedInstalling);
            var helperCode = await uacHelperLauncher.RunAsync(_helperPath, _journalPath, operationId, "install", cancellationToken);
            if (helperCode != 0)
            {
                // 提权后二次检查与分步安装失败都使用固定退出码；不把异常原文或敏感参数带入界面。
                if (Enum.IsDefined((RemoteMaintenanceSetupError)helperCode))
                    throw new RemoteMaintenanceSetupException((RemoteMaintenanceSetupError)helperCode);
                throw new InvalidOperationException("远程维护 UAC 安装未完成。");
            }
            // helper 以管理员身份运行，直接由它读取 RustDesk ID 并回写 journal；普通用户
            // 不再依赖 RustDesk IPC/配置权限，也不会因 runas 切换 profile 而读错位置。
            var installedState = await journal.ReadAsync(cancellationToken);
            var rustdeskId = installedState?.RustdeskId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rustdeskId))
            {
                throw new InvalidOperationException("RustDesk ID 读取失败。");
            }
            installation = new RemoteMaintenanceInstallationResult(true, true, rustdeskId, prepare.ArtifactManifest.RustDesk.Version, dataDirectory);
            await journal.WriteAsync(
                new RemoteMaintenanceJournalState(
                    operationId,
                    prepare.DeviceId,
                    RemoteMaintenanceOperationState.InstalledPendingCommit,
                    rustdeskId,
                    prepare.ArtifactManifest.RustDesk.Version,
                    secretProtector.Protect(password),
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    prepare.Config,
                    rustDeskPath,
                    agentPath,
                    dataDirectory,
                    prepare.ArtifactManifest,
                    true,
                    true),
                cancellationToken);

            ReportStage(RemoteMaintenanceStage.Registering);
            var commit = await apiClient.CommitAsync(
                new RemoteMaintenanceCommitRequest(
                    operationId,
                    rustdeskId,
                    prepare.ArtifactManifest.RustDesk.Version,
                    password),
                cancellationToken);
            await journal.WriteAsync(
                new RemoteMaintenanceJournalState(
                    operationId,
                    prepare.DeviceId,
                    RemoteMaintenanceOperationState.Committed,
                    rustdeskId,
                    prepare.ArtifactManifest.RustDesk.Version,
                    secretProtector.Protect(password),
                    secretProtector.Protect(commit.MonitorToken),
                    commit.HeartbeatUrl,
                    DateTimeOffset.UtcNow,
                    prepare.Config,
                    rustDeskPath,
                    agentPath,
                    dataDirectory,
                    prepare.ArtifactManifest,
                    true,
                    true),
                cancellationToken);
            ReportStage(RemoteMaintenanceStage.Configuring);
            var configureCode = await uacHelperLauncher.RunAsync(_helperPath, _journalPath, operationId, "configure", cancellationToken);
            if (configureCode != 0)
            {
                throw new InvalidOperationException("远程维护状态服务配置未完成。");
            }
            return new RemoteMaintenanceProvisionResult(
                true,
                "settings.remoteMaintenance.result.configured",
                new RemoteMaintenanceStatus(true, rustdeskId, prepare.ArtifactManifest.RustDesk.Version, "running"));
        }
        catch (OperationCanceledException)
        {
            if (installation is not null && operationId != Guid.Empty)
            {
                try { await uacHelperLauncher.RunAsync(_helperPath, _journalPath, operationId, "fail-closed", CancellationToken.None); }
                catch { /* 取消也不能把未提交的无人值守服务静默留下，helper 失败由下一次恢复读取 journal。 */ }
            }
            // HTTP 超时不等于用户取消；登记超时仍先停用本次已安装的服务，再提示恢复。
            if (!cancellationToken.IsCancellationRequested &&
                stage is RemoteMaintenanceStage.Preparing or RemoteMaintenanceStage.DownloadingRustDesk or
                    RemoteMaintenanceStage.DownloadingStatusAgent or RemoteMaintenanceStage.Registering)
            {
                ConsoleLog.WriteWarning(
                    "RemoteMaintenance",
                    $"remote maintenance provisioning timed out stage={stage} operationId={operationId}",
                    new ApplicationLogContext(
                        TraceId: operationId == Guid.Empty ? null : operationId.ToString("D"),
                        Properties: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["phase"] = stage.ToString(),
                            ["reason"] = "timeout"
                        }));
                return new RemoteMaintenanceProvisionResult(false,
                    stage == RemoteMaintenanceStage.Preparing
                        ? "settings.remoteMaintenance.result.preparationTimedOut"
                        : FailureMessage(stage), await GetSafeStatusAsync());
            }
            throw;
        }
        catch (Exception ex)
        {
            LogProvisioningFailure(stage, operationId, ex);
            if (installation is not null)
            {
                try
                {
                    await uacHelperLauncher.RunAsync(_helperPath, _journalPath, operationId, "fail-closed", CancellationToken.None);
                }
                catch
                {
                    // fail closed 尽力执行；保留本地 journal 让后续人工重试可见。
                }
            }

            // 状态提示只给用户可理解的固定文案；异常原文可能包含服务器细节或路径。
            return new RemoteMaintenanceProvisionResult(
                false,
                ex switch
                {
                    RemoteMaintenanceSetupException { Error: RemoteMaintenanceSetupError.InstallationLocationInvalid } => "settings.remoteMaintenance.result.installationLocationInvalid",
                    RemoteMaintenanceSetupException { Error: RemoteMaintenanceSetupError.ComponentsMissing } => "settings.remoteMaintenance.result.componentsMissing",
                    RemoteMaintenanceSetupException { Error: RemoteMaintenanceSetupError.InstallationPermissionsInvalid } => "settings.remoteMaintenance.result.installationPermissionsInvalid",
                    RemoteMaintenanceSetupException { Error: RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged } => "settings.remoteMaintenance.result.existingRustDeskUnmanaged",
                    RemoteMaintenanceSetupException { Error: RemoteMaintenanceSetupError.RustDeskSetupIncomplete } => "settings.remoteMaintenance.result.rustDeskSetupIncomplete",
                    RemoteMaintenanceSetupException { Error: RemoteMaintenanceSetupError.RustDeskConfigurationFailed } => "settings.remoteMaintenance.result.rustDeskConfigurationFailed",
                    // 先按已知错误码区分服务端问题；中心鉴权失败不能引导用户重新激活 POS。
                    // 仅映射固定资源键，未知错误仍保留阶段提示，避免透传服务器原文。
                    RemoteMaintenanceApiException { Code: "REMOTE_MAINTENANCE_DISABLED" } => "settings.remoteMaintenance.result.serverDisabled",
                    RemoteMaintenanceApiException { Code: "REMOTE_MAINTENANCE_NOT_READY" } => "settings.remoteMaintenance.result.serverNotReady",
                    RemoteMaintenanceApiException { Code: "REMOTE_MAINTENANCE_GATEWAY_INVALID" } => "settings.remoteMaintenance.result.serverConfigurationInvalid",
                    RemoteMaintenanceApiException { Code: "REMOTE_MAINTENANCE_INTERNAL_AUTH_REQUIRED" } => "settings.remoteMaintenance.result.serverAuthorizationFailed",
                    RemoteMaintenanceApiException { Code: "REMOTE_MAINTENANCE_GATEWAY_UNAVAILABLE" } => "settings.remoteMaintenance.result.serverUnavailable",
                    RemoteMaintenanceApiException { StatusCode: 401 or 403 } => "settings.remoteMaintenance.result.authorizationExpired",
                    RemoteMaintenanceApiException { StatusCode: 404 or 405 } when stage == RemoteMaintenanceStage.Preparing => "settings.remoteMaintenance.result.endpointUnavailable",
                    System.Net.Http.HttpRequestException when stage == RemoteMaintenanceStage.Preparing => "settings.remoteMaintenance.result.connectionFailed",
                    System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } => "settings.remoteMaintenance.result.uacCanceled",
                    FileNotFoundException when stage is RemoteMaintenanceStage.DownloadedInstalling or RemoteMaintenanceStage.Configuring => "settings.remoteMaintenance.result.componentsMissing",
                    InvalidDataException when stage is RemoteMaintenanceStage.DownloadingRustDesk or RemoteMaintenanceStage.DownloadingStatusAgent => "settings.remoteMaintenance.result.downloadVerificationFailed",
                    _ => FailureMessage(stage)
                },
                await GetSafeStatusAsync());
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// 界面只显示固定文案，这里把真实原因（异常类型、HTTP 状态、服务端 code、Setup 错误枚举、阶段）写进中心日志。
    /// 用户在 UAC 弹窗点"否"（Win32 1223）属于主动取消，只记 Information。
    /// </summary>
    private static void LogProvisioningFailure(RemoteMaintenanceStage stage, Guid operationId, Exception exception)
    {
        int? statusCode = exception switch
        {
            RemoteMaintenanceApiException apiException => apiException.StatusCode,
            System.Net.Http.HttpRequestException { StatusCode: { } status } => (int)status,
            _ => null
        };
        var code = exception switch
        {
            RemoteMaintenanceApiException apiException => apiException.Code,
            RemoteMaintenanceSetupException setupException => setupException.Error.ToString(),
            System.ComponentModel.Win32Exception win32Exception => $"WIN32_{win32Exception.NativeErrorCode}",
            _ => null
        };
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["phase"] = stage.ToString()
        };
        if (!string.IsNullOrWhiteSpace(code))
        {
            properties["errorCode"] = code;
        }

        var context = new ApplicationLogContext(
            TraceId: operationId == Guid.Empty ? null : operationId.ToString("D"),
            StatusCode: statusCode,
            Properties: properties);
        var message =
            $"remote maintenance provisioning failed stage={stage} error={exception.GetType().Name} status={statusCode?.ToString() ?? "-"} code={code ?? "-"} operationId={operationId}";
        if (exception is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 })
        {
            ConsoleLog.WriteInformation("RemoteMaintenance", message + " reason=uac-canceled", context);
            return;
        }

        ConsoleLog.WriteWarning("RemoteMaintenance", message, context, exception);
    }

    private async Task<RemoteMaintenanceProvisionResult> ResumePendingCommitAsync(
        RemoteMaintenanceJournalState state,
        CancellationToken cancellationToken,
        Action<RemoteMaintenanceStage> reportStage)
    {
        if (state.Config is null || state.ArtifactManifest is null ||
            state.RustDeskArtifactPath is null || state.StatusAgentArtifactPath is null ||
            string.IsNullOrWhiteSpace(state.RustdeskId) || string.IsNullOrWhiteSpace(state.ClientVersion))
        {
            return new RemoteMaintenanceProvisionResult(false, "settings.remoteMaintenance.result.journalIncomplete", await GetSafeStatusAsync());
        }

        var password = secretProtector.Unprotect(state.ProtectedPassword);
        if (string.IsNullOrWhiteSpace(password))
        {
            return new RemoteMaintenanceProvisionResult(false, "settings.remoteMaintenance.result.journalReinstallRequired", await GetSafeStatusAsync());
        }

        reportStage(RemoteMaintenanceStage.Registering);
        var commit = await apiClient.CommitAsync(
            new RemoteMaintenanceCommitRequest(state.OperationId, state.RustdeskId, state.ClientVersion, password),
            cancellationToken);
        await journal.WriteAsync(state with
        {
            State = RemoteMaintenanceOperationState.Committed,
            ProtectedMonitorToken = secretProtector.Protect(commit.MonitorToken),
            HeartbeatUrl = commit.HeartbeatUrl,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
        reportStage(RemoteMaintenanceStage.Configuring);
        var helperCode = await uacHelperLauncher.RunAsync(_helperPath, _journalPath, state.OperationId, "configure", cancellationToken);
        return new RemoteMaintenanceProvisionResult(
            helperCode == 0,
            helperCode == 0 ? "settings.remoteMaintenance.result.restored" : "settings.remoteMaintenance.result.restorePending",
            await GetSafeStatusAsync());
    }

    private static string FailureMessage(RemoteMaintenanceStage stage) => stage switch
    {
        RemoteMaintenanceStage.Preparing => "settings.remoteMaintenance.result.preparationFailed",
        RemoteMaintenanceStage.DownloadingRustDesk or RemoteMaintenanceStage.DownloadingStatusAgent => "settings.remoteMaintenance.result.downloadFailed",
        RemoteMaintenanceStage.DownloadedInstalling => "settings.remoteMaintenance.result.installationFailed",
        RemoteMaintenanceStage.Registering => "settings.remoteMaintenance.result.registrationFailed",
        _ => "settings.remoteMaintenance.result.serviceRestoreFailed"
    };

    private async Task<RemoteMaintenanceStatus> GetSafeStatusAsync()
    {
        try { return await GetStatusAsync(CancellationToken.None); }
        catch { return new RemoteMaintenanceStatus(false, string.Empty, string.Empty, "checkFailed", "无法读取本机服务状态。"); }
    }
}
