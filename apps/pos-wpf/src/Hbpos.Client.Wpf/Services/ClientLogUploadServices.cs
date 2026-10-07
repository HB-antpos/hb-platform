using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Hbpos.Client.Wpf.Services;

internal sealed record OperationAuditUploadOptions(bool Enabled)
{
    public static OperationAuditUploadOptions FromConfiguration(IConfiguration configuration)
    {
        var environmentValue = Environment.GetEnvironmentVariable("HBPOS_OPERATION_AUDIT_UPLOAD_ENABLED");
        if (bool.TryParse(environmentValue, out var environmentEnabled))
        {
            return new OperationAuditUploadOptions(environmentEnabled);
        }

        return new OperationAuditUploadOptions(
            !bool.TryParse(configuration["OperationAuditLogging:Enabled"], out var configuredEnabled) || configuredEnabled);
    }
}

internal abstract class ClientLogUploadServiceBase : BackgroundService
{
    private static readonly TimeSpan[] RetrySchedule =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30)
    ];

    private readonly ClientLogOutboxStore _store;
    private readonly HttpClient _httpClient;
    private readonly ClientLogOutboxKind _kind;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _uploadInterval;
    private readonly int _batchSize;
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private bool _authorizationFailureReported;

    protected ClientLogUploadServiceBase(
        ClientLogOutboxStore store,
        HttpClient httpClient,
        ClientLogOutboxKind kind,
        TimeProvider timeProvider,
        TimeSpan? uploadInterval = null,
        int batchSize = 100)
    {
        _store = store;
        _httpClient = httpClient;
        _kind = kind;
        _timeProvider = timeProvider;
        _uploadInterval = uploadInterval ?? TimeSpan.FromSeconds(60);
        _batchSize = Math.Clamp(batchSize, 1, 100);
    }

    protected abstract bool IsEnabled { get; }

    protected abstract HttpRequestMessage CreateRequest(IReadOnlyList<JsonElement> payloads);

    protected abstract string EventIdPropertyName { get; }

    protected ClientLogOutboxStore Store => _store;

    protected virtual Task<IReadOnlyList<ClientLogOutboxRecord>> ReadCandidatesAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        return _store.ReadPendingAsync(_kind, nowUtc, _batchSize, cancellationToken);
    }

    protected virtual Task<IReadOnlyList<ClientLogOutboxRecord>> SelectBatchAsync(
        IReadOnlyList<ClientLogOutboxRecord> records,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        _ = nowUtc;
        _ = cancellationToken;
        return Task.FromResult(records);
    }

    internal async Task UploadOnceAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken,
        bool wakeAfterSuccessfulFullAuditBatch = false)
    {
        if (!await _uploadGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            await _store.InitializeAsync(cancellationToken);
            await _store.DeleteExpiredRejectedAsync(nowUtc, cancellationToken);
            ReportChannelHealth();
            if (!IsEnabled)
            {
                return;
            }

            var records = await ReadCandidatesAsync(nowUtc, cancellationToken);
            records = await SelectBatchAsync(records, nowUtc, cancellationToken);
            if (records.Count == 0)
            {
                return;
            }

            var payloads = new List<JsonElement>(records.Count);
            var validRecords = new List<ClientLogOutboxRecord>(records.Count);
            var invalidPayloads = new List<ClientLogRejection>();
            foreach (var record in records)
            {
                try
                {
                    using var document = JsonDocument.Parse(record.PayloadJson);
                    payloads.Add(document.RootElement.Clone());
                    validRecords.Add(record);
                }
                catch (JsonException)
                {
                    invalidPayloads.Add(new ClientLogRejection(
                        record.EventId,
                        "INVALID_LOCAL_PAYLOAD",
                        "local outbox payload is not valid JSON"));
                }
            }

            if (invalidPayloads.Count > 0)
            {
                await _store.ApplyResultsAsync(_kind, [], invalidPayloads, nowUtc, cancellationToken);
                ReportPermanentRejections(invalidPayloads, "invalid local payload");
            }

            records = validRecords;
            if (records.Count == 0)
            {
                return;
            }

            using var request = CreateRequest(payloads);
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(15));
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, timeoutCancellation.Token);
            }
            catch (OperationCanceledException) when (
                timeoutCancellation.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                await ScheduleRetryAsync(records, nowUtc, "TIMEOUT", "upload timed out", cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 服务器端点切换会取消旧代际请求；保留原重试时间，切换完成后的唤醒即可立即续传。
                return;
            }
            catch (HttpRequestException ex)
            {
                await ScheduleRetryAsync(records, nowUtc, "NETWORK", ex.GetType().Name, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await ScheduleRetryAsync(records, nowUtc, "UPLOAD_ERROR", ex.GetType().Name, cancellationToken);
                return;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    await HandleHttpFailureAsync(response, records, nowUtc, cancellationToken);
                    return;
                }

                try
                {
                    await ApplyAcknowledgementsAsync(response, records, nowUtc, cancellationToken);
                    _authorizationFailureReported = false;
                    if (wakeAfterSuccessfulFullAuditBatch &&
                        _kind == ClientLogOutboxKind.OperationAudit &&
                        records.Count == _batchSize)
                    {
                        // 满批成功后立即处理下一批，避免积压审计事件被 60 秒空闲轮询限速。
                        SignalWake();
                    }
                }
                catch (JsonException ex)
                {
                    await ScheduleRetryAsync(
                        records,
                        nowUtc,
                        "INVALID_RESPONSE",
                        ex.GetType().Name,
                        cancellationToken);
                }
            }
        }
        catch (JsonException ex)
        {
            WriteInternalDiagnostic($"invalid local payload kind={_kind} error={ex.GetType().Name}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 日志库或上传器自身故障只能进入旁路诊断，不能让 BackgroundService 停止 POS 主进程。
            WriteInternalDiagnostic($"upload cycle failed kind={_kind} error={ex.GetType().Name}");
        }
        finally
        {
            _uploadGate.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await UploadOnceAsync(_timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await UploadOnceAsync(
                    _timeProvider.GetUtcNow(),
                    stoppingToken,
                    wakeAfterSuccessfulFullAuditBatch: true);
                await WaitForNextTriggerAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        }
    }

    private async Task WaitForNextTriggerAsync(CancellationToken stoppingToken)
    {
        using var cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var intervalTask = Task.Delay(_uploadInterval, _timeProvider, cycleCancellation.Token);
        var wakeTask = _wakeSignal.WaitAsync(cycleCancellation.Token);
        await Task.WhenAny(intervalTask, wakeTask);
        // 取消未胜出的等待，避免旧 wake waiter 抢走后续联网恢复信号。
        await cycleCancellation.CancelAsync();
        try
        {
            await Task.WhenAll(intervalTask, wakeTask);
        }
        catch (OperationCanceledException) when (cycleCancellation.IsCancellationRequested)
        {
        }
    }

    private async Task ApplyAcknowledgementsAsync(
        HttpResponseMessage response,
        IReadOnlyList<ClientLogOutboxRecord> records,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var resultContainer = document.RootElement;
        if (!TryGetProperty(resultContainer, "results", out var resultsElement) &&
            TryGetProperty(resultContainer, "data", out var dataElement) &&
            dataElement.ValueKind == JsonValueKind.Object)
        {
            _ = TryGetProperty(dataElement, "results", out resultsElement);
        }

        if (resultsElement.ValueKind != JsonValueKind.Array)
        {
            await ScheduleMissingAcknowledgementsAsync(records, [], nowUtc, cancellationToken);
            return;
        }

        var knownIds = records.Select(item => item.EventId).ToHashSet();
        var acknowledgedIds = new HashSet<Guid>();
        var completedIds = new List<Guid>();
        var rejections = new List<ClientLogRejection>();
        foreach (var result in resultsElement.EnumerateArray())
        {
            if (!TryGetGuid(result, EventIdPropertyName, out var eventId) || !knownIds.Contains(eventId) ||
                !TryGetProperty(result, "status", out var statusElement) ||
                statusElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var status = statusElement.GetString();
            if (string.Equals(status, "accepted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "duplicate", StringComparison.OrdinalIgnoreCase))
            {
                completedIds.Add(eventId);
                acknowledgedIds.Add(eventId);
            }
            else if (string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase))
            {
                var errorCode = GetOptionalString(result, "errorCode");
                var errorMessage = GetOptionalString(result, "errorMessage");
                rejections.Add(new ClientLogRejection(eventId, errorCode ?? "REJECTED", errorMessage));
                acknowledgedIds.Add(eventId);
            }
        }

        await _store.ApplyResultsAsync(_kind, completedIds, rejections, nowUtc, cancellationToken);
        if (rejections.Count > 0)
        {
            ReportPermanentRejections(rejections, "server rejection");
        }

        await ScheduleMissingAcknowledgementsAsync(records, acknowledgedIds, nowUtc, cancellationToken);
    }

    private async Task ScheduleMissingAcknowledgementsAsync(
        IReadOnlyList<ClientLogOutboxRecord> records,
        IReadOnlyCollection<Guid> acknowledgedIds,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var missingIds = records
            .Where(item => !acknowledgedIds.Contains(item.EventId))
            .Select(item => item.EventId)
            .ToArray();
        if (missingIds.Length == 0)
        {
            return;
        }

        var delay = CalculateRetryDelay(records.Max(item => item.AttemptCount) + 1);
        await _store.ScheduleRetryAsync(
            _kind,
            missingIds,
            nowUtc.Add(delay),
            "MISSING_ACK",
            "server did not acknowledge this event",
            cancellationToken);
    }

    private async Task HandleHttpFailureAsync(
        HttpResponseMessage response,
        IReadOnlyList<ClientLogOutboxRecord> records,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        TimeSpan delay;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            delay = TimeSpan.FromMinutes(30);
            // 鉴权配置错误直接写本地诊断通道，明确绕开 ConsoleLog sink。
            WriteInternalDiagnostic($"CRITICAL configuration failure kind={_kind} http={(int)response.StatusCode}");
            if (_kind == ClientLogOutboxKind.OperationAudit && !_authorizationFailureReported)
            {
                // 审计上传被拒时运行日志通道通常仍可用，只在状态切换时记一次，恢复成功后才允许再次记录。
                _authorizationFailureReported = true;
                ConsoleLog.WriteWarning(
                    "LogUpload",
                    $"operation audit upload unauthorized http={(int)response.StatusCode} pending={records.Count} retryInMinutes={delay.TotalMinutes:0}",
                    new ApplicationLogContext(
                        RequestMethod: "POST",
                        StatusCode: (int)response.StatusCode,
                        Properties: new Dictionary<string, object?>
                        {
                            ["itemCount"] = records.Count
                        }));
            }
        }
        else if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            delay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - nowUtc) ??
                    TimeSpan.FromMinutes(1);
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.FromMinutes(1);
            }
        }
        else
        {
            delay = CalculateRetryDelay(records.Max(item => item.AttemptCount) + 1);
        }

        await _store.ScheduleRetryAsync(
            _kind,
            records.Select(item => item.EventId).ToArray(),
            nowUtc.Add(delay),
            $"HTTP_{(int)response.StatusCode}",
            response.ReasonPhrase,
            cancellationToken);
    }

    private async Task ScheduleRetryAsync(
        IReadOnlyList<ClientLogOutboxRecord> records,
        DateTimeOffset nowUtc,
        string errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var delay = CalculateRetryDelay(records.Max(item => item.AttemptCount) + 1);
        await _store.ScheduleRetryAsync(
            _kind,
            records.Select(item => item.EventId).ToArray(),
            nowUtc.Add(delay),
            errorCode,
            errorMessage,
            cancellationToken);
    }

    private void ReportPermanentRejections(
        IReadOnlyList<ClientLogRejection> rejections,
        string reason)
    {
        WriteInternalDiagnostic(
            $"CRITICAL permanent rejection kind={_kind} reason={reason} count={rejections.Count} firstEventId={rejections[0].EventId:D}");
        if (_kind == ClientLogOutboxKind.OperationAudit)
        {
            // 操作审计拒绝转为系统 Critical；系统日志自身被拒绝时不会走此分支，避免递归。
            ConsoleLog.WriteCritical(
                "OperationAudit",
                $"operation audit permanently rejected reason={reason} count={rejections.Count} firstEventId={rejections[0].EventId:D}");
        }
    }

    private static TimeSpan CalculateRetryDelay(int attempt)
    {
        var baseDelay = RetrySchedule[Math.Clamp(attempt - 1, 0, RetrySchedule.Length - 1)];
        return baseDelay + TimeSpan.FromSeconds(Random.Shared.Next(0, 16));
    }

    private static bool TryGetGuid(JsonElement element, string propertyName, out Guid value)
    {
        value = Guid.Empty;
        return TryGetProperty(element, propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               Guid.TryParse(property.GetString(), out value);
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args)
    {
        if (!args.IsAvailable)
        {
            return;
        }

        SignalWake();
    }

    private void SignalWake()
    {
        if (_wakeSignal.CurrentCount != 0)
        {
            return;
        }

        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    internal void RequestUpload()
    {
        SignalWake();
    }

    /// <summary>
    /// 运行日志通道的自检：本轮以前被丢弃（内存队列溢出或本地 outbox 超上限裁剪）的条数有增量时记一次 Warning。
    /// </summary>
    protected virtual void ReportChannelHealth()
    {
    }

    protected static void WriteInternalDiagnostic(string message)
    {
        var line = $"[HBPOS][Client][LogUpload] {DateTimeOffset.Now:O} {message}";
        Console.WriteLine(line);
        Debug.WriteLine(line);
        Trace.WriteLine(line);
    }
}

internal sealed class ApplicationLogUploadService(
    ClientLogOutboxStore store,
    ApplicationLogOptions options,
    HttpClient httpClient,
    TimeProvider timeProvider,
    Func<long>? queueDroppedCount = null) : ClientLogUploadServiceBase(
        store,
        httpClient,
        ClientLogOutboxKind.Runtime,
        timeProvider,
        batchSize: options.BatchSize)
{
    private long _reportedStoreDropped;
    private long _reportedQueueDropped;

    protected override bool IsEnabled => options.IsConfigured;

    protected override void ReportChannelHealth()
    {
        var storeDropped = Store.RuntimeDroppedCount;
        var queueDropped = queueDroppedCount?.Invoke() ?? 0;
        var delta = (storeDropped - _reportedStoreDropped) + (queueDropped - _reportedQueueDropped);
        if (delta <= 0)
        {
            return;
        }

        _reportedStoreDropped = storeDropped;
        _reportedQueueDropped = queueDropped;
        // 只报增量：每轮上传最多一条，不会因自身写入形成刷屏；离线积压被裁剪时能在恢复后看到丢了多少。
        ConsoleLog.WriteWarning(
            "LogOutbox",
            $"runtime logs dropped delta={delta} storeTrimmedTotal={storeDropped} queueDroppedTotal={queueDropped}",
            new ApplicationLogContext(
                Properties: new Dictionary<string, object?>
                {
                    ["droppedCount"] = delta
                }));
    }

    protected override string EventIdPropertyName => "clientEventId";

    protected override HttpRequestMessage CreateRequest(IReadOnlyList<JsonElement> payloads)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, options.IngestUri)
        {
            Content = JsonContent.Create(new { logs = payloads })
        };
        request.Headers.TryAddWithoutValidation("X-Log-Project", options.ProjectCode);
        request.Headers.TryAddWithoutValidation("X-Log-Key", options.ApiKey);
        return request;
    }
}

