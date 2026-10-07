using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Hosting;

namespace Hbpos.Client.Wpf.Services;

public interface IOperationAuditLogger
{
    void Record(OperationAuditEventDto auditEvent);
}

internal sealed record ClientLogIdentity(string InstanceId, string AppVersion)
{
    public static ClientLogIdentity CreateCurrent()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(ClientLogIdentity).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        return new ClientLogIdentity(Guid.NewGuid().ToString("D"), version);
    }
}

internal sealed record QueuedClientLog(Guid EventId, DateTimeOffset OccurredAtUtc, string PayloadJson);

internal sealed class ClientLogOutboxWriter : BackgroundService, IApplicationLogSink, IOperationAuditLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedRuntimeProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "category", "storeCode", "deviceCode", "errorCode", "elapsedMs", "source", "action",
        "status", "screen", "mode", "reason", "result", "itemCount",
        // 联网重试与日志通道自检用到的计数字段，只允许数值/短文本，不承载业务原文。
        "attemptCount", "nextRetrySeconds", "operation", "phase", "droppedCount", "offlineSeconds"
    };
    private static readonly HashSet<string> AllowedOperationProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "source", "action", "status", "screen", "mode", "reason", "result",
        "paymentMethod", "cashDrawerMode", "itemCount", "requestingCashierId",
        "authorizingCashierId", "authorizingUserGuid", "permissionCode", "authorizationMode"
    };
    private readonly ClientLogOutboxStore _store;
    private readonly DeviceAuthorizationState _authorizationState;
    private readonly ICashierSessionContext _cashierSessionContext;
    private readonly ClientLogIdentity _identity;
    private readonly int _runtimeQueueCapacity;
    private readonly int _operationAuditQueueCapacity;
    private readonly Channel<QueuedClientLog> _runtimeChannel;
    private readonly Channel<QueuedClientLog> _operationChannel;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _operationRevisionGate = new();
    private readonly object _operationFlushGate = new();
    private TaskCompletionSource _operationFlushed = CreateCompletedSource();
    private long _pendingOperationAuditPersistenceCount;
    private long _operationAuditRevision;
    private long _runtimeQueueDroppedCount;
    private long _operationAuditQueueDroppedCount;
    private CancellationToken _hostShutdownToken = CancellationToken.None;
    // 0 = 尚无人负责消费通道与最终落库；1 = 已由 ExecuteAsync 或 StopAsync 其中一方认领，保证只落库一次。
    private int _drainOwnershipClaimed;

    public ClientLogOutboxWriter(
        ClientLogOutboxStore store,
        DeviceAuthorizationState authorizationState,
        ICashierSessionContext cashierSessionContext,
        ClientLogIdentity identity,
        int runtimeQueueCapacity = 200,
        int operationAuditQueueCapacity = 1000)
    {
        _store = store;
        _authorizationState = authorizationState;
        _cashierSessionContext = cashierSessionContext;
        _identity = identity;
        _runtimeQueueCapacity = Math.Clamp(runtimeQueueCapacity, 10, 5_000);
        _operationAuditQueueCapacity = Math.Clamp(operationAuditQueueCapacity, 10, 5_000);
        _runtimeChannel = Channel.CreateBounded<QueuedClientLog>(
            new BoundedChannelOptions(_runtimeQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            },
            _ => Interlocked.Increment(ref _runtimeQueueDroppedCount));
        _operationChannel = Channel.CreateBounded<QueuedClientLog>(
            new BoundedChannelOptions(_operationAuditQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            },
            _ => HandleOperationAuditQueueDrop());
    }

    public long RuntimeQueueDroppedCount => Interlocked.Read(ref _runtimeQueueDroppedCount);

    public long OperationAuditQueueDroppedCount => Interlocked.Read(ref _operationAuditQueueDroppedCount);

    public long PendingOperationAuditPersistenceCount =>
        Interlocked.Read(ref _pendingOperationAuditPersistenceCount);

    public Task WaitForOperationAuditFlushAsync(CancellationToken cancellationToken)
    {
        Task flushed;
        lock (_operationFlushGate)
        {
            flushed = _operationFlushed.Task;
        }

        return flushed.WaitAsync(cancellationToken);
    }

    public long CaptureOperationAuditRevision()
    {
        lock (_operationRevisionGate)
        {
            return _operationAuditRevision;
        }
    }

    public bool TryPublishForOperationAuditRevision(long expectedRevision, Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (_operationRevisionGate)
        {
            if (_operationAuditRevision != expectedRevision)
            {
                return false;
            }

            // revision 校验与运行时边界发布必须原子，Record 只能发生在发布前或发布后。
            publish();
            return true;
        }
    }

    public void PublishWithinOperationAuditBoundary(Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        // Record 与端点发布共用此锁，保证每条审计完整落在旧端点或新端点一侧。
        lock (_operationRevisionGate)
        {
            publish();
        }
    }

    public void Enqueue(ApplicationLogEntry entry)
    {
        try
        {
            var eventId = Guid.NewGuid();
            var session = _cashierSessionContext.CurrentSession;
            var device = _authorizationState.Current;
            var payload = new ApplicationLogIngestItemDto
            {
                ClientEventId = eventId,
                Level = entry.Level,
                Message = entry.Message,
                TimestampUtc = entry.TimestampUtc.UtcDateTime,
                ProjectCode = entry.ProjectCode,
                Environment = entry.Environment,
                SourceType = entry.SourceType,
                Category = entry.Category,
                ServiceName = entry.ServiceName,
                InstanceId = _identity.InstanceId,
                StoreCode = device?.StoreCode ?? session?.StoreCode,
                DeviceCode = device?.DeviceCode ?? session?.DeviceCode,
                AppVersion = _identity.AppVersion,
                TraceId = entry.TraceId,
                RequestPath = entry.RequestPath,
                RequestMethod = entry.RequestMethod,
                StatusCode = entry.StatusCode,
                UserId = entry.UserId ?? session?.CashierId,
                UserName = entry.UserName ?? session?.CashierName,
                ExceptionType = entry.ExceptionType,
                ExceptionMessage = entry.ExceptionMessage,
                StackTrace = entry.StackTrace,
                Properties = entry.Properties is null
                    ? null
                    : entry.Properties
                        .Where(property => AllowedRuntimeProperties.Contains(property.Key))
                        .ToDictionary(property => property.Key, property => property.Value, StringComparer.OrdinalIgnoreCase)
            };
            var queued = new QueuedClientLog(eventId, entry.TimestampUtc, ClientLogSanitizer.Serialize(payload));
            if (_runtimeChannel.Writer.TryWrite(queued))
            {
                TryReleaseSignal();
            }
        }
        catch (Exception ex)
        {
            WriteInternalDiagnostic($"runtime enqueue failed error={ex.GetType().Name}");
        }
    }

    public void Record(OperationAuditEventDto auditEvent)
    {
        try
        {
            if (auditEvent is null)
            {
                return;
            }

            // 在调用线程同步克隆和脱敏，后续业务对象变化不会污染已记录的审计快照。
            var snapshot = JsonSerializer.Deserialize<OperationAuditEventDto>(
                JsonSerializer.Serialize(auditEvent, JsonOptions),
                JsonOptions) ?? new OperationAuditEventDto();
            snapshot.EventId = snapshot.EventId == Guid.Empty ? Guid.NewGuid() : snapshot.EventId;
            snapshot.SchemaVersion = snapshot.SchemaVersion <= 0 ? 1 : snapshot.SchemaVersion;
            snapshot.OccurredAtUtc = snapshot.OccurredAtUtc == default ? DateTimeOffset.UtcNow : snapshot.OccurredAtUtc;
            var session = _cashierSessionContext.CurrentSession;
            var device = _authorizationState.Current;
            snapshot.CashierId ??= session?.CashierId;
            snapshot.UserGuid ??= session?.UserGuid;
            snapshot.CashierName ??= session?.CashierName;
            snapshot.IsOfflineCached |= session?.IsOfflineCached == true;
            snapshot.IsEmergencyOverride |= session?.IsEmergencyOverride == true;
            // 本机已授权设备上下文优先，避免调用方伪造或误传门店、终端标识。
            snapshot.StoreCode = FirstNonEmpty(device?.StoreCode, session?.StoreCode, snapshot.StoreCode);
            snapshot.DeviceCode = FirstNonEmpty(device?.DeviceCode, session?.DeviceCode, snapshot.DeviceCode);
            snapshot.AppVersion ??= _identity.AppVersion;
            snapshot.InstanceId ??= _identity.InstanceId;
            // V1 仅支持澳元，调用方传入的其他币种不能污染本地 outbox 或导致服务端永久拒绝。
            snapshot.CurrencyCode = "AUD";
            snapshot.Properties = snapshot.Properties?
                .Where(entry => AllowedOperationProperties.Contains(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            var queued = new QueuedClientLog(
                snapshot.EventId,
                snapshot.OccurredAtUtc,
                ClientLogSanitizer.Serialize(snapshot));
            var enqueued = false;
            long droppedCountBefore;
            long droppedCountAfter;
            lock (_operationRevisionGate)
            {
                droppedCountBefore = OperationAuditQueueDroppedCount;
                BeginOperationAuditPersistence();
                if (_operationChannel.Writer.TryWrite(queued))
                {
                    _operationAuditRevision++;
                    enqueued = true;
                }
                else
                {
                    HandleOperationAuditQueueDrop();
                }

                droppedCountAfter = OperationAuditQueueDroppedCount;
            }

            if (enqueued)
            {
                TryReleaseSignal();
            }

            if (droppedCountAfter > droppedCountBefore)
            {
                QueueOperationAuditQueueDropDiagnostic(droppedCountAfter);
            }
        }
        catch (Exception ex)
        {
            // 审计记录失败不能反向打断支付、销售或退款主链路。
            WriteInternalDiagnostic($"operation enqueue failed error={ex.GetType().Name}");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _hostShutdownToken = cancellationToken;
        _operationChannel.Writer.TryComplete();
        _runtimeChannel.Writer.TryComplete();
        TryReleaseSignal();
        await base.StopAsync(cancellationToken);

        // 关键：Microsoft.Extensions.Hosting 10 起 BackgroundService 以 Task.Run(ExecuteAsync, stoppingToken) 启动。
        // 启动后立即停止时，停止令牌先于调度被取消，ExecuteAsync 连同 finally 里的最终落库根本不会执行，
        // 通道里已接收的审计与运行日志会全部丢失。这里仍能认领，说明 ExecuteAsync 尚未开始消费，由停止路径代为落库。
        if (TryClaimDrainOwnership())
        {
            try
            {
                await FlushRemainingAsync();
            }
            catch (OperationCanceledException) when (_hostShutdownToken.IsCancellationRequested)
            {
                // 宿主退出预算已耗尽：与 ExecuteAsync 路径一样放弃剩余事件，不把取消异常抛给宿主。
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!TryClaimDrainOwnership())
        {
            // StopAsync 已抢先认领最终落库（启动即停止），这里不能再并发消费同一批通道。
            return;
        }

        try
        {
            await InitializeWithRetryAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(stoppingToken);
                await DrainAvailableAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await FlushRemainingAsync();
        }
    }

    private bool TryClaimDrainOwnership() =>
        Interlocked.CompareExchange(ref _drainOwnershipClaimed, 1, 0) == 0;

    private async Task FlushRemainingAsync()
    {
        try
        {
            // 启动后立即退出时初始化可能尚未完成，最终落库前必须先保证 outbox 表存在。
            await _store.InitializeAsync(_hostShutdownToken);
            await DrainAvailableAsync(_hostShutdownToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteInternalDiagnostic($"final local flush failed error={ex.GetType().Name}");
        }
    }

    private async Task InitializeWithRetryAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await _store.InitializeAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                WriteInternalDiagnostic($"database initialize failed error={ex.GetType().Name}");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task DrainAvailableAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_operationChannel.Reader.TryRead(out var operation))
            {
                await PersistWithRetryAsync(ClientLogOutboxKind.OperationAudit, operation, cancellationToken);
                CompleteOperationAuditPersistence();
                continue;
            }

            if (_runtimeChannel.Reader.TryRead(out var runtime))
            {
                await PersistWithRetryAsync(ClientLogOutboxKind.Runtime, runtime, cancellationToken);
                continue;
            }

            return;
        }
    }

    private async Task PersistWithRetryAsync(
        ClientLogOutboxKind kind,
        QueuedClientLog queued,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await _store.EnqueueAsync(
                    kind,
                    queued.EventId,
                    queued.OccurredAtUtc,
                    queued.PayloadJson,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (
                cancellationToken != _hostShutdownToken &&
                !_hostShutdownToken.IsCancellationRequested)
            {
                // 事件已从内存队列取出时，切换到宿主共享退出预算重试，避免取消窗口造成最后一条丢失。
                cancellationToken = _hostShutdownToken;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                WriteInternalDiagnostic($"database write failed kind={kind} error={ex.GetType().Name}");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static void WriteInternalDiagnostic(string message)
    {
        try
        {
            var line = $"[HBPOS][Client][LogOutbox] {DateTimeOffset.Now:O} {message}";
            try
            {
                Console.WriteLine(line);
            }
            catch
            {
            }

            try
            {
                Debug.WriteLine(line);
            }
            catch
            {
            }

            try
            {
                Trace.WriteLine(line);
            }
            catch
            {
            }
        }
        catch
        {
        }
    }

    private void TryReleaseSignal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void BeginOperationAuditPersistence()
    {
        lock (_operationFlushGate)
        {
            if (_pendingOperationAuditPersistenceCount == 0)
            {
                _operationFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _pendingOperationAuditPersistenceCount++;
        }
    }

    private void CompleteOperationAuditPersistence()
    {
        TaskCompletionSource? flushed = null;
        lock (_operationFlushGate)
        {
            _pendingOperationAuditPersistenceCount--;
            if (_pendingOperationAuditPersistenceCount == 0)
            {
                flushed = _operationFlushed;
            }
        }

        flushed?.TrySetResult();
    }

    private void HandleOperationAuditQueueDrop()
    {
        // itemDropped 在 TryWrite 返回前同步执行；这里只能完成确定性计数和 pending 收尾，不能做诊断 I/O。
        Interlocked.Increment(ref _operationAuditQueueDroppedCount);
        CompleteOperationAuditPersistence();
    }

    private void QueueOperationAuditQueueDropDiagnostic(long droppedCount)
    {
        if ((droppedCount & (droppedCount - 1)) == 0)
        {
            try
            {
                // 诊断监听器可能阻塞或抛错，必须在 revision 锁和 Record 业务路径之外执行。
                _ = ThreadPool.QueueUserWorkItem(
                    static state => WriteInternalDiagnostic(
                        $"operation audit queue dropped count={state.DroppedCount} capacity={state.Capacity}"),
                    (DroppedCount: droppedCount, Capacity: _operationAuditQueueCapacity),
                    preferLocal: false);
            }
            catch
            {
            }
        }
    }

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

}
