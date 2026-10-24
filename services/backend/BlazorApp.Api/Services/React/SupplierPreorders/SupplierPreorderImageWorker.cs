using BlazorApp.Api.Data;
using BlazorApp.Api.Features.ProductDescriptions;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>
/// 新品图片转存 COS 的后台任务：有待办就一批接一批地处理（每批 30 张），没有就隔 2 分钟再看。
/// 状态都在 SupplierPreorderFormItem 的转存列里，进程重启不丢。配置 SupplierPreorders:ImageCopyEnabled = false 可关闭。
/// </summary>
public sealed class SupplierPreorderImageWorker : BackgroundService
{
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SupplierPreorderImageWorker> _logger;

    public SupplierPreorderImageWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<SupplierPreorderImageWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("SupplierPreorders:ImageCopyEnabled", true))
        {
            return;
        }

        await DelayAsync(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = new SupplierPreorderImageCopyService(
                    scope.ServiceProvider.GetRequiredService<SqlSugarContext>().Db,
                    scope.ServiceProvider.GetRequiredService<IProductImageFetcher>(),
                    scope.ServiceProvider.GetRequiredService<ISupplierPreorderImageStore>(),
                    _logger);
                processed = await service.RunBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "供应商预订单图片转存后台任务失败");
            }

            await DelayAsync(processed > 0 ? BusyInterval : IdleInterval, stoppingToken);
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
