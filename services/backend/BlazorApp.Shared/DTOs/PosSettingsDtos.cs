namespace BlazorApp.Shared.DTOs;

// 收银端设置下发：后台 / 员工 App 管理接口的数据结构。路由前缀 api/react/v1/pos-settings。
// 值的约定：Values 字典里只出现「已设置」的项（true = 开，false = 关）；没出现 = 该层没设置（跟随上一层）。
// 保存时 Values 里值为 null 表示「清除这一项」，没出现的项保持不变。

public sealed class PosSettingDefinitionDto
{
    public string Key { get; set; } = string.Empty;

    /// <summary>适用端：wpf / ipad / handheld。</summary>
    public List<string> ClientKinds { get; set; } = [];

    /// <summary>三层都没设置时的缺省值；null = 收银机沿用本机值。</summary>
    public bool? DefaultWhenUnset { get; set; }

    public bool IsNew { get; set; }
}

public sealed class PosSettingsCatalogDto
{
    public List<PosSettingDefinitionDto> Definitions { get; set; } = [];
}

/// <summary>某一层（全局 / 某门店 / 某设备）已设置的项。</summary>
public sealed class PosSettingLayerDto
{
    /// <summary>global / store / device。</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>全局为 "*"，门店为门店代码，设备为设备编号。</summary>
    public string ScopeKey { get; set; } = string.Empty;

    public Dictionary<string, bool> Values { get; set; } = [];
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedByName { get; set; }
}

public sealed class SavePosSettingsRequest
{
    /// <summary>true / false = 设置；null = 清除（回到跟随上一层）。</summary>
    public Dictionary<string, bool?> Values { get; set; } = [];
}

/// <summary>某项最终生效的值与来源。Value 为 null 表示三层都没设置、收银机沿用本机值。</summary>
public sealed class PosSettingEffectiveValueDto
{
    public bool? Value { get; set; }

    /// <summary>global / store / device / default / none。</summary>
    public string Source { get; set; } = "none";
}

public sealed class PosSettingDeviceStatusDto
{
    public string DeviceCode { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>wpf / ipad / handheld / other；从未回执过的设备取设备表的系统类型推导。</summary>
    public string ClientKind { get; set; } = "other";

    public string? AppVersion { get; set; }

    /// <summary>
    /// applied = 已应用最新；pending = 已同步过但有新改动还没应用；
    /// never = 从未回执（旧版本不支持，或还没联过网）。
    /// </summary>
    public string Status { get; set; } = "never";

    public DateTime? AppliedAtUtc { get; set; }
    public bool HasDeviceOverride { get; set; }

    /// <summary>离线时临时修改过的累计次数（收银机恢复联网后上报）。</summary>
    public int OfflineOverrideCount { get; set; }

    public DateTime? LastOfflineOverrideAtUtc { get; set; }
}

public sealed class PosSettingStoreSummaryDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;

    /// <summary>本店已设置的项（与全局不同或显式设置的项）。</summary>
    public Dictionary<string, bool> Overrides { get; set; } = [];

    public int DeviceOverrideCount { get; set; }
    public int DeviceTotal { get; set; }
    public int DeviceApplied { get; set; }
}

public sealed class PosSettingStoreDetailDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public Dictionary<string, bool> Overrides { get; set; } = [];

    /// <summary>每个可下发项在「本店层」的最终值（全局 + 门店合并，不含设备覆盖）。</summary>
    public Dictionary<string, PosSettingEffectiveValueDto> Effective { get; set; } = [];

    public List<PosSettingDeviceStatusDto> Devices { get; set; } = [];
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedByName { get; set; }
}

public sealed class PosSettingDeviceDetailDto
{
    public PosSettingDeviceStatusDto Status { get; set; } = new();
    public string StoreName { get; set; } = string.Empty;
    public Dictionary<string, bool> Overrides { get; set; } = [];

    /// <summary>设备层最终值（设备 + 门店 + 全局合并），只含该设备端适用的项。</summary>
    public Dictionary<string, PosSettingEffectiveValueDto> Effective { get; set; } = [];

    /// <summary>设备最近一次回执上报的「实际生效值」；从未回执为空。</summary>
    public Dictionary<string, bool> Reported { get; set; } = [];
}

public sealed class PosSettingChangeDto
{
    public long Id { get; set; }
    public string Scope { get; set; } = string.Empty;
    public string ScopeKey { get; set; } = string.Empty;
    public string SettingKey { get; set; } = string.Empty;

    /// <summary>null = 修改前没设置。</summary>
    public bool? OldValue { get; set; }

    /// <summary>null = 被清除。</summary>
    public bool? NewValue { get; set; }

    public DateTime ChangedAtUtc { get; set; }
    public string? ChangedByName { get; set; }
}
