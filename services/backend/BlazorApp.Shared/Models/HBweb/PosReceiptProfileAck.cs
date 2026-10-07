using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 收银设备对小票资料下发版本的「已应用」回执：每台设备一行（由 Hbpos.Api upsert）。
/// HBweb 只读它来展示「设备应用情况」；设备清单在 POSM 库，这里不跨库关联，应用层按 DeviceCode 内存关联。
/// 实体不进 SqlSugarContext 的自动建表清单，由版本号迁移 20261007.001-store-receipt-profile-release 创建。
/// </summary>
[SugarTable("PosReceiptProfileAck")]
public sealed class PosReceiptProfileAck
{
    public const string ClientKindWpf = "wpf";
    public const string ClientKindHandheld = "handheld";
    public const string ClientKindIpad = "ipad";
    public const string ClientKindOther = "other";

    /// <summary>主键：对应 POSM 设备表「系统设备编号」，也是设备认证声明 hbpos_device_code。</summary>
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 100)]
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>回执时设备所属门店；设备换店后旧店回执的 StoreCode 仍是旧店，HBweb 统计时据此隔离。</summary>
    [SugarColumn(IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public int AppliedVersion { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime AppliedAtUtc { get; set; }

    /// <summary>wpf / handheld / ipad / other。</summary>
    [SugarColumn(IsNullable = false, Length = 16)]
    public string ClientKind { get; set; } = ClientKindOther;
}
