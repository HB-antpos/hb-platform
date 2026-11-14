using System.Net;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.CashMovements;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

/// <summary>
/// 收银端现金存入 / 取出：汇总口径、新建与冲正校验、并入日结应有现金、日结存档与上传字段、明细上传状态。
/// 用真实 SQLite（临时库 + LocalSchemaService 建表），不依赖 WPF 界面。
/// </summary>
public sealed class CashMovementServiceTests
{
    private static readonly PosSessionState Session = new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    [Fact]
    public void Totals_net_voids_per_reason_and_split_cash_in_and_out()
    {
        var safeDrop = Movement(CashMovementDirection.Out, CashMovementReasons.SafeDrop, 300m);
        var movements = new[]
        {
            Movement(CashMovementDirection.In, CashMovementReasons.ChangeTopUp, 50m),
            safeDrop,
            // 冲正：方向相反、金额相同，与原记录抵消。
            Movement(CashMovementDirection.In, CashMovementReasons.SafeDrop, 300m) with { VoidsMovementGuid = safeDrop.MovementGuid },
            Movement(CashMovementDirection.Out, CashMovementReasons.SafeDrop, 400m),
            Movement(CashMovementDirection.Out, CashMovementReasons.PaidOut, 18.50m),
        };

        var totals = DailyCloseCashMovementTotals.FromMovements(movements);

        Assert.Equal(new DailyCloseCashMovementTotals(50m, 0m, 400m, 18.50m), totals);
        Assert.Equal(50m, totals.CashIn);
        Assert.Equal(418.50m, totals.CashOut);
        Assert.True(totals.HasAny);
        Assert.False(DailyCloseCashMovementTotals.Empty.HasAny);
    }

    [Fact]
    public void Expected_cash_adds_cash_in_and_subtracts_cash_out()
    {
        var report = new DailyCloseReport(
            new DateTime(2026, 11, 13),
            DateTimeOffset.Now,
            DateTimeOffset.Now,
            "S001",
            "POS-01",
            "C001",
            "Alice",
            1,
            [new DailyClosePaymentSummary(PaymentMethodKind.Cash, 1262.40m, 42.85m, 1219.55m, 1)],
            42.85m,
            0m,
            CashRoundingAmount: -0.05m,
            CashMovements: new DailyCloseCashMovementTotals(50m, 0m, 400m, 18.50m));

        // 1,219.55 − 0.05 + 50 − 418.50 = 851.00
        Assert.Equal(851.00m, report.ExpectedCashAmount);
        Assert.False(report.HasSameFiguresAs(report with { CashMovements = null }));
        Assert.True(report.HasSameFiguresAs(report with { }));
    }

    [Theory]
    [InlineData("Unknown", 10, null, "reasonRequired")]
    [InlineData(CashMovementReasons.SafeDrop, 0, null, "invalidAmount")]
    [InlineData(CashMovementReasons.SafeDrop, 10.005, null, "invalidAmount")]
    [InlineData(CashMovementReasons.PaidOut, 18.5, null, "noteRequired")]
    [InlineData(CashMovementReasons.PaidOut, 18.5, " 牛 ", "noteRequired")]
    [InlineData(CashMovementReasons.OtherIn, 5, "", "noteRequired")]
    public async Task CreateAsync_rejects_invalid_drafts(string reason, double amount, string? note, string code)
    {
        await using var fixture = await Fixture.CreateAsync();

        var error = await Assert.ThrowsAsync<CashMovementValidationException>(() =>
            fixture.Service.CreateAsync(Session, new CashMovementDraft(reason, (decimal)amount, note), null));

        Assert.Equal(code, error.Code);
        Assert.Empty(await fixture.ListTodayAsync());
    }

