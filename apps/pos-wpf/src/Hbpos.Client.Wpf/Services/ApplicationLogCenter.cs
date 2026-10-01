using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Hbpos.Client.Wpf.Services;

internal sealed record ApplicationLogDefaults(
    string ProjectCode,
    string Environment,
    string SourceType)
{
    public static ApplicationLogDefaults Default { get; } = new("hbpos_win", "production", "POS");
}

internal sealed record ApplicationLogEntry(
    string Level,
    string Message,
    DateTimeOffset TimestampUtc,
    string ProjectCode,
    string Environment,
    string SourceType,
    string? Category = null,
    string? ServiceName = null,
    string? TraceId = null,
    string? RequestPath = null,
    string? RequestMethod = null,
    int? StatusCode = null,
    string? UserId = null,
    string? UserName = null,
    string? ExceptionType = null,
    string? ExceptionMessage = null,
    string? StackTrace = null,
    IReadOnlyDictionary<string, object?>? Properties = null);

internal sealed record ApplicationLogContext(
    string? TraceId = null,
    string? RequestPath = null,
    string? RequestMethod = null,
    int? StatusCode = null,
    string? UserId = null,
    string? UserName = null,
    IReadOnlyDictionary<string, object?>? Properties = null);

internal sealed record ApplicationLogOptions(
    bool Enabled,
    string? ApiKey,
    string ProjectCode,
    string Environment,
    string SourceType,
    string ServiceName,
    Uri? IngestUri,
    int BatchSize,
    int QueueCapacity)
{
    public bool IsConfigured => Enabled &&
                                !string.IsNullOrWhiteSpace(ApiKey) &&
                                IngestUri is { IsAbsoluteUri: true } uri &&
                                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public ApplicationLogDefaults ToDefaults()
    {
        return new ApplicationLogDefaults(ProjectCode, Environment, SourceType);
    }

    internal const string BuildApiKeyMetadataKey = "HbposCenterLogApiKey";
    internal const string BuildIngestUrlMetadataKey = "HbposCenterLogIngestUrl";

    public static ApplicationLogOptions FromConfiguration(IConfiguration configuration, Uri apiBaseAddress) =>
        FromConfiguration(configuration, apiBaseAddress, ReadBuildDefaults(typeof(ApplicationLogOptions).Assembly));

    /// <summary>
    /// 读取顺序：环境变量 > 配置文件 > 安装包构建时写入程序集元数据的默认值。
    /// 门店收银机没有配置文件也不设环境变量，正式安装包靠构建默认值上传；本地与测试构建没有默认值则不上传。
    /// </summary>
    internal static ApplicationLogOptions FromConfiguration(
        IConfiguration configuration,
        Uri apiBaseAddress,
        IReadOnlyDictionary<string, string> buildDefaults)
    {
        _ = apiBaseAddress;
        var enabled = ReadBool(configuration, "CentralLogging:Enabled", "HBPOS_LOG_CENTER_ENABLED") ?? true;
        var ingestUrl = ReadText(configuration, "CentralLogging:IngestUrl", "HBPOS_LOG_CENTER_INGEST_URL")
            ?? BuildDefault(buildDefaults, BuildIngestUrlMetadataKey);
        var projectCode = ReadText(configuration, "CentralLogging:ProjectCode", "HBPOS_LOG_CENTER_PROJECT_CODE") ?? "hbpos_win";
        var environment = ReadText(configuration, "CentralLogging:Environment", "HBPOS_LOG_CENTER_ENVIRONMENT") ??
            System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
            System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
            "production";
        var sourceType = ReadText(configuration, "CentralLogging:SourceType", "HBPOS_LOG_CENTER_SOURCE_TYPE") ?? "POS";
        var serviceName = ReadText(configuration, "CentralLogging:ServiceName", "HBPOS_LOG_CENTER_SERVICE_NAME") ?? "Hbpos.Client.Wpf";
        var batchSize = ReadInt(configuration, "CentralLogging:BatchSize", "HBPOS_LOG_CENTER_BATCH_SIZE") ?? 100;
        var queueCapacity = ReadInt(configuration, "CentralLogging:QueueCapacity", "HBPOS_LOG_CENTER_QUEUE_CAPACITY") ?? 200;

        return new ApplicationLogOptions(
            enabled,
            ReadText(configuration, "CentralLogging:ApiKey", "HBPOS_LOG_CENTER_API_KEY")
                ?? BuildDefault(buildDefaults, BuildApiKeyMetadataKey),
            projectCode,
            environment,
            sourceType,
            serviceName,
            ResolveIngestUri(ingestUrl),
            Math.Clamp(batchSize, 1, 100),
            Math.Clamp(queueCapacity, 10, 5_000));
    }

    private static Uri? ResolveIngestUri(string? ingestUrl)
    {
        if (Uri.TryCreate(ingestUrl, UriKind.Absolute, out var absoluteIngestUri) &&
            (absoluteIngestUri.Scheme == Uri.UriSchemeHttp || absoluteIngestUri.Scheme == Uri.UriSchemeHttps))
        {
            return absoluteIngestUri;
        }

        // 中心日志属于 BlazorApp.Api，禁止错误回退到本机 Hbpos.Api 相对地址。
        return null;
    }

    internal static IReadOnlyDictionary<string, string> ReadBuildDefaults(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute =>
                (attribute.Key == BuildApiKeyMetadataKey || attribute.Key == BuildIngestUrlMetadataKey) &&
                !string.IsNullOrWhiteSpace(attribute.Value))
            .GroupBy(attribute => attribute.Key)
            .ToDictionary(group => group.Key, group => group.Last().Value!.Trim());

    private static string? BuildDefault(IReadOnlyDictionary<string, string> buildDefaults, string key) =>
        buildDefaults.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string? ReadText(IConfiguration configuration, string configKey, string environmentKey)
    {
        // 环境变量用于部署覆盖，必须优先于 appsettings 中的默认值。
        var environmentValue = System.Environment.GetEnvironmentVariable(environmentKey);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue.Trim();
        }

        var configuredValue = configuration[configKey];
        return string.IsNullOrWhiteSpace(configuredValue) ? null : configuredValue.Trim();
    }

    private static int? ReadInt(IConfiguration configuration, string configKey, string environmentKey)
    {
        var text = ReadText(configuration, configKey, environmentKey);
        return int.TryParse(text, out var value) ? value : null;
    }

    private static bool? ReadBool(IConfiguration configuration, string configKey, string environmentKey)
    {
        var text = ReadText(configuration, configKey, environmentKey);
        return bool.TryParse(text, out var value) ? value : null;
    }
}

internal interface IApplicationLogSink
{
    void Enqueue(ApplicationLogEntry entry);
}

internal sealed class NoopApplicationLogSink : IApplicationLogSink
{
    public static NoopApplicationLogSink Instance { get; } = new();

    private NoopApplicationLogSink()
    {
    }

    public void Enqueue(ApplicationLogEntry entry)
    {
        _ = entry;
    }
}
