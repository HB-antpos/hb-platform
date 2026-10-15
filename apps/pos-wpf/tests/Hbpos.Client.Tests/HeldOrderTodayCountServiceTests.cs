using System.Net;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.HeldOrders;
using Microsoft.Extensions.Time.Testing;
using static Hbpos.Client.Tests.SharedHeldOrderClientTestSupport;
using HeldClaimStatus = Hbpos.Client.Wpf.Models.SharedHeldOrderClaimStatus;

namespace Hbpos.Client.Tests;

/// <summary>
/// 首页「取单」角标统计服务：口径（本机 + 共享、按 HoldGuid 去重、仍可取）、门店本地「今天」边界、
/// 远端缓存 / 超时 / 失败降级。全部用假仓储 + FakeTimeProvider，不碰真实 SQLite 与网络。
/// 服务会写 ConsoleLog（远端降级告警），所以与其他日志测试串行。
/// </summary>
[Collection(GlobalLoggingTestCollection.Name)]
public sealed class HeldOrderTodayCountServiceTests
{
    // 门店本地时区固定为 UTC+10（无夏令时），「今天」= 2026-10-15。
    private static readonly TimeSpan StoreOffset = TimeSpan.FromHours(10);
    private static readonly TimeZoneInfo StoreTimeZone =
        TimeZoneInfo.CreateCustomTimeZone("StoreTime+10", StoreOffset, "StoreTime+10", "StoreTime+10");
    private static readonly PosSessionState Online = Session(isOnline: true);
    private static readonly PosSessionState Offline = Session(isOnline: false);

    // ───────────── 本机：今天 / 非今天 ─────────────

    [Fact]
    public async Task Counts_only_local_orders_suspended_today_in_store_local_time()
    {
        var clock = CreateClock(Local(2026, 10, 15, 14, 0));
        var suspended = new FakeSuspendedOrderService
        {
            PendingOrders =
            [
                LocalSummary(Guid.NewGuid(), Local(2026, 10, 15, 0, 0)),     // 今天 0 点整：算
                LocalSummary(Guid.NewGuid(), Local(2026, 10, 15, 9, 30)),    // 今天：算
                LocalSummary(Guid.NewGuid(), Local(2026, 10, 14, 23, 59)),   // 昨天：不算
                LocalSummary(Guid.NewGuid(), Local(2026, 10, 16, 0, 0))      // 明天 0 点：不算
            ]
        };
        var service = CreateService(suspended, clock: clock);

        var count = await service.GetTodayCountAsync(Online);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Today_boundary_follows_store_local_midnight_not_utc_or_machine_offset()
    {
        // 门店时间 10-14 23:30（= UTC 13:30）。订单 A 门店 23:45（UTC 13:45）、订单 B 门店 00:10（UTC 14:10 的次日）。
        var clock = CreateClock(Local(2026, 10, 14, 23, 30));
        var orderA = LocalSummary(Guid.NewGuid(), Local(2026, 10, 14, 23, 45));
        var orderB = LocalSummary(Guid.NewGuid(), Local(2026, 10, 15, 0, 10));
        var suspended = new FakeSuspendedOrderService { PendingOrders = [orderA, orderB] };
        var service = CreateService(suspended, clock: clock);

        // 跨午夜前：只有 A 属于「今天」。
        Assert.Equal(1, await service.GetTodayCountAsync(Online));

        // 跨过门店午夜：A 变成昨天，B 变成今天——数量仍是 1，但换了一张单。
        clock.Advance(TimeSpan.FromHours(1));
        suspended.PendingOrders = [orderA];
        Assert.Equal(0, await service.GetTodayCountAsync(Online));
        suspended.PendingOrders = [orderA, orderB];
        Assert.Equal(1, await service.GetTodayCountAsync(Online));
    }

    [Fact]
    public async Task Local_read_failure_propagates_so_caller_can_keep_previous_value()
    {
        var suspended = new FakeSuspendedOrderService { Failure = new InvalidOperationException("sqlite busy") };
        var service = CreateService(suspended);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetTodayCountAsync(Online));
    }

    // ───────────── 本机 + 共享：去重 ─────────────

