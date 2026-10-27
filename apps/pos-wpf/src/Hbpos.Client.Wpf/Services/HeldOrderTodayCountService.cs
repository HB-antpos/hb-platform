using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.HeldOrders;
using HeldClaimStatus = Hbpos.Client.Wpf.Models.SharedHeldOrderClaimStatus;
using HeldPublicationStatus = Hbpos.Client.Wpf.Models.SharedHeldOrderPublicationStatus;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 首页「取单」按钮角标用：统计门店本地「今天」仍可取的挂单数量。
/// </summary>
public interface IHeldOrderTodayCountService
{
    /// <summary>
    /// 统计今天（<see cref="TimeProvider.LocalTimeZone"/> 的日历日）仍可取的挂单张数。
    /// 本机暂存挂单与本店共享挂单（远端 Pending 汇总）按挂单单号去重后合并计数。
    /// 离线、未登录收银员、远端失败或超时只降级为本机计数，不抛出；本机读取失败才会抛出（调用方保留旧值）。
    /// </summary>
    /// <param name="session">当前会话：门店、设备与联机状态。</param>
    /// <param name="forceRemoteRefresh">true 时绕过远端短缓存与失败退避，立即向服务端要最新（挂单/取单成功后使用）。</param>
    Task<int> GetTodayCountAsync(
        PosSessionState session,
        bool forceRemoteRefresh = false,
        CancellationToken cancellationToken = default);
}

/// <summary>统计服务的可调参数；默认值即生产值，测试可缩短时间。</summary>
public sealed record HeldOrderTodayCountOptions
{
    /// <summary>远端 Pending 汇总的缓存时长，避免每次刷新都打接口。</summary>
    public TimeSpan RemoteCacheDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>单次远端请求的超时；超时按失败降级为只算本机。</summary>
    public TimeSpan RemoteTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>远端失败后的退避：期间普通刷新直接只算本机，不再重复请求。</summary>
    public TimeSpan RemoteFailureBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>本机暂存挂单一次最多读取的张数（仓储上限 500，按挂单时间倒序，今天的在最前）。</summary>
    public int LocalTake { get; init; } = 500;
}

/// <summary>
/// <para>
/// 口径与「取单」页（<c>TransactionHistoryViewModel</c> 挂单源）一致，取「本机」与「非本机」两个页签的并集：
/// 本机暂存 Pending 挂单 + 远端 Pending 汇总，按 HoldGuid（= SuspendedOrderGuid）去重；
/// 一张单只有在页面上 <c>CanRecall</c> 为真时才计数（排除删除暂存中、已被本机 claim 占用、远端已不再 Pending 的已发布单）。
/// </para>
/// <para>
/// 「今天」取 <see cref="TimeProvider.LocalTimeZone"/> 的日历日（与取单页日期筛选、日结业务日同一来源），
/// 本机单用 SuspendedAt、共享单用 HeldAtUtc 换算后比较。
/// </para>
/// </summary>
public sealed class HeldOrderTodayCountService : IHeldOrderTodayCountService
{
    private readonly ISuspendedOrderService _suspendedOrderService;
    private readonly ISharedHeldOrderRepository? _sharedHeldOrderRepository;
    private readonly ISharedHeldOrderApiClient? _sharedHeldOrderApiClient;
    private readonly TimeProvider _timeProvider;
    private readonly HeldOrderTodayCountOptions _options;

    // 远端汇总缓存与单飞：全部字段只在 _remoteGate 内读写。
    private readonly object _remoteGate = new();
    private RemoteSnapshot? _remoteCache;
    private DateTimeOffset? _remoteFailedAtUtc;
    private Task<RemoteSnapshot?>? _remoteInFlight;
    private string? _remoteInFlightStore;
    private bool _remoteFailureLogged;

    public HeldOrderTodayCountService(
        ISuspendedOrderService suspendedOrderService,
        ISharedHeldOrderRepository? sharedHeldOrderRepository,
        ISharedHeldOrderApiClient? sharedHeldOrderApiClient,
        TimeProvider? timeProvider = null,
        HeldOrderTodayCountOptions? options = null)
    {
        _suspendedOrderService = suspendedOrderService ?? throw new ArgumentNullException(nameof(suspendedOrderService));
        _sharedHeldOrderRepository = sharedHeldOrderRepository;
        _sharedHeldOrderApiClient = sharedHeldOrderApiClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new HeldOrderTodayCountOptions();
    }

