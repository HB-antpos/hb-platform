using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 仓库商品批量修改后台任务服务。
/// </summary>
public sealed class WarehouseProductBatchUpdateJobService
    : IWarehouseProductBatchUpdateJobService
{
    private static readonly TimeSpan DefaultCompletedRetention = TimeSpan.FromMinutes(45);
    private const int DefaultMaxActiveJobs = 20;
    private const int MaxItemsPerJob = 2000;

    // 与仓库现有后台任务一致，任务状态保存在当前 API 进程；服务重启后不自动重放写操作。
    private readonly ConcurrentDictionary<string, JobState> _jobs = new();
    private readonly ConcurrentDictionary<string, string> _runningOperationJobIds = new();
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<WarehouseProductBatchUpdateJobService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _completedRetention;
    private readonly int _maxActiveJobs;
    private readonly object _jobStartSyncRoot = new();
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    public WarehouseProductBatchUpdateJobService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<WarehouseProductBatchUpdateJobService> logger
    )
        : this(
            serviceScopeFactory,
            logger,
            TimeProvider.System,
            DefaultCompletedRetention,
            DefaultMaxActiveJobs
        ) { }

    public WarehouseProductBatchUpdateJobService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<WarehouseProductBatchUpdateJobService> logger,
        TimeProvider? timeProvider,
        TimeSpan? completedRetention,
        int maxActiveJobs = DefaultMaxActiveJobs
    )
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _completedRetention = completedRetention ?? DefaultCompletedRetention;
        _maxActiveJobs = maxActiveJobs > 0
            ? maxActiveJobs
            : throw new ArgumentOutOfRangeException(nameof(maxActiveJobs));
    }

    public Task<WarehouseProductBatchUpdateJobDto> StartJobAsync(
        WarehouseProductBatchUpdateJobRequestDto request,
        string? updatedBy,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        CleanupExpiredJobs();

        var normalizedRequest = NormalizeRequest(request);
        var operationId = BuildOperationId(normalizedRequest);
        lock (_jobStartSyncRoot)
        {
            var duplicate = GetRunningSnapshotNoLock(operationId);
            if (duplicate != null)
            {
                duplicate.IsDuplicateRequest = true;
                return Task.FromResult(duplicate);
            }
            if (_runningOperationJobIds.Count >= _maxActiveJobs)
            {
                throw new WarehouseProductBatchUpdateQueueFullException();
            }

            var state = new JobState
            {
                JobId = Guid.NewGuid().ToString("N"),
                OperationId = operationId,
                Request = CloneRequest(normalizedRequest),
                UpdatedBy = NormalizeUpdatedBy(updatedBy),
                CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                Message = "仓库商品批量修改任务已提交",
            };
            _jobs[state.JobId] = state;
            _runningOperationJobIds[operationId] = state.JobId;

            // 后台重新创建 scope，不能继续使用控制器请求生命周期内的数据库上下文。
            _ = Task.Run(() => ExecuteJobAsync(state), CancellationToken.None);
            return Task.FromResult(CreateSnapshot(state, false));
        }
    }

    public Task<WarehouseProductBatchUpdateJobDto?> GetJobAsync(
        string jobId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        CleanupExpiredJobs();
        if (string.IsNullOrWhiteSpace(jobId) || !_jobs.TryGetValue(jobId.Trim(), out var state))
        {
            return Task.FromResult<WarehouseProductBatchUpdateJobDto?>(null);
        }

        return Task.FromResult<WarehouseProductBatchUpdateJobDto?>(CreateSnapshot(state, false));
    }

    private async Task ExecuteJobAsync(JobState state)
    {
        // 不同请求也串行执行，避免重叠商品的价格、状态或图片出现最后写入者竞争。
        await _executionLock.WaitAsync();
        try
        {
            MarkRunning(state);
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var localService = scope.ServiceProvider
                    .GetRequiredService<IProductWarehouseReactService>();
                var hqService = scope.ServiceProvider.GetRequiredService<IProductHqSyncService>();
                var result = await ExecuteUpdateAsync(
                    localService,
                    hqService,
                    CloneRequest(state.Request),
                    state.UpdatedBy
                );
                // 任务在自己的 DI 作用域内执行，通知汇总也在这个作用域里累积；用 GetService 兼容未注册的测试容器。
                state.PriceNotification = scope.ServiceProvider
                    .GetService<IPriceNotificationSummaryAccessor>()
                    ?.GetSummary();
                var status = ResolveStatus(result);
                CompleteJob(state, status, result, ResolveMessage(status, result));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "仓库商品批量修改后台任务失败: {JobId}", state.JobId);
                CompleteJob(
                    state,
                    WarehouseProductBatchUpdateJobStatusConstants.Failed,
                    new WarehouseProductBatchUpdateResultDto
                    {
                        Success = false,
                        Message = "后台任务执行失败",
                        FailedCount = state.Request.Items.Count,
                        Errors = ["后台任务执行失败"],
                    },
                    "仓库商品批量修改任务执行失败"
                );
            }
        }
        finally
        {
            _executionLock.Release();
        }
    }

    private static async Task<WarehouseProductBatchUpdateResultDto> ExecuteUpdateAsync(
        IProductWarehouseReactService localService,
        IProductHqSyncService hqService,
        WarehouseProductBatchUpdateJobRequestDto request,
        string updatedBy
    )
    {
        var options = new WarehouseProductBatchUpdateOptionsDto
        {
            GenerateImageUrls = request.GenerateImageUrls,
            ImageBaseUrl = request.ImageBaseUrl,
            SyncImageToHq = request.SyncImageToHq,
            SupplyNotice = request.SupplyNotice,
        };
        WarehouseProductBatchUpdateResultDto result;
        // 带供货说明时必须走带选项的重载，旧重载不会把说明传到写入器。
        if (options.GenerateImageUrls || options.SupplyNotice != null)
        {
            result = await localService.BatchUpdateAsync(request.Items, updatedBy, options);
        }
        else
        {
            var legacyResult = await localService.BatchUpdateAsync(request.Items, updatedBy);
            result = CopyLegacyResult(legacyResult);
        }

        // 本地事务整体失败时补全失败范围，便于后台结果明确提示并保留整批选择。
        if (!result.Success && result.FailedCount == 0)
        {
            result.FailedCount = request.Items.Count;
        }

        if (!options.SyncImageToHq)
        {
            return result;
        }

        if (!result.Success)
        {
            result.HqImageSync = new ProductHqImageSyncResultDto
            {
                Requested = true,
                Success = false,
                ErrorCode = "HQ_IMAGE_SYNC_LOCAL_UPDATE_FAILED",
                Errors = ["本地图片更新失败，未执行 HQ 图片同步"],
            };
        }
        else if (result.ImageUpdates.Count == 0)
        {
            result.HqImageSync = new ProductHqImageSyncResultDto
            {
                Requested = true,
                Success = false,
                ErrorCode = "HQ_IMAGE_SYNC_NO_LOCAL_IMAGES",
                Errors = ["没有本地成功更新的图片可同步至 HQ"],
            };
        }
        else
        {
            result.HqImageSync = await hqService.SyncProductImagesAsync(
                result.ImageUpdates,
                updatedBy,
                CancellationToken.None
            );
            if (!result.HqImageSync.Success)
            {
                result.Message = "本地更新完成，HQ 图片同步存在失败";
            }
        }

        return result;
    }

    private static WarehouseProductBatchUpdateResultDto CopyLegacyResult(
        BatchOperationResultDto result
    )
    {
        return new WarehouseProductBatchUpdateResultDto
        {
            Success = result.Success,
            Message = result.Message,
            SuccessCount = result.SuccessCount,
            FailedCount = result.FailedCount,
            SkippedCount = result.SkippedCount,
            Errors = result.Errors.ToList(),
            SkippedItems = result.SkippedItems.ToList(),
        };
    }

    private static string ResolveStatus(WarehouseProductBatchUpdateResultDto result)
    {
        if (!result.Success || (result.SuccessCount == 0 && result.FailedCount > 0))
        {
            return WarehouseProductBatchUpdateJobStatusConstants.Failed;
        }

        if (
            result.FailedCount > 0
            || result.SkippedCount > 0
            || (result.HqImageSync.Requested && !result.HqImageSync.Success)
        )
        {
            return WarehouseProductBatchUpdateJobStatusConstants.PartiallySucceeded;
        }

        return WarehouseProductBatchUpdateJobStatusConstants.Succeeded;
    }

    private static string ResolveMessage(
        string status,
        WarehouseProductBatchUpdateResultDto result
    )
    {
        if (status == WarehouseProductBatchUpdateJobStatusConstants.Succeeded)
        {
            return string.IsNullOrWhiteSpace(result.Message)
                ? "仓库商品批量修改完成"
                : result.Message;
        }
        if (status == WarehouseProductBatchUpdateJobStatusConstants.PartiallySucceeded)
        {
            return result.HqImageSync.Requested && !result.HqImageSync.Success
                ? "本地修改已提交，部分商品或 HQ 图片同步失败"
                : "仓库商品批量修改部分完成";
        }

        return string.IsNullOrWhiteSpace(result.Message) || result.Message == "更新完成"
            ? "仓库商品批量修改失败"
            : result.Message;
    }

    private void MarkRunning(JobState state)
    {
        lock (state.SyncRoot)
        {
            state.Status = WarehouseProductBatchUpdateJobStatusConstants.Running;
            state.StartedAt = _timeProvider.GetUtcNow().UtcDateTime;
            state.Message = "仓库商品批量修改处理中";
        }
    }

    private void CompleteJob(
        JobState state,
        string status,
        WarehouseProductBatchUpdateResultDto result,
        string message
    )
    {
        lock (_jobStartSyncRoot)
        {
            lock (state.SyncRoot)
            {
                var completedAt = _timeProvider.GetUtcNow().UtcDateTime;
                state.Status = status;
                state.Result = CloneResult(result);
                state.Message = message;
                state.CompletedAt = completedAt;
                state.ExpiresAt = completedAt.Add(_completedRetention);
            }

            if (
                _runningOperationJobIds.TryGetValue(state.OperationId, out var runningJobId)
                && string.Equals(runningJobId, state.JobId, StringComparison.OrdinalIgnoreCase)
            )
            {
                _runningOperationJobIds.TryRemove(state.OperationId, out _);
            }
        }
    }

    private WarehouseProductBatchUpdateJobDto? GetRunningSnapshotNoLock(string operationId)
    {
        if (!_runningOperationJobIds.TryGetValue(operationId, out var jobId))
        {
            return null;
        }
        if (!_jobs.TryGetValue(jobId, out var state))
        {
            _runningOperationJobIds.TryRemove(operationId, out _);
            return null;
        }

        var snapshot = CreateSnapshot(state, true);
        if (
            snapshot.Status != WarehouseProductBatchUpdateJobStatusConstants.Queued
            && snapshot.Status != WarehouseProductBatchUpdateJobStatusConstants.Running
        )
        {
            _runningOperationJobIds.TryRemove(operationId, out _);
            return null;
        }

        return snapshot;
    }

    private void CleanupExpiredJobs()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var pair in _jobs)
        {
            if (pair.Value.ExpiresAt is { } expiresAt && expiresAt <= now)
            {
                _jobs.TryRemove(pair.Key, out _);
            }
        }
    }

    private static WarehouseProductBatchUpdateJobDto CreateSnapshot(
        JobState state,
        bool isDuplicateRequest
    )
    {
        lock (state.SyncRoot)
        {
            return new WarehouseProductBatchUpdateJobDto
            {
                JobId = state.JobId,
                OperationId = state.OperationId,
                Status = state.Status,
                IsDuplicateRequest = isDuplicateRequest,
                CreatedAt = state.CreatedAt,
                StartedAt = state.StartedAt,
                CompletedAt = state.CompletedAt,
                ExpiresAt = state.ExpiresAt,
                Message = state.Message,
                Result = CloneResult(state.Result),
                PriceNotification = state.PriceNotification,
            };
        }
    }

    private static WarehouseProductBatchUpdateJobRequestDto NormalizeRequest(
        WarehouseProductBatchUpdateJobRequestDto? request
    )
    {
        if (request?.Items == null || request.Items.Count == 0)
        {
            throw new ArgumentException("请求数据不能为空");
        }
        if (request.Items.Count > MaxItemsPerJob)
        {
            throw new ArgumentException($"单个后台任务最多允许 {MaxItemsPerJob} 个商品");
        }
        if (request.SyncImageToHq && !request.GenerateImageUrls)
        {
            throw new ArgumentException("同步 HQ 图片前必须启用图片地址生成");
        }

        var normalized = CloneRequest(request);
        foreach (var item in normalized.Items)
        {
            // 去重哈希与实际执行使用同一份 trim 后请求，避免任务复用语义与落库语义分叉。
            item.ProductCode = NormalizeRequestKey(item.ProductCode);
            item.ItemNumber = NormalizeRequestKey(item.ItemNumber);
            item.SupplierCode = NormalizeRequestKey(item.SupplierCode);
        }
        if (normalized.SyncStorePurchasePrice.HasValue)
        {
            foreach (var item in normalized.Items)
            {
                item.SyncStorePurchasePrice ??= normalized.SyncStorePurchasePrice;
            }
        }
        if (normalized.GenerateImageUrls)
        {
            if (
                !WarehouseProductBatchImageUrlBuilder.TryNormalizeBaseUrl(
                    normalized.ImageBaseUrl,
                    out var normalizedBaseUrl,
                    out var error
                )
            )
            {
                throw new ArgumentException(error);
            }
            normalized.ImageBaseUrl = normalizedBaseUrl;
        }
        else
        {
            normalized.ImageBaseUrl = null;
        }

        return normalized;
    }

    private static string BuildOperationId(WarehouseProductBatchUpdateJobRequestDto request)
    {
        var itemParts = request.Items
            .Select(BuildItemOperationPart)
            .ToList();
        var canonical = string.Join('\u001E', itemParts)
            + $"|store={FormatNullable(request.SyncStorePurchasePrice)}"
            + $"|image={request.GenerateImageUrls}"
            + $"|base={request.ImageBaseUrl}"
            + $"|hq={request.SyncImageToHq}"
            + $"|notice={BuildSupplyNoticeOperationPart(request.SupplyNotice)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"warehouse-product-batch-update:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>
    /// 供货说明纳入去重指纹：仅说明不同的两次提交不能被合并成同一个任务，否则后一次的说明会被静默丢弃。
    /// </summary>
    private static string BuildSupplyNoticeOperationPart(
        WarehouseProductSupplyNoticeInputDto? notice
    )
    {
        if (notice == null)
        {
            return "~";
        }

        return string.Join(
            '\u001F',
            NormalizeKey(notice.SupplyPlan),
            NormalizeKey(notice.ExpectedPrecision),
            notice.ExpectedFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "~",
            notice.ExpectedTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "~",
            NormalizeKey(notice.StoreFacingNote),
            NormalizeKey(notice.InternalNote)
        );
    }

    private static string BuildItemOperationPart(UpdateItemDto item)
    {
        return string.Join(
            '\u001F',
            NormalizeKey(item.ProductCode),
            NormalizeKey(item.ItemNumber),
            NormalizeKey(item.SupplierCode),
            FormatNullable(item.DomesticPrice),
            FormatNullable(item.OEMPrice),
            FormatNullable(item.ImportPrice),
            FormatNullable(item.Volume),
            FormatNullable(item.PackingQuantity),
            FormatNullable(item.MinOrderQuantity),
            FormatNullable(item.IsActive),
            FormatNullable(item.SyncStorePurchasePrice)
        );
    }

    private static string NormalizeKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "~" : value.Trim();
    }

    private static string? NormalizeRequestKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string FormatNullable<T>(T? value)
        where T : struct, IFormattable
    {
        return value.HasValue
            ? value.Value.ToString(null, CultureInfo.InvariantCulture)
            : "~";
    }

    private static string FormatNullable(bool? value)
    {
        return value.HasValue ? value.Value ? "1" : "0" : "~";
    }

    private static string NormalizeUpdatedBy(string? updatedBy)
    {
        return string.IsNullOrWhiteSpace(updatedBy) ? "system" : updatedBy.Trim();
    }

    private static WarehouseProductBatchUpdateJobRequestDto CloneRequest(
        WarehouseProductBatchUpdateJobRequestDto request
    )
    {
        return new WarehouseProductBatchUpdateJobRequestDto
        {
            Items = request.Items.Select(CloneItem).ToList(),
            SyncStorePurchasePrice = request.SyncStorePurchasePrice,
            GenerateImageUrls = request.GenerateImageUrls,
            ImageBaseUrl = request.ImageBaseUrl,
            SyncImageToHq = request.SyncImageToHq,
            SupplyNotice = CloneSupplyNotice(request.SupplyNotice),
        };
    }

    private static WarehouseProductSupplyNoticeInputDto? CloneSupplyNotice(
        WarehouseProductSupplyNoticeInputDto? notice
    )
    {
        // 任务快照与调用方请求不共享可变对象，避免排队期间被外部改写。
        return notice == null
            ? null
            : new WarehouseProductSupplyNoticeInputDto
            {
                SupplyPlan = notice.SupplyPlan,
                ExpectedFrom = notice.ExpectedFrom,
                ExpectedTo = notice.ExpectedTo,
                ExpectedPrecision = notice.ExpectedPrecision,
                StoreFacingNote = notice.StoreFacingNote,
                InternalNote = notice.InternalNote,
            };
    }

    private static UpdateItemDto CloneItem(UpdateItemDto item)
    {
        return new UpdateItemDto
        {
            ProductCode = item.ProductCode,
            ItemNumber = item.ItemNumber,
            SupplierCode = item.SupplierCode,
            DomesticPrice = item.DomesticPrice,
            OEMPrice = item.OEMPrice,
            ImportPrice = item.ImportPrice,
            Volume = item.Volume,
            PackingQuantity = item.PackingQuantity,
            MinOrderQuantity = item.MinOrderQuantity,
            IsActive = item.IsActive,
            SyncStorePurchasePrice = item.SyncStorePurchasePrice,
        };
    }

    private static WarehouseProductBatchUpdateResultDto? CloneResult(
        WarehouseProductBatchUpdateResultDto? result
    )
    {
        if (result == null)
        {
            return null;
        }

        return new WarehouseProductBatchUpdateResultDto
        {
            Success = result.Success,
            Message = result.Message,
            SuccessCount = result.SuccessCount,
            FailedCount = result.FailedCount,
            SkippedCount = result.SkippedCount,
            Errors = result.Errors.ToList(),
            SkippedItems = result.SkippedItems.ToList(),
            ImageUpdatedCount = result.ImageUpdatedCount,
            HqImageSync = new ProductHqImageSyncResultDto
            {
                Requested = result.HqImageSync.Requested,
                Success = result.HqImageSync.Success,
                UpdatedCount = result.HqImageSync.UpdatedCount,
                FailedCount = result.HqImageSync.FailedCount,
                ErrorCode = result.HqImageSync.ErrorCode,
                Errors = result.HqImageSync.Errors.ToList(),
            },
        };
    }

    private sealed class JobState
    {
        public object SyncRoot { get; } = new();
        public string JobId { get; init; } = string.Empty;
        public string OperationId { get; init; } = string.Empty;
        public WarehouseProductBatchUpdateJobRequestDto Request { get; init; } = new();
        public string UpdatedBy { get; init; } = "system";
        public string Status { get; set; } = WarehouseProductBatchUpdateJobStatusConstants.Queued;
        public DateTime CreatedAt { get; init; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string? Message { get; set; }
        public WarehouseProductBatchUpdateResultDto? Result { get; set; }
        public PriceNotificationSummaryDto? PriceNotification { get; set; }
    }
}