    [Fact]
    public async Task Same_hold_guid_in_local_and_remote_is_counted_once_and_remote_only_orders_from_other_devices_count()
    {
        var shared = Guid.NewGuid();
        var otherDeviceHold = Guid.NewGuid();
        var localOnly = Guid.NewGuid();
        var suspended = new FakeSuspendedOrderService
        {
            PendingOrders =
            [
                LocalSummary(shared, Local(2026, 10, 15, 9, 0)),
                LocalSummary(localOnly, Local(2026, 10, 15, 10, 0))
            ]
        };
        var repository = new FakeSharedHeldOrderRepository();
        repository.Publications[shared] = Publication(shared, SharedHeldOrderPublicationStatus.Published);
        var api = ApiReturning(
            RemoteItem(shared, Local(2026, 10, 15, 9, 0)),
            RemoteItem(otherDeviceHold, Local(2026, 10, 15, 11, 0), deviceCode: "POS-02"));
        var service = CreateService(suspended, repository, api);

        var count = await service.GetTodayCountAsync(Online);

        // shared（本机 + 远端同一张）算 1，localOnly 算 1，别的收银机共享来的 otherDeviceHold 算 1。
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task Remote_orders_of_other_stores_or_other_days_are_ignored()
    {
        var suspended = new FakeSuspendedOrderService();
        var api = ApiReturning(
            RemoteItem(Guid.NewGuid(), Local(2026, 10, 15, 9, 0), deviceCode: "POS-02"),
            RemoteItem(Guid.NewGuid(), Local(2026, 10, 14, 9, 0), deviceCode: "POS-02"),
            RemoteItem(Guid.NewGuid(), Local(2026, 10, 15, 9, 0), deviceCode: "POS-02", storeCode: "S99"));
        var service = CreateService(suspended, new FakeSharedHeldOrderRepository(), api);

        Assert.Equal(1, await service.GetTodayCountAsync(Online));
    }

    // ───────────── 远端失败 / 离线 / 超时：只算本机 ─────────────

    [Fact]
    public async Task Remote_failure_degrades_to_local_only_without_throwing()
    {
        var suspended = new FakeSuspendedOrderService
        {
            PendingOrders = [LocalSummary(Guid.NewGuid(), Local(2026, 10, 15, 9, 0))]
        };
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => Task.FromException<IReadOnlyList<SharedHeldOrderListItemDto>>(
                new SharedHeldOrderApiException(
                    SharedHeldOrderApiErrorKind.Retryable,
                    "boom",
                    errorCode: null,
                    HttpStatusCode.BadGateway))
        };
        var service = CreateService(suspended, new FakeSharedHeldOrderRepository(), api);

        Assert.Equal(1, await service.GetTodayCountAsync(Online));
    }

    [Fact]
    public async Task Remote_timeout_degrades_to_local_only()
    {
        var suspended = new FakeSuspendedOrderService
        {
            PendingOrders = [LocalSummary(Guid.NewGuid(), Local(2026, 10, 15, 9, 0))]
        };
        var api = new StubSharedHeldOrderApiClient
        {
            // 客户端永不返回，只能靠服务自己的超时放手。
            ListPending = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Array.Empty<SharedHeldOrderListItemDto>();
            }
        };
        var service = CreateService(
            suspended,
            new FakeSharedHeldOrderRepository(),
            api,
            options: new HeldOrderTodayCountOptions { RemoteTimeout = TimeSpan.FromMilliseconds(50) });

        var count = await service.GetTodayCountAsync(Online).WaitAsync(DefaultTimeout);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Offline_session_never_calls_remote_and_counts_local_only()
    {
        var suspended = new FakeSuspendedOrderService
        {
            PendingOrders = [LocalSummary(Guid.NewGuid(), Local(2026, 10, 15, 9, 0))]
        };
        var api = ApiReturning(RemoteItem(Guid.NewGuid(), Local(2026, 10, 15, 9, 0), deviceCode: "POS-02"));
        var service = CreateService(suspended, new FakeSharedHeldOrderRepository(), api);

        Assert.Equal(1, await service.GetTodayCountAsync(Offline));
        Assert.Equal(0, api.ListCalls);
    }

    // ───────────── 远端缓存 / 退避 ─────────────

