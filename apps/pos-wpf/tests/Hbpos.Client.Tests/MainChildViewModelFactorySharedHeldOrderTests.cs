using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.HeldOrders;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using static Hbpos.Client.Tests.SharedHeldOrderClientTestSupport;

namespace Hbpos.Client.Tests;

/// <summary>
/// 工厂注入收口：MainChildViewModelFactory 必须把 shared coordinator/api/repository
/// 注入 TransactionHistoryViewModel（Held 列表、claim badge、force release 都走注入实例）。
/// </summary>
public sealed class MainChildViewModelFactorySharedHeldOrderTests
{
    [Fact]
    public async Task Factory_injects_shared_held_order_coordinator_api_and_repository()
    {
        await using var scope = await CreateRepositoryScopeAsync();
        var holdGuid = Guid.NewGuid();
        var claimGuid = Guid.NewGuid();
        var session = Session();
        var draft = new SharedHeldOrderClaimDraft(
            claimGuid,
            holdGuid,
            session.StoreCode,
            session.DeviceCode,
            SharedHeldOrderClaimSource.OfflineOrigin,
            $"prepare:{claimGuid:D}",
            SampleCanonical(),
            "2026-07-01T09:00:00.000Z",
            "2026-07-01T09:05:00.000Z");
        Assert.True(await scope.Repository.TrySavePreparedClaimAsync(draft));
        Assert.True(await scope.Repository.TryActivateClaimAsync(
            claimGuid,
            $"prepare:{claimGuid:D}",
            $"activate:{claimGuid:D}",
            serverRevision: 1L,
            "2026-07-01T09:05:00.000Z"));

        var remoteOnlyHold = Guid.NewGuid();
        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>(
            [
                HeldItem(holdGuid, 1100, 100, 1000, 2),
                HeldItem(remoteOnlyHold, 5000, 0, 5000, 3)
            ])
        };
        var coordinator = new RecordingCoordinator();
        var cart = new PosCartService();
        var factory = new MainChildViewModelFactory(
            deviceRegistrationWorkflowService: null!,
            receiptQueryService: new EmptyReceiptQueryService(),
            suspendedOrderService: new StubSuspendedOrderService
            {
                PendingOrders =
                [
                    HeldSummary(holdGuid, "POS-01", 10m, 1m, 9m, 2)
                ]
            },
            remoteOrderHistoryService: new EmptyRemoteOrderHistoryService(),
            receiptTextFormatter: null!,
            receiptPrinterSettingsStore: null,
            installmentOrderService: null!,
            localization: new TestLocalization(),
            cardTerminalClient: null,
            priceIndex: new LocalSellableItemIndex(),
            cart: cart,
            catalogRepository: null!,
            specialProductService: null!,
            specialProductsWorkflowService: null!,
            receiptReturnsWorkflowService: null!,
            cashPaymentWorkflowService: null!,
            cardTerminalSetupService: null,
            rawScannerService: null!,
            dailyCloseService: null!,
            dailyClosePrintService: null!,
            sharedHeldOrderCoordinator: coordinator,
            sharedHeldOrderApiClient: api,
            sharedHeldOrderRepository: scope.Repository);

        var viewModel = factory.CreateTransactionHistoryViewModel(
            session,
            onSuspendedOrderRecalledAsync: () => Task.CompletedTask,
            showPos: () => { },
            printSelectedHistoryReceiptAsync: _ => Task.CompletedTask);

        viewModel.DateFrom = new DateTime(2026, 7, 1);
        viewModel.DateTo = new DateTime(2026, 7, 1);
        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();

        // api 注入生效：远端列表参与合并。
        Assert.Equal(2, viewModel.Orders.Count);
        Assert.Contains(viewModel.Orders, order => order.OrderGuid == remoteOnlyHold);
        // repository 注入生效：本地 Active claim 显示为可取回/可强制释放。
        var claimed = viewModel.Orders.Single(order => order.OrderGuid == holdGuid);
        Assert.Equal(claimGuid, claimed.HeldClaimId);
        Assert.True(claimed.CanForceRelease);

