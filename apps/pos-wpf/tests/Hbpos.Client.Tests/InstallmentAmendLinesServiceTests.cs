using System.Net;
using System.Net.Http.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

/// <summary>分期单“修改商品”的服务层 / API 客户端 / 加商品检索测试。</summary>
public sealed class InstallmentAmendLinesServiceTests
{
    private static readonly Guid InstallmentGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid LineGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-01T10:00:00+10:00");
    private static readonly DateTimeOffset T1 = DateTimeOffset.Parse("2026-10-01T11:00:00+10:00");

    [Fact]
    public async Task AmendLinesAsync_offline_requires_online_and_never_calls_the_api()
    {
        await using var harness = await Harness.CreateAsync();

        var result = await harness.Service.AmendLinesAsync(
            Session(isOnline: false),
            harness.Baseline,
            DoubledLines());

        Assert.Equal(InstallmentAmendLinesOutcome.OnlineRequired, result.Outcome);
        Assert.Empty(harness.Api.AmendRequests);
        Assert.Equal(0, harness.Api.DetailsCallCount);
    }

    [Fact]
    public async Task AmendLinesAsync_sends_expected_updated_at_and_saves_the_returned_snapshot()
    {
        await using var harness = await Harness.CreateAsync();
        var newLines = DoubledLines();
        harness.Api.OnAmend = request => Task.FromResult(new InstallmentAmendLinesResponse(
            InstallmentGuid,
            InstallmentStatus.Active,
            240m,
            30m,
            210m,
            Details(T1, newLines)));

        var result = await harness.Service.AmendLinesAsync(Session(), harness.Baseline, newLines);

        var request = Assert.Single(harness.Api.AmendRequests);
        Assert.Equal(InstallmentGuid, request.InstallmentGuid);
        Assert.Equal("S001", request.StoreCode);
        Assert.Equal("POS-01", request.DeviceCode);
        Assert.Equal("C001", request.CashierId);
        Assert.Equal("Alice", request.CashierName);
        // 并发令牌必须是编辑器加载时看到的订单版本，而不是“现在”。
        Assert.Equal(T0, request.ExpectedUpdatedAt);
        Assert.Equal(newLines, request.Lines);

        Assert.True(result.Succeeded);
        Assert.Equal(T1, result.Order!.UpdatedAt);
        Assert.Equal(240m, result.Order.TotalAmount);
        Assert.Equal(210m, result.Summary!.OutstandingAmount);
        Assert.True(result.Summary.CanAddRepayment);
        var cached = await harness.Repository.GetAsync(InstallmentGuid);
        Assert.Equal(T1, cached!.UpdatedAt);
        Assert.Equal(240m, cached.TotalAmount);
        Assert.Equal(2m, Assert.Single(cached.Lines).Quantity);
    }

    [Fact]
    public async Task AmendLinesAsync_reports_paid_off_summary_when_new_total_equals_paid()
    {
        await using var harness = await Harness.CreateAsync();
        var lines = new[] { Line(1m, 60m) };
        harness.Api.OnAmend = _ => Task.FromResult(new InstallmentAmendLinesResponse(
            InstallmentGuid,
            InstallmentStatus.PaidOff,
            60m,
            60m,
            0m,
            Details(T1, lines, paid: 60m)));
        var baseline = harness.Baseline with { PaidAmount = 60m, BalanceAmount = 60m };

        var result = await harness.Service.AmendLinesAsync(Session(), baseline, lines);

        Assert.True(result.Succeeded);
        Assert.Equal(InstallmentStatus.PaidOff, result.Order!.Status);
        Assert.True(result.Summary!.CanConfirmPickup);
        Assert.False(result.Summary.CanAddRepayment);
    }

