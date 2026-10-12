using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Hbpos.Api.Services;

public interface ICatalogBackgroundRefreshScheduler
{
    void QueueRefresh(string storeCode);

    /// <summary>返回与同门店共享的构建结果，供批处理等待发布完成。</summary>
    Task QueueRefreshAsync(string storeCode)
    {
        QueueRefresh(storeCode);
        return Task.CompletedTask;
    }
}

public interface ICatalogIndexRefreshWorker
{
    Task RefreshCatalogIndexAsync(
        string storeCode,
        CancellationToken cancellationToken);
}

/// <summary>
/// 目录后台刷新队列。每个门店的排队、构建和重试始终合并为同一项工作；
/// 构建最多并发一项，重试等待不占用构建槽位。
/// </summary>
public sealed class CatalogBackgroundRefreshService : BackgroundService, ICatalogBackgroundRefreshScheduler
{
    private const int MaxConcurrentBuilds = 1;
    // 构建期间被失效后立即重建的连续次数上限（不计入失败重试次数）。
    private const int MaxInvalidatedRebuilds = 3;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromMinutes(60)
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CatalogBackgroundRefreshService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Channel<CatalogRefreshRequest> _queue =
        Channel.CreateUnbounded<CatalogRefreshRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly ConcurrentDictionary<CatalogRefreshKey, CatalogRefreshRequest> _scheduled = new();
    private readonly ConcurrentDictionary<int, Task> _outstandingWork = new();
    private readonly SemaphoreSlim _buildSlots = new(MaxConcurrentBuilds, MaxConcurrentBuilds);
    private int _workSequence;