    public async Task<int> GetTodayCountAsync(
        PosSessionState session,
        bool forceRemoteRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var storeCode = session.StoreCode;
        var timeZone = _timeProvider.LocalTimeZone;
        var today = _timeProvider.GetLocalNow().Date;

        // 1) 本机暂存挂单：全店口径（不按 device 过滤），含他机共享后物化到本机的单；
        // 仓储只返回 Status=Pending，已取走/已删除的不会出现。
        var pendingOrders = await _suspendedOrderService
            .GetPendingOrdersAsync(storeCode, null, null, _options.LocalTake, cancellationToken)
            .ConfigureAwait(false);
        var localPendingGuids = new HashSet<Guid>();
        var localToday = new Dictionary<Guid, SuspendedOrderSummary>();
        foreach (var order in pendingOrders)
        {
            localPendingGuids.Add(order.SuspendedOrderGuid);
            if (IsToday(order.SuspendedAt, today, timeZone))
            {
                localToday[order.SuspendedOrderGuid] = order;
            }
        }

        var publications = await LoadPublicationsAsync(localToday.Keys, cancellationToken).ConfigureAwait(false);
        var claims = await LoadClaimsAsync(session, cancellationToken).ConfigureAwait(false);

        // 2) 共享挂单：只有联机且已登录收银员才请求；失败/超时/退避期内 remoteItems 为 null，退化为只算本机。
        // 未登录收银员时（登录弹窗盖在首页上，整夜无人登录很常见）请求不会带 X-HBPOS-Cashier-Authorization，
        // GET held-orders 必然被服务端以 401 拒绝；每台空闲收银机每分钟白打一次（生产日志 10-25 仅此接口约 5700 次 401）。
        // 这里只看「是否已登录」，不看 CashierSession 上的票据有效期：后台刷新只更新 ICashierSessionContext 里的票据，
        // PosSessionState.CashierSession 仍是登录时的快照，登录满 24 小时后它的 AuthorizationExpiresAtUtc 会过期但实际票据仍有效。
        // 收银员登录后范围变化会触发一次刷新（见 OnSessionChangedForHeldOrderCount），角标随即补上共享挂单。
        var remoteItems = session.IsOnline && session.CashierSession is not null
            ? await TryGetRemoteItemsAsync(storeCode, forceRemoteRefresh, cancellationToken).ConfigureAwait(false)
            : null;

        var remoteToday = new Dictionary<Guid, SharedHeldOrderListItemDto>();
        HashSet<Guid>? remoteAllGuids = null;
        if (remoteItems is not null)
        {
            remoteAllGuids = new HashSet<Guid>();
            foreach (var item in remoteItems)
            {
                if (!string.Equals(item.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                remoteAllGuids.Add(item.HoldGuid);
                if (IsToday(item.HeldAtUtc, today, timeZone))
                {
                    remoteToday[item.HoldGuid] = item;
                }
            }
        }

        // 本机已确认处理掉的自家共享单（已取走/已删除），远端缓存可能还没收敛：以本机为准排除。
        var locallyResolved = await FindLocallyResolvedOwnRemoteOrdersAsync(
            session,
            remoteToday,
            localPendingGuids,
            cancellationToken).ConfigureAwait(false);

        // 3) 按 HoldGuid 合并去重，再逐张判定「仍可取」。
        var holdGuids = new HashSet<Guid>(localToday.Keys);
        holdGuids.UnionWith(remoteToday.Keys);
        var count = 0;
        foreach (var holdGuid in holdGuids)
        {
            localToday.TryGetValue(holdGuid, out var local);
            remoteToday.TryGetValue(holdGuid, out var remote);
            publications.TryGetValue(holdGuid, out var publication);
            HeldClaimStatus? claimStatus = claims.TryGetValue(holdGuid, out var claim) ? claim.Status : null;

            if (remote is null)
            {
                // 远端成功响应是权威：本机标记 Published 且无本机 claim 的单，若服务端 Pending 汇总已不含它
                // （已被别处取走/完成/取消），取单页会把它收敛移除，这里同样不计。
                if (remoteAllGuids is not null &&
                    local is not null &&
                    publication?.Status == HeldPublicationStatus.Published &&
                    claimStatus is null &&
                    !remoteAllGuids.Contains(holdGuid))
                {
                    continue;
                }
            }
            else if (local is null && locallyResolved.Contains(holdGuid))
            {
                continue;
            }

            if (CanRecall(
                    hasLocalCopy: local is not null,
                    publication,
                    claimStatus,
                    serverPending: remote is not null,
                    session.IsOnline))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 与 <c>TransactionHistoryViewModel.BuildHeldOrderRow</c> 里 CanRecall 的判定逐项一致：
    /// 远端召回（联机 + 服务端 Pending + 无 claim）、离线召回（本地有副本 + 已排队/已发布 + 无 claim）、
    /// 旧版本地召回（本地有副本 + 未评估/被阻断/无发布记录），且删除暂存中的单一律不可取。
    /// </summary>
    internal static bool CanRecall(
        bool hasLocalCopy,
        SharedHeldOrderPublication? publication,
        HeldClaimStatus? claimStatus,
        bool serverPending,
        bool isOnline)
    {
        var isDeletePending = publication is
        {
            Status: HeldPublicationStatus.Blocked,
            ErrorCode: "LOCAL_DELETE_PENDING_REMOTE" or "LOCAL_DELETE_PENDING_LOCAL"
        };
        var canRemoteRecall = !isDeletePending &&
            serverPending &&
            claimStatus is null &&
            isOnline;
        var canOfflineRecall = !isDeletePending &&
            hasLocalCopy &&
            publication?.Status is (HeldPublicationStatus.PendingPublish or HeldPublicationStatus.Published) &&
            claimStatus is null;
        var canLegacyRecall = hasLocalCopy &&
            !isDeletePending &&
            !canRemoteRecall &&
            !canOfflineRecall &&
            (publication is null ||
             publication.Status is (HeldPublicationStatus.NeedsEvaluation or HeldPublicationStatus.Blocked));
        return canRemoteRecall || canOfflineRecall || canLegacyRecall;
    }

    /// <summary>以门店本地日历日判断是否「今天」，不依赖 DateTimeOffset 自带的偏移。</summary>
    private static bool IsToday(DateTimeOffset instant, DateTime today, TimeZoneInfo timeZone)
    {
        return TimeZoneInfo.ConvertTime(instant, timeZone).Date == today;
    }

    private async Task<Dictionary<Guid, SharedHeldOrderPublication?>> LoadPublicationsAsync(
        IEnumerable<Guid> holdGuids,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, SharedHeldOrderPublication?>();
        if (_sharedHeldOrderRepository is null)
        {
            return result;
        }

        // 今天的本机挂单通常只有个位数，顺序读取即可，避免后台统计与收银读库抢连接。
        foreach (var holdGuid in holdGuids)
        {
            try
            {
                result[holdGuid] = await _sharedHeldOrderRepository
                    .GetPublicationAsync(holdGuid, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 与取单页一致：单张发布记录读取失败按「无发布记录」处理。
                result[holdGuid] = null;
            }
        }

        return result;
    }

    private async Task<Dictionary<Guid, SharedHeldOrderClaimRecovery>> LoadClaimsAsync(
        PosSessionState session,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, SharedHeldOrderClaimRecovery>();
        if (_sharedHeldOrderRepository is null)
        {
            return result;
        }

        var claims = await _sharedHeldOrderRepository
            .FindRecoverableClaimsAsync(session.StoreCode, session.DeviceCode, cancellationToken)
            .ConfigureAwait(false);
        foreach (var claim in claims)
        {
            result[claim.HoldGuid] = claim;
        }

        return result;
    }

    /// <summary>
    /// 远端缓存最长滞后 60 秒。本机自己挂出的共享单若本机记录已不是 Pending（已取走/已删除），
    /// 在缓存收敛前仍会出现在远端汇总里，需要用本机记录排除，否则取单/删除后角标会多算一张。
    /// 其他设备的单本机无法判断，仍以远端为准。
    /// </summary>
    private async Task<HashSet<Guid>> FindLocallyResolvedOwnRemoteOrdersAsync(
        PosSessionState session,
        IReadOnlyDictionary<Guid, SharedHeldOrderListItemDto> remoteToday,
        IReadOnlySet<Guid> localPendingGuids,
        CancellationToken cancellationToken)
    {
        var resolved = new HashSet<Guid>();
        foreach (var (holdGuid, item) in remoteToday)
        {
            if (localPendingGuids.Contains(holdGuid) ||
                !string.Equals(item.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var localOrder = await _suspendedOrderService
                    .GetOrderAsync(holdGuid, cancellationToken)
                    .ConfigureAwait(false);
                if (localOrder is not null && localOrder.Status != SuspendedOrderStatus.Pending)
                {
                    resolved.Add(holdGuid);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 读取失败不排除：宁可多算一张，等缓存过期后自然收敛。
            }
        }

        return resolved;
    }

    private sealed record RemoteSnapshot(
        string StoreCode,
        DateTimeOffset FetchedAtUtc,
        IReadOnlyList<SharedHeldOrderListItemDto> Items);

    /// <summary>
    /// 取远端 Pending 汇总；null 表示本轮不可用（无客户端、退避期内、失败或超时），调用方只算本机。
    /// 缓存命中不发请求；并发请求共用同一次在途调用（单飞）。
    /// </summary>
    private async Task<IReadOnlyList<SharedHeldOrderListItemDto>?> TryGetRemoteItemsAsync(
        string storeCode,
        bool force,
        CancellationToken cancellationToken)
    {
        if (_sharedHeldOrderApiClient is null)
        {
            return null;
        }

        Task<RemoteSnapshot?> fetch;
        lock (_remoteGate)
        {
            var now = _timeProvider.GetUtcNow();
            if (!force &&
                _remoteCache is { } cache &&
                string.Equals(cache.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase) &&
                now >= cache.FetchedAtUtc &&
                now - cache.FetchedAtUtc < _options.RemoteCacheDuration)
            {
                return cache.Items;
            }

            if (!force &&
                _remoteFailedAtUtc is { } failedAt &&
                now >= failedAt &&
                now - failedAt < _options.RemoteFailureBackoff)
            {
                return null;
            }

            if (_remoteInFlight is { IsCompleted: false } inFlight &&
                string.Equals(_remoteInFlightStore, storeCode, StringComparison.OrdinalIgnoreCase))
            {
                fetch = inFlight;
            }
            else
            {
                // 放到线程池再进入 FetchRemoteAsync：它会回头拿同一把锁，避免在锁内同步重入。
                fetch = Task.Run(() => FetchRemoteAsync(storeCode), CancellationToken.None);
                _remoteInFlight = fetch;
                _remoteInFlightStore = storeCode;
            }
        }

        // 等待方可以各自取消，但不能取消共享的那次请求（它自带超时）。
        var snapshot = await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot?.Items;
    }

    private async Task<RemoteSnapshot?> FetchRemoteAsync(string storeCode)
    {
        try
        {
            using var timeout = new CancellationTokenSource(_options.RemoteTimeout);
            // WaitAsync：即便客户端实现忽略取消令牌，也能在超时点放手，不拖住角标刷新。
            var items = await _sharedHeldOrderApiClient!
                .ListPendingAsync(timeout.Token)
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
            var snapshot = new RemoteSnapshot(
                storeCode,
                _timeProvider.GetUtcNow(),
                items ?? []);
            bool recovered;
            lock (_remoteGate)
            {
                _remoteCache = snapshot;
                _remoteFailedAtUtc = null;
                recovered = _remoteFailureLogged;
                _remoteFailureLogged = false;
            }

            if (recovered)
            {
                ConsoleLog.WriteInformation(
                    "HeldOrder",
                    $"held order today count remote recovered storeCode={storeCode}");
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            // 含超时取消、网络错误、功能未启用等：统一降级，不影响收银。
            bool firstFailure;
            lock (_remoteGate)
            {
                _remoteFailedAtUtc = _timeProvider.GetUtcNow();
                firstFailure = !_remoteFailureLogged;
                _remoteFailureLogged = true;
            }

            if (firstFailure)
            {
                var apiException = ex as SharedHeldOrderApiException;
                ConsoleLog.WriteWarning(
                    "HeldOrder",
                    $"held order today count remote unavailable, counting local only storeCode={storeCode} " +
                    $"kind={apiException?.Kind.ToString() ?? "<none>"} error={ex.GetType().Name} message={ex.Message}",
                    new ApplicationLogContext(
                        RequestPath: "api/v1/held-orders",
                        RequestMethod: "GET",
                        StatusCode: apiException is null ? null : (int)apiException.StatusCode),
                    apiException?.InnerException ?? ex);
            }

            return null;
        }
    }
}
