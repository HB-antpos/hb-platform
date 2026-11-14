using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 扫码打包的箱：一个纸箱一行。箱号（BX + 年月日 + 流水）就是箱标签上的条码内容，重新开箱后不变。
/// 每只箱有「所属地点」（分店编码或总仓）：店间调货时发出方发出、接收方扫码签收，签收后所属地点换成接收方。
/// 位置挂在箱 / 托盘上，商品位置由箱明细推导；本功能不改任何库存。
/// </summary>
[SugarTable("PackCarton")]
public sealed class PackCarton
{
    /// <summary>UUIDv7 的 32 位十六进制串，由打包服务插入时生成（Shared 同时编译 net8.0，不能在这里做默认值）。</summary>
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string CartonGUID { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 20)]
    public string CartonNo { get; set; } = string.Empty;

    /// <summary>见 <see cref="PackCartonStatuses"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public int Status { get; set; }

    /// <summary>当前所属地点：分店编码，或 <see cref="PackOwners.Warehouse"/>。在路上时仍属于发出方，签收后才换成接收方。</summary>
    [SugarColumn(IsNullable = false, Length = 50)]
    public string OwnerCode { get; set; } = string.Empty;

    /// <summary>最近一次的发出方（标签上的 FROM）：建箱时是建箱地点，发出时更新为发出时的所属地点。</summary>
    [SugarColumn(IsNullable = false, Length = 50)]
    public string OriginCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 100)]
    public string OriginName { get; set; } = string.Empty;

    /// <summary>见 <see cref="PackDestinationTypes"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public int DestinationType { get; set; }

    /// <summary>收货地点：去向为分店时是分店编码，去向为总仓时是 <see cref="PackOwners.Warehouse"/>。</summary>
    [SugarColumn(IsNullable = true, Length = 50)]
    public string? DestinationCode { get; set; }

    /// <summary>怎么送：交给哪家运输公司；为空表示自己送 / 对方来取。</summary>
    [SugarColumn(IsNullable = true, Length = 32)]
    public string? CarrierGUID { get; set; }

    /// <summary>可选关联的分店订货单（仓库按订货单拣货装箱时用），仅用于对照配货数量，不回写订货单。</summary>
    [SugarColumn(IsNullable = true, Length = 50)]
    public string? OrderGUID { get; set; }

    /// <summary>所在托盘；为空表示散箱。</summary>
    [SugarColumn(IsNullable = true, Length = 32)]
    public string? PalletGUID { get; set; }

    /// <summary>存放位置名称（分店取本店存放位置，总仓取货位编码）；上托盘后以托盘的位置为准。</summary>
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

    [SugarColumn(IsNullable = true)]
    public DateTime? DispatchedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? DispatchedByName { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? ReceivedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? ReceivedByName { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>箱内商品：同一箱同一商品只有一行，数量为件数。</summary>
[SugarTable("PackCartonItem")]
public sealed class PackCartonItem
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

/// <summary>扫码打包的托盘：托盘号为 PL + 年月日 + 流水。托盘是可选的，箱可以不上托盘。</summary>
[SugarTable("PackPallet")]
public sealed class PackPallet
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string PalletGUID { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 20)]
    public string PalletNo { get; set; } = string.Empty;

    /// <summary>见 <see cref="PackPalletStatuses"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public int Status { get; set; }

    [SugarColumn(IsNullable = false, Length = 50)]
    public string OwnerCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string OriginCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 100)]
    public string OriginName { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public int DestinationType { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? DestinationCode { get; set; }

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

    [SugarColumn(IsNullable = true)]
    public DateTime? DispatchedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? DispatchedByName { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? ReceivedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? ReceivedByName { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>第三方运输公司主数据（全公司共用）。</summary>
[SugarTable("PackCarrier")]
public sealed class PackCarrier
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

/// <summary>分店自己维护的存放位置（如「后仓 A 架」）；总仓继续用现有货位表，不进这张表。</summary>
[SugarTable("PackLocation")]
public sealed class PackLocation
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string LocationGUID { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string OwnerCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string Name { get; set; } = string.Empty;

    /// <summary>位置标签上的条码（LC + 6 位流水），扫它就能选中这个位置。</summary>
    [SugarColumn(IsNullable = false, Length = 20)]
    public string Barcode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public bool IsActive { get; set; } = true;

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>打包操作记录（只追加）：箱 / 托盘的轨迹与审计。</summary>
[SugarTable("PackEvent")]
public sealed class PackEvent
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string EventGUID { get; set; } = string.Empty;

    /// <summary>见 <see cref="PackTargetTypes"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public int TargetType { get; set; }

    [SugarColumn(IsNullable = false, Length = 32)]
    public string TargetGUID { get; set; } = string.Empty;

    /// <summary>见 <see cref="PackEventActions"/>。</summary>
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
/// 编号发号表：一个「前缀 + 年月日」一行（位置标签用「LC」一行）。
/// 由数据库行锁保证多台设备同时新建不重号（进程内计数做不到）。
/// </summary>
[SugarTable("PackSequence")]
public sealed class PackSequence
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 20)]
    public string SequenceKey { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public int LastValue { get; set; }
}

public static class PackOwners
{
    /// <summary>总仓的所属地点编码；系统里总仓不是一家分店，用这个保留编码表示。</summary>
    public const string Warehouse = "WAREHOUSE";

    public static bool IsWarehouse(string? code) =>
        string.Equals(code?.Trim(), Warehouse, StringComparison.OrdinalIgnoreCase);
}

public static class PackCartonStatuses
{
    public const int Packing = 1;
    public const int Sealed = 2;

    /// <summary>已发出、还没被接收方签收；所属地点仍是发出方。</summary>
    public const int InTransit = 3;
    public const int Voided = 9;
}

public static class PackPalletStatuses
{
    public const int Building = 1;
    public const int Sealed = 2;
    public const int InTransit = 3;
    public const int Voided = 9;
}

public static class PackDestinationTypes
{
    /// <summary>还没决定去哪，发出前补上。</summary>
    public const int Unassigned = 0;

    /// <summary>留在本地存放（本店后仓 / 总仓货位），不发出。</summary>
    public const int Local = 1;

    /// <summary>其他分店；<see cref="PackCarton.DestinationCode"/> 是分店编码。</summary>
    public const int Store = 2;

    /// <summary>总仓；<see cref="PackCarton.DestinationCode"/> 固定为 <see cref="PackOwners.Warehouse"/>。</summary>
    public const int Warehouse = 3;

    public static bool IsKnown(int type) => type is >= Unassigned and <= Warehouse;

    /// <summary>能发出的去向：只有其他分店和总仓。</summary>
    public static bool IsShippable(int type) => type is Store or Warehouse;
}

public static class PackTargetTypes
{
    public const int Carton = 1;
    public const int Pallet = 2;
}

public static class PackEventActions
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
    public const int Dispatched = 10;
    public const int Received = 11;
}