        // coordinator 注入生效：强制释放走 durable 方法而非直接调 API。
        viewModel.ForceReleaseHeldOrderCommand.Execute(claimed);
        viewModel.ForceReleaseReason = "主管确认";
        await viewModel.ConfirmForceReleaseCommand.ExecuteAsync(null);

        var release = Assert.Single(coordinator.ForceReleases);
        Assert.Equal(holdGuid, release.HoldGuid);
        Assert.Equal(claimGuid, release.ClaimGuid);
        Assert.Equal("主管确认", release.Reason);

        // POS 清空接线也必须走同一 coordinator 的 owner-release，不能直接遗弃 Active claim。
        cart.RestoreSharedSaleSnapshot(new PosCartSnapshot(
        [
            new PosCartLineSnapshot(
                "S001", "P-1", null, "Product 1", "CODE-1", null, null,
                1m, 10m, 0m, null, PriceSourceKind.ProductBase, "Product Base")
        ], claimGuid));
        coordinator.OwnerReleaseHandler = (actualClaimId, _) =>
        {
            Assert.True(cart.ClearSharedHeldOrderClaim(actualClaimId));
            return Task.CompletedTask;
        };
        using var pos = factory.CreatePosTerminalViewModel(session, onOpenPayment: null);
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(pos.ClearCartCommand).ExecuteAsync(null);
        Assert.Equal(claimGuid, Assert.Single(coordinator.OwnerReleases).ClaimGuid);
        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public async Task Factory_injects_share_worker_so_share_command_persists_and_runs_worker()
    {
        await using var scope = await CreateRepositoryScopeAsync();
        var session = Session();
        var holdGuid = Guid.NewGuid();
        var suspendedRepository = new SuspendedOrderRepository(scope.Store);
        await suspendedRepository.SaveAsync(new SuspendedOrder(
            holdGuid,
            session.StoreCode,
            session.DeviceCode,
            "C001",
            "Alice",
            new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
            10m,
            0m,
            10m,
            SuspendedOrderStatus.Pending,
            [
                new SuspendedOrderLine(
                    Guid.NewGuid(),
                    holdGuid,
                    session.StoreCode,
                    "P-1",
                    null,
                    "Product 1",
                    "CODE-1",
                    null,
                    null,
                    1m,
                    10m,
                    0m,
                    null,
                    10m,
                    PriceSourceKind.ProductBase,
                    "Product Base")
            ]));

        var api = new StubSharedHeldOrderApiClient
        {
            ListPending = _ => Task.FromResult<IReadOnlyList<SharedHeldOrderListItemDto>>([]),
            Capabilities = _ => Task.FromResult(new SharedHeldOrderCapabilitiesResponse(
                Enabled: false,
                PayloadVersion: 1,
                PreparedTtlSeconds: 120,
                ForceReleaseSupported: true))
        };
        var worker = new SharedHeldOrderPublicationWorker(
            scope.Repository,
            new SharedHeldOrderMapper(),
            api,
            new SharedHeldOrderPublicationGate());
        var factory = new MainChildViewModelFactory(
            deviceRegistrationWorkflowService: null!,
            receiptQueryService: new EmptyReceiptQueryService(),
            suspendedOrderService: new StubSuspendedOrderService
            {
                PendingOrders =
                [
                    HeldSummary(holdGuid, "POS-01", 10m, 0m, 10m, 1)
                ]
            },
            remoteOrderHistoryService: new EmptyRemoteOrderHistoryService(),
            receiptTextFormatter: null!,
            receiptPrinterSettingsStore: null,
            installmentOrderService: null!,
            localization: new TestLocalization(),
            cardTerminalClient: null,
            priceIndex: new LocalSellableItemIndex(),
            cart: new PosCartService(),
            catalogRepository: null!,
            specialProductService: null!,
            specialProductsWorkflowService: null!,
            receiptReturnsWorkflowService: null!,
            cashPaymentWorkflowService: null!,
            cardTerminalSetupService: null,
            rawScannerService: null!,
            dailyCloseService: null!,
            dailyClosePrintService: null!,
            sharedHeldOrderCoordinator: new RecordingCoordinator(),
            sharedHeldOrderApiClient: api,
            sharedHeldOrderRepository: scope.Repository,
            sharedHeldOrderPublicationWorker: worker);

        var viewModel = factory.CreateTransactionHistoryViewModel(
            session,
            onSuspendedOrderRecalledAsync: () => Task.CompletedTask,
            showPos: () => { },
            printSelectedHistoryReceiptAsync: _ => Task.CompletedTask);

        viewModel.DateFrom = new DateTime(2026, 7, 1);
        viewModel.DateTo = new DateTime(2026, 7, 1);
        viewModel.IsHeldSourceSelected = true;
        await viewModel.LoadAsync();
        var row = Assert.Single(viewModel.Orders);
        Assert.True(row.CanShare);

        // 点击共享：持久化请求后立即调用注入的 worker（能力 disabled 时只退避不失败）。
        await viewModel.ShareHeldOrderCommand.ExecuteAsync(row);

        var publication = await scope.Repository.GetPublicationAsync(holdGuid);
        Assert.NotNull(publication);
        Assert.NotNull(publication!.ShareRequestedAtIso);
        Assert.Equal(SharedHeldOrderPublicationStatus.PendingPublish, publication.Status);
        var refreshed = Assert.Single(viewModel.Orders);
        Assert.False(refreshed.CanShare);
        Assert.Equal("Awaiting share", refreshed.ShareStatusLabel);
    }

