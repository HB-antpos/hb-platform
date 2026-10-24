using System.Text.Json.Serialization;

namespace BlazorApp.Api.Services.PosApiEntry;

// 全局 JSON 配置是 camelCase + WhenWritingNull（null 属性整个省略）。前端靠 `mode === null` 判断「未设置」，
// 所以可空属性统一标 JsonIgnoreCondition.Never，显式输出 null。

/// <summary>各入口代号（与 Hbpos.Api 的 X-HB-Entry / receipt-profile/sync 的 ApiEntryMode 一致）。</summary>
public static class PosApiEntryModes
{
    public const string Edge = "edge";
    public const string Aws = "aws";
    public const string Direct = "direct";

    /// <summary>白名单；PUT 只接受这三个值，不提供清空 / NULL。</summary>
    public static readonly IReadOnlyList<string> All = [Edge, Aws, Direct];

    /// <summary>库里的 NULL＝未设置，收银端按 edge 处理。</summary>
    public const string Default = Edge;

    /// <summary>白名单归一化（去空白、转小写）；不在白名单返回 null。</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();
        return trimmed is Edge or Aws or Direct ? trimmed : null;
    }
}

public static class PosApiEntryErrorCodes
{
    public const string InvalidRequest = "POS_API_ENTRY_INVALID_REQUEST";
    public const string InvalidMode = "POS_API_ENTRY_INVALID_MODE";
    public const string StoreForbidden = "POS_API_ENTRY_STORE_FORBIDDEN";
    public const string StoreNotFound = "POS_API_ENTRY_STORE_NOT_FOUND";
    public const string ScopeForbidden = "POS_API_ENTRY_SCOPE_FORBIDDEN";
}

/// <summary>PUT 请求体：批量把若干分店的首选入口设为同一个值。</summary>
public sealed class PosApiEntryUpdateRequestDto
{
    public List<string>? StoreGuids { get; set; }

    public string? Mode { get; set; }
}

public sealed class PosApiEntryUpdateResultDto
{
    /// <summary>实际写库的分店数（mode 与现值不同的）。</summary>
    public int Updated { get; set; }

    /// <summary>已经是目标值而跳过的分店数。</summary>
    public int Skipped { get; set; }
}

/// <summary>某家分店按「实际走的入口」分组的在线设备数；unknown＝在线但入口未知 / 已过期。</summary>
public sealed class PosApiEntryCountsDto
{
    public int Edge { get; set; }
    public int Aws { get; set; }
    public int Direct { get; set; }
    public int Unknown { get; set; }
}

public sealed class PosApiEntryDeviceStatsDto
{
    /// <summary>该店启用的收银设备数（设备类型 POS、状态启用；含 WPF / 手持 / iPad）。</summary>
    public int Total { get; set; }

    /// <summary>其中在线的设备数（沿用设备管理页的在线口径）。</summary>
    public int Online { get; set; }

    /// <summary>在线设备按实际入口分组；四项之和等于 Online。</summary>
    public PosApiEntryCountsDto ByEntry { get; set; } = new();

    /// <summary>实际入口≠首选入口的在线设备数（入口已知且新鲜的才计入）。</summary>
    public int FallbackCount { get; set; }

    /// <summary>版本过低、不认后台设置的 WPF 台数（上报版本低于 1.0.89 或从未上报版本）；手持 / iPad 暂不判断。</summary>
    public int UnsupportedCount { get; set; }
}

public sealed class PosApiEntryStoreDto
{
    public string StoreGuid { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;

    /// <summary>库里存的首选入口；null＝未设置（前端按 edge 展示）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Mode { get; set; }

    /// <summary>实际生效的首选入口：mode 为空时是 edge。</summary>
    public string EffectiveMode { get; set; } = PosApiEntryModes.Default;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTime? UpdatedAt { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? UpdatedBy { get; set; }

    public PosApiEntryDeviceStatsDto Devices { get; set; } = new();
}