    public CatalogBackgroundRefreshService(
        IServiceScopeFactory scopeFactory,
        ILogger<CatalogBackgroundRefreshService> logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public void QueueRefresh(string storeCode)
    {
        _ = QueueRefreshAsync(storeCode);
    }

    public Task QueueRefreshAsync(string storeCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeCode);
        var key = new CatalogRefreshKey(storeCode.Trim().ToUpperInvariant());

        while (true)
        {
            if (_scheduled.TryGetValue(key, out var existing))
            {
                return existing.Completion.Task;
            }

            var request = new CatalogRefreshRequest(key);
            if (!_scheduled.TryAdd(key, request))
            {
                continue;
            }

            if (_queue.Writer.TryWrite(request))
            {
                _logger.LogInformation(
                    "Catalog background refresh queued store={StoreCode} attempt={Attempt}",
                    key.StoreCode,
                    request.Attempt);
                return request.Completion.Task;
            }

            RemoveScheduledRequest(request);
            var exception = new InvalidOperationException("Catalog refresh queue is not accepting work.");
            request.Completion.TrySetException(exception);
            _logger.LogWarning(
                exception,
                "Catalog refresh queue rejected store={StoreCode}",
                key.StoreCode);
            return request.Completion.Task;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await _buildSlots.WaitAsync(stoppingToken);
                Track(ProcessRefreshAsync(request, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停止时不再取新工作；已开始的构建和重试会收到同一个取消信号。
        }
        finally
        {
            _queue.Writer.TryComplete();
            await WaitForOutstandingWorkAsync();
            CancelUnfinishedRequests();
        }
    }

    private async Task ProcessRefreshAsync(
        CatalogRefreshRequest request,
        CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var worker = scope.ServiceProvider.GetRequiredService<ICatalogIndexRefreshWorker>();
            await worker.RefreshCatalogIndexAsync(request.Key.StoreCode, stoppingToken);

            RemoveScheduledRequest(request);
            request.Completion.TrySetResult();
            _logger.LogInformation(
                "Catalog background refresh completed store={StoreCode} attempt={Attempt}",
                request.Key.StoreCode,
                request.Attempt);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            RemoveScheduledRequest(request);
            request.Completion.TrySetCanceled(stoppingToken);
            _logger.LogInformation(
                "Catalog background refresh cancelled store={StoreCode} attempt={Attempt}",
                request.Key.StoreCode,
                request.Attempt);
        }
        catch (Exception exception)
        {
            // 构建期间门店被失效不是构建故障：整店数据已拉完，只是结果因数据又变化而作废。
            // 立即重新排队重建，不走 15/30/60 分钟的失败退避（退避期内同店刷新都会合并到待重试请求上），
            // 也不计入失败重试次数；连续失效超过上限才落入下面的失败路径，防止持续失效造成活锁。
            if (CatalogIndexCache.IsBuildInvalidated(exception) &&
                request.InvalidatedRebuilds < MaxInvalidatedRebuilds &&
                TryRequeueAfterInvalidation(request))
            {
                _logger.LogInformation(
                    exception,
                    "Catalog background refresh invalidated during build; requeued immediately store={StoreCode} rebuild={Rebuild}/{MaxRebuilds}",
                    request.Key.StoreCode,
                    request.InvalidatedRebuilds,
                    MaxInvalidatedRebuilds);
                return;
            }

            var retryDelay = GetRetryDelay(request.Attempt);
            if (retryDelay is not null)
            {
                _logger.LogWarning(
                    exception,
                    "Catalog background refresh failed; retry scheduled store={StoreCode} attempt={Attempt} retryDelayMinutes={RetryDelayMinutes}",
                    request.Key.StoreCode,
                    request.Attempt,
                    retryDelay.Value.TotalMinutes);
                Track(DelayAndRequeueAsync(request, retryDelay.Value, stoppingToken));
            }
            else
            {
                RemoveScheduledRequest(request);
                request.Completion.TrySetException(exception);
                _logger.LogError(
                    exception,
                    "Catalog background refresh failed permanently store={StoreCode} attempts={AttemptCount}",
                    request.Key.StoreCode,
                    request.Attempt);
            }
        }
        finally
        {
            _buildSlots.Release();
        }
    }

    private bool TryRequeueAfterInvalidation(CatalogRefreshRequest request)
    {
        // 请求仍保留在 _scheduled 中，新到的同店刷新继续合并到它身上；只是不经过延迟直接回到队列。
        request.IncrementInvalidatedRebuilds();
        return _queue.Writer.TryWrite(request);
    }

    private async Task DelayAndRequeueAsync(
        CatalogRefreshRequest request,
        TimeSpan retryDelay,
        CancellationToken stoppingToken)
    {
        try
        {
            await _delayAsync(retryDelay, stoppingToken);
            request.IncrementAttempt();
            if (_queue.Writer.TryWrite(request))
            {
                _logger.LogInformation(
                    "Catalog background refresh retry queued store={StoreCode} attempt={Attempt}",
                    request.Key.StoreCode,
                    request.Attempt);
                return;
            }

            throw new InvalidOperationException("Catalog refresh queue is not accepting retries.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            RemoveScheduledRequest(request);
            request.Completion.TrySetCanceled(stoppingToken);
        }
        catch (Exception exception)
        {
            RemoveScheduledRequest(request);
            request.Completion.TrySetException(exception);
            _logger.LogError(
                exception,
                "Catalog background refresh retry could not be queued store={StoreCode} attempt={Attempt}",
                request.Key.StoreCode,
                request.Attempt);
        }
    }

    private void Track(Task task)
    {
        var workId = Interlocked.Increment(ref _workSequence);
        _outstandingWork.TryAdd(workId, task);
        _ = ObserveTrackedWorkAsync(workId, task);
    }

    private async Task ObserveTrackedWorkAsync(int workId, Task task)
    {
        try
        {
            await task;
        }
        finally
        {
            _outstandingWork.TryRemove(workId, out _);
        }
    }

    private async Task WaitForOutstandingWorkAsync()
    {
        while (!_outstandingWork.IsEmpty)
        {
            var outstanding = _outstandingWork.Values.ToArray();
            if (outstanding.Length == 0)
            {
                break;
            }

            await Task.WhenAll(outstanding);
        }
    }

    private void CancelUnfinishedRequests()
    {
        foreach (var pair in _scheduled)
        {
            if (RemoveScheduledRequest(pair.Value))
            {
                pair.Value.Completion.TrySetCanceled();
            }
        }
    }

    private bool RemoveScheduledRequest(CatalogRefreshRequest request)
    {
        // 终态先按键和值移除自身，completion 的 continuation 才能立即创建下一次刷新；
        // 值匹配也避免旧请求误删同门店后来入队的新请求。
        return _scheduled.TryRemove(
            new KeyValuePair<CatalogRefreshKey, CatalogRefreshRequest>(request.Key, request));
    }

    private static TimeSpan? GetRetryDelay(int attempt)
    {
        return attempt <= RetryDelays.Length
            ? RetryDelays[attempt - 1]
            : null;
    }

    private sealed record CatalogRefreshKey(string StoreCode);

    private sealed class CatalogRefreshRequest(CatalogRefreshKey key)
    {
        public CatalogRefreshKey Key { get; } = key;

        public int Attempt { get; private set; } = 1;

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InvalidatedRebuilds { get; private set; }

        public void IncrementAttempt()
        {
            Attempt++;
        }

        public void IncrementInvalidatedRebuilds()
        {
            InvalidatedRebuilds++;
        }
    }
}