    [Fact]
    public async Task Factory_injects_held_order_today_count_service_into_pos_terminal()
    {
        var counter = new FakeHeldOrderTodayCountService { Count = 3 };
        var factory = new MainChildViewModelFactory(
            deviceRegistrationWorkflowService: null!,
            receiptQueryService: new EmptyReceiptQueryService(),
            suspendedOrderService: new StubSuspendedOrderService(),
            remoteOrderHistoryService: new EmptyRemoteOrderHistoryService(),
            receiptTextFormatter: null!,
            receiptPrinterSettingsStore: null,
            installmentOrderService: null!,
            localization: new TestLocalization(),
            cardTerminalClient: null,
            priceIndex: new LocalSellableItemIndex(),
            cart: new PosCartService(),
            catalogRepository: null!,
            specialProductService: null!,
            specialProductsWorkflowService: null!,
            receiptReturnsWorkflowService: null!,
            cashPaymentWorkflowService: null!,
            cardTerminalSetupService: null,
            rawScannerService: null!,
            dailyCloseService: null!,
            dailyClosePrintService: null!,
            heldOrderTodayCountService: counter);

        using var pos = factory.CreatePosTerminalViewModel(Session(), onOpenPayment: null);
        pos.OnScreenShown();
        await pos.LastHeldOrderCountRefreshTask!;

        // 首页 VM 拿到的就是工厂注入的统计服务：进入首页即刷新出角标数字。
        Assert.Equal(3, pos.HeldOrderTodayCount);
        Assert.Equal("3", pos.HeldOrderBadgeText);
        Assert.Equal(1, counter.CallCount);
    }

    private static SuspendedOrderSummary HeldSummary(
        Guid guid,
        string deviceCode,
        decimal total,
        decimal discount,
        decimal actual,
        int lineCount)
    {
        return new SuspendedOrderSummary(
            guid,
            "S001",
            deviceCode,
            "Alice",
            new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
            total,
            discount,
            actual,
            lineCount,
            SuspendedOrderStatus.Pending);
    }