    [Fact]
    public async Task Remote_result_is_cached_for_60_seconds_and_force_bypasses_cache()
    {
        var clock = CreateClock(Local(2026, 10, 15, 14, 0));
        var api = ApiReturning(RemoteItem(Guid.NewGuid(), Local(2026, 10, 15, 9, 0), deviceCode: "POS-02"));
        var service = CreateService(new FakeSuspendedOrderService(), new FakeSharedHeldOrderRepository(), api, clock);

        await service.GetTodayCountAsync(Online);
        await service.GetTodayCountAsync(Online);
        Assert.Equal(1, api.ListCalls);

        clock.Advance(TimeSpan.FromSeconds(59));
        await service.GetTodayCountAsync(Online);
        Assert.Equal(1, api.ListCalls);

        clock.Advance(TimeSpan.FromSeconds(2));
        await service.GetTodayCountAsync(Online);
        Assert.Equal(2, api.ListCalls);

        await service.GetTodayCountAsync(Online, forceRemoteRefresh: true);
        Assert.Equal(3, api.ListCalls);
    }

    [Fact]
    public async Task Remote_failure_backs_off_for_normal_refreshes_but_force_retries_immediately()
    {
        var clock = CreateClock(Local(2026, 10, 15, 14, 0));
        var failing = true;
        var calls = 0;
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ =>
            {
                calls++;
                return failing
                    ? Task.FromException<IReadOnlyList<SharedHeldOrderListItemDto>>(new InvalidOperationException("down"))
                    : Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>(
                        [RemoteItem(Guid.NewGuid(), Local(2026, 10, 15, 9, 0), deviceCode: "POS-02")]);
            }
        };
        var service = CreateService(new FakeSuspendedOrderService(), new FakeSharedHeldOrderRepository(), api, clock);

        Assert.Equal(0, await service.GetTodayCountAsync(Online));
        Assert.Equal(1, calls);

