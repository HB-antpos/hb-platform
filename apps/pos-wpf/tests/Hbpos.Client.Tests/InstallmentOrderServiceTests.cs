using System.Diagnostics;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class InstallmentOrderServiceTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task SearchAsync_matches_item_number_and_barcode_within_two_seconds_for_bounded_history()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var service = new InstallmentOrderService(repository, new StubInstallmentApiClient());

            await schema.InitializeAsync();
            for (var index = 0; index < 200; index++)
            {
                await repository.UpsertAsync(CreateSearchableOrder(index, isTarget: index == 199));
            }

            foreach (var keyword in new[] { "ITEM-TARGET", "930000000001" })
            {
                var stopwatch = Stopwatch.StartNew();

                var result = await service.SearchAsync(CreateOnlineSession(), keyword);

                stopwatch.Stop();
                Assert.Equal("IO-SEARCH-0199", Assert.Single(result).OrderNumber);
                Assert.True(
                    stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                    $"分期按 {keyword} 查询耗时 {stopwatch.Elapsed.TotalMilliseconds:F1} ms，超过 2 秒预算。");
            }
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task SearchAsync_matches_each_product_identifier_exactly_and_case_insensitively()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var service = new InstallmentOrderService(repository, new StubInstallmentApiClient());
            var targetGuid = Guid.Parse("00000000-0000-0000-0001-000000000101");
            var decoyGuid = Guid.Parse("00000000-0000-0000-0001-000000000102");

            LocalInstallmentOrder CreateOrder(Guid guid, string productCode, string itemNumber, string lookupCode) =>
                CreateSearchableOrder(101, isTarget: false) with
                {
                    OrderGuid = guid,
                    InstallmentGuid = guid,
                    InstallmentNumber = guid == targetGuid ? "IO-IDENTIFIER-TARGET" : "IO-IDENTIFIER-DECOY",
                    Lines =
                    [
                        new InstallmentLineDto(
                            Guid.NewGuid(),
                            productCode,
                            null,
                            "Tea",
                            lookupCode,
                            1m,
                            120m,
                            0m,
                            120m,
                            itemNumber)
                    ]
                };

            await schema.InitializeAsync();
            await repository.UpsertAsync(CreateOrder(targetGuid, "SKU-EXACT", "ITEM-EXACT", "BAR-EXACT"));
            await repository.UpsertAsync(CreateOrder(decoyGuid, "SKU-EXACT-SUFFIX", "ITEM-EXACT-SUFFIX", "BAR-EXACT-SUFFIX"));

            foreach (var keyword in new[] { "sku-exact", "item-exact", "bar-exact" })
            {
                var result = await service.SearchAsync(CreateOfflineSession(), keyword);

                Assert.Equal(targetGuid, Assert.Single(result).OrderId);
            }

            Assert.Empty(await service.SearchAsync(CreateOfflineSession(), "exact"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetOrderDetailsAsync_online_reads_authoritative_detail_and_refreshes_local_cache()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var authoritativeUpdatedAt = DateTimeOffset.Parse("2026-08-20T04:30:00Z");
            var authoritative = CreateActiveDetails() with
            {
                PaidAmount = 80m,
                BalanceAmount = 40m,
                UpdatedAt = authoritativeUpdatedAt,
                Lines =
                [
                    new InstallmentLineDto(
                        Guid.NewGuid(),
                        "SKU-AUTHORITATIVE",
                        null,
                        "Authoritative Item",
                        "BAR-AUTHORITATIVE",
                        1m,
                        120m,
                        0m,
                        120m,
                        "ITEM-AUTHORITATIVE")
                ]
            };
            var apiClient = new StubInstallmentApiClient { DetailsResponse = authoritative };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            await repository.UpsertAsync(CreateLocalOrderWithPayments([]));

            var result = await service.GetOrderDetailsAsync(CreateOnlineSession(), authoritative.InstallmentGuid);
            var cached = await repository.GetAsync(authoritative.InstallmentGuid);

            Assert.NotNull(result);
            Assert.Equal(80m, result!.PaidAmount);
            Assert.Contains(result.Lines, line => line.DisplayName == "Authoritative Item");
            Assert.Equal(80m, cached?.PaidAmount);
            Assert.Equal(authoritativeUpdatedAt, result.UpdatedAt);
            Assert.Equal(authoritativeUpdatedAt, cached?.UpdatedAt);
            Assert.Equal(1, apiClient.DetailsCallCount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetOrderDetailsAsync_online_does_not_fall_back_to_stale_snapshot_when_authority_fails()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                DetailsException = new InvalidOperationException("authoritative detail failed")
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            var stale = CreateLocalOrderWithPayments([]);
            await repository.UpsertAsync(stale);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.GetOrderDetailsAsync(CreateOnlineSession(), stale.InstallmentGuid));

            var cached = await repository.GetAsync(stale.InstallmentGuid);
            Assert.Equal(stale.PaidAmount, cached?.PaidAmount);
            Assert.Equal(1, apiClient.DetailsCallCount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetOrderDetailsAsync_offline_uses_complete_local_snapshot_without_api_call()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient { DetailsResponse = CreateActiveDetails() };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            var local = CreateLocalOrderWithPayments([]);
            await repository.UpsertAsync(local);

            var result = await service.GetOrderDetailsAsync(CreateOfflineSession(), local.InstallmentGuid);

            Assert.NotNull(result);
            Assert.Equal(local.InstallmentGuid, result!.InstallmentGuid);
            Assert.Equal(local.PaidAmount, result.PaidAmount);
            Assert.Equal(local.Lines, result.Lines);
            Assert.Equal(0, apiClient.DetailsCallCount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task QueryHistoryAsync_uses_authoritative_api_when_online_and_forwards_history_filters()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var updatedAt = DateTimeOffset.Parse("2026-08-25T05:30:00Z");
            var apiClient = new StubInstallmentApiClient
            {
                HistoryResponse = new InstallmentHistoryQueryResponse(
                [
                    new InstallmentSummaryDto(
                        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                        "IO-REMOTE-0001",
                        "S001",
                        "POS-02",
                        "Alice",
                        "Customer",
                        "0400111222",
                        DateTimeOffset.Parse("2026-08-20T00:00:00Z"),
                        120m,
                        20m,
                        40m,
                        80m,
                        InstallmentStatus.Active,
                        updatedAt),
                    new InstallmentSummaryDto(
                        Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
                        "IO-REMOTE-VOID",
                        "S001",
                        "POS-02",
                        "Alice",
                        "Customer",
                        "0400111222",
                        DateTimeOffset.Parse("2026-08-19T00:00:00Z"),
                        120m,
                        20m,
                        20m,
                        100m,
                        InstallmentStatus.Cancelled,
                        updatedAt.AddMinutes(-1),
                        InstallmentCancellationKind.VoidCancel)
                ])
            };
            var service = new InstallmentOrderService(repository, apiClient);
            var updatedFrom = DateTimeOffset.Parse("2026-08-25T00:00:00Z");
            var updatedTo = DateTimeOffset.Parse("2026-08-25T23:59:59Z");

            await schema.InitializeAsync();
            var result = await service.QueryHistoryAsync(
                CreateOnlineSession(),
                new InstallmentHistorySearchQuery(
                    UpdatedFrom: updatedFrom,
                    UpdatedTo: updatedTo,
                    DeviceCode: "POS-02",
                    Keyword: "ITEM-TARGET",
                    Take: 100));

            var order = Assert.Single(result, candidate => candidate.OrderNumber == "IO-REMOTE-0001");
            Assert.Equal("IO-REMOTE-0001", order.OrderNumber);
            Assert.Equal(updatedAt, order.UpdatedAt);
            Assert.True(order.CanAddRepayment);
            Assert.Equal("待补款", order.Status);
            Assert.Equal(
                "已作废",
                Assert.Single(result, candidate => candidate.OrderNumber == "IO-REMOTE-VOID").Status);
            Assert.NotNull(apiClient.LastHistoryRequest);
            Assert.Equal("S001", apiClient.LastHistoryRequest!.StoreCode);
            Assert.Equal("POS-02", apiClient.LastHistoryRequest.DeviceCode);
            Assert.Equal(updatedFrom, apiClient.LastHistoryRequest.UpdatedFrom);
            Assert.Equal(updatedTo, apiClient.LastHistoryRequest.UpdatedTo);
            Assert.Equal("ITEM-TARGET", apiClient.LastHistoryRequest.Keyword);
            Assert.True(apiClient.LastHistoryRequest.OrderByUpdatedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task QueryHistoryAsync_allows_cancel_refund_for_paid_off_but_not_picked_up_or_void()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var updatedAt = DateTimeOffset.Parse("2026-10-06T05:30:00Z");
            InstallmentSummaryDto Summary(string number, InstallmentStatus status, decimal paid, decimal balance) =>
                new(
                    Guid.NewGuid(),
                    number,
                    "S001",
                    "POS-02",
                    "Alice",
                    "Customer",
                    "0400111222",
                    DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
                    120m,
                    20m,
                    paid,
                    balance,
                    status,
                    updatedAt);
            var apiClient = new StubInstallmentApiClient
            {
                HistoryResponse = new InstallmentHistoryQueryResponse(
                [
                    Summary("IO-PAID-OFF", InstallmentStatus.PaidOff, 120m, 0m),
                    Summary("IO-PICKED-UP", InstallmentStatus.PickedUp, 120m, 0m)
                ])
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            var result = await service.QueryHistoryAsync(
                CreateOnlineSession(),
                new InstallmentHistorySearchQuery(Take: 100));

            var paidOff = Assert.Single(result, candidate => candidate.OrderNumber == "IO-PAID-OFF");
            Assert.True(paidOff.CanCancelWithRefund);
            Assert.False(paidOff.CanVoidCancel);
            Assert.True(paidOff.CanConfirmPickup);
            var pickedUp = Assert.Single(result, candidate => candidate.OrderNumber == "IO-PICKED-UP");
            Assert.False(pickedUp.CanCancelWithRefund);
            Assert.False(pickedUp.CanVoidCancel);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task QueryHistoryAsync_uses_local_snapshot_search_when_offline()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient();
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            await repository.UpsertAsync(CreateSearchableOrder(1, isTarget: true));

            var result = await service.QueryHistoryAsync(
                CreateOfflineSession(),
                new InstallmentHistorySearchQuery(Keyword: "930000000001"));

            Assert.Equal("IO-SEARCH-0001", Assert.Single(result).OrderNumber);
            Assert.Equal(0, apiClient.HistoryCallCount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CreateAsync_returns_online_required_when_session_is_offline()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient();
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();

            var result = await service.CreateAsync(CreateOfflineSession(), CreateInstallmentCreateRequest());

            Assert.Equal(InstallmentWriteStatus.OnlineRequired, result.Status);
            Assert.Null(result.LocalOrder);
            Assert.Equal(0, apiClient.CreateCallCount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Write_operations_return_online_required_when_session_is_offline()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient();
            var service = new InstallmentOrderService(repository, apiClient);
            var offlineSession = CreateOfflineSession();

            await schema.InitializeAsync();

            var appendResult = await service.AppendPaymentAsync(offlineSession, CreateAppendPaymentRequest());
            var pickupResult = await service.ConfirmPickupAsync(offlineSession, CreateConfirmPickupRequest());
            var cancelResult = await service.CancelWithRefundAsync(offlineSession, CreateCancelRequest());
            var voidResult = await service.VoidCancelAsync(offlineSession, CreateVoidRequest());

            Assert.Equal(InstallmentWriteStatus.OnlineRequired, appendResult.Status);
            Assert.Equal(InstallmentWriteStatus.OnlineRequired, pickupResult.Status);
            Assert.Equal(InstallmentWriteStatus.OnlineRequired, cancelResult.Status);
            Assert.Equal(InstallmentWriteStatus.OnlineRequired, voidResult.Status);
            Assert.Equal(0, apiClient.AppendPaymentCallCount);
            Assert.Equal(0, apiClient.ConfirmPickupCallCount);
            Assert.Equal(0, apiClient.CancelCallCount);
            Assert.Equal(0, apiClient.VoidCallCount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CreateAsync_saves_local_snapshot_after_online_success()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse()
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();

            var result = await service.CreateAsync(CreateOnlineSession(), CreateInstallmentCreateRequest());

            Assert.Equal(InstallmentWriteStatus.Succeeded, result.Status);
            Assert.NotNull(result.LocalOrder);
            Assert.Equal(1, apiClient.CreateCallCount);
            Assert.Equal("IO-20260530-0001", result.LocalOrder!.InstallmentNumber);

            var saved = await repository.GetAsync(result.LocalOrder.InstallmentGuid);
            Assert.NotNull(saved);
            Assert.Equal(InstallmentStatus.Active, saved.Status);
            Assert.Equal(30m, saved.PaidAmount);
            Assert.Equal(90m, saved.BalanceAmount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CreateOrderAsync_maps_cart_lines_and_voucher_payment_into_api_request()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse()
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();

            var result = await service.CreateOrderAsync(
                new InstallmentOrderCreateRequest(
                    CreateOnlineSession(),
                    CreateCartSnapshot(),
                    "张三",
                    "0400111222",
                    30m,
                    new InstallmentPaymentDraft(
                        Guid.Parse("12345678-1111-2222-3333-444444444444"),
                        PaymentMethodKind.Voucher,
                        30m,
                        "VIP001",
                        "LOCK-001"),
                    "周末取货"));

            Assert.True(result.Succeeded);
            Assert.Equal("已创建分期单。", result.Message);
            Assert.NotNull(result.Order);
            Assert.NotNull(apiClient.LastCreateRequest);
            Assert.Equal(30m, apiClient.LastCreateRequest!.DownPayment.Amount);
            Assert.Equal(PaymentMethodKind.Voucher, apiClient.LastCreateRequest.DownPayment.Method);
            Assert.Equal("VIP001", apiClient.LastCreateRequest.DownPayment.Reference);
            Assert.Equal("LOCK-001", apiClient.LastCreateRequest.DownPayment.ReservationToken);
            Assert.Equal(2, apiClient.LastCreateRequest.Lines.Count);
            Assert.Equal("待补款", result.Order!.Status);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CreateOrderAsync_authorizes_card_down_payment_before_create_and_uses_authorized_details()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var events = new List<string>();
            var cardTransactions = new[]
            {
                new CardTransactionDto("ANZ", "TXN-1", "123456", "VISA", 4, "****1234", "MID", "00", "APPROVED", "42", DateTimeOffset.UtcNow, 35m, "receipt")
            };
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse(),
                OnCreate = _ => events.Add("api")
            };
            var cardTerminalClient = new RecordingCardTerminalClient(
                new PaymentAuthorizationResult(true, "ANZ:TXN-1", AuthorizedAmount: 35m, CardTransactions: cardTransactions),
                () => events.Add("authorize"));
            var service = new InstallmentOrderService(repository, apiClient, cardTerminalClient: cardTerminalClient);

            await schema.InitializeAsync();

            var result = await service.CreateOrderAsync(
                new InstallmentOrderCreateRequest(
                    CreateOnlineSession(),
                    CreateCartSnapshot(),
                    "张三",
                    "0400111222",
                    30m,
                    new InstallmentPaymentDraft(
                        Guid.Parse("12345678-1111-2222-3333-444444444444"),
                        PaymentMethodKind.Card,
                        30m,
                        "draft-reference"),
                    "周末取货"));

            Assert.True(result.Succeeded);
            Assert.Equal(new[] { "authorize", "api" }, events);
            Assert.Equal(1, cardTerminalClient.AuthorizeCallCount);
            Assert.Equal(30m, cardTerminalClient.LastAuthorizeAmount);
            Assert.NotNull(apiClient.LastCreateRequest);
            Assert.Equal(PaymentMethodKind.Card, apiClient.LastCreateRequest!.DownPayment.Method);
            Assert.Equal(35m, apiClient.LastCreateRequest.DownPayment.Amount);
            Assert.Equal("ANZ:TXN-1", apiClient.LastCreateRequest.DownPayment.Reference);
            Assert.Same(cardTransactions, apiClient.LastCreateRequest.DownPayment.CardTransactions);
            Assert.False(string.IsNullOrWhiteSpace(apiClient.LastCreateRequest.DownPayment.IdempotencyKey));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CreateOrderAsync_uses_preauthorized_card_down_payment_without_authorizing_again()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var cardTransactions = new[]
            {
                new CardTransactionDto("ANZ", "TXN-PAID", "654321", "VISA", 4, "****4321", "MID", "00", "APPROVED", "43", DateTimeOffset.UtcNow, 30m, "receipt")
            };
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse()
            };
            var cardTerminalClient = new RecordingCardTerminalClient(
                new PaymentAuthorizationResult(true, "ANZ:SHOULD-NOT-RUN", AuthorizedAmount: 30m));
            var service = new InstallmentOrderService(repository, apiClient, cardTerminalClient: cardTerminalClient);

            await schema.InitializeAsync();

            var result = await service.CreateOrderAsync(
                new InstallmentOrderCreateRequest(
                    CreateOnlineSession(),
                    CreateCartSnapshot(),
                    "张三",
                    "0400111222",
                    30m,
                    new InstallmentPaymentDraft(
                        Guid.Parse("12345678-1111-2222-3333-555555555555"),
                        PaymentMethodKind.Card,
                        30m,
                        "ANZ:TXN-PAID",
                        CardTransactions: cardTransactions,
                        IdempotencyKey: "existing-card-tender"),
                    "周末取货"));

            Assert.True(result.Succeeded);
            Assert.Equal(0, cardTerminalClient.AuthorizeCallCount);
            Assert.Equal(1, apiClient.CreateCallCount);
            Assert.NotNull(apiClient.LastCreateRequest);
            Assert.Equal("ANZ:TXN-PAID", apiClient.LastCreateRequest!.DownPayment.Reference);
            Assert.Same(cardTransactions, apiClient.LastCreateRequest.DownPayment.CardTransactions);
            Assert.Equal("existing-card-tender", apiClient.LastCreateRequest.DownPayment.IdempotencyKey);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CreateOrderAsync_authorizes_card_down_payment_when_only_idempotency_key_exists()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse()
            };
            var cardTerminalClient = new RecordingCardTerminalClient(
                new PaymentAuthorizationResult(true, "ANZ:TXN-IDEMP", AuthorizedAmount: 30m));
            var service = new InstallmentOrderService(repository, apiClient, cardTerminalClient: cardTerminalClient);

            await schema.InitializeAsync();

            var result = await service.CreateOrderAsync(
                new InstallmentOrderCreateRequest(
                    CreateOnlineSession(),
                    CreateCartSnapshot(),
                    "张三",
                    "0400111222",
                    30m,
                    new InstallmentPaymentDraft(
                        Guid.Parse("12345678-1111-2222-3333-666666666666"),
                        PaymentMethodKind.Card,
                        30m,
                        "draft-reference",
                        IdempotencyKey: "client-key-only"),
                    "周末取货"));

            Assert.True(result.Succeeded);
            Assert.Equal(1, cardTerminalClient.AuthorizeCallCount);
            Assert.NotNull(apiClient.LastCreateRequest);
            Assert.Equal("ANZ:TXN-IDEMP", apiClient.LastCreateRequest!.DownPayment.Reference);
            Assert.Equal("client-key-only", apiClient.LastCreateRequest.DownPayment.IdempotencyKey);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CreateOrderAsync_does_not_call_api_when_card_down_payment_authorization_fails()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse()
            };
            var cardTerminalClient = new RecordingCardTerminalClient(
                new PaymentAuthorizationResult(false, Message: "card auth declined"));
            var service = new InstallmentOrderService(repository, apiClient, cardTerminalClient: cardTerminalClient);

            await schema.InitializeAsync();

            var result = await service.CreateOrderAsync(
                new InstallmentOrderCreateRequest(
                    CreateOnlineSession(),
                    CreateCartSnapshot(),
                    "张三",
                    "0400111222",
                    30m,
                    new InstallmentPaymentDraft(
                        Guid.Parse("12345678-1111-2222-3333-444444444444"),
                        PaymentMethodKind.Card,
                        30m,
                        "draft-reference"),
                    "周末取货"));

            Assert.False(result.Succeeded);
            Assert.Equal("card auth declined", result.Message);
            Assert.Equal(1, cardTerminalClient.AuthorizeCallCount);
            Assert.Equal(0, apiClient.CreateCallCount);
            Assert.Null(apiClient.LastCreateRequest);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task AddRepaymentAsync_without_claim_operation_service_fails_closed_before_legacy_append()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse(),
                AppendPaymentResponse = CreateAppendPaymentResponse()
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            await service.CreateAsync(CreateOnlineSession(), CreateInstallmentCreateRequest());

            var result = await service.AddRepaymentAsync(
                new InstallmentOrderRepaymentRequest(
                    Guid.Parse("11111111-2222-3333-4444-555555555555"),
                    CreateOnlineSession(),
                    new InstallmentPaymentDraft(
                        Guid.Parse("12345678-9999-aaaa-bbbb-cccccccccccc"),
                        PaymentMethodKind.Voucher,
                        40m,
                        "VIP001",
                        "LOCK-002")));

            Assert.False(result.Succeeded);
            Assert.Contains("安全补款服务未配置", result.Message);
            Assert.Equal(0, apiClient.AppendPaymentCallCount);
            Assert.Null(apiClient.LastAppendPaymentRequest);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CancelWithRefundAsync_without_operation_service_fails_before_legacy_cancel()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse(),
                CancelResponse = CreateCancelResponse()
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            await service.CreateAsync(CreateOnlineSession(), CreateInstallmentCreateRequest());

            var result = await service.CancelWithRefundAsync(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                CreateOnlineSession());

            Assert.False(result.Succeeded);
            Assert.Contains("安全取消服务未配置", result.Message);
            Assert.Null(apiClient.LastCancelRequest);

            var saved = await repository.GetAsync(Guid.Parse("11111111-2222-3333-4444-555555555555"));
            Assert.NotNull(saved);
            Assert.Equal(InstallmentStatus.Active, saved.Status);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CancelWithRefundAsync_without_operation_service_stops_before_card_refund()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CancelResponse = CreateCancelResponse()
            };
            var cardTerminal = new DeclinedCardTerminalClient();
            var service = new InstallmentOrderService(
                repository,
                apiClient,
                cardTerminalClient: cardTerminal);

            await schema.InitializeAsync();
            await repository.UpsertAsync(CreateLocalOrderWithPayments([
                new InstallmentPaymentDto(
                    Guid.Parse("12345678-5555-6666-7777-888888888888"),
                    PaymentMethodKind.Card,
                    30m,
                    "CARD-TXN-1",
                    InstallmentPaymentStatus.Recorded,
                    DateTimeOffset.Parse("2026-05-30T10:00:00+10:00"),
                    "C001",
                    "POS-01")
            ]));

            var result = await service.CancelWithRefundAsync(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                CreateOnlineSession());

            Assert.False(result.Succeeded);
            Assert.Contains("安全取消服务未配置", result.Message);
            Assert.Equal(0, cardTerminal.RefundCallCount);
            Assert.Null(apiClient.LastCancelRequest);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task VoidCancelAsync_builds_void_request_with_reason()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse(),
                VoidResponse = CreateVoidResponse()
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            await service.CreateAsync(CreateOnlineSession(), CreateInstallmentCreateRequest());

            var result = await service.VoidCancelAsync(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                CreateOnlineSession(),
                "门店作废");

            Assert.True(result.Succeeded);
            Assert.Equal("已作废", result.Message);
            Assert.NotNull(apiClient.LastVoidRequest);
            Assert.Equal("门店作废", apiClient.LastVoidRequest!.Reason);
            Assert.Equal(result.Order!.OrderId, apiClient.LastVoidRequest.OperationGuid);
            Assert.Equal($"{result.Order.OrderId:D}:void", apiClient.LastVoidRequest.IdempotencyKey);
            Assert.NotNull(result.Order);
            Assert.False(result.Order!.CanVoidCancel);

            var saved = await repository.GetAsync(result.Order.OrderId);
            Assert.NotNull(saved);
            Assert.Equal(InstallmentStatus.Cancelled, saved.Status);
            Assert.Equal(InstallmentCancellationKind.VoidCancel, saved.CancellationInfo?.Kind);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ConfirmPickupAsync_builds_stable_operation_identity()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var response = CreateActiveDetails() with
            {
                Status = InstallmentStatus.PickedUp,
                PickupInfo = new InstallmentPickupInfoDto(
                    DateTimeOffset.Parse("2026-05-30T11:00:00+10:00"),
                    "Alice",
                    null)
            };
            var apiClient = new StubInstallmentApiClient
            {
                ConfirmPickupResponse = new InstallmentConfirmPickupResponse(
                    response.InstallmentGuid,
                    response.Status,
                    response.PickupInfo.PickedUpAt,
                    response)
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            var result = await service.ConfirmPickupAsync(response.InstallmentGuid, CreateOnlineSession());

            Assert.True(result.Succeeded);
            Assert.NotNull(apiClient.LastConfirmPickupRequest);
            Assert.Equal(response.InstallmentGuid, apiClient.LastConfirmPickupRequest!.OperationGuid);
            Assert.Equal($"{response.InstallmentGuid:D}:pickup", apiClient.LastConfirmPickupRequest.IdempotencyKey);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ConfirmPickupAsync_returns_requires_review_when_api_times_out_without_caller_cancellation()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                ConfirmPickupException = new TaskCanceledException("pickup API timed out")
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            var result = await service.ConfirmPickupAsync(CreateActiveDetails().InstallmentGuid, CreateOnlineSession());

            Assert.False(result.Succeeded);
            Assert.True(result.RequiresReview);
            Assert.Contains("结果可能已提交", result.Message, StringComparison.Ordinal);
            Assert.Contains("刷新核对", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ConfirmPickupAsync_propagates_caller_cancellation()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var service = new InstallmentOrderService(repository, new StubInstallmentApiClient());
            using var cancellation = new CancellationTokenSource();

            await schema.InitializeAsync();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ConfirmPickupAsync(CreateActiveDetails().InstallmentGuid, CreateOnlineSession(), cancellation.Token));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static LocalInstallmentOrder CreateSearchableOrder(int index, bool isTarget)
    {
        var installmentGuid = Guid.Parse($"00000000-0000-0000-0001-{index:D12}");
        var createdAt = DateTimeOffset.Parse("2026-08-25T10:00:00+10:00").AddSeconds(-index);
        return new LocalInstallmentOrder(
            installmentGuid,
            installmentGuid,
            $"IO-SEARCH-{index:D4}",
            "S001",
            "POS-01",
            "C001",
            "Alice",
            $"Customer {index}",
            $"0400{index:D6}",
            createdAt,
            createdAt,
            120m,
            20m,
            30m,
            30m,
            90m,
            InstallmentStatus.Active,
            [
                new InstallmentLineDto(
                    Guid.NewGuid(),
                    $"SKU-{index:D4}",
                    null,
                    "Tea",
                    isTarget ? "930000000001" : $"9301{index:D8}",
                    1m,
                    120m,
                    0m,
                    120m,
                    isTarget ? "ITEM-TARGET" : $"ITEM-{index:D4}")
            ],
            [],
            null);
    }

    private static PosSessionState CreateOfflineSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", false, 0);
    }

    private static PosSessionState CreateOnlineSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
    }

    private static PosCartServiceSnapshot CreateCartSnapshot()
    {
        return new PosCartServiceSnapshot(
            130m,
            10m,
            120m,
            [
                new PosCartLineServiceSnapshot("SKU-001", null, "Premium Rice Cooker", "690001", "ITEM-001", 1m, 130m, 10m, 120m),
                new PosCartLineServiceSnapshot("SKU-002", null, "Rice Bowl Set", "690002", "ITEM-002", 1m, 0m, 0m, 0m)
            ]);
    }

    private static InstallmentCreateRequest CreateInstallmentCreateRequest()
    {
        return new InstallmentCreateRequest(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.Parse("2026-05-30T10:00:00+10:00"),
            120m,
            30m,
            [
                new InstallmentLineDto(
                    Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    "SKU-001",
                    null,
                    "Premium Rice Cooker",
                    "690001",
                    1m,
                    120m,
                    0m,
                    120m,
                    "ITEM-001")
            ],
            new InstallmentPaymentCommandDto(
                Guid.Parse("12345678-1111-2222-3333-444444444444"),
                PaymentMethodKind.Cash,
                30m,
                null),
            "张三",
            "0400111222",
            "周末取货");
    }

    private static InstallmentAppendPaymentRequest CreateAppendPaymentRequest()
    {
        return new InstallmentAppendPaymentRequest(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Guid.Parse("12345678-9999-aaaa-bbbb-cccccccccccc"),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            40m,
            PaymentMethodKind.Cash,
            null,
            null);
    }

    private static InstallmentConfirmPickupRequest CreateConfirmPickupRequest()
    {
        return new InstallmentConfirmPickupRequest(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.Parse("2026-05-30T11:00:00+10:00"),
            "客户本人提货");
    }

    private static InstallmentCancelRequest CreateCancelRequest()
    {
        return new InstallmentCancelRequest(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.Parse("2026-05-30T11:10:00+10:00"),
            [
                new InstallmentRefundPaymentCommandDto(
                    Guid.Parse("55555555-9999-aaaa-bbbb-cccccccccccc"),
                    PaymentMethodKind.Cash,
                    30m,
                    null,
                    null,
                    "refund-offline-test")
            ],
            "客户取消",
            "cancel-offline-test");
    }

    private static InstallmentVoidRequest CreateVoidRequest()
    {
        return new InstallmentVoidRequest(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.Parse("2026-05-30T11:20:00+10:00"),
            "门店作废",
            "void-offline-test");
    }

    private static InstallmentCreateResponse CreateCreateResponse()
    {
        var details = CreateActiveDetails();
        return new InstallmentCreateResponse(
            details.InstallmentGuid,
            details.InstallmentNumber,
            details.Status,
            details.PaidAmount,
            details.BalanceAmount,
            details,
            false,
            "已创建分期单。");
    }

    private static InstallmentAppendPaymentResponse CreateAppendPaymentResponse()
    {
        var details = CreateActiveDetails() with
        {
            PaidAmount = 70m,
            BalanceAmount = 50m,
            Payments =
            [
                .. CreateActiveDetails().Payments,
                new InstallmentPaymentDto(
                    Guid.Parse("12345678-9999-aaaa-bbbb-cccccccccccc"),
                    PaymentMethodKind.Voucher,
                    40m,
                    "VIP001",
                    InstallmentPaymentStatus.Recorded,
                    DateTimeOffset.Parse("2026-05-30T10:20:00+10:00"),
                    "C001",
                    "POS-01")
            ]
        };

        return new InstallmentAppendPaymentResponse(
            details.InstallmentGuid,
            details.Payments[^1].PaymentGuid,
            details.PaidAmount,
            details.BalanceAmount,
            details.Status,
            details,
            false,
            "补款完成");
    }

    private static InstallmentCancelResponse CreateCancelResponse()
    {
        var cancelledAt = DateTimeOffset.Parse("2026-05-30T10:30:00+10:00");
        var details = CreateActiveDetails() with
        {
            Status = InstallmentStatus.Cancelled,
            CancellationInfo = new InstallmentCancellationInfoDto(
                InstallmentCancellationKind.RefundCancel,
                cancelledAt,
                "Alice",
                "客户取消")
        };

        return new InstallmentCancelResponse(details.InstallmentGuid, details.Status, details, false, "已取消并退款");
    }

    private static InstallmentVoidResponse CreateVoidResponse()
    {
        var voidedAt = DateTimeOffset.Parse("2026-05-30T10:35:00+10:00");
        var details = CreateActiveDetails() with
        {
            Status = InstallmentStatus.Cancelled,
            CancellationInfo = new InstallmentCancellationInfoDto(
                InstallmentCancellationKind.VoidCancel,
                voidedAt,
                "Alice",
                "门店作废")
        };

        return new InstallmentVoidResponse(details.InstallmentGuid, details.Status, details, false, "已作废");
    }

    private static InstallmentDetailsDto CreateActiveDetails()
    {
        var createdAt = DateTimeOffset.Parse("2026-05-30T10:00:00+10:00");
        return new InstallmentDetailsDto(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "IO-20260530-0001",
            "S001",
            "POS-01",
            "C001",
            "Alice",
            "张三",
            "0400111222",
            createdAt,
            120m,
            20m,
            30m,
            30m,
            90m,
            InstallmentStatus.Active,
            [
                new InstallmentLineDto(
                    Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    "SKU-001",
                    null,
                    "Premium Rice Cooker",
                    "690001",
                    1m,
                    120m,
                    0m,
                    120m,
                    "ITEM-001")
            ],
            [
                new InstallmentPaymentDto(
                    Guid.Parse("12345678-1111-2222-3333-444444444444"),
                    PaymentMethodKind.Cash,
                    30m,
                    null,
                    InstallmentPaymentStatus.Recorded,
                    createdAt,
                    "C001",
                    "POS-01")
            ],
            null,
            null,
            "周末取货");
    }

    private static LocalInstallmentOrder CreateLocalOrderWithPayments(IReadOnlyList<InstallmentPaymentDto> payments)
    {
        var paidAmount = payments.Where(payment => payment.Status == InstallmentPaymentStatus.Recorded).Sum(payment => payment.Amount);
        var details = CreateActiveDetails() with
        {
            PaidAmount = paidAmount,
            BalanceAmount = 120m - paidAmount,
            Payments = payments
        };
        return new LocalInstallmentOrder(
            details.InstallmentGuid,
            details.InstallmentGuid,
            details.InstallmentNumber,
            details.StoreCode,
            details.DeviceCode,
            details.CashierId,
            details.CashierName,
            details.CustomerName,
            details.CustomerPhone,
            details.CreatedAt,
            DateTimeOffset.UtcNow,
            details.TotalAmount,
            details.MinimumDownPayment,
            details.DownPaymentAmount,
            details.PaidAmount,
            details.BalanceAmount,
            details.Status,
            details.Lines,
            details.Payments,
            details.PickupInfo,
            details.Note,
            details.CancellationInfo);
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-installment-service-{Guid.NewGuid():N}.db");
    }

    private static void DeleteTempDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class StubInstallmentApiClient : IInstallmentApiClient
    {
        public InstallmentHistoryQueryResponse? HistoryResponse { get; set; }

        public InstallmentHistoryQueryRequest? LastHistoryRequest { get; private set; }

        public int HistoryCallCount { get; private set; }

        public InstallmentDetailsDto? DetailsResponse { get; set; }

        public Exception? DetailsException { get; set; }

        public int DetailsCallCount { get; private set; }

        public InstallmentCreateResponse? CreateResponse { get; set; }

        public InstallmentAppendPaymentResponse? AppendPaymentResponse { get; set; }

        public InstallmentConfirmPickupResponse? ConfirmPickupResponse { get; set; }

        public Exception? ConfirmPickupException { get; set; }

        public InstallmentCancelResponse? CancelResponse { get; set; }

        public InstallmentVoidResponse? VoidResponse { get; set; }

        public Action<InstallmentCreateRequest>? OnCreate { get; set; }

        public InstallmentCreateRequest? LastCreateRequest { get; private set; }

        public InstallmentAppendPaymentRequest? LastAppendPaymentRequest { get; private set; }

        public InstallmentCancelRequest? LastCancelRequest { get; private set; }

        public InstallmentConfirmPickupRequest? LastConfirmPickupRequest { get; private set; }

        public InstallmentVoidRequest? LastVoidRequest { get; private set; }

        public int CreateCallCount { get; private set; }

        public int AppendPaymentCallCount { get; private set; }

        public int ConfirmPickupCallCount { get; private set; }

        public int CancelCallCount { get; private set; }

        public int VoidCallCount { get; private set; }

        public Task<InstallmentHistoryQueryResponse> QueryHistoryAsync(
            InstallmentHistoryQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HistoryCallCount++;
            LastHistoryRequest = request;
            return Task.FromResult(HistoryResponse ?? throw new InvalidOperationException("HistoryResponse was not configured."));
        }

        public Task<InstallmentCreateResponse> CreateAsync(InstallmentCreateRequest request, CancellationToken cancellationToken = default)
        {
            OnCreate?.Invoke(request);
            CreateCallCount++;
            LastCreateRequest = request;
            return Task.FromResult(CreateResponse ?? throw new InvalidOperationException("CreateResponse was not configured."));
        }

        public Task<InstallmentDetailsDto> GetDetailsAsync(Guid installmentGuid, CancellationToken cancellationToken = default)
        {
            DetailsCallCount++;
            if (DetailsException is not null)
            {
                return Task.FromException<InstallmentDetailsDto>(DetailsException);
            }

            return Task.FromResult(DetailsResponse ?? throw new InvalidOperationException("DetailsResponse was not configured."));
        }

        public Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default)
        {
            AppendPaymentCallCount++;
            LastAppendPaymentRequest = request;
            return Task.FromResult(AppendPaymentResponse ?? throw new InvalidOperationException("AppendPaymentResponse was not configured."));
        }

        public Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmPickupCallCount++;
            LastConfirmPickupRequest = request;
            cancellationToken.ThrowIfCancellationRequested();
            if (ConfirmPickupException is not null)
            {
                return Task.FromException<InstallmentConfirmPickupResponse>(ConfirmPickupException);
            }

            return Task.FromResult(ConfirmPickupResponse ?? throw new InvalidOperationException("ConfirmPickupResponse was not configured."));
        }

        public Task<InstallmentCancelResponse> CancelAsync(InstallmentCancelRequest request, CancellationToken cancellationToken = default)
        {
            CancelCallCount++;
            LastCancelRequest = request;
            return Task.FromResult(CancelResponse ?? throw new InvalidOperationException("CancelResponse was not configured."));
        }

        public Task<InstallmentVoidResponse> VoidAsync(InstallmentVoidRequest request, CancellationToken cancellationToken = default)
        {
            VoidCallCount++;
            LastVoidRequest = request;
            return Task.FromResult(VoidResponse ?? throw new InvalidOperationException("VoidResponse was not configured."));
        }
    }

    private sealed class RecordingCardTerminalClient(
        PaymentAuthorizationResult authorizeResult,
        Action? onAuthorize = null) : ICardTerminalClient
    {
        public int AuthorizeCallCount { get; private set; }

        public decimal? LastAuthorizeAmount { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            onAuthorize?.Invoke();
            AuthorizeCallCount++;
            LastAuthorizeAmount = amount;
            return Task.FromResult(authorizeResult);
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(false, Message: "card refund declined"));
        }
    }

    private sealed class DeclinedCardTerminalClient : ICardTerminalClient
    {
        public int RefundCallCount { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(false, Message: "card auth declined"));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            RefundCallCount++;
            return Task.FromResult(new PaymentAuthorizationResult(false, Message: "card refund declined"));
        }
    }
}
