using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Client.Tests;

public sealed class ServiceRegistrationReceiptProfileTests
{
    [Fact]
    public void AddHbposClientServices_shares_one_receipt_settings_store_between_print_and_profile_sync()
    {
        var services = new ServiceCollection();
        services.AddHbposClientServices(
            new AppStartupOptions([], PreviewMode: true, InitialScreen: null, InitialCulture: null));

        using var provider = services.BuildServiceProvider();

        // 保存设置页与后台同步写入共用同一个进程内互斥，所以三种解析方式必须是同一个单例。
        var concrete = provider.GetRequiredService<ReceiptPrinterSettingsStore>();
        Assert.Same(concrete, provider.GetRequiredService<IReceiptPrinterSettingsStore>());
        Assert.Same(concrete, provider.GetRequiredService<IReceiptProfileLocalStore>());
    }

    [Fact]
    public void AddHbposClientServices_registers_the_receipt_profile_sync_service_as_a_singleton()
    {
        var services = new ServiceCollection();
        services.AddHbposClientServices(
            new AppStartupOptions([], PreviewMode: true, InitialScreen: null, InitialCulture: null));

        using var provider = services.BuildServiceProvider();

        var sync = provider.GetRequiredService<IReceiptProfileSyncService>();
        Assert.IsType<ReceiptProfileSyncService>(sync);
        Assert.Same(sync, provider.GetRequiredService<IReceiptProfileSyncService>());
    }
}
