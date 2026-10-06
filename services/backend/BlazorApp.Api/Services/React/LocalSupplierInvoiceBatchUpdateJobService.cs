using System.Collections.Concurrent;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.React
{
    public class LocalSupplierInvoiceBatchUpdateJobConflictException : InvalidOperationException
    {
        public LocalSupplierInvoiceBatchUpdateJobConflictException(
            string message,
            string existingJobId,
            string operationId
        )
            : base(message)
        {
            ExistingJobId = existingJobId;
            OperationId = operationId;
        }

        public string ExistingJobId { get; }
        public string OperationId { get; }
    }

    /// <summary>
    /// 本地进货单批量更新后台任务服务。
    /// </summary>
    public partial class LocalSupplierInvoiceBatchUpdateJobService : ILocalSupplierInvoiceBatchUpdateJobService
    {
        private static readonly TimeSpan DefaultCompletedRetention = TimeSpan.FromMinutes(45);
        private static readonly TimeSpan HqLockRetryBudget = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan HqLockRetryDelay = TimeSpan.FromMilliseconds(250);
        private const int MaxHqLockWaitMilliseconds = 10_000;
        private const string HqUpdateCostLockBusyCode = "HQ_UPDATE_COST_LOCK_BUSY";
        private const string HqLockRetryTimeoutMessage = "商品更新繁忙，等待其他成本操作超时，本次未更新 HQ 商品";
        private static readonly TimeSpan StorePriceLockRetryBudget = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan StorePriceLockRetryDelay = TimeSpan.FromMilliseconds(250);
        private const int MaxStorePriceLockWaitMilliseconds = 10_000;
        private const string StorePriceUpdateCostLockBusyCode = "STORE_UPDATE_COST_LOCK_BUSY";
        private const string StorePriceLockRetryTimeoutMessage = "分店价格更新繁忙，等待其他成本操作超时，本次未更新分店价格";

        private readonly ConcurrentDictionary<string, JobState<UpdateToStorePricesResultDto>> _storePriceJobs = new();
        private readonly ConcurrentDictionary<string, JobState<UpdateHqProductsResult>> _hqProductJobs = new();
        private readonly ConcurrentDictionary<string, JobState<BatchResultDto>> _pasteDetailsJobs = new();
        private readonly ConcurrentDictionary<string, JobState<CheckProductsResponseDto>> _checkProductsJobs = new();
        private readonly ConcurrentDictionary<string, string> _runningOperationJobIds = new();
        private readonly ConcurrentDictionary<string, string> _runningInvoiceFamilyJobIds = new();
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<LocalSupplierInvoiceBatchUpdateJobService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _completedRetention;
        private readonly object _jobStartSyncRoot = new();

        public LocalSupplierInvoiceBatchUpdateJobService(
            IServiceScopeFactory serviceScopeFactory,
            ILogger<LocalSupplierInvoiceBatchUpdateJobService> logger
        )
            : this(serviceScopeFactory, logger, TimeProvider.System, DefaultCompletedRetention) { }

        public LocalSupplierInvoiceBatchUpdateJobService(
            IServiceScopeFactory serviceScopeFactory,
            ILogger<LocalSupplierInvoiceBatchUpdateJobService> logger,
            TimeProvider? timeProvider = null,
            TimeSpan? completedRetention = null
        )
        {
            _serviceScopeFactory = serviceScopeFactory;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _completedRetention = completedRetention ?? DefaultCompletedRetention;
        }

        public Task<LocalSupplierInvoiceUpdateToStorePricesJobDto> StartUpdateToStorePricesJobAsync(
            UpdateToStorePricesRequest request,
            string updatedBy,
            CancellationToken cancellationToken = default
        )
        {
            CleanupExpiredJobs();

            var operationId = BuildUpdateToStoreOperationId(request);
            var duplicate = GetRunningStorePriceJob(operationId);
            if (duplicate != null)
            {
                duplicate.IsDuplicateRequest = true;
                return Task.FromResult(duplicate);
            }

            var familyKey = BuildInvoiceFamilyKey("update-to-store", request.InvoiceGuid);
            var jobState = CreateJobState<UpdateToStorePricesResultDto>(
                request.InvoiceGuid,
                NormalizeStoreCodes(request.TargetStoreCodes),
                operationId,
                familyKey,
                "更新到分店价格任务已提交"
            );
            lock (_jobStartSyncRoot)
            {
                duplicate = GetRunningStorePriceJob(operationId);
                if (duplicate != null)
                {
                    duplicate.IsDuplicateRequest = true;
                    return Task.FromResult(duplicate);
                }
                ThrowIfInvoiceFamilyRunning(familyKey, operationId, _storePriceJobs);

                // 关键位置：先登记 job 和 operation，再启动后台任务，避免重复点击创建多个写入任务。
                _storePriceJobs[jobState.JobId] = jobState;
                _runningOperationJobIds[operationId] = jobState.JobId;
                _runningInvoiceFamilyJobIds[familyKey] = jobState.JobId;
            }

            // 关键位置：后台任务重新创建作用域，避免复用请求结束后的 scoped 服务。
            _ = Task.Run(() => ExecuteUpdateToStorePricesJobAsync(jobState, CloneUpdateToStoreRequest(request), updatedBy), CancellationToken.None);
            return Task.FromResult(CreateStorePriceSnapshot(jobState, false));
        }

        public Task<LocalSupplierInvoiceUpdateToStorePricesJobDto?> GetUpdateToStorePricesJobAsync(
            string jobId,
            CancellationToken cancellationToken = default
        )
        {
            CleanupExpiredJobs();
            return Task.FromResult(
                _storePriceJobs.TryGetValue(jobId, out var jobState)
                    ? CreateStorePriceSnapshot(jobState, false)
                    : null
            );
        }

        public Task<LocalSupplierInvoiceUpdateHqProductsJobDto> StartUpdateHqProductsJobAsync(
            string invoiceGuid,
            UpdateHqProductsRequest request,
            string updatedBy,
            CancellationToken cancellationToken = default
        ) => StartUpdateHqProductsJobAsync(
            invoiceGuid,
            request,
            actorUserGuid: null,
            actorName: updatedBy,
            cancellationToken
        );

        public Task<LocalSupplierInvoiceUpdateHqProductsJobDto> StartUpdateHqProductsJobAsync(
            string invoiceGuid,
            UpdateHqProductsRequest request,
            string? actorUserGuid,
            string actorName,
            CancellationToken cancellationToken = default
        )
        {
            CleanupExpiredJobs();

            var operationId = BuildUpdateHqProductsOperationId(invoiceGuid, request);
            var duplicate = GetRunningHqProductJob(operationId);
            if (duplicate != null)
            {
                duplicate.IsDuplicateRequest = true;
                return Task.FromResult(duplicate);
            }

            var familyKey = BuildInvoiceFamilyKey("update-hq-products", invoiceGuid);
            var jobState = CreateJobState<UpdateHqProductsResult>(
                invoiceGuid,
                NormalizeStoreCodes(request.TargetStoreCodes),
                operationId,
                familyKey,
                "更新HQ商品任务已提交"
            );
            lock (_jobStartSyncRoot)
            {
                duplicate = GetRunningHqProductJob(operationId);
                if (duplicate != null)
                {
                    duplicate.IsDuplicateRequest = true;
                    return Task.FromResult(duplicate);
                }
                ThrowIfInvoiceFamilyRunning(familyKey, operationId, _hqProductJobs);

                // 关键位置：operationId 包含幂等键，确保同一轮前端重试接管同一个后台任务。
                _hqProductJobs[jobState.JobId] = jobState;
                _runningOperationJobIds[operationId] = jobState.JobId;
                _runningInvoiceFamilyJobIds[familyKey] = jobState.JobId;
            }

            // 请求结束后仍使用提交时固化的操作者，不能在后台 scope 中重新读取 HttpContext。
            _ = Task.Run(
                () => ExecuteUpdateHqProductsJobAsync(
                    jobState,
                    invoiceGuid,
                    CloneUpdateHqProductsRequest(request),
                    actorUserGuid,
                    actorName
                ),
                CancellationToken.None
            );
            return Task.FromResult(CreateHqProductSnapshot(jobState, false));
        }

        public Task<LocalSupplierInvoiceUpdateHqProductsJobDto?> GetUpdateHqProductsJobAsync(
            string jobId,
            CancellationToken cancellationToken = default
        )
        {
            CleanupExpiredJobs();
            return Task.FromResult(
                _hqProductJobs.TryGetValue(jobId, out var jobState)
                    ? CreateHqProductSnapshot(jobState, false)
                    : null
            );
        }

        public Task<LocalSupplierInvoicePasteDetailsJobDto> StartPasteDetailsJobAsync(
            PasteDetailsRequest request,
            string updatedBy,
            CancellationToken cancellationToken = default
        )
        {
            CleanupExpiredJobs();

            var operationId = BuildPasteDetailsOperationId(request);
            var duplicate = GetRunningPasteDetailsJob(operationId);
            if (duplicate != null)
            {
                duplicate.IsDuplicateRequest = true;
                return Task.FromResult(duplicate);
            }

            var familyKey = BuildInvoiceFamilyKey("paste-details", request.InvoiceGuid);
            var jobState = CreateJobState<BatchResultDto>(
                request.InvoiceGuid,
                new List<string>(),
                operationId,
                familyKey,
                "粘贴明细任务已提交"
            );
            lock (_jobStartSyncRoot)
            {
                duplicate = GetRunningPasteDetailsJob(operationId);
                if (duplicate != null)
                {
                    duplicate.IsDuplicateRequest = true;
                    return Task.FromResult(duplicate);
                }
                ThrowIfInvoiceFamilyRunning(familyKey, operationId, _pasteDetailsJobs);

                // 关键位置：粘贴会批量写入明细，必须先登记 job 再后台执行，避免请求线程被长事务阻塞。
                _pasteDetailsJobs[jobState.JobId] = jobState;
                _runningOperationJobIds[operationId] = jobState.JobId;
                _runningInvoiceFamilyJobIds[familyKey] = jobState.JobId;
            }

            _ = Task.Run(() => ExecutePasteDetailsJobAsync(jobState, ClonePasteDetailsRequest(request), updatedBy), CancellationToken.None);
            return Task.FromResult(CreatePasteDetailsSnapshot(jobState, false));
        }

        public Task<LocalSupplierInvoicePasteDetailsJobDto?> GetPasteDetailsJobAsync(
            string jobId,
            CancellationToken cancellationToken = default
        )
        {
            CleanupExpiredJobs();
            return Task.FromResult(
                _pasteDetailsJobs.TryGetValue(jobId, out var jobState)
                    ? CreatePasteDetailsSnapshot(jobState, false)
                    : null
            );
        }

        public Task<LocalSupplierInvoiceCheckProductsJobDto> StartCheckProductsJobAsync(
            CheckProductsRequest request,
            CancellationToken cancellationToken = default
        )
        {
            CleanupExpiredJobs();

            var operationId = BuildCheckProductsOperationId(request);
            var duplicate = GetRunningCheckProductsJob(operationId);
            if (duplicate != null)
            {
                duplicate.IsDuplicateRequest = true;
                return Task.FromResult(duplicate);
            }

            var familyKey = BuildInvoiceFamilyKey("check-products", request.InvoiceGuid);
            var jobState = CreateJobState<CheckProductsResponseDto>(
                request.InvoiceGuid,
                new List<string>(),
                operationId,
                familyKey,
                "商品检测任务已提交"
            );
            lock (_jobStartSyncRoot)
            {
                duplicate = GetRunningCheckProductsJob(operationId);
                if (duplicate != null)
                {
                    duplicate.IsDuplicateRequest = true;
                    return Task.FromResult(duplicate);
                }
                ThrowIfInvoiceFamilyRunning(familyKey, operationId, _checkProductsJobs);

                // 关键位置：商品检测可能进行大量商品/条码查询，后台执行后由前端轮询终态。
                _checkProductsJobs[jobState.JobId] = jobState;
                _runningOperationJobIds[operationId] = jobState.JobId;
                _runningInvoiceFamilyJobIds[familyKey] = jobState.JobId;
            }

            _ = Task.Run(() => ExecuteCheckProductsJobAsync(jobState, CloneCheckProductsRequest(request)), CancellationToken.None);
            return Task.FromResult(CreateCheckProductsSnapshot(jobState, false));
        }

        public Task<LocalSupplierInvoiceCheckProductsJobDto?> GetCheckProductsJobAsync(
            string jobId,
            CancellationToken cancellationToken = default
        )
        {
            CleanupExpiredJobs();
            return Task.FromResult(
                _checkProductsJobs.TryGetValue(jobId, out var jobState)
                    ? CreateCheckProductsSnapshot(jobState, false)
                    : null
            );
        }

        private async Task ExecuteUpdateToStorePricesJobAsync(
            JobState<UpdateToStorePricesResultDto> jobState,
            UpdateToStorePricesRequest request,
            string updatedBy
        )
        {
            var startedTimestamp = _timeProvider.GetTimestamp();
            var attempt = 0;
            UpdateToStorePricesResultDto? lastBusyResult = null;
            try
            {
                while (true)
                {
                    var remainingBudget = StorePriceLockRetryBudget - GetElapsed(startedTimestamp);
                    if (remainingBudget <= TimeSpan.Zero)
                    {
                        CompleteJob(
                            jobState,
                            LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
                            lastBusyResult ?? new UpdateToStorePricesResultDto(),
                            StorePriceLockRetryTimeoutMessage
                        );
                        return;
                    }

                    attempt++;
                    var lockWaitMilliseconds = Math.Clamp(
                        (int)Math.Ceiling(remainingBudget.TotalMilliseconds),
                        1,
                        MaxStorePriceLockWaitMilliseconds
                    );
                    ApiResponse<UpdateToStorePricesResultDto> response;

                    // 每次尝试使用独立 scope 和克隆请求；锁忙等待发生在 scope 释放之后。
                    using (_logger.BeginScope(new Dictionary<string, object?>
                    {
                        ["JobId"] = jobState.JobId,
                        ["InvoiceGuid"] = request.InvoiceGuid,
                        ["Attempt"] = attempt,
                        ["Elapsed"] = GetElapsed(startedTimestamp),
                    }))
                    {
                        using var scope = _serviceScopeFactory.CreateScope();
                        var service = scope.ServiceProvider.GetRequiredService<ILocalSupplierInvoicesReactService>();
                        response = await service.UpdateDetailsToStorePricesAsync(
                            CloneUpdateToStoreRequest(request),
                            updatedBy,
                            lockWaitMilliseconds
                        );
                    }

                    var result = response.Data ?? response.Details as UpdateToStorePricesResultDto;
                    if (IsRetryableStorePriceLockBusy(response, result))
                    {
                        lastBusyResult = result;
                        remainingBudget = StorePriceLockRetryBudget - GetElapsed(startedTimestamp);
                        if (remainingBudget <= TimeSpan.Zero)
                        {
                            CompleteJob(
                                jobState,
                                LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
                                result ?? new UpdateToStorePricesResultDto(),
                                StorePriceLockRetryTimeoutMessage
                            );
                            return;
                        }

                        var delay = remainingBudget < StorePriceLockRetryDelay
                            ? remainingBudget
                            : StorePriceLockRetryDelay;
                        _logger.LogWarning(
                            "更新分店价格遇到成本锁冲突，将重试。JobId={JobId}, InvoiceGuid={InvoiceGuid}, Attempt={Attempt}, Elapsed={Elapsed}, Delay={Delay}",
                            jobState.JobId,
                            request.InvoiceGuid,
                            attempt,
                            GetElapsed(startedTimestamp),
                            delay
                        );
                        await Task.Delay(delay, _timeProvider);
                        continue;
                    }

                    CompleteJob(
                        jobState,
                        response.Success
                            ? LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded
                            : LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
                        result,
                        response.Message
                    );
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "执行更新到分店价格 job 失败: JobId={JobId}, InvoiceGuid={InvoiceGuid}, Attempt={Attempt}, Elapsed={Elapsed}",
                    jobState.JobId,
                    request.InvoiceGuid,
                    attempt,
                    GetElapsed(startedTimestamp)
                );
                CompleteJob(
                    jobState,
                    LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
                    new UpdateToStorePricesResultDto
                    {
                        Failed = 1,
                        Errors = [ex.Message],
                    },
                    ex.Message
                );
            }
        }

        private async Task ExecutePasteDetailsJobAsync(
            JobState<BatchResultDto> jobState,
            PasteDetailsRequest request,
            string updatedBy
        )
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ILocalSupplierInvoicesReactService>();
                var response = await service.PasteDetailsAsync(request, updatedBy);
                var result = response.Data ?? response.Details as BatchResultDto;
                CompleteJob(
                    jobState,
                    response.Success
                        ? LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded
                        : LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
                    result,
                    response.Message
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行粘贴明细 job 失败: {JobId}", jobState.JobId);
                CompleteJob(
                    jobState,
                    LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
                    new BatchResultDto
                    {
                        Failed = 1,
                        Errors = [ex.Message],
                    },
                    ex.Message
                );
            }
        }

        private async Task ExecuteCheckProductsJobAsync(
            JobState<CheckProductsResponseDto> jobState,
            CheckProductsRequest request
        )
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ILocalSupplierInvoicesReactService>();
                var response = await service.CheckProductsAsync(request);
                var result = response.Data ?? response.Details as CheckProductsResponseDto;
                CompleteJob(
                    jobState,
                    response.Success
                        ? LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded
                        : LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
                    result,
                    response.Message
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行商品检测 job 失败: {JobId}", jobState.JobId);
                CompleteJob(
                    jobState,
                    LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
                    new CheckProductsResponseDto(),
                    ex.Message
                );
            }
        }

        private async Task ExecuteUpdateHqProductsJobAsync(
            JobState<UpdateHqProductsResult> jobState,
            string invoiceGuid,
            UpdateHqProductsRequest request,
            string? actorUserGuid,
            string actorName
        )
        {
            var startedTimestamp = _timeProvider.GetTimestamp();
            var attempt = 0;
            UpdateHqProductsResult? lastBusyResult = null;
            try
            {
                while (true)
                {
                    var remainingBudget = HqLockRetryBudget - GetElapsed(startedTimestamp);
                    if (remainingBudget <= TimeSpan.Zero)
                    {
                        CompleteJob(
                            jobState,
                            LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
                            lastBusyResult ?? new UpdateHqProductsResult(),
                            HqLockRetryTimeoutMessage
                        );
                        return;
                    }

                    attempt++;
                    var lockWaitMilliseconds = Math.Clamp(
                        (int)Math.Ceiling(remainingBudget.TotalMilliseconds),
                        1,
                        MaxHqLockWaitMilliseconds
                    );
                    ApiResponse<UpdateHqProductsResult> response;

                    // 每次尝试独立创建 scope 和 request；scope 必须在重试等待前释放，避免持有数据库连接或事务。
                    using (_logger.BeginScope(new Dictionary<string, object?>
                    {
                        ["JobId"] = jobState.JobId,
                        ["InvoiceGuid"] = invoiceGuid,
                        ["Attempt"] = attempt,
                        ["Elapsed"] = GetElapsed(startedTimestamp),
                    }))
                    {
                        using var scope = _serviceScopeFactory.CreateScope();
                        var service = scope.ServiceProvider.GetRequiredService<ILocalSupplierInvoiceHqProductSyncService>();
                        response = await service.UpdateHqProductsAsync(
                            invoiceGuid,
                            CloneUpdateHqProductsRequest(request),
                            actorUserGuid,
                            actorName,
                            lockWaitMilliseconds
                        );
                    }

                    var result = response.Data ?? response.Details as UpdateHqProductsResult;
                    if (IsRetryableHqLockBusy(response, result))
                    {
                        lastBusyResult = result;
                        remainingBudget = HqLockRetryBudget - GetElapsed(startedTimestamp);
                        if (remainingBudget <= TimeSpan.Zero)
                        {
                            CompleteJob(
                                jobState,
                                LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
                                result ?? new UpdateHqProductsResult(),
                                HqLockRetryTimeoutMessage
                            );
                            return;
                        }

                        var delay = remainingBudget < HqLockRetryDelay
                            ? remainingBudget
                            : HqLockRetryDelay;
                        _logger.LogWarning(
                            "更新HQ商品遇到成本锁冲突，将重试。JobId={JobId}, InvoiceGuid={InvoiceGuid}, Attempt={Attempt}, Elapsed={Elapsed}, Delay={Delay}",
                            jobState.JobId,
                            invoiceGuid,
                            attempt,
                            GetElapsed(startedTimestamp),
                            delay
                        );
                        await Task.Delay(delay, _timeProvider);
                        continue;
                    }

                    CompleteJob(
                        jobState,
                        response.Success
                            ? LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded
                            : LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
                        result,
                        response.Message
                    );
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "执行更新HQ商品 job 失败: JobId={JobId}, InvoiceGuid={InvoiceGuid}, Attempt={Attempt}, Elapsed={Elapsed}",
                    jobState.JobId,
                    invoiceGuid,
                    attempt,
                    GetElapsed(startedTimestamp)
                );
                CompleteJob(
                    jobState,
                    LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
                    new UpdateHqProductsResult
                    {
                        Failed = 1,
                        Errors = [new EnsureHqProductError { Message = ex.Message }],
                    },
                    ex.Message
                );
            }
        }

        private TimeSpan GetElapsed(long startedTimestamp)
        {
            var elapsed = _timeProvider.GetElapsedTime(startedTimestamp);
            return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        }

        private static bool IsRetryableHqLockBusy(
            ApiResponse<UpdateHqProductsResult> response,
            UpdateHqProductsResult? result
        )
        {
            return !response.Success
                && string.Equals(response.Code, HqUpdateCostLockBusyCode, StringComparison.Ordinal)
                && result is not null
                && result.HqExisting == 0
                && result.Skipped == 0
                && result.Failed == 0
                && result.Errors is { Count: 0 }
                && !HasHqWriteCounts(result);
        }

        private static bool IsRetryableStorePriceLockBusy(
            ApiResponse<UpdateToStorePricesResultDto> response,
            UpdateToStorePricesResultDto? result
        )
        {
            return !response.Success
                && string.Equals(response.Code, StorePriceUpdateCostLockBusyCode, StringComparison.Ordinal)
                && result is not null
                && result.Inserted == 0
                && result.Updated == 0
                && result.UpdatedPurchasePrices == 0
                && result.Skipped == 0
                && result.Failed == 0
                && result.Errors is { Count: 0 };
        }

        private static bool HasHqWriteCounts(UpdateHqProductsResult? result)
        {
            return result is not null
                && (result.HbwebCreated > 0
                    || result.HqCreated > 0
                    || result.HqSynced > 0
                    || result.HqPurchasePricesUpdated > 0
                    || result.Updated > 0
                    || result.HqRetailPricesUpdated > 0
                    || result.HqAutoPricingUpdated > 0
                    || result.HqSpecialProductsUpdated > 0
                    || result.HqDiscountRatesUpdated > 0
                    || result.HqProductSetCodesCreated > 0
                    || result.HqProductSetCodesUpdated > 0
                    || result.HqStoreMultiCodesCreated > 0
                    || result.HqStoreMultiCodesUpdated > 0);
        }

        private void CompleteJob<T>(
            JobState<T> jobState,
            string status,
            T? result,
            string? message
        )
        {
            lock (_jobStartSyncRoot)
            {
                lock (jobState.SyncRoot)
                {
                    var completedAt = _timeProvider.GetUtcNow().UtcDateTime;
                    jobState.Status = status;
                    jobState.CompletedAt = completedAt;
                    jobState.ExpiresAt = completedAt.Add(_completedRetention);
                    jobState.Result = result;
                    jobState.Message = message;
                }

                _runningOperationJobIds.TryRemove(jobState.OperationId, out _);
                _runningInvoiceFamilyJobIds.TryRemove(jobState.FamilyKey, out _);
            }
        }

        private void CleanupExpiredJobs()
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            CleanupExpiredJobs(_storePriceJobs, now);
            CleanupExpiredJobs(_hqProductJobs, now);
            CleanupExpiredJobs(_pasteDetailsJobs, now);
            CleanupExpiredJobs(_checkProductsJobs, now);
            CleanupExpiredBatchCheckProductsJobs(now);
        }

        private void CleanupExpiredJobs<T>(
            ConcurrentDictionary<string, JobState<T>> jobs,
            DateTime now
        )
        {
            foreach (var pair in jobs)
            {
                var state = pair.Value;
                if (state.ExpiresAt.HasValue && state.ExpiresAt.Value <= now)
                {
                    jobs.TryRemove(pair.Key, out _);
                }
            }
        }

        private void ThrowIfInvoiceFamilyRunning<T>(
            string familyKey,
            string operationId,
            ConcurrentDictionary<string, JobState<T>> jobs
        )
        {
            if (!TryGetRunningInvoiceFamilyJobId(familyKey, jobs, out var existingJobId))
                return;

            throw new LocalSupplierInvoiceBatchUpdateJobConflictException(
                "同一张本地进货单已有同类后台任务正在执行，请等待完成后再提交新的批量写入",
                existingJobId,
                operationId
            );
        }

        /// <summary>同张单同类任务仍在运行时返回其 JobId；登记已失效（任务结束或被清理）时顺手移除。</summary>
        private bool TryGetRunningInvoiceFamilyJobId<T>(
            string familyKey,
            ConcurrentDictionary<string, JobState<T>> jobs,
            out string existingJobId
        )
        {
            if (!_runningInvoiceFamilyJobIds.TryGetValue(familyKey, out existingJobId!))
                return false;
            if (!jobs.TryGetValue(existingJobId, out var existingState) || !IsRunning(existingState))
            {
                _runningInvoiceFamilyJobIds.TryRemove(familyKey, out _);
                existingJobId = string.Empty;
                return false;
            }

            return true;
        }

        private LocalSupplierInvoiceUpdateToStorePricesJobDto? GetRunningStorePriceJob(string operationId)
        {
            return _runningOperationJobIds.TryGetValue(operationId, out var jobId)
                && _storePriceJobs.TryGetValue(jobId, out var jobState)
                && IsRunning(jobState)
                    ? CreateStorePriceSnapshot(jobState, true)
                    : null;
        }

        private LocalSupplierInvoiceUpdateHqProductsJobDto? GetRunningHqProductJob(string operationId)
        {
            return _runningOperationJobIds.TryGetValue(operationId, out var jobId)
                && _hqProductJobs.TryGetValue(jobId, out var jobState)
                && IsRunning(jobState)
                    ? CreateHqProductSnapshot(jobState, true)
                    : null;
        }

        private LocalSupplierInvoicePasteDetailsJobDto? GetRunningPasteDetailsJob(string operationId)
        {
            return _runningOperationJobIds.TryGetValue(operationId, out var jobId)
                && _pasteDetailsJobs.TryGetValue(jobId, out var jobState)
                && IsRunning(jobState)
                    ? CreatePasteDetailsSnapshot(jobState, true)
                    : null;
        }

        private LocalSupplierInvoiceCheckProductsJobDto? GetRunningCheckProductsJob(string operationId)
        {
            return _runningOperationJobIds.TryGetValue(operationId, out var jobId)
                && _checkProductsJobs.TryGetValue(jobId, out var jobState)
                && IsRunning(jobState)
                    ? CreateCheckProductsSnapshot(jobState, true)
                    : null;
        }

        private static bool IsRunning<T>(JobState<T> jobState)
        {
            lock (jobState.SyncRoot)
            {
                return jobState.Status == LocalSupplierInvoiceBatchUpdateJobStatusConstants.Running;
            }
        }

        private JobState<T> CreateJobState<T>(
            string invoiceGuid,
            List<string> targetStoreCodes,
            string operationId,
            string familyKey,
            string message
        )
        {
            return new JobState<T>
            {
                JobId = Guid.NewGuid().ToString("N"),
                InvoiceGuid = invoiceGuid,
                TargetStoreCodes = targetStoreCodes,
                OperationId = operationId,
                FamilyKey = familyKey,
                CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                Message = message,
            };
        }

        private static LocalSupplierInvoiceUpdateToStorePricesJobDto CreateStorePriceSnapshot(
            JobState<UpdateToStorePricesResultDto> jobState,
            bool isDuplicateRequest
        )
        {
            lock (jobState.SyncRoot)
            {
                return new LocalSupplierInvoiceUpdateToStorePricesJobDto
                {
                    JobId = jobState.JobId,
                    InvoiceGuid = jobState.InvoiceGuid,
                    TargetStoreCodes = jobState.TargetStoreCodes.ToList(),
                    OperationId = jobState.OperationId,
                    Status = jobState.Status,
                    IsDuplicateRequest = isDuplicateRequest,
                    CreatedAt = jobState.CreatedAt,
                    CompletedAt = jobState.CompletedAt,
                    ExpiresAt = jobState.ExpiresAt,
                    Message = jobState.Message,
                    Result = jobState.Result,
                };
            }
        }

        private static LocalSupplierInvoiceUpdateHqProductsJobDto CreateHqProductSnapshot(
            JobState<UpdateHqProductsResult> jobState,
            bool isDuplicateRequest
        )
        {
            lock (jobState.SyncRoot)
            {
                return new LocalSupplierInvoiceUpdateHqProductsJobDto
                {
                    JobId = jobState.JobId,
                    InvoiceGuid = jobState.InvoiceGuid,
                    TargetStoreCodes = jobState.TargetStoreCodes.ToList(),
                    OperationId = jobState.OperationId,
                    Status = jobState.Status,
                    IsDuplicateRequest = isDuplicateRequest,
                    CreatedAt = jobState.CreatedAt,
                    CompletedAt = jobState.CompletedAt,
                    ExpiresAt = jobState.ExpiresAt,
                    Message = jobState.Message,
                    Result = jobState.Result,
                };
            }
        }

        private static LocalSupplierInvoicePasteDetailsJobDto CreatePasteDetailsSnapshot(
            JobState<BatchResultDto> jobState,
            bool isDuplicateRequest
        )
        {
            lock (jobState.SyncRoot)
            {
                return new LocalSupplierInvoicePasteDetailsJobDto
                {
                    JobId = jobState.JobId,
                    InvoiceGuid = jobState.InvoiceGuid,
                    TargetStoreCodes = jobState.TargetStoreCodes.ToList(),
                    OperationId = jobState.OperationId,
                    Status = jobState.Status,
                    IsDuplicateRequest = isDuplicateRequest,
                    CreatedAt = jobState.CreatedAt,
                    CompletedAt = jobState.CompletedAt,
                    ExpiresAt = jobState.ExpiresAt,
                    Message = jobState.Message,
                    Result = jobState.Result,
                };
            }
        }

        private static LocalSupplierInvoiceCheckProductsJobDto CreateCheckProductsSnapshot(
            JobState<CheckProductsResponseDto> jobState,
            bool isDuplicateRequest
        )
        {
            lock (jobState.SyncRoot)
            {
                return new LocalSupplierInvoiceCheckProductsJobDto
                {
                    JobId = jobState.JobId,
                    InvoiceGuid = jobState.InvoiceGuid,
                    TargetStoreCodes = jobState.TargetStoreCodes.ToList(),
                    OperationId = jobState.OperationId,
                    Status = jobState.Status,
                    IsDuplicateRequest = isDuplicateRequest,
                    CreatedAt = jobState.CreatedAt,
                    CompletedAt = jobState.CompletedAt,
                    ExpiresAt = jobState.ExpiresAt,
                    Message = jobState.Message,
                    Result = jobState.Result,
                };
            }
        }

        private static string BuildUpdateToStoreOperationId(UpdateToStorePricesRequest request)
        {
            return string.Join(
                "|",
                "update-to-store",
                request.InvoiceGuid,
                JoinSorted(request.DetailGuids),
                JoinSorted(request.TargetStoreCodes),
                BuildUpdateFieldsKey(request.UpdateFields)
            );
        }

        private static string BuildUpdateHqProductsOperationId(
            string invoiceGuid,
            UpdateHqProductsRequest request
        )
        {
            return string.Join(
                "|",
                "update-hq-products",
                invoiceGuid,
                JoinSorted(request.DetailGuids),
                JoinSorted(request.TargetStoreCodes),
                BuildUpdateFieldsKey(request.UpdateFields),
                request.IdempotencyKey ?? string.Empty
            );
        }

        private static string BuildPasteDetailsOperationId(PasteDetailsRequest request)
        {
            return string.Join(
                "|",
                "paste-details",
                request.InvoiceGuid,
                request.Mode,
                BuildPasteItemsKey(request.Items)
            );
        }

        private static string BuildCheckProductsOperationId(CheckProductsRequest request)
        {
            return string.Join(
                "|",
                "check-products",
                request.InvoiceGuid,
                JoinSorted(request.DetailGuids)
            ) + (request.ExcludeExecutedDetails ? "|exclude-executed" : string.Empty);
        }

        private static string BuildInvoiceFamilyKey(string family, string invoiceGuid)
        {
            return string.Join("|", family, invoiceGuid);
        }

        private static List<string> NormalizeStoreCodes(IEnumerable<string>? values)
        {
            return (values ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value)
                .ToList();
        }

        private static string JoinSorted(IEnumerable<string>? values)
        {
            return string.Join(",", NormalizeStoreCodes(values));
        }

        private static string BuildUpdateFieldsKey(UpdateToStorePricesFields fields)
        {
            return string.Join(
                ",",
                fields.UpdatePurchasePrice,
                fields.PurchasePrice,
                fields.UpdateRetailPrice,
                fields.RetailPrice,
                fields.UpdateIsAutoPricing,
                fields.IsAutoPricing,
                fields.UpdateIsSpecialProduct,
                fields.IsSpecialProduct,
                fields.UpdateDiscountRate,
                fields.DiscountRate
            );
        }

        private static UpdateToStorePricesRequest CloneUpdateToStoreRequest(UpdateToStorePricesRequest request)
        {
            return new UpdateToStorePricesRequest
            {
                InvoiceGuid = request.InvoiceGuid,
                DetailGuids = request.DetailGuids?.ToList() ?? new List<string>(),
                TargetStoreCodes = request.TargetStoreCodes?.ToList() ?? new List<string>(),
                UpdateFields = CloneUpdateFields(request.UpdateFields),
            };
        }

        private static UpdateHqProductsRequest CloneUpdateHqProductsRequest(UpdateHqProductsRequest request)
        {
            return new UpdateHqProductsRequest
            {
                DetailGuids = request.DetailGuids?.ToList() ?? new List<string>(),
                TargetStoreCodes = request.TargetStoreCodes?.ToList() ?? new List<string>(),
                UpdateFields = CloneUpdateFields(request.UpdateFields),
                IdempotencyKey = request.IdempotencyKey,
            };
        }

        private static PasteDetailsRequest ClonePasteDetailsRequest(PasteDetailsRequest request)
        {
            return new PasteDetailsRequest
            {
                InvoiceGuid = request.InvoiceGuid,
                Mode = request.Mode,
                Items = request.Items
                    .Select(item => new PastedDetailItemDto
                    {
                        ItemNumber = item.ItemNumber,
                        Barcode = item.Barcode,
                        // 关键位置：异步粘贴任务要跨线程执行，副条码必须随请求一起克隆，避免后台入库时丢失多码提示。
                        AdditionalBarcodes = item.AdditionalBarcodes?.ToList() ?? new List<string>(),
                        ProductName = item.ProductName,
                        Quantity = item.Quantity,
                        PurchasePrice = item.PurchasePrice,
                        NewAutoRetailPrice = item.NewAutoRetailPrice,
                        RetailPrice = item.RetailPrice,
                    })
                    .ToList(),
            };
        }

        private static CheckProductsRequest CloneCheckProductsRequest(CheckProductsRequest request)
        {
            return new CheckProductsRequest
            {
                InvoiceGuid = request.InvoiceGuid,
                DetailGuids = request.DetailGuids?.ToList(),
                ExcludeExecutedDetails = request.ExcludeExecutedDetails,
            };
        }

        private static UpdateToStorePricesFields CloneUpdateFields(UpdateToStorePricesFields fields)
        {
            return new UpdateToStorePricesFields
            {
                UpdatePurchasePrice = fields.UpdatePurchasePrice,
                PurchasePrice = fields.PurchasePrice,
                UpdateRetailPrice = fields.UpdateRetailPrice,
                RetailPrice = fields.RetailPrice,
                UpdateIsAutoPricing = fields.UpdateIsAutoPricing,
                IsAutoPricing = fields.IsAutoPricing,
                UpdateIsSpecialProduct = fields.UpdateIsSpecialProduct,
                IsSpecialProduct = fields.IsSpecialProduct,
                UpdateDiscountRate = fields.UpdateDiscountRate,
                DiscountRate = fields.DiscountRate,
            };
        }

        private static string BuildPasteItemsKey(IEnumerable<PastedDetailItemDto>? items)
        {
            return string.Join(
                ";",
                (items ?? [])
                    .Select(item => string.Join(
                        ",",
                        item.ItemNumber,
                        item.Barcode,
                        item.ProductName,
                        item.Quantity,
                        item.PurchasePrice,
                        item.NewAutoRetailPrice,
                        item.RetailPrice,
                        string.Join(",", item.AdditionalBarcodes ?? [])
                    ))
            );
        }

        private sealed class JobState<T>
        {
            public object SyncRoot { get; } = new();
            public string JobId { get; init; } = string.Empty;
            public string InvoiceGuid { get; init; } = string.Empty;
            public List<string> TargetStoreCodes { get; init; } = new();
            public string OperationId { get; init; } = string.Empty;
            public string FamilyKey { get; init; } = string.Empty;
            public string Status { get; set; } = LocalSupplierInvoiceBatchUpdateJobStatusConstants.Running;
            public DateTime CreatedAt { get; init; }
            public DateTime? CompletedAt { get; set; }
            public DateTime? ExpiresAt { get; set; }
            public string? Message { get; set; }
            public T? Result { get; set; }
        }
    }
}
