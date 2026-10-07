using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Service.Models.HBPOSM_POSM;
using Hbpos.Api.Services;
using Hbpos.Api.Data;
using BlazorApp.Shared.Models.POSM;
using Hbpos.Contracts.Vouchers;
using SqlSugar;

namespace Hbpos.Api.Tests;

public sealed class StoreVoucherServiceTests
{
    [Fact]
    public void StoreVoucher_identity_key_is_mapped_as_database_generated()
    {
        var idColumn = typeof(StoreVoucher)
            .GetProperty(nameof(StoreVoucher.ID))!
            .GetCustomAttribute<SugarColumn>();

        Assert.NotNull(idColumn);
        Assert.True(idColumn!.IsIdentity);
    }

    [Fact]
    public void OrderByExactStoreFirst_UsesSqlServerScalarExpression()
    {
        using var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Server=localhost;Database=hbpos;Trusted_Connection=True;TrustServerCertificate=True",
            DbType = DbType.SqlServer,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = true
        });

        var sql = db.Queryable<StoreVoucher>()
            .OrderByExactStoreFirst("S01")
            .ToSqlString();

        var orderByIndex = sql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
        Assert.True(orderByIndex >= 0, sql);
        var orderBySql = sql[orderByIndex..];
        Assert.True(
            orderBySql.Contains("IIF", StringComparison.OrdinalIgnoreCase)
            || orderBySql.Contains("CASE", StringComparison.OrdinalIgnoreCase),
            orderBySql);
        Assert.DoesNotMatch(
            @"(?i)ORDER\s+BY\s*\(?\s*(?:\[[^\]]+\]\.)?\[?StoreCode\]?\s*=",
            orderBySql);
    }

    [Fact]
    public async Task QueryAsync_ReturnsFoundVoucher()
    {
        var voucher = CreateVoucher(remainingAmount: 12.5m);
        var service = new StoreVoucherService(
            new FakeStoreVoucherRepository(voucher),
            new FakeReservationService());

        var response = await service.QueryAsync("S01", "V001", CancellationToken.None);

        Assert.True(response.Found);
        Assert.NotNull(response.Voucher);
        Assert.Equal("V001", response.Voucher.VoucherCode);
        Assert.Equal(12.5m, response.Voucher.RemainingAmount);
    }

    [Fact]
    public async Task FindAvailableAsync_PrefersStoreVoucherOverGlobalVoucher()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var globalVoucher = CreateVoucher(remainingAmount: 30m);
        globalVoucher.StoreCode = null;
        await fixture.SeedVoucherAsync(globalVoucher);
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var repository = new SqlSugarStoreVoucherRepository(fixture.DbContext);

        var voucher = await repository.FindAvailableAsync("S01", "V001", CancellationToken.None);

        Assert.NotNull(voucher);
        Assert.Equal("S01", voucher!.StoreCode);
        Assert.Equal(10m, voucher.RemainingAmount);
    }

    [Fact]
    public async Task LockAsync_ReturnsPartialAmountWhenExistingReservationReducesAvailability()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var service = new StoreVoucherService(
            new SqlSugarStoreVoucherRepository(fixture.DbContext),
            new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider));

        var first = await service.LockAsync(new StoreVoucherLockRequest("S01", "V001", 6m), CancellationToken.None);
        var second = await service.LockAsync(new StoreVoucherLockRequest("S01", "V001", 6m), CancellationToken.None);

        Assert.Equal(6m, first.LockedAmount);
        Assert.Equal(4m, second.LockedAmount);
        Assert.Equal(4m, first.RemainingAmountAfterLock);
        Assert.Equal(0m, second.RemainingAmountAfterLock);
        Assert.NotEqual(first.ReservationToken, second.ReservationToken);
    }

    [Fact]
    public async Task Reservation_ReserveAsync_PrefersStoreVoucherOverGlobalVoucher()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        var globalVoucher = CreateVoucher(remainingAmount: 30m);
        globalVoucher.StoreCode = null;
        await fixture.SeedVoucherAsync(globalVoucher);
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var service = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);

        var reservation = await service.ReserveAsync(" S01 ", "V001", 4m, 30m, CancellationToken.None);

        Assert.Equal(4m, reservation.LockedAmount);
        Assert.Equal(6m, reservation.RemainingAmountAfterLock);
    }

    [Fact]
    public async Task Reservation_GetAsync_ReturnsOnlyPendingUnexpiredRecords()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var service = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);

        var reservation = await service.ReserveAsync("S01", "V001", 5m, 10m, CancellationToken.None);
        var found = await service.GetAsync(reservation.Token, CancellationToken.None);
        timeProvider.UtcNow = timeProvider.UtcNow.AddMinutes(6);
        var expired = await service.GetAsync(reservation.Token, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Null(expired);
        Assert.NotNull(await fixture.GetReservationEntityAsync(reservation.Token));
    }

    [Fact]
    public async Task Reservation_ExpiredPendingAmount_DoesNotReduceFutureLocks()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var service = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);

        await service.ReserveAsync("S01", "V001", 9m, 10m, CancellationToken.None);
        timeProvider.UtcNow = timeProvider.UtcNow.AddMinutes(6);
        var fresh = await service.ReserveAsync("S01", "V001", 10m, 10m, CancellationToken.None);

        Assert.Equal(10m, fresh.LockedAmount);
    }

    [Fact]
    public async Task ReleaseAsync_MarksReservationReleasedAndRestoresLockableAmount()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var service = new StoreVoucherService(
            new SqlSugarStoreVoucherRepository(fixture.DbContext),
            new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider));

        var locked = await service.LockAsync(new StoreVoucherLockRequest("S01", "V001", 6m), CancellationToken.None);
        var released = await service.ReleaseAsync(
            new StoreVoucherReleaseRequest("S01", "V001", locked.ReservationToken),
            CancellationToken.None);
        var fresh = await service.LockAsync(new StoreVoucherLockRequest("S01", "V001", 10m), CancellationToken.None);
        var releasedEntity = await fixture.GetReservationEntityAsync(locked.ReservationToken);

        Assert.True(released.Released);
        Assert.Equal("V001", released.VoucherCode);
        Assert.Equal(locked.ReservationToken, released.ReservationToken);
        Assert.Equal("released", releasedEntity?.Status);
        Assert.Equal(10m, fresh.LockedAmount);
    }

    [Fact]
    public async Task Reservation_ReleaseAsync_IsIdempotentOnlyForTheSameReleasedReservation()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var service = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);

        var released = await service.ReserveAsync("S01", "V001", 5m, 10m, CancellationToken.None);
        Assert.True(await service.ReleaseAsync(released.Token, "S01", "V001", CancellationToken.None));
        Assert.True(await service.ReleaseAsync(released.Token, "S01", "V001", CancellationToken.None));
        Assert.False(await service.ReleaseAsync(released.Token, "S02", "V001", CancellationToken.None));
        Assert.False(await service.ReleaseAsync(released.Token, "S01", "V002", CancellationToken.None));
        Assert.False(await service.ReleaseAsync("missing-token", "S01", "V001", CancellationToken.None));

        var consumed = await service.ReserveAsync("S01", "V001", 5m, 10m, CancellationToken.None);
        await service.ConsumeAsync(consumed.Token, CancellationToken.None);
        Assert.False(await service.ReleaseAsync(consumed.Token, "S01", "V001", CancellationToken.None));
    }

    [Fact]
    public async Task Reservation_ConsumeAsync_MarksConsumedAndIsIdempotent()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var service = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var reservation = await service.ReserveAsync("S01", "V001", 5m, 10m, CancellationToken.None);

        await service.ConsumeAsync(reservation.Token, CancellationToken.None);
        await service.ConsumeAsync(reservation.Token, CancellationToken.None);

        var found = await service.GetAsync(reservation.Token, CancellationToken.None);
        var entity = await fixture.GetReservationEntityAsync(reservation.Token);
        Assert.Null(found);
        Assert.NotNull(entity);
        Assert.Equal("consumed", entity!.Status);
        Assert.NotNull(entity.ConsumedAtUtc);
    }

    [Fact]
    public async Task Reservation_ClaimAsync_AllowsTokenOnlyOnce()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var service = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var reservation = await service.ReserveAsync("S01", "V001", 5m, 10m, CancellationToken.None);

        var claimed = await service.ClaimAsync(reservation.Token, "S01", "V001", 5m, "ORDER-1", CancellationToken.None);
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ClaimAsync(reservation.Token, "S01", "V001", 5m, "ORDER-2", CancellationToken.None));

        var entity = await fixture.GetReservationEntityAsync(reservation.Token);
        Assert.Equal(reservation.Token, claimed.Token);
        Assert.Contains("already claimed", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await service.GetAsync(reservation.Token, CancellationToken.None));
        Assert.Equal("claimed", entity?.Status);
        Assert.Equal("ORDER-1", entity?.ConsumedByReference);

        await service.ConsumeAsync(reservation.Token, CancellationToken.None);
        var consumed = await fixture.GetReservationEntityAsync(reservation.Token);
        Assert.Equal("consumed", consumed?.Status);
        Assert.Equal("ORDER-1", consumed?.ConsumedByReference);
    }

    [Fact]
    public async Task Reservation_ClaimAsync_RejectsExpiredAndConsumedTokens()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var service = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var expired = await service.ReserveAsync("S01", "V001", 4m, 10m, CancellationToken.None);
        timeProvider.UtcNow = timeProvider.UtcNow.AddMinutes(6);
        var active = await service.ReserveAsync("S01", "V001", 4m, 10m, CancellationToken.None);
        await service.ConsumeAsync(active.Token, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ClaimAsync(expired.Token, "S01", "V001", 4m, "ORDER-EXPIRED", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ClaimAsync(active.Token, "S01", "V001", 4m, "ORDER-CONSUMED", CancellationToken.None));
    }

    [Fact]
    public async Task Reservation_CanBeReadAcrossServiceInstances()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var firstInstance = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var secondInstance = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);

        var reservation = await firstInstance.ReserveAsync("S01", "V001", 5m, 10m, CancellationToken.None);
        var found = await secondInstance.GetAsync(reservation.Token, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(reservation.Token, found!.Token);
    }

    [Fact]
    public async Task RedeemInsideTransactionAsync_UsesAtomicBalanceCondition()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        await RedeemVoucherAsync(fixture, 6m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RedeemVoucherAsync(fixture, 6m));
        var voucher = await fixture.GetVoucherAsync("V001");

        Assert.Contains("balance is not enough", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(voucher);
        Assert.Equal(4m, voucher!.RemainingAmount);
        Assert.Equal("1", voucher.Status);
    }

    [Fact]
    public async Task RedeemInsideTransactionAsync_PrefersStoreVoucherOverGlobalVoucher()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var globalVoucher = CreateVoucher(remainingAmount: 30m);
        globalVoucher.StoreCode = null;
        await fixture.SeedVoucherAsync(globalVoucher);
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));

        await RedeemVoucherAsync(fixture, 4m);

        var storeVoucher = await fixture.GetVoucherAsync("V001", "S01");
        var persistedGlobalVoucher = await fixture.GetVoucherAsync("V001", null);
        Assert.Equal(6m, storeVoucher?.RemainingAmount);
        Assert.Equal(30m, persistedGlobalVoucher?.RemainingAmount);
    }

    [Fact]
    public async Task ClaimAndRedeemInsideTransaction_UsesReservationOnlyOnce()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var reservationService = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var reservation = await reservationService.ReserveAsync("S01", "V001", 5m, 10m, CancellationToken.None);

        await ClaimAndRedeemVoucherAsync(fixture, reservation.Token, 5m, "ORDER-1", timeProvider.UtcNow);
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClaimAndRedeemVoucherAsync(fixture, reservation.Token, 5m, "ORDER-2", timeProvider.UtcNow));

        var voucher = await fixture.GetVoucherAsync("V001");
        var storedReservation = await fixture.GetReservationEntityAsync(reservation.Token);
        Assert.Contains("already claimed", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(voucher);
        Assert.Equal(5m, voucher!.RemainingAmount);
        Assert.Equal("claimed", storedReservation?.Status);
        Assert.Equal("ORDER-1", storedReservation?.ConsumedByReference);
    }

    [Fact]
    public async Task IssueRefundAsync_ExpiresAtEndOfStoreLocalDayNinetyDaysAfterIssue()
    {
        // 悉尼 5 月为 UTC+10：UTC 10:00 = 当地 20:00（05-26），第 90 天是 08-24，取整到当地 23:59:59。
        var time = DateTimeOffset.Parse("2026-05-26T10:00:00Z");
        var expectedExpiry = new DateTimeOffset(2026, 8, 24, 23, 59, 59, TimeSpan.FromHours(10));
        var repository = new FakeStoreVoucherRepository(null)
        {
            CreatedVoucherCode = "RF001"
        };
        var service = new StoreVoucherService(
            repository,
            new FakeReservationService(),
            new FakeTimeProvider(time),
            timeZoneResolver: new StubStoreTimeZoneResolver());

        var response = await service.IssueRefundAsync(
            new StoreVoucherIssueRefundRequest("S01", 18.5m, "C001", IdempotencyKey: "ORDER-1:PAY-1", OrderReference: "ORDER-1", Reason: "Refund"),
            CancellationToken.None);

        Assert.Equal("RF001", response.VoucherCode);
        Assert.Equal(18.5m, response.Amount);
        Assert.Equal(18.5m, response.RemainingAmount);
        Assert.Equal("1", response.Status);
        Assert.Equal(expectedExpiry, response.ExpiredAt);
        Assert.NotNull(repository.LastRefundRequest);
        // 落库的到期时间也必须是门店当地当天结束，不能只是响应里算对。
        Assert.Equal(expectedExpiry, repository.LastRefundRequest!.ExpiredAt);
        Assert.Equal("S01", repository.LastRefundRequest!.StoreCode);
        Assert.Equal("C001", repository.LastRefundRequest.CashierId);
        Assert.Equal("ORDER-1:PAY-1", repository.LastRefundRequest.IdempotencyKey);
        Assert.Equal("ORDER-1", repository.LastRefundRequest.OrderReference);
    }

    [Theory]
    // UTC 15:00 在悉尼已是次日 01:00：发券日要按门店当地日历日算，不是 UTC 日期（08-25，不是 08-24）。
    [InlineData("2026-05-26T15:00:00Z", "Sydney", "2026-08-25T23:59:59+10:00")]
    // 悉尼 3 月仍是夏令时 UTC+11，第 90 天（06-08）已回到标准时间 UTC+10：偏移按到期日当天算。
    [InlineData("2026-03-10T00:00:00Z", "Sydney", "2026-06-08T23:59:59+10:00")]
    // 10-07 发券，第 90 天是 2027-01-05（夏令时 UTC+11），这是业主确认的样例日期。
    [InlineData("2026-10-06T23:30:00Z", "Sydney", "2027-01-05T23:59:59+11:00")]
    // 布里斯班全年 UTC+10 不受夏令时影响；同一时刻在悉尼是 UTC+11，绝对时刻不同。
    [InlineData("2026-12-01T20:00:00Z", "Brisbane", "2027-03-02T23:59:59+10:00")]
    [InlineData("2026-12-01T20:00:00Z", "Sydney", "2027-03-02T23:59:59+11:00")]
    public async Task IssueRefundAsync_ExpiryFollowsStoreTimeZoneCalendarDay(
        string issuedAtUtc,
        string storeZone,
        string expectedExpiry)
    {
        var time = DateTimeOffset.Parse(issuedAtUtc);
        var repository = new FakeStoreVoucherRepository(null) { CreatedVoucherCode = "RF010" };
        var timeZone = storeZone == "Brisbane" ? TestStoreTimeZones.Brisbane : TestStoreTimeZones.Sydney;
        var service = new StoreVoucherService(
            repository,
            new FakeReservationService(),
            new FakeTimeProvider(time),
            timeZoneResolver: new StubStoreTimeZoneResolver(timeZone));

        var response = await service.IssueRefundAsync(
            new StoreVoucherIssueRefundRequest("S01", 10m, "C001", IdempotencyKey: "ORDER-TZ:PAY-1", OrderReference: "ORDER-TZ", Reason: "Refund"),
            CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse(expectedExpiry), response.ExpiredAt);
        Assert.Equal(DateTimeOffset.Parse(expectedExpiry), repository.LastRefundRequest!.ExpiredAt);
    }

    [Fact]
    public async Task IssueRefundAsync_WithoutTimeZoneResolverFallsBackToUtcCalendarDay()
    {
        // 仅单元测试会出现未注入解析器；生产 DI 一定注入，门店未配时区时由解析器回退悉尼。
        var time = DateTimeOffset.Parse("2026-05-26T10:00:00Z");
        var repository = new FakeStoreVoucherRepository(null) { CreatedVoucherCode = "RF011" };
        var service = new StoreVoucherService(
            repository,
            new FakeReservationService(),
            new FakeTimeProvider(time));

        var response = await service.IssueRefundAsync(
            new StoreVoucherIssueRefundRequest("S01", 10m, "C001", IdempotencyKey: "ORDER-UTC:PAY-1", OrderReference: "ORDER-UTC", Reason: "Refund"),
            CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2026-08-24T23:59:59Z"), response.ExpiredAt);
    }

    [Fact]
    public async Task IssueRefundAsync_ReturnsExistingVoucherForSameIdempotencyKey()
    {
        var time = DateTimeOffset.Parse("2026-05-26T10:00:00Z");
        var repository = new FakeStoreVoucherRepository(null)
        {
            CreatedVoucherCode = "RF001"
        };
        var service = new StoreVoucherService(
            repository,
            new FakeReservationService(),
            new FakeTimeProvider(time));
        var request = new StoreVoucherIssueRefundRequest(
            "S01",
            18.5m,
            "C001",
            IdempotencyKey: "ORDER-1:PAY-1",
            OrderReference: "ORDER-1",
            Reason: "Refund");

        var first = await service.IssueRefundAsync(request, CancellationToken.None);
        repository.CreatedVoucherCode = "RF002";
        var second = await service.IssueRefundAsync(request, CancellationToken.None);

        Assert.Equal("RF001", first.VoucherCode);
        Assert.Equal("RF001", second.VoucherCode);
    }

    [Fact]
    public async Task IssueRefundAsync_RejectsReasonThatInjectsARefundMarker()
    {
        var service = new StoreVoucherService(
            new FakeStoreVoucherRepository(null),
            new FakeReservationService(),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.IssueRefundAsync(
            new StoreVoucherIssueRefundRequest(
                "S01", 18.5m, "C001", "ORDER-1:PAY-1", Reason: "RefundKey[forged]"),
            CancellationToken.None));

        Assert.Contains("refund idempotency marker", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueAsync_CreatesIssuedVoucherWithCustomerAndExpiry()
    {
        var time = DateTimeOffset.Parse("2026-05-26T10:00:00Z");
        var expiredAt = time.AddDays(30);
        var repository = new FakeStoreVoucherRepository(null)
        {
            CreatedVoucherCode = "VC001"
        };
        var service = new StoreVoucherService(
            repository,
            new FakeReservationService(),
            new FakeTimeProvider(time));

        var response = await service.IssueAsync(
            new StoreVoucherIssueRequest("S01", 25m, "C001", "ISSUE-1", expiredAt, "CUS001", "Manual issue"),
            CancellationToken.None);

        Assert.Equal("VC001", response.VoucherCode);
        Assert.Equal(25m, response.Amount);
        Assert.Equal(25m, response.RemainingAmount);
        Assert.Equal("1", response.Status);
        Assert.Equal(expiredAt, response.ExpiredAt);
        Assert.Equal("S01", response.StoreCode);
        Assert.Equal("CUS001", response.CustomerCode);
        Assert.NotNull(repository.LastIssueRequest);
        Assert.Equal("ISSUE-1", repository.LastIssueRequest!.IdempotencyKey);
        Assert.Equal("Manual issue", repository.LastIssueRequest.Reason);
    }

    [Fact]
    public async Task IssueAsync_DefaultsToEndOfStoreLocalDayNinetyDaysAfterIssueWhenNoExpiryGiven()
    {
        var time = DateTimeOffset.Parse("2026-05-26T10:00:00Z");
        var expectedExpiry = new DateTimeOffset(2026, 8, 24, 23, 59, 59, TimeSpan.FromHours(10));
        var repository = new FakeStoreVoucherRepository(null)
        {
            CreatedVoucherCode = "VC002"
        };
        var service = new StoreVoucherService(
            repository,
            new FakeReservationService(),
            new FakeTimeProvider(time),
            timeZoneResolver: new StubStoreTimeZoneResolver());

        var response = await service.IssueAsync(
            new StoreVoucherIssueRequest("S01", 25m, "C001", "ISSUE-2", null, "CUS001", "Manual issue"),
            CancellationToken.None);

        Assert.Equal(expectedExpiry, response.ExpiredAt);
        Assert.NotNull(repository.LastIssueRequest);
        Assert.Equal(expectedExpiry, repository.LastIssueRequest!.ExpiredAt);
    }

    [Fact]
    public async Task IssueAsync_ReturnsExistingVoucherForSameStoreAndIdempotencyKey()
    {
        var time = DateTimeOffset.Parse("2026-05-26T10:00:00Z");
        var repository = new FakeStoreVoucherRepository(null)
        {
            CreatedVoucherCode = "VC001"
        };
        var service = new StoreVoucherService(
            repository,
            new FakeReservationService(),
            new FakeTimeProvider(time));
        var request = new StoreVoucherIssueRequest("S01", 25m, "C001", "ISSUE-1", CustomerCode: "CUS001");

        var first = await service.IssueAsync(request, CancellationToken.None);
        repository.CreatedVoucherCode = "VC002";
        var second = await service.IssueAsync(request, CancellationToken.None);

        Assert.Equal("VC001", first.VoucherCode);
        Assert.Equal("VC001", second.VoucherCode);
    }

    private static StoreVoucher CreateVoucher(decimal remainingAmount)
    {
        return new StoreVoucher
        {
            ID = 1,
            StoreCode = "S01",
            VoucherCode = "V001",
            VoucherType = 3,
            Amount = 20m,
            RemainingAmount = remainingAmount,
            Status = "1",
            ExpiredDate = DateTime.UtcNow.AddDays(3),
            CustomerCode = "C01",
            DiscountRate = 0m,
            Remark = "cash voucher",
            IsDelete = false
        };
    }

    private static async Task RedeemVoucherAsync(StoreVoucherSqliteFixture fixture, decimal amount)
    {
        await fixture.Client.Ado.BeginTranAsync();
        try
        {
            await SqlSugarStoreVoucherRepository.RedeemInsideTransactionAsync(
                fixture.Client,
                "S01",
                "V001",
                amount,
                "C001",
                CancellationToken.None);
            await fixture.Client.Ado.CommitTranAsync();
        }
        catch
        {
            await fixture.Client.Ado.RollbackTranAsync();
            throw;
        }
    }

    private static async Task ClaimAndRedeemVoucherAsync(
        StoreVoucherSqliteFixture fixture,
        string reservationToken,
        decimal amount,
        string orderReference,
        DateTimeOffset now)
    {
        await fixture.Client.Ado.BeginTranAsync();
        try
        {
            await SqlSugarStoreVoucherReservationService.ClaimInsideTransactionAsync(
                fixture.Client,
                reservationToken,
                "S01",
                "V001",
                amount,
                orderReference,
                now,
                CancellationToken.None);
            await SqlSugarStoreVoucherRepository.RedeemInsideTransactionAsync(
                fixture.Client,
                "S01",
                "V001",
                amount,
                "C001",
                CancellationToken.None);
            await fixture.Client.Ado.CommitTranAsync();
        }
        catch
        {
            await fixture.Client.Ado.RollbackTranAsync();
            throw;
        }
    }

    private sealed class FakeStoreVoucherRepository(StoreVoucher? voucher) : IStoreVoucherRepository
    {
        private readonly Dictionary<string, StoreVoucher> refundVouchersByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StoreVoucher> issuedVouchersByKey = new(StringComparer.Ordinal);

        public string CreatedVoucherCode { get; set; } = "RF001";

        public RefundVoucherCreateModel? LastRefundRequest { get; private set; }

        public IssuedVoucherCreateModel? LastIssueRequest { get; private set; }

        public Task<StoreVoucher?> FindAvailableAsync(
            string storeCode,
            string voucherCode,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(voucher);
        }

        public Task<StoreVoucher> CreateRefundVoucherAsync(
            RefundVoucherCreateModel request,
            CancellationToken cancellationToken)
        {
            LastRefundRequest = request;
            if (!refundVouchersByKey.TryGetValue(request.IdempotencyKey, out var createdVoucher))
            {
                createdVoucher = new StoreVoucher
                {
                    StoreCode = request.StoreCode,
                    VoucherCode = CreatedVoucherCode,
                    VoucherType = 3,
                    Amount = request.Amount,
                    RemainingAmount = request.Amount,
                    Status = "1",
                    ExpiredDate = request.ExpiredAt.UtcDateTime,
                    CustomerCode = null,
                    DiscountRate = 0m,
                    Remark = $"RefundKey={request.IdempotencyKey}",
                    IsDelete = false
                };
                refundVouchersByKey[request.IdempotencyKey] = createdVoucher;
            }

            return Task.FromResult(createdVoucher);
        }

        public Task<StoreVoucher> CreateIssuedVoucherAsync(
            IssuedVoucherCreateModel request,
            CancellationToken cancellationToken)
        {
            LastIssueRequest = request;
            var key = $"{request.StoreCode ?? string.Empty}:{request.IdempotencyKey}";
            if (!issuedVouchersByKey.TryGetValue(key, out var createdVoucher))
            {
                createdVoucher = new StoreVoucher
                {
                    StoreCode = request.StoreCode,
                    VoucherCode = CreatedVoucherCode,
                    VoucherType = 3,
                    Amount = request.Amount,
                    RemainingAmount = request.Amount,
                    Status = "1",
                    ExpiredDate = request.ExpiredAt.UtcDateTime,
                    CustomerCode = request.CustomerCode,
                    DiscountRate = 0m,
                    Remark = $"IssueKey={request.IdempotencyKey}",
                    IsDelete = false
                };
                issuedVouchersByKey[key] = createdVoucher;
            }

            return Task.FromResult(createdVoucher);
        }
    }

    private sealed class FakeReservationService : IStoreVoucherReservationService
    {
        public Task<StoreVoucherReservation?> GetAsync(string token, CancellationToken cancellationToken)
        {
            return Task.FromResult<StoreVoucherReservation?>(null);
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
            throw new NotSupportedException();
        }

        public Task ConsumeAsync(string token, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<bool> ReleaseAsync(
            string token,
            string storeCode,
            string voucherCode,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableFakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class StoreVoucherSqliteFixture : IAsyncDisposable
    {
        private readonly string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-store-voucher-tests-{Guid.NewGuid():N}.db");
        private readonly SqlSugarClient client;

        private StoreVoucherSqliteFixture()
        {
            client = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={databasePath}",
                DbType = DbType.Sqlite,
                InitKeyType = InitKeyType.Attribute,
                IsAutoCloseConnection = true
            });
            client.CodeFirst.InitTables<StoreVoucher, StoreVoucherReservationEntity>();
            DbContext = CreateDbContext(client);
        }

        public SqlSugarClient Client => client;

        public HbposSqlSugarContext DbContext { get; }

        public static Task<StoreVoucherSqliteFixture> CreateAsync()
        {
            return Task.FromResult(new StoreVoucherSqliteFixture());
        }

        public Task SeedVoucherAsync(StoreVoucher voucher)
        {
            return client.Insertable(voucher).ExecuteCommandAsync();
        }

        public async Task<StoreVoucher?> GetVoucherAsync(string voucherCode)
        {
            return await client.Queryable<StoreVoucher>()
                .Where(x => x.VoucherCode == voucherCode)
                .FirstAsync();
        }

        public async Task<StoreVoucher?> GetVoucherAsync(string voucherCode, string? storeCode)
        {
            var query = client.Queryable<StoreVoucher>()
                .Where(x => x.VoucherCode == voucherCode);
            query = storeCode is null
                ? query.Where(x => x.StoreCode == null || x.StoreCode == string.Empty)
                : query.Where(x => x.StoreCode == storeCode);
            return await query.FirstAsync();
        }

        public async Task<StoreVoucherReservationEntity?> GetReservationEntityAsync(string token)
        {
            return await client.Queryable<StoreVoucherReservationEntity>()
                .Where(x => x.Token == token)
                .FirstAsync();
        }

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            if (File.Exists(databasePath))
            {
                try
                {
                    File.Delete(databasePath);
                }
                catch (IOException)
                {
                    // SQLite 可能短暂占用测试数据库文件，不影响断言结果。
                }
            }

            return ValueTask.CompletedTask;
        }

        private static HbposSqlSugarContext CreateDbContext(ISqlSugarClient posmDb)
        {
            var context = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HbposSqlSugarContext));
            SetAutoProperty(context, nameof(HbposSqlSugarContext.MainDb), posmDb);
            SetAutoProperty(context, nameof(HbposSqlSugarContext.PosmDb), posmDb);
            return context;
        }

        private static void SetAutoProperty(HbposSqlSugarContext context, string propertyName, ISqlSugarClient value)
        {
            var backingField = typeof(HbposSqlSugarContext).GetField(
                $"<{propertyName}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(backingField);
            backingField!.SetValue(context, value);
        }
    }
}
