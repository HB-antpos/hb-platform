using BlazorApp.FaceIngest;
using Microsoft.Extensions.DependencyInjection.Extensions;

// 人脸抓拍原始报文接收服务（P0）：校验摄像机 token → 请求体逐字节原样落盘 + 元数据 → 立刻 200。
// 不解析 multipart、不入库、不接 COS、不做人脸比对；部署与运维见 docs/runbooks/face-ingest.md。
var builder = WebApplication.CreateBuilder(args);

// 中文注释：cameras.json 的目录必须在 AddJsonFile 之前，从已有配置（环境变量 FaceIngest__ConfigDirectory /
// appsettings / 命令行）读出来。目录不存在时不注册：FileConfigurationSource 会一路向上找到存在的祖先目录
// （最坏是 /）并递归监听，开销不可控；生产由 compose 挂载 /app/config，必然存在。
var configDirectory = builder.Configuration[FaceIngestOptions.ConfigDirectoryKey];
if (string.IsNullOrWhiteSpace(configDirectory))
{
    configDirectory = FaceIngestOptions.DefaultConfigDirectory;
}

configDirectory = Path.GetFullPath(configDirectory);
var camerasFileLoadErrors = new CamerasFileLoadErrorRelay();
if (Directory.Exists(configDirectory))
{
    // 中文注释：reloadOnChange 让现场加 / 停摄像机不必重启容器；绑定挂载下由 DOTNET_USE_POLLING_FILE_WATCHER=true 改为轮询。
    // OnLoadException：运行中改成坏 JSON 时框架默认只在监听线程里静默吞掉异常，这里转成 Error 日志（见 CamerasFileLoadErrorRelay）。
    builder.Configuration.AddJsonFile(source =>
    {
        source.Path = Path.Combine(configDirectory, FaceIngestOptions.CamerasFileName);
        source.Optional = true;
        source.ReloadOnChange = true;
        source.ResolveFileProvider();
        source.OnLoadException = camerasFileLoadErrors.OnLoadException;
    });
}

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z' ";
    // 关键逻辑：ASP.NET Core 的请求 scope 带完整 RequestPath（含路径 token）。这里在配置绑定之后强制关闭，
    // 即使有人为排障加了 Logging__Console__FormatterOptions__IncludeScopes=true 也不会把 token 打进容器日志。
    options.IncludeScopes = false;
});
// 关键逻辑：请求 scope 只在 Hosting.Diagnostics 类别启用 Critical 时才创建；从源头关掉这个类别，
// 任何日志提供程序（含以后接入的）都拿不到带 token 的 RequestPath。该类别只记请求起止，本服务用不上。
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);

builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    kestrel.AddServerHeader = false;
    // 中文注释：全局请求体上限与 FaceIngest:MaxRequestBodyBytes 同值（默认 16MB，与 Nginx client_max_body_size 对齐），
    // 超限由 Kestrel 在读取时抛 413；端点里按当前快照再兜底一次。
    var maxRequestBodyBytes = context.Configuration.GetValue(
        FaceIngestOptions.SectionName + ":" + nameof(FaceIngestOptions.MaxRequestBodyBytes),
        FaceIngestOptions.DefaultMaxRequestBodyBytes);
    if (maxRequestBodyBytes > 0)
    {
        kestrel.Limits.MaxRequestBodySize = maxRequestBodyBytes;
    }
});

builder.Services.AddOptions<FaceIngestOptions>()
    .Bind(builder.Configuration.GetSection(FaceIngestOptions.SectionName));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.TryAddSingleton<IDiskSpaceProbe, DriveInfoDiskSpaceProbe>();
builder.Services.AddSingleton(camerasFileLoadErrors);
builder.Services.AddSingleton<FaceIngestSettingsMonitor>();
builder.Services.AddSingleton<RawCaptureStore>();
builder.Services.AddSingleton<FaceIngestRequestHandler>();
builder.Services.AddSingleton<CaptureStorageCleaner>();
builder.Services.AddHostedService<CaptureStorageCleanupService>();

var app = builder.Build();

// 中文注释：启动即校验配置（等效 ValidateOnStart：非法直接抛 OptionsValidationException，容器启动失败）
// 并扫描已有报文得到存储统计，确保第一个请求进来前护栏数据已就绪。
app.Services.GetRequiredService<FaceIngestSettingsMonitor>();
app.Services.GetRequiredService<RawCaptureStore>();

app.MapFaceIngestEndpoints();

app.Run();

/// <summary>供 WebApplicationFactory 集成测试引用的入口类型。</summary>
public partial class Program;
