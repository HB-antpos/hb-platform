using System.Collections.Concurrent;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 列表批量商品检测：一个批量任务按提交顺序逐张调用单张检测，全局同一时间只执行一个批量任务。
    /// </summary>
    public partial class LocalSupplierInvoiceBatchUpdateJobService
    {
        private const string BatchCheckProductsOperationPrefix = "batch-check-products";

        private readonly ConcurrentDictionary<string, BatchCheckProductsJobState> _batchCheckProductsJobs = new();
        // 关键位置：API 到数据库走公网且受带宽限制，多个批量任务排队串行，避免同时压生产库。
        private readonly SemaphoreSlim _batchCheckProductsGate = new(1, 1);

        public Task<LocalSupplierInvoiceBatchCheckProductsJobDto> StartBatchCheckProductsJobAsync(
            IReadOnlyList<string> invoiceGuids,
            IReadOnlyCollection<string> storeCodes,
            CancellationToken cancellationToken = default
        )
        {
            CleanupExpiredJobs();

            var normalizedGuids = invoiceGuids
                .Where(guid => !string.IsNullOrWhiteSpace(guid))
                .Select(guid => guid.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalizedGuids.Count == 0)
                throw new ArgumentException("请选择要检测的进货单", nameof(invoiceGuids));
            if (normalizedGuids.Count > LocalSupplierInvoiceBatchCheckProductsLimits.MaxInvoices)
                throw new ArgumentException(
                    $"单次最多检测 {LocalSupplierInvoiceBatchCheckProductsLimits.MaxInvoices} 张进货单",
                    nameof(invoiceGuids)
                );

            var operationId = string.Join("|", BatchCheckProductsOperationPrefix, JoinSorted(normalizedGuids));
            BatchCheckProductsJobState jobState;
            lock (_jobStartSyncRoot)
            {
                // 同一组进货单重复提交（双击、刷新后重提）时复用运行中的任务。
                if (_runningOperationJobIds.TryGetValue(operationId, out var existingJobId)
                    && _batchCheckProductsJobs.TryGetValue(existingJobId, out var existing)
                    && IsBatchRunning(existing))
                {
                    return Task.FromResult(CreateBatchCheckProductsSnapshot(existing, true));
                }

                jobState = new BatchCheckProductsJobState
                {
                    JobId = Guid.NewGuid().ToString("N"),
                    OperationId = operationId,
                    StoreCodes = NormalizeStoreCodes(storeCodes),
                    CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    Message = "批量商品检测任务已提交",
                    Items = normalizedGuids
                        .Select(guid => new BatchCheckProductsItemState { InvoiceGuid = guid })
                        .ToList(),
                };
                _batchCheckProductsJobs[jobState.JobId] = jobState;
                _runningOperationJobIds[operationId] = jobState.JobId;
            }

            _ = Task.Run(() => ExecuteBatchCheckProductsJobAsync(jobState), CancellationToken.None);
            return Task.FromResult(CreateBatchCheckProductsSnapshot(jobState, false));
        }

        public Task<LocalSupplierInvoiceBatchCheckProductsJobDto?> GetBatchCheckProductsJobAsync(
            string jobId,
            CancellationToken cancellationToken = default
        )
        {
            CleanupExpiredJobs();
            return Task.FromResult(
                _batchCheckProductsJobs.TryGetValue(jobId, out var jobState)
                    ? CreateBatchCheckProductsSnapshot(jobState, false)
                    : null
            );
        }

        public Task<LocalSupplierInvoiceBatchCheckProductsJobDto?> CancelBatchCheckProductsJobAsync(
            string jobId,
            CancellationToken cancellationToken = default
        )
        {
            if (!_batchCheckProductsJobs.TryGetValue(jobId, out var jobState))
                return Task.FromResult<LocalSupplierInvoiceBatchCheckProductsJobDto?>(null);

            lock (jobState.SyncRoot)
            {
                // 正在检测的那张跑完再停，排队中的单子由执行循环标记为跳过。
                if (jobState.Status == LocalSupplierInvoiceBatchCheckProductsJobStatusConstants.Running)
                    jobState.CancelRequested = true;
            }

            return Task.FromResult<LocalSupplierInvoiceBatchCheckProductsJobDto?>(
                CreateBatchCheckProductsSnapshot(jobState, false)
            );
        }

        private async Task ExecuteBatchCheckProductsJobAsync(BatchCheckProductsJobState jobState)
        {
            var gateAcquired = false;
            try
            {
                await _batchCheckProductsGate.WaitAsync();
                gateAcquired = true;
                lock (jobState.SyncRoot)
                {
                    jobState.StartedAt = _timeProvider.GetUtcNow().UtcDateTime;
                }

                foreach (var item in jobState.Items)
                {
                    bool cancelRequested;
                    lock (jobState.SyncRoot)
                    {
                        cancelRequested = jobState.CancelRequested;
                    }
                    if (cancelRequested)
                    {
                        CompleteBatchItem(jobState, item, LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Skipped, "已停止", 0);
                        continue;
                    }

                    await ExecuteBatchCheckProductsItemAsync(jobState, item);
                }

                CompleteBatchJob(jobState, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行批量商品检测 job 失败: {JobId}", jobState.JobId);
                foreach (var item in jobState.Items)
                {
                    if (!IsBatchItemFinal(jobState, item))
                        CompleteBatchItem(jobState, item, LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Failed, ex.Message, 0);
                }
                CompleteBatchJob(jobState, ex.Message);
            }
            finally
            {
                if (gateAcquired)
                    _batchCheckProductsGate.Release();
            }
        }

        private async Task ExecuteBatchCheckProductsItemAsync(
            BatchCheckProductsJobState jobState,
            BatchCheckProductsItemState item
        )
        {
            var request = new CheckProductsRequest
            {
                InvoiceGuid = item.InvoiceGuid,
                ExcludeExecutedDetails = true,
            };
            var operationId = BuildCheckProductsOperationId(request);
            var familyKey = BuildInvoiceFamilyKey("check-products", item.InvoiceGuid);

            JobState<CheckProductsResponseDto> childJob;
            lock (_jobStartSyncRoot)
            {
                // 关键位置：每张单借用单张检测的 family 锁登记成普通检测任务，
                // 明细页同时点「商品检测」会收到冲突提示，反之明细页已在检测时批量跳过这张单。
                if (TryGetRunningInvoiceFamilyJobId(familyKey, _checkProductsJobs, out _))
                {
                    CompleteBatchItem(jobState, item, LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Skipped, "该单正在检测中", 0);
                    return;
                }

                childJob = CreateJobState<CheckProductsResponseDto>(
                    item.InvoiceGuid,
                    new List<string>(),
                    operationId,
                    familyKey,
                    "批量商品检测中"
                );
                _checkProductsJobs[childJob.JobId] = childJob;
                _runningOperationJobIds[operationId] = childJob.JobId;
                _runningInvoiceFamilyJobIds[familyKey] = childJob.JobId;
            }

            lock (jobState.SyncRoot)
            {
                item.Status = LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Running;
            }

            try
            {
                // 每张单独立 scope、独立事务：一张失败不影响后面的单。
                using var scope = _serviceScopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ILocalSupplierInvoicesReactService>();
                var response = await service.CheckProductsAsync(request);
                var result = response.Data ?? response.Details as CheckProductsResponseDto;
                CompleteJob(
                    childJob,
                    response.Success
                        ? LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded
                        : LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
                    result,
                    response.Message
                );

                if (!response.Success)
                {
                    CompleteBatchItem(jobState, item, LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Failed, response.Message ?? "检测失败", 0);
                    return;
                }

                var checkedCount = result?.Summary.Total ?? 0;
                if (checkedCount == 0)
                {
                    CompleteBatchItem(jobState, item, LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Skipped, "没有需要检测的明细（无明细或已全部执行）", 0);
                    return;
                }

                CompleteBatchItem(jobState, item, LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Succeeded, null, checkedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量商品检测单张失败: {JobId} {InvoiceGuid}", jobState.JobId, item.InvoiceGuid);
                CompleteJob(
                    childJob,
                    LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
                    new CheckProductsResponseDto(),
                    ex.Message
                );
                CompleteBatchItem(jobState, item, LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Failed, ex.Message, 0);
            }
        }

        private void CompleteBatchItem(
            BatchCheckProductsJobState jobState,
            BatchCheckProductsItemState item,
            string status,
            string? message,
            int checkedCount
        )
        {
            lock (jobState.SyncRoot)
            {
                item.Status = status;
                item.Message = message;
                item.CheckedCount = checkedCount;
                item.CompletedAt = _timeProvider.GetUtcNow().UtcDateTime;
            }
        }

        private static bool IsBatchItemFinal(BatchCheckProductsJobState jobState, BatchCheckProductsItemState item)
        {
            lock (jobState.SyncRoot)
            {
                return item.CompletedAt.HasValue;
            }
        }

        private void CompleteBatchJob(BatchCheckProductsJobState jobState, string? errorMessage)
        {
            lock (_jobStartSyncRoot)
            {
                lock (jobState.SyncRoot)
                {
                    var completedAt = _timeProvider.GetUtcNow().UtcDateTime;
                    jobState.Status = jobState.CancelRequested
                        ? LocalSupplierInvoiceBatchCheckProductsJobStatusConstants.Cancelled
                        : LocalSupplierInvoiceBatchCheckProductsJobStatusConstants.Completed;
                    jobState.CompletedAt = completedAt;
                    jobState.ExpiresAt = completedAt.Add(_completedRetention);
                    jobState.Message = errorMessage ?? (jobState.CancelRequested ? "批量商品检测已停止" : "批量商品检测已完成");
                }

                _runningOperationJobIds.TryRemove(jobState.OperationId, out _);
            }
        }

        private static bool IsBatchRunning(BatchCheckProductsJobState jobState)
        {
            lock (jobState.SyncRoot)
            {
                return jobState.Status == LocalSupplierInvoiceBatchCheckProductsJobStatusConstants.Running;
            }
        }

        private void CleanupExpiredBatchCheckProductsJobs(DateTime now)
        {
            foreach (var pair in _batchCheckProductsJobs)
            {
                var state = pair.Value;
                bool expired;
                lock (state.SyncRoot)
                {
                    expired = state.ExpiresAt.HasValue && state.ExpiresAt.Value <= now;
                }
                if (expired)
                    _batchCheckProductsJobs.TryRemove(pair.Key, out _);
            }
        }

        private static LocalSupplierInvoiceBatchCheckProductsJobDto CreateBatchCheckProductsSnapshot(
            BatchCheckProductsJobState jobState,
            bool isDuplicateRequest
        )
        {
            lock (jobState.SyncRoot)
            {
                var items = jobState.Items
                    .Select(item => new LocalSupplierInvoiceBatchCheckProductsItemDto
                    {
                        InvoiceGuid = item.InvoiceGuid,
                        Status = item.Status,
                        Message = item.Message,
                        CheckedCount = item.CheckedCount,
                        CompletedAt = item.CompletedAt,
                    })
                    .ToList();
                return new LocalSupplierInvoiceBatchCheckProductsJobDto
                {
                    JobId = jobState.JobId,
                    OperationId = jobState.OperationId,
                    Status = jobState.Status,
                    IsDuplicateRequest = isDuplicateRequest,
                    IsWaiting = jobState.Status == LocalSupplierInvoiceBatchCheckProductsJobStatusConstants.Running
                        && !jobState.StartedAt.HasValue,
                    CancelRequested = jobState.CancelRequested,
                    StoreCodes = jobState.StoreCodes.ToList(),
                    CreatedAt = jobState.CreatedAt,
                    StartedAt = jobState.StartedAt,
                    CompletedAt = jobState.CompletedAt,
                    ExpiresAt = jobState.ExpiresAt,
                    Message = jobState.Message,
                    Total = items.Count,
                    Processed = items.Count(item => item.CompletedAt.HasValue),
                    Succeeded = items.Count(item => item.Status == LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Succeeded),
                    Failed = items.Count(item => item.Status == LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Failed),
                    Skipped = items.Count(item => item.Status == LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Skipped),
                    Items = items,
                };
            }
        }

        private sealed class BatchCheckProductsJobState
        {
            public object SyncRoot { get; } = new();
            public string JobId { get; init; } = string.Empty;
            public string OperationId { get; init; } = string.Empty;
            public List<string> StoreCodes { get; init; } = new();
            public string Status { get; set; } = LocalSupplierInvoiceBatchCheckProductsJobStatusConstants.Running;
            public bool CancelRequested { get; set; }
            public DateTime CreatedAt { get; init; }
            public DateTime? StartedAt { get; set; }
            public DateTime? CompletedAt { get; set; }
            public DateTime? ExpiresAt { get; set; }
            public string? Message { get; set; }
            public List<BatchCheckProductsItemState> Items { get; init; } = new();
        }

        private sealed class BatchCheckProductsItemState
        {
            public string InvoiceGuid { get; init; } = string.Empty;
            public string Status { get; set; } = LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Queued;
            public string? Message { get; set; }
            public int CheckedCount { get; set; }
            public DateTime? CompletedAt { get; set; }
        }
    }
}
