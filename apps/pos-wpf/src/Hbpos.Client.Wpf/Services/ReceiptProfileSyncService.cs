using System.Net;
using Hbpos.Contracts.Stores;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 总部下发的小票资料字段（与 StoreReceiptProfileDto 的资料部分一一对应）：六个资料字段，
/// 外加退款代金券「使用说明」与分期小票「分期条款」的定制正文（null/空白＝未定制，打印走默认文案）。
/// 后两个放在末尾并带默认值，旧调用点（只传六个字段）不受影响。
/// </summary>
public sealed record ReceiptProfileFields(
    string? BrandName,
    string? StoreName,
    string? Address,
    string? Phone,
    string? Abn,
    string? ReturnPolicy,
    string? VoucherTerms = null,
    string? InstallmentTerms = null);

/// <summary>
/// 本机保存的下发状态：Version=已应用的下发版本（0=从未应用过下发），
/// AckedVersion=已成功回执给服务端的版本。
/// </summary>
public sealed record ReceiptProfileLocalState(int Version, int AckedVersion)
{
    public static ReceiptProfileLocalState None { get; } = new(0, 0);

    /// <summary>已应用但尚未回执（上次写入后回执失败，需要补发）。</summary>
    public bool NeedsAck => Version > 0 && Version > AckedVersion;
}