    [Fact]
    public async Task CreateAsync_saves_natural_direction_display_number_and_authorizer()
    {
        await using var fixture = await Fixture.CreateAsync();

        var topUp = await fixture.Service.CreateAsync(Session, new CashMovementDraft(CashMovementReasons.ChangeTopUp, 50m, null), null);
        var drop = await fixture.Service.CreateAsync(
            Session,
            new CashMovementDraft(CashMovementReasons.SafeDrop, 400m, "  交给店长 Amy  "),
            new CashMovementAuthorizer("M001", "Amy"));

        Assert.Equal(CashMovementDirection.In, topUp.Direction);
        Assert.Equal("CM-0001", topUp.DisplayNumber);
        Assert.Equal(CashMovementDirection.Out, drop.Direction);
        Assert.Equal("CM-0002", drop.DisplayNumber);
        Assert.Equal("交给店长 Amy", drop.Note);
        Assert.Equal("Amy", drop.AuthorizedByName);
        Assert.Equal(fixture.Service.Today, drop.BusinessDate);

        var snapshot = await fixture.Service.GetPeriodAsync(Session, fixture.Service.Today);
        Assert.Equal(2, snapshot.Movements.Count);
        Assert.Equal(50m, snapshot.Totals.CashIn);
        Assert.Equal(400m, snapshot.Totals.CashOut);
        Assert.All(snapshot.Movements, item => Assert.Equal(LocalCashMovementUploadStatus.Pending, item.UploadStatus));
    }

    [Fact]
    public async Task VoidAsync_creates_one_reversal_and_blocks_repeat_or_nested_voids()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = await fixture.Service.CreateAsync(Session, new CashMovementDraft(CashMovementReasons.SafeDrop, 300m, null), null);

        var reversal = await fixture.Service.VoidAsync(Session, original, "金额输错", null);

        Assert.Equal(CashMovementDirection.In, reversal.Direction);
        Assert.Equal(CashMovementReasons.SafeDrop, reversal.ReasonCode);
        Assert.Equal(300m, reversal.Amount);
        Assert.Equal(original.MovementGuid, reversal.VoidsMovementGuid);
        var snapshot = await fixture.Service.GetPeriodAsync(Session, fixture.Service.Today);
        Assert.True(snapshot.IsVoided(original));
        Assert.False(snapshot.Totals.HasAny);

