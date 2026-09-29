using System.Collections.Concurrent;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 货柜明细创建新商品 job 服务。
    /// </summary>
    public class ContainerProductCreationJobService : IContainerProductCreationJobService
    {
        private static readonly TimeSpan DefaultCompletedRetention = TimeSpan.FromMinutes(45);

        private readonly ConcurrentDictionary<string, ContainerProductCreationJobState> _jobs = new();
        private readonly ConcurrentDictionary<string, string> _operationJobIds = new(
            StringComparer.OrdinalIgnoreCase
        );
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<ContainerProductCreationJobService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _completedRetention;
        private readonly object _jobStartSyncRoot = new();

        public ContainerProductCreationJobService(
            IServiceScopeFactory serviceScopeFactory,
            ILogger<ContainerProductCreationJobService> logger
        )
            : this(serviceScopeFactory, logger, TimeProvider.System, DefaultCompletedRetention) { }

        public ContainerProductCreationJobService(
            IServiceScopeFactory serviceScopeFactory,
            ILogger<ContainerProductCreationJobService> logger,
            TimeProvider? timeProvider = null,
            TimeSpan? completedRetention = null
        )
        {
            _serviceScopeFactory = serviceScopeFactory;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _completedRetention = completedRetention ?? DefaultCompletedRetention;
        }

        public Task<ContainerProductCreationJobDto> StartJobAsync(
            string userId,
            ContainerProductCreationJobRequestDto request,
            CancellationToken cancellationToken = default
        )
        {
            return StartJobAsync(userId, null, request, cancellationToken);
        }

        public Task<ContainerProductCreationJobDto> StartJobAsync(
            string userId,
            string? updatedBy,
            ContainerProductCreationJobRequestDto request,
            CancellationToken cancellationToken = default
        )
        {
            CleanupExpiredJobs();

            var normalizedUserId = NormalizeUserId(userId);
            var normalizedUpdatedBy = NormalizeUpdatedBy(updatedBy);
            var normalizedRequest = NormalizeRequest(request);
            var operationKey = BuildOperationKey(normalizedRequest);

            lock (_jobStartSyncRoot)
            {
                if (_operationJobIds.TryGetValue(operationKey, out var existingJobId))
                {
                    var existingJob = GetSnapshot(
                        normalizedUserId,
                        existingJobId,
                        isDuplicateRequest: true
                    );
                    // 排队/运行中与已成功的任务继续复用，防止重复提交；已失败的任务不复用，
                    // 否则用户修正数据（如补英文名）后再次提交会在保留期内一直拿到旧的失败结果。
                    if (
                        existingJob != null
                        && existingJob.Status != ContainerProductCreationJobStatusConstants.Failed
                    )
                    {
                        return Task.FromResult(existingJob);
                    }

                    _operationJobIds.TryRemove(operationKey, out _);
                }

                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var jobState = new ContainerProductCreationJobState
                {
                    JobId = Guid.NewGuid().ToString("N"),
                    UserId = normalizedUserId,
                    UpdatedBy = normalizedUpdatedBy,
                    OperationKey = operationKey,
                    Request = normalizedRequest,
                    CreatedAt = now,
                    Status = ContainerProductCreationJobStatusConstants.Queued,
                };

                _jobs[jobState.JobId] = jobState;
                _operationJobIds[operationKey] = jobState.JobId;

                // 关键位置：后台任务重新创建作用域，避免复用请求结束后的 scoped 服务。
                _ = Task.Run(() => ExecuteJobAsync(jobState), CancellationToken.None);
                return Task.FromResult(CreateSnapshot(jobState, isDuplicateRequest: false));
            }
        }

        public Task<ContainerProductCreationJobDto?> GetJobAsync(
            string userId,
            string jobId,
            CancellationToken cancellationToken = default
        )
        {
            CleanupExpiredJobs();
            return Task.FromResult(GetSnapshot(NormalizeUserId(userId), jobId, isDuplicateRequest: false));
        }

        private async Task ExecuteJobAsync(ContainerProductCreationJobState jobState)
        {
            try
            {
                SetRunning(jobState);
                using var scope = _serviceScopeFactory.CreateScope();
                var executor = scope.ServiceProvider.GetRequiredService<IContainerProductCreationExecutorService>();
                // 后台 scope 没有原始 HttpContext，必须从 job state 传递可信的更新人。
                var result = await executor.ExecuteAsync(
                    jobState.Request,
                    jobState.UserId,
                    jobState.UpdatedBy
                );
                var status = result.FailedCount > 0
                    ? ContainerProductCreationJobStatusConstants.Failed
                    : ContainerProductCreationJobStatusConstants.Succeeded;
                var actionName = jobState.Request.SubmitContainer ? "提交货柜" : "创建新商品";
                CompleteJob(
                    jobState,
                    status,
                    result,
                    status == ContainerProductCreationJobStatusConstants.Failed
                        ? $"{actionName}存在失败明细"
                        : $"{actionName}完成"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行货柜创建新商品 job 失败: {JobId}", jobState.JobId);
                CompleteJob(
                    jobState,
                    ContainerProductCreationJobStatusConstants.Failed,
                    new ContainerProductCreationResultDto
                    {
                        Errors = new List<ContainerProductCreationResultItemDto>
                        {
                            new()
                            {
                                ReasonCode = "JOB_EXCEPTION",
                                Message = ex.Message,
                            },
                        },
                        FailedCount = 1,
                    },
                    ex.Message
                );
            }
        }

        private void SetRunning(ContainerProductCreationJobState jobState)
        {
            lock (jobState.SyncRoot)
            {
                jobState.Status = ContainerProductCreationJobStatusConstants.Running;
            }
        }

        private void CompleteJob(
            ContainerProductCreationJobState jobState,
            string status,
            ContainerProductCreationResultDto result,
            string message
        )
        {
            lock (_jobStartSyncRoot)
            {
                lock (jobState.SyncRoot)
                {
                    var completedAt = _timeProvider.GetUtcNow().UtcDateTime;
                    jobState.Status = status;
                    jobState.Message = message;
                    jobState.Result = result;
                    jobState.CompletedAt = completedAt;
                    jobState.ExpiresAt = completedAt.Add(_completedRetention);
                }
            }
        }

        private void CleanupExpiredJobs()
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            foreach (var pair in _jobs)
            {
                var jobState = pair.Value;
                DateTime? expiresAt;
                string status;
                string operationKey;

                lock (jobState.SyncRoot)
                {
                    expiresAt = jobState.ExpiresAt;
                    status = jobState.Status;
                    operationKey = jobState.OperationKey;
                }

                if (
                    status != ContainerProductCreationJobStatusConstants.Queued
                    && status != ContainerProductCreationJobStatusConstants.Running
                    && expiresAt.HasValue
                    && expiresAt.Value <= now
                )
                {
                    _jobs.TryRemove(pair.Key, out _);
                    if (_operationJobIds.TryGetValue(operationKey, out var jobId) && jobId == pair.Key)
                    {
                        _operationJobIds.TryRemove(operationKey, out _);
                    }
                }
            }
        }

        private ContainerProductCreationJobDto? GetSnapshot(
            string userId,
            string jobId,
            bool isDuplicateRequest
        )
        {
            if (!_jobs.TryGetValue(jobId, out var jobState))
            {
                return null;
            }

            lock (jobState.SyncRoot)
            {
                if (!string.Equals(jobState.UserId, userId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("无权访问其他用户提交的创建新商品 job");
                }
            }

            return CreateSnapshot(jobState, isDuplicateRequest);
        }

        private static ContainerProductCreationJobDto CreateSnapshot(
            ContainerProductCreationJobState jobState,
            bool isDuplicateRequest
        )
        {
            lock (jobState.SyncRoot)
            {
                return new ContainerProductCreationJobDto
                {
                    JobId = jobState.JobId,
                    Status = jobState.Status,
                    OperationId = jobState.Request.OperationId,
                    Message = jobState.Message,
                    IsDuplicateRequest = isDuplicateRequest,
                    CreatedAt = jobState.CreatedAt,
                    CompletedAt = jobState.CompletedAt,
                    ExpiresAt = jobState.ExpiresAt,
                    Result = CloneResult(jobState.Result),
                };
            }
        }

        private static ContainerProductCreationResultDto CloneResult(
            ContainerProductCreationResultDto result
        )
        {
            return new ContainerProductCreationResultDto
            {
                CreatedCount = result.CreatedCount,
                UpdatedCount = result.UpdatedCount,
                SkippedCount = result.SkippedCount,
                FailedCount = result.FailedCount,
                ContainerCompleted = result.ContainerCompleted,
                Created = result.Created.Select(CloneItem).ToList(),
                Updated = result.Updated.Select(CloneItem).ToList(),
                Skipped = result.Skipped.Select(CloneItem).ToList(),
                Errors = result.Errors.Select(CloneItem).ToList(),
            };
        }

        private static ContainerProductCreationResultItemDto CloneItem(
            ContainerProductCreationResultItemDto item
        )
        {
            return new ContainerProductCreationResultItemDto
            {
                ProductCode = item.ProductCode,
                ItemNumber = item.ItemNumber,
                DetailHguid = item.DetailHguid,
                ReasonCode = item.ReasonCode,
                Message = item.Message,
            };
        }

        private static ContainerProductCreationJobRequestDto NormalizeRequest(
            ContainerProductCreationJobRequestDto? request
        )
        {
            return new ContainerProductCreationJobRequestDto
            {
                OperationId = request?.OperationId?.Trim() ?? string.Empty,
                ContainerGuid = request?.ContainerGuid?.Trim() ?? string.Empty,
                SubmitContainer = request?.SubmitContainer ?? false,
                DetailHguids = (request?.DetailHguids ?? new List<string>())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

        private static string BuildOperationKey(ContainerProductCreationJobRequestDto request)
        {
            if (request.SubmitContainer)
            {
                return $"submit-container:{request.ContainerGuid}";
            }

            var detailPart = request.DetailHguids.Count == 0
                ? "empty"
                : string.Join("|", request.DetailHguids);
            // 幂等键由后端根据业务范围生成，不信任前端 operationId 作为复用条件。
            return $"create-new-products::{request.ContainerGuid}::{detailPart}";
        }

        private static string NormalizeUserId(string? userId)
        {
            return string.IsNullOrWhiteSpace(userId) ? "anonymous" : userId.Trim();
        }

        private static string NormalizeUpdatedBy(string? updatedBy)
        {
            return string.IsNullOrWhiteSpace(updatedBy) ? "System" : updatedBy.Trim();
        }

        private sealed class ContainerProductCreationJobState
        {
            public object SyncRoot { get; } = new();
            public string JobId { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public string UpdatedBy { get; set; } = "System";
            public string OperationKey { get; set; } = string.Empty;
            public ContainerProductCreationJobRequestDto Request { get; set; } = new();
            public string Status { get; set; } = ContainerProductCreationJobStatusConstants.Queued;
            public string? Message { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime? CompletedAt { get; set; }
            public DateTime? ExpiresAt { get; set; }
            public ContainerProductCreationResultDto Result { get; set; } = new();
        }
    }
}