/// <summary>同步服务读写本机下发状态的抽象，由 <see cref="ReceiptPrinterSettingsStore"/> 实现。</summary>
public interface IReceiptProfileLocalStore
{
    /// <summary>读取指定门店的下发状态；本机未绑定该门店（含换店后）一律返回 (0, 0)。</summary>
    Task<ReceiptProfileLocalState> LoadProfileStateAsync(string storeCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// 原子写入总部下发的资料：所有字段（含代金券使用说明与分期条款）、版本、绑定门店在同一次批量写里完成，回执版本清零。
    /// 返回 false 表示设备当前门店已不是 <paramref name="storeCode"/>，未写入任何数据。
    /// </summary>
    Task<bool> ApplyHeadquartersProfileAsync(
        string storeCode,
        ReceiptProfileFields fields,
        int version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 记录「<paramref name="version"/> 已回执」。仅当本机仍绑定该门店且当前版本正是它时才写入，
    /// 否则返回 false（回执期间资料已被替换或换店）。
    /// </summary>
    Task<bool> MarkProfileAckedAsync(
        string storeCode,
        int version,
        CancellationToken cancellationToken = default);
}

public enum ReceiptProfileSyncOutcome
{
    /// <summary>总部有新版本，已校验并写入本机。</summary>
    Applied,

    /// <summary>总部版本与本机一致，无需更新。</summary>
    UpToDate,

    /// <summary>总部没有任何下发版本，本机资料保持不变。</summary>
    NeverReleased,

    /// <summary>响应无效（缺版本/缺资料/串店/校验未通过），未写入任何数据。</summary>
    Discarded,

    /// <summary>网络、服务端或本机读写失败，下一轮重试。</summary>
    Failed,

    /// <summary>服务端尚未部署同步接口（404）。</summary>
    ServerUnsupported,

    /// <summary>设备认证尚未就绪，没有门店可同步。</summary>
    NotReady
}

/// <summary>
/// 一轮同步的结果。<see cref="Detail"/> 只放不含资料内容的原因说明；
/// <see cref="AckPending"/>=true 表示资料已生效但回执还没成功，会在后续轮次自动补发。
/// </summary>
public sealed record ReceiptProfileSyncResult(
    ReceiptProfileSyncOutcome Outcome,
    int Version = 0,
    string? Detail = null,
    bool AckPending = false);

public sealed class ReceiptProfileAppliedEventArgs(string storeCode, int version) : EventArgs
{
    public string StoreCode { get; } = storeCode;

    public int Version { get; } = version;
}

public interface IReceiptProfileSyncService
{
    /// <summary>新版本已写入本机后触发（可能在任意线程），订阅方需自行切回 UI 线程。</summary>
    event EventHandler<ReceiptProfileAppliedEventArgs>? ProfileApplied;

    /// <summary>
    /// 立即同步一轮（设置页「立即同步」）。同一时刻只有一个同步在飞：已有同步进行中时直接复用它的结果。
    /// 不受 404 退避限制，只在取消时抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    Task<ReceiptProfileSyncResult> SyncNowAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 搭 15 秒健康探测节拍调用：设备认证就绪后的第一次立即同步，之后每 4 拍（约 60 秒）一轮，
    /// 换店后立即同步；遇到 404 退避 10 分钟。本拍不需要同步时返回 null。
    /// </summary>
    Task<ReceiptProfileSyncResult?> RunScheduledTickAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 总部下发小票资料的客户端同步：按版本号轻量轮询 → 校验 → 原子写入本机 → 回执。
/// 所有失败只记日志、不弹窗，下一轮重试；离线时继续使用本机最后一次应用的资料；
/// 日志只记版本号和结果，不写资料内容（地址、电话、ABN 等）。
/// </summary>
public sealed class ReceiptProfileSyncService : IReceiptProfileSyncService
{
    /// <summary>每 4 个 15 秒拍同步一轮，约 60 秒。</summary>
    public const int TicksPerRound = 4;

    /// <summary>服务端未部署同步接口（404）时的退避时长。</summary>
    public static readonly TimeSpan NotFoundBackoff = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 定时同步一轮的总预算：同步被挂在连接探测流程里，网络异常时不能长时间占住它。
    /// 超时按失败处理，已写入的资料是原子的，不会半截生效。
    /// </summary>
    public static readonly TimeSpan ScheduledRoundBudget = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(10);
    private const string LogCategory = "ReceiptProfile";

    private readonly IReceiptProfileLocalStore _store;
    private readonly IStoreReceiptProfileApiClient _apiClient;
    private readonly DeviceAuthorizationState _authorization;
    private readonly TimeProvider _timeProvider;

    private readonly object _gate = new();
    private Task<ReceiptProfileSyncResult>? _inFlight;
    private bool _needsImmediateRound = true;
    private int _ticksSinceRound;
    private string? _lastTickStoreCode;
    private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;
    private readonly HashSet<(string StoreCode, int Version)> _rejectedAcks = [];
    private string? _lastFailureSignature;
    private DateTimeOffset _lastFailureLoggedAt;

    public ReceiptProfileSyncService(
        IReceiptProfileLocalStore store,
        IStoreReceiptProfileApiClient apiClient,
        DeviceAuthorizationState authorization,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _apiClient = apiClient;
        _authorization = authorization;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<ReceiptProfileAppliedEventArgs>? ProfileApplied;

    public Task<ReceiptProfileSyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        return RunExclusiveAsync(budget: null, cancellationToken);
    }

    public async Task<ReceiptProfileSyncResult?> RunScheduledTickAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var storeCode = CurrentStoreCode();
        if (storeCode is null)
        {
            // 设备认证尚未就绪：不消耗「就绪后立即同步一次」的机会，认证完成后的第一拍再同步。
            return null;
        }

        lock (_gate)
        {
            if (!string.Equals(_lastTickStoreCode, storeCode, StringComparison.Ordinal))
            {
                // 第一次见到门店或设备换店：下一拍立即同步，不等 4 拍（换店后旧资料已被清空，要尽快拉新店的）。
                _lastTickStoreCode = storeCode;
                _needsImmediateRound = true;
            }

            _ticksSinceRound++;
            var due = _needsImmediateRound || _ticksSinceRound >= TicksPerRound;
            if (!due)
            {
                return null;
            }

            if (_timeProvider.GetUtcNow() < _backoffUntil)
            {
                // 404 退避期内静默跳过（保留「到点」状态，退避结束后的第一拍就会同步），不刷日志。
                return null;
            }

            _needsImmediateRound = false;
            _ticksSinceRound = 0;
        }

        return await RunExclusiveAsync(ScheduledRoundBudget, cancellationToken);
    }

    /// <summary>
    /// 同一时刻只允许一个同步在飞：已有进行中的同步时复用它的结果，不再发第二个请求。
    /// 调用方自己的取消只影响自己的等待，不会取消被复用的那一轮。
    /// </summary>
    private async Task<ReceiptProfileSyncResult> RunExclusiveAsync(TimeSpan? budget, CancellationToken cancellationToken)
    {
        TaskCompletionSource<ReceiptProfileSyncResult>? owned = null;
        Task<ReceiptProfileSyncResult> shared;
        lock (_gate)
        {
            if (_inFlight is { } existing)
            {
                shared = existing;
            }
            else
            {
                owned = new TaskCompletionSource<ReceiptProfileSyncResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight = owned.Task;
                shared = owned.Task;
            }
        }

        if (owned is not null)
        {
            ReceiptProfileSyncResult? result = null;
            Exception? failure = null;
            try
            {
                result = await ExecuteRoundAsync(budget, cancellationToken);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            // 先清掉「在飞」标记再完成任务：等待者醒来后立刻再发起同步时，不能复用到这个已结束的结果。
            lock (_gate)
            {
                _inFlight = null;
            }

            if (failure is OperationCanceledException canceled)
            {
                owned.TrySetCanceled(canceled.CancellationToken);
            }
            else if (failure is not null)
            {
                owned.TrySetException(failure);
            }
            else
            {
                owned.TrySetResult(result!);
            }
        }

        return await shared.WaitAsync(cancellationToken);
    }

    private async Task<ReceiptProfileSyncResult> ExecuteRoundAsync(TimeSpan? budget, CancellationToken cancellationToken)
    {
        using var budgetSource = budget is { } limit ? new CancellationTokenSource(limit, _timeProvider) : null;
        using var linked = budgetSource is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budgetSource.Token);
        var token = linked?.Token ?? cancellationToken;

        try
        {
            return await RunRoundAsync(token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 调用方没有取消，说明是本轮预算耗尽：当作失败，下一轮重试。
            return Report(ReceiptProfileSyncOutcome.Failed, "sync round timed out");
        }
    }

    private async Task<ReceiptProfileSyncResult> RunRoundAsync(CancellationToken cancellationToken)
    {
        var storeCode = CurrentStoreCode();
        if (storeCode is null)
        {
            return new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.NotReady, Detail: "device authorization is not ready");
        }

        ReceiptProfileLocalState local;
        try
        {
            local = await _store.LoadProfileStateAsync(storeCode, cancellationToken);
        }
        catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
        {
            return Report(ReceiptProfileSyncOutcome.Failed, $"read local profile state failed: {ex.GetType().Name}");
        }

        StoreReceiptProfileSyncDto sync;
        try
        {
            sync = await _apiClient.GetSyncAsync(local.Version, cancellationToken);
        }
        catch (Exception ex) when (IsCancellation(ex, cancellationToken))
        {
            throw;
        }
        catch (CatalogApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // 服务端还没部署同步接口：视为「暂不支持」，每 10 分钟再试一次，避免每 60 秒刷 404。
            lock (_gate)
            {
                _backoffUntil = _timeProvider.GetUtcNow() + NotFoundBackoff;
            }

            return Report(ReceiptProfileSyncOutcome.ServerUnsupported, "sync endpoint not found (HTTP 404), backing off");
        }
        catch (Exception ex)
        {
            return Report(ReceiptProfileSyncOutcome.Failed, $"sync request failed: {ex.Message}");
        }

        lock (_gate)
        {
            // 接口可用了：恢复 60 秒节奏。
            _backoffUntil = DateTimeOffset.MinValue;
        }

        // 容错：字段缺失按 changed=false / version=0 / 无资料处理（DTO 反序列化已给默认值）。
        var serverVersion = Math.Max(0, sync.Version);
        return sync.Changed
            ? await ApplyChangedProfileAsync(storeCode, sync, cancellationToken)
            : await HandleUnchangedAsync(storeCode, local, serverVersion, cancellationToken);
    }

    private async Task<ReceiptProfileSyncResult> HandleUnchangedAsync(
        string storeCode,
        ReceiptProfileLocalState local,
        int serverVersion,
        CancellationToken cancellationToken)
    {
        if (serverVersion == 0)
        {
            // 总部没有任何下发（含快照表被重建）：本机资料保持不变，也不回执（服务端必然拒绝）。
            ClearFailureState();
            return new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.NeverReleased);
        }

        if (serverVersion != local.Version)
        {
            // 协议约定版本不同必然 changed=true；两者矛盾说明响应不可信，不动本机资料。
            return Report(
                ReceiptProfileSyncOutcome.Failed,
                $"sync response is inconsistent (server version {serverVersion}, local {local.Version}, changed=false)");
        }

        // 先清失败签名再补发回执：补发失败会重新记下签名，连续失败才能去重。
        ClearFailureState();

        // 上次写入后回执失败的重试：changed=false 且本机版本高于已回执版本。
        var ackPending = false;
        if (local.NeedsAck)
        {
            ackPending = !await TryAckAsync(storeCode, local.Version, cancellationToken);
        }

        return new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.UpToDate, local.Version, AckPending: ackPending);
    }

