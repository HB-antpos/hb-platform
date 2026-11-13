using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 扫码打包的箱：一个纸箱一行。箱号（BX + 年月日 + 4 位流水）就是箱标签上的条码内容，重新开箱后不变。
/// 位置挂在箱 / 托盘上，商品位置由箱明细推导；本功能不改仓库库存。
/// </summary>
[SugarTable("WarehousePackCarton")]
public sealed class WarehousePackCarton
{
    /// <summary>UUIDv7 的 32 位十六进制串，由打包服务插入时生成（Shared 同时编译 net8.0，不能在这里做默认值）。</summary>
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string CartonGUID { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 20)]
    public string CartonNo { get; set; } = string.Empty;

    /// <summary>见 <see cref="WarehousePackCartonStatuses"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public int Status { get; set; }

    /// <summary>发出地名称快照（标签上的 FROM），建箱时取配置。</summary>
    [SugarColumn(IsNullable = false, Length = 100)]
    public string OriginName { get; set; } = string.Empty;

    /// <summary>见 <see cref="WarehousePackDestinationTypes"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public int DestinationType { get; set; }

    /// <summary>收货分店；去向为运输公司时是最终收货分店。</summary>
    [SugarColumn(IsNullable = true, Length = 50)]
    public string? DestinationStoreCode { get; set; }

    [SugarColumn(IsNullable = true, Length = 32)]
    public string? CarrierGUID { get; set; }

    /// <summary>可选关联的分店订货单，仅用于对照配货数量，不回写订货单。</summary>
    [SugarColumn(IsNullable = true, Length = 50)]
    public string? OrderGUID { get; set; }

    /// <summary>所在托盘；为空表示散箱。</summary>
    [SugarColumn(IsNullable = true, Length = 32)]
    public string? PalletGUID { get; set; }

    /// <summary>暂存货位编码（散箱时有效；上托盘后以托盘的货位为准）。</summary>
    [SugarColumn(IsNullable = true, Length = 50)]
    public string? LocationCode { get; set; }

    [SugarColumn(IsNullable = true, DecimalDigits = 2, Length = 9)]
    public decimal? WeightKg { get; set; }

    [SugarColumn(IsNullable = true, Length = 200)]
    public string? Remark { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(IsNullable = false, Length = 50)]
    public string CreatedByUserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 100)]
    public string CreatedByName { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true)]
    public DateTime? SealedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? SealedByUserGuid { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? SealedByName { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>箱内商品：同一箱同一商品只有一行，数量为件数。</summary>
[SugarTable("WarehousePackCartonItem")]
public sealed class WarehousePackCartonItem
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string CartonGUID { get; set; } = string.Empty;

    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string ProductCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public int Quantity { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime UpdatedAtUtc { get; set; }

    [SugarColumn(IsNullable = false, Length = 100)]
    public string UpdatedByName { get; set; } = string.Empty;
}

/// <summary>扫码打包的托盘：托盘号为 PL + 年月日 + 3 位流水。托盘是可选的，箱可以不上托盘。</summary>
[SugarTable("WarehousePackPallet")]
public sealed class WarehousePackPallet
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string PalletGUID { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 20)]
    public string PalletNo { get; set; } = string.Empty;

    /// <summary>见 <see cref="WarehousePackPalletStatuses"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public int Status { get; set; }

    [SugarColumn(IsNullable = false, Length = 100)]
    public string OriginName { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public int DestinationType { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? DestinationStoreCode { get; set; }

    [SugarColumn(IsNullable = true, Length = 32)]
    public string? CarrierGUID { get; set; }

    /// <summary>运输公司运单号，可在封托后补填。</summary>
    [SugarColumn(IsNullable = true, Length = 100)]
    public string? ConsignmentNo { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? LocationCode { get; set; }

    [SugarColumn(IsNullable = true, DecimalDigits = 2, Length = 9)]
    public decimal? WeightKg { get; set; }

    [SugarColumn(IsNullable = true, Length = 200)]
    public string? Remark { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(IsNullable = false, Length = 50)]
    public string CreatedByUserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 100)]
    public string CreatedByName { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true)]
    public DateTime? SealedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? SealedByUserGuid { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? SealedByName { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>第三方运输公司主数据。</summary>
[SugarTable("WarehousePackCarrier")]
public sealed class WarehousePackCarrier
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string CarrierGUID { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 100)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, Length = 200)]
    public string? Remark { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool IsActive { get; set; } = true;

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>打包操作记录（只追加）：箱 / 托盘的轨迹与审计。</summary>
[SugarTable("WarehousePackEvent")]
public sealed class WarehousePackEvent
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string EventGUID { get; set; } = string.Empty;

    /// <summary>见 <see cref="WarehousePackTargetTypes"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public int TargetType { get; set; }

    [SugarColumn(IsNullable = false, Length = 32)]
    public string TargetGUID { get; set; } = string.Empty;

    /// <summary>见 <see cref="WarehousePackEventActions"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public int Action { get; set; }

    [SugarColumn(IsNullable = true, Length = 400)]
    public string? Detail { get; set; }

    [SugarColumn(IsNullable = false, Length = 50)]
    public string OperatorUserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 100)]
    public string OperatorName { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// 箱号 / 托盘号发号表：一个「前缀 + 年月日」一行。
/// 由数据库行锁保证多台设备同时新建不重号（进程内计数做不到）。
/// </summary>
[SugarTable("WarehousePackSequence")]
public sealed class WarehousePackSequence
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 20)]
    public string SequenceKey { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public int LastValue { get; set; }
}

public static class WarehousePackCartonStatuses
{
    public const int Packing = 1;
    public const int Sealed = 2;
    public const int Voided = 9;
}

public static class WarehousePackPalletStatuses
{
    public const int Building = 1;
    public const int Sealed = 2;
    public const int Voided = 9;
}

public static class WarehousePackDestinationTypes
{
    public const int Unassigned = 0;
    public const int Store = 1;
    public const int Carrier = 2;

    public static bool IsKnown(int type) => type is >= Unassigned and <= Carrier;
}

public static class WarehousePackTargetTypes
{
    public const int Carton = 1;
    public const int Pallet = 2;
}

public static class WarehousePackEventActions
{
    public const int Created = 1;
    public const int ItemChanged = 2;
    public const int HeaderChanged = 3;
    public const int Sealed = 4;
    public const int Reopened = 5;
    public const int Voided = 6;
    public const int AddedToPallet = 7;
    public const int RemovedFromPallet = 8;
    public const int LocationChanged = 9;
}
