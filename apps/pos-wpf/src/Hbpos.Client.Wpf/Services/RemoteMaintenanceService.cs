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

/// <summary>启动对账的结果，只用于日志与测试；界面不展示。</summary>
public enum RemoteMaintenanceReconcileResult
{
    /// <summary>本机没有已提交的受管 RustDesk（未安装、已卸载或未完成登记），不在对账范围。</summary>
    NotApplicable,
    /// <summary>本机配置与中心期望一致，无需任何操作。</summary>
    UpToDate,
    /// <summary>已通过免 UAC 代理或提权 helper 把 RustDesk 切换到中心期望的服务器。</summary>
    Reconciled,
    /// <summary>暂不处理：正在安装、中心不可达、自动重试次数已用完等，之后会再试或由手动触发。</summary>
    Deferred,
    /// <summary>需要对账但未完成：代理未受理、UAC 被拒、等待超时或 helper 失败。设备仍在旧服务器，可手动修复。</summary>
    Failed,
    /// <summary>中心给出的配置不在公司白名单或要求更换公钥，设备端拒绝执行；需要运维修正中心配置。</summary>
    Rejected
}

/// <summary>
/// 启动对账：已安装的 RustDesk 以中心当前下发的服务器为准。中心把服务器从旧域名切到专用域名后，
/// 已升级的设备在下次启动时自动收敛一次；一致后每次都是空操作，回滚时中心改回旧值即可原路改回。
/// </summary>
public interface IRemoteMaintenanceReconciler
{
    Task<RemoteMaintenanceReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default);
}

/// <summary>夜间升级 RustDesk 的结果，只用于日志与测试；界面不展示。</summary>
public enum RemoteMaintenanceUpgradeResult
{
    /// <summary>没有待升级的构建、RustDesk 未受管或已卸载。</summary>
    NotApplicable,
    /// <summary>已通过 SYSTEM 代理升级到待升级构建。</summary>
    Upgraded,
    /// <summary>暂不处理：代理未注册、正在安装或对账、失败次数已达上限。</summary>
    Deferred,
    /// <summary>升级未完成，计入失败次数，下个窗口再试。</summary>
    Failed
}

