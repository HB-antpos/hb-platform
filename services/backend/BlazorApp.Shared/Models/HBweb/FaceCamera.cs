using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 门店人脸抓拍摄像机注册表（dbo.FaceCamera）。表由迁移 20261013.001-face-capture 创建，
/// 人脸接收服务（BlazorApp.FaceIngest）每 30 秒读取并用它做 token 鉴权。
/// 注意：本实体只用于后台管理读写，刻意不进自动建表清单（tableTypes），表结构以迁移为准。
/// 只存 token 的 SHA-256，永远不存 token 原文。
/// </summary>
[SugarTable("FaceCamera")]
public sealed class FaceCamera
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 64)]
    public string CameraId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 32)]
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>token 的小写 SHA-256 十六进制（64 位），唯一索引 UX_FaceCamera_TokenSha256 兜底。</summary>
    [SugarColumn(IsNullable = false, Length = 64)]
    public string TokenSha256 { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public bool Enabled { get; set; } = true;

    [SugarColumn(IsNullable = true, Length = 200)]
    public string? Note { get; set; }

    [SugarColumn(IsNullable = true, Length = 64)]
    public string? DeviceModel { get; set; }

    [SugarColumn(IsNullable = true, Length = 32)]
    public string? MacAddress { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>人脸接收服务收到该摄像机的有效推送时节流更新（至多每 5 分钟一次）。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? LastSeenAtUtc { get; set; }
}

/// <summary>
/// 抓拍事件表（dbo.FaceCaptureEvent）的后台只读映射：只映射后台列表需要的列，
/// 该表由人脸接收服务写入，后台不写、不返回任何图片或图片地址。同样不进自动建表清单。
/// </summary>
[SugarTable("FaceCaptureEvent")]
public sealed class FaceCaptureEvent
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string CaptureId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 64)]
    public string CameraId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime ReceivedAtUtc { get; set; }

    /// <summary>摄像机报文里的带偏移时间。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? CameraEventTime { get; set; }

    [SugarColumn(IsNullable = true, Length = 64)]
    public string? EventType { get; set; }

    [SugarColumn(IsNullable = true, Length = 32)]
    public string? DuplicateOfCaptureId { get; set; }

    [SugarColumn(IsNullable = false)]
    public int FaceCount { get; set; }

    /// <summary>0 Parsed / 1 PartiallyParsed / 2 Unparsed / 3 NotFaceEvent。</summary>
    [SugarColumn(IsNullable = false)]
    public byte ParseStatus { get; set; }

    [SugarColumn(IsNullable = true, Length = 500)]
    public string? ParseNote { get; set; }
}
