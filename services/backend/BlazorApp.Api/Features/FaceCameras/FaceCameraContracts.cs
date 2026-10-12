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

/// <summary>
/// 抓拍记录元数据；刻意没有对象键 / 存储地址 / 哈希字段（图片字节只经专用内容接口转发）。
/// <see cref="Images"/> 只在调用者持有 FaceCapture.Images.View 时才有内容，否则恒为空数组。
/// </summary>
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

    /// <summary>图片元数据（不含对象键）；无权限者恒为空，避免从元数据得知图片结构。</summary>
    public IReadOnlyList<FaceCaptureImageMetaDto> Images { get; init; } = [];
}

/// <summary>人脸框：<see cref="FaceCaptureImageMetaDto.RectNormalized"/> 为 true 时是 0~1 比例，否则是像素。</summary>
public sealed record FaceCaptureImageRectDto(decimal X, decimal Y, decimal W, decimal H);

/// <summary>单张图片的元数据。没有 ObjectKey、地址、哈希：对象键只存在于库里，由服务端读取图片时使用。</summary>
public sealed record FaceCaptureImageMetaDto
{
    public string ImageId { get; init; } = string.Empty;

    /// <summary>0 人脸图 / 1 背景图 / 2 其他。</summary>
    public int Kind { get; init; }

    /// <summary>cos | local；local 表示图片仍在接收服务本地磁盘，后台无法读取。</summary>
    public string Storage { get; init; } = string.Empty;

    public int? Width { get; init; }
    public int? Height { get; init; }
    public int ByteSize { get; init; }
    public int? FaceIndex { get; init; }
    public FaceCaptureImageRectDto? Rect { get; init; }
    public bool? RectNormalized { get; init; }
    public decimal? FaceScore { get; init; }
}

public sealed record FaceCapturePageDto
{
    public IReadOnlyList<FaceCaptureItemDto> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public bool HasMore { get; init; }

    /// <summary>调用者是否持有 FaceCapture.Images.View；为 false 时每条记录的 Images 都是空数组。</summary>
    public bool CanViewImages { get; init; }
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

    /// <summary>
    /// 最近抓拍（元数据）。includeImages 必须由调用方按 FaceCapture.Images.View 权限决定：
    /// 为 false 时不查图片表，响应里 CanViewImages=false、每条 Images 为空数组。
    /// </summary>
    Task<FaceCapturePageDto> ListCapturesAsync(
        string cameraId, int page, int pageSize, bool includeImages = false, CancellationToken cancellationToken = default);
}

/// <summary>图片字节与对外声明的 Content-Type（只会是白名单里的 image/jpeg 或 image/png）。</summary>
public sealed record FaceImageContent(byte[] Bytes, string ContentType);

/// <summary>读取人脸抓拍图片内容：库里校验归属 → 服务端向私有 COS 取字节 → 审计。</summary>
public interface IFaceCaptureImageService
{
    /// <summary>
    /// 读取一张图片的字节。callerHasImagesViewPermission 是调用者是否持有 FaceCapture.Images.View，
    /// 由控制器通过授权服务判定后传入；为 false 时直接拒绝（并记审计），不查库、不碰 COS。
    /// </summary>
    Task<FaceImageContent> GetContentAsync(
        string cameraId,
        string captureId,
        string imageId,
        FaceCameraActor actor,
        bool callerHasImagesViewPermission,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 私有图片桶读取。对象键只能来自数据库；实现负责再次校验键的形态、签名、超时与大小上限。
/// 失败一律抛 <see cref="FaceCameraException"/>，消息里不含桶名、密钥、签名或完整地址。
/// </summary>
public interface IFaceImageStore
{
    Task<byte[]> ReadAsync(string objectKey, int maxBytes, CancellationToken cancellationToken = default);
}