    [Fact]
    public async Task AmendLinesAsync_pre_validation_rejects_without_touching_the_server()
    {
        await using var harness = await Harness.CreateAsync();

        // 新总额 40 不低于已付 30，但低于 50 的总额下限。
        var belowFloor = await harness.Service.AmendLinesAsync(Session(), harness.Baseline, [Line(1m, 40m)]);
        // 新总额 80 >= 50 但低于已付 100。
        var paidHeavy = harness.Baseline with { PaidAmount = 100m, BalanceAmount = 20m };
        var belowPaid = await harness.Service.AmendLinesAsync(Session(), paidHeavy, [Line(1m, 80m)]);
        // 行本身不合法（数量 0）。
        var invalid = await harness.Service.AmendLinesAsync(Session(), harness.Baseline, [Line(0m, 100m)]);
        // 已提货的订单不允许改。
        var pickedUp = await harness.Service.AmendLinesAsync(
            Session(),
            harness.Baseline with { Status = InstallmentStatus.PickedUp },
            DoubledLines());

        Assert.Equal(InstallmentAmendLinesErrorCodes.TotalBelowMinimum, belowFloor.ErrorCode);
        Assert.Equal(InstallmentAmendLinesErrorCodes.TotalBelowPaid, belowPaid.ErrorCode);
        Assert.Equal(InstallmentAmendLinesErrorCodes.InvalidLines, invalid.ErrorCode);
        Assert.Equal(InstallmentAmendLinesErrorCodes.StatusNotAllowed, pickedUp.ErrorCode);
        Assert.All(
            new[] { belowFloor, belowPaid, invalid, pickedUp },
            result => Assert.Equal(InstallmentAmendLinesOutcome.Rejected, result.Outcome));
        Assert.Empty(harness.Api.AmendRequests);
    }

    [Fact]
    public async Task AmendLinesAsync_stale_409_refetches_saves_snapshot_and_reports_stale()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Api.OnAmend = _ => Task.FromException<InstallmentAmendLinesResponse>(
            new CatalogApiException("stale", HttpStatusCode.Conflict, InstallmentAmendLinesErrorCodes.Stale));
        var elsewhere = new[] { Line(3m, 120m) };
        harness.Api.OnGetDetails = () => Task.FromResult(Details(T1, elsewhere));

        var result = await harness.Service.AmendLinesAsync(Session(), harness.Baseline, DoubledLines());

