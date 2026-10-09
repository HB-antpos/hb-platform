using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.HeldOrders;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

public sealed class OrderSyncServiceTests
{
    // 连接级 MoreSettings.IsWithNoLockQuery = true 会给所有 Queryable 自动加 WITH(NOLOCK)，
    // 而表提示会覆盖会话隔离级别。订单幂等判断一旦脏读到别的事务尚未提交的订单行，
    // 就会返回 AlreadySynced，客户端据此标记 Synced，对方回滚后这笔销售永久丢失
    // （SyncQueue 只重试 Pending/Failed，且全仓没有上传后对账）。
    // 以下两个用例直接捕获真实仓储生成的 SQL，确保这两处读永远不会退回 NOLOCK。
    [Fact]
    public async Task ExistsAsync_does_not_dirty_read_the_order_table()
    {
        var (repository, capturedSql) = CreateRepositoryCapturingSql();

        try
        {
            await repository.ExistsAsync(Guid.NewGuid(), CancellationToken.None);
        }
        catch
        {
            // 测试用的是不可达的连接串，只关心 SQL 文本，不关心执行结果。
        }

        var sql = Assert.Single(capturedSql);
        Assert.DoesNotContain("NOLOCK", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("READCOMMITTED", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Order_idempotency_read_inside_the_insert_transaction_takes_update_and_range_locks()
    {
        // InsertAsync 需要真实事务才能执行到该语句，这里退一步校验源码契约：
        // 事务内的幂等判断必须带 UPDLOCK（串行化同 OrderGuid 的并发判断）
        // 与 HOLDLOCK（范围锁，防止判断与插入之间插入同 OrderGuid 的行）。
        var source = ReadOrderSyncServiceSource();
        var marker = source.IndexOf("AnyAsync(x => x.OrderGuid == plan.Order.OrderGuid", StringComparison.Ordinal);
        Assert.True(marker > 0, "未找到事务内的订单幂等判断。");

        var windowStart = Math.Max(0, marker - 400);
        var window = source[windowStart..marker];
        Assert.Contains("UPDLOCK", window, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HOLDLOCK", window, StringComparison.OrdinalIgnoreCase);
    }

    private static (SqlSugarOrderRepository Repository, List<string> CapturedSql) CreateRepositoryCapturingSql()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MainConnection"] =
                    "Server=127.0.0.1,1;Database=hb_sql_probe;User Id=sa;Password=x;Encrypt=False;TrustServerCertificate=True;Connect Timeout=1",
                ["ConnectionStrings:PosmConnection"] =
                    "Server=127.0.0.1,1;Database=hb_sql_probe;User Id=sa;Password=x;Encrypt=False;TrustServerCertificate=True;Connect Timeout=1"
            })
            .Build();

        var context = new HbposSqlSugarContext(
            configuration,
            NullLogger<HbposSqlSugarContext>.Instance);
        var capturedSql = new List<string>();
        context.PosmDb.Aop.OnLogExecuting = (sql, _) => capturedSql.Add(sql);

        return (
            new SqlSugarOrderRepository(context, NullLogger<SqlSugarOrderRepository>.Instance),
            capturedSql);
    }

    private static string ReadOrderSyncServiceSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "src", "Hbpos.Api", "Services", "OrderSyncService.cs")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(
            Path.Combine(directory!.FullName, "src", "Hbpos.Api", "Services", "OrderSyncService.cs"));
    }

    [Fact]
    public async Task SyncAsync_ReturnsAlreadySyncedWhenOrderExists()
    {
        var orderGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: true);
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(CreateRequest(orderGuid), CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.True(response.AlreadySynced);
        Assert.Equal("AlreadySynced", response.Message);
        Assert.False(repository.InsertCalled);
    }

    [Fact]
    public async Task SyncAsync_verifies_card_payments_only_after_the_order_is_inserted()
    {
        var repository = new FakeOrderRepository(exists: false);
        var insertedBeforeVerify = false;
        var verifier = new RecordingCardTenderVerifier(() => insertedBeforeVerify = repository.InsertCalled);
        var service = new OrderSyncService(
            repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver(),
            cardTenderVerifier: verifier);
        var request = CreateRequest(
            Guid.NewGuid(),
            payments: [new PaymentSyncDto(Guid.NewGuid(), PaymentMethodKind.Card, 9.99m, "ANZ:1")]);

        var response = await service.SyncAsync(request, CancellationToken.None);

        Assert.Equal("Synced", response.Message);
        Assert.Same(request, Assert.Single(verifier.Requests));
        Assert.True(insertedBeforeVerify);
    }

    [Fact]
    public async Task SyncAsync_does_not_verify_orders_without_card_payments_or_already_synced_orders()
    {
        var verifier = new RecordingCardTenderVerifier();
        var cashService = new OrderSyncService(
            new FakeOrderRepository(exists: false), new OrderSyncPlanner(), new FakeReservationService(),
            new StubStoreTimeZoneResolver(), cardTenderVerifier: verifier);
        var syncedService = new OrderSyncService(
            new FakeOrderRepository(exists: true), new OrderSyncPlanner(), new FakeReservationService(),
            new StubStoreTimeZoneResolver(), cardTenderVerifier: verifier);

        await cashService.SyncAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None);
        await syncedService.SyncAsync(
            CreateRequest(Guid.NewGuid(), payments: [new PaymentSyncDto(Guid.NewGuid(), PaymentMethodKind.Card, 9.99m, "ANZ:1")]),
            CancellationToken.None);

        Assert.Empty(verifier.Requests);
    }

    [Fact]
    public async Task SyncAsync_still_reports_synced_when_card_verification_throws()
    {
        // 订单已提交，核对只是旁路；任何异常都不能把已入库订单变成 500 让客户端无限重试。
        var verifier = new RecordingCardTenderVerifier(() => throw new InvalidOperationException("boom"));
        var service = new OrderSyncService(
            new FakeOrderRepository(exists: false), new OrderSyncPlanner(), new FakeReservationService(),
            new StubStoreTimeZoneResolver(), cardTenderVerifier: verifier);

        var response = await service.SyncAsync(
            CreateRequest(Guid.NewGuid(), payments: [new PaymentSyncDto(Guid.NewGuid(), PaymentMethodKind.Card, 9.99m, "ANZ:1")]),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal("Synced", response.Message);
    }

    [Fact]
    public async Task SyncAsync_DoesNotConsumeVoucherReservationWhenOrderAlreadySyncedDuringInsert()
    {
        var orderGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            InsertResult = false
        };
        var reservationService = new FakeReservationService();
        reservationService.Add(new StoreVoucherReservation("token-1", "S01", "V001", 5m, DateTimeOffset.UtcNow.AddMinutes(5)));
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), reservationService, new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                payments:
                [
                    new PaymentSyncDto(Guid.NewGuid(), PaymentMethodKind.Voucher, 5m, "V001", "token-1")
                ]),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.True(response.AlreadySynced);
        Assert.Equal("AlreadySynced", response.Message);
        Assert.True(repository.InsertCalled);
        Assert.Empty(reservationService.ConsumedTokens);
        Assert.NotNull(await reservationService.GetAsync("token-1", CancellationToken.None));
    }

    [Fact]
    public async Task SyncAsync_InsertsSnapshotWhenOrderDoesNotExist()
    {
        var orderGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false);
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(CreateRequest(orderGuid), CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.False(response.AlreadySynced);
        Assert.True(repository.InsertCalled);
        Assert.Equal(orderGuid.ToString("D"), repository.LastPlan?.Order.OrderGuid);
        Assert.Empty(repository.LastVoucherRedemptions);
        Assert.Equal(9.99m, repository.LastPlan?.Lines.Single().Price);
        Assert.Equal("SOURCE-GUID-01", repository.LastPlan?.Lines.Single().ReferenceGUID);
        Assert.Equal("priceSource=1", repository.LastPlan?.Lines.Single().Remark);
        Assert.Equal("POS_S01_POS01", repository.LastPlan?.Order.CreatedBy);
        Assert.Equal("POS_S01_POS01", repository.LastPlan?.Lines.Single().CreatedBy);
        Assert.Equal("POS_S01_POS01", repository.LastPlan?.Payments.Single().CreatedBy);
    }

    [Fact]
    public async Task SyncAsync_ForwardsReturnRecordsInPlan()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderGuid = Guid.NewGuid();
        var originalDetailGuid = Guid.NewGuid();
        var returnLineGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(originalOrderGuid, originalDetailGuid, quantity: 1m, actualAmount: 9.99m)
            ]
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                lines:
                [
                    new OrderLineSyncDto(
                        returnLineGuid,
                        "P02",
                        "REF-RETURN",
                        "Returned Apple",
                        "BAR02",
                        -1m,
                        9.99m,
                        0m,
                        -9.99m,
                        PriceSourceKind.StoreRetailPrice,
                        Kind: OrderLineKind.Return,
                        ReturnSourceKey: "RETURN-SOURCE",
                        OriginalOrderGuid: originalOrderGuid,
                        OriginalOrderDetailGuid: originalDetailGuid)
                ]),
            CancellationToken.None);

        Assert.True(response.Accepted);
        var record = Assert.Single(repository.LastPlan!.ReturnRecords);
        Assert.Equal(returnLineGuid.ToString("D"), record.ReturnDetailGuid);
        Assert.Equal(orderGuid.ToString("D"), record.ReturnOrderGuid);
        Assert.Equal(originalOrderGuid.ToString("D"), record.OriginalOrderGuid);
        Assert.Equal(originalDetailGuid.ToString("D"), record.OriginalOrderDetailGuid);
        Assert.Equal(1m, record.ReturnQuantity);
        Assert.Equal(9.99m, record.ReturnAmount);
        Assert.Equal("C01", record.StaffCode);
        Assert.Equal("POS_S01_POS01", record.CreatedBy);
        Assert.Equal("POS_S01_POS01", record.UpdatedBy);
        Assert.Empty(repository.LastPlan.Lines);
        Assert.Equal(1, repository.AtomicReturnValidationCallCount);
    }

    [Fact]
    public async Task SyncAsync_RejectsReturnLineExceedingOriginalRemaining()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderGuid = Guid.NewGuid();
        var originalDetailGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(originalOrderGuid, originalDetailGuid, quantity: 1m, actualAmount: 9.99m)
            ]
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(
            CreateRequest(
                orderGuid,
                lines:
                [
                    CreateReturnLine(
                        originalOrderGuid,
                        originalDetailGuid,
                        quantity: -2m,
                        actualAmount: -19.98m)
                ]),
            CancellationToken.None));

        Assert.Equal("Return quantity exceeds the available original order quantity.", ex.Message);
        Assert.True(repository.InsertCalled);
        Assert.Equal(1, repository.AtomicReturnValidationCallCount);
        Assert.Equal(0, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_RejectsReturnLineWhenExistingRecordsExhaustCapacity()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderGuid = Guid.NewGuid();
        var originalDetailGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(originalOrderGuid, originalDetailGuid, quantity: 1m, actualAmount: 9.99m)
            ],
            ExistingReturnRecords =
            [
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = Guid.NewGuid().ToString("D"),
                    OriginalOrderGuid = originalOrderGuid.ToString("D"),
                    OriginalOrderDetailGuid = originalDetailGuid.ToString("D"),
                    ReturnQuantity = 1m,
                    ReturnAmount = 9.99m
                }
            ]
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(
            CreateRequest(
                orderGuid,
                lines:
                [
                    CreateReturnLine(
                        originalOrderGuid,
                        originalDetailGuid,
                        quantity: -1m,
                        actualAmount: -9.99m)
                ]),
            CancellationToken.None));

        Assert.Equal("Return quantity exceeds the available original order quantity.", ex.Message);
        Assert.True(repository.InsertCalled);
        Assert.Equal(1, repository.AtomicReturnValidationCallCount);
        Assert.Equal(0, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_SkipsDuplicateReturnRecordsWhenReturnOrderAlreadyExists()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderGuid = Guid.NewGuid();
        var originalDetailGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            ExistingReturnRecords =
            [
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = orderGuid.ToString("D"),
                    OriginalOrderGuid = originalOrderGuid.ToString("D"),
                    OriginalOrderDetailGuid = originalDetailGuid.ToString("D"),
                    ReturnQuantity = 1m,
                    ReturnAmount = 9.99m
                }
            ]
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                lines:
                [
                    CreateReturnLine(
                        originalOrderGuid,
                        originalDetailGuid,
                        quantity: -1m,
                        actualAmount: -9.99m)
                ]),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.False(response.AlreadySynced);
        Assert.True(repository.InsertCalled);
        Assert.Single(repository.LastPlan!.ReturnRecords);
        Assert.Equal(1, repository.AtomicReturnValidationCallCount);
        Assert.Equal(0, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_RejectsCardRefundWithoutOriginalCardCapacity()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderGuid = Guid.NewGuid();
        var originalDetailGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(originalOrderGuid, originalDetailGuid, quantity: 1m, actualAmount: 9.99m)
            ]
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(
            CreateRequest(
                orderGuid,
                payments:
                [
                    new PaymentSyncDto(Guid.NewGuid(), PaymentMethodKind.Card, -9.99m, "SQRF:refund-1")
                ],
                lines:
                [
                    CreateReturnLine(
                        originalOrderGuid,
                        originalDetailGuid,
                        quantity: -1m,
                        actualAmount: -9.99m)
                ]),
            CancellationToken.None));

        Assert.Equal("Card refunds require an original card payment reference.", ex.Message);
        Assert.True(repository.InsertCalled);
        Assert.Equal(1, repository.AtomicReturnValidationCallCount);
        Assert.Equal(0, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_AllowsCardRefundWithinOriginalCardCapacity()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderGuid = Guid.NewGuid();
        var originalDetailGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(originalOrderGuid, originalDetailGuid, quantity: 1m, actualAmount: 9.99m)
            ],
            OriginalCardPaymentAmountsByReference = new Dictionary<string, decimal>
            {
                ["SQ:payment-1"] = 9.99m
            }
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                payments:
                [
                    new PaymentSyncDto(
                        Guid.NewGuid(),
                        PaymentMethodKind.Card,
                        -9.99m,
                        CardRefundReference.Format("SQRF:refund-1", "SQ:payment-1"))
                ],
                lines:
                [
                    CreateReturnLine(
                        originalOrderGuid,
                        originalDetailGuid,
                        quantity: -1m,
                        actualAmount: -9.99m)
                ]),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal(1, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_RejectsCardRefundExceedingMatchedOriginalCardReference()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderGuid = Guid.NewGuid();
        var originalDetailGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(originalOrderGuid, originalDetailGuid, quantity: 1m, actualAmount: 12m)
            ],
            OriginalCardPaymentAmountsByReference = new Dictionary<string, decimal>
            {
                ["SQ:card-1"] = 5m,
                ["SQ:card-2"] = 7m
            }
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(
            CreateRequest(
                orderGuid,
                payments:
                [
                    new PaymentSyncDto(
                        Guid.NewGuid(),
                        PaymentMethodKind.Card,
                        -7m,
                        CardRefundReference.Format("SQRF:refund-1", "SQ:card-1"))
                ],
                lines:
                [
                    CreateReturnLine(
                        originalOrderGuid,
                        originalDetailGuid,
                        quantity: -1m,
                        actualAmount: -12m)
                ]),
            CancellationToken.None));

        Assert.Equal("Card refund amount exceeds the available original card payment capacity.", ex.Message);
        Assert.Equal(0, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_RejectsCardRefundExceedingCurrentReturnAmountForOriginalCardOrder()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderA = Guid.NewGuid();
        var originalOrderB = Guid.NewGuid();
        var originalDetailA = Guid.NewGuid();
        var originalDetailB = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(originalOrderA, originalDetailA, quantity: 1m, actualAmount: 10m),
                CreateOriginalOrder(originalOrderB, originalDetailB, quantity: 1m, actualAmount: 90m)
            ],
            OriginalCardPaymentAmountsByReference = new Dictionary<string, decimal>
            {
                ["SQ:card-a"] = 100m,
                ["SQ:card-b"] = 90m
            },
            OriginalCardPaymentOrderGuidsByReference = new Dictionary<string, Guid>
            {
                ["SQ:card-a"] = originalOrderA,
                ["SQ:card-b"] = originalOrderB
            }
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(
            CreateRequest(
                orderGuid,
                payments:
                [
                    new PaymentSyncDto(
                        Guid.NewGuid(),
                        PaymentMethodKind.Card,
                        -100m,
                        CardRefundReference.Format("SQRF:refund-1", "SQ:card-a"))
                ],
                lines:
                [
                    CreateReturnLine(
                        originalOrderA,
                        originalDetailA,
                        quantity: -1m,
                        actualAmount: -10m),
                    CreateReturnLine(
                        originalOrderB,
                        originalDetailB,
                        quantity: -1m,
                        actualAmount: -90m)
                ]),
            CancellationToken.None));

        Assert.Equal("Card refund amount exceeds the return amount for the original card order.", ex.Message);
        Assert.Equal(0, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_RejectsCardRefundWhenExistingLegacyRefundSpansMultipleOriginalOrders()
    {
        var returnOrderGuid = Guid.NewGuid();
        var orderAGuid = Guid.NewGuid();
        var orderBGuid = Guid.NewGuid();
        var lineAGuid = Guid.NewGuid();
        var lineBGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(orderAGuid, lineAGuid, quantity: 2m, actualAmount: 5m)
            ],
            OriginalCardPaymentAmountsByReference = new Dictionary<string, decimal>
            {
                ["SQ:card-a"] = 5m
            },
            ExistingReturnRecords =
            [
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = returnOrderGuid.ToString("D"),
                    OriginalOrderGuid = orderAGuid.ToString("D"),
                    OriginalOrderDetailGuid = lineAGuid.ToString("D"),
                    ReturnQuantity = 1m,
                    ReturnAmount = 3m
                },
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = returnOrderGuid.ToString("D"),
                    OriginalOrderGuid = orderBGuid.ToString("D"),
                    OriginalOrderDetailGuid = lineBGuid.ToString("D"),
                    ReturnQuantity = 1m,
                    ReturnAmount = 3m
                }
            ],
            ExistingCardRefundsByReturnOrder = new Dictionary<Guid, IReadOnlyList<PaymentDetail>>
            {
                [returnOrderGuid] =
                [
                    new PaymentDetail
                    {
                        OrderGuid = returnOrderGuid.ToString("D"),
                        PaymentMethod = (int)PaymentMethodKind.Card,
                        Amount = -3m,
                        Reference = "SQRF:legacy"
                    }
                ]
            }
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(
            CreateRequest(
                Guid.NewGuid(),
                payments:
                [
                    new PaymentSyncDto(
                        Guid.NewGuid(),
                        PaymentMethodKind.Card,
                        -1m,
                        CardRefundReference.Format("SQRF:new", "SQ:card-a"))
                ],
                lines:
                [
                    CreateReturnLine(
                        orderAGuid,
                        lineAGuid,
                        quantity: -1m,
                        actualAmount: -1m)
                ]),
            CancellationToken.None));

        Assert.Equal("Card refund amount exceeds the available original card payment capacity.", ex.Message);
        Assert.Equal(0, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_RequiresReservationTokenForVoucherPayments()
    {
        var repository = new FakeOrderRepository(exists: false);
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(
            CreateRequest(
                Guid.NewGuid(),
                payments:
                [
                    new PaymentSyncDto(Guid.NewGuid(), PaymentMethodKind.Voucher, 5m, "V001")
                ]),
            CancellationToken.None));

        Assert.Equal("Voucher reservation token is required.", ex.Message);
        Assert.False(repository.InsertCalled);
    }

    [Fact]
    public async Task SyncAsync_ForwardsVoucherRedemptionAndConsumesReservation()
    {
        var orderGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false);
        var reservationService = new FakeReservationService();
        reservationService.Add(new StoreVoucherReservation("token-1", "S01", "V001", 5m, DateTimeOffset.UtcNow.AddMinutes(5)));
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), reservationService, new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                payments:
                [
                    new PaymentSyncDto(Guid.NewGuid(), PaymentMethodKind.Voucher, 5m, "V001", "token-1")
                ]),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Single(repository.LastVoucherRedemptions);
        var redemption = repository.LastVoucherRedemptions.Single();
        Assert.Equal("V001", redemption.VoucherCode);
        Assert.Equal("token-1", redemption.ReservationToken);
        Assert.Equal(5m, redemption.Amount);
        Assert.Equal(["token-1"], reservationService.ConsumedTokens);
    }

    [Fact]
    public async Task SyncAsync_AllowsNegativeVoucherPaymentWithoutReservation()
    {
        var orderGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false);
        var reservationService = new FakeReservationService();
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), reservationService, new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                payments:
                [
                    new PaymentSyncDto(Guid.NewGuid(), PaymentMethodKind.Voucher, -5m, "V001")
                ]),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.True(repository.InsertCalled);
        Assert.Empty(repository.LastVoucherRedemptions);
        Assert.Empty(reservationService.ConsumedTokens);
    }

    [Fact]
    public async Task SyncAsync_WithoutHeldSource_ReturnsNoneDisposition()
    {
        var repository = new FakeOrderRepository(exists: false);
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.False(response.AlreadySynced);
        Assert.Equal(HeldOrderDisposition.None, response.HeldOrderDisposition);
    }

    [Fact]
    public async Task SyncAsync_PrimaryRemoteClaim_CompletesHoldAndClaim()
    {
        var orderGuid = Guid.NewGuid();
        var holdGuid = Guid.NewGuid();
        var claimGuid = Guid.NewGuid();
        var store = new FakeHeldOrderAssociationStore();
        store.AddHold(holdGuid, "S01", SharedHeldOrderStatus.Claimed);
        store.AddClaim(claimGuid, holdGuid, "S01", SharedHeldOrderClaimStatus.Active);
        var repository = new FakeOrderRepository(exists: false) { HeldOrderStore = store };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(
            CreateRequest(orderGuid, heldSource: new HeldOrderSourceDto(holdGuid, claimGuid)),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.False(response.AlreadySynced);
        Assert.Equal(HeldOrderDisposition.Primary, response.HeldOrderDisposition);
        Assert.Equal(SharedHeldOrderStatus.Completed, store.GetHoldStatus(holdGuid));
        Assert.Equal(SharedHeldOrderClaimStatus.Completed, store.GetClaimStatus(claimGuid));
        Assert.False(store.GetClaimBlocking(claimGuid));
    }

    [Fact]
    public async Task SyncAsync_PreparedRemoteClaimFirstOrder_IsPrimarySupersedesClaim_AndRetryKeepsDisposition()
    {
        var firstOrderGuid = Guid.NewGuid();
        var holdGuid = Guid.NewGuid();
        var claimGuid = Guid.NewGuid();
        var store = new FakeHeldOrderAssociationStore();
        store.AddHold(holdGuid, "S01", SharedHeldOrderStatus.Pending);
        store.AddClaim(claimGuid, holdGuid, "S01", SharedHeldOrderClaimStatus.Prepared);
        var repository = new FakeOrderRepository(exists: false) { HeldOrderStore = store };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());
        var source = new HeldOrderSourceDto(holdGuid, claimGuid);

        // 服务端 claim 仍为 Prepared：首笔真实订单必须 Primary，hold 完成、claim 推进为 Superseded。
        var first = await service.SyncAsync(
            CreateRequest(firstOrderGuid, heldSource: source),
            CancellationToken.None);
        Assert.True(first.Accepted);
        Assert.Equal(HeldOrderDisposition.Primary, first.HeldOrderDisposition);
        Assert.Equal(SharedHeldOrderStatus.Completed, store.GetHoldStatus(holdGuid));
        Assert.Equal(SharedHeldOrderClaimStatus.Superseded, store.GetClaimStatus(claimGuid));
        Assert.False(store.GetClaimBlocking(claimGuid));
        Assert.Equal(1, store.PrimaryAssociationCount);

        // 后续真实订单仍 Duplicate；同一 orderGuid 重试保持原 disposition。
        var second = await service.SyncAsync(
            CreateRequest(Guid.NewGuid(), heldSource: source),
            CancellationToken.None);
        Assert.True(second.Accepted);
        Assert.Equal(HeldOrderDisposition.Duplicate, second.HeldOrderDisposition);

        var retry = await service.SyncAsync(
            CreateRequest(firstOrderGuid, heldSource: source),
            CancellationToken.None);
        Assert.True(retry.AlreadySynced);
        Assert.Equal(HeldOrderDisposition.Primary, retry.HeldOrderDisposition);
    }

    [Fact]
    public async Task SyncAsync_OfflineOrigin_CompletesPendingHoldWithoutClaim()
    {
        var orderGuid = Guid.NewGuid();
        var holdGuid = Guid.NewGuid();
        var store = new FakeHeldOrderAssociationStore();
        store.AddHold(holdGuid, "S01", SharedHeldOrderStatus.Pending);
        var repository = new FakeOrderRepository(exists: false) { HeldOrderStore = store };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                heldSource: new HeldOrderSourceDto(
                    holdGuid,
                    SourceKind: HeldOrderSourceKind.OfflineOrigin)),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal(HeldOrderDisposition.Primary, response.HeldOrderDisposition);
        Assert.Equal(SharedHeldOrderStatus.Completed, store.GetHoldStatus(holdGuid));
    }

    [Fact]
    public async Task SyncAsync_SecondRealOrderOnSameHold_IsDuplicateButAccepted()
    {
        var holdGuid = Guid.NewGuid();
        var claimGuid = Guid.NewGuid();
        var store = new FakeHeldOrderAssociationStore();
        store.AddHold(holdGuid, "S01", SharedHeldOrderStatus.Claimed);
        store.AddClaim(claimGuid, holdGuid, "S01", SharedHeldOrderClaimStatus.Active);
        var repository = new FakeOrderRepository(exists: false) { HeldOrderStore = store };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var first = await service.SyncAsync(
            CreateRequest(Guid.NewGuid(), heldSource: new HeldOrderSourceDto(holdGuid, claimGuid)),
            CancellationToken.None);
        var second = await service.SyncAsync(
            CreateRequest(Guid.NewGuid(), heldSource: new HeldOrderSourceDto(holdGuid, claimGuid)),
            CancellationToken.None);

        Assert.Equal(HeldOrderDisposition.Primary, first.HeldOrderDisposition);
        Assert.True(second.Accepted);
        Assert.False(second.AlreadySynced);
        Assert.Equal(HeldOrderDisposition.Duplicate, second.HeldOrderDisposition);
        Assert.Equal(SharedHeldOrderStatus.Completed, store.GetHoldStatus(holdGuid));
    }

    [Fact]
    public async Task SyncAsync_CrossStoreSource_IsUnmatchedButAccepted()
    {
        var orderGuid = Guid.NewGuid();
        var holdGuid = Guid.NewGuid();
        var store = new FakeHeldOrderAssociationStore();
        store.AddHold(holdGuid, "S02", SharedHeldOrderStatus.Pending);
        var repository = new FakeOrderRepository(exists: false) { HeldOrderStore = store };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                heldSource: new HeldOrderSourceDto(
                    holdGuid,
                    SourceKind: HeldOrderSourceKind.OfflineOrigin)),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal(HeldOrderDisposition.Unmatched, response.HeldOrderDisposition);
        Assert.Equal(SharedHeldOrderStatus.Pending, store.GetHoldStatus(holdGuid));
    }

    [Fact]
    public async Task SyncAsync_InvalidOrForeignClaim_IsUnmatchedButAccepted()
    {
        var orderGuid = Guid.NewGuid();
        var holdGuid = Guid.NewGuid();
        var otherHoldGuid = Guid.NewGuid();
        var claimGuid = Guid.NewGuid();
        var store = new FakeHeldOrderAssociationStore();
        store.AddHold(holdGuid, "S01", SharedHeldOrderStatus.Claimed);
        // claim 属于另一个 hold：来源无效，但订单照常接受。
        store.AddClaim(claimGuid, otherHoldGuid, "S01", SharedHeldOrderClaimStatus.Active);
        var repository = new FakeOrderRepository(exists: false) { HeldOrderStore = store };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(
            CreateRequest(orderGuid, heldSource: new HeldOrderSourceDto(holdGuid, claimGuid)),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal(HeldOrderDisposition.Unmatched, response.HeldOrderDisposition);
        Assert.Equal(SharedHeldOrderStatus.Claimed, store.GetHoldStatus(holdGuid));

        // Released claim 同样无效但订单照常接受。
        var releasedClaimGuid = Guid.NewGuid();
        store.AddClaim(releasedClaimGuid, holdGuid, "S01", SharedHeldOrderClaimStatus.Released);
        var second = await service.SyncAsync(
            CreateRequest(Guid.NewGuid(), heldSource: new HeldOrderSourceDto(holdGuid, releasedClaimGuid)),
            CancellationToken.None);
        Assert.True(second.Accepted);
        Assert.Equal(HeldOrderDisposition.Unmatched, second.HeldOrderDisposition);
    }

    [Fact]
    public async Task SyncAsync_SameOrderGuidRetry_KeepsOriginalDisposition()
    {
        var orderGuid = Guid.NewGuid();
        var holdGuid = Guid.NewGuid();
        var claimGuid = Guid.NewGuid();
        var store = new FakeHeldOrderAssociationStore();
        store.AddHold(holdGuid, "S01", SharedHeldOrderStatus.Claimed);
        store.AddClaim(claimGuid, holdGuid, "S01", SharedHeldOrderClaimStatus.Active);
        var repository = new FakeOrderRepository(exists: false) { HeldOrderStore = store };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());
        var source = new HeldOrderSourceDto(holdGuid, claimGuid);

        var first = await service.SyncAsync(
            CreateRequest(orderGuid, heldSource: source),
            CancellationToken.None);
        var retry = await service.SyncAsync(
            CreateRequest(orderGuid, heldSource: source),
            CancellationToken.None);

        Assert.Equal(HeldOrderDisposition.Primary, first.HeldOrderDisposition);
        Assert.True(retry.AlreadySynced);
        Assert.Equal(HeldOrderDisposition.Primary, retry.HeldOrderDisposition);
    }

    [Fact]
    public async Task SyncAsync_ConcurrentOrdersOnSameHold_ExactlyOnePrimary()
    {
        var holdGuid = Guid.NewGuid();
        var claimGuid = Guid.NewGuid();
        var store = new FakeHeldOrderAssociationStore();
        store.AddHold(holdGuid, "S01", SharedHeldOrderStatus.Claimed);
        store.AddClaim(claimGuid, holdGuid, "S01", SharedHeldOrderClaimStatus.Active);
        var firstRepository = new FakeOrderRepository(exists: false) { HeldOrderStore = store };
        var secondRepository = new FakeOrderRepository(exists: false) { HeldOrderStore = store };
        var firstService = new OrderSyncService(firstRepository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());
        var secondService = new OrderSyncService(secondRepository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new HeldOrderSourceDto(holdGuid, claimGuid);

        var firstTask = Task.Run(async () =>
        {
            await start.Task;
            return await firstService.SyncAsync(
                CreateRequest(Guid.NewGuid(), heldSource: source),
                CancellationToken.None);
        });
        var secondTask = Task.Run(async () =>
        {
            await start.Task;
            return await secondService.SyncAsync(
                CreateRequest(Guid.NewGuid(), heldSource: source),
                CancellationToken.None);
        });

        start.SetResult(true);
        var first = await firstTask;
        var second = await secondTask;

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.Equal(
            [HeldOrderDisposition.Primary, HeldOrderDisposition.Duplicate],
            new[] { first.HeldOrderDisposition, second.HeldOrderDisposition }.OrderBy(disposition => disposition));
        Assert.Equal(1, store.PrimaryAssociationCount);
        Assert.Equal(SharedHeldOrderStatus.Completed, store.GetHoldStatus(holdGuid));
    }

    [Fact]
    public async Task SyncAsync_WithoutHeldSource_NeverQueriesAssociationTable()
    {
        // 成功插入路径：disposition 由 InsertAsync 同事务直接返回，不查关联表。
        var insertedRepository = new FakeOrderRepository(exists: false);
        var insertedService = new OrderSyncService(insertedRepository,
            new OrderSyncPlanner(),
            new FakeReservationService(), new StubStoreTimeZoneResolver());
        var inserted = await insertedService.SyncAsync(
            CreateRequest(Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(inserted.Accepted);
        Assert.Equal(HeldOrderDisposition.None, inserted.HeldOrderDisposition);
        Assert.Equal(0, insertedRepository.DispositionQueryCount);

        // insert-loser 路径：并发重复上传同样不查关联表。
        var loserRepository = new FakeOrderRepository(exists: false)
        {
            InsertResult = false
        };
        var loserService = new OrderSyncService(loserRepository,
            new OrderSyncPlanner(),
            new FakeReservationService(), new StubStoreTimeZoneResolver());
        var loser = await loserService.SyncAsync(
            CreateRequest(Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(loser.AlreadySynced);
        Assert.Equal(HeldOrderDisposition.None, loser.HeldOrderDisposition);
        Assert.Equal(0, loserRepository.DispositionQueryCount);

        // already-exists 路径：同订单重试同样不查关联表。
        var existingRepository = new FakeOrderRepository(exists: true);
        var existingService = new OrderSyncService(existingRepository,
            new OrderSyncPlanner(),
            new FakeReservationService(), new StubStoreTimeZoneResolver());
        var existing = await existingService.SyncAsync(
            CreateRequest(Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(existing.AlreadySynced);
        Assert.Equal(HeldOrderDisposition.None, existing.HeldOrderDisposition);
        Assert.Equal(0, existingRepository.DispositionQueryCount);
    }

    [Theory]
    [InlineData(SharedHeldOrderClaimStatus.Released)]
    [InlineData(SharedHeldOrderClaimStatus.Superseded)]
    public async Task SyncAsync_StaleRealOrderOnCompletedHold_IsDuplicateButAccepted(
        SharedHeldOrderClaimStatus staleStatus)
    {
        var holdGuid = Guid.NewGuid();
        var claimGuid = Guid.NewGuid();
        var store = new FakeHeldOrderAssociationStore();
        store.AddHold(holdGuid, "S01", SharedHeldOrderStatus.Claimed);
        store.AddClaim(claimGuid, holdGuid, "S01", SharedHeldOrderClaimStatus.Active);
        var repository = new FakeOrderRepository(exists: false) { HeldOrderStore = store };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());
        var source = new HeldOrderSourceDto(holdGuid, claimGuid);

        var first = await service.SyncAsync(
            CreateRequest(Guid.NewGuid(), heldSource: source),
            CancellationToken.None);
        Assert.Equal(HeldOrderDisposition.Primary, first.HeldOrderDisposition);

        // 离线竞态：Primary 已创建后，另一设备把 claim 推进到 Released/Superseded。
        store.SetHoldStatus(holdGuid, SharedHeldOrderStatus.Completed);
        store.SetClaimStatus(claimGuid, staleStatus);

        var second = await service.SyncAsync(
            CreateRequest(Guid.NewGuid(), heldSource: source),
            CancellationToken.None);

        Assert.True(second.Accepted);
        Assert.False(second.AlreadySynced);
        Assert.Equal(HeldOrderDisposition.Duplicate, second.HeldOrderDisposition);
        Assert.Equal(1, store.PrimaryAssociationCount);
    }

    [Fact]
    public async Task SyncAsync_ExplicitOfflineOriginWithClaim_IsUnmatchedButAccepted()
    {
        var orderGuid = Guid.NewGuid();
        var holdGuid = Guid.NewGuid();
        var claimGuid = Guid.NewGuid();
        var store = new FakeHeldOrderAssociationStore();
        store.AddHold(holdGuid, "S01", SharedHeldOrderStatus.Pending);
        store.AddClaim(claimGuid, holdGuid, "S01", SharedHeldOrderClaimStatus.Active);
        var repository = new FakeOrderRepository(exists: false) { HeldOrderStore = store };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                heldSource: new HeldOrderSourceDto(
                    holdGuid,
                    claimGuid,
                    SourceKind: HeldOrderSourceKind.OfflineOrigin)),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal(HeldOrderDisposition.Unmatched, response.HeldOrderDisposition);
        Assert.Equal(SharedHeldOrderStatus.Pending, store.GetHoldStatus(holdGuid));
        Assert.Equal(SharedHeldOrderClaimStatus.Active, store.GetClaimStatus(claimGuid));
        Assert.Equal(0, store.PrimaryAssociationCount);
    }

    [Fact]
    public async Task SyncAsync_CompletedHoldWithoutPrimary_IsUnmatchedButAccepted()
    {
        var orderGuid = Guid.NewGuid();
        var holdGuid = Guid.NewGuid();
        var claimGuid = Guid.NewGuid();
        var store = new FakeHeldOrderAssociationStore();
        // Completed 却无 Primary 属于不一致状态：不改状态、不建 Primary，订单照常接受。
        store.AddHold(holdGuid, "S01", SharedHeldOrderStatus.Completed);
        store.AddClaim(claimGuid, holdGuid, "S01", SharedHeldOrderClaimStatus.Active);
        var repository = new FakeOrderRepository(exists: false) { HeldOrderStore = store };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                heldSource: new HeldOrderSourceDto(holdGuid, claimGuid)),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal(HeldOrderDisposition.Unmatched, response.HeldOrderDisposition);
        Assert.Equal(SharedHeldOrderStatus.Completed, store.GetHoldStatus(holdGuid));
        Assert.Equal(SharedHeldOrderClaimStatus.Active, store.GetClaimStatus(claimGuid));
        Assert.Equal(0, store.PrimaryAssociationCount);
    }

    [Fact]
    public async Task SyncAsync_EmptyHoldGuidSource_IsUnmatchedButAccepted()
    {
        var orderGuid = Guid.NewGuid();
        var store = new FakeHeldOrderAssociationStore();
        var repository = new FakeOrderRepository(exists: false) { HeldOrderStore = store };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService(), new StubStoreTimeZoneResolver());

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                heldSource: new HeldOrderSourceDto(
                    Guid.Empty,
                    SourceKind: HeldOrderSourceKind.OfflineOrigin)),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal(HeldOrderDisposition.Unmatched, response.HeldOrderDisposition);
        Assert.Equal(0, store.PrimaryAssociationCount);
    }

    [Fact]
    public void HeldOrderCompletionSql_RequiresExactRowMatchBeforePrimary()
    {
        // SQL 合同：CompleteHold/CompleteClaim 必须各精确命中 1 行才允许插 Primary；
        // 业务竞态都在锁定后预检成 Duplicate/Unmatched，0 行更新视为数据库不一致。
        Assert.Contains(
            "[Status] IN (N'Pending', N'Claimed')",
            SharedHeldOrderAssociationStore.CompleteHoldSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Revision] = @ExpectedRevision",
            SharedHeldOrderAssociationStore.CompleteHoldSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "[HoldGuid] = @HoldGuid",
            SharedHeldOrderAssociationStore.CompleteHoldSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "[ClaimGuid] = @ClaimGuid",
            SharedHeldOrderAssociationStore.CompleteClaimSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "[HoldGuid] = @HoldGuid",
            SharedHeldOrderAssociationStore.CompleteClaimSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Status] = N'Active'",
            SharedHeldOrderAssociationStore.CompleteClaimSql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Planner_WritesItemNumberAsItemNoMetadata()
    {
        var request = CreateRequest(Guid.NewGuid(), itemNumber: "ITEM-1001");

        var plan = new OrderSyncPlanner().CreatePlan(request, TestStoreTimeZones.Sydney);

        var line = Assert.Single(plan.Lines);
        Assert.Equal("P01", line.ProductCode);
        Assert.Contains("itemNo=ITEM-1001", line.Remark);
    }

    [Fact]
    public void Planner_RejectsSaleQuantitiesThatPosmCannotStoreWithoutLoss()
    {
        var baseline = CreateRequest(Guid.NewGuid());
        var original = Assert.Single(baseline.Lines);
        foreach (var quantity in new[] { 0m, -1m, 0.5m, 1.25m, (decimal)int.MaxValue + 1m })
        {
            var request = baseline with { Lines = [original with { Quantity = quantity }] };
            Assert.Throws<OrderSyncQuantityUnsupportedException>(() => new OrderSyncPlanner().CreatePlan(request, TestStoreTimeZones.Sydney));
        }

        var overflow = baseline with
        {
            Lines = [original with { Quantity = int.MaxValue }, original with { Quantity = 1m }]
        };
        Assert.Throws<OrderSyncQuantityUnsupportedException>(() => new OrderSyncPlanner().CreatePlan(overflow, TestStoreTimeZones.Sydney));

        var valid = baseline with { Lines = [original with { Quantity = 2m }] };
        var plan = new OrderSyncPlanner().CreatePlan(valid, TestStoreTimeZones.Sydney);
        Assert.Equal(2, Assert.Single(plan.Lines).Quantity);
        Assert.Equal(1, plan.Order.ItemCount);
    }

    [Fact]
    public void Planner_SanitizesSalesOrderDetailTextBeforeInsert()
    {
        var request = new OrderSyncRequest(
            Guid.NewGuid(),
            "S01",
            "POS01",
            "  Cashier-01  ",
            "Cashier",
            DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            9.99m,
            0m,
            9.99m,
            [
                new OrderLineSyncDto(
                    Guid.NewGuid(),
                    $"  {new string('P', 130)}  ",
                    $"  {new string('R', 130)}  ",
                    $"  {new string('N', 280)}  ",
                    $"  {new string('B', 130)}  ",
                    1m,
                    9.99m,
                    0m,
                    9.99m,
                    PriceSourceKind.StoreRetailPrice,
                    new string('I', 260))
            ],
            []);

        var plan = new OrderSyncPlanner().CreatePlan(request, TestStoreTimeZones.Sydney);

        var line = Assert.Single(plan.Lines);
        Assert.Equal(50, line.ProductCode.Length);
        Assert.Equal(50, line.ReferenceGUID.Length);
        Assert.Equal(255, line.ProductName!.Length);
        Assert.Equal(50, line.Barcode!.Length);
        Assert.Equal(50, line.Remark!.Length);
        Assert.Equal("POS_S01_POS01", line.CreatedBy);
        Assert.Equal("POS_S01_POS01", line.UpdatedBy);
        Assert.DoesNotContain("  ", line.ProductCode);
        Assert.StartsWith("priceSource=1;itemNo=", line.Remark);
    }

    [Fact]
    public void Planner_ConvertsBlankSalesOrderDetailTextToEmptyStrings()
    {
        var request = new OrderSyncRequest(
            Guid.NewGuid(),
            "S01",
            "POS01",
            "   ",
            "Cashier",
            DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            9.99m,
            0m,
            9.99m,
            [
                new OrderLineSyncDto(
                    Guid.NewGuid(),
                    "   ",
                    "   ",
                    "   ",
                    "   ",
                    1m,
                    9.99m,
                    0m,
                    9.99m,
                    PriceSourceKind.StoreRetailPrice,
                    "   ")
            ],
            []);

        var plan = new OrderSyncPlanner().CreatePlan(request, TestStoreTimeZones.Sydney);

        var line = Assert.Single(plan.Lines);
        Assert.Equal(string.Empty, line.ProductCode);
        Assert.Equal(string.Empty, line.ReferenceGUID);
        Assert.Equal(string.Empty, line.ProductName);
        Assert.Equal(string.Empty, line.Barcode);
        Assert.Equal("priceSource=1", line.Remark);
        Assert.Equal("POS_S01_POS01", line.CreatedBy);
        Assert.Equal("POS_S01_POS01", line.UpdatedBy);
    }

    [Fact]
    public void Planner_UsesExistingPosmDeviceCodeForAuditFields()
    {
        var request = CreateRequest(
            Guid.NewGuid(),
            storeCode: "1042",
            deviceCode: "POS_1042_1234");

        var plan = new OrderSyncPlanner().CreatePlan(request, TestStoreTimeZones.Sydney);

        Assert.Equal("POS_1042_1234", plan.Order.CreatedBy);
        Assert.Equal("POS_1042_1234", plan.Order.UpdatedBy);
        Assert.Equal("POS_1042_1234", Assert.Single(plan.Lines).CreatedBy);
        Assert.Equal("POS_1042_1234", Assert.Single(plan.Payments).CreatedBy);
        Assert.Equal("POS_1042_1234", Assert.Single(plan.Payments).UpdatedBy);
    }

    [Fact]
    public void Planner_PreservesExistingPosmDeviceCodeWithUnderscoreSuffix()
    {
        var request = CreateRequest(
            Guid.NewGuid(),
            storeCode: "1042",
            deviceCode: "POS_1042_TILL_01");

        var plan = new OrderSyncPlanner().CreatePlan(request, TestStoreTimeZones.Sydney);

        Assert.Equal("POS_1042_TILL_01", plan.Order.CreatedBy);
        Assert.Equal("POS_1042_TILL_01", Assert.Single(plan.Lines).CreatedBy);
    }

    [Fact]
    public void Planner_SynthesizesPosmAuditFieldsFromStoreAndDeviceSuffix()
    {
        var request = CreateRequest(
            Guid.NewGuid(),
            storeCode: "1042",
            deviceCode: "Register-A");

        var plan = new OrderSyncPlanner().CreatePlan(request, TestStoreTimeZones.Sydney);

        Assert.Equal("POS_1042_Register-A", plan.Order.CreatedBy);
        Assert.Equal("POS_1042_Register-A", Assert.Single(plan.Lines).UpdatedBy);
        Assert.Equal("POS_1042_Register-A", Assert.Single(plan.Payments).CreatedBy);
    }

    [Fact]
    public void Planner_FallsBackToCashierWhenDeviceCodeIsBlank()
    {
        var request = CreateRequest(
            Guid.NewGuid(),
            storeCode: "1042",
            deviceCode: "   ",
            cashierId: "Cashier-7");

        var plan = new OrderSyncPlanner().CreatePlan(request, TestStoreTimeZones.Sydney);

        Assert.Equal("POS_1042_Cashier-7", plan.Order.CreatedBy);
        Assert.Equal("POS_1042_Cashier-7", Assert.Single(plan.Lines).UpdatedBy);
    }

    [Fact]
    public void Planner_TruncatesPosmAuditFieldsWithoutBreakingShopCodePrefix()
    {
        var request = CreateRequest(
            Guid.NewGuid(),
            storeCode: "1042",
            deviceCode: $"POS_1042_{new string('X', 80)}");

        var plan = new OrderSyncPlanner().CreatePlan(request, TestStoreTimeZones.Sydney);

        Assert.Equal(50, plan.Order.CreatedBy!.Length);
        Assert.StartsWith("POS_1042_", plan.Order.CreatedBy);
        Assert.Equal(2, plan.Order.CreatedBy.Count(ch => ch == '_'));
        Assert.Equal(plan.Order.CreatedBy, Assert.Single(plan.Lines).CreatedBy);
    }

    [Fact]
    public void Repository_BuildsSalesOrderDetailDiagnosticsWithLengthsAndSafePreview()
    {
        var line = new SalesOrderDetail
        {
            OrderDetailGuid = "detail-1",
            ProductCode = new string('P', 90),
            ReferenceGUID = "REF-1",
            ProductName = "Name\r\nWithControl",
            Barcode = null,
            Remark = new string('R', 120),
            CreatedBy = "POS_1042_1234",
            UpdatedBy = "POS_1042_1234"
        };

        var diagnostic = Assert.Single(SqlSugarOrderRepository.BuildSalesOrderDetailDiagnostics([line]));

        Assert.Equal("detail-1", diagnostic.OrderDetailGuid);
        Assert.Equal(90, diagnostic.ProductCode.Length);
        Assert.Equal(80, diagnostic.ProductCode.Preview.Length);
        Assert.Equal(5, diagnostic.ReferenceGUID.Length);
        Assert.Equal("REF-1", diagnostic.ReferenceGUID.Preview);
        Assert.Equal(17, diagnostic.ProductName.Length);
        Assert.Equal("Name  WithControl", diagnostic.ProductName.Preview);
        Assert.Equal(0, diagnostic.Barcode.Length);
        Assert.Equal(string.Empty, diagnostic.Barcode.Preview);
        Assert.Equal(120, diagnostic.Remark.Length);
        Assert.Equal(80, diagnostic.Remark.Preview.Length);
        Assert.Equal("POS_1042_1234", diagnostic.CreatedBy.Preview);
        Assert.Equal("POS_1042_1234", diagnostic.UpdatedBy.Preview);

        var diagnosticsText = SqlSugarOrderRepository.BuildSalesOrderDetailDiagnosticsText([line]);
        Assert.Contains("\"ProductName\"", diagnosticsText);
        Assert.Contains("\"CreatedBy\"", diagnosticsText);
        Assert.Contains("\"UpdatedBy\"", diagnosticsText);
        Assert.Contains("\"Preview\":\"Name  WithControl\"", diagnosticsText);
        Assert.DoesNotContain("\r", diagnosticsText);
        Assert.DoesNotContain("\n", diagnosticsText);
    }

    [Fact]
    public void Planner_CreatesBankTransactionForCardPayment()
    {
        var paymentGuid = Guid.NewGuid();
        var orderGuid = Guid.NewGuid();
        var request = CreateRequest(
            orderGuid,
            payments:
            [
                new PaymentSyncDto(
                    paymentGuid,
                    PaymentMethodKind.Card,
                    12.34m,
                    "ANZ:TXN-1",
                    CardTransactions:
                    [
                        new CardTransactionDto(
                            "ANZ",
                            "TXN-1",
                            "123456",
                            "VISA",
                            4,
                            "****1234",
                            "MID-1",
                            "00",
                            "APPROVED",
                            "42",
                            DateTimeOffset.Parse("2026-05-26T00:00:00Z"),
                            12.34m,
                            "merchant receipt")
                    ])
            ]);

        var plan = new OrderSyncPlanner().CreatePlan(request, TestStoreTimeZones.Sydney);

        var bankTransaction = Assert.Single(plan.BankTransactions);
        Assert.Equal(paymentGuid.ToString("D"), bankTransaction.PaymentGuid);
        Assert.Equal(orderGuid.ToString("D"), bankTransaction.OrderGuid);
        Assert.Equal("TXN-1", bankTransaction.TxnRef);
        Assert.Equal("123456", bankTransaction.AuthCode);
        Assert.Equal("VISA", bankTransaction.CardType);
        Assert.Equal(4, bankTransaction.CardBIN);
        Assert.Equal("****1234", bankTransaction.CardNumber);
        Assert.Equal("MID-1", bankTransaction.Caid);
        Assert.Equal("00", bankTransaction.ResponseCode);
        Assert.Equal("APPROVED", bankTransaction.ResponseText);
        Assert.Equal("42", bankTransaction.Stan);
        Assert.Equal(12.34m, bankTransaction.Amount);
        Assert.Equal("merchant receipt", bankTransaction.ReceiptText);
    }

    [Fact]
    public void Planner_CreatesNegativeBankTransactionForCardRefund()
    {
        var paymentGuid = Guid.NewGuid();
        var request = CreateRequest(
            Guid.NewGuid(),
            payments:
            [
                new PaymentSyncDto(
                    paymentGuid,
                    PaymentMethodKind.Card,
                    -12.34m,
                    "SQRF:refund-1",
                    CardTransactions:
                    [
                        new CardTransactionDto(
                            "Square",
                            "refund-1",
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            "PENDING",
                            null,
                            DateTimeOffset.Parse("2026-05-26T00:00:00Z"),
                            12.34m,
                            null)
                    ])
            ]);

        var plan = new OrderSyncPlanner().CreatePlan(request, TestStoreTimeZones.Sydney);

        var bankTransaction = Assert.Single(plan.BankTransactions);
        Assert.Equal(paymentGuid.ToString("D"), bankTransaction.PaymentGuid);
        Assert.Equal(-12.34m, bankTransaction.Amount);
    }

    [Fact]
    public void Planner_SkipsReturnLinesFromSalesOrderDetailsAndItemCount()
    {
        var saleLineGuid = Guid.NewGuid();
        var returnLineGuid = Guid.NewGuid();
        var request = new OrderSyncRequest(
            Guid.NewGuid(),
            "S01",
            "POS01",
            "C01",
            "Cashier",
            DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            4.99m,
            0m,
            0m,
            [
                new OrderLineSyncDto(
                    saleLineGuid,
                    "P01",
                    "SOURCE-GUID-01",
                    "Apple",
                    "BAR01",
                    2m,
                    9.99m,
                    0m,
                    19.98m,
                    PriceSourceKind.StoreRetailPrice),
                new OrderLineSyncDto(
                    returnLineGuid,
                    "P02",
                    "RETURN-SOURCE-01",
                    "Orange",
                    "BAR02",
                    1m,
                    15m,
                    0m,
                    15m,
                    PriceSourceKind.StoreRetailPrice,
                    null,
                    OrderLineKind.Return,
                    "RETURN-SOURCE-KEY",
                    Guid.NewGuid(),
                    Guid.NewGuid())
            ],
            []);

        var plan = new OrderSyncPlanner().CreatePlan(request, TestStoreTimeZones.Sydney);

        var saleLine = Assert.Single(plan.Lines);
        Assert.Equal(saleLineGuid.ToString("D"), saleLine.OrderDetailGuid);
        // ItemCount 是销售明细行数（退货行不计入），与旧 POS 的历史数据口径一致。
        Assert.Equal(1, plan.Order.ItemCount);
        Assert.Equal(2, saleLine.Quantity);
        var returnRecord = Assert.Single(plan.ReturnRecords);
        Assert.Equal(returnLineGuid.ToString("D"), returnRecord.ReturnDetailGuid);
        Assert.Equal(request.OrderGuid.ToString("D"), returnRecord.ReturnOrderGuid);
        Assert.Equal(1m, returnRecord.ReturnQuantity);
        Assert.Equal(15m, returnRecord.ReturnAmount);
    }

    private static OrderSyncRequest CreateRequest(
        Guid orderGuid,
        string? itemNumber = null,
        IReadOnlyList<PaymentSyncDto>? payments = null,
        IReadOnlyList<OrderLineSyncDto>? lines = null,
        string storeCode = "S01",
        string deviceCode = "POS01",
        string cashierId = "C01",
        HeldOrderSourceDto? heldSource = null)
    {
        return new OrderSyncRequest(
            orderGuid,
            storeCode,
            deviceCode,
            cashierId,
            "Cashier",
            DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            9.99m,
            0m,
            9.99m,
            lines ??
            [
                new OrderLineSyncDto(
                    Guid.NewGuid(),
                    "P01",
                    "SOURCE-GUID-01",
                    "Apple",
                    "BAR01",
                    1m,
                    9.99m,
                    0m,
                    9.99m,
                    PriceSourceKind.StoreRetailPrice,
                    itemNumber)
            ],
            payments ??
            [
                new PaymentSyncDto(
                    Guid.NewGuid(),
                    PaymentMethodKind.Cash,
                    9.99m,
                null)
            ],
            heldSource);
    }

    private static OrderLineSyncDto CreateReturnLine(
        Guid originalOrderGuid,
        Guid originalDetailGuid,
        decimal quantity,
        decimal actualAmount)
    {
        return new OrderLineSyncDto(
            Guid.NewGuid(),
            "P02",
            "REF-RETURN",
            "Returned Apple",
            "BAR02",
            quantity,
            Math.Abs(actualAmount),
            0m,
            actualAmount,
            PriceSourceKind.StoreRetailPrice,
            Kind: OrderLineKind.Return,
            ReturnSourceKey: "RETURN-SOURCE",
            OriginalOrderGuid: originalOrderGuid,
            OriginalOrderDetailGuid: originalDetailGuid);
    }

    private static OrderReturnOriginalOrder CreateOriginalOrder(
        Guid orderGuid,
        Guid lineGuid,
        decimal quantity,
        decimal actualAmount)
    {
        return new OrderReturnOriginalOrder(
            orderGuid,
            [new OrderReturnOriginalLine(lineGuid, quantity, actualAmount)]);
    }

    private sealed class RecordingCardTenderVerifier(Action? onVerify = null) : ICardTenderOrderVerifier
    {
        public List<OrderSyncRequest> Requests { get; } = [];

        public Task VerifyAsync(OrderSyncRequest request)
        {
            Requests.Add(request);
            onVerify?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOrderRepository(bool exists) : IOrderRepository
    {
        public bool InsertCalled { get; private set; }

        public OrderSyncPlan? LastPlan { get; private set; }

        public IReadOnlyList<StoreVoucherRedemptionCommit> LastVoucherRedemptions { get; private set; } = [];

        public FakeHeldOrderAssociationStore HeldOrderStore { get; init; } = new();

        public HeldOrderDisposition LastHeldDisposition { get; private set; }

        public IReadOnlyList<OrderReturnOriginalOrder> OriginalOrders { get; init; } = [];

        public IReadOnlyList<SalesReturnRecord> ExistingReturnRecords { get; init; } = [];

        public IReadOnlyDictionary<string, decimal> OriginalCardPaymentAmountsByReference { get; init; } =
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, Guid> OriginalCardPaymentOrderGuidsByReference { get; init; } =
            new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<Guid, IReadOnlyList<PaymentDetail>> ExistingCardRefundsByReturnOrder { get; init; } =
            new Dictionary<Guid, IReadOnlyList<PaymentDetail>>();

        public int AtomicReturnValidationCallCount { get; private set; }

        public int InsertedReturnRecordCount { get; private set; }

        public bool InsertResult { get; init; } = true;

        public int DispositionQueryCount { get; private set; }

        private readonly HashSet<Guid> _insertedOrders = [];

        public Task<bool> ExistsAsync(Guid orderGuid, CancellationToken cancellationToken)
        {
            return Task.FromResult(exists || _insertedOrders.Contains(orderGuid));
        }

        public async Task<OrderInsertResult> InsertAsync(
            OrderSyncPlan plan,
            IReadOnlyList<StoreVoucherRedemptionCommit> voucherRedemptions,
            HeldOrderSourceDto? heldOrderSource,
            CancellationToken cancellationToken)
        {
            InsertCalled = true;
            LastPlan = plan;
            LastVoucherRedemptions = voucherRedemptions;
            if (!InsertResult)
            {
                return new OrderInsertResult(false, HeldOrderDisposition.None);
            }

            var returnRecords = await PrepareReturnRecordsAsync(plan.ReturnRecords, plan.Payments, cancellationToken);
            InsertedReturnRecordCount = returnRecords.Count;
            _insertedOrders.Add(Guid.Parse(plan.Order.OrderGuid!));
            var heldDisposition = heldOrderSource is null
                ? HeldOrderDisposition.None
                : await HeldOrderStore.AssociateAsync(
                    Guid.Parse(plan.Order.OrderGuid!),
                    plan.Order.BranchCode ?? string.Empty,
                    heldOrderSource,
                    cancellationToken);
            LastHeldDisposition = heldDisposition;
            return new OrderInsertResult(true, heldDisposition);
        }

        public Task<HeldOrderDisposition> GetHeldOrderDispositionAsync(
            Guid orderGuid,
            CancellationToken cancellationToken)
        {
            DispositionQueryCount++;
            return Task.FromResult(HeldOrderStore.GetDisposition(orderGuid));
        }

        private async Task<IReadOnlyList<SalesReturnRecord>> PrepareReturnRecordsAsync(
            IReadOnlyList<SalesReturnRecord> returnRecords,
            IReadOnlyList<PaymentDetail> payments,
            CancellationToken cancellationToken)
        {
            if (returnRecords.Count == 0)
            {
                return [];
            }

            AtomicReturnValidationCallCount++;
            var returnOrderGuids = returnRecords
                .Select(record => Normalize(record.ReturnOrderGuid))
                .Where(returnOrderGuid => returnOrderGuid is not null)
                .Select(returnOrderGuid => returnOrderGuid!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (ExistingReturnRecords.Any(record =>
                Normalize(record.ReturnOrderGuid) is { } returnOrderGuid &&
                returnOrderGuids.Contains(returnOrderGuid)))
            {
                return [];
            }

            await OrderReturnRecordValidator.ValidateAsync(
                returnRecords,
                (orderGuid, _) =>
                    Task.FromResult(OriginalOrders.FirstOrDefault(order => order.OrderGuid == orderGuid)),
                (orderGuid, _) =>
                    Task.FromResult(ExistingReturnRecords
                        .Where(record => record.OriginalOrderGuid == orderGuid.ToString("D"))
                        .ToList()
                        as IReadOnlyList<SalesReturnRecord>),
                    cancellationToken);

            ValidateCardRefundCapacity(returnRecords, payments);

            return returnRecords;
        }

        private void ValidateCardRefundCapacity(
            IReadOnlyList<SalesReturnRecord> returnRecords,
            IReadOnlyList<PaymentDetail> payments)
        {
            var requestedCardRefundsByReference = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var payment in payments
                .Where(payment => payment.PaymentMethod == (int)PaymentMethodKind.Card)
                .Where(payment => (payment.Amount ?? 0m) < 0m))
            {
                if (!CardRefundReference.TryGetOriginalReference(payment.Reference, out var originalReference) ||
                    Normalize(originalReference) is not { } normalizedOriginalReference)
                {
                    throw new InvalidOperationException("Card refunds require an original card payment reference.");
                }

                requestedCardRefundsByReference[normalizedOriginalReference] =
                    requestedCardRefundsByReference.GetValueOrDefault(normalizedOriginalReference) + Math.Abs(payment.Amount ?? 0m);
            }

            if (requestedCardRefundsByReference.Count == 0)
            {
                return;
            }

            var originalOrderGuids = returnRecords
                .Select(record => Guid.TryParse(record.OriginalOrderGuid, out var guid) ? guid : (Guid?)null)
                .OfType<Guid>()
                .Distinct()
                .ToList();
            if (originalOrderGuids.Count == 0)
            {
                throw new InvalidOperationException("Card refunds require an original card payment.");
            }

            var requestedReturnAmountByOriginalOrder = returnRecords
                .Select(record => new
                {
                    OriginalOrderGuid = Guid.TryParse(record.OriginalOrderGuid, out var guid) ? guid : (Guid?)null,
                    ReturnAmount = Math.Abs(record.ReturnAmount ?? 0m)
                })
                .Where(item => item.OriginalOrderGuid is not null)
                .GroupBy(item => item.OriginalOrderGuid!.Value)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(item => item.ReturnAmount));
            var remainingByReference = OriginalCardPaymentAmountsByReference
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var originalOrdersByReference = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
            foreach (var originalReference in remainingByReference.Keys)
            {
                if (OriginalCardPaymentOrderGuidsByReference.TryGetValue(originalReference, out var mappedOriginalOrderGuid))
                {
                    originalOrdersByReference[originalReference] = [mappedOriginalOrderGuid];
                }
                else if (originalOrderGuids.Count == 1)
                {
                    originalOrdersByReference[originalReference] = [originalOrderGuids[0]];
                }
            }

            var currentReturnOrderGuids = returnRecords
                .Select(record => Normalize(record.ReturnOrderGuid))
                .Where(returnOrderGuid => returnOrderGuid is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existingReturnOrderGuids = ExistingReturnRecords
                .Where(record =>
                    Guid.TryParse(record.OriginalOrderGuid, out var originalOrderGuid) &&
                    originalOrderGuids.Contains(originalOrderGuid))
                .Select(record => Normalize(record.ReturnOrderGuid))
                .Where(returnOrderGuid => returnOrderGuid is not null && !currentReturnOrderGuids.Contains(returnOrderGuid))
                .Select(returnOrderGuid => Guid.TryParse(returnOrderGuid, out var guid) ? guid : (Guid?)null)
                .OfType<Guid>()
                .Distinct();
            foreach (var returnOrderGuid in existingReturnOrderGuids)
            {
                var originalGuidsForExistingReturnOrder = ExistingReturnRecords
                    .Where(record => string.Equals(Normalize(record.ReturnOrderGuid), returnOrderGuid.ToString("D"), StringComparison.OrdinalIgnoreCase))
                    .Select(record => Guid.TryParse(record.OriginalOrderGuid, out var guid) ? guid : (Guid?)null)
                    .OfType<Guid>()
                    .Distinct()
                    .ToList();
                foreach (var payment in ExistingCardRefundsByReturnOrder.GetValueOrDefault(returnOrderGuid) ?? [])
                {
                    if (CardRefundReference.TryGetOriginalReference(payment.Reference, out var existingOriginalReference) &&
                        Normalize(existingOriginalReference) is { } normalizedExistingOriginalReference)
                    {
                        remainingByReference[normalizedExistingOriginalReference] =
                            remainingByReference.GetValueOrDefault(normalizedExistingOriginalReference) - Math.Abs(payment.Amount ?? 0m);
                    }
                    else if (originalGuidsForExistingReturnOrder.Count == 1 && remainingByReference.Count == 1)
                    {
                        var singleReference = remainingByReference.Keys.Single();
                        remainingByReference[singleReference] -= Math.Abs(payment.Amount ?? 0m);
                    }
                    else
                    {
                        foreach (var originalReference in remainingByReference.Keys.ToList())
                        {
                            remainingByReference[originalReference] = 0m;
                        }
                    }
                }
            }

            var requestedCardRefundsByOriginalOrder = new Dictionary<Guid, decimal>();
            foreach (var (originalReference, requestedAmount) in requestedCardRefundsByReference)
            {
                if (originalOrdersByReference.TryGetValue(originalReference, out var referenceOriginalOrders) &&
                    referenceOriginalOrders.Count == 1)
                {
                    var originalOrderGuid = referenceOriginalOrders.Single();
                    requestedCardRefundsByOriginalOrder[originalOrderGuid] =
                        requestedCardRefundsByOriginalOrder.GetValueOrDefault(originalOrderGuid) + requestedAmount;
                }

                if (requestedAmount > Math.Max(0m, remainingByReference.GetValueOrDefault(originalReference)))
                {
                    throw new InvalidOperationException("Card refund amount exceeds the available original card payment capacity.");
                }
            }

            foreach (var (originalOrderGuid, requestedAmount) in requestedCardRefundsByOriginalOrder)
            {
                var returnAmount = Math.Max(0m, requestedReturnAmountByOriginalOrder.GetValueOrDefault(originalOrderGuid));
                if (requestedAmount > returnAmount)
                {
                    throw new InvalidOperationException("Card refund amount exceeds the return amount for the original card order.");
                }
            }
        }

        private static string? Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    private sealed class FakeReservationService : IStoreVoucherReservationService
    {
        private readonly Dictionary<string, StoreVoucherReservation> reservations = new(StringComparer.OrdinalIgnoreCase);

        public List<string> ConsumedTokens { get; } = [];

        public void Add(StoreVoucherReservation reservation)
        {
            reservations[reservation.Token] = reservation;
        }

        public Task<StoreVoucherReservation?> GetAsync(string token, CancellationToken cancellationToken)
        {
            reservations.TryGetValue(token, out var reservation);
            return Task.FromResult(reservation);
        }

        public Task<StoreVoucherReservation> ReserveAsync(
            string storeCode,
            string voucherCode,
            decimal requestedAmount,
            decimal currentRemainingAmount,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<StoreVoucherReservation> ClaimAsync(
            string token,
            string storeCode,
            string voucherCode,
            decimal amount,
            string? consumedByReference,
            CancellationToken cancellationToken)
        {
            if (!reservations.TryGetValue(token, out var reservation) ||
                !string.Equals(reservation.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(reservation.VoucherCode, voucherCode, StringComparison.OrdinalIgnoreCase) ||
                reservation.LockedAmount < amount)
            {
                throw new InvalidOperationException("Voucher reservation token is invalid, expired, or already claimed.");
            }

            reservations.Remove(token);
            return Task.FromResult(reservation);
        }

        public Task ConsumeAsync(string token, CancellationToken cancellationToken)
        {
            ConsumedTokens.Add(token);
            reservations.Remove(token);
            return Task.CompletedTask;
        }

        public Task<bool> ReleaseAsync(
            string token,
            string storeCode,
            string voucherCode,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(reservations.Remove(token));
        }
    }

    /// <summary>
    /// 进程内关联存储：以信号量模拟 applock 串行化，断言并发下单同一 hold 恰好一个 Primary。
    /// 仅用于 fake 并发证明，不代表真实 SQL 执行。
    /// </summary>
    private sealed class FakeHeldOrderAssociationStore
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<Guid, FakeHoldRow> _holds = [];
        private readonly Dictionary<Guid, FakeClaimRow> _claims = [];
        private readonly Dictionary<Guid, HeldOrderDisposition> _associations = [];
        private readonly HashSet<Guid> _primaryHolds = [];

        public int PrimaryAssociationCount => _primaryHolds.Count;

        public void AddHold(
            Guid holdGuid,
            string storeCode,
            SharedHeldOrderStatus status = SharedHeldOrderStatus.Pending)
        {
            _holds[holdGuid] = new FakeHoldRow(holdGuid, storeCode, status);
        }

        public void AddClaim(
            Guid claimGuid,
            Guid holdGuid,
            string storeCode,
            SharedHeldOrderClaimStatus status = SharedHeldOrderClaimStatus.Active)
        {
            _claims[claimGuid] = new FakeClaimRow(claimGuid, holdGuid, storeCode, status);
        }

        public SharedHeldOrderStatus GetHoldStatus(Guid holdGuid) => _holds[holdGuid].Status;

        public SharedHeldOrderClaimStatus GetClaimStatus(Guid claimGuid) => _claims[claimGuid].Status;

        public bool GetClaimBlocking(Guid claimGuid) => _claims[claimGuid].IsBlocking;

        public void SetHoldStatus(Guid holdGuid, SharedHeldOrderStatus status)
        {
            _holds[holdGuid] = _holds[holdGuid] with { Status = status };
        }

        public void SetClaimStatus(Guid claimGuid, SharedHeldOrderClaimStatus status)
        {
            var claim = _claims[claimGuid];
            _claims[claimGuid] = claim with
            {
                Status = status,
                IsBlocking = status is SharedHeldOrderClaimStatus.Prepared
                    or SharedHeldOrderClaimStatus.Active
            };
        }

        public async Task<HeldOrderDisposition> AssociateAsync(
            Guid orderGuid,
            string storeCode,
            HeldOrderSourceDto source,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_associations.TryGetValue(orderGuid, out var existing))
                {
                    return existing;
                }

                var disposition = ComputeDisposition(storeCode, source);
                _associations[orderGuid] = disposition;
                if (disposition == HeldOrderDisposition.Primary)
                {
                    ApplyPrimary(source);
                }

                return disposition;
            }
            finally
            {
                _gate.Release();
            }
        }

        public HeldOrderDisposition GetDisposition(Guid orderGuid)
        {
            return _associations.GetValueOrDefault(orderGuid, HeldOrderDisposition.None);
        }

        private HeldOrderDisposition ComputeDisposition(
            string storeCode,
            HeldOrderSourceDto source)
        {
            // 严格来源组合：HoldGuid 非空；RemoteClaim 必须非空 ClaimGuid；OfflineOrigin 必须 ClaimGuid=null。
            if (source is null ||
                source.HoldGuid == Guid.Empty ||
                (source.Kind == HeldOrderSourceKind.RemoteClaim && source.ClaimGuid is null) ||
                (source.Kind == HeldOrderSourceKind.OfflineOrigin && source.ClaimGuid is not null))
            {
                return HeldOrderDisposition.Unmatched;
            }

            if (!_holds.TryGetValue(source.HoldGuid, out var hold) ||
                !string.Equals(hold.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
            {
                return HeldOrderDisposition.Unmatched;
            }

            FakeClaimRow? claim = null;
            if (source.Kind == HeldOrderSourceKind.RemoteClaim)
            {
                if (source.ClaimGuid is not { } claimGuid ||
                    !_claims.TryGetValue(claimGuid, out var candidate) ||
                    candidate.HoldGuid != source.HoldGuid ||
                    !string.Equals(candidate.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
                {
                    return HeldOrderDisposition.Unmatched;
                }

                claim = candidate;
            }

            if (_primaryHolds.Contains(source.HoldGuid))
            {
                // 已有 Primary：只要 claim 归属正确即 Duplicate（离线竞态下任意终态均如此）。
                return HeldOrderDisposition.Duplicate;
            }

            // 创建 Primary 前 hold 必须 Pending/Claimed；否则不改状态，记录 Unmatched。
            if (hold.Status is not (SharedHeldOrderStatus.Pending or SharedHeldOrderStatus.Claimed))
            {
                return HeldOrderDisposition.Unmatched;
            }

            // Remote 必须 Active/Prepared claim（与生产合同一致，Prepared 也允许首笔赢下）；
            // 其余终态先不改状态，记录 Unmatched。
            if (source.Kind == HeldOrderSourceKind.RemoteClaim &&
                claim!.Status is not (
                    SharedHeldOrderClaimStatus.Active or SharedHeldOrderClaimStatus.Prepared))
            {
                return HeldOrderDisposition.Unmatched;
            }

            return HeldOrderDisposition.Primary;
        }

        private void ApplyPrimary(HeldOrderSourceDto source)
        {
            // 与 SQL 合同一致：更新 0 行的场景绝不创建 Primary。
            var currentHold = _holds[source.HoldGuid];
            if (currentHold.Status is not (SharedHeldOrderStatus.Pending or SharedHeldOrderStatus.Claimed))
            {
                throw new InvalidOperationException(
                    "Fake SQL contract: CompleteHoldSql matched 0 rows; Primary must not be created.");
            }

            if (source.Kind == HeldOrderSourceKind.RemoteClaim)
            {
                if (source.ClaimGuid is not { } requiredClaimGuid ||
                    !_claims.TryGetValue(requiredClaimGuid, out var requiredClaim))
                {
                    throw new InvalidOperationException(
                        "Fake SQL contract: claim row missing; Primary must not be created.");
                }

                // Active → CompleteClaimSql（Completed）；Prepared → SupersedePreparedClaimSql（Superseded）。
                if (requiredClaim.Status == SharedHeldOrderClaimStatus.Active)
                {
                    _primaryHolds.Add(source.HoldGuid);
                    _holds[source.HoldGuid] = currentHold with { Status = SharedHeldOrderStatus.Completed };
                    _claims[requiredClaimGuid] = requiredClaim with
                    {
                        Status = SharedHeldOrderClaimStatus.Completed,
                        IsBlocking = false
                    };
                    return;
                }

                if (requiredClaim.Status == SharedHeldOrderClaimStatus.Prepared &&
                    requiredClaim.IsBlocking)
                {
                    _primaryHolds.Add(source.HoldGuid);
                    _holds[source.HoldGuid] = currentHold with { Status = SharedHeldOrderStatus.Completed };
                    _claims[requiredClaimGuid] = requiredClaim with
                    {
                        Status = SharedHeldOrderClaimStatus.Superseded,
                        IsBlocking = false
                    };
                    return;
                }

                throw new InvalidOperationException(
                    "Fake SQL contract: claim completion/supersede update matched 0 rows; Primary must not be created.");
            }

            _primaryHolds.Add(source.HoldGuid);
            _holds[source.HoldGuid] = currentHold with { Status = SharedHeldOrderStatus.Completed };
            foreach (var key in _claims.Keys
                .Where(key => _claims[key].HoldGuid == source.HoldGuid && _claims[key].IsBlocking)
                .ToArray())
            {
                var blocking = _claims[key];
                _claims[key] = blocking with
                {
                    Status = SharedHeldOrderClaimStatus.Superseded,
                    IsBlocking = false
                };
            }
        }

        private sealed record FakeHoldRow(
            Guid HoldGuid,
            string StoreCode,
            SharedHeldOrderStatus Status);

        private sealed record FakeClaimRow(
            Guid ClaimGuid,
            Guid HoldGuid,
            string StoreCode,
            SharedHeldOrderClaimStatus Status,
            bool IsBlocking = true);
    }
}
