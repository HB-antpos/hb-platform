namespace BlazorApp.FaceIngest;

/// <summary>
/// 后台定期清理：启动后先跑一轮，之后每 CleanupIntervalMinutes 分钟一轮（周期支持热更新）。
/// 整轮异常只记 Error，不让服务崩溃（BackgroundService 未处理异常默认会停掉整个宿主）。
/// </summary>
internal sealed class CaptureStorageCleanupService : BackgroundService
{
    private readonly CaptureStorageCleaner _cleaner;
    private readonly FaceIngestSettingsMonitor _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CaptureStorageCleanupService> _logger;

    public CaptureStorageCleanupService(
        CaptureStorageCleaner cleaner,
        FaceIngestSettingsMonitor settings,
        TimeProvider timeProvider,
        ILogger<CaptureStorageCleanupService> logger)
    {
        _cleaner = cleaner;
        _settings = settings;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 中文注释：先让出线程，清理里的同步文件 IO 不阻塞宿主启动。
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _cleaner.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Face ingest cleanup round failed; will retry next interval.");
            }

            var interval = TimeSpan.FromMinutes(Math.Max(1, _settings.Current.CleanupIntervalMinutes));
            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