internal sealed class OperationAuditUploadService : ClientLogUploadServiceBase
{
    // 服务端逐条事务写入，批量上限需在客户端 15 秒请求超时内为网络和数据库写入留出余量。
    private const int UploadBatchSize = 8;
    private const int MaximumRequestBytes = 4 * 1024 * 1024;
    private static readonly int EmptyRequestBytes = Encoding.UTF8.GetByteCount("{\"events\":[]}");
    private readonly bool _isEnabled;
    private readonly DeviceAuthorizationState? _authorizationState;

    public OperationAuditUploadService(
        ClientLogOutboxStore store,
        HttpClient httpClient,
        TimeProvider timeProvider,
        OperationAuditUploadOptions options,
        DeviceAuthorizationState? authorizationState = null) : base(
            store,
            httpClient,
            ClientLogOutboxKind.OperationAudit,
            timeProvider,
            batchSize: UploadBatchSize)
    {
        _isEnabled = options.Enabled && httpClient.BaseAddress is { IsAbsoluteUri: true };
        _authorizationState = authorizationState;
    }

    protected override bool IsEnabled => _isEnabled;

    protected override string EventIdPropertyName => "eventId";

    protected override Task<IReadOnlyList<ClientLogOutboxRecord>> ReadCandidatesAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (_authorizationState is null)
        {
            return base.ReadCandidatesAsync(nowUtc, cancellationToken);
        }