        Assert.Equal(InstallmentAmendLinesOutcome.Stale, result.Outcome);
        Assert.Equal(InstallmentAmendLinesErrorCodes.Stale, result.ErrorCode);
        Assert.Contains("已被其他设备修改", result.Message, StringComparison.Ordinal);
        Assert.Equal(T1, result.Order!.UpdatedAt);
        Assert.NotNull(result.Summary);
        Assert.Equal(1, harness.Api.DetailsCallCount);
        var cached = await harness.Repository.GetAsync(InstallmentGuid);
        Assert.Equal(3m, Assert.Single(cached!.Lines).Quantity);
    }

    [Fact]
    public async Task AmendLinesAsync_stale_still_reports_stale_when_the_refresh_itself_fails()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Api.OnAmend = _ => Task.FromException<InstallmentAmendLinesResponse>(
            new CatalogApiException("stale", HttpStatusCode.Conflict, InstallmentAmendLinesErrorCodes.Stale));
        harness.Api.OnGetDetails = () => Task.FromException<InstallmentDetailsDto>(new HttpRequestException("offline"));

        var result = await harness.Service.AmendLinesAsync(Session(), harness.Baseline, DoubledLines());

        Assert.Equal(InstallmentAmendLinesOutcome.Stale, result.Outcome);
        Assert.Null(result.Order);
        Assert.Null(result.Summary);
    }

    [Fact]
    public async Task AmendLinesAsync_non_stale_conflict_is_a_clean_rejection_without_refetch()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Api.OnAmend = _ => Task.FromException<InstallmentAmendLinesResponse>(
            new CatalogApiException("busy", HttpStatusCode.Conflict, "INSTALLMENT_BUSY"));

        var result = await harness.Service.AmendLinesAsync(Session(), harness.Baseline, DoubledLines());

        Assert.Equal(InstallmentAmendLinesOutcome.Rejected, result.Outcome);
        Assert.Equal("INSTALLMENT_BUSY", result.ErrorCode);
        Assert.Equal(0, harness.Api.DetailsCallCount);
    }

    [Theory]
    [InlineData(InstallmentAmendLinesErrorCodes.InvalidLines)]
    [InlineData(InstallmentAmendLinesErrorCodes.TotalBelowPaid)]
    [InlineData(InstallmentAmendLinesErrorCodes.TotalBelowMinimum)]
    public async Task AmendLinesAsync_server_business_errors_are_rejections_with_their_error_code(string errorCode)
    {
        await using var harness = await Harness.CreateAsync();
        harness.Api.OnAmend = _ => Task.FromException<InstallmentAmendLinesResponse>(
            new CatalogApiException("server says no", HttpStatusCode.BadRequest, errorCode));

        var result = await harness.Service.AmendLinesAsync(Session(), harness.Baseline, DoubledLines());

        Assert.Equal(InstallmentAmendLinesOutcome.Rejected, result.Outcome);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Equal("server says no", result.Message);
        Assert.Equal(0, harness.Api.DetailsCallCount);
    }

    [Fact]
    public async Task AmendLinesAsync_status_not_allowed_refreshes_the_snapshot()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Api.OnAmend = _ => Task.FromException<InstallmentAmendLinesResponse>(
            new CatalogApiException("status", HttpStatusCode.BadRequest, InstallmentAmendLinesErrorCodes.StatusNotAllowed));
        harness.Api.OnGetDetails = () => Task.FromResult(Details(T1, DoubledLines(), status: InstallmentStatus.Cancelled));

        var result = await harness.Service.AmendLinesAsync(Session(), harness.Baseline, DoubledLines());

        Assert.Equal(InstallmentAmendLinesOutcome.Rejected, result.Outcome);
        Assert.Equal(InstallmentAmendLinesErrorCodes.StatusNotAllowed, result.ErrorCode);
        Assert.Equal(InstallmentStatus.Cancelled, result.Order!.Status);
    }

    [Fact]
    public async Task AmendLinesAsync_forbidden_is_a_rejection()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Api.OnAmend = _ => Task.FromException<InstallmentAmendLinesResponse>(
            new CatalogApiException("no permission", HttpStatusCode.Forbidden));

        var result = await harness.Service.AmendLinesAsync(Session(), harness.Baseline, DoubledLines());

        Assert.Equal(InstallmentAmendLinesOutcome.Rejected, result.Outcome);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public async Task AmendLinesAsync_timeout_with_server_already_applied_is_reconciled_as_success()
    {
        await using var harness = await Harness.CreateAsync();
        var target = DoubledLines();
        harness.Api.OnAmend = _ => Task.FromException<InstallmentAmendLinesResponse>(new TaskCanceledException("timeout"));
        // 服务端已落库：商品内容一致，但行 Guid 被服务端重发，且版本已前进。
        harness.Api.OnGetDetails = () => Task.FromResult(Details(
            T1,
            target.Select(line => line with { InstallmentLineGuid = Guid.NewGuid() }).ToList()));

        var result = await harness.Service.AmendLinesAsync(Session(), harness.Baseline, target);

        Assert.True(result.Succeeded);
        Assert.Equal(T1, result.Order!.UpdatedAt);
        var cached = await harness.Repository.GetAsync(InstallmentGuid);
        Assert.Equal(T1, cached!.UpdatedAt);
    }

    [Fact]
    public async Task AmendLinesAsync_timeout_with_unchanged_server_order_reports_not_applied()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Api.OnAmend = _ => Task.FromException<InstallmentAmendLinesResponse>(new HttpRequestException("reset"));
        harness.Api.OnGetDetails = () => Task.FromResult(Details(T0, OriginalLines()));

        var result = await harness.Service.AmendLinesAsync(Session(), harness.Baseline, DoubledLines());

        Assert.Equal(InstallmentAmendLinesOutcome.Failed, result.Outcome);
        Assert.Equal(T0, result.Order!.UpdatedAt);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AmendLinesAsync_timeout_when_someone_else_changed_the_order_reports_stale()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Api.OnAmend = _ => Task.FromException<InstallmentAmendLinesResponse>(new TaskCanceledException("timeout"));
        harness.Api.OnGetDetails = () => Task.FromResult(Details(T1, [Line(5m, 120m)]));

        var result = await harness.Service.AmendLinesAsync(Session(), harness.Baseline, DoubledLines());

        Assert.Equal(InstallmentAmendLinesOutcome.Stale, result.Outcome);
        Assert.Equal(T1, result.Order!.UpdatedAt);
    }

    [Fact]
    public async Task AmendLinesAsync_timeout_and_failed_reconciliation_is_unknown_not_success()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Api.OnAmend = _ => Task.FromException<InstallmentAmendLinesResponse>(new TaskCanceledException("timeout"));
        harness.Api.OnGetDetails = () => Task.FromException<InstallmentDetailsDto>(new HttpRequestException("still offline"));

        var result = await harness.Service.AmendLinesAsync(Session(), harness.Baseline, DoubledLines());

        Assert.Equal(InstallmentAmendLinesOutcome.Unknown, result.Outcome);
        Assert.Null(result.Order);
        Assert.Contains("重复保存不会重复生效", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmendLinesAsync_response_for_another_order_is_not_written_to_the_snapshot()
    {
        await using var harness = await Harness.CreateAsync();
        var other = Details(T1, DoubledLines()) with { InstallmentGuid = Guid.NewGuid() };
        harness.Api.OnAmend = _ => Task.FromResult(new InstallmentAmendLinesResponse(
            other.InstallmentGuid, InstallmentStatus.Active, 240m, 30m, 210m, other));
        harness.Api.OnGetDetails = () => Task.FromResult(Details(T0, OriginalLines()));

        var result = await harness.Service.AmendLinesAsync(Session(), harness.Baseline, DoubledLines());

        Assert.False(result.Succeeded);
        Assert.Null(await harness.Repository.GetAsync(other.InstallmentGuid));
    }

    [Fact]
    public async Task AmendLinesAsync_caller_cancellation_propagates_instead_of_reconciling()
    {
        await using var harness = await Harness.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        harness.Api.OnAmend = _ =>
        {
            cancellation.Cancel();
            return Task.FromException<InstallmentAmendLinesResponse>(new OperationCanceledException(cancellation.Token));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Service.AmendLinesAsync(Session(), harness.Baseline, DoubledLines(), cancellation.Token));
        Assert.Equal(0, harness.Api.DetailsCallCount);
    }

    [Fact]
    public async Task IsAmendLinesSupportedAsync_follows_the_capabilities_flag_and_fails_closed()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.False(await harness.Service.IsAmendLinesSupportedAsync(Session(isOnline: false)));
        Assert.Equal(0, harness.Api.CapabilitiesCallCount);

        harness.Api.Capabilities = Capabilities(amendLinesSupported: true);
        Assert.True(await harness.Service.IsAmendLinesSupportedAsync(Session()));

        harness.Api.Capabilities = Capabilities(amendLinesSupported: false);
        Assert.False(await harness.Service.IsAmendLinesSupportedAsync(Session()));

        harness.Api.CapabilitiesException = new HttpRequestException("offline");
        Assert.False(await harness.Service.IsAmendLinesSupportedAsync(Session()));
    }

    [Fact]
    public async Task Api_client_posts_amend_lines_with_expected_updated_at()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(ApiResult<InstallmentAmendLinesResponse>.Ok(new InstallmentAmendLinesResponse(
                InstallmentGuid, InstallmentStatus.Active, 240m, 30m, 210m, Details(T1, DoubledLines()))))
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://hbpos.test/") };
        var client = new InstallmentApiClient(httpClient);

        var response = await client.AmendLinesAsync(new InstallmentAmendLinesRequest(
            InstallmentGuid, "S001", "POS-01", "C001", "Alice", DoubledLines(), T0));

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal(
            $"https://hbpos.test/api/v1/installments/{InstallmentGuid:D}/amend-lines",
            handler.LastUri?.AbsoluteUri);
        Assert.Contains("\"expectedUpdatedAt\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"lines\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Equal(210m, response.BalanceAmount);
    }

    [Fact]
    public async Task Api_client_surfaces_the_stale_error_code_from_a_409()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(ApiResult<InstallmentAmendLinesResponse>.Fail(
                InstallmentAmendLinesErrorCodes.Stale, "订单已被修改"))
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://hbpos.test/") };
        var client = new InstallmentApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            client.AmendLinesAsync(new InstallmentAmendLinesRequest(
                InstallmentGuid, "S001", "POS-01", "C001", "Alice", DoubledLines(), T0)));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal(InstallmentAmendLinesErrorCodes.Stale, exception.ErrorCode);
    }

    [Fact]
    public async Task Default_interface_members_keep_older_fakes_working_and_report_not_supported()
    {
        IInstallmentOrderService service = NoopInstallmentOrderService.Instance;

        var online = await service.AmendLinesAsync(Session(), ToLocal(Details(T0, OriginalLines())), DoubledLines());
        var offline = await service.AmendLinesAsync(Session(isOnline: false), ToLocal(Details(T0, OriginalLines())), DoubledLines());

        Assert.Equal(InstallmentAmendLinesOutcome.Rejected, online.Outcome);
        Assert.Equal(InstallmentAmendLinesOutcome.OnlineRequired, offline.Outcome);
        Assert.False(await service.IsAmendLinesSupportedAsync(Session()));
    }

    [Fact]
    public async Task Product_search_prefers_exact_code_matches_and_flags_them()
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([Item("P1", "930001", "Green Tea"), Item("P2", "930002", "Black Tea")]);
        var search = new LocalInstallmentLineProductSearch(index);

        var exact = await search.SearchAsync("S001", " 930002 ");

        Assert.True(exact.IsExactMatch);
        Assert.Equal("P2", Assert.Single(exact.Items).ProductCode);
    }

    [Fact]
    public async Task Product_search_falls_back_to_name_search_with_at_most_eight_candidates()
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(Enumerable.Range(1, 12).Select(number => Item($"P{number}", $"9300{number:00}", $"Tea {number:00}")));
        var search = new LocalInstallmentLineProductSearch(index);

        var fuzzy = await search.SearchAsync("S001", "tea");

        Assert.False(fuzzy.IsExactMatch);
        Assert.Equal(8, fuzzy.Items.Count);
    }

    [Fact]
    public async Task Product_search_is_scoped_to_the_store_and_ignores_blank_queries()
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([Item("P1", "930001", "Green Tea")]);
        var search = new LocalInstallmentLineProductSearch(index);

        Assert.Empty((await search.SearchAsync("S999", "930001")).Items);
        Assert.Empty((await search.SearchAsync("S001", "   ")).Items);
        Assert.Empty((await NoopInstallmentLineProductSearch.Instance.SearchAsync("S001", "930001")).Items);
    }

    private static PosSessionState Session(bool isOnline = true) =>
        new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", isOnline, 0);

    private static InstallmentLineDto Line(decimal quantity, decimal unitPrice) => new(
        LineGuid,
        "SKU-001",
        null,
        "Premium Rice Cooker",
        "690001",
        quantity,
        unitPrice,
        0m,
        InstallmentAmendRules.CalculateActualAmount(quantity, unitPrice, 0m),
        "ITEM-001");

    private static IReadOnlyList<InstallmentLineDto> OriginalLines() => [Line(1m, 120m)];

    private static IReadOnlyList<InstallmentLineDto> DoubledLines() => [Line(2m, 120m)];

    private static SellableItemDto Item(string productCode, string lookupCode, string name) => new(
        "S001",
        productCode,
        null,
        name,
        lookupCode,
        $"ITEM-{productCode}",
        lookupCode,
        10m,
        PriceSourceKind.ProductBase,
        "Product",
        1m,
        DateTimeOffset.UtcNow);

    private static InstallmentRepaymentCapabilitiesResponse Capabilities(bool amendLinesSupported) =>
        new(true, false, false, 120, AmendLinesSupported: amendLinesSupported);

    private static InstallmentDetailsDto Details(
        DateTimeOffset updatedAt,
        IReadOnlyList<InstallmentLineDto> lines,
        decimal paid = 30m,
        InstallmentStatus? status = null)
    {
        var total = InstallmentAmendRules.CalculateTotal(lines);
        return new InstallmentDetailsDto(
            InstallmentGuid,
            "IO-20261001-0001",
            "S001",
            "POS-01",
            "C001",
            "Alice",
            "张三",
            "0400111222",
            T0.AddHours(-1),
            total,
            20m,
            30m,
            paid,
            InstallmentAmendRules.CalculateBalance(total, paid),
            status ?? InstallmentAmendRules.ResolveStatus(total, paid),
            lines,
            [
                new InstallmentPaymentDto(
                    Guid.Parse("12345678-1111-2222-3333-444444444444"),
                    PaymentMethodKind.Cash,
                    paid,
                    null,
                    InstallmentPaymentStatus.Recorded,
                    T0.AddHours(-1),
                    "C001",
                    "POS-01")
            ],
            null,
            UpdatedAt: updatedAt);
    }

    private static LocalInstallmentOrder ToLocal(InstallmentDetailsDto details) => new(
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
        details.UpdatedAt ?? DateTimeOffset.UtcNow,
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

    /// <summary>临时 SQLite 快照库 + 服务 + 假 API 的一次性组合。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _databasePath;

        private Harness(string databasePath, LocalInstallmentOrderRepository repository, FakeAmendApi api)
        {
            _databasePath = databasePath;
            Repository = repository;
            Api = api;
            Service = new InstallmentOrderService(repository, api);
            Baseline = ToLocal(Details(T0, OriginalLines()));
        }

        public LocalInstallmentOrderRepository Repository { get; }

        public FakeAmendApi Api { get; }

        public InstallmentOrderService Service { get; }

        public LocalInstallmentOrder Baseline { get; }

        public static async Task<Harness> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-installment-amend-{Guid.NewGuid():N}.db");
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOrderRepository(store);
            var harness = new Harness(databasePath, repository, new FakeAmendApi());
            await repository.UpsertAsync(harness.Baseline);
            return harness;
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAmendApi : IInstallmentApiClient
    {
        public List<InstallmentAmendLinesRequest> AmendRequests { get; } = [];

        public Func<InstallmentAmendLinesRequest, Task<InstallmentAmendLinesResponse>>? OnAmend { get; set; }

        public Func<Task<InstallmentDetailsDto>>? OnGetDetails { get; set; }

        public int DetailsCallCount { get; private set; }

        public InstallmentRepaymentCapabilitiesResponse? Capabilities { get; set; }

        public Exception? CapabilitiesException { get; set; }

        public int CapabilitiesCallCount { get; private set; }

        public Task<InstallmentAmendLinesResponse> AmendLinesAsync(
            InstallmentAmendLinesRequest request,
            CancellationToken cancellationToken = default)
        {
            AmendRequests.Add(request);
            return OnAmend?.Invoke(request)
                ?? throw new InvalidOperationException("OnAmend was not configured.");
        }

        public Task<InstallmentDetailsDto> GetDetailsAsync(Guid installmentGuid, CancellationToken cancellationToken = default)
        {
            DetailsCallCount++;
            return OnGetDetails?.Invoke()
                ?? throw new InvalidOperationException("OnGetDetails was not configured.");
        }

        public Task<InstallmentRepaymentCapabilitiesResponse> GetRepaymentCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            CapabilitiesCallCount++;
            return CapabilitiesException is not null
                ? Task.FromException<InstallmentRepaymentCapabilitiesResponse>(CapabilitiesException)
                : Task.FromResult(Capabilities ?? throw new InvalidOperationException("Capabilities were not configured."));
        }

        public Task<InstallmentCreateResponse> CreateAsync(InstallmentCreateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentCancelResponse> CancelAsync(InstallmentCancelRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentVoidResponse> VoidAsync(InstallmentVoidRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpMethod? LastMethod { get; private set; }

        public Uri? LastUri { get; private set; }

        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
