using System.Globalization;
using System.Net;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.DailyClose;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

/// <summary>日结上传服务与 Worker：真实 SQLite 仓储 + 假 API 客户端，参照 Linkly 结算上传测试的风格。</summary>
[Collection(ConsoleLogGlobalStateTestCollection.Name)]
public sealed class DailyCloseUploadServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecutePendingAsync_logs_a_warning_with_code_when_the_server_rejects_permanently()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var guid = await fixture.InsertDailyCloseAsync();
        var client = new FakeDailyCloseSyncApiClient(_ =>
            FakeDailyCloseSyncApiClient.Fail(HttpStatusCode.Conflict, "DAILY_CLOSE_CONTENT_CONFLICT", "server said no"));
        var service = fixture.CreateService(client, new MutableTimeProvider(Now));
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            await service.ExecutePendingAsync();
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        // 永久拒绝后不再自动重试，中心日志必须有一条可按日结 GUID 追溯的 Warning。
        var entry = Assert.Single(sink.Entries, item => item.Level == "Warning" && item.TraceId == guid.ToString("D"));
        Assert.Equal("DailyCloseUpload", entry.Category);
        Assert.Equal(409, entry.StatusCode);
        Assert.Equal("DAILY_CLOSE_CONTENT_CONFLICT", entry.Properties!["errorCode"]);
        Assert.Equal("S001", entry.Properties["storeCode"]);
        Assert.Equal("POS-01", entry.Properties["deviceCode"]);
        Assert.Contains("errorCode=DAILY_CLOSE_CONTENT_CONFLICT http=409 message=server said no", entry.Message);
    }

    [Fact]
    public async Task ExecutePendingAsync_logs_a_throttled_warning_when_the_upload_is_deferred()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var guid = await fixture.InsertDailyCloseAsync();
        var client = new FakeDailyCloseSyncApiClient(_ =>
            FakeDailyCloseSyncApiClient.Fail(HttpStatusCode.ServiceUnavailable, "UPSTREAM_DOWN", "maintenance"));
        var service = fixture.CreateService(client, new MutableTimeProvider(Now));
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            await service.ExecutePendingAsync();
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        // 5xx 退避重试：首次失败必须有可按日结 GUID 追溯的 Warning，并带退避信息。
        var entry = Assert.Single(sink.Entries, item => item.Level == "Warning" && item.TraceId == guid.ToString("D"));
        Assert.Equal("DailyCloseUpload", entry.Category);
        Assert.Equal(503, entry.StatusCode);
        Assert.Equal("api/v1/daily-closes/sync", entry.RequestPath);
        Assert.Equal("UPSTREAM_DOWN", entry.Properties!["errorCode"]);
        Assert.Equal(1, entry.Properties["attemptCount"]);
        Assert.Equal(DailyCloseUploadService.GetRetryDelaySeconds(1), entry.Properties["nextRetrySeconds"]);
        Assert.Contains("daily close upload deferred", entry.Message);
        Assert.Equal("Pending", (await fixture.ReadUploadRowAsync(guid)).Status);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(6, false)]
    [InlineData(8, true)]
    public void IsLoggedAttempt_only_escalates_powers_of_two(int attempt, bool expected)
    {
        Assert.Equal(expected, DailyCloseUploadService.IsLoggedAttempt(attempt));
    }

    [Fact]
    public async Task ExecutePendingAsync_does_not_warn_for_retryable_conflicts()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var guid = await fixture.InsertDailyCloseAsync();
        var client = new FakeDailyCloseSyncApiClient(_ => FakeDailyCloseSyncApiClient.Fail(
            HttpStatusCode.Conflict,
            "DAILY_CLOSE_SYNC_CONCURRENT_UPDATE"));
        var service = fixture.CreateService(client, new MutableTimeProvider(Now));
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            await service.ExecutePendingAsync();
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        // 可重试的冲突会自动退避重试，不该刷 Warning。
        Assert.DoesNotContain(sink.Entries, item => item.Level == "Warning" && item.TraceId == guid.ToString("D"));
    }

    [Fact]
    public async Task ExecutePendingAsync_uploads_a_new_daily_close_and_marks_it_synced()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var guid = await fixture.InsertDailyCloseAsync();
        var client = new FakeDailyCloseSyncApiClient(_ => FakeDailyCloseSyncApiClient.Accepted());
        var service = fixture.CreateService(client, new MutableTimeProvider(Now));

        var result = await service.ExecutePendingAsync();

        Assert.Equal(new DailyCloseUploadExecutionResult(1, 1, 0, 0, false), result);
        var row = await fixture.ReadUploadRowAsync(guid);
        Assert.Equal("Synced", row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Null(row.NextUploadAt);
        Assert.Null(row.ErrorCode);
        Assert.Equal(Now.ToString("O", CultureInfo.InvariantCulture), row.UploadedAt);

        var request = Assert.Single(client.Requests);
        Assert.Equal(DailyCloseContractConstants.SchemaVersion, request.SchemaVersion);
        Assert.Equal(guid, request.DailyCloseGuid);
        Assert.Equal("S001", request.StoreCode);
        Assert.Equal("POS-01", request.DeviceCode);
        Assert.Equal(DailyCloseContractConstants.ClientKindWpf, request.ClientKind);
        Assert.Equal(new DateOnly(2026, 5, 28), request.BusinessDate);
        Assert.Equal("1.2.3-test", request.AppVersion);
        Assert.Equal(11, request.CashCounts.Count);
        Assert.Equal(3, request.Tenders.Count);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task ExecutePendingAsync_treats_accepted_already_synced_and_replaced_placeholder_as_synced(
        bool accepted,
        bool alreadySynced,
        bool replacedPlaceholder)
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var guid = await fixture.InsertDailyCloseAsync();
        var client = new FakeDailyCloseSyncApiClient(_ =>
            Task.FromResult(new DailyCloseSyncResponse(accepted, alreadySynced, replacedPlaceholder)));
        var service = fixture.CreateService(client, new MutableTimeProvider(Now));

        var result = await service.ExecutePendingAsync();

        Assert.Equal(1, result.UploadedCount);
        Assert.Equal("Synced", (await fixture.ReadUploadRowAsync(guid)).Status);
    }

    [Fact]
    public async Task ExecutePendingAsync_retries_a_200_without_any_acceptance_flag_instead_of_rejecting_it()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var guid = await fixture.InsertDailyCloseAsync();
        var client = new FakeDailyCloseSyncApiClient(_ =>
            Task.FromResult(new DailyCloseSyncResponse(false, false, false)));
        var service = fixture.CreateService(client, new MutableTimeProvider(Now));

        var result = await service.ExecutePendingAsync();

        Assert.Equal(1, result.DeferredCount);
        var row = await fixture.ReadUploadRowAsync(guid);
        Assert.Equal("Pending", row.Status);
        Assert.Equal("SYNC_NOT_ACCEPTED", row.ErrorCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_CASH_COUNTS")]
    [InlineData(HttpStatusCode.Conflict, "DAILY_CLOSE_SCOPE_CONFLICT")]
    [InlineData(HttpStatusCode.Conflict, "DAILY_CLOSE_CONTENT_CONFLICT")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, null)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "UNPROCESSABLE")]
    public async Task ExecutePendingAsync_permanently_rejects_invalid_or_conflicting_daily_closes(
        HttpStatusCode statusCode,
        string? errorCode)
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var guid = await fixture.InsertDailyCloseAsync();
        var client = new FakeDailyCloseSyncApiClient(_ =>
            FakeDailyCloseSyncApiClient.Fail(statusCode, errorCode, "server said no"));
        var clock = new MutableTimeProvider(Now);
        var service = fixture.CreateService(client, clock);

        var result = await service.ExecutePendingAsync();

        Assert.Equal(new DailyCloseUploadExecutionResult(1, 0, 1, 0, false), result);
        var row = await fixture.ReadUploadRowAsync(guid);
        Assert.Equal("Rejected", row.Status);
        Assert.Equal(errorCode ?? $"HTTP_{(int)statusCode}", row.ErrorCode);
        Assert.Equal("server said no", row.ErrorMessage);
        Assert.Null(row.NextUploadAt);

        // 永久拒绝不再重试：时间过去很久也不会再次上传。
        clock.Advance(TimeSpan.FromDays(1));
        var again = await service.ExecutePendingAsync();
        Assert.Equal(0, again.AttemptedCount);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task ExecutePendingAsync_keeps_concurrent_update_conflict_pending_with_backoff()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var guid = await fixture.InsertDailyCloseAsync();
        var client = new FakeDailyCloseSyncApiClient(_ => FakeDailyCloseSyncApiClient.Fail(
            HttpStatusCode.Conflict,
            "DAILY_CLOSE_SYNC_CONCURRENT_UPDATE"));
        var service = fixture.CreateService(client, new MutableTimeProvider(Now));

        var result = await service.ExecutePendingAsync();

        Assert.Equal(new DailyCloseUploadExecutionResult(1, 0, 0, 1, false), result);
        var row = await fixture.ReadUploadRowAsync(guid);
        Assert.Equal("Pending", row.Status);
        Assert.Equal("DAILY_CLOSE_SYNC_CONCURRENT_UPDATE", row.ErrorCode);
        Assert.Equal(Now.AddSeconds(5).ToString("O", CultureInfo.InvariantCulture), row.NextUploadAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ExecutePendingAsync_interrupts_the_batch_on_device_authorization_errors_without_burning_attempts(
        HttpStatusCode statusCode)
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var first = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-01");
        var second = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-02");
        var third = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-03");
        var authorized = false;
        var client = new FakeDailyCloseSyncApiClient(_ => authorized
            ? FakeDailyCloseSyncApiClient.Accepted()
            : FakeDailyCloseSyncApiClient.Fail(statusCode, "DEVICE_AUTH_REQUIRED", "device not authorized"));
        var service = fixture.CreateService(client, new MutableTimeProvider(Now));

        var interrupted = await service.ExecutePendingAsync();

        // 只发了一次请求就中断：后面的记录必然同样失败，不再浪费请求。
        Assert.True(interrupted.WasInterrupted);
        Assert.Equal(1, interrupted.AttemptedCount);
        Assert.Single(client.Requests);
        foreach (var guid in new[] { first, second, third })
        {
            var row = await fixture.ReadUploadRowAsync(guid);
            Assert.Equal("Pending", row.Status);
            // 授权问题不是记录自己的失败：尝试次数仍为 0，也没有退避时间，授权恢复后立即可传。
            Assert.Equal(0, row.AttemptCount);
            Assert.Null(row.NextUploadAt);
        }

        Assert.Equal("DEVICE_AUTH_REQUIRED", (await fixture.ReadUploadRowAsync(first)).ErrorCode);

        authorized = true;
        var recovered = await service.ExecutePendingAsync();
        Assert.Equal(3, recovered.UploadedCount);
        Assert.False(recovered.WasInterrupted);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    // 服务端还没部署上传接口（旧版本服务端）时是 404：必须重试，不能当成永久拒绝。
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ExecutePendingAsync_retries_transient_http_errors_with_backoff(HttpStatusCode statusCode)
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var guid = await fixture.InsertDailyCloseAsync();
        var client = new FakeDailyCloseSyncApiClient(_ => FakeDailyCloseSyncApiClient.Fail(statusCode));
        var service = fixture.CreateService(client, new MutableTimeProvider(Now));

        var result = await service.ExecutePendingAsync();

        Assert.Equal(new DailyCloseUploadExecutionResult(1, 0, 0, 1, false), result);
        var row = await fixture.ReadUploadRowAsync(guid);
        Assert.Equal("Pending", row.Status);
        Assert.Equal($"HTTP_{(int)statusCode}", row.ErrorCode);
        Assert.Equal(Now.AddSeconds(5).ToString("O", CultureInfo.InvariantCulture), row.NextUploadAt);
    }

    [Fact]
    public async Task ExecutePendingAsync_retries_network_timeout_and_unexpected_exceptions_with_backoff()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var network = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-01");
        var timeout = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-02");
        var unexpected = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-03");
        var client = new FakeDailyCloseSyncApiClient(request =>
        {
            if (request.DailyCloseGuid == network)
            {
                return Task.FromException<DailyCloseSyncResponse>(new HttpRequestException("connection refused"));
            }

            return request.DailyCloseGuid == timeout
                ? Task.FromException<DailyCloseSyncResponse>(new TaskCanceledException("timeout"))
                : Task.FromException<DailyCloseSyncResponse>(new InvalidOperationException("boom"));
        });
        var service = fixture.CreateService(client, new MutableTimeProvider(Now));

        var result = await service.ExecutePendingAsync();

        Assert.Equal(new DailyCloseUploadExecutionResult(3, 0, 0, 3, false), result);
        Assert.Equal("NETWORK", (await fixture.ReadUploadRowAsync(network)).ErrorCode);
        Assert.Equal("REQUEST_CANCELED", (await fixture.ReadUploadRowAsync(timeout)).ErrorCode);
        Assert.Equal("UPLOAD_EXCEPTION", (await fixture.ReadUploadRowAsync(unexpected)).ErrorCode);
        foreach (var guid in new[] { network, timeout, unexpected })
        {
            Assert.Equal("Pending", (await fixture.ReadUploadRowAsync(guid)).Status);
        }
    }

    [Fact]
    public async Task ExecutePendingAsync_backs_off_five_seconds_doubling_up_to_three_hundred_seconds_then_succeeds()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var guid = await fixture.InsertDailyCloseAsync();
        var fail = true;
        var client = new FakeDailyCloseSyncApiClient(_ => fail
            ? FakeDailyCloseSyncApiClient.Fail(HttpStatusCode.ServiceUnavailable)
            : FakeDailyCloseSyncApiClient.Accepted());
        var clock = new MutableTimeProvider(Now);
        var service = fixture.CreateService(client, clock);

        var expectedDelays = new[] { 5, 10, 20, 40, 80, 160, 300, 300 };
        for (var attempt = 1; attempt <= expectedDelays.Length; attempt++)
        {
            await service.ExecutePendingAsync();
            var row = await fixture.ReadUploadRowAsync(guid);
            Assert.Equal(attempt, row.AttemptCount);
            Assert.Equal(
                clock.GetUtcNow().AddSeconds(expectedDelays[attempt - 1]).ToString("O", CultureInfo.InvariantCulture),
                row.NextUploadAt);

            // 退避未到期时不会再次发请求。
            var requestsBefore = client.Requests.Count;
            clock.Advance(TimeSpan.FromSeconds(expectedDelays[attempt - 1] - 1));
            Assert.Equal(0, (await service.ExecutePendingAsync()).AttemptedCount);
            Assert.Equal(requestsBefore, client.Requests.Count);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        fail = false;
        var recovered = await service.ExecutePendingAsync();
        Assert.Equal(1, recovered.UploadedCount);
        var synced = await fixture.ReadUploadRowAsync(guid);
        Assert.Equal("Synced", synced.Status);
        Assert.Equal(expectedDelays.Length + 1, synced.AttemptCount);
    }

    [Fact]
    public async Task ExecutePendingAsync_recovers_only_an_expired_upload_lease()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var stale = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-01");
        var active = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-02");
        Assert.NotNull(await fixture.Repository.TryClaimUploadAsync(
            stale,
            "S001",
            "POS-01",
            Now - DailyCloseUploadService.UploadLeaseTimeout - TimeSpan.FromSeconds(1)));
        Assert.NotNull(await fixture.Repository.TryClaimUploadAsync(active, "S001", "POS-01", Now - TimeSpan.FromSeconds(10)));
        var client = new FakeDailyCloseSyncApiClient(_ => FakeDailyCloseSyncApiClient.Accepted());
        var service = fixture.CreateService(client, new MutableTimeProvider(Now));

        var result = await service.ExecutePendingAsync();

        // 过期租约被回收后立即补传；仍有效的租约（别的进程正在上传）不被抢走。
        Assert.Equal([stale], client.Requests.Select(request => request.DailyCloseGuid));
        Assert.Equal(1, result.UploadedCount);
        Assert.Equal("Synced", (await fixture.ReadUploadRowAsync(stale)).Status);
        Assert.Equal("Uploading", (await fixture.ReadUploadRowAsync(active)).Status);
    }

    [Fact]
    public async Task ExecutePendingAsync_only_uploads_rows_of_the_current_device_scope()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var mine = await fixture.InsertDailyCloseAsync();
        var otherDevice = await fixture.InsertDailyCloseAsync(deviceCode: "POS-02");
        var otherStore = await fixture.InsertDailyCloseAsync(storeCode: "S002");
        var client = new FakeDailyCloseSyncApiClient(_ => FakeDailyCloseSyncApiClient.Accepted());
        var service = fixture.CreateService(client, new MutableTimeProvider(Now));

        var result = await service.ExecutePendingAsync();

        Assert.Equal(1, result.UploadedCount);
        Assert.Equal([mine], client.Requests.Select(request => request.DailyCloseGuid));
        foreach (var guid in new[] { otherDevice, otherStore })
        {
            // 别的范围的行完全不动：保持 Pending、尝试 0 次，设备换回去后仍可上传。
            var row = await fixture.ReadUploadRowAsync(guid);
            Assert.Equal("Pending", row.Status);
            Assert.Equal(0, row.AttemptCount);
        }
    }

    [Fact]
    public async Task ExecutePendingAsync_does_nothing_until_the_device_is_authorized_then_follows_a_rebound_device()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var oldDevice = await fixture.InsertDailyCloseAsync();
        var newDevice = await fixture.InsertDailyCloseAsync(storeCode: "S009", deviceCode: "POS-09");
        var authorization = new DeviceAuthorizationState();
        var client = new FakeDailyCloseSyncApiClient(_ => FakeDailyCloseSyncApiClient.Accepted());
        var service = fixture.CreateService(client, new MutableTimeProvider(Now), authorization);

        // 没有设备授权：不发请求、不消耗尝试次数。
        var unauthorized = await service.ExecutePendingAsync();
        Assert.Equal(new DailyCloseUploadExecutionResult(0, 0, 0, 0, false), unauthorized);
        Assert.Empty(client.Requests);
        Assert.Equal(0, (await fixture.ReadUploadRowAsync(oldDevice)).AttemptCount);

        // 设备换绑到 S009/POS-09：只传新范围的行，旧范围的行不动。
        authorization.Set(new DeviceAuthorizationContext("POS-09", "S009", "HW", "code"));
        var rebound = await service.ExecutePendingAsync();
        Assert.Equal(1, rebound.UploadedCount);
        Assert.Equal([newDevice], client.Requests.Select(request => request.DailyCloseGuid));
        Assert.Equal("Pending", (await fixture.ReadUploadRowAsync(oldDevice)).Status);
        var request = Assert.Single(client.Requests);
        Assert.Equal("S009", request.StoreCode);
        Assert.Equal("POS-09", request.DeviceCode);
    }

    [Fact]
    public async Task ExecutePendingAsync_loops_in_batches_of_twenty_until_the_queue_is_empty()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var guids = new List<Guid>();
        for (var index = 0; index < 45; index++)
        {
            guids.Add(await fixture.InsertDailyCloseAsync(businessDate: new DateTime(2026, 1, 1).AddDays(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        var client = new FakeDailyCloseSyncApiClient(_ => FakeDailyCloseSyncApiClient.Accepted());
        var service = fixture.CreateService(client, new MutableTimeProvider(Now));

        // 默认批次 20：45 条要循环 3 轮才取空，一次调用必须全部传完，而不是只传第一批。
        var result = await RunWithTimeoutAsync(service);

        Assert.Equal(new DailyCloseUploadExecutionResult(45, 45, 0, 0, false), result);
        Assert.Equal(45, client.Requests.Count);
        Assert.Equal(guids.OrderBy(guid => guid), client.Requests.Select(request => request.DailyCloseGuid).OrderBy(guid => guid));
        foreach (var guid in guids)
        {
            Assert.Equal("Synced", (await fixture.ReadUploadRowAsync(guid)).Status);
        }
    }

    [Fact]
    public async Task ExecutePendingAsync_does_not_retry_a_failed_row_again_within_the_same_run()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var failing = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-01");
        var healthy = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-02");
        var client = new FakeDailyCloseSyncApiClient(request => request.DailyCloseGuid == failing
            ? FakeDailyCloseSyncApiClient.Fail(HttpStatusCode.ServiceUnavailable)
            : FakeDailyCloseSyncApiClient.Accepted());
        // 真实时钟：失败行的退避时间（5 秒）在同一次执行里不会到期，不会被反复取出。
        var service = fixture.CreateService(client);

        var result = await RunWithTimeoutAsync(service);

        Assert.Equal(2, result.AttemptedCount);
        Assert.Equal(1, result.UploadedCount);
        Assert.Equal(1, result.DeferredCount);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal("Pending", (await fixture.ReadUploadRowAsync(failing)).Status);
        Assert.Equal("Synced", (await fixture.ReadUploadRowAsync(healthy)).Status);
    }

    [Fact]
    public async Task ExecutePendingAsync_one_row_failing_does_not_affect_the_rest_of_the_batch()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var first = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-01");
        var second = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-02");
        var third = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-03");
        var client = new FakeDailyCloseSyncApiClient(request => request.DailyCloseGuid == second
            ? Task.FromException<DailyCloseSyncResponse>(new InvalidOperationException("one bad row"))
            : FakeDailyCloseSyncApiClient.Accepted());
        var service = fixture.CreateService(client, new MutableTimeProvider(Now));

        var result = await service.ExecutePendingAsync();

        Assert.Equal(new DailyCloseUploadExecutionResult(3, 2, 0, 1, false), result);
        Assert.Equal("Synced", (await fixture.ReadUploadRowAsync(first)).Status);
        Assert.Equal("Pending", (await fixture.ReadUploadRowAsync(second)).Status);
        Assert.Equal("Synced", (await fixture.ReadUploadRowAsync(third)).Status);
    }

    [Fact]
    public async Task ExecutePendingAsync_survives_a_claim_failure_on_one_row_and_still_terminates()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var poison = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-01");
        var healthy = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-02");
        var client = new FakeDailyCloseSyncApiClient(_ => FakeDailyCloseSyncApiClient.Accepted());
        var service = fixture.CreateService(
            client,
            new MutableTimeProvider(Now),
            repository: new ThrowingClaimRepository(fixture.Repository, poison));

        // 认领阶段抛异常的记录会一直留在"到期"结果里；执行必须仍然终止，且不影响其余记录。
        var result = await RunWithTimeoutAsync(service);

        Assert.Equal(1, result.UploadedCount);
        Assert.Equal(1, result.DeferredCount);
        Assert.Equal([healthy], client.Requests.Select(request => request.DailyCloseGuid));
        Assert.Equal("Pending", (await fixture.ReadUploadRowAsync(poison)).Status);
        Assert.Equal("Synced", (await fixture.ReadUploadRowAsync(healthy)).Status);
    }

    [Fact]
    public async Task Upgraded_database_uploads_every_historical_daily_close_with_full_denominations()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-daily-close-backfill-{Guid.NewGuid():N}.db");
        try
        {
            // 旧库：只有旧版 LocalDailyCloses 表（无上传列），里面是历史存档。
            var store = new LocalSqliteStore(databasePath);
            var historicalGuids = new[] { Guid.NewGuid(), Guid.NewGuid() };
            await using (var connection = await store.OpenConnectionAsync())
            {
                await ExecuteAsync(connection, LegacyTableSql);
                foreach (var guid in historicalGuids)
                {
                    await ExecuteAsync(connection, LegacyRowSql(guid));
                }

                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE LocalDailyCloseCashCounts (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT, DailyCloseGuid TEXT NOT NULL, DenominationValue TEXT NOT NULL,
                        Label TEXT NOT NULL, Kind INTEGER NOT NULL, Quantity INTEGER NOT NULL, Amount TEXT NOT NULL);
                    """);
                foreach (var guid in historicalGuids)
                {
                    // 历史明细只落了有数量的面额，其余档位没有行（补传时补 0）。
                    await ExecuteAsync(
                        connection,
                        $"""
                        INSERT INTO LocalDailyCloseCashCounts (DailyCloseGuid, DenominationValue, Label, Kind, Quantity, Amount)
                        VALUES ('{guid}', '100', '$100', 1, 1, '100'), ('{guid}', '0.05', '5c', 2, 3, '0.15');
                        """);
                }
            }

            // 新版本启动：补列 → Worker 才开放上传 → 历史行自动 Pending 且立即到期，被逐条补传。
            await new LocalSchemaService(store).InitializeAsync();
            var client = new FakeDailyCloseSyncApiClient(_ => FakeDailyCloseSyncApiClient.Accepted());
            var service = new DailyCloseUploadService(
                new LocalDailyCloseRepository(store),
                client,
                DailyCloseUploadFixture.Authorized("S001", "POS-01"),
                new MutableTimeProvider(Now),
                appVersion: "9.9.9");

            var result = await service.ExecutePendingAsync();

            Assert.Equal(2, result.UploadedCount);
            Assert.Equal(historicalGuids.OrderBy(guid => guid), client.Requests.Select(request => request.DailyCloseGuid).OrderBy(guid => guid));
            // 历史行带完整面额：$100×1、5c×3，其余 9 档补 0，共 11 档；并且满足服务端的一致性校验。
            Assert.All(client.Requests, request =>
            {
                Assert.Equal(11, request.CashCounts.Count);
                Assert.Equal(1, request.CashCounts.Single(count => count.DenominationCents == 10000).Quantity);
                Assert.Equal(3, request.CashCounts.Single(count => count.DenominationCents == 5).Quantity);
                Assert.Null(DailyCloseServerRuleReplica.FirstViolation(request, "S001", "POS-01"));
            });
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Worker_runs_without_background_service_execute_async_and_wakes_up_on_request()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var executor = new CountingExecutor();
        var schema = new LocalSchemaService(fixture.Store);
        await schema.InitializeAsync();
        schema.SignalReady();
        using var worker = new DailyCloseUploadWorker(schema, executor);
        using var stopping = new CancellationTokenSource();

        // StartAsync 自己启动循环（Hosting 10 可能不执行 ExecuteAsync）：启动后立即执行一轮。
        await worker.StartAsync(stopping.Token);
        await WaitUntilAsync(() => executor.CallCount >= 1);

        // 保存日结后的唤醒：不用等 30 秒轮询就会再执行一轮。
        worker.RequestUpload();
        await WaitUntilAsync(() => executor.CallCount >= 2);

        stopping.Cancel();
        await worker.StopAsync(CancellationToken.None).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
    }

    [Fact]
    public async Task Worker_continues_after_a_single_execution_failure()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var executor = new CountingExecutor { FailFirstCall = true };
        var schema = new LocalSchemaService(fixture.Store);
        await schema.InitializeAsync();
        schema.SignalReady();
        using var worker = new DailyCloseUploadWorker(schema, executor);
        using var stopping = new CancellationTokenSource();

        await worker.StartAsync(stopping.Token);
        await WaitUntilAsync(() => executor.CallCount >= 1);
        worker.RequestUpload();
        // 一次执行抛异常（SQLite 锁、网络瞬断）不能终止 Worker：下一次唤醒照常执行。
        await WaitUntilAsync(() => executor.CallCount >= 2);

        stopping.Cancel();
        await worker.StopAsync(CancellationToken.None).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
    }

    [Fact]
    public async Task Worker_waits_for_the_main_schema_ready_signal_without_initializing_itself()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var schema = new LocalSchemaService(fixture.Store);
        var executor = new CountingExecutor();
        using var worker = new DailyCloseUploadWorker(schema, executor);
        using var stopping = new CancellationTokenSource();

        await worker.StartAsync(stopping.Token).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        // 反向等待：schema 没就绪时 Worker 不能开始上传（旧库补列在 schema 初始化里完成）。
        await Task.Delay(150);
        Assert.Equal(0, executor.CallCount);
        schema.SignalReady();
        await WaitUntilAsync(() => executor.CallCount >= 1);

        stopping.Cancel();
        await worker.StopAsync(CancellationToken.None).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
    }

    [Fact]
    public async Task Request_upload_after_the_worker_is_disposed_does_not_throw()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var worker = new DailyCloseUploadWorker(new LocalSchemaService(fixture.Store), new CountingExecutor());

        worker.Dispose();

        // 保存日结时调用 RequestUpload：Worker 已释放也只能静默忽略。
        worker.RequestUpload();
    }

    [Fact]
    public async Task Saving_a_daily_close_wakes_the_real_worker_and_uploads_it_without_waiting_for_the_poll()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var client = new FakeDailyCloseSyncApiClient(_ => FakeDailyCloseSyncApiClient.Accepted());
        var uploadService = fixture.CreateService(client);
        var schema = new LocalSchemaService(fixture.Store);
        await schema.InitializeAsync();
        schema.SignalReady();
        using var worker = new DailyCloseUploadWorker(schema, uploadService);
        using var stopping = new CancellationTokenSource();
        var closeService = new DailyCloseService(fixture.Repository, uploadScheduler: worker);

        await worker.StartAsync(stopping.Token);
        // 启动时的首轮空跑结束之后再保存：此后只有"保存后唤醒"能让它在 30 秒轮询之前上传。
        await Task.Delay(300);
        var archive = await closeService.SaveAsync(
            DailyCloseTestData.Session(),
            new DateTime(2026, 5, 28),
            DailyCloseTestData.AllDenominationCounts());

        await WaitUntilAsync(
            async () => (await fixture.ReadUploadRowAsync(archive.DailyCloseGuid)).Status == "Synced",
            TimeSpan.FromSeconds(10));
        Assert.Equal(archive.DailyCloseGuid, Assert.Single(client.Requests).DailyCloseGuid);

        stopping.Cancel();
        await worker.StopAsync(CancellationToken.None).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
    }

    /// <summary>
    /// 在线程池线程上执行并带超时：SQLite 的异步调用实际是同步完成的，循环终止守卫一旦回归，
    /// 直接 await 会把测试线程卡死（只会挂住而不是失败）；放到线程池线程上才能让超时生效，把回归变成失败。
    /// </summary>
    private static Task<DailyCloseUploadExecutionResult> RunWithTimeoutAsync(DailyCloseUploadService service)
    {
        return Task.Run(() => service.ExecutePendingAsync()).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
    }

    private const string LegacyTableSql = """
        CREATE TABLE LocalDailyCloses (
            DailyCloseGuid TEXT PRIMARY KEY, StoreCode TEXT NOT NULL, DeviceCode TEXT NOT NULL, CashierId TEXT NOT NULL,
            CashierName TEXT NOT NULL, BusinessDate TEXT NOT NULL, PeriodFrom TEXT NOT NULL, PeriodTo TEXT NOT NULL,
            SavedAt TEXT NOT NULL, OrderCount INTEGER NOT NULL, CashSalesAmount TEXT NOT NULL, CashRefundAmount TEXT NOT NULL,
            CashNetAmount TEXT NOT NULL, CardSalesAmount TEXT NOT NULL, CardRefundAmount TEXT NOT NULL, CardNetAmount TEXT NOT NULL,
            VoucherSalesAmount TEXT NOT NULL, VoucherRefundAmount TEXT NOT NULL, VoucherNetAmount TEXT NOT NULL,
            RefundAmount TEXT NOT NULL, ReturnQuantity TEXT NOT NULL, NoteSubtotal TEXT NOT NULL, CoinSubtotal TEXT NOT NULL,
            CountedCashAmount TEXT NOT NULL, CashDifference TEXT NOT NULL
        );
        """;

    // 一条自洽的历史存档：现金净额 90，盘点 $100×1 + 5c×3 = 100.15，差额 10.15。
    private static string LegacyRowSql(Guid guid) =>
        $"""
        INSERT INTO LocalDailyCloses VALUES ('{guid}', 'S001', 'POS-01', 'C001', 'Alice', '2026-05-28',
            '2026-05-28T00:00:00.0000000+10:00', '2026-05-29T00:00:00.0000000+10:00', '2026-05-28T22:30:00.1234567+10:00',
            3, '90', '0', '90', '50', '5', '45', '10', '0', '10', '5', '1', '100', '0.15', '100.15', '10.15');
        """;

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class CountingExecutor : IDailyCloseUploadExecutionService
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public bool FailFirstCall { get; init; }

        public Task<DailyCloseUploadExecutionResult> ExecutePendingAsync(
            int batchSize = 20,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref callCount);
            if (FailFirstCall && call == 1)
            {
                throw new InvalidOperationException("transient execution failure");
            }

            return Task.FromResult(new DailyCloseUploadExecutionResult(0, 0, 0, 0, false));
        }
    }

    /// <summary>认领指定记录时抛异常、其余委托给真实仓储：模拟 SQLite 瞬时锁等认领阶段故障。</summary>
    private sealed class ThrowingClaimRepository(ILocalDailyCloseUploadRepository inner, Guid poison)
        : ILocalDailyCloseUploadRepository
    {
        public Task<IReadOnlyList<Guid>> GetDueUploadGuidsAsync(
            string storeCode, string deviceCode, int take, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            inner.GetDueUploadGuidsAsync(storeCode, deviceCode, take, now, cancellationToken);

        public Task<LocalDailyCloseUploadLease?> TryClaimUploadAsync(
            Guid dailyCloseGuid, string storeCode, string deviceCode, DateTimeOffset attemptedAt, CancellationToken cancellationToken = default) =>
            dailyCloseGuid == poison
                ? throw new InvalidOperationException("database is locked")
                : inner.TryClaimUploadAsync(dailyCloseGuid, storeCode, deviceCode, attemptedAt, cancellationToken);

        public Task<DailyCloseArchive?> GetArchiveForUploadAsync(Guid dailyCloseGuid, CancellationToken cancellationToken = default) =>
            inner.GetArchiveForUploadAsync(dailyCloseGuid, cancellationToken);

        public Task MarkUploadSucceededAsync(Guid dailyCloseGuid, DateTimeOffset uploadedAt, CancellationToken cancellationToken = default) =>
            inner.MarkUploadSucceededAsync(dailyCloseGuid, uploadedAt, cancellationToken);

        public Task MarkUploadPendingAsync(
            Guid dailyCloseGuid, DateTimeOffset nextUploadAt, string? errorCode, string? errorMessage, CancellationToken cancellationToken = default) =>
            inner.MarkUploadPendingAsync(dailyCloseGuid, nextUploadAt, errorCode, errorMessage, cancellationToken);

        public Task ReleaseUploadWithoutAttemptAsync(
            Guid dailyCloseGuid, string? errorCode, string? errorMessage, CancellationToken cancellationToken = default) =>
            inner.ReleaseUploadWithoutAttemptAsync(dailyCloseGuid, errorCode, errorMessage, cancellationToken);

        public Task MarkUploadRejectedAsync(
            Guid dailyCloseGuid, string? errorCode, string? errorMessage, CancellationToken cancellationToken = default) =>
            inner.MarkUploadRejectedAsync(dailyCloseGuid, errorCode, errorMessage, cancellationToken);

        public Task RecoverExpiredUploadingAsync(
            DateTimeOffset staleBefore, DateTimeOffset nextUploadAt, CancellationToken cancellationToken = default) =>
            inner.RecoverExpiredUploadingAsync(staleBefore, nextUploadAt, cancellationToken);
    }
}
