using System.Text.Encodings.Web;
using System.Text.Json;

namespace BlazorApp.FaceIngest;

/// <summary>
/// 每条原始报文旁的元数据（<c>.json</c>，camelCase）。.json 出现即代表该条报文完整落盘。
/// 业务口径：P1 解析会依赖这些字段，字段名与含义改动需升 <see cref="SchemaVersion"/>；
/// 任何字段都不得出现 token 原文（path / query / 请求头均已打码）。
/// </summary>
internal sealed record CaptureMetadata
{
    /// <summary>当前元数据格式版本。</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string CaptureId { get; init; }

    public required string CameraId { get; init; }

    public required string StoreCode { get; init; }

    /// <summary>服务端 UTC 接收时间，ISO 8601，Z 结尾。</summary>
    public required string ReceivedAtUtc { get; init; }

    /// <summary>从开始处理请求到请求体完整落盘的耗时（毫秒）。</summary>
    public required double DurationMs { get; init; }

    public required string Method { get; init; }

    /// <summary>请求路径，路径里的 token 已替换为 <c>***</c>。</summary>
    public required string Path { get; init; }

    /// <summary>原始 query（含开头的 ?），token 参数值已替换为 <c>***</c>，其余原样。</summary>
    public required string Query { get; init; }

    public required string Protocol { get; init; }

    /// <summary>全部请求头，展开为数组以保留重复项；敏感头的值替换为 <c>***</c>。</summary>
    public required IReadOnlyList<CapturedHeader> Headers { get; init; }

    /// <summary>
    /// 连接层对端地址。生产经宿主机 Nginx → docker-proxy 进入容器，这里是 Docker 网桥网关地址
    /// （如 ::ffff:172.18.0.1），不是摄像机地址；门店出口 IP 看 <see cref="RealIp"/>。
    /// </summary>
    public string? RemoteIpAddress { get; init; }

    /// <summary>X-Forwarded-For 原样记录，不作信任判断。</summary>
    public string? ForwardedFor { get; init; }

    /// <summary>X-Real-IP 原样记录，不作信任判断。</summary>
    public string? RealIp { get; init; }

    public string? ContentType { get; init; }

    public long? DeclaredContentLength { get; init; }

    public required long BodyBytes { get; init; }

    /// <summary>请求体 SHA-256，小写十六进制。</summary>
    public required string BodySha256 { get; init; }

    /// <summary>token 来源：path | query | basic。</summary>
    public required string TokenSource { get; init; }

    /// <summary>落盘用的序列化选项：camelCase、缩进、不做 HTML 转义（文件不会嵌入网页，保持请求头原貌便于人工排查）。</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>一个请求头条目（同名多值的头会拆成多条）。</summary>
internal sealed record CapturedHeader(string Name, string Value);

/// <summary>
/// 由端点从 HttpContext 提取、已完成 token 打码的请求信息，交给 <see cref="RawCaptureStore"/> 写入元数据。
/// </summary>
internal sealed record CapturedRequestInfo
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    public required string Query { get; init; }

    public required string Protocol { get; init; }

    public required IReadOnlyList<CapturedHeader> Headers { get; init; }

    public string? RemoteIpAddress { get; init; }

    public string? ForwardedFor { get; init; }

    public string? RealIp { get; init; }

    public string? ContentType { get; init; }

    public long? DeclaredContentLength { get; init; }

    public required string TokenSource { get; init; }
}
