using System.Text.Json.Serialization;

namespace BlazorApp.Api.Services.StoreReceiptProfiles;

// 全局 JSON 配置是 camelCase + WhenWritingNull（null 属性整个省略）。契约要求这些可空字段显式输出 null，
// 前端靠 `=== null` 判断「从未下发 / 没有回执」，所以可空属性统一标 JsonIgnoreCondition.Never。

/// <summary>status / publish 的请求体：门店 GUID 列表（1–100 个、不重复、非空白）。</summary>
public sealed class StoreReceiptProfileRequestDto
{
    public List<string>? StoreGuids { get; set; }
}

/// <summary>小票资料 6 个字段；Store 当前值与下发快照共用。</summary>
public sealed class StoreReceiptProfileFieldsDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? BrandName { get; set; }

    public string StoreName { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Address { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Phone { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Abn { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ReturnPolicy { get; set; }
}

public sealed class StoreReceiptProfileStatusItemDto
{
    public string StoreGuid { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;

    /// <summary>never＝从未下发；synced＝Store 当前值与最新快照一致；pending＝有未下发的修改。</summary>
    public string Status { get; set; } = StoreReceiptProfileStatuses.Never;

    /// <summary>0＝从未下发。</summary>
    public int LatestVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTime? PublishedAtUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PublishedBy { get; set; }

    /// <summary>Store 当前值（用于确认框新旧对比）。</summary>
    public StoreReceiptProfileFieldsDto Current { get; set; } = new();

    /// <summary>最新快照；从未下发为 null。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public StoreReceiptProfileFieldsDto? Latest { get; set; }

    /// <summary>POSM 中该店 设备类型='POS' 且 设备状态=1 的台数。</summary>
    public int DeviceTotal { get; set; }

    /// <summary>其中已应用最新版本的台数；LatestVersion=0 时为 0。</summary>
    public int DeviceApplied { get; set; }
}

public static class StoreReceiptProfileStatuses
{
    public const string Never = "never";
    public const string Synced = "synced";
    public const string Pending = "pending";
}

public sealed class StoreReceiptProfileDevicesDto
{
    public string StoreGuid { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public int LatestVersion { get; set; }
    public List<StoreReceiptProfileDeviceDto> Devices { get; set; } = new();
}

public sealed class StoreReceiptProfileDeviceDto
{
    public string DeviceCode { get; set; } = string.Empty;
    public string DeviceSystem { get; set; } = string.Empty;

    /// <summary>wpf / handheld / ipad / other，由设备系统推导（与 Hbpos.Api 回执写入口径一致）。</summary>
    public string ClientKind { get; set; } = string.Empty;

    public int DeviceStatus { get; set; }
    public bool IsOnline { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTime? LastHeartbeatAt { get; set; }

    /// <summary>该设备在当前门店下已应用的版本；没有回执或回执属于别的门店时为 null。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? AppliedVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTime? AppliedAtUtc { get; set; }

    /// <summary>latestVersion&gt;0 且 appliedVersion&gt;=latestVersion。</summary>
    public bool UpToDate { get; set; }
}

public sealed class StoreReceiptProfilePublishResultDto
{
    public int RequestedCount { get; set; }
    public int PublishedCount { get; set; }
    public int UnchangedCount { get; set; }
    public List<StoreReceiptProfilePublishItemDto> Items { get; set; } = new();
}

public sealed class StoreReceiptProfilePublishItemDto
{
    public string StoreGuid { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>published＝生成了新版本；unchanged＝Store 与最新快照一致，未生成新版本。</summary>
    public string Outcome { get; set; } = StoreReceiptProfileOutcomes.Unchanged;

    /// <summary>published 为新版本号；unchanged 为当前最新版本号。</summary>
    public int Version { get; set; }
}

public static class StoreReceiptProfileOutcomes
{
    public const string Published = "published";
    public const string Unchanged = "unchanged";
}

/// <summary>整批下发失败时 ApiResponse.Details 里的逐店原因。</summary>
public sealed class StoreReceiptProfilePublishErrorDetailDto
{
    public string StoreGuid { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StoreCode { get; set; }

    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>本功能对外暴露的错误码（与契约一致）。</summary>
public static class StoreReceiptProfileErrorCodes
{
    public const string InvalidRequest = "INVALID_RECEIPT_PROFILE_REQUEST";
    public const string StoreNotFound = "STORE_NOT_FOUND";
    public const string NotPublishable = "RECEIPT_PROFILE_NOT_PUBLISHABLE";
    public const string PublishConflict = "RECEIPT_PROFILE_PUBLISH_CONFLICT";

    // 以下三个只出现在 NotPublishable 的 Details 里，说明具体哪家店为什么不能下发。
    public const string StoreInactive = "STORE_INACTIVE";
    public const string StoreNameRequired = "STORE_NAME_REQUIRED";
    public const string InvalidCharacters = "STORE_PROFILE_INVALID_CHARACTERS";
    public const string StoreCodeRequired = "STORE_CODE_REQUIRED";
}
