using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 门店小票资料「下发」快照：只追加，每次下发写一行，Version 对每家店从 1 递增。
/// 收银端（WPF / 手持 / iPad）只认快照，不直接读 Store；回滚＝把旧内容在 Web 改回后再下发一次（生成新版本号），不删行。
/// 独立建表而不给 [Store] 加列：Store 实体由 HBweb 与 Hbpos.Api 共用，加列会让未迁移环境的 POS 查询报列不存在。
/// 实体不进 SqlSugarContext 的自动建表清单，由版本号迁移 20261007.001-store-receipt-profile-release 创建；
/// 列宽与 Store 表对应列一致。
/// </summary>
[SugarTable("StoreReceiptProfileRelease")]
public sealed class StoreReceiptProfileRelease
{
    /// <summary>主键第一列：门店代码（对应 Store.StoreCode）。</summary>
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>主键第二列：该店的下发版本号，从 1 递增。</summary>
    [SugarColumn(IsPrimaryKey = true, IsNullable = false)]
    public int Version { get; set; }

    [SugarColumn(IsNullable = false, Length = 100)]
    public string StoreName { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? BrandName { get; set; }

    [SugarColumn(IsNullable = true, Length = 500)]
    public string? Address { get; set; }

    [SugarColumn(IsNullable = true, Length = 200)]
    public string? Phone { get; set; }

    [SugarColumn(IsNullable = true, Length = 20)]
    public string? ABN { get; set; }

    [SugarColumn(IsNullable = true, Length = 500)]
    public string? ReturnPolicy { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime PublishedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? PublishedBy { get; set; }
}