        var again = await Assert.ThrowsAsync<CashMovementValidationException>(() =>
            fixture.Service.VoidAsync(Session, original, "再冲一次", null));
        Assert.Equal("alreadyVoided", again.Code);
        var nested = await Assert.ThrowsAsync<CashMovementValidationException>(() =>
            fixture.Service.VoidAsync(Session, reversal, "冲正的冲正", null));
        Assert.Equal("cannotVoidVoid", nested.Code);
    }

    [Fact]
    public async Task VoidAsync_rejects_records_before_a_saved_daily_close_and_other_days()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = await fixture.Service.CreateAsync(Session, new CashMovementDraft(CashMovementReasons.SafeDrop, 100m, null), null);
        var yesterday = original with { MovementGuid = Guid.NewGuid(), BusinessDate = original.BusinessDate.AddDays(-1) };

        var notToday = await Assert.ThrowsAsync<CashMovementValidationException>(() =>
            fixture.Service.VoidAsync(Session, yesterday, "昨天的", null));
        Assert.Equal("notToday", notToday.Code);

        // 记录之后保存了今天的日结：不能再冲正。
        await Task.Delay(20);
        await fixture.DailyClose.SaveAsync(Session, fixture.Service.Today, []);
        var closed = await Assert.ThrowsAsync<CashMovementValidationException>(() =>
            fixture.Service.VoidAsync(Session, original, "日结后", null));
        Assert.Equal("closedAfter", closed.Code);
    }

    [Fact]
    public async Task Daily_close_includes_today_movements_in_expected_cash_archive_and_upload_request()
    {
        await using var fixture = await Fixture.CreateAsync();
        var drop = await fixture.Service.CreateAsync(Session, new CashMovementDraft(CashMovementReasons.SafeDrop, 400m, null), null);
        await fixture.Service.CreateAsync(Session, new CashMovementDraft(CashMovementReasons.PaidOut, 18.50m, "牛奶"), null);
        await fixture.Service.CreateAsync(Session, new CashMovementDraft(CashMovementReasons.ChangeTopUp, 50m, null), null);
        var voided = await fixture.Service.CreateAsync(Session, new CashMovementDraft(CashMovementReasons.SafeDrop, 300m, null), null);
        await fixture.Service.VoidAsync(Session, voided, "金额输错", null);
        // 另一台收银机的记录不算本机的应有现金。
        await fixture.Service.CreateAsync(Session with { DeviceCode = "POS-02" }, new CashMovementDraft(CashMovementReasons.SafeDrop, 999m, null), null);

        var report = await fixture.DailyClose.LoadReportAsync(Session, fixture.Service.Today);
        Assert.Equal(new DailyCloseCashMovementTotals(50m, 0m, 400m, 18.50m), report.CashMovementTotals);
        Assert.Equal(-368.50m, report.ExpectedCashAmount);

        var archive = await fixture.DailyClose.SaveAsync(Session, fixture.Service.Today, [], expectedReport: report);
        var reloaded = Assert.Single(await fixture.DailyClose.GetArchivesAsync(Session, fixture.Service.Today));
        Assert.Equal(report.CashMovementTotals, reloaded.Report.CashMovementTotals);
        Assert.Equal(368.50m, archive.CashDifference);

        var request = DailyCloseSyncRequestMapper.ToRequest(reloaded, "1.0.120");
        Assert.Equal(50m, request.CashInAmount);
        Assert.Equal(418.50m, request.CashOutAmount);
        Assert.Equal(drop.BusinessDate, reloaded.Report.BusinessDate);
    }

    [Fact]
    public async Task Daily_close_detects_a_movement_added_while_counting()
    {
        await using var fixture = await Fixture.CreateAsync();
        var snapshot = await fixture.DailyClose.LoadReportAsync(Session, fixture.Service.Today);

        await fixture.Service.CreateAsync(Session, new CashMovementDraft(CashMovementReasons.SafeDrop, 100m, null), null);

        var changed = await Assert.ThrowsAsync<DailyCloseReportChangedException>(() =>
            fixture.DailyClose.SaveAsync(Session, fixture.Service.Today, [], expectedReport: snapshot));
        Assert.Equal(100m, changed.LatestReport.CashOutAmount);
    }

    [Fact]
    public async Task Upload_marks_accepted_rejected_and_retries_missing_originals()
    {
        await using var fixture = await Fixture.CreateAsync();
        var accepted = await fixture.Service.CreateAsync(Session, new CashMovementDraft(CashMovementReasons.SafeDrop, 10m, null), null);
        var rejected = await fixture.Service.CreateAsync(Session, new CashMovementDraft(CashMovementReasons.SafeDrop, 20m, null), null);
        var retry = await fixture.Service.CreateAsync(Session, new CashMovementDraft(CashMovementReasons.SafeDrop, 30m, null), null);
        var api = new FakeApi(request => new CashMovementSyncResponse(request.Movements.Select(item =>
            item.MovementGuid == rejected.MovementGuid
                ? new CashMovementSyncResult(item.MovementGuid, CashMovementSyncStatuses.Rejected, "INVALID_AMOUNT", "bad")
                : item.MovementGuid == retry.MovementGuid
                    ? new CashMovementSyncResult(item.MovementGuid, CashMovementSyncStatuses.Rejected, "VOIDED_MOVEMENT_NOT_FOUND", "later")
                    : new CashMovementSyncResult(item.MovementGuid, CashMovementSyncStatuses.Accepted, null, null)).ToList()));
        var authorization = new DeviceAuthorizationState();
        var upload = new CashMovementUploadService(fixture.Repository, api, authorization, appVersion: "1.0.120");

        // 没有设备授权：不发请求。
        Assert.Equal(0, await upload.ExecutePendingAsync());
        Assert.Empty(api.Requests);

        authorization.Set(new DeviceAuthorizationContext("POS-01", "S001", "HW", "code"));
        Assert.Equal(1, await upload.ExecutePendingAsync());

        var request = Assert.Single(api.Requests);
        Assert.Equal(CashMovementContractConstants.SchemaVersion, request.SchemaVersion);
        Assert.Equal("Wpf", request.ClientKind);
        Assert.Equal([accepted.MovementGuid, rejected.MovementGuid, retry.MovementGuid], request.Movements.Select(item => item.MovementGuid));
        Assert.Equal("Out", request.Movements[0].Direction);
        var statuses = (await fixture.ListTodayAsync()).ToDictionary(item => item.MovementGuid, item => item.UploadStatus);
        Assert.Equal(LocalCashMovementUploadStatus.Synced, statuses[accepted.MovementGuid]);
        Assert.Equal(LocalCashMovementUploadStatus.Rejected, statuses[rejected.MovementGuid]);
        Assert.Equal(LocalCashMovementUploadStatus.Pending, statuses[retry.MovementGuid]);

        // 退避期内不重发。
        Assert.Equal(0, await upload.ExecutePendingAsync());
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task Upload_keeps_records_pending_on_device_authorization_failure()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateAsync(Session, new CashMovementDraft(CashMovementReasons.SafeDrop, 10m, null), null);
        var api = new FakeApi(_ => throw new DailyCloseUploadApiException("unauthorized", HttpStatusCode.Unauthorized));
        var authorization = new DeviceAuthorizationState();
        authorization.Set(new DeviceAuthorizationContext("POS-01", "S001", "HW", "code"));
        var upload = new CashMovementUploadService(fixture.Repository, api, authorization, appVersion: "1.0.120");

        Assert.Equal(0, await upload.ExecutePendingAsync());
        // 401 不消耗尝试次数、不退避：授权恢复后下一轮立即补传。
        Assert.Equal(0, await upload.ExecutePendingAsync());
        Assert.Equal(2, api.Requests.Count);
        Assert.All(await fixture.ListTodayAsync(), item => Assert.Equal(LocalCashMovementUploadStatus.Pending, item.UploadStatus));
    }

    private static LocalCashMovement Movement(CashMovementDirection direction, string reason, decimal amount) =>
        new(
            Guid.NewGuid(),
            "S001",
            "POS-01",
            direction,
            reason,
            amount,
            null,
            null,
            new DateTime(2026, 11, 13),
            DateTimeOffset.Now,
            "C001",
            "Alice",
            null,
            null,
            "CM-0001");

    private sealed class FakeApi(Func<CashMovementSyncRequest, CashMovementSyncResponse> respond) : ICashMovementSyncApiClient
    {
        public List<CashMovementSyncRequest> Requests { get; } = [];

        public Task<CashMovementSyncResponse> SyncAsync(CashMovementSyncRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string databasePath;

        private Fixture(string databasePath, LocalSqliteStore store)
        {
            this.databasePath = databasePath;
            Repository = new LocalCashMovementRepository(store);
            Service = new CashMovementService(Repository);
            DailyClose = new DailyCloseService(new LocalDailyCloseRepository(store));
        }

        public LocalCashMovementRepository Repository { get; }

        public CashMovementService Service { get; }

        public DailyCloseService DailyClose { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"hbpos-cash-movement-{Guid.NewGuid():N}.db");
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            return new Fixture(path, store);
        }

        public async Task<IReadOnlyList<LocalCashMovement>> ListTodayAsync() =>
            (await Service.GetPeriodAsync(Session, Service.Today)).Movements;

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // 临时库删不掉不影响断言。
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
