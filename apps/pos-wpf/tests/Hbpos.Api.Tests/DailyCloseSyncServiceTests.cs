using System.Text.Json;
using Hbpos.Api.Services;
using Hbpos.Contracts.DailyClose;

namespace Hbpos.Api.Tests;

public sealed class DailyCloseSyncServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 2, 0, 0, TimeSpan.Zero);

    // 悉尼夏令时前的 +10 / 之后的 +11 都无所谓，这里固定用 +11：保存于 10-07 10:30（UTC 为 10-06 23:30）。
    private static readonly TimeSpan StoreOffset = TimeSpan.FromHours(11);
    private static readonly DateTimeOffset SavedAt = new(2026, 10, 7, 10, 30, 0, StoreOffset);

    // ---- 入库 / 幂等 ----

    [Fact]
    public async Task SyncAsync_inserts_new_close_as_full_client_upload()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);

        var response = await service.SyncAsync(CreateRequest(), "S001", "POS-01", CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.False(response.AlreadySynced);
        Assert.False(response.ReplacedPlaceholder);
        var stored = Assert.Single(repository.Records);
        Assert.Equal("S001", stored.StoreCode);
        Assert.Equal("POS-01", stored.DeviceCode);
        Assert.Equal("Wpf", stored.ClientKind);
        Assert.Equal("Full", stored.DetailLevel);
        Assert.Equal("ClientUpload", stored.DataSource);
        Assert.Null(stored.BackfillBatch);
        Assert.False(stored.BusinessDateInferred);
        Assert.Equal(new DateTime(2026, 10, 7), stored.BusinessDate);
        Assert.Equal(new DateTime(2026, 10, 6, 13, 0, 0), stored.PeriodFromUtc);
        Assert.Equal(new DateTime(2026, 10, 7, 13, 0, 0), stored.PeriodToUtc);
        Assert.Equal(new DateTime(2026, 10, 6, 23, 30, 0), stored.SavedAtUtc);
        Assert.Equal("C001", stored.CashierId);
        Assert.Equal("Alice", stored.CashierName);
        Assert.Equal("1.0.47", stored.AppVersion);
        Assert.Equal(12, stored.OrderCount);
        Assert.Equal(1.5m, stored.ReturnQuantity);
        Assert.Equal(500m, stored.CashSalesAmount);
        Assert.Equal(100m, stored.CashRefundAmount);
        Assert.Equal(400m, stored.CashNetAmount);
        Assert.Equal(250.50m, stored.CardSalesAmount);
        Assert.Equal(0m, stored.CardRefundAmount);
        Assert.Equal(250.50m, stored.CardNetAmount);
        Assert.Equal(20m, stored.VoucherSalesAmount);
        Assert.Equal(5m, stored.VoucherRefundAmount);
        Assert.Equal(15m, stored.VoucherNetAmount);
        Assert.Equal(105m, stored.RefundAmount);
        // 应有现金 = 现金净额，不是别的支付方式的合计。
        Assert.Equal(400m, stored.ExpectedCashAmount);
        Assert.Equal(401.35m, stored.CountedCashAmount);
        Assert.Equal(1.35m, stored.CashDifference);
        Assert.Equal(375m, stored.NoteSubtotal);
        Assert.Equal(26.35m, stored.CoinSubtotal);
        Assert.Equal(Now.UtcDateTime, stored.ReceivedAtUtc);
        Assert.Equal(Now.UtcDateTime, stored.UpdatedAtUtc);
    }

    [Fact]
    public async Task SyncAsync_serializes_cash_counts_for_all_denominations_in_descending_order()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);

        await service.SyncAsync(CreateRequest(), "S001", "POS-01", CancellationToken.None);

        var json = Assert.Single(repository.Records).CashCountsJson!;
        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(11, items.Length);
        Assert.Equal(
            [10000, 5000, 2000, 1000, 500, 200, 100, 50, 20, 10, 5],
            items.Select(item => item.GetProperty("denominationCents").GetInt32()).ToArray());
        Assert.Equal(
            [2, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11],
            items.Select(item => item.GetProperty("quantity").GetInt32()).ToArray());
        Assert.StartsWith("[{\"denominationCents\":10000,\"quantity\":2},", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncAsync_accepts_cash_counts_in_any_order()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest();

        await service.SyncAsync(
            request with { CashCounts = request.CashCounts.Reverse().ToArray() },
            "S001",
            "POS-01",
            CancellationToken.None);

        Assert.StartsWith(
            "[{\"denominationCents\":10000,\"quantity\":2},",
            Assert.Single(repository.Records).CashCountsJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncAsync_is_idempotent_for_identical_retry()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest();

        var first = await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);
        var second = await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        Assert.False(first.AlreadySynced);
        Assert.True(second.Accepted);
        Assert.True(second.AlreadySynced);
        Assert.False(second.ReplacedPlaceholder);
        Assert.Single(repository.Records);
    }

    [Fact]
    public async Task SyncAsync_retry_is_equivalent_when_the_same_instants_use_other_offsets()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest();
        await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        var response = await service.SyncAsync(
            request with
            {
                PeriodFrom = request.PeriodFrom.ToUniversalTime(),
                PeriodTo = request.PeriodTo.ToUniversalTime(),
                SavedAt = request.SavedAt.ToUniversalTime()
            },
            "S001",
            "POS-01",
            CancellationToken.None);

        Assert.True(response.AlreadySynced);
    }

    [Fact]
    public async Task SyncAsync_retry_survives_sql_round_trip_shape_of_values()
    {
        // 假仓储读回时模拟 SQL Server：时间 Kind=Unspecified、金额统一补到列的小数位。
        var repository = new FakeRepository { SimulateSqlRoundTrip = true };
        var service = CreateService(repository);
        var request = CreateRequest() with
        {
            SavedAt = SavedAt.AddTicks(1234567),
            ReturnQuantity = 1.5m
        };
        await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        var response = await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        Assert.True(response.AlreadySynced);
    }

    [Fact]
    public async Task SyncAsync_treats_same_content_with_a_different_app_version_as_already_synced()
    {
        // AppVersion 只是上传方的客户端版本元数据：首次上传已被收下、响应丢失后客户端升级重发，
        // 不能因为版本号变了就判内容冲突（那会让客户端把一条服务端早已持有的记录标成永久拒绝）。
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var original = CreateRequest();
        await service.SyncAsync(original, "S001", "POS-01", CancellationToken.None);

        var response = await service.SyncAsync(
            original with { AppVersion = "1.0.48" },
            "S001",
            "POS-01",
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.True(response.AlreadySynced);
        Assert.False(response.ReplacedPlaceholder);
        Assert.Single(repository.Records);
        // 已存数据不被改写：仍是首次上传时的版本。
        Assert.Equal("1.0.47", repository.Records[0].AppVersion);
    }

    [Theory]
    [MemberData(nameof(ContentChanges))]
    public async Task SyncAsync_rejects_same_guid_with_different_content(
        string field,
        Func<DailyCloseSyncRequest, DailyCloseSyncRequest> change)
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var original = CreateRequest();
        await service.SyncAsync(original, "S001", "POS-01", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DailyCloseConflictException>(() =>
            service.SyncAsync(change(original), "S001", "POS-01", CancellationToken.None));

        Assert.True(
            exception.Code == "DAILY_CLOSE_CONTENT_CONFLICT",
            $"{field} 变化应报内容冲突，实际 {exception.Code}");
        // 冲突时不得改写已存在的记录。
        Assert.Single(repository.Records);
        Assert.Equal(401.35m, repository.Records[0].CountedCashAmount);
    }

    public static IEnumerable<object[]> ContentChanges()
    {
        yield return Change("ClientKind", request => request with { ClientKind = "Ipad" });
        yield return Change("BusinessDate", request => request with { BusinessDate = new DateOnly(2026, 10, 6) });
        yield return Change("PeriodFrom", request => request with { PeriodFrom = request.PeriodFrom.AddMinutes(1) });
        yield return Change("PeriodTo", request => request with { PeriodTo = request.PeriodTo.AddMinutes(1) });
        yield return Change("SavedAt", request => request with { SavedAt = request.SavedAt.AddSeconds(1) });
        yield return Change("CashierId", request => request with { CashierId = "C002" });
        yield return Change("CashierName", request => request with { CashierName = "Bob" });
        yield return Change("OrderCount", request => request with { OrderCount = 13 });
        yield return Change("ReturnQuantity", request => request with { ReturnQuantity = 2m });
        yield return Change("RefundAmount", request => request with { RefundAmount = 106m });
        yield return Change(
            "CardSalesAmount",
            request => request with
            {
                Tenders =
                [
                    request.Tenders[0],
                    new DailyCloseTenderSync("Card", 260.50m, 0m, 260.50m),
                    request.Tenders[2]
                ]
            });
        yield return Change(
            "CashCounts",
            request => request with
            {
                CashCounts = request.CashCounts
                    .Select(count => count.DenominationCents == 10000 ? count with { Quantity = 3 } : count)
                    .ToArray(),
                NoteSubtotal = 475m,
                CountedCashAmount = 501.35m,
                CashDifference = 101.35m
            });
    }

    private static object[] Change(string field, Func<DailyCloseSyncRequest, DailyCloseSyncRequest> change) =>
        [field, change];

    // ---- scope ----

    [Fact]
    public async Task SyncAsync_rejects_guid_owned_by_another_device_even_with_identical_content()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest();
        await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DailyCloseConflictException>(() =>
            service.SyncAsync(request with { DeviceCode = "POS-02" }, "S001", "POS-02", CancellationToken.None));

        Assert.Equal("DAILY_CLOSE_SCOPE_CONFLICT", exception.Code);
        Assert.Equal("POS-01", Assert.Single(repository.Records).DeviceCode);
    }

    [Fact]
    public async Task SyncAsync_rejects_guid_owned_by_another_store()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest();
        await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DailyCloseConflictException>(() =>
            service.SyncAsync(request with { StoreCode = "S002" }, "S002", "POS-01", CancellationToken.None));

        Assert.Equal("DAILY_CLOSE_SCOPE_CONFLICT", exception.Code);
    }

    [Fact]
    public async Task SyncAsync_never_overwrites_a_placeholder_owned_by_another_scope()
    {
        var repository = new FakeRepository();
        var request = CreateRequest();
        repository.Records.Add(CreatePlaceholder(request.DailyCloseGuid, "S002", "POS-09"));
        var service = CreateService(repository);

        var exception = await Assert.ThrowsAsync<DailyCloseConflictException>(() =>
            service.SyncAsync(request, "S001", "POS-01", CancellationToken.None));

        Assert.Equal("DAILY_CLOSE_SCOPE_CONFLICT", exception.Code);
        var stored = Assert.Single(repository.Records);
        Assert.Equal("CashOnly", stored.DetailLevel);
        Assert.Equal(0, repository.ReplaceAttempts);
    }

    [Fact]
    public async Task SyncAsync_treats_scope_codes_case_insensitively_like_the_database_collation()
    {
        var repository = new FakeRepository();
        var request = CreateRequest();
        await CreateService(repository).SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        var response = await CreateService(repository).SyncAsync(
            request with { StoreCode = "s001", DeviceCode = "pos-01" },
            "s001",
            "pos-01",
            CancellationToken.None);

        Assert.True(response.AlreadySynced);
    }

    // ---- 回填占位覆盖 ----

    [Theory]
    [InlineData("CashOnly")]
    [InlineData("TraceOnly")]
    public async Task SyncAsync_replaces_backfill_placeholder_with_full_client_data(string detailLevel)
    {
        var repository = new FakeRepository();
        var request = CreateRequest();
        var placeholder = CreatePlaceholder(request.DailyCloseGuid, "S001", "POS-01");
        placeholder.DetailLevel = detailLevel;
        if (detailLevel == "TraceOnly")
        {
            placeholder.CashNetAmount = null;
            placeholder.CountedCashAmount = null;
            placeholder.CashDifference = null;
        }

        repository.Records.Add(placeholder);
        var placeholderId = placeholder.Id;
        var service = CreateService(repository);

        var response = await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.False(response.AlreadySynced);
        Assert.True(response.ReplacedPlaceholder);
        var stored = Assert.Single(repository.Records);
        Assert.Equal(placeholderId, stored.Id);
        Assert.Equal("Full", stored.DetailLevel);
        Assert.Equal("ClientUpload", stored.DataSource);
        Assert.Null(stored.BackfillBatch);
        Assert.False(stored.BusinessDateInferred);
        Assert.Equal("Wpf", stored.ClientKind);
        Assert.Equal(new DateTime(2026, 10, 7), stored.BusinessDate);
        Assert.Equal("Alice", stored.CashierName);
        Assert.Equal(401.35m, stored.CountedCashAmount);
        Assert.Equal(1.35m, stored.CashDifference);
        Assert.NotNull(stored.CashCountsJson);
        Assert.Equal(Now.UtcDateTime, stored.ReceivedAtUtc);
        Assert.Equal(Now.UtcDateTime, stored.UpdatedAtUtc);

        // 覆盖后同一份内容再传一次，就是普通的幂等重复。
        var retry = await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);
        Assert.True(retry.AlreadySynced);
        Assert.False(retry.ReplacedPlaceholder);
    }

    [Fact]
    public async Task SyncAsync_placeholder_replacement_guard_only_matches_placeholders_of_the_same_scope()
    {
        var repository = new FakeRepository();
        var request = CreateRequest();
        repository.Records.Add(CreatePlaceholder(request.DailyCloseGuid, "S001", "POS-01"));
        var service = CreateService(repository);

        await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        Assert.Equal(1, repository.ReplaceAttempts);
        Assert.Equal("S001", repository.LastReplaceScope.StoreCode);
        Assert.Equal("POS-01", repository.LastReplaceScope.DeviceCode);
    }

    // ---- 并发 ----

    [Fact]
    public async Task SyncAsync_returns_already_synced_when_a_concurrent_request_inserts_the_same_content()
    {
        var repository = new FakeRepository();
        var request = CreateRequest();
        var service = CreateService(repository);
        repository.BeforeInsert = attempt =>
        {
            // 第一次插入前，另一个请求抢先把同样的内容插进去了。
            if (attempt == 1)
            {
                repository.Records.Add(ToRecord(request, "S001", "POS-01"));
            }
        };

        var response = await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.True(response.AlreadySynced);
        Assert.Single(repository.Records);
        Assert.Equal(1, repository.InsertAttempts);
    }

    [Fact]
    public async Task SyncAsync_reports_content_conflict_when_a_concurrent_insert_has_different_content()
    {
        var repository = new FakeRepository();
        var request = CreateRequest();
        var service = CreateService(repository);
        repository.BeforeInsert = attempt =>
        {
            if (attempt == 1)
            {
                var other = ToRecord(request, "S001", "POS-01");
                other.OrderCount = 99;
                repository.Records.Add(other);
            }
        };

        var exception = await Assert.ThrowsAsync<DailyCloseConflictException>(() =>
            service.SyncAsync(request, "S001", "POS-01", CancellationToken.None));

        Assert.Equal("DAILY_CLOSE_CONTENT_CONFLICT", exception.Code);
        Assert.Equal(99, Assert.Single(repository.Records).OrderCount);
    }

    [Fact]
    public async Task SyncAsync_rereads_after_a_concurrent_request_already_replaced_the_placeholder()
    {
        var repository = new FakeRepository();
        var request = CreateRequest();
        repository.Records.Add(CreatePlaceholder(request.DailyCloseGuid, "S001", "POS-01"));
        var service = CreateService(repository);
        repository.BeforeReplace = attempt =>
        {
            // 读到占位之后、覆盖之前，另一个请求先把占位换成了同样的完整内容。
            if (attempt == 1)
            {
                repository.Records[0] = ToRecord(request, "S001", "POS-01");
            }
        };

        var response = await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        // 覆盖守卫（DetailLevel <> Full）让本次写入影响 0 行；重读后是等价的 Full，按幂等返回，不重复标记「已覆盖」。
        Assert.True(response.AlreadySynced);
        Assert.False(response.ReplacedPlaceholder);
        Assert.Equal(1, repository.ReplaceAttempts);
        Assert.Single(repository.Records);
    }

    [Fact]
    public async Task SyncAsync_reports_content_conflict_when_a_concurrent_replacement_has_different_content()
    {
        var repository = new FakeRepository();
        var request = CreateRequest();
        repository.Records.Add(CreatePlaceholder(request.DailyCloseGuid, "S001", "POS-01"));
        var service = CreateService(repository);
        repository.BeforeReplace = attempt =>
        {
            if (attempt == 1)
            {
                var other = ToRecord(request, "S001", "POS-01");
                other.CashierName = "Someone Else";
                repository.Records[0] = other;
            }
        };

        var exception = await Assert.ThrowsAsync<DailyCloseConflictException>(() =>
            service.SyncAsync(request, "S001", "POS-01", CancellationToken.None));

        Assert.Equal("DAILY_CLOSE_CONTENT_CONFLICT", exception.Code);
    }

    [Fact]
    public async Task SyncAsync_gives_up_after_five_attempts_with_a_concurrency_conflict()
    {
        var repository = new FakeRepository { AlwaysFailInsertWithoutStoring = true };
        var service = CreateService(repository);

        var exception = await Assert.ThrowsAsync<DailyCloseConflictException>(() =>
            service.SyncAsync(CreateRequest(), "S001", "POS-01", CancellationToken.None));

        Assert.Equal("DAILY_CLOSE_SYNC_CONCURRENT_UPDATE", exception.Code);
        Assert.Equal(5, repository.InsertAttempts);
    }

    [Fact]
    public async Task SyncAsync_gives_up_after_five_failed_placeholder_replacements()
    {
        var repository = new FakeRepository { AlwaysFailReplace = true };
        var request = CreateRequest();
        repository.Records.Add(CreatePlaceholder(request.DailyCloseGuid, "S001", "POS-01"));
        var service = CreateService(repository);

        var exception = await Assert.ThrowsAsync<DailyCloseConflictException>(() =>
            service.SyncAsync(request, "S001", "POS-01", CancellationToken.None));

        Assert.Equal("DAILY_CLOSE_SYNC_CONCURRENT_UPDATE", exception.Code);
        Assert.Equal(5, repository.ReplaceAttempts);
    }

    [Fact]
    public async Task SyncAsync_honors_cancellation_before_touching_the_repository()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SyncAsync(CreateRequest(), "S001", "POS-01", cts.Token));

        Assert.Equal(0, repository.InsertAttempts);
    }

    // ---- 规整 ----

    [Fact]
    public async Task SyncAsync_rounds_amounts_half_away_from_zero_to_two_decimals()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest() with
        {
            Tenders =
            [
                new DailyCloseTenderSync("Cash", 500.005m, 100m, 400.005m),
                new DailyCloseTenderSync("Card", 250.50m, 0m, 250.50m),
                new DailyCloseTenderSync("Voucher", 20m, 5m, 15m)
            ],
            ReturnQuantity = 1.2345m
        };

        await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        var stored = Assert.Single(repository.Records);
        Assert.Equal(500.01m, stored.CashSalesAmount);
        Assert.Equal(400.01m, stored.CashNetAmount);
        Assert.Equal(400.01m, stored.ExpectedCashAmount);
        Assert.Equal(1.235m, stored.ReturnQuantity);
    }

    [Fact]
    public async Task SyncAsync_normalizes_text_fields()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest() with
        {
            ClientKind = "  handheld ",
            CashierId = "  C001  ",
            CashierName = null!,
            AppVersion = "   "
        };

        await service.SyncAsync(request, "  S001 ", " POS-01 ", CancellationToken.None);

        var stored = Assert.Single(repository.Records);
        Assert.Equal("Handheld", stored.ClientKind);
        Assert.Equal("S001", stored.StoreCode);
        Assert.Equal("POS-01", stored.DeviceCode);
        Assert.Equal("C001", stored.CashierId);
        // 收银员姓名 null 等价空串（列是 NOT NULL），AppVersion 空白按未提供处理。
        Assert.Equal(string.Empty, stored.CashierName);
        Assert.Null(stored.AppVersion);
    }

    [Fact]
    public async Task SyncAsync_accepts_empty_cashier_and_all_zero_counts()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        var request = CreateRequest() with
        {
            CashierId = string.Empty,
            CashierName = string.Empty,
            CashCounts = DailyCloseContractConstants.DenominationCents
                .Select(denomination => new DailyCloseCashCountSync(denomination, 0))
                .ToArray(),
            NoteSubtotal = 0m,
            CoinSubtotal = 0m,
            CountedCashAmount = 0m,
            CashDifference = -400m
        };

        await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        var stored = Assert.Single(repository.Records);
        Assert.Equal(string.Empty, stored.CashierId);
        Assert.Equal(0m, stored.CountedCashAmount);
        Assert.Equal(-400m, stored.CashDifference);
    }

    // ---- 校验 ----

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task SyncAsync_rejects_invalid_request_with_specific_code(
        string expectedCode,
        string description,
        Func<DailyCloseSyncRequest, DailyCloseSyncRequest> mutate)
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);

        var exception = await Assert.ThrowsAsync<DailyCloseValidationException>(() =>
            service.SyncAsync(mutate(CreateRequest()), "S001", "POS-01", CancellationToken.None));

        Assert.True(
            exception.Code == expectedCode,
            $"{description}：期望 {expectedCode}，实际 {exception.Code}");
        Assert.Empty(repository.Records);
        Assert.Equal(0, repository.InsertAttempts);
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        yield return Invalid("UNSUPPORTED_SCHEMA_VERSION", "schemaVersion=2", r => r with { SchemaVersion = 2 });
        yield return Invalid("UNSUPPORTED_SCHEMA_VERSION", "schemaVersion=0", r => r with { SchemaVersion = 0 });
        yield return Invalid("DAILY_CLOSE_GUID_REQUIRED", "empty guid", r => r with { DailyCloseGuid = Guid.Empty });

        yield return Invalid("INVALID_CLIENT_KIND", "unknown kind", r => r with { ClientKind = "Web" });
        yield return Invalid("INVALID_CLIENT_KIND", "blank kind", r => r with { ClientKind = " " });
        yield return Invalid("INVALID_CLIENT_KIND", "null kind", r => r with { ClientKind = null! });

        yield return Invalid("BUSINESS_DATE_REQUIRED", "default date", r => r with { BusinessDate = default });
        yield return Invalid("PERIOD_REQUIRED", "default periodFrom", r => r with { PeriodFrom = default });
        yield return Invalid("PERIOD_REQUIRED", "default periodTo", r => r with { PeriodTo = default });
        yield return Invalid("SAVED_AT_REQUIRED", "default savedAt", r => r with { SavedAt = default });
        // SavedAt 的 UTC 日期是 10-06，营业日最晚 10-07；10-08 是错误数据。
        yield return Invalid(
            "INVALID_BUSINESS_DATE",
            "business date two days after saved UTC date",
            r => r with { BusinessDate = new DateOnly(2026, 10, 8) });

        yield return Invalid("CASHIER_ID_TOO_LONG", "cashierId 65 chars", r => r with { CashierId = new string('1', 65) });
        yield return Invalid("CASHIER_NAME_TOO_LONG", "cashierName 129 chars", r => r with { CashierName = new string('N', 129) });
        yield return Invalid("APP_VERSION_TOO_LONG", "appVersion 65 chars", r => r with { AppVersion = new string('v', 65) });
        yield return Invalid("INVALID_ORDER_COUNT", "negative orderCount", r => r with { OrderCount = -1 });
        yield return Invalid(
            "INVALID_RETURN_QUANTITY",
            "returnQuantity too large",
            r => r with { ReturnQuantity = 100_000_000.001m });
        yield return Invalid(
            "AMOUNT_OUT_OF_RANGE",
            "refundAmount above 100,000,000",
            r => r with { RefundAmount = 100_000_000.01m });
        yield return Invalid(
            "AMOUNT_OUT_OF_RANGE",
            "negative refundAmount below -100,000,000",
            r => r with { RefundAmount = -100_000_000.01m });
        yield return Invalid(
            "AMOUNT_OUT_OF_RANGE",
            "tender sales above 100,000,000",
            r => r with
            {
                Tenders = [new("Cash", 100_000_001m, 0m, 100_000_001m), r.Tenders[1], r.Tenders[2]]
            });
        yield return Invalid(
            "AMOUNT_OUT_OF_RANGE",
            "countedCashAmount above 100,000,000",
            r => r with { CountedCashAmount = 100_000_000.5m });
        yield return Invalid(
            "AMOUNT_OUT_OF_RANGE",
            "cashDifference below -100,000,000",
            r => r with { CashDifference = -100_000_001m });

        yield return Invalid("INVALID_TENDERS", "null tenders", r => r with { Tenders = null! });
        yield return Invalid("INVALID_TENDERS", "two tenders", r => r with { Tenders = [r.Tenders[0], r.Tenders[1]] });
        yield return Invalid(
            "INVALID_TENDERS",
            "four tenders",
            r => r with { Tenders = [.. r.Tenders, new DailyCloseTenderSync("Cash", 0m, 0m, 0m)] });
        yield return Invalid(
            "INVALID_TENDERS",
            "duplicate method",
            r => r with { Tenders = [r.Tenders[0], r.Tenders[1], new DailyCloseTenderSync("Card", 0m, 0m, 0m)] });
        yield return Invalid(
            "INVALID_TENDERS",
            "unknown method",
            r => r with { Tenders = [r.Tenders[0], r.Tenders[1], new DailyCloseTenderSync("Cheque", 20m, 5m, 15m)] });
        yield return Invalid(
            "INVALID_TENDERS",
            "null tender entry",
            r => r with { Tenders = [r.Tenders[0], r.Tenders[1], null!] });
        yield return Invalid(
            "TENDER_NET_MISMATCH",
            "net off by 0.02",
            r => r with { Tenders = [r.Tenders[0], new DailyCloseTenderSync("Card", 250.50m, 0m, 250.52m), r.Tenders[2]] });
        yield return Invalid(
            "TENDER_NET_MISMATCH",
            "net ignores refund",
            r => r with { Tenders = [r.Tenders[0], r.Tenders[1], new DailyCloseTenderSync("Voucher", 20m, 5m, 20m)] });

        yield return Invalid("INVALID_CASH_COUNTS", "null counts", r => r with { CashCounts = null! });
        yield return Invalid("INVALID_CASH_COUNTS", "ten counts", r => r with { CashCounts = r.CashCounts.Take(10).ToArray() });
        yield return Invalid(
            "INVALID_CASH_COUNTS",
            "twelve counts",
            r => r with { CashCounts = [.. r.CashCounts, new DailyCloseCashCountSync(10000, 0)] });
        yield return Invalid(
            "INVALID_CASH_COUNTS",
            "duplicate denomination",
            r => r with
            {
                CashCounts = [.. r.CashCounts.Take(10), new DailyCloseCashCountSync(10000, 1)]
            });
        yield return Invalid(
            "INVALID_CASH_COUNTS",
            "unsupported denomination 15",
            r => r with
            {
                CashCounts = [.. r.CashCounts.Take(10), new DailyCloseCashCountSync(15, 11)]
            });
        yield return Invalid(
            "INVALID_CASH_COUNTS",
            "null count entry",
            r => r with { CashCounts = [.. r.CashCounts.Take(10), null!] });
        yield return Invalid(
            "INVALID_CASH_COUNT_QUANTITY",
            "negative quantity",
            r => r with { CashCounts = ReplaceQuantity(r, 5, -1) });
        yield return Invalid(
            "INVALID_CASH_COUNT_QUANTITY",
            "quantity 100001",
            r => r with { CashCounts = ReplaceQuantity(r, 5, 100_001) });

        yield return Invalid(
            "INVALID_NOTE_SUBTOTAL",
            "noteSubtotal off by one cent",
            r => r with { NoteSubtotal = 375.01m });
        yield return Invalid(
            "INVALID_COIN_SUBTOTAL",
            "coinSubtotal off by one cent",
            r => r with { CoinSubtotal = 26.36m });
        yield return Invalid(
            "INVALID_COUNTED_CASH_AMOUNT",
            "counted does not equal sum of counts",
            r => r with { CountedCashAmount = 401.36m });
        yield return Invalid(
            "INVALID_COUNTED_CASH_AMOUNT",
            "counted swapped with a plausible but wrong total",
            r => r with { CountedCashAmount = 400m, CashDifference = 0m });
        yield return Invalid(
            "INVALID_CASH_DIFFERENCE",
            "difference off by 0.02",
            r => r with { CashDifference = 1.37m });
        yield return Invalid(
            "INVALID_CASH_DIFFERENCE",
            "difference sign flipped",
            r => r with { CashDifference = -1.35m });
    }

    private static DailyCloseCashCountSync[] ReplaceQuantity(DailyCloseSyncRequest request, int denominationCents, int quantity)
    {
        return request.CashCounts
            .Select(count => count.DenominationCents == denominationCents ? count with { Quantity = quantity } : count)
            .ToArray();
    }

    private static object[] Invalid(
        string code,
        string description,
        Func<DailyCloseSyncRequest, DailyCloseSyncRequest> mutate) =>
        [code, description, mutate];

    [Theory]
    [InlineData("", "POS-01", "STORE_CODE_REQUIRED")]
    [InlineData("   ", "POS-01", "STORE_CODE_REQUIRED")]
    [InlineData("S0123456789012345678901234567890123", "POS-01", "STORE_CODE_REQUIRED")]
    [InlineData("S001", "", "DEVICE_CODE_REQUIRED")]
    public async Task SyncAsync_rejects_missing_or_overlong_authenticated_scope(
        string storeCode,
        string deviceCode,
        string expectedCode)
    {
        var service = CreateService(new FakeRepository());

        var exception = await Assert.ThrowsAsync<DailyCloseValidationException>(() =>
            service.SyncAsync(CreateRequest(), storeCode, deviceCode, CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public async Task SyncAsync_rejects_overlong_device_code()
    {
        var service = CreateService(new FakeRepository());

        var exception = await Assert.ThrowsAsync<DailyCloseValidationException>(() =>
            service.SyncAsync(CreateRequest(), "S001", new string('D', 65), CancellationToken.None));

        Assert.Equal("DEVICE_CODE_REQUIRED", exception.Code);
    }

    [Fact]
    public async Task SyncAsync_accepts_values_exactly_on_the_validation_boundaries()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);
        // 营业日恰为保存 UTC 日期 +1、字段恰为长度上限、数量恰为 100000、金额恰为 ±100,000,000，都应通过。
        var counts = DailyCloseContractConstants.DenominationCents
            .Select(denomination => new DailyCloseCashCountSync(denomination, denomination == 5 ? 100_000 : 0))
            .ToArray();
        var request = CreateRequest() with
        {
            BusinessDate = new DateOnly(2026, 10, 7),
            CashierId = new string('1', 64),
            CashierName = new string('N', 128),
            AppVersion = new string('v', 64),
            RefundAmount = -100_000_000m,
            CashCounts = counts,
            NoteSubtotal = 0m,
            CoinSubtotal = 5000m,
            CountedCashAmount = 5000m,
            CashDifference = 4600m
        };

        var response = await service.SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal(5000m, Assert.Single(repository.Records).CountedCashAmount);
    }

    [Theory]
    [InlineData("0.01", true)]
    [InlineData("-0.01", true)]
    [InlineData("0.02", false)]
    [InlineData("-0.02", false)]
    public async Task SyncAsync_allows_one_cent_rounding_tolerance_for_net_and_difference(
        string delta,
        bool accepted)
    {
        var offset = decimal.Parse(delta, System.Globalization.CultureInfo.InvariantCulture);
        var service = CreateService(new FakeRepository());
        var request = CreateRequest();
        var netOffByTolerance = request with
        {
            Tenders = [request.Tenders[0], new DailyCloseTenderSync("Card", 250.50m, 0m, 250.50m + offset), request.Tenders[2]]
        };
        var differenceOffByTolerance = request with { CashDifference = request.CashDifference + offset };

        if (accepted)
        {
            Assert.True((await service.SyncAsync(netOffByTolerance, "S001", "POS-01", CancellationToken.None)).Accepted);
            var second = request with { DailyCloseGuid = Guid.NewGuid(), CashDifference = request.CashDifference + offset };
            Assert.True((await service.SyncAsync(second, "S001", "POS-01", CancellationToken.None)).Accepted);
        }
        else
        {
            await Assert.ThrowsAsync<DailyCloseValidationException>(() =>
                service.SyncAsync(netOffByTolerance, "S001", "POS-01", CancellationToken.None));
            await Assert.ThrowsAsync<DailyCloseValidationException>(() =>
                service.SyncAsync(differenceOffByTolerance, "S001", "POS-01", CancellationToken.None));
        }
    }

    // ---- 构造辅助 ----

    private static DailyCloseSyncService CreateService(FakeRepository repository)
    {
        return new DailyCloseSyncService(repository, new FixedTimeProvider(Now));
    }

    /// <summary>
    /// 一份内部自洽的合法请求：纸币 $375.00 + 硬币 $26.35 = 实点 $401.35，现金净额 $400.00，差额 +$1.35（长款）。
    /// </summary>
    private static DailyCloseSyncRequest CreateRequest()
    {
        int[] quantities = [2, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        var counts = DailyCloseContractConstants.DenominationCents
            .Select((denomination, index) => new DailyCloseCashCountSync(denomination, quantities[index]))
            .ToArray();
        return new DailyCloseSyncRequest(
            SchemaVersion: 1,
            DailyCloseGuid: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            StoreCode: "S001",
            DeviceCode: "POS-01",
            ClientKind: "Wpf",
            BusinessDate: new DateOnly(2026, 10, 7),
            PeriodFrom: new DateTimeOffset(2026, 10, 7, 0, 0, 0, StoreOffset),
            PeriodTo: new DateTimeOffset(2026, 10, 8, 0, 0, 0, StoreOffset),
            SavedAt: SavedAt,
            CashierId: "C001",
            CashierName: "Alice",
            AppVersion: "1.0.47",
            OrderCount: 12,
            ReturnQuantity: 1.5m,
            RefundAmount: 105m,
            Tenders:
            [
                new DailyCloseTenderSync("Cash", 500m, 100m, 400m),
                new DailyCloseTenderSync("Card", 250.50m, 0m, 250.50m),
                new DailyCloseTenderSync("Voucher", 20m, 5m, 15m)
            ],
            CashCounts: counts,
            NoteSubtotal: 375m,
            CoinSubtotal: 26.35m,
            CountedCashAmount: 401.35m,
            CashDifference: 1.35m);
    }

    /// <summary>按 CreateRequest 同一规整口径直接造一条已入库的 Full 记录，用来模拟「别的请求先写入」。</summary>
    private static PosmDailyCloseRecord ToRecord(DailyCloseSyncRequest request, string storeCode, string deviceCode)
    {
        var holder = new FakeRepository();
        var service = new DailyCloseSyncService(holder, new FixedTimeProvider(Now));
        service.SyncAsync(request, storeCode, deviceCode, CancellationToken.None).GetAwaiter().GetResult();
        var record = holder.Records.Single();
        record.Id = 1000;
        return record;
    }

    /// <summary>审计回填产生的占位：CashOnly、回填批次、营业日由推断得到，且数值与真实上传不同。</summary>
    private static PosmDailyCloseRecord CreatePlaceholder(Guid guid, string storeCode, string deviceCode)
    {
        return new PosmDailyCloseRecord
        {
            Id = 500,
            DailyCloseGuid = guid,
            StoreCode = storeCode,
            DeviceCode = deviceCode,
            ClientKind = "Ipad",
            DetailLevel = "CashOnly",
            DataSource = "AuditBackfill",
            BackfillBatch = "daily-close-backfill-20261007",
            BusinessDate = new DateTime(2026, 10, 6),
            BusinessDateInferred = true,
            PeriodFromUtc = null,
            PeriodToUtc = null,
            CashierId = string.Empty,
            CashierName = "Backfilled Cashier",
            SavedAtUtc = new DateTime(2026, 10, 6, 23, 31, 0),
            ExpectedCashAmount = 400m,
            CountedCashAmount = 399m,
            CashDifference = -1m,
            ReceivedAtUtc = new DateTime(2026, 10, 6, 1, 0, 0),
            UpdatedAtUtc = new DateTime(2026, 10, 6, 1, 0, 0)
        };
    }

    private sealed class FakeRepository : IDailyCloseRepository
    {
        public List<PosmDailyCloseRecord> Records { get; } = [];

        /// <summary>读回时模拟 SQL Server 的形状：时间 Kind=Unspecified，金额按列小数位补零。</summary>
        public bool SimulateSqlRoundTrip { get; init; }

        public bool AlwaysFailInsertWithoutStoring { get; init; }

        public bool AlwaysFailReplace { get; init; }

        public Action<int>? BeforeInsert { get; set; }

        public Action<int>? BeforeReplace { get; set; }

        public int InsertAttempts { get; private set; }

        public int ReplaceAttempts { get; private set; }

        public (string StoreCode, string DeviceCode) LastReplaceScope { get; private set; }

        public Task<PosmDailyCloseRecord?> GetByGuidAsync(Guid dailyCloseGuid, CancellationToken cancellationToken)
        {
            var record = Records.FirstOrDefault(item => item.DailyCloseGuid == dailyCloseGuid);
            return Task.FromResult(record is null ? null : SimulateSqlRoundTrip ? RoundTrip(record) : Clone(record));
        }

        public Task<bool> TryInsertAsync(PosmDailyCloseRecord record, CancellationToken cancellationToken)
        {
            InsertAttempts++;
            BeforeInsert?.Invoke(InsertAttempts);
            if (AlwaysFailInsertWithoutStoring)
            {
                return Task.FromResult(false);
            }

            // 与真实表一致：DailyCloseGuid 全局唯一，撞唯一键返回 false。
            if (Records.Any(item => item.DailyCloseGuid == record.DailyCloseGuid))
            {
                return Task.FromResult(false);
            }

            record.Id = Records.Count + 1;
            Records.Add(Clone(record));
            return Task.FromResult(true);
        }

        public Task<bool> TryReplacePlaceholderAsync(PosmDailyCloseRecord record, CancellationToken cancellationToken)
        {
            ReplaceAttempts++;
            LastReplaceScope = (record.StoreCode, record.DeviceCode);
            BeforeReplace?.Invoke(ReplaceAttempts);
            if (AlwaysFailReplace)
            {
                return Task.FromResult(false);
            }

            // 与 SQL 的 WHERE 一致：Guid + StoreCode + DeviceCode 命中，且 DetailLevel <> 'Full'。
            var index = Records.FindIndex(item =>
                item.DailyCloseGuid == record.DailyCloseGuid &&
                string.Equals(item.StoreCode, record.StoreCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.DeviceCode, record.DeviceCode, StringComparison.OrdinalIgnoreCase) &&
                item.DetailLevel != "Full");
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            var replacement = Clone(record);
            replacement.Id = Records[index].Id;
            Records[index] = replacement;
            return Task.FromResult(true);
        }

        private static PosmDailyCloseRecord Clone(PosmDailyCloseRecord source) =>
            (PosmDailyCloseRecord)CloneMethod.Invoke(source, null)!;

        private static readonly System.Reflection.MethodInfo CloneMethod =
            typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        private static PosmDailyCloseRecord RoundTrip(PosmDailyCloseRecord source)
        {
            var copy = Clone(source);
            copy.BusinessDate = DateTime.SpecifyKind(source.BusinessDate.Date, DateTimeKind.Unspecified);
            copy.PeriodFromUtc = Unspecified(source.PeriodFromUtc);
            copy.PeriodToUtc = Unspecified(source.PeriodToUtc);
            copy.SavedAtUtc = DateTime.SpecifyKind(source.SavedAtUtc, DateTimeKind.Unspecified);
            copy.ReceivedAtUtc = DateTime.SpecifyKind(source.ReceivedAtUtc, DateTimeKind.Unspecified);
            copy.UpdatedAtUtc = DateTime.SpecifyKind(source.UpdatedAtUtc, DateTimeKind.Unspecified);
            copy.ReturnQuantity = Scale(source.ReturnQuantity, 3);
            copy.CashSalesAmount = Scale(source.CashSalesAmount, 2);
            copy.CashNetAmount = Scale(source.CashNetAmount, 2);
            copy.CountedCashAmount = Scale(source.CountedCashAmount, 2);
            copy.CashDifference = Scale(source.CashDifference, 2);
            return copy;
        }

        private static DateTime? Unspecified(DateTime? value) =>
            value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);

        private static decimal? Scale(decimal? value, int digits) =>
            value is null ? null : decimal.Round(value.Value, digits, MidpointRounding.AwayFromZero);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
