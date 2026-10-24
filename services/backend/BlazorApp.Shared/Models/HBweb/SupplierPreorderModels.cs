using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>供应商节日预订单状态：Draft 导入后未发布（下单人看不到）；Open 进行中；Closed 已截止锁定。</summary>
public static class SupplierPreorderFormStatuses
{
    public const string Draft = "Draft";
    public const string Open = "Open";
    public const string Closed = "Closed";
}

/// <summary>分店订单状态：Draft 草稿（含提交后又修改）；Submitted 已提交。</summary>
public static class SupplierPreorderStoreOrderStatuses
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
}

/// <summary>订单表商品与我方商品的匹配方式。</summary>
public static class SupplierPreorderMatchMethods
{
    public const string Barcode = "Barcode";
    public const string ItemNumber = "ItemNumber";
    public const string PrevYearItemCode = "PrevYearItemCode";
    public const string None = "None";
}

// 以下实体全部由迁移 20261023.001-supplier-preorder 建表，不进 SqlSugarContext 的自动建表清单（tableTypes）。

/// <summary>一张供应商节日订单表（如 KFC Christmas 2026、Dats Halloween 2026）。</summary>
[SugarTable("SupplierPreorderForm")]
public class SupplierPreorderForm
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string FormGuid { get; set; } = Guid.NewGuid().ToString("N");

    [SugarColumn(Length = 50)]
    public string LocalSupplierCode { get; set; } = string.Empty;

    [SugarColumn(Length = 200)]
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>节日：Christmas / Halloween / Easter / Diaries / Custom（与 HolidayType 枚举名一致）。</summary>
    [SugarColumn(Length = 30)]
    public string HolidayType { get; set; } = "Christmas";

    public int SeasonYear { get; set; }

    [SugarColumn(Length = 200)]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? QuoteNo { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? OfferDate { get; set; }

    /// <summary>下单截止（UTC）。过了截止时间，只有持有 Manage 的人还能改。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? DeadlineAtUtc { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? ContainerType { get; set; }

    [SugarColumn(DecimalDigits = 3, Length = 10, IsNullable = true)]
    public decimal? ContainerCapacityCbm { get; set; }

    [SugarColumn(Length = 200, IsNullable = true)]
    public string? PriceTerms { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? HistoryFrom { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? HistoryTo { get; set; }

    [SugarColumn(Length = 20)]
    public string Status { get; set; } = SupplierPreorderFormStatuses.Draft;

    /// <summary>导入时的列映射与原表定位（工作表名、表头行、各字段所在列、下单数量列），填回原表时使用。</summary>
    [SugarColumn(ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true)]
    public string? ColumnMappingJson { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? SourceFileGuid { get; set; }

    /// <summary>填回原表时写入的数量单位：Cartons 箱数 / Pieces 件数（Dats 原表各店列是件数）。</summary>
    [SugarColumn(Length = 20)]
    public string ExportQtyUnit { get; set; } = "Cartons";

    public int ItemCount { get; set; }

    /// <summary>修订版次数：0 = 原始导入，每上传一次修订版 +1（迁移 20261024.002 加列）。</summary>
    public int Revision { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? RevisedAtUtc { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? Notes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? CreatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? UpdatedBy { get; set; }

    public bool IsDeleted { get; set; }
}

/// <summary>订单表里的一款商品；价格、箱规按订单表快照，不随商品表变化。</summary>
[SugarTable("SupplierPreorderFormItem")]
public class SupplierPreorderFormItem
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string ItemGuid { get; set; } = Guid.NewGuid().ToString("N");

    [SugarColumn(Length = 50)]
    public string FormGuid { get; set; } = string.Empty;

    /// <summary>原表 Seq（没有就用导入顺序）。</summary>
    public int Seq { get; set; }

    /// <summary>原表中的行号（1 起）；粘贴导入时为 0。填回原表按它定位。</summary>
    public int SourceRowNo { get; set; }

    public int SortOrder { get; set; }

    [SugarColumn(Length = 100)]
    public string SupplierItemCode { get; set; } = string.Empty;

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? Barcode { get; set; }

    [SugarColumn(Length = 300)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(Length = 30, IsNullable = true)]
    public string? Unit { get; set; }

    /// <summary>箱规 = 起订量（按整箱下单）。Dats 取 Inner。</summary>
    public int CartonQty { get; set; }

    [SugarColumn(DecimalDigits = 4, Length = 10, IsNullable = true)]
    public decimal? CartonCbm { get; set; }

    [SugarColumn(DecimalDigits = 4, Length = 18)]
    public decimal Price { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? Category { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? PrevYearItemCode { get; set; }

    [SugarColumn(Length = 500, IsNullable = true)]
    public string? ImageSourceUrl { get; set; }

    // 新品图片转存 COS（迁移 20261024.001-supplier-preorder-item-image 加列）：原表图片单张约 700KB、可能防盗链或失效，
    // 后台下载后转成 JPEG 存进商品图片桶，页面缩略图由 COS 数据万象按需生成。

    /// <summary>转存后的 COS 地址；有我方商品图的商品不转存。</summary>
    [SugarColumn(Length = 500, IsNullable = true)]
    public string? ImageCosUrl { get; set; }

    /// <summary>null 未处理 / Done 已转存 / Skipped 不需要（有我方商品图）/ Failed 失败（重试 3 次后停止）。</summary>
    [SugarColumn(Length = 20, IsNullable = true)]
    public string? ImageCopyStatus { get; set; }

    public int ImageCopyAttempts { get; set; }

    [SugarColumn(Length = 300, IsNullable = true)]
    public string? ImageCopyError { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? ImageCopiedAtUtc { get; set; }

    [SugarColumn(Length = 500, IsNullable = true)]
    public string? Notes { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? MatchedProductCode { get; set; }

    [SugarColumn(Length = 20)]
    public string MatchMethod { get; set; } = SupplierPreorderMatchMethods.None;

    [SugarColumn(Length = 300, IsNullable = true)]
    public string? MatchNote { get; set; }

    // 参考商品（PR 2 启用；字段先随本迁移建好，避免再改表）
    [SugarColumn(Length = 100, IsNullable = true)]
    public string? RefProductCode { get; set; }

    [SugarColumn(Length = 20, IsNullable = true)]
    public string? RefSource { get; set; }

    [SugarColumn(DecimalDigits = 3, Length = 5, IsNullable = true)]
    public decimal? RefScore { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? RefSetBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? RefSetAtUtc { get; set; }

    public bool IsActive { get; set; } = true;

    // 修订版（迁移 20261024.002-supplier-preorder-revision 加列）：只记最近一次修订的改动，用来高亮

    /// <summary>最近一次有改动的修订版号（0 = 原始导入后没改过）。</summary>
    public int RevisionNo { get; set; }

    /// <summary>最近一次修订的改动，逗号分隔：Added / Restored / Removed / Price / Carton / Name / Barcode / Image / Cbm / Category。</summary>
    [SugarColumn(Length = 100, IsNullable = true)]
    public string? RevisionChange { get; set; }

    [SugarColumn(DecimalDigits = 4, Length = 18, IsNullable = true)]
    public decimal? PrevPrice { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? PrevCartonQty { get; set; }

    /// <summary>修订版里被删掉（下架）的时间；重新出现时清空。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? RemovedAtUtc { get; set; }
}

/// <summary>某张订单表开启的分店（这张表有哪些分店参与）。关闭即删除该行，变更写入授权日志。</summary>
[SugarTable("SupplierPreorderFormStore")]
public class SupplierPreorderFormStore
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string FormStoreGuid { get; set; } = Guid.NewGuid().ToString("N");

    [SugarColumn(Length = 50)]
    public string FormGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string StoreGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(Length = 200)]
    public string StoreName { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? CreatedBy { get; set; }
}

/// <summary>
/// 下单人员（独立授权）：只认这里，不看人员管理里的可管理分店、也不看是否店长。
/// 对所有供应商节日预订单长期有效。
/// </summary>
[SugarTable("SupplierPreorderOrderer")]
public class SupplierPreorderOrderer
{
    [SugarColumn(IsPrimaryKey = true, Length = 100)]
    public string UserGuid { get; set; } = string.Empty;

    /// <summary>可查看其他分店今年的下单与去年数据（只读）。</summary>
    public bool CanViewOtherStores { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? CreatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? UpdatedBy { get; set; }
}

/// <summary>下单人员可替哪些分店下单。</summary>
[SugarTable("SupplierPreorderOrdererStore")]
public class SupplierPreorderOrdererStore
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string OrdererStoreGuid { get; set; } = Guid.NewGuid().ToString("N");

    [SugarColumn(Length = 100)]
    public string UserGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string StoreGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(Length = 200)]
    public string StoreName { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? CreatedBy { get; set; }
}

/// <summary>授权变更记录：下单人员加/减分店、开关查看其他分店，订单表开启/关闭分店。</summary>
[SugarTable("SupplierPreorderAuthLog")]
public class SupplierPreorderAuthLog
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string LogGuid { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Orderer / FormStore。</summary>
    [SugarColumn(Length = 20)]
    public string Scope { get; set; } = string.Empty;

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? TargetUserGuid { get; set; }

    [SugarColumn(Length = 200, IsNullable = true)]
    public string? TargetUserName { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? FormGuid { get; set; }

    [SugarColumn(Length = 50)]
    public string Action { get; set; } = string.Empty;

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? Detail { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? OperatorUserGuid { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? OperatorName { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>某张订单表下某分店的订单。</summary>
[SugarTable("SupplierPreorderStoreOrder")]
public class SupplierPreorderStoreOrder
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string OrderGuid { get; set; } = Guid.NewGuid().ToString("N");

    [SugarColumn(Length = 50)]
    public string FormGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string StoreGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(Length = 200)]
    public string StoreName { get; set; } = string.Empty;

    [SugarColumn(Length = 20)]
    public string Status { get; set; } = SupplierPreorderStoreOrderStatuses.Draft;

    [SugarColumn(IsNullable = true)]
    public DateTime? SubmittedAtUtc { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? SubmittedBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LastEditedAtUtc { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? LastEditedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>分店订单明细：只存箱数，件数、金额、体积按订单表商品快照实时计算。</summary>
[SugarTable("SupplierPreorderStoreOrderLine")]
public class SupplierPreorderStoreOrderLine
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string LineGuid { get; set; } = Guid.NewGuid().ToString("N");

    [SugarColumn(Length = 50)]
    public string OrderGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string FormGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string ItemGuid { get; set; } = string.Empty;

    public int Cartons { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? UpdatedBy { get; set; }
}

/// <summary>按供应商记住的列映射：同一供应商、同样表头再次上传时自动套用。</summary>
[SugarTable("SupplierPreorderImportProfile")]
public class SupplierPreorderImportProfile
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string ProfileGuid { get; set; } = Guid.NewGuid().ToString("N");

    [SugarColumn(Length = 50)]
    public string LocalSupplierCode { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string HeaderSignature { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string MappingJson { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? UpdatedBy { get; set; }
}

/// <summary>上传的原表文件（填回原表导出用）。解析时先存，建表时再关联 FormGuid。</summary>
[SugarTable("SupplierPreorderSourceFile")]
public class SupplierPreorderSourceFile
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string FileGuid { get; set; } = Guid.NewGuid().ToString("N");

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? FormGuid { get; set; }

    [SugarColumn(Length = 260)]
    public string FileName { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string ContentType { get; set; } = string.Empty;

    public int SizeBytes { get; set; }

    // SqlSugar 按 byte[] 映射：SQL Server 为 varbinary(max)（与迁移一致），SQLite 测试库为 blob
    public byte[] Content { get; set; } = Array.Empty<byte>();

    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? UploadedBy { get; set; }
}

/// <summary>新品图片转存状态。</summary>
public static class SupplierPreorderImageCopyStatuses
{
    public const string Done = "Done";
    public const string Skipped = "Skipped";
    public const string Failed = "Failed";
}

/// <summary>参考商品来源：Auto 系统推荐（每次计算可换）；Manual 人工指定（系统不覆盖）；Cleared 人工清除（系统不再自动带上）。</summary>
public static class SupplierPreorderRefSources
{
    public const string Auto = "Auto";
    public const string Manual = "Manual";
    public const string Cleared = "Cleared";
}

/// <summary>
/// 去年历史快照（迁移 20261023.002-supplier-preorder-history 建表，不进 tableTypes）。
/// 一行 = 一张订单表 × 一家分店 × 一个我方商品（本品或参考商品）在历史窗口内的进货、日销与折扣；只存有数据的组合。
/// 另有一行 StoreCode = ProductCode = "*" 的表头行，记录这张订单表的计算状态（排队 / 计算中 / 完成 / 失败）与窗口。
/// </summary>
[SugarTable("SupplierPreorderHistorySnapshot")]
public class SupplierPreorderHistorySnapshot
{
    /// <summary>表头行的 StoreCode / ProductCode。</summary>
    public const string FormMetaKey = "*";

    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string SnapshotGuid { get; set; } = Guid.NewGuid().ToString("N");

    [SugarColumn(Length = 50)]
    public string FormGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string ProductCode { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string PayloadJson { get; set; } = string.Empty;

    public DateTime ComputedAtUtc { get; set; } = DateTime.UtcNow;

    [SugarColumn(Length = 50)]
    public string RuleVersion { get; set; } = string.Empty;
}
