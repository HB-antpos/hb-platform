using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Client.Tests;

/// <summary>依赖注入装配：真实 ServiceRegistration（含 WPF 外壳依赖），只能在 Windows CI 上运行。</summary>
public sealed class DailyCloseUploadRegistrationTests
{
    [Fact]
    public void Service_registration_wires_the_daily_close_upload_pipeline_with_shared_singletons()
    {
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], PreviewMode: true, InitialScreen: null, InitialCulture: null));
        using var provider = services.BuildServiceProvider();

        // 日结仓储的三个服务类型必须是同一个单例（共用同一个 LocalSqliteStore）。
        var repository = provider.GetRequiredService<LocalDailyCloseRepository>();
        Assert.Same(repository, provider.GetRequiredService<ILocalDailyCloseRepository>());
        Assert.Same(repository, provider.GetRequiredService<ILocalDailyCloseUploadRepository>());

        // Worker 同时是唤醒入口；上传服务只有一个实例。
        var worker = provider.GetRequiredService<DailyCloseUploadWorker>();
        Assert.Same(worker, provider.GetRequiredService<IDailyCloseUploadScheduler>());
        Assert.Same(
            provider.GetRequiredService<DailyCloseUploadService>(),
            provider.GetRequiredService<IDailyCloseUploadExecutionService>());

        // 带设备授权 handler 的 API 客户端与日结服务都能解析（保存日结后唤醒用的就是上面那个 Worker）。
        Assert.NotNull(provider.GetRequiredService<IDailyCloseSyncApiClient>());
        Assert.IsType<DailyCloseService>(provider.GetRequiredService<IDailyCloseService>());
    }
}
