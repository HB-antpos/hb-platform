using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Reflection;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hbpos.Client.Tests;

[Collection(GlobalLoggingTestCollection.Name)]
public sealed class CentralLoggingTests
{
    [Fact]
    public void ApplicationLogOptions_from_configuration_reads_appsettings_and_environment_override()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_LOG_CENTER_API_KEY"] = "env-key",
            ["HBPOS_LOG_CENTER_QUEUE_CAPACITY"] = "25"
        });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CentralLogging:Enabled"] = "true",
                ["CentralLogging:ApiKey"] = "appsettings-key",
                ["CentralLogging:ProjectCode"] = "hbpos_win",
                ["CentralLogging:Environment"] = "staging",
                ["CentralLogging:SourceType"] = "POS",
                ["CentralLogging:ServiceName"] = "Hbpos.Client.Wpf",
                ["CentralLogging:IngestUrl"] = "https://logs.example.com/api/system/logs/ingest",
                ["CentralLogging:BatchSize"] = "12",
                ["CentralLogging:QueueCapacity"] = "80"
            })
            .AddEnvironmentVariables()
            .Build();

        var options = ApplicationLogOptions.FromConfiguration(
            configuration,
            new Uri("https://pos-api.example.com/"));

        Assert.True(options.Enabled);
        Assert.Equal("env-key", options.ApiKey);
        Assert.Equal("hbpos_win", options.ProjectCode);
        Assert.Equal("staging", options.Environment);
        Assert.Equal("POS", options.SourceType);
        Assert.Equal("Hbpos.Client.Wpf", options.ServiceName);
        Assert.Equal(12, options.BatchSize);
        Assert.Equal(25, options.QueueCapacity);
        Assert.Equal(new Uri("https://logs.example.com/api/system/logs/ingest"), options.IngestUri);
        Assert.True(options.IsConfigured);
    }

    [Fact]
    public void ApplicationLogOptions_uses_build_embedded_key_and_ingest_url_when_store_pc_has_no_configuration()
    {
        // 门店收银机没有配置文件也不设环境变量，正式安装包靠构建时写入的默认值上传中心日志。
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_LOG_CENTER_INGEST_URL"] = null,
            ["HBPOS_LOG_CENTER_API_KEY"] = null,
            ["HBPOS_LOG_CENTER_ENABLED"] = null
        });
        var configuration = new ConfigurationBuilder().Build();
        var buildDefaults = new Dictionary<string, string>
        {
            [ApplicationLogOptions.BuildApiKeyMetadataKey] = "build-key",
            [ApplicationLogOptions.BuildIngestUrlMetadataKey] = "https://hotbargain.vip/api/system/logs/ingest"
        };

        var options = ApplicationLogOptions.FromConfiguration(configuration, new Uri("https://pos-api.example.com/"), buildDefaults);

        Assert.True(options.IsConfigured);
        Assert.Equal("build-key", options.ApiKey);
        Assert.Equal("hbpos_win", options.ProjectCode);
        Assert.Equal(new Uri("https://hotbargain.vip/api/system/logs/ingest"), options.IngestUri);
    }

    [Fact]
    public void ApplicationLogOptions_configuration_and_environment_override_build_defaults()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_LOG_CENTER_INGEST_URL"] = null,
            ["HBPOS_LOG_CENTER_API_KEY"] = "env-key",
            ["HBPOS_LOG_CENTER_ENABLED"] = null
        });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CentralLogging:IngestUrl"] = "https://logs.example.com/api/system/logs/ingest"
            })
            .Build();
        var buildDefaults = new Dictionary<string, string>
        {
            [ApplicationLogOptions.BuildApiKeyMetadataKey] = "build-key",
            [ApplicationLogOptions.BuildIngestUrlMetadataKey] = "https://hotbargain.vip/api/system/logs/ingest"
        };

        var options = ApplicationLogOptions.FromConfiguration(configuration, new Uri("https://pos-api.example.com/"), buildDefaults);

        Assert.Equal("env-key", options.ApiKey);
        Assert.Equal(new Uri("https://logs.example.com/api/system/logs/ingest"), options.IngestUri);

        // 现场可以用环境变量关闭上传，构建默认值不能把它重新打开。
        using var disabled = new EnvironmentVariableScope(new Dictionary<string, string?> { ["HBPOS_LOG_CENTER_ENABLED"] = "false" });
        Assert.False(ApplicationLogOptions.FromConfiguration(configuration, new Uri("https://pos-api.example.com/"), buildDefaults).IsConfigured);
    }

    [Fact]
    public void ApplicationLogOptions_local_and_test_builds_have_no_embedded_key()
    {
        // 只有 CI 安装包构建注入 HbposCenterLogApiKey；测试程序集引用的客户端构建不能带真实 Key。
        var defaults = ApplicationLogOptions.ReadBuildDefaults(typeof(ApplicationLogOptions).Assembly);

        Assert.False(defaults.ContainsKey(ApplicationLogOptions.BuildApiKeyMetadataKey));
    }

    [Theory]
    [InlineData("/api/system/logs/ingest")]
    [InlineData("file:///C:/temp/logs.json")]
    public void ApplicationLogOptions_rejects_non_http_ingest_url(string ingestUrl)
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_LOG_CENTER_INGEST_URL"] = null,
            ["HBPOS_LOG_CENTER_API_KEY"] = null,
            ["HBPOS_LOG_CENTER_ENABLED"] = null
        });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CentralLogging:Enabled"] = "true",
                ["CentralLogging:ApiKey"] = "configured-key",
                ["CentralLogging:IngestUrl"] = ingestUrl
            })
            .Build();

        var options = ApplicationLogOptions.FromConfiguration(
            configuration,
            new Uri("https://pos-api.example.com/"));

        Assert.Null(options.IngestUri);
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void ApplicationLogOptions_defaults_enabled_when_key_and_url_exist()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_LOG_CENTER_ENABLED"] = null,
            ["HBPOS_LOG_CENTER_API_KEY"] = null,
            ["HBPOS_LOG_CENTER_INGEST_URL"] = null
        });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CentralLogging:ApiKey"] = "configured-key",
                ["CentralLogging:IngestUrl"] = "https://logs.example.com/api/system/logs/ingest"
            })
            .Build();

        var options = ApplicationLogOptions.FromConfiguration(configuration, new Uri("https://pos-api.example.com/"));

        Assert.True(options.Enabled);
        Assert.True(options.IsConfigured);
    }

    [Fact]
    public void ApplicationLogOptions_allows_explicit_false_to_disable_upload()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_LOG_CENTER_ENABLED"] = null,
            ["HBPOS_LOG_CENTER_API_KEY"] = null,
            ["HBPOS_LOG_CENTER_INGEST_URL"] = null
        });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CentralLogging:Enabled"] = "false",
                ["CentralLogging:ApiKey"] = "configured-key",
                ["CentralLogging:IngestUrl"] = "https://logs.example.com/api/system/logs/ingest"
            })
            .Build();

        var options = ApplicationLogOptions.FromConfiguration(configuration, new Uri("https://pos-api.example.com/"));

        Assert.False(options.Enabled);
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void OperationAuditUploadOptions_defaults_enabled_and_allows_environment_override()
    {
        var defaultOptions = OperationAuditUploadOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.True(defaultOptions.Enabled);

        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_OPERATION_AUDIT_UPLOAD_ENABLED"] = "false"
        });
        var overridden = OperationAuditUploadOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OperationAuditLogging:Enabled"] = "true"
                })
                .Build());

        Assert.False(overridden.Enabled);
    }

    [Fact]
    public void ConsoleLog_write_enqueues_center_log_entry()
    {
        var sink = new FakeApplicationLogSink();
        ConsoleLog.ConfigureCenterDefaults(ApplicationLogDefaults.Default);
        ConsoleLog.ConfigureCenterSink(sink);

        try
        {
            ConsoleLog.Write("OrderSync", "upload completed orderGuid=abc");

            var entry = Assert.Single(sink.Entries.Where(item =>
                item.Category == "OrderSync" &&
                item.Message == "upload completed orderGuid=abc"));
            Assert.Equal("Information", entry.Level);
            Assert.Equal("upload completed orderGuid=abc", entry.Message);
            Assert.Equal("hbpos_win", entry.ProjectCode);
            Assert.Equal("POS", entry.SourceType);
            Assert.Equal("OrderSync", entry.Category);
            Assert.Equal("OrderSync", entry.ServiceName);
            Assert.NotNull(entry.Properties);
            Assert.Equal("OrderSync", entry.Properties!["category"]);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
            ConsoleLog.ConfigureCenterDefaults(ApplicationLogDefaults.Default);
        }
    }

    [Fact]
    public void Service_registration_uses_one_writer_for_runtime_and_operation_logging()
    {
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], PreviewMode: true, InitialScreen: null, InitialCulture: null));

        using var provider = services.BuildServiceProvider();
        var writer = provider.GetRequiredService<ClientLogOutboxWriter>();

        Assert.Same(writer, provider.GetRequiredService<IApplicationLogSink>());
        Assert.Same(writer, provider.GetRequiredService<IOperationAuditLogger>());
        Assert.Contains(writer, provider.GetServices<IHostedService>());
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is ApplicationLogUploadService);
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is OperationAuditUploadService);
        Assert.StartsWith(Path.GetTempPath(), provider.GetRequiredService<ClientLogOutboxStore>().DatabasePath, StringComparison.OrdinalIgnoreCase);
        var field = typeof(MainViewModel).GetField("_operationAuditLogger", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Same(writer, field!.GetValue(provider.GetRequiredService<MainViewModel>()));
        var authorizationField = typeof(OperationAuditUploadService)
            .GetField("_authorizationState", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Same(
            provider.GetRequiredService<DeviceAuthorizationState>(),
            authorizationField!.GetValue(provider.GetRequiredService<OperationAuditUploadService>()));
    }

    [Fact]
    public void Hosted_service_registration_places_writer_last_so_host_stops_it_before_uploaders()
    {
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], PreviewMode: true, InitialScreen: null, InitialCulture: null));

        using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        var loggingServices = hostedServices
            .Where(service => service is ApplicationLogUploadService or OperationAuditUploadService or ClientLogOutboxWriter)
            .ToArray();

        Assert.Collection(
            loggingServices,
            service => Assert.IsType<ApplicationLogUploadService>(service),
            service => Assert.IsType<OperationAuditUploadService>(service),
            service => Assert.IsType<ClientLogOutboxWriter>(service));
    }

    [Fact]
    public void Service_registration_uses_expected_persistent_log_database_and_operation_upload_switch()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_OPERATION_AUDIT_UPLOAD_ENABLED"] = "false"
        });
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], PreviewMode: false, InitialScreen: null, InitialCulture: null));

        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hbpos.Client",
                "hbpos_logs.db"),
            provider.GetRequiredService<ClientLogOutboxStore>().DatabasePath);
        Assert.False(provider.GetRequiredService<OperationAuditUploadOptions>().Enabled);
        Assert.NotNull(provider.GetRequiredService<IOperationAuditLogger>());
    }

    [Fact]
    public async Task OrderSyncApiClient_http_failure_enqueues_structured_center_log()
    {
        var sink = new FakeApplicationLogSink();
        ConsoleLog.ConfigureCenterDefaults(ApplicationLogDefaults.Default);
        ConsoleLog.ConfigureCenterSink(sink);

        try
        {
            var client = new OrderSyncApiClient(new HttpClient(new StubHttpMessageHandler((_, _) =>
                new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""
                    {"success":false,"message":"sync failed","errorCode":"ORDER_SYNC_FAILED"}
                    """, Encoding.UTF8, "application/json")
                }))
            {
                BaseAddress = new Uri("https://pos-api.example.com/")
            });

            var request = new OrderSyncRequest(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                "S001",
                "POS-01",
                "C001",
                "Alice",
                DateTimeOffset.Parse("2026-06-05T00:00:00Z"),
                10m,
                0m,
                10m,
                [],
                []);

            await Assert.ThrowsAsync<CatalogApiException>(() => client.SyncAsync(request));

            var entry = Assert.Single(sink.Entries.Where(item =>
                item.Level == "Error" &&
                item.ServiceName == "OrderSync" &&
                item.Message.StartsWith("Order sync request failed.", StringComparison.Ordinal)));
            // 消息现在附带 http/errorCode/服务端 message/attempt，便于直接在中心日志定位原因。
            Assert.Contains("http=500 errorCode=ORDER_SYNC_FAILED message=sync failed attempt=1", entry.Message);
            Assert.Equal("OrderSync", entry.ServiceName);
            Assert.Equal("POST", entry.RequestMethod);
            Assert.Equal("/api/v1/orders/sync", entry.RequestPath);
            Assert.Equal(500, entry.StatusCode);
            Assert.NotNull(entry.Properties);
            Assert.Equal("S001", entry.Properties!["storeCode"]);
            Assert.Equal("POS-01", entry.Properties["deviceCode"]);
            Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", entry.TraceId);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
            ConsoleLog.ConfigureCenterDefaults(ApplicationLogDefaults.Default);
        }
    }

    private sealed class FakeApplicationLogSink : IApplicationLogSink
    {
        private readonly ConcurrentQueue<ApplicationLogEntry> _entries = new();

        public IReadOnlyList<ApplicationLogEntry> Entries => _entries.ToArray();

        public void Enqueue(ApplicationLogEntry entry)
        {
            _entries.Enqueue(entry);
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request, cancellationToken));
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues = new(StringComparer.OrdinalIgnoreCase);

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var entry in values)
            {
                _originalValues[entry.Key] = Environment.GetEnvironmentVariable(entry.Key);
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }

        public void Dispose()
        {
            foreach (var entry in _originalValues)
            {
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }
    }
}
