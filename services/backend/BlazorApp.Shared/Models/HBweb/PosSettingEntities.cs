using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 收银端设置下发的「已设置值」：每层每项一行（没设置 = 没有这一行）。主键 (Scope, ScopeKey, SettingKey)。
/// Scope = global / store / device；ScopeKey：全局固定 "*"，门店为门店代码，设备为设备编号（认证声明 hbpos_device_code）。
/// 实体不进 SqlSugarContext 的自动建表清单，由版本号迁移 20261114.001-pos-settings 创建。
/// </summary>
[SugarTable("PosSettingValue")]
public sealed class PosSettingValue
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 16)]
    public string Scope { get; set; } = string.Empty;

    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 100)]
    public string ScopeKey { get; set; } = string.Empty;

    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 100)]
    public string SettingKey { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public bool Value { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime UpdatedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? UpdatedBy { get; set; }
}

/// <summary>设置修改记录（只追加）：谁、在哪一层、改了哪一项、旧值 → 新值。OldValue / NewValue 为 null 表示「没设置 / 被清除」。</summary>
[SugarTable("PosSettingChange")]
public sealed class PosSettingChange
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(IsNullable = false, Length = 16)]
    public string Scope { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 100)]
    public string ScopeKey { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 100)]
    public string SettingKey { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true)]
    public bool? OldValue { get; set; }

    [SugarColumn(IsNullable = true)]
    public bool? NewValue { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime ChangedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? ChangedBy { get; set; }
}

/// <summary>
/// 设备对设置下发的回执：每台设备一行（由 Hbpos.Api upsert）。AppliedHash 是设备应用的指纹，
/// EffectiveJson 是设备上报的「实际生效值」（含离线临时修改后的值），后台据此显示设备回报。
/// </summary>
[SugarTable("PosSettingAck")]
public sealed class PosSettingAck
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 100)]
    public string DeviceCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 64)]
    public string AppliedHash { get; set; } = string.Empty;

    // 生产列类型 nvarchar(max) 由迁移建表时指定；这里不写 ColumnDataType，免得 SQLite 测试库建表失败。
    [SugarColumn(IsNullable = true)]
    public string? EffectiveJson { get; set; }

    [SugarColumn(IsNullable = false, Length = 16)]
    public string ClientKind { get; set; } = "other";

    [SugarColumn(IsNullable = true, Length = 64)]
    public string? AppVersion { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime AppliedAtUtc { get; set; }

    [SugarColumn(IsNullable = false)]
    public int OfflineOverrideCount { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? LastOfflineOverrideAtUtc { get; set; }
}
