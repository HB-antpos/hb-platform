using System.Net;
using System.Text;
using System.Collections.Concurrent;
using System.Text.Json;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Client.Tests;

[Collection(GlobalLoggingTestCollection.Name)]
public sealed class ClientLogUploadServiceTests
{
    [Fact]
    public async Task Disabled_uploaders_cleanup_day31_rejections_for_both_outboxes_without_touching_pending_or_http()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        var old = now.AddDays(-31);
        var runtimeRejected = Guid.NewGuid();
        var operationRejected = Guid.NewGuid();
        var runtimePending = Guid.NewGuid();
        var operationPending = Guid.NewGuid();
        await store.EnqueueAsync(ClientLogOutboxKind.Runtime, runtimeRejected, old, "{}", old, CancellationToken.None);
        await store.EnqueueAsync(ClientLogOutboxKind.OperationAudit, operationRejected, old, "{}", old, CancellationToken.None);
        await store.EnqueueAsync(ClientLogOutboxKind.Runtime, runtimePending, old, "{}", old, CancellationToken.None);
        await store.EnqueueAsync(ClientLogOutboxKind.OperationAudit, operationPending, old, "{}", old, CancellationToken.None);
        await store.ApplyResultsAsync(
            ClientLogOutboxKind.Runtime,
            [],
            [new ClientLogRejection(runtimeRejected, "INVALID", null)],
            old,
            CancellationToken.None);
        await store.ApplyResultsAsync(
            ClientLogOutboxKind.OperationAudit,
            [],
            [new ClientLogRejection(operationRejected, "INVALID", null)],
            old,
            CancellationToken.None);

