namespace BlazorApp.Api.Features.FaceCameras;

/// <summary>摄像机列表项：含服务端按 TimeProvider 算好的在线状态与近 24 小时统计。</summary>
public sealed record FaceCameraDto
{
    public string CameraId { get; init; } = string.Empty;
    public string StoreCode { get; init; } = string.Empty;
    public string? StoreName { get; init; }
    public string? DeviceModel { get; init; }
    public string? MacAddress { get; init; }
    public string? Note { get; init; }
    public bool Enabled { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public DateTime? LastSeenAtUtc { get; init; }

    /// <summary>启用且 LastSeenAtUtc 在在线窗口（10 分钟）内；停用的摄像机恒为 false。</summary>
    public bool Online { get; init; }

    public int CaptureCount24h { get; init; }

    /// <summary>近 24 小时人脸数；排除 DuplicateOfCaptureId 不为空的重复报文。</summary>
    public int FaceCount24h { get; init; }

    /// <summary>近 24 小时 ParseStatus=2（无法解析）的事件数。</summary>
    public int ParseFailureCount24h { get; init; }

    /// <summary>该摄像机最近一次抓拍事件的入库时间（不限 24 小时）。</summary>
    public DateTime? LastEventAtUtc { get; init; }
}

public sealed record FaceCameraListDto
{
    public IReadOnlyList<FaceCameraDto> Items { get; init; } = [];
    public DateTime GeneratedAtUtc { get; init; }
    public int OnlineWindowMinutes { get; init; }
    public int StatsWindowHours { get; init; }
}

public sealed record FaceCameraCreateRequest
{
    public string? CameraId { get; init; }
    public string? StoreCode { get; init; }
    public string? Note { get; init; }
    public string? DeviceModel { get; init; }
    public string? MacAddress { get; init; }
}

public sealed record FaceCameraUpdateRequest
{
    public string? StoreCode { get; init; }
    public string? Note { get; init; }
    public string? DeviceModel { get; init; }
    public string? MacAddress { get; init; }

    /// <summary>必填。用可空类型是为了区分「没传」和「传了 false」：漏传不能被当成停用。</summary>
    public bool? Enabled { get; init; }
}

/// <summary>
/// 新增 / 轮换后的一次性响应：Token 与 CameraUrl 只在这一次返回，库里只有哈希，之后无法再查看。
/// </summary>
public sealed record FaceCameraSecretDto
{
    public FaceCameraDto Camera { get; init; } = new();
    public string Token { get; init; } = string.Empty;
    public string CameraUrl { get; init; } = string.Empty;

    // 防止 token 经 ToString / 日志模板的 {@Record} 解构被打印出来。
    public override string ToString() => $"FaceCameraSecretDto {{ CameraId = {Camera.CameraId}, Token = *** }}";
}

/// <summary>抓拍记录元数据；刻意没有任何图片 / 对象键 / 地址字段。</summary>
public sealed record FaceCaptureItemDto
{
    public string CaptureId { get; init; } = string.Empty;
    public DateTime ReceivedAtUtc { get; init; }
    public DateTimeOffset? CameraEventTime { get; init; }
    public string? EventType { get; init; }
    public int FaceCount { get; init; }
    public int ParseStatus { get; init; }
    public string? ParseNote { get; init; }
    public bool IsDuplicate { get; init; }
}

public sealed record FaceCapturePageDto
{
    public IReadOnlyList<FaceCaptureItemDto> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public bool HasMore { get; init; }
}

/// <summary>操作人，仅用于审计日志。</summary>
public sealed record FaceCameraActor(string Id, string Name);

/// <summary>业务错误：由控制器转成对应 HTTP 状态码与 ApiResponse 错误码；消息不得包含 token。</summary>
public sealed class FaceCameraException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public interface IFaceCameraAdminService
{
    Task<FaceCameraListDto> ListAsync(CancellationToken cancellationToken = default);

    Task<FaceCameraSecretDto> CreateAsync(
        FaceCameraCreateRequest request, FaceCameraActor actor, CancellationToken cancellationToken = default);

    Task<FaceCameraDto> UpdateAsync(
        string cameraId, FaceCameraUpdateRequest request, FaceCameraActor actor, CancellationToken cancellationToken = default);

    Task<FaceCameraSecretDto> RotateTokenAsync(
        string cameraId, FaceCameraActor actor, CancellationToken cancellationToken = default);

    Task<FaceCapturePageDto> ListCapturesAsync(
        string cameraId, int page, int pageSize, CancellationToken cancellationToken = default);
}