        var scope = _authorizationState.Current;
        if (scope is null || string.IsNullOrWhiteSpace(scope.StoreCode) || string.IsNullOrWhiteSpace(scope.DeviceCode))
        {
            return Task.FromResult<IReadOnlyList<ClientLogOutboxRecord>>([]);
        }

        // 先按当前设备 claims 对 payload scope 做数据库过滤，旧设备事件保留 Pending 且不会占用当前上传批次。
        return Store.ReadPendingOperationForScopeAsync(
            nowUtc,
            scope.StoreCode,
            scope.DeviceCode,
            UploadBatchSize,
            cancellationToken);
    }

    protected override async Task<IReadOnlyList<ClientLogOutboxRecord>> SelectBatchAsync(
        IReadOnlyList<ClientLogOutboxRecord> records,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var selected = new List<ClientLogOutboxRecord>(records.Count);
        var oversized = new List<ClientLogRejection>();
        var requestBytes = EmptyRequestBytes;
        foreach (var record in records)
        {
            var eventBytes = Encoding.UTF8.GetByteCount(record.PayloadJson);
            var additionalBytes = eventBytes + (selected.Count == 0 ? 0 : 1);
            if (requestBytes + additionalBytes <= MaximumRequestBytes)
            {
                selected.Add(record);
                requestBytes += additionalBytes;
                continue;
            }

            if (selected.Count > 0)
            {
                // 只发送不超过 4 MiB 的最旧前缀，剩余事件留待下一批。
                break;
            }

            oversized.Add(new ClientLogRejection(
                record.EventId,
                "PAYLOAD_TOO_LARGE",
                $"serialized event exceeds {MaximumRequestBytes} bytes"));
        }

        if (oversized.Count > 0)
        {
            await Store.ApplyResultsAsync(
                ClientLogOutboxKind.OperationAudit,
                [],
                oversized,
                nowUtc,
                cancellationToken);
            WriteInternalDiagnostic(
                $"CRITICAL oversized operation audit count={oversized.Count} firstEventId={oversized[0].EventId:D}");
            ConsoleLog.WriteCritical(
                "OperationAudit",
                $"operation audit payload too large count={oversized.Count} firstEventId={oversized[0].EventId:D}");
        }

        return selected;
    }

    protected override HttpRequestMessage CreateRequest(IReadOnlyList<JsonElement> payloads)
    {
        return new HttpRequestMessage(HttpMethod.Post, "api/v1/operation-audits/batch")
        {
            Content = JsonContent.Create(new { events = payloads })
        };
    }
}
