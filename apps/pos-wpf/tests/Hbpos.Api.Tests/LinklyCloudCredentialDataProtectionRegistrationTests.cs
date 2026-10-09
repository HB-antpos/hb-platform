using BlazorApp.Shared.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Api.Tests;

public sealed class LinklyCloudCredentialDataProtectionRegistrationTests
{
    private const string ContainerKeysPath =
        "/app/App_Data/LinklyCloudCredentialDataProtectionKeys";
    private const string HostKeysPathExpression =
        "${LINKLY_CLOUD_CREDENTIAL_DATA_PROTECTION_KEYS_HOST_PATH:?required}";

    [Fact]
    public void Production_without_Linkly_keys_path_fails_closed()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["AttendanceQrDataProtection:KeysPath"] = Path.Combine(
                    Path.GetTempPath(),
                    "hbpos-attendance-tests",
                    Guid.NewGuid().ToString("N"))
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddHbposApiServices(configuration));

        Assert.Equal(
            "Production requires LinklyCloudCredentialDataProtection:KeysPath.",
            exception.Message);
    }

    [Fact]
    public void Development_registers_shared_Linkly_credential_protector()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddHbposApiServices(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ILinklyCloudTerminalCredentialProtector>());
    }

    [Fact]
    public void Development_without_configured_path_uses_the_directory_shared_with_Admin()
    {
        // M8：两个进程的内容根不同，各自回落到“程序目录/App_Data”会让本机密钥环永远不一致。
        var sharedPath = LinklyCloudTerminalCredentialDataProtection.ResolveSharedDevelopmentKeysPath();
        Assert.NotNull(sharedPath);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHbposApiServices(configuration);
        using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<ILinklyCloudTerminalCredentialProtector>().ProtectPassword("probe");

        Assert.True(Directory.Exists(sharedPath));
        Assert.NotEmpty(Directory.GetFiles(sharedPath!, "key-*.xml"));
    }

    [Fact]
    public async Task Pos_compose_uses_required_shared_host_mount()
    {
        var composePath = Path.Combine(
            FindRepoRoot(),
            "apps/pos-wpf/docker-compose.hotbargain.yml");
        var lines = await File.ReadAllLinesAsync(composePath);

        Assert.Equal(
            ContainerKeysPath,
            GetConfiguredValue(lines, "LinklyCloudCredentialDataProtection__KeysPath"));
        Assert.Equal(
            HostKeysPathExpression,
            GetHostPath(lines, ContainerKeysPath));
    }

    private static string GetConfiguredValue(IEnumerable<string> lines, string settingName)
    {
        var prefix = $"- {settingName}=";
        var setting = Assert.Single(
            lines.Select(line => line.Trim()),
            line => line.StartsWith(prefix, StringComparison.Ordinal));
        return setting[prefix.Length..];
    }

    private static string GetHostPath(IEnumerable<string> lines, string containerPath)
    {
        var suffix = $":{containerPath}";
        var volume = Assert.Single(
            lines.Select(line => line.Trim()),
            line => line.StartsWith("- ", StringComparison.Ordinal)
                && line.EndsWith(suffix, StringComparison.Ordinal));
        return volume[2..^suffix.Length];
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps"))
                && Directory.Exists(Path.Combine(directory.FullName, "services")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("找不到仓库根目录");
    }
}
