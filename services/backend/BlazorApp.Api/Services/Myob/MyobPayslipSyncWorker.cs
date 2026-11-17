using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Services.Myob;

/// <summary>
/// MYOB 工资单后台同步：每分钟看一眼哪些公司文件「到期」，到期的才去拉 MYOB。
/// 到期规则见 <see cref="MyobPayslipSyncSchedule.IsDue"/>：悉尼本地 0 / 8 / 12 / 15 / 18 / 21 点整点之后还没成功同步过就同步，
/// 首次部署后历史没回填完也会同步（整段 24 个月）；失败后隔 15 分钟再试。所以容器重启、错过整点、MYOB 一时不可用都会自己补上。
/// 同一家公司同一时刻只有一个同步（库里的租约），与手动「同步」按钮、新旧容器重叠互不冲突。
/// 只在 MYOB 开发者应用已配置（ClientId / ClientSecret / RedirectUri 齐全）时运行：没有密钥的开发机不会去碰生产库里的授权。
/// 配置节 MyobPayslipSync:Enabled=false 可临时停掉后台任务（运维用）。
/// </summary>
internal sealed class MyobPayslipSyncWorker : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<MyobOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MyobPayslipSyncWorker> _logger;

    public MyobPayslipSyncWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<MyobOptions> options,
        IConfiguration configuration,
        ILogger<MyobPayslipSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("MyobPayslipSync:Enabled", true))
        {
            return;
        }

        if (!_options.Value.IsConfigured)
        {
            _logger.LogInformation("MYOB 开发者应用未配置，工资单后台同步不启动");
            return;
        }

        // 启动后稍等，避开启动迁移检查与缓存预热的高峰。
        await DelayAsync(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<IMyobPayslipSyncService>();
                var due = await sync.FindDueConnectionsAsync(stoppingToken);
                if (due.Count > 0)
                {
                    var outcomes = await sync.SyncAsync(due, MyobPayslipSyncTrigger.Scheduled, stoppingToken);
                    _logger.LogInformation(
                        "MYOB 工资单后台同步：{Total} 家公司，成功 {Synced}，失败 {Failed}，别处正在同步 {Busy}",
                        outcomes.Count,
                        outcomes.Count(item => item.Status == MyobPayslipSyncStatuses.Synced),
                        outcomes.Count(item => item.Status == MyobPayslipSyncStatuses.Failed),
                        outcomes.Count(item => item.Status == MyobPayslipSyncStatuses.Busy));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 只记异常类型：数据库异常可能带出被截断的值（含薪资）。
                _logger.LogError("MYOB 工资单后台同步失败 Error={ErrorType}", ex.GetType().Name);
            }

            await DelayAsync(CheckInterval, stoppingToken);
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
            // 停机信号：由外层循环条件退出。
        }
    }
}