/// <summary>
/// 把白天预先下载好的 RustDesk 构建交给 SYSTEM 代理升级。只在免 UAC 代理可用时执行，绝不为升级弹 UAC。
/// </summary>
public interface IRemoteMaintenanceUpgrader
{
    Task<RemoteMaintenanceUpgradeResult> ApplyPendingUpgradeAsync(CancellationToken cancellationToken = default);
}

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
    IRemoteMaintenanceSecretProtector secretProtector,
    // 免 UAC 代理（SYSTEM 计划任务）；为 null 或任务未注册时对账退回 UAC helper。
    IRemoteMaintenanceAgentClient? agentClient = null) : IRemoteMaintenanceService, IRemoteMaintenanceReconciler, IRemoteMaintenanceUpgrader
{
    // 自动对账连续失败达到该次数后不再自动弹 UAC，用户可在设置页点“修复”手动触发。
    // 走免 UAC 代理时不弹窗，不受此上限约束。
    internal const int MaxAutomaticReconcileFailures = 5;

    // 同一个构建连续升级失败达到该次数后不再自动升级（RustDesk 安装脚本不可回滚，反复重装只会反复断开远程连接）。
    internal const int MaxAutomaticUpgradeFailures = 3;

    // 提权 helper 的最长等待：UAC 无人理会时不能一直占着操作闸门（安装/修复也共用它）。
    // 超时只是不再等待，已弹出的 UAC 与 helper 本身不受影响，下次对账会按实际状态收敛。
    internal TimeSpan ReconcileHelperTimeout { get; init; } = TimeSpan.FromMinutes(5);

    // 升级等待上限：复制校验安装包 + RustDesk 安装脚本最长 5 分钟 + 读回配置；与代理任务的 15 分钟执行时限一致。
    internal TimeSpan UpgradeHelperTimeout { get; init; } = TimeSpan.FromMinutes(15);

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
                // 手动修复同样先对账服务器；失败不阻断下面的状态服务恢复（两者互相独立）。
                var reconcile = RemoteMaintenanceReconcileResult.Deferred;
                try { reconcile = await ReconcileCoreAsync(existing, manual: true, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogReconcile(existing.OperationId, $"reconcile aborted error={ex.GetType().Name}", warning: true);
                }

                // 对账成功后 helper 已更新 journal，重新读取避免用旧快照。
                existing = await journal.ReadAsync(cancellationToken) ?? existing;
                // 这一轮刚把服务器改好（已弹过一次 UAC），且 RustDesk 与状态服务都在运行：修复已经完成，
                // 不再为 configure 弹第二次 UAC。其余情况（对账无事可做、失败、状态服务没跑）仍照旧 configure。
                if (reconcile == RemoteMaintenanceReconcileResult.Reconciled)
                {
                    var afterReconcile = await GetSafeStatusAsync();
                    if (afterReconcile.ServiceStatus == "running" && afterReconcile.StatusAgentStatus == "running")
                    {
                        return new RemoteMaintenanceProvisionResult(true, "settings.remoteMaintenance.result.serviceRestored", afterReconcile);
                    }
                }

                var existingConfigureCode = await uacHelperLauncher.RunAsync(
                    _helperPath,
                    _journalPath,
                    existing.OperationId,
                    "configure",
                    cancellationToken);
                return new RemoteMaintenanceProvisionResult(
                    existingConfigureCode == 0,
                    existingConfigureCode != 0
                        ? "settings.remoteMaintenance.result.serviceRestoreFailed"
                        // 状态服务已恢复但服务器地址没改成：如实告知，不能让用户以为一切就绪。
                        : reconcile == RemoteMaintenanceReconcileResult.Failed
                            ? "settings.remoteMaintenance.result.serviceRestoredServerPending"
                            : "settings.remoteMaintenance.result.serviceRestored",
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

    public async Task<RemoteMaintenanceReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        // 安装/恢复正在进行时让路，不排队等待；下次启动或手动修复再对账。
        if (!await _operationGate.WaitAsync(0, cancellationToken)) return RemoteMaintenanceReconcileResult.Deferred;
        try
        {
            return await ReconcileCoreAsync(await journal.ReadAsync(cancellationToken), manual: false, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>调用方必须已持有 <see cref="_operationGate"/>。</summary>
    private async Task<RemoteMaintenanceReconcileResult> ReconcileCoreAsync(
        RemoteMaintenanceJournalState? state, bool manual, CancellationToken cancellationToken)
    {
        if (state is not { State: RemoteMaintenanceOperationState.Committed, Config: { } current })
        {
            return RemoteMaintenanceReconcileResult.NotApplicable;
        }

        // RustDesk 已被卸载时走完整重装（它会按中心当前配置安装），这里不处理。
        var local = await GetSafeStatusAsync();
        if (local.ServiceStatus is "notInstalled" or "checkFailed")
        {
            return RemoteMaintenanceReconcileResult.NotApplicable;
        }

        // 同一 operationId 的 prepare 对已提交设备是幂等的，只用来读取中心当前期望的服务器配置。
        RemoteMaintenancePrepareResponse prepare;
        try
        {
            prepare = await apiClient.PrepareAsync(new RemoteMaintenancePrepareRequest(state.OperationId, Environment.MachineName), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 离线、中心维护、功能关闭或票据失效都属正常情况，静默等下次，不计入失败次数。
            var detail = ex is RemoteMaintenanceApiException api ? $" status={api.StatusCode} code={api.Code ?? "-"}" : string.Empty;
            LogReconcile(state.OperationId, $"reconcile deferred reason=prepare-failed error={ex.GetType().Name}{detail}", warning: false);
            return RemoteMaintenanceReconcileResult.Deferred;
        }

        if (prepare.OperationId != state.OperationId || prepare.DeviceId != state.DeviceId)
        {
            LogReconcile(state.OperationId, "reconcile deferred reason=prepare-mismatch", warning: true);
            return RemoteMaintenanceReconcileResult.Deferred;
        }

        var desired = prepare.Config;
        // 中心给出的配置必须在公司白名单内，且公钥不变；否则宁可不动，也不把设备指向未知服务器。
        if (!RemoteMaintenanceServerPolicy.IsTrusted(desired) ||
            !string.Equals(desired.PublicKey, current.PublicKey, StringComparison.Ordinal))
        {
            LogReconcile(state.OperationId, "reconcile rejected reason=untrusted-desired-config", warning: true);
            return RemoteMaintenanceReconcileResult.Rejected;
        }

        // 中心若已下发新的 RustDesk 构建，趁现在有登录票据先下载校验好，夜间空闲时再交给代理升级；
        // 下载失败不影响对账。staging 会改 journal，之后的写入要基于最新内容。
        await StageRustDeskUpgradeAsync(state, prepare.ArtifactManifest.RustDesk, cancellationToken);
        state = await journal.ReadAsync(cancellationToken) ?? state;

        if (desired == current)
        {
            // 清掉上次失败遗留的待应用配置与失败计数，保持 journal 干净；计数也因此不会把设备永久锁死。
            if (state.PendingConfig is not null || state.ReconcileFailureCount != 0)
            {
                await journal.WriteAsync(
                    state with { PendingConfig = null, ReconcileFailureCount = 0, UpdatedAtUtc = DateTimeOffset.UtcNow },
                    cancellationToken);
            }

            return RemoteMaintenanceReconcileResult.UpToDate;
        }

        // 代理可用就不弹窗，不需要自动重试上限；只有要退回 UAC 时才检查：达到上限后不再自动弹 UAC，手动修复仍可再试。
        var useAgent = agentClient?.IsAvailable(_helperPath) == true;
        if (!useAgent && !manual && state.ReconcileFailureCount >= MaxAutomaticReconcileFailures)
        {
            return RemoteMaintenanceReconcileResult.Deferred;
        }

        // PendingConfig 只是“请求”，真正是否应用由提权侧再次校验后决定（UAC helper 从 journal 读取它）。
        await journal.WriteAsync(state with { PendingConfig = desired, UpdatedAtUtc = DateTimeOffset.UtcNow }, cancellationToken);
        var failureReason = "helper-failed";
        var helperCode = -1;
        using var helperTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        helperTimeout.CancelAfter(ReconcileHelperTimeout);
        try
        {
            helperCode = useAgent
                // 代理只接收目标服务器，不读写本 journal；成功后由下面这里更新 journal。
                ? await agentClient!.ReconcileAsync(_helperPath, desired, helperTimeout.Token)
                : await uacHelperLauncher.RunAsync(_helperPath, _journalPath, state.OperationId, "reconcile", helperTimeout.Token);
            if (helperCode == RemoteMaintenanceAgentClient.NotRespondingExitCode) failureReason = "agent-not-responding";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            failureReason = "helper-timeout";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            failureReason = "uac-declined";
        }
        catch (Exception ex)
        {
            failureReason = $"helper-error:{ex.GetType().Name}";
        }

        if (helperCode == 0)
        {
            // UAC helper 已自行把 PendingConfig 并入 Config；代理不碰 journal，由这里补写。两条路径写入同样内容，重复写无害。
            var applied = await journal.ReadAsync(CancellationToken.None) ?? state;
            await journal.WriteAsync(
                applied with { Config = desired, PendingConfig = null, ReconcileFailureCount = 0, UpdatedAtUtc = DateTimeOffset.UtcNow },
                CancellationToken.None);
            LogReconcile(state.OperationId, $"reconcile applied via={(useAgent ? "agent" : "uac")} idServer={desired.IdServer}", warning: false);
            return RemoteMaintenanceReconcileResult.Reconciled;
        }

        // helper 可能已改过 journal，失败计数必须基于最新内容累加。
        var latest = await journal.ReadAsync(CancellationToken.None) ?? state;
        await journal.WriteAsync(
            latest with { ReconcileFailureCount = latest.ReconcileFailureCount + 1, UpdatedAtUtc = DateTimeOffset.UtcNow },
            CancellationToken.None);
        LogReconcile(
            state.OperationId,
            $"reconcile failed via={(useAgent ? "agent" : "uac")} reason={failureReason} exitCode={helperCode} failures={latest.ReconcileFailureCount + 1}",
            warning: failureReason != "uac-declined");
        return RemoteMaintenanceReconcileResult.Failed;
    }

    /// <summary>
    /// 夜间空闲窗口调用：把白天预先下载好的构建交给 SYSTEM 代理升级。只走代理，不弹 UAC；
    /// 安装 / 对账正在进行时让路，同一构建连续失败达到上限后停止自动升级。
    /// </summary>
    public async Task<RemoteMaintenanceUpgradeResult> ApplyPendingUpgradeAsync(CancellationToken cancellationToken = default)
    {
        if (!await _operationGate.WaitAsync(0, cancellationToken)) return RemoteMaintenanceUpgradeResult.Deferred;
        try
        {
            var state = await journal.ReadAsync(cancellationToken);
            if (state is not
                {
                    State: RemoteMaintenanceOperationState.Committed,
                    PendingUpgradeVersion: { } version, PendingUpgradeSha256: { } sha256, PendingUpgradeArtifactPath: { } artifactPath
                })
                return RemoteMaintenanceUpgradeResult.NotApplicable;
            // 安装包被清理或白名单已不含该构建（WPF 换了版本）：清掉待升级记录，等白天对账重新下载。
            if (!File.Exists(artifactPath) || !WindowsRemoteMaintenanceInstaller.TryGetTrustedRustDeskBuild(sha256, out _))
            {
                await journal.WriteAsync(ClearPendingUpgrade(state), cancellationToken);
                return RemoteMaintenanceUpgradeResult.NotApplicable;
            }

            if (state.UpgradeFailureCount >= MaxAutomaticUpgradeFailures) return RemoteMaintenanceUpgradeResult.Deferred;
            if (agentClient?.IsAvailable(_helperPath) != true) return RemoteMaintenanceUpgradeResult.Deferred;
            var local = await GetSafeStatusAsync();
            if (local.ServiceStatus is "notInstalled" or "checkFailed") return RemoteMaintenanceUpgradeResult.NotApplicable;

            var helperCode = -1;
            var failureReason = "helper-failed";
            using (var helperTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                helperTimeout.CancelAfter(UpgradeHelperTimeout);
                try
                {
                    helperCode = await agentClient!.UpgradeAsync(_helperPath, sha256, artifactPath, helperTimeout.Token);
                    if (helperCode == RemoteMaintenanceAgentClient.NotRespondingExitCode) failureReason = "agent-not-responding";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    failureReason = "helper-timeout";
                }
            }

            var latest = await journal.ReadAsync(CancellationToken.None) ?? state;
            if (helperCode == 0)
            {
                // ClientVersion 记成新构建，白天对账就不会再为同一构建重复下载。
                await journal.WriteAsync(
                    ClearPendingUpgrade(latest) with { ClientVersion = version, UpgradeFailureCount = 0 },
                    CancellationToken.None);
                TryDeleteFile(artifactPath);
                LogReconcile(state.OperationId, $"rustdesk upgrade applied version={version}", warning: false);
                return RemoteMaintenanceUpgradeResult.Upgraded;
            }

            await journal.WriteAsync(
                latest with { UpgradeFailureCount = latest.UpgradeFailureCount + 1, UpdatedAtUtc = DateTimeOffset.UtcNow },
                CancellationToken.None);
            LogReconcile(
                state.OperationId,
                $"rustdesk upgrade failed version={version} reason={failureReason} exitCode={helperCode} failures={latest.UpgradeFailureCount + 1}",
                warning: true);
            return RemoteMaintenanceUpgradeResult.Failed;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// 白天对账拿到中心 manifest 后调用：中心下发的构建与本机已装的不同、且在随 WPF 发布的白名单内时，先下载校验到本地，
    /// 记进 journal 等夜间升级。只在免 UAC 代理可用时准备（升级绝不弹 UAC）；失败只记日志，不影响对账。调用方必须已持有操作闸门。
    /// </summary>
    private async Task StageRustDeskUpgradeAsync(RemoteMaintenanceJournalState state, RemoteMaintenanceArtifact artifact, CancellationToken cancellationToken)
    {
        try
        {
            if (string.Equals(state.ClientVersion, artifact.Version, StringComparison.Ordinal))
            {
                // 已是中心下发的构建：遗留的待升级记录（例如中心改回旧版）一并清掉。
                if (state.PendingUpgradeSha256 is not null)
                    await journal.WriteAsync(ClearPendingUpgrade(state), cancellationToken);
                return;
            }

            if (!WindowsRemoteMaintenanceInstaller.TryGetTrustedRustDeskBuild(artifact.Sha256, out var build))
            {
                LogReconcile(state.OperationId, $"rustdesk upgrade skipped reason=untrusted-build version={artifact.Version}", warning: true);
                return;
            }

            if (agentClient?.IsAvailable(_helperPath) != true) return;
            var sha256 = artifact.Sha256.Trim().ToLowerInvariant();
            if (string.Equals(state.PendingUpgradeSha256, sha256, StringComparison.Ordinal) &&
                state.PendingUpgradeArtifactPath is { } existing && File.Exists(existing))
                return;

            var directory = Path.Combine(
                state.DataDirectory ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HBPOS", "RemoteMaintenance", state.OperationId.ToString("N")),
                "upgrade-" + sha256[..12]);
            var path = await artifactDownloader.DownloadAndVerifyAsync(artifact, directory, cancellationToken);
            var latest = await journal.ReadAsync(cancellationToken) ?? state;
            var sameBuild = string.Equals(latest.PendingUpgradeSha256, sha256, StringComparison.Ordinal);
            await journal.WriteAsync(latest with
            {
                PendingUpgradeVersion = build.Version,
                PendingUpgradeSha256 = sha256,
                PendingUpgradeArtifactPath = path,
                // 换了新构建就重新给自动升级机会；同一构建重新下载不清零，避免绕过失败上限。
                UpgradeFailureCount = sameBuild ? latest.UpgradeFailureCount : 0,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken);
            LogReconcile(state.OperationId, $"rustdesk upgrade staged version={build.Version}", warning: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogReconcile(state.OperationId, $"rustdesk upgrade staging failed error={ex.GetType().Name}", warning: true);
        }
    }

    private static RemoteMaintenanceJournalState ClearPendingUpgrade(RemoteMaintenanceJournalState state) => state with
    {
        PendingUpgradeVersion = null,
        PendingUpgradeSha256 = null,
        PendingUpgradeArtifactPath = null,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void LogReconcile(Guid operationId, string message, bool warning)
    {
        var context = new ApplicationLogContext(TraceId: operationId.ToString("D"));
        if (warning)
        {
            ConsoleLog.WriteWarning("RemoteMaintenance", message, context);
        }
        else
        {
            ConsoleLog.WriteInformation("RemoteMaintenance", message, context);
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
