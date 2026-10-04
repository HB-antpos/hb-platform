using System.Collections.Concurrent;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 分店订货发票邮件后台发送 job 服务。
    /// </summary>
    public class StoreOrderInvoiceEmailJobService : IStoreOrderInvoiceEmailJobService
    {
        private static readonly TimeSpan DefaultCompletedRetention = TimeSpan.FromMinutes(45);

        private readonly ConcurrentDictionary<string, StoreOrderInvoiceEmailJobState> _jobs = new();
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<StoreOrderInvoiceEmailJobService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _completedRetention;

        public StoreOrderInvoiceEmailJobService(
            IServiceScopeFactory serviceScopeFactory,
            ILogger<StoreOrderInvoiceEmailJobService> logger
        )
            : this(serviceScopeFactory, logger, TimeProvider.System, DefaultCompletedRetention) { }

        public StoreOrderInvoiceEmailJobService(
            IServiceScopeFactory serviceScopeFactory,
            ILogger<StoreOrderInvoiceEmailJobService> logger,
            TimeProvider? timeProvider = null,
            TimeSpan? completedRetention = null
        )
        {
            _serviceScopeFactory = serviceScopeFactory;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _completedRetention = completedRetention ?? DefaultCompletedRetention;
        }

        public Task<StoreOrderInvoiceEmailJobDto> StartJobAsync(
            SendStoreOrderInvoiceEmailDto request,
            CancellationToken cancellationToken = default
        )
        {
            CleanupExpiredJobs();

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var jobState = new StoreOrderInvoiceEmailJobState
            {
                JobId = Guid.NewGuid().ToString("N"),
                Request = CloneRequest(request),
                OrderGUID = request.OrderGUID?.Trim() ?? string.Empty,
                ToEmail = request.ToEmail?.Trim() ?? string.Empty,
                CreatedAt = now,
                Status = StoreOrderInvoiceEmailJobStatusConstants.Queued,
                Message = "发票邮件发送任务已提交",
            };

            _jobs[jobState.JobId] = jobState;

            // 关键位置：后台任务重新创建作用域，避免复用请求结束后的 scoped 服务。
            _ = Task.Run(() => ExecuteJobAsync(jobState), CancellationToken.None);
            return Task.FromResult(CreateSnapshot(jobState));
        }

        public Task<StoreOrderInvoiceEmailJobDto?> GetJobAsync(
            string jobId,
            CancellationToken cancellationToken = default
        )
        {
            CleanupExpiredJobs();

            if (string.IsNullOrWhiteSpace(jobId))
            {
                return Task.FromResult<StoreOrderInvoiceEmailJobDto?>(null);
            }

            return Task.FromResult(
                _jobs.TryGetValue(jobId.Trim(), out var jobState)
                    ? CreateSnapshot(jobState)
                    : null
            );
        }

        private async Task ExecuteJobAsync(StoreOrderInvoiceEmailJobState jobState)
        {
            try
            {
                SetRunning(jobState);

                using var scope = _serviceScopeFactory.CreateScope();
                var attachmentService =
                    scope.ServiceProvider.GetRequiredService<IStoreOrderInvoiceAttachmentService>();
                var invoiceEmailService = scope.ServiceProvider.GetRequiredService<IInvoiceEmailService>();
                var attachmentResult = await attachmentService.GenerateAttachmentsAsync(
                    jobState.Request.OrderGUID
                );
                if (!attachmentResult.Success || attachmentResult.Data == null)
                {
                    CompleteJob(
                        jobState,
                        StoreOrderInvoiceEmailJobStatusConstants.Failed,
                        string.IsNullOrWhiteSpace(attachmentResult.Message)
                            ? "生成发票附件失败"
                            : attachmentResult.Message
                    );
                    return;
                }

                var bundle = attachmentResult.Data;
                var subject = string.IsNullOrWhiteSpace(jobState.Request.Subject)
                    ? $"分店订货发票 {bundle.OrderNo ?? bundle.OrderGUID}"
                    : jobState.Request.Subject.Trim();
                var body = string.IsNullOrWhiteSpace(jobState.Request.Body)
                    ? "您好，附件为本次分店订货发票，请查收。"
                    : jobState.Request.Body.Trim();
                var result = await invoiceEmailService.SendInvoiceAsync(
                    new StoreOrderInvoiceEmailMessage
                    {
                        ToEmail = jobState.Request.ToEmail.Trim(),
                        Subject = subject,
                        Body = body,
                        Attachments = bundle.Attachments,
                        FromAccountId = jobState.Request.FromAccountId,
                    }
                );

                if (result.Success)
                {
                    await TryPersistSendRecordAsync(scope.ServiceProvider, jobState);
                }

                CompleteJob(
                    jobState,
                    result.Success
                        ? StoreOrderInvoiceEmailJobStatusConstants.Succeeded
                        : StoreOrderInvoiceEmailJobStatusConstants.Failed,
                    string.IsNullOrWhiteSpace(result.Message)
                        ? result.Success
                            ? "发票邮件发送成功"
                            : "发票邮件发送失败"
                        : result.Message
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行分店订货发票邮件发送 job 失败: {JobId}", jobState.JobId);
                CompleteJob(
                    jobState,
                    StoreOrderInvoiceEmailJobStatusConstants.Failed,
                    string.IsNullOrWhiteSpace(ex.Message) ? "发票邮件发送失败" : ex.Message
                );
            }
        }

        private async Task TryPersistSendRecordAsync(IServiceProvider serviceProvider, StoreOrderInvoiceEmailJobState jobState)
        {
            try
            {
                var context = serviceProvider.GetRequiredService<SqlSugarContext>();
                var sentAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

                // 邮件发送成功后仅补写本地发送记录；写库失败不能反向影响已发送结果。
                await context.Db.Insertable(
                    new StoreOrderInvoiceEmailSendRecord
                    {
                        StoreOrderUuid = jobState.OrderGUID,
                        ToEmail = jobState.ToEmail,
                        SentAtUtc = sentAtUtc,
                        JobId = jobState.JobId,
                        CreatedAtUtc = sentAtUtc,
                    }
                ).ExecuteCommandAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "发票邮件已发送，但写入发送记录失败: {JobId}, OrderGUID={OrderGUID}",
                    jobState.JobId,
                    jobState.OrderGUID
                );
            }
        }

        private void SetRunning(StoreOrderInvoiceEmailJobState jobState)
        {
            lock (jobState.SyncRoot)
            {
                jobState.Status = StoreOrderInvoiceEmailJobStatusConstants.Running;
                jobState.Message = "发票邮件发送中";
            }
        }

        private void CompleteJob(
            StoreOrderInvoiceEmailJobState jobState,
            string status,
            string message
        )
        {
            lock (jobState.SyncRoot)
            {
                jobState.Status = status;
                jobState.Message = message;
                jobState.CompletedAt = _timeProvider.GetUtcNow().UtcDateTime;
            }
        }

        private void CleanupExpiredJobs()
        {
            var threshold = _timeProvider.GetUtcNow().UtcDateTime - _completedRetention;
            foreach (var pair in _jobs)
            {
                var completedAt = pair.Value.CompletedAt;
                if (completedAt.HasValue && completedAt.Value < threshold)
                {
                    _jobs.TryRemove(pair.Key, out _);
                }
            }
        }

        private static SendStoreOrderInvoiceEmailDto CloneRequest(SendStoreOrderInvoiceEmailDto request)
        {
            return new SendStoreOrderInvoiceEmailDto
            {
                OrderGUID = request.OrderGUID,
                ToEmail = request.ToEmail,
                Subject = request.Subject,
                Body = request.Body,
                FromAccountId = string.IsNullOrWhiteSpace(request.FromAccountId)
                    ? null
                    : request.FromAccountId.Trim(),
            };
        }

        private static StoreOrderInvoiceEmailJobDto CreateSnapshot(
            StoreOrderInvoiceEmailJobState jobState
        )
        {
            lock (jobState.SyncRoot)
            {
                return new StoreOrderInvoiceEmailJobDto
                {
                    JobId = jobState.JobId,
                    Status = jobState.Status,
                    Message = jobState.Message,
                    OrderGUID = jobState.OrderGUID,
                    ToEmail = jobState.ToEmail,
                    CreatedAt = jobState.CreatedAt,
                    CompletedAt = jobState.CompletedAt,
                };
            }
        }

        private sealed class StoreOrderInvoiceEmailJobState
        {
            public object SyncRoot { get; } = new();
            public string JobId { get; init; } = string.Empty;
            public SendStoreOrderInvoiceEmailDto Request { get; init; } = new();
            public string OrderGUID { get; init; } = string.Empty;
            public string ToEmail { get; init; } = string.Empty;
            public string Status { get; set; } = StoreOrderInvoiceEmailJobStatusConstants.Queued;
            public string Message { get; set; } = string.Empty;
            public DateTime CreatedAt { get; init; }
            public DateTime? CompletedAt { get; set; }
        }
    }
}