    private async Task<ReceiptProfileSyncResult> ApplyChangedProfileAsync(
        string storeCode,
        StoreReceiptProfileSyncDto sync,
        CancellationToken cancellationToken)
    {
        var profile = sync.Profile;
        if (profile is null)
        {
            return Report(ReceiptProfileSyncOutcome.Discarded, "sync response is changed but has no profile");
        }

        // 版本缺失或非正数：无法回执也无法比较新旧，按无效响应丢弃，绝不写入。
        if (profile.Version <= 0)
        {
            return Report(ReceiptProfileSyncOutcome.Discarded, "sync profile has no valid version");
        }

        // 防串店：资料里的门店必须就是本机绑定的门店（trim、大小写不敏感）。
        if (!string.Equals(profile.StoreCode?.Trim(), storeCode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Report(
                ReceiptProfileSyncOutcome.Discarded,
                $"sync profile belongs to a different store (version {profile.Version})");
        }

        // 与设置页「载入门店资料」同一套校验口径，另要求门店名非空；不通过就整份丢弃，不写半截数据。
        var validationError = StoreReceiptProfileValidator.Validate(profile, requireStoreName: true);
        if (validationError is not null)
        {
            return Report(
                ReceiptProfileSyncOutcome.Discarded,
                $"sync profile rejected (version {profile.Version}): {validationError}");
        }

        // 请求期间设备换了店：这份资料属于旧店，丢弃。
        if (!string.Equals(CurrentStoreCode(), storeCode, StringComparison.Ordinal))
        {
            return Report(ReceiptProfileSyncOutcome.Discarded, "device store changed during sync");
        }

        bool applied;
        try
        {
            applied = await _store.ApplyHeadquartersProfileAsync(
                storeCode,
                new ReceiptProfileFields(
                    profile.BrandName,
                    profile.StoreName,
                    profile.Address,
                    profile.Phone,
                    profile.Abn,
                    profile.ReturnPolicy,
                    profile.VoucherTerms,
                    profile.InstallmentTerms),
                profile.Version,
                cancellationToken);
        }
        catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
        {
            return Report(
                ReceiptProfileSyncOutcome.Failed,
                $"write profile failed (version {profile.Version}): {ex.GetType().Name}");
        }

        if (!applied)
        {
            return Report(ReceiptProfileSyncOutcome.Discarded, "device store changed during sync");
        }

        ClearFailureState();
        ConsoleLog.Write(LogCategory, $"applied version={profile.Version} store={storeCode}");
        RaiseProfileApplied(storeCode, profile.Version);

        // 写入成功后回执；回执失败不影响已生效的资料，下一轮按 version > ackedVersion 补发。
        var acked = await TryAckAsync(storeCode, profile.Version, cancellationToken);
        return new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.Applied, profile.Version, AckPending: !acked);
    }

