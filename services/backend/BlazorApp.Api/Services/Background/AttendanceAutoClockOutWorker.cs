using BlazorApp.Api.Interfaces.React;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlazorApp.Api.Services.Background;

/// <summary>
/// 午夜自动下班的定时触发器：每隔几分钟检查一次，门店时区过了 00:00 仍未闭合的班段由服务补系统下班卡。
/// 具体规则（不计薪、生成漏下班、结案不重开）在 AttendanceReactService.RunMidnightAutoClockOutAsync。
/// 总开关在考勤设置 AutoClockOutAtMidnight；这里的配置节只用于运维临时停掉后台任务。
/// </summary>
public sealed class AttendanceAutoClockOutWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AttendanceAutoClockOutWorker> _logger;

    public AttendanceAutoClockOutWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<AttendanceAutoClockOutWorker> logger
    )
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("AttendanceAutoClockOut:Enabled", true))
        {
            return;
        }

        var intervalMinutes = Math.Max(1, _configuration.GetValue("AttendanceAutoClockOut:IntervalMinutes", 5));
        // 启动后稍等，避开启动迁移检查与缓存预热的高峰。
        await DelayAsync(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IAttendanceReactService>();
                var created = await service.RunMidnightAutoClockOutAsync(stoppingToken);
                if (created > 0)
                {
                    _logger.LogInformation("本轮午夜自动下班补卡 {Count} 张", created);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "午夜自动下班定时任务失败");
            }

            await DelayAsync(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
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
