using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Models.HBweb;

namespace BlazorApp.Api.Services
{
    public class MobileAppBuildMirrorBackgroundService : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan StaleRunningAfter = TimeSpan.FromMinutes(30);
        private const int MaxAttempts = 3;
        // 补算与镜像共用 CosMirrorAttempts 计数：成功镜像后的剩余次数即补算重试额度，两次尝试至少间隔 30 分钟。
        private const int ChecksumBackfillMaxAttempts = 6;
        private static readonly TimeSpan ChecksumBackfillRetryAfter = TimeSpan.FromMinutes(30);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MobileAppBuildMirrorBackgroundService> _logger;

        public MobileAppBuildMirrorBackgroundService(
            IServiceScopeFactory scopeFactory,
            ILogger<MobileAppBuildMirrorBackgroundService> logger
        )
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessOneAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "APK COS 镜像后台任务执行失败");
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }

        internal async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var mirrorQueue = scope.ServiceProvider.GetRequiredService<IMobileAppBuildMirrorQueue>();
            var mirror = scope.ServiceProvider.GetRequiredService<IMobileAppBuildArtifactMirror>();
            var job = await mirrorQueue.ClaimNextCosMirrorJobAsync(
                DateTime.UtcNow,
                MaxAttempts,
                StaleRunningAfter
            );

            if (job == null)
            {
                // 镜像队列空闲时才补算，新构建的镜像优先。
                return await BackfillOneChecksumAsync(mirrorQueue, mirror, cancellationToken);
            }

            try
            {
                var result = await mirror.MirrorAsync(job, cancellationToken);
                await mirrorQueue.CompleteCosMirrorSuccessAsync(job, result);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 关键位置：宿主关闭属于正常取消，不能把任务错误地回写成镜像失败。
                throw;
            }
            catch (Exception ex)
            {
                LogMirrorFailure(job, ex);
                await mirrorQueue.CompleteCosMirrorFailureAsync(job, ex);
                return true;
            }
        }

        private async Task<bool> BackfillOneChecksumAsync(
            IMobileAppBuildMirrorQueue mirrorQueue,
            IMobileAppBuildArtifactMirror mirror,
            CancellationToken cancellationToken
        )
        {
            var job = await mirrorQueue.ClaimNextCosChecksumBackfillJobAsync(
                DateTime.UtcNow,
                ChecksumBackfillMaxAttempts,
                ChecksumBackfillRetryAfter
            );
            if (job == null)
            {
                return false;
            }

            try
            {
                var checksum = await mirror.ComputeMirroredChecksumAsync(job, cancellationToken);
                await mirrorQueue.CompleteCosChecksumBackfillSuccessAsync(job, checksum);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "APK COS 校验值补算失败，EasBuildId: {EasBuildId}, Attempts: {Attempts}",
                    job.EasBuildId,
                    job.CosMirrorAttempts
                );
                await mirrorQueue.CompleteCosChecksumBackfillFailureAsync(job, ex);
                return true;
            }
        }

        private void LogMirrorFailure(MobileAppBuild job, Exception ex)
        {
            if (ex is MobileAppBuildArtifactMirrorException { IsDownloadUnsafe: true })
            {
                _logger.LogWarning(
                    ex,
                    "APK COS 镜像判定为不安全，EasBuildId: {EasBuildId}",
                    job.EasBuildId
                );
                return;
            }

            _logger.LogWarning(
                ex,
                "APK COS 镜像失败，EasBuildId: {EasBuildId}, Attempts: {Attempts}",
                job.EasBuildId,
                job.CosMirrorAttempts
            );
        }
    }
}