    /// <summary>
    /// 回执并记录已回执版本。返回 true 表示回执已被服务端接受。
    /// 400 说明服务端不认这个版本（例如快照表被重建）：本进程内不再对同一门店同一版本重试，避免死循环；
    /// 401/403/网络/5xx 等：下一轮重试。
    /// </summary>
    private async Task<bool> TryAckAsync(string storeCode, int version, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_rejectedAcks.Contains((storeCode, version)))
            {
                return false;
            }
        }

        try
        {
            await _apiClient.AckAsync(version, cancellationToken);
        }
        catch (Exception ex) when (IsCancellation(ex, cancellationToken))
        {
            throw;
        }
        catch (CatalogApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            lock (_gate)
            {
                _rejectedAcks.Add((storeCode, version));
            }

            ConsoleLog.Write(
                LogCategory,
                $"ack rejected version={version} store={storeCode} code={ex.ErrorCode ?? "-"}; will not retry this version in this process");
            return false;
        }
        catch (Exception ex)
        {
            Report(ReceiptProfileSyncOutcome.Failed, $"ack request failed (version {version}): {ex.Message}");
            return false;
        }

        try
        {
            // 返回 false 只表示回执期间资料已被替换/换店，回执本身已被服务端接受。
            await _store.MarkProfileAckedAsync(storeCode, version, cancellationToken);
        }
        catch (Exception ex) when (IsCancellation(ex, cancellationToken))
        {
            throw;
        }
        catch (Exception ex)
        {
            Report(ReceiptProfileSyncOutcome.Failed, $"record ack failed (version {version}): {ex.GetType().Name}");
            return false;
        }

        ConsoleLog.Write(LogCategory, $"acked version={version} store={storeCode}");
        return true;
    }

    private void RaiseProfileApplied(string storeCode, int version)
    {
        var handlers = ProfileApplied;
        if (handlers is null)
        {
            return;
        }

        var args = new ReceiptProfileAppliedEventArgs(storeCode, version);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<ReceiptProfileAppliedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                // 订阅方（例如设置页刷新）出错不能影响同步结果。
                ConsoleLog.Write(LogCategory, $"applied handler failed error={ex.GetType().Name}");
            }
        }
    }

    private string? CurrentStoreCode()
    {
        // 与 ReceiptPrinterSettingsStore 的绑定判断使用同一个来源（设备认证上下文里的门店代码）。
        return _authorization.Current?.StoreCode is { Length: > 0 } code ? code : null;
    }

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken)
    {
        return exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
    }

    private void ClearFailureState()
    {
        lock (_gate)
        {
            _lastFailureSignature = null;
        }
    }

    /// <summary>
    /// 记录失败并生成结果。同样的失败连续出现时只记第一条，之后每 10 分钟最多再记一条，
    /// 避免离线或服务端异常时每 60 秒刷一行日志。日志只含版本号与原因，不含资料内容。
    /// </summary>
    private ReceiptProfileSyncResult Report(ReceiptProfileSyncOutcome outcome, string detail)
    {
        var signature = $"{outcome}:{detail}";
        var now = _timeProvider.GetUtcNow();
        bool shouldLog;
        lock (_gate)
        {
            shouldLog = !string.Equals(signature, _lastFailureSignature, StringComparison.Ordinal) ||
                        now - _lastFailureLoggedAt >= FailureLogInterval;
            if (shouldLog)
            {
                _lastFailureSignature = signature;
                _lastFailureLoggedAt = now;
            }
        }

        if (shouldLog)
        {
            ConsoleLog.Write(LogCategory, $"sync {outcome}: {detail}");
        }

        return new ReceiptProfileSyncResult(outcome, Detail: detail);
    }
}

