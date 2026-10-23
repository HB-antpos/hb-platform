using BlazorApp.Api.Data;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>
/// 供应商节日预订单去年历史的后台任务：每分钟看一次有没有到期的订单表（排队中的、新建还没算过的、
/// 进行中且截止前每晚悉尼 3 点后还没刷新的），逐张计算快照。状态存在快照表的表头行里，进程重启不丢。
/// 同一循环里顺带做截止前 72 / 24 小时的「提醒未提交分店」。配置 SupplierPreorders:HistoryWorkerEnabled = false 可关闭（两者一起关）。
/// </summary>
public sealed class SupplierPreorderHistoryWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SupplierPreorderHistoryWorker> _logger;

    public SupplierPreorderHistoryWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<SupplierPreorderHistoryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("SupplierPreorders:HistoryWorkerEnabled", true))
        {
            return;
        }

        // 启动后稍等，避开启动迁移检查与缓存预热的高峰
        await DelayAsync(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<SqlSugarContext>();
                var service = new SupplierPreorderHistoryService(context.Db, _logger);
                await service.RunDueAsync(stoppingToken);

                // 截止前 72 / 24 小时提醒未提交的分店（幂等键防重复，每分钟检查一次即可）
                var publisher = scope.ServiceProvider.GetService<BlazorApp.Api.Features.Notifications.INotificationPublisher>();
                if (publisher is not null)
                {
                    await new SupplierPreorderReminderService(context.Db, publisher, _logger).RunAutoAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "供应商预订单去年历史后台任务失败");
            }

            await DelayAsync(PollInterval, stoppingToken);
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 停机信号：由外层循环条件退出
        }
    }
}
