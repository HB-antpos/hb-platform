using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Models.Planogram;

/// <summary>
/// HBweb 库 dbo.Planogram：陈列方案头（不绑定分店，没有 StoreCode）。
/// 类名叫 PlanogramHeader 而不是 Planogram：同名类型会与 BlazorApp.Api.Models.Planogram / Services.Planogram 两个命名空间冲突；
/// 表名仍是 Planogram。
/// 表由迁移 20261104.001-planogram 创建，不进 tableTypes（启动不得代建表）。批 2 才有写入路径。
/// </summary>
[SugarTable("Planogram")]
internal sealed class PlanogramHeader : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string Guid { get; set; } = UuidHelper.GenerateUuid7();

    [SugarColumn(Length = 100)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(Length = 500, IsNullable = true)]
    public string? Notes { get; set; }

    [SugarColumn(Length = 200, IsNullable = true)]
    public string? Tags { get; set; }

    /// <summary>商品左右间距（mm），默认 10。</summary>
    public int GapXMm { get; set; } = 10;

    /// <summary>商品上下间距（mm），默认 10。</summary>
    public int GapYMm { get; set; } = 10;

    [SugarColumn(Length = 50)]
    public string ShelfModelGuid { get; set; } = string.Empty;

    public int BayCount { get; set; } = 1;

    /// <summary>0 草稿 / 1 已发布 / 2 已归档。</summary>
    public byte Status { get; set; }

    /// <summary>当前编辑版本号，每次发布 +1。</summary>
    public int Version { get; set; } = 1;

    [SugarColumn(IsNullable = true)]
    public int? PublishedVersion { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? PublishedAt { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? PublishedBy { get; set; }

    /// <summary>保存时服务端复算的冲突数。</summary>
    public int ConflictCount { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LastValidatedAt { get; set; }
}