/// <summary>把同步结果映射成设置页状态文案的资源键与参数（便于脱离 WPF 单测）。</summary>
public static class ReceiptProfileSyncStatusText
{
    public const string AppliedKey = "settings.status.receiptProfileSyncApplied";
    public const string UpToDateKey = "settings.status.receiptProfileSyncUpToDate";
    public const string NeverReleasedKey = "settings.status.receiptProfileSyncNeverReleased";
    public const string DiscardedKey = "settings.status.receiptProfileSyncDiscarded";
    public const string FailedKey = "settings.status.receiptProfileSyncFailed";
    public const string UnsupportedKey = "settings.status.receiptProfileSyncUnsupported";
    public const string NotReadyKey = "settings.status.receiptProfileSyncNotReady";

    public static (string Key, object[] Args) Describe(ReceiptProfileSyncResult result)
    {
        return result.Outcome switch
        {
            ReceiptProfileSyncOutcome.Applied => (AppliedKey, [result.Version]),
            ReceiptProfileSyncOutcome.UpToDate => (UpToDateKey, [result.Version]),
            ReceiptProfileSyncOutcome.NeverReleased => (NeverReleasedKey, []),
            ReceiptProfileSyncOutcome.Discarded => (DiscardedKey, []),
            ReceiptProfileSyncOutcome.ServerUnsupported => (UnsupportedKey, []),
            ReceiptProfileSyncOutcome.NotReady => (NotReadyKey, []),
            _ => (FailedKey, [result.Detail ?? string.Empty])
        };
    }
}
