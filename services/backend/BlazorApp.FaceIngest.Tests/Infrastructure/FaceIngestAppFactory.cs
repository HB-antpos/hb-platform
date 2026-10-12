using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlazorApp.FaceIngest.Tests.Infrastructure;

/// <summary>工厂可调项：配置键值、是否带默认摄像机、热更新配置源、cameras.json 内容、是否启用后台清理。</summary>
internal sealed class FaceIngestAppOptions
{
    public Dictionary<string, string?> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>为 false 时不往内存配置里放默认摄像机（测试自己提供 cameras.json 或热更新源）。</summary>
    public bool IncludeDefaultCameras { get; set; } = true;

    public ReloadableTestConfigurationSource? ReloadableSource { get; set; }

    /// <summary>启动前写入配置目录的 cameras.json 内容。</summary>
    public string? CamerasJson { get; set; }

    /// <summary>默认移除后台清理服务，避免它与测试并发扫描、改写统计；清理测试直接调用 RunOnceAsync。</summary>
    public bool EnableCleanupService { get; set; }

    /// <summary>
    /// 默认移除 P1 后台处理服务：它会在 raw/ 旁写 .processed 等标记、在 images/ 写图，P0 测试按文件清单断言；
    /// 处理器测试直接调用 <see cref="CaptureProcessor.RunOnceAsync"/>。
    /// </summary>
    public bool EnableProcessingService { get; set; }

    /// <summary>默认移除数据库摄像机来源的后台刷新；测试直接调用 <see cref="DatabaseCameraSource.RefreshAsync"/> 控制时序。</summary>
    public bool EnableCameraRefreshService { get; set; }

    /// <summary>额外的服务替换（在框架默认替换之后执行），例如注入假仓储 / 假写库器 / 假图片存储。</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>
    /// 为每个请求设置的连接层对端地址。TestServer 默认是 null；生产容器里看到的是 Docker 网桥网关
    /// （如 172.18.0.1），需要模拟这一点时设置它。
    /// </summary>
    public IPAddress? RemoteIpAddress { get; set; }
}

/// <summary>
/// 每个测试一个独立实例：临时存储目录 + 临时配置目录 + 可控时钟 + 可控剩余空间 + 日志捕获。
/// 默认 CaptureEnabled=true，登记两台摄像机：启用的 <see cref="FrontCamera"/> 与停用的 <see cref="DisabledCamera"/>。
/// </summary>
internal sealed class FaceIngestAppFactory : WebApplicationFactory<Program>
{
    public static readonly DateTimeOffset DefaultUtcNow = new(2026, 10, 12, 1, 2, 3, 456, TimeSpan.Zero);

    private readonly FaceIngestAppOptions _options = new();

    public FaceIngestAppFactory(Action<FaceIngestAppOptions>? configure = null)
    {
        TempRoot = Path.Combine(Path.GetTempPath(), "face-ingest-tests", Guid.NewGuid().ToString("N"));
        StorageRoot = Path.Combine(TempRoot, "storage");
        ConfigDirectory = Path.Combine(TempRoot, "config");
        Directory.CreateDirectory(ConfigDirectory);

        _options.Settings["FaceIngest:CaptureEnabled"] = "true";
        _options.Settings["FaceIngest:StorageRootPath"] = StorageRoot;
        configure?.Invoke(_options);

        if (_options.IncludeDefaultCameras)
        {
            foreach (var pair in TestCamera.ToSettings(FrontCamera, DisabledCamera))
            {
                _options.Settings.TryAdd(pair.Key, pair.Value);
            }
        }

        if (_options.CamerasJson is not null)
        {
            File.WriteAllText(Path.Combine(ConfigDirectory, "cameras.json"), _options.CamerasJson);
        }
    }

    public TestCamera FrontCamera { get; } = TestCamera.Create("cam-front-01", "S1001");

    public TestCamera DisabledCamera { get; } = TestCamera.Create("cam-back-02", "S1001", enabled: false);

    public string TempRoot { get; }

    public string StorageRoot { get; }

    public string RawRoot => Path.Combine(StorageRoot, "raw");

    public string ConfigDirectory { get; }

    public FakeTimeProvider Time { get; } = new(DefaultUtcNow);

    public FakeDiskSpaceProbe Disk { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public T GetService<T>()
        where T : notnull => Services.GetRequiredService<T>();

    /// <summary>存储根目录下的全部文件（含 .tmp），相对路径用 / 分隔。</summary>
    public IReadOnlyList<string> AllStoredFiles()
    {
        if (!Directory.Exists(StorageRoot))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(StorageRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(StorageRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>以 .json 为准列出完整报文（.bin 与 .json 成对）。</summary>
    public IReadOnlyList<StoredCapture> StoredCaptures()
    {
        if (!Directory.Exists(RawRoot))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(RawRoot, "*.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(jsonPath => new StoredCapture(Path.ChangeExtension(jsonPath, ".bin"), jsonPath))];
    }

    /// <summary>每个日志条目（含 Microsoft.* 类别里通过过滤的部分）。</summary>
    public IReadOnlyList<CapturedLog> LogEntries => Logs.Entries;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        // 中文注释：ConfigDirectory 要在 Program 里 AddJsonFile 之前读出，UseSetting 走主机配置，能在 Build 前生效。
        builder.UseSetting("FaceIngest:ConfigDirectory", ConfigDirectory);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(_options.Settings);
            if (_options.ReloadableSource is { } reloadable)
            {
                configuration.Add(reloadable);
            }
        });
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.RemoveAll<IDiskSpaceProbe>();
            services.AddSingleton<IDiskSpaceProbe>(Disk);
            if (_options.RemoteIpAddress is { } remoteIpAddress)
            {
                services.AddSingleton<IStartupFilter>(new RemoteIpAddressStartupFilter(remoteIpAddress));
            }

            if (!_options.EnableCleanupService)
            {
                RemoveHostedService<CaptureStorageCleanupService>(services);
            }

            if (!_options.EnableProcessingService)
            {
                RemoveHostedService<CaptureProcessingService>(services);
            }

            if (!_options.EnableCameraRefreshService)
            {
                RemoveHostedService<CameraRegistryRefreshService>(services);
            }

            _options.ConfigureServices?.Invoke(services);
        });
    }

    private static void RemoveHostedService<T>(IServiceCollection services)
    {
        var hosted = services.Where(descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(T))
            .ToList();
        foreach (var descriptor in hosted)
        {
            services.Remove(descriptor);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                Directory.Delete(TempRoot, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响结论。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>在整个管道最前面改写连接层对端地址，模拟 docker-proxy 转发后容器看到的网桥网关地址。</summary>
internal sealed class RemoteIpAddressStartupFilter(IPAddress remoteIpAddress) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = remoteIpAddress;
                return nextMiddleware(context);
            });
            next(app);
        };
}

/// <summary>一条完整落盘的报文。</summary>
internal sealed record StoredCapture(string BodyPath, string MetadataPath)
{
    public byte[] ReadBody() => File.ReadAllBytes(BodyPath);

    public JsonElement ReadMetadata()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(MetadataPath));
        return document.RootElement.Clone();
    }
}