        // 退避窗口内普通刷新不再打接口。
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await service.GetTodayCountAsync(Online));
        Assert.Equal(1, calls);

        // 强制刷新无视退避；服务恢复后立刻拿到共享单。
        failing = false;
        Assert.Equal(1, await service.GetTodayCountAsync(Online, forceRemoteRefresh: true));
        Assert.Equal(2, calls);
    }

    // ───────────── 「仍可取」判定 ─────────────

    [Fact]
    public async Task Published_local_order_missing_from_successful_remote_list_is_not_counted_but_counts_when_remote_fails()
    {
        var holdGuid = Guid.NewGuid();
        var suspended = new FakeSuspendedOrderService
        {
            PendingOrders = [LocalSummary(holdGuid, Local(2026, 10, 15, 9, 0))]
        };
        var repository = new FakeSharedHeldOrderRepository();
        repository.Publications[holdGuid] = Publication(holdGuid, SharedHeldOrderPublicationStatus.Published);

        // 服务端成功返回、Pending 汇总已不含该单：已被别处取走 / 完成 / 取消，取单页会收敛移除。
        var authoritative = CreateService(suspended, repository, ApiReturning());
        Assert.Equal(0, await authoritative.GetTodayCountAsync(Online));

        // 远端失败时没有权威结论，按离线召回仍可取。
        var failing = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => Task.FromException<IReadOnlyList<SharedHeldOrderListItemDto>>(
                new InvalidOperationException("down"))
        };
        var degraded = CreateService(suspended, repository, failing);
        Assert.Equal(1, await degraded.GetTodayCountAsync(Online));
    }

    [Theory]
    [InlineData("LOCAL_DELETE_PENDING_REMOTE")]
    [InlineData("LOCAL_DELETE_PENDING_LOCAL")]
    public async Task Orders_staged_for_local_delete_are_not_counted(string errorCode)
    {
        var holdGuid = Guid.NewGuid();
        var suspended = new FakeSuspendedOrderService
        {
            PendingOrders = [LocalSummary(holdGuid, Local(2026, 10, 15, 9, 0))]
        };
        var repository = new FakeSharedHeldOrderRepository();
        repository.Publications[holdGuid] = Publication(
            holdGuid,
            SharedHeldOrderPublicationStatus.Blocked,
            errorCode);
        var service = CreateService(suspended, repository, ApiReturning(RemoteItem(holdGuid, Local(2026, 10, 15, 9, 0))));

        Assert.Equal(0, await service.GetTodayCountAsync(Online));
    }

    [Fact]
    public async Task Orders_claimed_by_this_device_are_not_counted()
    {
        var holdGuid = Guid.NewGuid();
        var suspended = new FakeSuspendedOrderService
        {
            PendingOrders = [LocalSummary(holdGuid, Local(2026, 10, 15, 9, 0))]
        };
        var repository = new FakeSharedHeldOrderRepository();
        repository.Publications[holdGuid] = Publication(holdGuid, SharedHeldOrderPublicationStatus.Published);
        repository.Claims =
        [
            Recovery(Guid.NewGuid(), holdGuid, HeldClaimStatus.Active)
        ];
        var service = CreateService(suspended, repository, ApiReturning(RemoteItem(holdGuid, Local(2026, 10, 15, 9, 0))));

        Assert.Equal(0, await service.GetTodayCountAsync(Online));
    }

    [Fact]
    public async Task Own_remote_order_already_recalled_locally_is_excluded_while_stale_remote_cache_lingers()
    {
        var recalled = Guid.NewGuid();
        var otherDevice = Guid.NewGuid();
        var suspended = new FakeSuspendedOrderService();
        suspended.Orders[recalled] = LocalOrder(recalled, SuspendedOrderStatus.Recalled);
        var api = ApiReturning(
            RemoteItem(recalled, Local(2026, 10, 15, 9, 0)),                          // 本机自己挂的，本机已取走
            RemoteItem(otherDevice, Local(2026, 10, 15, 9, 5), deviceCode: "POS-02")); // 他机的，本机无法判断
        var service = CreateService(suspended, new FakeSharedHeldOrderRepository(), api);

        Assert.Equal(1, await service.GetTodayCountAsync(Online));
    }

    [Theory]
    // hasLocal, publication, claim, serverPending, online → 期望
    [InlineData(true, null, null, false, false, true)]                                 // 旧版本地挂单（无发布记录）
    [InlineData(true, SharedHeldOrderPublicationStatus.NeedsEvaluation, null, false, true, true)]
    [InlineData(true, SharedHeldOrderPublicationStatus.PendingPublish, null, false, false, true)]  // 离线召回
    [InlineData(true, SharedHeldOrderPublicationStatus.Published, null, false, false, true)]       // 离线召回
    [InlineData(true, SharedHeldOrderPublicationStatus.Published, null, true, true, true)]         // 远端召回
    [InlineData(true, SharedHeldOrderPublicationStatus.Published, HeldClaimStatus.Prepared, true, true, false)]
    [InlineData(true, SharedHeldOrderPublicationStatus.PendingPublish, HeldClaimStatus.Active, false, false, false)]
    [InlineData(true, SharedHeldOrderPublicationStatus.Blocked, null, false, false, true)]         // 被阻断仍可旧版召回
    [InlineData(false, null, null, true, true, true)]                                              // 他机共享单
    [InlineData(false, null, null, true, false, false)]                                            // 他机共享单但本机离线
    [InlineData(false, null, HeldClaimStatus.Active, true, true, false)]
    public void CanRecall_mirrors_history_page_rules(
        bool hasLocalCopy,
        SharedHeldOrderPublicationStatus? publicationStatus,
        HeldClaimStatus? claimStatus,
        bool serverPending,
        bool isOnline,
        bool expected)
    {
        var holdGuid = Guid.NewGuid();
        var publication = publicationStatus is { } status ? Publication(holdGuid, status) : null;

        var actual = HeldOrderTodayCountService.CanRecall(
            hasLocalCopy,
            publication,
            claimStatus,
            serverPending,
            isOnline);

        Assert.Equal(expected, actual);
    }

    // ───────────── 构造与假对象 ─────────────

    private static HeldOrderTodayCountService CreateService(
        FakeSuspendedOrderService suspended,
        ISharedHeldOrderRepository? repository = null,
        ISharedHeldOrderApiClient? api = null,
        FakeTimeProvider? clock = null,
        HeldOrderTodayCountOptions? options = null)
    {
        return new HeldOrderTodayCountService(
            suspended,
            repository,
            api,
            clock ?? CreateClock(Local(2026, 10, 15, 14, 0)),
            options);
    }

    private static FakeTimeProvider CreateClock(DateTimeOffset now)
    {
        var clock = new FakeTimeProvider(now);
        clock.SetLocalTimeZone(StoreTimeZone);
        return clock;
    }

    /// <summary>以门店时区（UTC+10）表示的某一刻。</summary>
    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, StoreOffset);

    private static PosSessionState Session(bool isOnline) =>
        new("HB POS", "S01", "Main Branch", "POS-01", "C001", "Alice", isOnline, 0);

    private static SuspendedOrderSummary LocalSummary(
        Guid guid,
        DateTimeOffset suspendedAt,
        string deviceCode = "POS-01") =>
        new(
            guid,
            "S01",
            deviceCode,
            "Alice",
            suspendedAt,
            10m,
            0m,
            10m,
            2,
            SuspendedOrderStatus.Pending);

    private static SuspendedOrder LocalOrder(Guid guid, SuspendedOrderStatus status) =>
        new(
            guid,
            "S01",
            "POS-01",
            "C001",
            "Alice",
            Local(2026, 10, 15, 9, 0),
            10m,
            0m,
            10m,
            status,
            []);

    private static SharedHeldOrderListItemDto RemoteItem(
        Guid guid,
        DateTimeOffset heldAt,
        string deviceCode = "POS-01",
        string storeCode = "S01") =>
        new(
            guid,
            storeCode,
            deviceCode,
            "C001",
            "Alice",
            heldAt.ToUniversalTime(),
            heldAt.ToUniversalTime(),
            2,
            1000,
            0,
            1000,
            1L);

    private static SharedHeldOrderPublication Publication(
        Guid holdGuid,
        SharedHeldOrderPublicationStatus status,
        string? errorCode = null) =>
        new(
            holdGuid,
            "S01",
            "POS-01",
            status,
            Revision: 1,
            RetryCount: 0,
            errorCode,
            ErrorMessage: null,
            PayloadCiphertext: null,
            HeldAtIso: "2026-10-15T00:00:00.000Z",
            CreatedAtIso: "2026-10-15T00:00:00.000Z",
            UpdatedAtIso: "2026-10-15T00:00:00.000Z");

    private static SharedHeldOrderClaimRecovery Recovery(
        Guid claimId,
        Guid holdGuid,
        HeldClaimStatus status) =>
        new(
            claimId,
            holdGuid,
            "S01",
            "POS-01",
            SharedHeldOrderClaimSource.RemoteClaim,
            status,
            "prepare-1",
            status == HeldClaimStatus.Prepared ? null : "activate-1",
            null,
            SampleCanonical(),
            null,
            null,
            null,
            null,
            "2026-10-15T00:00:00.000Z",
            "2026-10-15T00:00:01.000Z");

    /// <summary>固定返回一组远端挂单，并统计 ListPending 被调用的次数。</summary>
    private static CountingApiClient ApiReturning(params SharedHeldOrderListItemDto[] items) => new(items);

    private sealed class CountingApiClient : ISharedHeldOrderApiClient
    {
        private readonly IReadOnlyList<SharedHeldOrderListItemDto> _items;

        public CountingApiClient(IReadOnlyList<SharedHeldOrderListItemDto> items)
        {
            _items = items;
        }

        public int ListCalls { get; private set; }

        public Task<IReadOnlyList<SharedHeldOrderListItemDto>> ListPendingAsync(
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(_items);
        }

        public Task<SharedHeldOrderCapabilitiesResponse> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SharedHeldOrderPublishResponse> PublishAsync(
            SharedHeldOrderPublishRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SharedHeldOrderCancelResponse> CancelAsync(Guid holdGuid, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SharedHeldOrderClaimPrepareResponse> PrepareAsync(
            Guid holdGuid,
            SharedHeldOrderClaimPrepareRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SharedHeldOrderClaimDto> ActivateAsync(
            Guid holdGuid,
            Guid claimGuid,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SharedHeldOrderClaimDto> ReleaseAsync(
            Guid holdGuid,
            Guid claimGuid,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SharedHeldOrderClaimDto> ForceReleaseAsync(
            Guid holdGuid,
            Guid claimGuid,
            SharedHeldOrderForceReleaseRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SharedHeldOrderRecoveryClaimDto>> ClaimsMineAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSuspendedOrderService : ISuspendedOrderService
    {
        public IReadOnlyList<SuspendedOrderSummary> PendingOrders { get; set; } = [];

        public Dictionary<Guid, SuspendedOrder> Orders { get; } = [];

        public Exception? Failure { get; init; }

        public Task<IReadOnlyList<SuspendedOrderSummary>> GetPendingOrdersAsync(
            string storeCode,
            string? deviceCode = null,
            string? keyword = null,
            int take = 100,
            CancellationToken cancellationToken = default)
        {
            return Failure is not null
                ? Task.FromException<IReadOnlyList<SuspendedOrderSummary>>(Failure)
                : Task.FromResult(PendingOrders);
        }

        public Task<SuspendedOrder?> GetOrderAsync(
            Guid suspendedOrderGuid,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Orders.GetValueOrDefault(suspendedOrderGuid));
        }

        public Task<SuspendedOrder> SuspendCurrentOrderAsync(
            PosSessionState session,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SuspendedOrder> RecallOrderAsync(
            Guid suspendedOrderGuid,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>只实现统计服务会读的 GetPublicationAsync / FindRecoverableClaimsAsync，其余一律不应被触达。</summary>
    private sealed class FakeSharedHeldOrderRepository : ISharedHeldOrderRepository
    {
        public Dictionary<Guid, SharedHeldOrderPublication> Publications { get; } = [];

        public IReadOnlyList<SharedHeldOrderClaimRecovery> Claims { get; set; } = [];

        public Task<SharedHeldOrderPublication?> GetPublicationAsync(
            Guid localHoldGuid,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Publications.GetValueOrDefault(localHoldGuid));

        public Task<IReadOnlyList<SharedHeldOrderClaimRecovery>> FindRecoverableClaimsAsync(
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Claims);

        public Task<IReadOnlyList<SuspendedOrder>> ListLegacyOrdersNeedingEvaluationAsync(
            string storeCode,
            string? deviceCode = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> UpsertPublicationAsync(
            Guid localHoldGuid,
            string storeCode,
            string deviceCode,
            SharedHeldOrderPublicationStatus status,
            byte[]? payloadCiphertext,
            string heldAtIso,
            string createdAtIso,
            string updatedAtIso,
            string? errorCode = null,
            string? errorMessage = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SharedHeldOrderPublication>> ListDuePublicationsAsync(
            string nowIso,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SharedHeldOrderShareRequestResult> TryRequestShareAsync(
            Guid holdGuid,
            string storeCode,
            string deviceCode,
            string requestedAtIso,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryAdvancePublicationAsync(
            Guid localHoldGuid,
            SharedHeldOrderPublicationStatus expectedStatus,
            int expectedRevision,
            SharedHeldOrderPublicationStatus newStatus,
            string updatedAtIso,
            string? errorCode = null,
            string? errorMessage = null,
            string? lastAttemptAtIso = null,
            string? nextAttemptAtIso = null,
            long? remoteRevision = null,
            string? remoteUpdatedAtIso = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SharedHeldOrderDeleteStage?> TryStageDeletePendingAsync(
            Guid holdGuid,
            string storeCode,
            string deviceCode,
            string updatedAtIso,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryCompleteDeletePendingAsync(
            Guid holdGuid,
            string storeCode,
            string deviceCode,
            string completedAtIso,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryStagePendingPublishAsync(
            Guid localHoldGuid,
            int expectedRevision,
            SharedHeldOrderCanonicalPayload payload,
            string updatedAtIso,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryBlockPublicationAsync(
            Guid localHoldGuid,
            int expectedRevision,
            string errorCode,
            string errorMessage,
            string updatedAtIso,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SharedHeldOrderCanonicalPayload?> GetPublicationPayloadAsync(
            Guid localHoldGuid,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TrySavePreparedClaimAsync(
            SharedHeldOrderClaimDraft draft,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryActivateClaimAsync(
            Guid claimId,
            string prepareIdempotencyKey,
            string activateIdempotencyKey,
            long? serverRevision,
            string updatedAtIso,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryBindOrderAsync(
            Guid claimId,
            string activateIdempotencyKey,
            string boundOrderGuid,
            string updatedAtIso,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryCompleteClaimAsync(
            Guid claimId,
            string activateIdempotencyKey,
            string releaseIdempotencyKey,
            string updatedAtIso,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryReleaseClaimAsync(
            Guid claimId,
            string releaseIdempotencyKey,
            HeldClaimStatus expectedStatus,
            string updatedAtIso,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryExpirePreparedRemoteClaimAsync(
            Guid claimId,
            string releaseIdempotencyKey,
            string nowIso,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryForceReleaseClaimAsync(
            Guid claimId,
            string releaseIdempotencyKey,
            HeldClaimStatus expectedStatus,
            string updatedAtIso,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TrySupersedeClaimAsync(
            Guid claimId,
            string supersedeIdempotencyKey,
            HeldClaimStatus expectedStatus,
            string updatedAtIso,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SharedHeldOrderClaimRecord?> GetClaimAsync(
            Guid claimId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
