using BlazorApp.Api.Services.Background;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Features.Notifications;

/// <summary>
/// App 推送后台任务：按 <c>Push:Expo:PollIntervalSeconds</c> 轮询发件箱，驱动 <see cref="ExpoPushSender"/> 发送、取回执，
/// 每小时清理一次过期记录。每个节拍开独立 DI 作用域。
/// <para>
/// 双层开关：① <c>ScheduledTasks:Enabled=false</c>（进程不参与定时任务调度，例如临时实例）→ 本任务启动即退出，不进循环；
/// ② <c>Push:Expo:Enabled=false</c>（默认）→ 每个节拍直接返回，不创建作用域、不查库、不发任何请求。
/// 注意其他通知后台任务（人脸 / 改价 / 新品）并不检查 <c>ScheduledTasks:Enabled</c>，本任务自己检查。
/// </para>
/// <para>
/// 失败处理：节拍内的异常（数据库不可用、迁移未执行导致表不存在、配置错误）不会让任务退出，
/// 只按「同类异常 10 分钟最多记一条」限频写日志（只记类型名与固定文案，不记异常 Message），并把下个节拍推迟到至少 30 秒后，
/// 避免迁移未执行时每 5 秒刷一条错误。
/// </para>
/// </summary>
internal sealed class ExpoPushBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ExpoPushOptions> options,
    IOptions<ScheduledTaskOptions> scheduledTaskOptions,
    TimeProvider timeProvider,
    ILogger<ExpoPushBackgroundService> logger
) : BackgroundService
{
    /// <summary>启动后的等待时间，避开启动迁移与缓存预热的高峰；测试可缩短。</summary>
    internal TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>失败后的最短等待。</summary>
    internal static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    /// <summary>清理任务的执行间隔。</summary>
    internal static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    /// <summary>同类失败日志的最小间隔。</summary>
    internal static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(10);

    private DateTimeOffset _nextCleanupAt = DateTimeOffset.MinValue;
    private readonly Dictionary<string, DateTimeOffset> _lastFailureLoggedAt = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!scheduledTaskOptions.Value.Enabled)
        {
            logger.LogInformation("App 推送后台任务已跳过：本进程未参与定时任务调度（ScheduledTasks:Enabled=false）");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, timeProvider, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = await TickAsync(stoppingToken);
            try
            {
                await Task.Delay(wait, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>跑一个节拍并返回下次等待时间。公开给测试直接驱动（配合假的 TimeProvider）。</summary>
    internal async Task<TimeSpan> TickAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        var pollInterval = TimeSpan.FromSeconds(current.EffectivePollIntervalSeconds);
        if (!current.Enabled)
        {
            // 默认关闭：不创建作用域、不查库、不发请求。
            return pollInterval;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ExpoPushSender>();
            await sender.RunOnceAsync(cancellationToken);

            var now = timeProvider.GetUtcNow();
            if (now >= _nextCleanupAt)
            {
                await sender.CleanupAsync(cancellationToken);
                _nextCleanupAt = now + CleanupInterval;
            }

            return pollInterval;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return pollInterval;
        }
        catch (Exception ex)
        {
            LogFailureThrottled(ex);
            return pollInterval > FailureBackoff ? pollInterval : FailureBackoff;
        }
    }

    private void LogFailureThrottled(Exception exception)
    {
        string category;
        string detail;
        if (exception is ExpoPushConfigurationException configurationException)
        {
            category = "configuration";
            detail = configurationException.Message;   // 固定文案，不含配置值
        }
        else if (SqlErrorClassifier.IsMissingObject(exception))
        {
            category = "schema-missing";
            detail = "AppPushDelivery / AppPushDevice 表不存在，请先带 --schema=migrate 执行迁移 20261017.001-app-push";
        }
        else
        {
            category = exception.GetType().Name;
            detail = "详见异常类型；异常消息可能带地址、SQL 或路径，未写入日志";
        }

        var now = timeProvider.GetUtcNow();
        if (_lastFailureLoggedAt.TryGetValue(category, out var last) && now - last < FailureLogInterval)
        {
            return;
        }

        _lastFailureLoggedAt[category] = now;
        // 不把 exception 对象传给日志：其 Message / StackTrace 可能带 SQL 参数（含推送令牌）或服务器地址。
        logger.LogError("App 推送后台任务本轮失败，稍后重试: Category={Category}, Detail={Detail}", category, detail);
    }
}
