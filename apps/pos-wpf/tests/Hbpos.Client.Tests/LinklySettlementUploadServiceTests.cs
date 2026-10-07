using System.Net;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Client.Tests;

[Collection(ConsoleLogGlobalStateTestCollection.Name)]
public sealed class LinklySettlementUploadServiceTests
{
    [Fact]
    public async Task ExecutePendingAsync_logs_a_warning_with_code_and_revision_when_the_server_rejects_permanently()
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var settlement = await fixture.CreateCompletedSettlementAsync();
        var client = new FakeSyncApiClient(_ => Task.FromException<LinklySettlementSyncResponse>(
            new LinklySettlementUploadApiException(
                "Immutable Linkly settlement fields cannot change.",
                HttpStatusCode.Conflict,
                "IMMUTABLE_FIELDS_CONFLICT")));
        var service = new LinklySettlementUploadService(fixture.Repository, client);
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

        // 2026-10 线上 1042 测试机的结算被永久拒绝 5 天，中心日志里却一条都没有；现在要有可按结算 GUID 追溯的 Warning。
        var entry = Assert.Single(
            sink.Entries,
            item => item.Level == "Warning" && item.TraceId == settlement.SettlementGuid.ToString("D"));
        Assert.Equal("LinklySettlementUpload", entry.Category);
        Assert.Equal(409, entry.StatusCode);
        Assert.Equal("IMMUTABLE_FIELDS_CONFLICT", entry.Properties!["errorCode"]);
        Assert.Contains(
            "revision=2 errorCode=IMMUTABLE_FIELDS_CONFLICT http=409 message=Immutable Linkly settlement fields cannot change.",
            entry.Message);
    }

    [Fact]
    public async Task ExecutePendingAsync_does_not_warn_for_retryable_conflicts()
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var settlement = await fixture.CreateCompletedSettlementAsync();
        var client = new FakeSyncApiClient(_ => Task.FromException<LinklySettlementSyncResponse>(
            new LinklySettlementUploadApiException("retry later", HttpStatusCode.Conflict, "SETTLEMENT_SYNC_CONCURRENT_UPDATE")));
        var service = new LinklySettlementUploadService(fixture.Repository, client);
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

        Assert.DoesNotContain(
            sink.Entries,
            item => item.Level == "Warning" && item.TraceId == settlement.SettlementGuid.ToString("D"));
    }

    [Fact]
    public async Task ExecutePendingAsync_logs_a_deferred_warning_with_trace_and_retry_properties_when_device_is_unauthorized()
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var settlement = await fixture.CreateCompletedSettlementAsync();
        var client = new FakeSyncApiClient(_ => Task.FromException<LinklySettlementSyncResponse>(
            new LinklySettlementUploadApiException("Device is not authorized.", HttpStatusCode.Unauthorized, "DEVICE_UNAUTHORIZED")));
        var service = new LinklySettlementUploadService(fixture.Repository, client);
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        LinklySettlementUploadExecutionResult result;
        try
        {
            result = await service.ExecutePendingAsync();
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        // 401/403 会中断整批上传：必须留下按结算 GUID 可检索的 Warning（授权问题不记 Error），并带上重试节奏。
        Assert.True(result.WasInterrupted);
        var entry = Assert.Single(
            sink.Entries,
            item => item.Level == "Warning" && item.TraceId == settlement.SettlementGuid.ToString("D"));
        Assert.Equal("LinklySettlementUpload", entry.Category);
        Assert.Equal(401, entry.StatusCode);
        Assert.Equal("DEVICE_UNAUTHORIZED", entry.Properties!["errorCode"]);
        Assert.Equal(1, entry.Properties["attemptCount"]);
        Assert.Equal(5, entry.Properties["nextRetrySeconds"]);
        Assert.Contains("deferred", entry.Message, StringComparison.Ordinal);
        Assert.Contains("batchInterrupted=true", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sink.Entries, item => item.Level == "Error");
    }

    [Fact]
    public async Task ExecutePendingAsync_logs_success_as_information_with_settlement_trace()
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var settlement = await fixture.CreateCompletedSettlementAsync();
        var client = new FakeSyncApiClient(request =>
            Task.FromResult(new LinklySettlementSyncResponse(true, false, request.ClientRevision)));
        var service = new LinklySettlementUploadService(fixture.Repository, client);
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

        var entry = Assert.Single(
            sink.Entries,
            item => item.TraceId == settlement.SettlementGuid.ToString("D"));
        Assert.Equal("Information", entry.Level);
        Assert.Contains("upload succeeded", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_logs_iteration_failure_as_warning_with_exception()
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var executor = new FailsOnceSettlementExecutor();
        var schema = new LocalSchemaService(fixture.Store);
        await schema.InitializeAsync();
        schema.SignalReady();
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            using var worker = new LinklySettlementUploadWorker(schema, executor);
            using var stopping = new CancellationTokenSource();

            await worker.StartAsync(stopping.Token);
            await executor.FirstFailure.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            worker.RequestUpload();
            await executor.SecondExecution.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

            stopping.Cancel();
            await worker.StopAsync(CancellationToken.None).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        // 原先只 Console.WriteLine 异常类型，中心日志看不到；现在要有带异常对象的 Warning。
        var entry = Assert.Single(
            sink.Entries,
            item => item.Level == "Warning" && item.Message.Contains("upload worker iteration failed", StringComparison.Ordinal));
        Assert.Equal("LinklySettlementUpload", entry.Category);
        Assert.Equal(nameof(InvalidOperationException), entry.ExceptionType);
        Assert.Equal("transient execution failure", entry.ExceptionMessage);
    }

    [Fact]
    public void Service_registration_reuses_one_concrete_schema_singleton_for_the_interface()
    {
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], PreviewMode: true, InitialScreen: null, InitialCulture: null));
        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<LocalSchemaService>(),
            provider.GetRequiredService<ILocalSchemaService>());
    }

    [Fact]
    public async Task ExecutePendingAsync_marks_only_the_uploaded_snapshot_as_synced()
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var settlement = await fixture.CreateCompletedSettlementAsync();
        var client = new FakeSyncApiClient(request =>
            Task.FromResult(new LinklySettlementSyncResponse(true, false, request.ClientRevision)));
        var service = new LinklySettlementUploadService(fixture.Repository, client);

        var result = await service.ExecutePendingAsync();
        var stored = await fixture.GetSettlementAsync(settlement.SettlementGuid);

        Assert.Equal(1, result.AttemptedCount);
        Assert.Equal(1, result.UploadedCount);
        Assert.Equal(LocalLinklySettlementUploadStatus.Synced, stored.UploadStatus);
        Assert.Equal(stored.PayloadRevision, stored.UploadedRevision);
        Assert.NotNull(stored.UploadedAt);
    }

    [Theory]
    [InlineData("LocalIp")]
    [InlineData("CloudDirectSync")]
    [InlineData("CloudBackendAsync")]
    public async Task ExecutePendingAsync_uploads_all_connection_modes(string connectionMode)
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        await fixture.CreateCompletedSettlementAsync(connectionMode);
        LinklySettlementSyncRequest? uploadedRequest = null;
        var client = new FakeSyncApiClient(request =>
        {
            uploadedRequest = request;
            return Task.FromResult(new LinklySettlementSyncResponse(true, false, request.ClientRevision));
        });
        var service = new LinklySettlementUploadService(fixture.Repository, client);

        var result = await service.ExecutePendingAsync();

        Assert.Equal(1, result.UploadedCount);
        Assert.Equal(connectionMode, Assert.IsType<LinklySettlementSyncRequest>(uploadedRequest).ConnectionMode);
        Assert.Equal(ProviderSubmissionState.Submitted, uploadedRequest.ProviderSubmissionState);
    }

    [Fact]
    public async Task ExecutePendingAsync_accepts_an_already_synced_newer_server_revision()
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var settlement = await fixture.CreateCompletedSettlementAsync();
        var client = new FakeSyncApiClient(request => Task.FromResult(
            new LinklySettlementSyncResponse(false, true, request.ClientRevision + 1)));
        var service = new LinklySettlementUploadService(fixture.Repository, client);

        var result = await service.ExecutePendingAsync();
        var stored = await fixture.GetSettlementAsync(settlement.SettlementGuid);

        Assert.Equal(1, result.UploadedCount);
        Assert.Equal(LocalLinklySettlementUploadStatus.Synced, stored.UploadStatus);
        Assert.Equal(stored.PayloadRevision, stored.UploadedRevision);
    }

    [Fact]
    public async Task ExecutePendingAsync_keeps_a_newer_local_revision_pending_after_old_snapshot_succeeds()
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var settlement = await fixture.CreateCompletedSettlementAsync();
        var client = new FakeSyncApiClient(async request =>
        {
            await fixture.Repository.MarkPrintedAsync(request.SettlementGuid, DateTimeOffset.UtcNow);
            return new LinklySettlementSyncResponse(true, false, request.ClientRevision);
        });
        var service = new LinklySettlementUploadService(fixture.Repository, client);

        var result = await service.ExecutePendingAsync();
        var stored = await fixture.GetSettlementAsync(settlement.SettlementGuid);

        Assert.Equal(1, result.UploadedCount);
        Assert.Equal(LocalLinklySettlementUploadStatus.Pending, stored.UploadStatus);
        Assert.Equal(3, stored.PayloadRevision);
        Assert.Equal(2, stored.UploadedRevision);
    }

    [Fact]
    public async Task ExecuteOneAsync_retries_a_rejected_upload_without_changing_the_settlement_snapshot()
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var settlement = await fixture.CreateCompletedSettlementAsync();
        var client = new FakeSyncApiClient(_ => Task.FromException<LinklySettlementSyncResponse>(
            new LinklySettlementUploadApiException(
                "invalid snapshot",
                HttpStatusCode.BadRequest,
                "INVALID_SNAPSHOT")));
        var service = new LinklySettlementUploadService(fixture.Repository, client);

        var rejected = await service.ExecutePendingAsync();
        var rejectedRecord = await fixture.GetSettlementAsync(settlement.SettlementGuid);
        Assert.Equal(1, rejected.FailedCount);
        Assert.Equal(LocalLinklySettlementUploadStatus.Rejected, rejectedRecord.UploadStatus);
        Assert.Equal(2, rejectedRecord.PayloadRevision);

        client.Handler = request => Task.FromResult(new LinklySettlementSyncResponse(true, false, request.ClientRevision));
        var retried = await service.ExecuteOneAsync(settlement.SettlementGuid);
        var synced = await fixture.GetSettlementAsync(settlement.SettlementGuid);

        Assert.Equal(1, retried.UploadedCount);
        Assert.Equal(LocalLinklySettlementUploadStatus.Synced, synced.UploadStatus);
        Assert.Equal(2, synced.PayloadRevision);
    }

    [Theory]
    [InlineData("SETTLEMENT_SYNC_CONCURRENT_UPDATE")]
    [InlineData("CLOUD_BACKEND_SESSION_NOT_FOUND")]
    [InlineData("CLOUD_BACKEND_SESSION_NOT_FINAL")]
    public async Task ExecutePendingAsync_keeps_retryable_conflicts_pending(string errorCode)
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var settlement = await fixture.CreateCompletedSettlementAsync();
        var client = new FakeSyncApiClient(_ => Task.FromException<LinklySettlementSyncResponse>(
            new LinklySettlementUploadApiException("retry later", HttpStatusCode.Conflict, errorCode)));
        var service = new LinklySettlementUploadService(fixture.Repository, client);

        var result = await service.ExecutePendingAsync();
        var stored = await fixture.GetSettlementAsync(settlement.SettlementGuid);

        Assert.Equal(1, result.DeferredCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(LocalLinklySettlementUploadStatus.Pending, stored.UploadStatus);
        Assert.Equal(errorCode, stored.UploadErrorCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task ExecutePendingAsync_rejects_permanent_payload_http_errors(HttpStatusCode statusCode)
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var settlement = await fixture.CreateCompletedSettlementAsync();
        var client = new FakeSyncApiClient(_ => Task.FromException<LinklySettlementSyncResponse>(
            new LinklySettlementUploadApiException("invalid payload", statusCode, "INVALID_PAYLOAD")));
        var service = new LinklySettlementUploadService(fixture.Repository, client);

        var result = await service.ExecutePendingAsync();
        var stored = await fixture.GetSettlementAsync(settlement.SettlementGuid);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.DeferredCount);
        Assert.Equal(LocalLinklySettlementUploadStatus.Rejected, stored.UploadStatus);
        Assert.Equal("INVALID_PAYLOAD", stored.UploadErrorCode);
    }

    [Fact]
    public async Task ExecutePendingAsync_continues_the_batch_after_a_request_timeout()
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var first = await fixture.CreateCompletedSettlementAsync();
        var second = await fixture.CreateCompletedSettlementAsync();
        var callCount = 0;
        var client = new FakeSyncApiClient(request =>
        {
            callCount++;
            return callCount == 1
                ? Task.FromException<LinklySettlementSyncResponse>(new TaskCanceledException("timeout"))
                : Task.FromResult(new LinklySettlementSyncResponse(true, false, request.ClientRevision));
        });
        var service = new LinklySettlementUploadService(fixture.Repository, client);

        var result = await service.ExecutePendingAsync();
        var firstStored = await fixture.GetSettlementAsync(first.SettlementGuid);
        var secondStored = await fixture.GetSettlementAsync(second.SettlementGuid);

        Assert.Equal(2, result.AttemptedCount);
        Assert.Equal(1, result.UploadedCount);
        Assert.Equal(1, result.DeferredCount);
        Assert.False(result.WasInterrupted);
        Assert.Equal(LocalLinklySettlementUploadStatus.Pending, firstStored.UploadStatus);
        Assert.Equal("REQUEST_CANCELED", firstStored.UploadErrorCode);
        Assert.Equal(LocalLinklySettlementUploadStatus.Synced, secondStored.UploadStatus);
    }

    [Fact]
    public async Task ExecutePendingAsync_recovers_only_an_expired_upload_lease()
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var stale = await fixture.CreateCompletedSettlementAsync();
        var active = await fixture.CreateCompletedSettlementAsync();
        var now = new[] { stale.NextUploadAt, active.NextUploadAt }
            .Max()!.Value.AddHours(1);
        Assert.NotNull(await fixture.Repository.TryClaimUploadAsync(
            stale.SettlementGuid,
            now - LinklySettlementUploadService.UploadLeaseTimeout - TimeSpan.FromSeconds(1)));
        Assert.NotNull(await fixture.Repository.TryClaimUploadAsync(active.SettlementGuid, now - TimeSpan.FromSeconds(10)));
        var uploadedIds = new List<Guid>();
        var client = new FakeSyncApiClient(request =>
        {
            uploadedIds.Add(request.SettlementGuid);
            return Task.FromResult(new LinklySettlementSyncResponse(true, false, request.ClientRevision));
        });
        var service = new LinklySettlementUploadService(fixture.Repository, client, new FixedTimeProvider(now));

        var result = await service.ExecutePendingAsync();
        var recovered = await fixture.GetSettlementAsync(stale.SettlementGuid);
        var stillActive = await fixture.GetSettlementAsync(active.SettlementGuid);

        Assert.Equal([stale.SettlementGuid], uploadedIds);
        Assert.Equal(1, result.UploadedCount);
        Assert.Equal(LocalLinklySettlementUploadStatus.Synced, recovered.UploadStatus);
        Assert.Equal(LocalLinklySettlementUploadStatus.Uploading, stillActive.UploadStatus);
    }

    [Fact]
    public async Task ExecuteOneAsync_does_not_steal_an_active_upload_lease()
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var settlement = await fixture.CreateCompletedSettlementAsync();
        var now = settlement.NextUploadAt!.Value.AddHours(1);
        Assert.NotNull(await fixture.Repository.TryClaimUploadAsync(settlement.SettlementGuid, now));
        var callCount = 0;
        var service = new LinklySettlementUploadService(
            fixture.Repository,
            new FakeSyncApiClient(request =>
            {
                callCount++;
                return Task.FromResult(new LinklySettlementSyncResponse(true, false, request.ClientRevision));
            }),
            new FixedTimeProvider(now.AddSeconds(30)));

        var result = await service.ExecuteOneAsync(settlement.SettlementGuid);
        var stored = await fixture.GetSettlementAsync(settlement.SettlementGuid);

        Assert.Equal(0, result.AttemptedCount);
        Assert.Equal(0, callCount);
        Assert.Equal(LocalLinklySettlementUploadStatus.Uploading, stored.UploadStatus);
    }

    [Fact]
    public async Task Worker_continues_after_a_single_execution_failure()
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var executor = new FailsOnceSettlementExecutor();
        var schema = new LocalSchemaService(fixture.Store);
        await schema.InitializeAsync();
        schema.SignalReady();
        using var worker = new LinklySettlementUploadWorker(
            schema,
            executor);
        using var stopping = new CancellationTokenSource();

        await worker.StartAsync(stopping.Token);
        await executor.FirstFailure.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        worker.RequestUpload();
        await executor.SecondExecution.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        stopping.Cancel();
        await worker.StopAsync(CancellationToken.None).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.True(executor.CallCount >= 2);
    }

    [Fact]
    public async Task Worker_waits_for_the_main_schema_ready_signal_without_initializing_itself()
    {
        await using var fixture = await SettlementFixture.CreateAsync();
        var schema = new LocalSchemaService(fixture.Store);
        var executor = new CapturingSettlementExecutor();
        using var worker = new LinklySettlementUploadWorker(schema, executor);
        using var stopping = new CancellationTokenSource();

        await worker.StartAsync(stopping.Token).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            executor.Executed.Task.WaitAsync(TimeSpan.FromMilliseconds(150)));
        await schema.InitializeAsync();
        schema.SignalReady();
        await executor.Executed.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        stopping.Cancel();
        await worker.StopAsync(CancellationToken.None).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
    }

    private sealed class FakeSyncApiClient : ILinklySettlementSyncApiClient
    {
        public FakeSyncApiClient(Func<LinklySettlementSyncRequest, Task<LinklySettlementSyncResponse>> handler)
        {
            Handler = handler;
        }

        public Func<LinklySettlementSyncRequest, Task<LinklySettlementSyncResponse>> Handler { get; set; }

        public Task<LinklySettlementSyncResponse> SyncAsync(
            LinklySettlementSyncRequest request,
            CancellationToken cancellationToken = default)
        {
            return Handler(request);
        }
    }

    private sealed class FailsOnceSettlementExecutor : ILinklySettlementUploadExecutionService
    {
        public TaskCompletionSource FirstFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondExecution { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public Task<LinklySettlementUploadExecutionResult> ExecuteOneAsync(
            Guid settlementGuid,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklySettlementUploadExecutionResult(0, 0, 0, 0, false));
        }

        public Task<LinklySettlementUploadExecutionResult> ExecutePendingAsync(
            int batchSize = 20,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                FirstFailure.SetResult();
                throw new InvalidOperationException("transient execution failure");
            }

            SecondExecution.SetResult();
            return Task.FromResult(new LinklySettlementUploadExecutionResult(0, 0, 0, 0, false));
        }
    }

    private sealed class CapturingSettlementExecutor : ILinklySettlementUploadExecutionService
    {
        public TaskCompletionSource Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<LinklySettlementUploadExecutionResult> ExecuteOneAsync(
            Guid settlementGuid,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinklySettlementUploadExecutionResult(0, 0, 0, 0, false));

        public Task<LinklySettlementUploadExecutionResult> ExecutePendingAsync(
            int batchSize = 20,
            CancellationToken cancellationToken = default)
        {
            Executed.TrySetResult();
            return Task.FromResult(new LinklySettlementUploadExecutionResult(0, 0, 0, 0, false));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SettlementFixture : IAsyncDisposable
    {
        private SettlementFixture(string databasePath, LocalSqliteStore store, LocalLinklySettlementRepository repository)
        {
            DatabasePath = databasePath;
            Store = store;
            Repository = repository;
        }

        public string DatabasePath { get; }

        public LocalSqliteStore Store { get; }

        public LocalLinklySettlementRepository Repository { get; }

        public static async Task<SettlementFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-linkly-settlement-upload-{Guid.NewGuid():N}.db");
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            return new SettlementFixture(databasePath, store, new LocalLinklySettlementRepository(store));
        }

        public async Task<LocalLinklySettlementRecord> CreateCompletedSettlementAsync(
            string connectionMode = "LocalIp")
        {
            var requestedAt = DateTimeOffset.UtcNow.AddMinutes(-6);
            var settlement = new LocalLinklySettlementRecord(
                Guid.NewGuid(),
                "S001",
                "POS-01",
                DateTime.Today,
                connectionMode,
                "Production",
                ProviderSessionId: null,
                LocalLinklySettlementStatus.Pending,
                ResponseCode: null,
                ResponseText: null,
                SettlementData: null,
                ReceiptTexts: [],
                requestedAt,
                CompletedAt: null,
                FirstPrintedAt: null,
                LastPrintedAt: null,
                PrintCount: 0,
                LastPrintError: null);
            await Repository.CreatePendingAsync(settlement);
            await Repository.CompleteAsync(settlement.SettlementGuid, new LocalLinklySettlementCompletion(
                LocalLinklySettlementStatus.Succeeded,
                "00",
                "Approved",
                "Totals: 1",
                ["CARD ****1111"],
                requestedAt.AddMinutes(1),
                ProviderSubmissionState.Submitted));
            return await GetSettlementAsync(settlement.SettlementGuid);
        }

        public async Task<LocalLinklySettlementRecord> GetSettlementAsync(Guid settlementGuid)
        {
            return (await Repository.GetByBusinessDateAsync("S001", "POS-01", DateTime.Today))
                .Single(settlement => settlement.SettlementGuid == settlementGuid);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { DatabasePath, $"{DatabasePath}-wal", $"{DatabasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