        var httpCalls = 0;
        using var runtimeClient = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref httpCalls);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
        }));
        using var operationClient = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref httpCalls);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
        }))
        {
            BaseAddress = new Uri("https://pos-api.example.com/")
        };
        var runtimeUploader = new ApplicationLogUploadService(
            store,
            CreateApplicationOptions() with { Enabled = false },
            runtimeClient,
            TimeProvider.System);
        var operationUploader = new OperationAuditUploadService(
            store,
            operationClient,
            TimeProvider.System,
            new OperationAuditUploadOptions(false));

        try
        {
            await runtimeUploader.UploadOnceAsync(now, CancellationToken.None);
            await operationUploader.UploadOnceAsync(now, CancellationToken.None);

            Assert.Empty(await store.ReadRejectedAsync(ClientLogOutboxKind.Runtime, 100, CancellationToken.None));
            Assert.Empty(await store.ReadRejectedAsync(ClientLogOutboxKind.OperationAudit, 100, CancellationToken.None));
            Assert.Equal(
                [runtimePending],
                (await store.ReadPendingAsync(ClientLogOutboxKind.Runtime, now, 100, CancellationToken.None))
                .Select(item => item.EventId));
            Assert.Equal(
                [operationPending],
                (await store.ReadPendingAsync(ClientLogOutboxKind.OperationAudit, now, 100, CancellationToken.None))
                .Select(item => item.EventId));
            Assert.Equal(0, httpCalls);
        }
        finally
        {
            runtimeUploader.Dispose();
            operationUploader.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Runtime_uploader_start_immediately_uploads_existing_offline_pending_record()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        await store.EnqueueAsync(
            ClientLogOutboxKind.Runtime,
            eventId,
            now,
            JsonSerializer.Serialize(new { clientEventId = eventId }),
            now,
            CancellationToken.None);
        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ApplicationLogUploadService(
            store,
            CreateApplicationOptions(),
            new HttpClient(new StubHttpMessageHandler(_ =>
            {
                uploaded.TrySetResult();
                return Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    JsonSerializer.Serialize(new
                    {
                        results = new[] { new { clientEventId = eventId, status = "accepted" } }
                    })));
            })),
            TimeProvider.System);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await uploaded.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            await WaitForRuntimePendingCountAsync(store, expectedCount: 0);
            using var shutdownBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await service.StopAsync(shutdownBudget.Token);

            Assert.Empty(await store.ReadPendingAsync(
                ClientLogOutboxKind.Runtime,
                DateTimeOffset.UtcNow.AddMinutes(1),
                100,
                CancellationToken.None));
        }
        finally
        {
            service.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Runtime_uploader_can_start_before_writer_on_a_new_database()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        var httpCalls = 0;
        var service = new ApplicationLogUploadService(
            store,
            CreateApplicationOptions(),
            new HttpClient(new StubHttpMessageHandler(_ =>
            {
                Interlocked.Increment(ref httpCalls);
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            })),
            TimeProvider.System);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await WaitForOutboxSchemaAsync(store);
            using var shutdownBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await service.StopAsync(shutdownBudget.Token);

            Assert.Equal(0, httpCalls);
        }
        finally
        {
            service.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Operation_upload_skips_more_than_one_batch_of_old_scope_without_blocking_current_scope()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var createdAt = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        var dueAt = createdAt.AddHours(1);
        var oldEventIds = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var pair in oldEventIds.Select((eventId, index) => (eventId, index)))
        {
            await store.EnqueueAsync(
                ClientLogOutboxKind.OperationAudit,
                pair.eventId,
                createdAt.AddSeconds(pair.index),
                JsonSerializer.Serialize(new
                {
                    eventId = pair.eventId,
                    storeCode = "S-OLD",
                    deviceCode = "D-OLD"
                }),
                createdAt,
                CancellationToken.None);
        }

        var currentEventId = Guid.NewGuid();
        await store.EnqueueAsync(
            ClientLogOutboxKind.OperationAudit,
            currentEventId,
            createdAt.AddMinutes(10),
            JsonSerializer.Serialize(new
            {
                eventId = currentEventId,
                storeCode = "S-NEW",
                deviceCode = "D-NEW"
            }),
            createdAt,
            CancellationToken.None);

        var authorizationState = new DeviceAuthorizationState();
        authorizationState.Set(new DeviceAuthorizationContext("D-NEW", "S-NEW", "HW-NEW", "new-secret"));
        var acceptedBatches = new List<Guid[]>();
        var terminal = new StubHttpMessageHandler(async request =>
        {
            var requestStore = request.Headers.GetValues(DeviceAuthConstants.StoreCodeHeader).Single();
            var requestDevice = request.Headers.GetValues(DeviceAuthConstants.DeviceCodeHeader).Single();
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var events = document.RootElement.GetProperty("events").EnumerateArray().ToArray();
            if (events.Any(item =>
                    !string.Equals(item.GetProperty("storeCode").GetString(), requestStore, StringComparison.Ordinal) ||
                    !string.Equals(item.GetProperty("deviceCode").GetString(), requestDevice, StringComparison.Ordinal)))
            {
                return JsonResponse(HttpStatusCode.Forbidden, "{}");
            }

            var eventIds = events.Select(item => item.GetProperty("eventId").GetGuid()).ToArray();
            acceptedBatches.Add(eventIds);
            return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                results = eventIds.Select(eventId => new { eventId, status = "accepted" })
            }));
        });
        var authHandler = new DeviceAuthorizationMessageHandler(authorizationState, new CashierSessionContext()) { InnerHandler = terminal };
        using var client = new HttpClient(authHandler) { BaseAddress = new Uri("https://pos-api.example.com/") };
        var services = new ServiceCollection().AddSingleton(authorizationState);
        using var provider = services.BuildServiceProvider();
        var service = ActivatorUtilities.CreateInstance<OperationAuditUploadService>(
            provider,
            store,
            client,
            TimeProvider.System,
            new OperationAuditUploadOptions(true));

        try
        {
            await service.UploadOnceAsync(dueAt, CancellationToken.None);

            Assert.Equal([currentEventId], Assert.Single(acceptedBatches));
            Assert.Equal(9L, await CountPendingOperationEventsAsync(store));
            Assert.Equal(0L, await CountPendingOperationEventsAsync(store, currentEventId));

            authorizationState.Set(new DeviceAuthorizationContext("D-OLD", "S-OLD", "HW-OLD", "old-secret"));
            await service.UploadOnceAsync(dueAt.AddHours(1), CancellationToken.None);

            Assert.Equal(8, acceptedBatches[1].Length);
            Assert.All(acceptedBatches[1], eventId => Assert.Contains(eventId, oldEventIds));
            Assert.Equal(1L, await CountPendingOperationEventsAsync(store));
        }
        finally
        {
            service.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Operation_uploader_immediately_drains_next_batch_after_full_batch_succeeds()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var eventIds = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var eventId in eventIds)
        {
            await store.EnqueueAsync(
                ClientLogOutboxKind.OperationAudit,
                eventId,
                now,
                JsonSerializer.Serialize(new { eventId, storeCode = "S-1", deviceCode = "D-1" }),
                now,
                CancellationToken.None);
        }

        var authorizationState = new DeviceAuthorizationState();
        authorizationState.Set(new DeviceAuthorizationContext("D-1", "S-1", "HW-1", "secret"));
        var batchSizes = new ConcurrentQueue<int>();
        var secondRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var client = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var ids = document.RootElement.GetProperty("events")
                .EnumerateArray()
                .Select(item => item.GetProperty("eventId").GetGuid())
                .ToArray();
            batchSizes.Enqueue(ids.Length);
            if (Interlocked.Increment(ref requestCount) == 2)
            {
                secondRequest.TrySetResult();
            }

            return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                results = ids.Select(eventId => new { eventId, status = "accepted" })
            }));
        }))
        {
            BaseAddress = new Uri("https://pos-api.example.com/")
        };
        var service = new OperationAuditUploadService(
            store,
            client,
            TimeProvider.System,
            new OperationAuditUploadOptions(true),
            authorizationState);
        var started = false;

        try
        {
            await service.StartAsync(CancellationToken.None);
            started = true;
            await secondRequest.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            await WaitForOperationPendingCountAsync(store, expectedCount: 0);

            Assert.Equal([8, 1], batchSizes);
        }
        finally
        {
            if (started)
            {
                using var shutdownBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await service.StopAsync(shutdownBudget.Token);
            }

            service.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Runtime_uploader_database_failure_does_not_escape_into_host()
    {
        var service = new ApplicationLogUploadService(
            new ClientLogOutboxStore(Path.GetTempPath()),
            CreateApplicationOptions(),
            new HttpClient(new StubHttpMessageHandler(_ =>
                Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")))),
            TimeProvider.System);

        try
        {
            var exception = await Record.ExceptionAsync(() =>
                service.UploadOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None));

            Assert.Null(exception);
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task Runtime_upload_deletes_accepted_and_duplicate_and_quarantines_rejected()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        var accepted = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var duplicate = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var rejected = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        foreach (var eventId in new[] { accepted, duplicate, rejected })
        {
            await store.EnqueueAsync(
                ClientLogOutboxKind.Runtime,
                eventId,
                now,
                $$"""{"clientEventId":"{{eventId:D}}","message":"test"}""",
                now,
                CancellationToken.None);
        }

        string? requestBody = null;
        string? projectHeader = null;
        string? keyHeader = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            projectHeader = request.Headers.GetValues("X-Log-Project").Single();
            keyHeader = request.Headers.GetValues("X-Log-Key").Single();
            return JsonResponse(HttpStatusCode.OK, $$"""
                { "success": true, "data": {
                    "acceptedCount": 1,
                    "duplicateCount": 1,
                    "rejectedCount": 1,
                    "results": [
                      { "clientEventId": "{{accepted:D}}", "status": "accepted" },
                      { "clientEventId": "{{duplicate:D}}", "status": "duplicate" },
                      { "clientEventId": "{{rejected:D}}", "status": "rejected", "errorCode": "INVALID_EVENT" }
                    ]
                  }
                }
                """);
        });
        var service = new ApplicationLogUploadService(
            store,
            CreateApplicationOptions(),
            new HttpClient(handler),
            TimeProvider.System);

        try
        {
            await service.UploadOnceAsync(now, CancellationToken.None);

            Assert.Equal("hbpos_win", projectHeader);
            Assert.Equal("log-key", keyHeader);
            Assert.Contains("\"logs\"", requestBody, StringComparison.Ordinal);
            Assert.Empty(await store.ReadPendingAsync(ClientLogOutboxKind.Runtime, now.AddDays(1), 100, CancellationToken.None));
            var item = Assert.Single(await store.ReadRejectedAsync(ClientLogOutboxKind.Runtime, 100, CancellationToken.None));
            Assert.Equal(rejected, item.EventId);
        }
        finally
        {
            service.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Runtime_upload_keeps_missing_ack_pending_and_schedules_retry()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        var eventId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        await store.EnqueueAsync(ClientLogOutboxKind.Runtime, eventId, now, "{}", now, CancellationToken.None);
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(JsonResponse(
            HttpStatusCode.OK,
            "{\"acceptedCount\":1,\"duplicateCount\":0,\"rejectedCount\":0,\"results\":[]}")));
        var service = new ApplicationLogUploadService(
            store,
            CreateApplicationOptions(),
            new HttpClient(handler),
            TimeProvider.System);

        try
        {
            await service.UploadOnceAsync(now, CancellationToken.None);

            Assert.Empty(await store.ReadPendingAsync(ClientLogOutboxKind.Runtime, now, 100, CancellationToken.None));
            var retry = Assert.Single(await store.ReadPendingAsync(
                ClientLogOutboxKind.Runtime,
                now.AddHours(1),
                100,
                CancellationToken.None));
            Assert.Equal(eventId, retry.EventId);
            Assert.Equal(1, retry.AttemptCount);
            Assert.Equal("MISSING_ACK", retry.LastErrorCode);
        }
        finally
        {
            service.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Operation_upload_uses_device_authorization_and_expected_endpoint()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        var eventId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        await store.EnqueueAsync(ClientLogOutboxKind.OperationAudit, eventId, now, "{}", now, CancellationToken.None);
        var authorizationState = new DeviceAuthorizationState();
        authorizationState.Set(new DeviceAuthorizationContext("POS-01", "S001", "HW-01", "device-secret"));
        Uri? requestUri = null;
        string? bearer = null;
        string? storeHeader = null;
        var terminal = new StubHttpMessageHandler(request =>
        {
            requestUri = request.RequestUri;
            bearer = request.Headers.Authorization?.Parameter;
            storeHeader = request.Headers.GetValues("X-HBPOS-Store-Code").Single();
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, $$"""
                { "acceptedCount": 1, "duplicateCount": 0, "rejectedCount": 0,
                  "results": [{ "eventId": "{{eventId:D}}", "status": "accepted" }] }
                """));
        });
        var authHandler = new DeviceAuthorizationMessageHandler(authorizationState, new CashierSessionContext()) { InnerHandler = terminal };
        var client = new HttpClient(authHandler) { BaseAddress = new Uri("https://pos-api.example.com/") };
        var service = new OperationAuditUploadService(
            store,
            client,
            TimeProvider.System,
            new OperationAuditUploadOptions(true));

        try
        {
            await service.UploadOnceAsync(now, CancellationToken.None);

            Assert.Equal(new Uri("https://pos-api.example.com/api/v1/operation-audits/batch"), requestUri);
            Assert.Equal("device-secret", bearer);
            Assert.Equal("S001", storeHeader);
            Assert.Empty(await store.ReadPendingAsync(ClientLogOutboxKind.OperationAudit, now.AddDays(1), 100, CancellationToken.None));
        }
        finally
        {
            service.Dispose();
            client.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Endpoint_generation_cancellation_keeps_audit_event_immediately_retryable()
    {
        var databasePath = CreateDatabasePath();
        const string oldAddress = "https://old.example.test/pos-api/";
        const string newAddress = "https://new.example.test/pos-api/";
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        await store.EnqueueAsync(
            ClientLogOutboxKind.OperationAudit,
            eventId,
            now,
            JsonSerializer.Serialize(new { eventId, storeCode = "S001", deviceCode = "POS-01" }),
            now,
            CancellationToken.None);
        var authorizationState = new DeviceAuthorizationState();
        authorizationState.Set(new DeviceAuthorizationContext("POS-01", "S001", "HW-01", "secret"));
        var endpointState = new ApiRuntimeEndpointState(oldAddress);
        var terminal = new EndpointSwitchAuditHandler(eventId);
        using var client = new HttpClient(new ApiRuntimeEndpointHandler(endpointState) { InnerHandler = terminal })
        {
            BaseAddress = new Uri(oldAddress)
        };
        using var service = new OperationAuditUploadService(
            store,
            client,
            TimeProvider.System,
            new OperationAuditUploadOptions(true),
            authorizationState);

        try
        {
            var upload = service.UploadOnceAsync(now, CancellationToken.None);
            await terminal.FirstRequestStarted.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            var transition = await endpointState.BeginTransitionAsync(newAddress, CancellationToken.None);
            await upload.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

            var pending = Assert.Single(await store.ReadPendingAsync(
                ClientLogOutboxKind.OperationAudit,
                now,
                10,
                CancellationToken.None));
            Assert.Equal(eventId, pending.EventId);
            Assert.Equal(0, pending.AttemptCount);
            Assert.Null(pending.LastErrorCode);

            endpointState.Commit(transition);
            await service.UploadOnceAsync(now, CancellationToken.None);

            Assert.Equal(
                [
                    new Uri($"{oldAddress}api/v1/operation-audits/batch"),
                    new Uri($"{newAddress}api/v1/operation-audits/batch")
                ],
                terminal.RequestUris);
            Assert.Empty(await store.ReadPendingAsync(
                ClientLogOutboxKind.OperationAudit,
                now,
                10,
                CancellationToken.None));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Endpoint_transition_rejection_keeps_audit_event_immediately_retryable()
    {
        var databasePath = CreateDatabasePath();
        const string oldAddress = "https://old.example.test/pos-api/";
        const string newAddress = "https://new.example.test/pos-api/";
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        await store.EnqueueAsync(
            ClientLogOutboxKind.OperationAudit,
            eventId,
            now,
            JsonSerializer.Serialize(new { eventId, storeCode = "S001", deviceCode = "POS-01" }),
            now,
            CancellationToken.None);
        var authorizationState = new DeviceAuthorizationState();
        authorizationState.Set(new DeviceAuthorizationContext("POS-01", "S001", "HW-01", "secret"));
        var endpointState = new ApiRuntimeEndpointState(oldAddress);
        var requestUris = new List<Uri>();
        using var client = new HttpClient(new ApiRuntimeEndpointHandler(endpointState)
        {
            InnerHandler = new StubHttpMessageHandler(async request =>
            {
                requestUris.Add(request.RequestUri!);
                var body = await request.Content!.ReadAsStringAsync();
                using var document = JsonDocument.Parse(body);
                var uploadedEventId = document.RootElement
                    .GetProperty("events")[0]
                    .GetProperty("eventId")
                    .GetGuid();
                return JsonResponse(
                    HttpStatusCode.OK,
                    JsonSerializer.Serialize(new
                    {
                        results = new[] { new { eventId = uploadedEventId, status = "accepted" } }
                    }));
            })
        })
        {
            BaseAddress = new Uri(oldAddress)
        };
        using var service = new OperationAuditUploadService(
            store,
            client,
            TimeProvider.System,
            new OperationAuditUploadOptions(true),
            authorizationState);

        try
        {
            var transition = await endpointState.BeginTransitionAsync(newAddress, CancellationToken.None);

            await service.UploadOnceAsync(now, CancellationToken.None);

            var pending = Assert.Single(await store.ReadPendingAsync(
                ClientLogOutboxKind.OperationAudit,
                now.AddDays(1),
                10,
                CancellationToken.None));
            Assert.Equal(eventId, pending.EventId);
            Assert.Equal(0, pending.AttemptCount);
            Assert.Null(pending.LastErrorCode);
            Assert.Empty(requestUris);

            endpointState.Commit(transition);
            await service.UploadOnceAsync(now, CancellationToken.None);

            Assert.Equal([new Uri($"{newAddress}api/v1/operation-audits/batch")], requestUris);
            Assert.Empty(await store.ReadPendingAsync(
                ClientLogOutboxKind.OperationAudit,
                now.AddDays(1),
                10,
                CancellationToken.None));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Authorization_failure_writes_critical_diagnostic_without_reentering_runtime_sink()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        var eventId = Guid.Parse("12121212-1212-1212-1212-121212121212");
        await store.EnqueueAsync(ClientLogOutboxKind.Runtime, eventId, now, "{}", now, CancellationToken.None);
        var service = new ApplicationLogUploadService(
            store,
            CreateApplicationOptions(),
            new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))),
            TimeProvider.System);
        var sink = new FakeApplicationLogSink();
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        ConsoleLog.ConfigureCenterSink(sink);
        Console.SetOut(output);

        try
        {
            await service.UploadOnceAsync(now, CancellationToken.None);

            Assert.DoesNotContain(sink.Entries, entry =>
                entry.Level == "Critical" && entry.Message.Contains("configuration failure", StringComparison.Ordinal));
            Assert.Contains("CRITICAL configuration failure", output.ToString(), StringComparison.Ordinal);
            var retry = Assert.Single(await store.ReadPendingAsync(
                ClientLogOutboxKind.Runtime,
                now.AddMinutes(30),
                100,
                CancellationToken.None));
            Assert.Equal("HTTP_401", retry.LastErrorCode);
        }
        finally
        {
            Console.SetOut(originalOutput);
            ConsoleLog.ConfigureCenterSink(null);
            service.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Permanent_rejection_writes_critical_diagnostic_without_recursive_console_log()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        var eventId = Guid.Parse("34343434-3434-3434-3434-343434343434");
        await store.EnqueueAsync(ClientLogOutboxKind.OperationAudit, eventId, now, "{}", now, CancellationToken.None);
        var client = new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(JsonResponse(
            HttpStatusCode.OK,
            $$"""{"results":[{"eventId":"{{eventId:D}}","status":"rejected","errorCode":"INVALID_EVENT"}]}"""))))
        {
            BaseAddress = new Uri("https://pos-api.example.com/")
        };
        var service = new OperationAuditUploadService(
            store,
            client,
            TimeProvider.System,
            new OperationAuditUploadOptions(true));
        var sink = new FakeApplicationLogSink();
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        ConsoleLog.ConfigureCenterSink(sink);
        Console.SetOut(output);

        try
        {
            await service.UploadOnceAsync(now, CancellationToken.None);

            var critical = Assert.Single(sink.Entries.Where(entry =>
                entry.Level == "Critical" && entry.Category == "OperationAudit"));
            Assert.Contains(eventId.ToString("D"), critical.Message, StringComparison.Ordinal);
            Assert.Contains("CRITICAL permanent rejection", output.ToString(), StringComparison.Ordinal);
            Assert.Single(await store.ReadRejectedAsync(ClientLogOutboxKind.OperationAudit, 100, CancellationToken.None));
        }
        finally
        {
            Console.SetOut(originalOutput);
            ConsoleLog.ConfigureCenterSink(null);
            service.Dispose();
            client.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Disabled_operation_uploader_keeps_local_pending_record_without_http_call()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        var eventId = Guid.Parse("56565656-5656-5656-5656-565656565656");
        await store.EnqueueAsync(ClientLogOutboxKind.OperationAudit, eventId, now, "{}", now, CancellationToken.None);
        var callCount = 0;
        var client = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            callCount++;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"results\":[]}"));
        }))
        {
            BaseAddress = new Uri("https://pos-api.example.com/")
        };
        var service = new OperationAuditUploadService(
            store,
            client,
            TimeProvider.System,
            new OperationAuditUploadOptions(false));

        try
        {
            await service.UploadOnceAsync(now, CancellationToken.None);

            Assert.Equal(0, callCount);
            Assert.Single(await store.ReadPendingAsync(
                ClientLogOutboxKind.OperationAudit,
                now,
                100,
                CancellationToken.None));
        }
        finally
        {
            service.Dispose();
            client.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Operation_upload_limits_request_to_four_mib_without_truncating_events()
    {
        const int eventCount = 9;
        const int payloadLength = 600_000;
        const int maximumRequestBytes = 4 * 1024 * 1024;
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        for (var index = 0; index < eventCount; index++)
        {
            var eventId = Guid.Parse($"70000000-0000-0000-0000-{index + 1:000000000000}");
            var payload = JsonSerializer.Serialize(new
            {
                eventId,
                operationType = "CART_ITEM_ADD",
                details = new string('x', payloadLength)
            });
            await store.EnqueueAsync(ClientLogOutboxKind.OperationAudit, eventId, now.AddMilliseconds(index), payload, now, CancellationToken.None);
        }

        var requestBytes = 0;
        var sentCount = 0;
        var allEventsComplete = true;
        var client = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            requestBytes = Encoding.UTF8.GetByteCount(body);
            using var document = JsonDocument.Parse(body);
            var events = document.RootElement.GetProperty("events").EnumerateArray().ToArray();
            sentCount = events.Length;
            allEventsComplete = events.All(item => item.GetProperty("details").GetString()!.Length == payloadLength);
            var results = events.Select(item => new
            {
                eventId = item.GetProperty("eventId").GetGuid(),
                status = "accepted"
            });
            return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { results }));
        }))
        {
            BaseAddress = new Uri("https://pos-api.example.com/")
        };
        var service = new OperationAuditUploadService(
            store,
            client,
            TimeProvider.System,
            new OperationAuditUploadOptions(true));

        try
        {
            await service.UploadOnceAsync(now.AddMinutes(1), CancellationToken.None);

            Assert.InRange(requestBytes, 1, maximumRequestBytes);
            Assert.Equal(6, sentCount);
            Assert.True(allEventsComplete);
            var remaining = await store.ReadPendingAsync(
                ClientLogOutboxKind.OperationAudit,
                now.AddHours(1),
                100,
                CancellationToken.None);
            Assert.Equal(eventCount - sentCount, remaining.Count);
        }
        finally
        {
            service.Dispose();
            client.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Oversized_single_operation_is_permanently_rejected_without_http_retry()
    {
        const int maximumRequestBytes = 4 * 1024 * 1024;
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        var eventId = Guid.Parse("78787878-7878-7878-7878-787878787878");
        var payload = JsonSerializer.Serialize(new
        {
            eventId,
            details = new string('x', maximumRequestBytes + 1)
        });
        await store.EnqueueAsync(ClientLogOutboxKind.OperationAudit, eventId, now, payload, now, CancellationToken.None);
        var callCount = 0;
        var client = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            callCount++;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"results\":[]}"));
        }))
        {
            BaseAddress = new Uri("https://pos-api.example.com/")
        };
        var service = new OperationAuditUploadService(
            store,
            client,
            TimeProvider.System,
            new OperationAuditUploadOptions(true));
        var sink = new FakeApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);

        try
        {
            await service.UploadOnceAsync(now, CancellationToken.None);

            Assert.Equal(0, callCount);
            var rejected = Assert.Single(await store.ReadRejectedAsync(
                ClientLogOutboxKind.OperationAudit,
                100,
                CancellationToken.None));
            Assert.Equal("PAYLOAD_TOO_LARGE", rejected.LastErrorCode);
            Assert.Contains(sink.Entries, entry => entry.Level == "Critical" && entry.Message.Contains(eventId.ToString("D"), StringComparison.Ordinal));
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
            service.Dispose();
            client.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Invalid_local_operation_payload_is_rejected_without_http_call()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        var eventId = Guid.Parse("89898989-8989-8989-8989-898989898989");
        await store.EnqueueAsync(ClientLogOutboxKind.OperationAudit, eventId, now, "not-json", now, CancellationToken.None);
        var callCount = 0;
        var client = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            callCount++;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"results\":[]}"));
        }))
        {
            BaseAddress = new Uri("https://pos-api.example.com/")
        };
        var service = new OperationAuditUploadService(
            store,
            client,
            TimeProvider.System,
            new OperationAuditUploadOptions(true));

        try
        {
            await service.UploadOnceAsync(now, CancellationToken.None);

            Assert.Equal(0, callCount);
            var rejected = Assert.Single(await store.ReadRejectedAsync(
                ClientLogOutboxKind.OperationAudit,
                100,
                CancellationToken.None));
            Assert.Equal("INVALID_LOCAL_PAYLOAD", rejected.LastErrorCode);
        }
        finally
        {
            service.Dispose();
            client.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Invalid_success_response_schedules_retry_instead_of_hot_looping()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        var eventId = Guid.Parse("90909090-9090-9090-9090-909090909090");
        await store.EnqueueAsync(ClientLogOutboxKind.Runtime, eventId, now, "{}", now, CancellationToken.None);
        var service = new ApplicationLogUploadService(
            store,
            CreateApplicationOptions(),
            new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK, "not-json")))),
            TimeProvider.System);

        try
        {
            await service.UploadOnceAsync(now, CancellationToken.None);

            Assert.Empty(await store.ReadPendingAsync(ClientLogOutboxKind.Runtime, now, 100, CancellationToken.None));
            var retry = Assert.Single(await store.ReadPendingAsync(
                ClientLogOutboxKind.Runtime,
                now.AddHours(1),
                100,
                CancellationToken.None));
            Assert.Equal("INVALID_RESPONSE", retry.LastErrorCode);
            Assert.Equal(1, retry.AttemptCount);
        }
        finally
        {
            service.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Runtime_upload_respects_configured_batch_size()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        var ids = new[]
        {
            Guid.Parse("a1000000-0000-0000-0000-000000000001"),
            Guid.Parse("a1000000-0000-0000-0000-000000000002"),
            Guid.Parse("a1000000-0000-0000-0000-000000000003")
        };
        foreach (var eventId in ids)
        {
            await store.EnqueueAsync(
                ClientLogOutboxKind.Runtime,
                eventId,
                now,
                JsonSerializer.Serialize(new { clientEventId = eventId }),
                now,
                CancellationToken.None);
        }

        var sentCount = 0;
        var service = new ApplicationLogUploadService(
            store,
            CreateApplicationOptions(batchSize: 2),
            new HttpClient(new StubHttpMessageHandler(async request =>
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var logs = document.RootElement.GetProperty("logs").EnumerateArray().ToArray();
                sentCount = logs.Length;
                var results = logs.Select(item => new
                {
                    clientEventId = item.GetProperty("clientEventId").GetGuid(),
                    status = "accepted"
                });
                return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { results }));
            })),
            TimeProvider.System);

        try
        {
            await service.UploadOnceAsync(now, CancellationToken.None);

            Assert.Equal(2, sentCount);
            Assert.Single(await store.ReadPendingAsync(ClientLogOutboxKind.Runtime, now, 100, CancellationToken.None));
        }
        finally
        {
            service.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Runtime_uploader_reports_dropped_log_delta_once_per_increase()
    {
        var databasePath = CreateDatabasePath();
        // 本地 outbox 只保留 2 条待传运行日志，第 3、4 条写入时会裁掉最旧的 2 条。
        var store = new ClientLogOutboxStore(databasePath, runtimePendingLimit: 2);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        for (var index = 0; index < 4; index++)
        {
            await store.EnqueueAsync(
                ClientLogOutboxKind.Runtime,
                Guid.NewGuid(),
                now.AddSeconds(index),
                "{}",
                now,
                CancellationToken.None);
        }

        long queueDropped = 3;
        var service = new ApplicationLogUploadService(
            store,
            CreateApplicationOptions() with { Enabled = false },
            new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("禁用时不应发起 HTTP"))),
            TimeProvider.System,
            () => Interlocked.Read(ref queueDropped));
        var sink = new FakeApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);

        try
        {
            await service.UploadOnceAsync(now, CancellationToken.None);
            await service.UploadOnceAsync(now.AddMinutes(1), CancellationToken.None);

            var dropped = Assert.Single(sink.Entries, entry => entry.Category == "LogOutbox");
            Assert.Equal("Warning", dropped.Level);
            Assert.Contains("delta=5", dropped.Message, StringComparison.Ordinal);
            Assert.Equal(5L, Convert.ToInt64(dropped.Properties!["droppedCount"]));

            Interlocked.Increment(ref queueDropped);
            await service.UploadOnceAsync(now.AddMinutes(2), CancellationToken.None);

            var entries = sink.Entries.Where(entry => entry.Category == "LogOutbox").ToArray();
            Assert.Equal(2, entries.Length);
            Assert.Contains("delta=1", entries[1].Message, StringComparison.Ordinal);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
            service.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Operation_audit_authorization_failure_writes_single_runtime_warning_until_recovered()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
        var eventId = Guid.Parse("56565656-5656-5656-5656-565656565656");
        await store.EnqueueAsync(ClientLogOutboxKind.OperationAudit, eventId, now, "{}", now, CancellationToken.None);
        var client = new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))))
        {
            BaseAddress = new Uri("https://pos-api.example.com/")
        };
        var service = new OperationAuditUploadService(
            store,
            client,
            TimeProvider.System,
            new OperationAuditUploadOptions(true));
        var sink = new FakeApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);

        try
        {
            await service.UploadOnceAsync(now, CancellationToken.None);
            await service.UploadOnceAsync(now.AddMinutes(31), CancellationToken.None);

            var warning = Assert.Single(sink.Entries, entry => entry.Category == "LogUpload");
            Assert.Equal("Warning", warning.Level);
            Assert.Equal(401, warning.StatusCode);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
            service.Dispose();
            client.Dispose();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    private static ApplicationLogOptions CreateApplicationOptions(int batchSize = 100) => new(
        true,
        "log-key",
        "hbpos_win",
        "test",
        "POS",
        "Hbpos.Client.Wpf",
        new Uri("https://logs.example.com/api/system/logs/ingest"),
        batchSize,
        20);

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string CreateDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"hbpos-logs-upload-test-{Guid.NewGuid():N}.db");

    private static async Task<long> CountPendingOperationEventsAsync(
        ClientLogOutboxStore store,
        Guid? eventId = null)
    {
        await using var connection = await store.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = eventId.HasValue
            ? "SELECT COUNT(*) FROM OperationAuditOutbox WHERE State = 'Pending' AND EventId = $eventId;"
            : "SELECT COUNT(*) FROM OperationAuditOutbox WHERE State = 'Pending';";
        if (eventId.HasValue)
        {
            command.Parameters.AddWithValue("$eventId", eventId.Value.ToString("D"));
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RequestUpload_wakes_operation_uploader_without_waiting_for_periodic_interval()
    {
        var databasePath = CreateDatabasePath();
        var store = new ClientLogOutboxStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);
        var expiredRejectedId = Guid.NewGuid();
        var expiredAt = DateTimeOffset.UtcNow.AddDays(-31);
        await store.EnqueueAsync(
            ClientLogOutboxKind.OperationAudit,
            expiredRejectedId,
            expiredAt,
            JsonSerializer.Serialize(new { eventId = expiredRejectedId, storeCode = "S001", deviceCode = "POS-01" }),
            expiredAt,
            CancellationToken.None);
        await store.ApplyResultsAsync(
            ClientLogOutboxKind.OperationAudit,
            [],
            [new ClientLogRejection(expiredRejectedId, "REJECTED", "expired")],
            expiredAt,
            CancellationToken.None);
        var authorization = new DeviceAuthorizationState();
        authorization.Set(new DeviceAuthorizationContext("POS-01", "S001", "HW-01", "secret"));
        var timeProvider = new ObservableWaitTimeProvider();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var eventId = document.RootElement.GetProperty("events")[0].GetProperty("eventId").GetGuid();
            called.TrySetResult();
            return JsonResponse(HttpStatusCode.OK, $$"""{"results":[{"eventId":"{{eventId:D}}","status":"accepted"}]}""");
        }))
        {
            BaseAddress = new Uri("https://api.example.test/")
        };
        var service = new OperationAuditUploadService(
            store,
            client,
            timeProvider,
            new OperationAuditUploadOptions(true),
            authorization);
        try
        {
            await service.StartAsync(CancellationToken.None);
            // 中文说明：同时观察清理副作用和 60 秒定时器，确保首次周期结束且上传器已进入等待。
            await WaitForNoRejectedOperationEventsAsync(store);
            await timeProvider.WaitUntilPeriodicDelayAsync();
            var eventId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await store.EnqueueAsync(
                ClientLogOutboxKind.OperationAudit,
                eventId,
                now,
                JsonSerializer.Serialize(new { eventId, storeCode = "S001", deviceCode = "POS-01" }),
                now,
                CancellationToken.None);

            service.RequestUpload();

            await called.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            await WaitForOperationPendingCountAsync(store, 0);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
            client.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    private static async Task WaitForOperationPendingCountAsync(ClientLogOutboxStore store, long expectedCount)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await CountPendingOperationEventsAsync(store) == expectedCount)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"操作审计 Pending 数未在预期时间内变为 {expectedCount}。");
    }

    private static async Task WaitForNoRejectedOperationEventsAsync(ClientLogOutboxStore store)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await store.ReadRejectedAsync(
                    ClientLogOutboxKind.OperationAudit,
                    1,
                    CancellationToken.None)).Count == 0)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("操作审计首次清理周期未在预期时间内完成。");
    }

    private sealed class ObservableWaitTimeProvider : TimeProvider
    {
        private readonly TaskCompletionSource _periodicDelayStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            if (dueTime >= TimeSpan.FromSeconds(60))
            {
                _periodicDelayStarted.TrySetResult();
            }

            return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
        }

        public Task WaitUntilPeriodicDelayAsync() =>
            _periodicDelayStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
    }

    private static async Task WaitForOutboxSchemaAsync(ClientLogOutboxStore store)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                _ = await store.ReadPendingAsync(
                    ClientLogOutboxKind.Runtime,
                    DateTimeOffset.UtcNow,
                    1,
                    CancellationToken.None);
                return;
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                await Task.Delay(20);
            }
        }

        throw new TimeoutException("上传器启动后未在预期时间内初始化日志 outbox。");
    }

    private static async Task WaitForRuntimePendingCountAsync(ClientLogOutboxStore store, int expectedCount)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var pending = await store.ReadPendingAsync(
                ClientLogOutboxKind.Runtime,
                DateTimeOffset.UtcNow.AddMinutes(1),
                100,
                CancellationToken.None);
            if (pending.Count == expectedCount)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"系统日志 Pending 数未在预期时间内变为 {expectedCount}。");
    }

    private static Task DeleteDatabaseFilesAsync(string databasePath)
    {
        // Windows 上连接 Dispose 后句柄可能短暂保留，统一走共享的 best-effort 清理，避免清理阶段误报。
        return SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }

    private sealed class EndpointSwitchAuditHandler(Guid eventId) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstRequestStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public Task FirstRequestStarted => _firstRequestStarted.Task;

        public List<Uri> RequestUris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                _firstRequestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                results = new[] { new { eventId, status = "accepted" } }
            }));
        }
    }

    private sealed class FakeApplicationLogSink : IApplicationLogSink
    {
        private readonly ConcurrentQueue<ApplicationLogEntry> _entries = new();

        public IReadOnlyList<ApplicationLogEntry> Entries => _entries.ToArray();

        public void Enqueue(ApplicationLogEntry entry) => _entries.Enqueue(entry);
    }
}