    private static SharedHeldOrderListItemDto HeldItem(
        Guid guid,
        long totalCents,
        long discountCents,
        long actualCents,
        int lineCount)
    {
        return new SharedHeldOrderListItemDto(
            guid,
            "S001",
            "POS-01",
            "C001",
            "Alice",
            new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 1, 9, 5, 0, TimeSpan.Zero),
            lineCount,
            totalCents,
            discountCents,
            actualCents,
            1L);
    }

    private sealed class RecordingCoordinator : ISharedHeldOrderCoordinator
    {
        public List<(Guid ClaimGuid, PosSessionState Session)> OwnerReleases { get; } = [];

        public Func<Guid, PosSessionState, Task>? OwnerReleaseHandler { get; set; }

        public List<(Guid HoldGuid, Guid ClaimGuid, string Reason, PosSessionState Session)> ForceReleases { get; } = [];

        public Task<SharedHeldOrderTakeResult> TakeRemoteHoldAsync(
            Guid holdGuid,
            PosSessionState session,
            Guid? claimGuid = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SharedHeldOrderTakeResult> RecallLocalPublicationAsync(
            Guid localHoldGuid,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SharedHeldOrderReconcileResult> ReconcileClaimsAsync(
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SharedHeldOrderLocalRecoveryResult> RecoverLocalClaimsAsync(
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ReleaseActiveClaimAsync(
            Guid claimGuid,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            OwnerReleases.Add((claimGuid, session));
            return OwnerReleaseHandler?.Invoke(claimGuid, session) ?? Task.CompletedTask;
        }

        public Task ForceReleaseAsync(
            Guid holdGuid,
            Guid claimGuid,
            string reason,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            ForceReleases.Add((holdGuid, claimGuid, reason, session));
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyReceiptQueryService : IReceiptQueryService
    {
        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<ReceiptDetails?> GetReceiptAsync(
            Guid orderGuid,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReceiptDetails?>(null);
        }

        public Task<ReceiptDetails?> GetLatestReceiptAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReceiptDetails?>(null);
        }
    }

    private sealed class StubSuspendedOrderService : ISuspendedOrderService
    {
        public IReadOnlyList<SuspendedOrderSummary> PendingOrders { get; set; } = [];

        public Task<SuspendedOrder> SuspendCurrentOrderAsync(
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<SuspendedOrderSummary>> GetPendingOrdersAsync(
            string storeCode,
            string? deviceCode = null,
            string? keyword = null,
            int take = 100,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PendingOrders);
        }

        public Task<SuspendedOrder?> GetOrderAsync(
            Guid suspendedOrderGuid,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SuspendedOrder?>(null);
        }

        public Task<SuspendedOrder> RecallOrderAsync(
            Guid suspendedOrderGuid,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class EmptyRemoteOrderHistoryService : IRemoteOrderHistoryService
    {
        public Task<RemoteOrderHistoryResult> QueryAsync(
            RemoteOrderHistoryQuery query,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RemoteOrderHistoryResult([]));
        }

        public Task<ReceiptDetails?> GetDetailsAsync(
            Guid orderGuid,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReceiptDetails?>(null);
        }

        public Task<OrderReturnContextDto?> GetReturnContextAsync(
            Guid orderGuid,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<OrderReturnContextDto?>(null);
        }

        public Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(
            OrderReturnRecordCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TestLocalization : ILocalizationService
    {
#pragma warning disable CS0067
        public event PropertyChangedEventHandler? PropertyChanged;

        public event EventHandler? CultureChanged;
#pragma warning restore CS0067

        public IReadOnlyList<CultureInfo> AvailableCultures { get; } =
            [new CultureInfo("en-US"), new CultureInfo("zh-CN")];

        public CultureInfo CurrentCulture { get; } = new CultureInfo("en-US");

        public void SetCulture(string cultureName)
        {
        }

        public void SetCulture(CultureInfo culture)
        {
        }

        public Task SetCultureAsync(
            string cultureName,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public string T(string key)
        {
            return key == "history.held.sharePending" ? "Awaiting share" : key;
        }
    }
}
