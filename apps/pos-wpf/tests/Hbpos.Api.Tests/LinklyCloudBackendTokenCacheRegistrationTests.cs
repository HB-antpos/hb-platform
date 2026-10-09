using Hbpos.Api;
using Hbpos.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Api.Tests;

public sealed class LinklyCloudBackendTokenCacheRegistrationTests
{
    [Fact]
    public void AddHbposApiServices_registers_one_process_wide_linkly_token_cache()
    {
        // Token Provider 是按请求创建的类型化 HttpClient：缓存若跟着它走，每次状态轮询仍会换一次 token（M1）。
        var services = new ServiceCollection();

        services.AddHbposApiServices();

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(LinklyCloudBackendTokenCache));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }
}
