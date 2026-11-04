using BlazorApp.Shared.Helper;
using SqlSugar;

namespace BlazorApp.Api.Models.Planogram;

/// <summary>
/// HBweb 库 dbo.PlanogramItem：方案明细，一钩一行（连排靠「向右复制 / 整行均分」生成多行）。
/// 每次保存整体重写，所以不带审计列。
/// 表由迁移 20261104.001-planogram 创建，不进 tableTypes（启动不得代建表）。批 2 才有写入路径。
/// </summary>
[SugarTable("PlanogramItem")]
internal sealed class PlanogramItem
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string Guid { get; set; } = UuidHelper.GenerateUuid7();

    [SugarColumn(Length = 50)]
    public string PlanogramGuid { get; set; } = string.Empty;

    /// <summary>节号，自左起 1。</summary>
    public int BayIndex { get; set; } = 1;

    /// <summary>面：单面恒 0（双面二期）。</summary>
    public byte Face { get; set; }

    /// <summary>1 挂钩商品 / 2 层板商品（二期）。</summary>
    public byte ItemType { get; set; } = 1;

    [SugarColumn(Length = 50)]
    public string ProductCode { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string HookModelGuid { get; set; } = string.Empty;

    /// <summary>锚点孔位列，自左起 1。</summary>
    public int HoleCol { get; set; }

    /// <summary>锚点孔位行，自板顶起 1。</summary>
    public int HoleRow { get; set; }

    /// <summary>保存时的每钩挂数快照；空 = 用参数表。</summary>
    [SugarColumn(IsNullable = true)]
    public int? PerHookQty { get; set; }

    /// <summary>保存时商品宽高厚快照，用于「参数已变」提示。</summary>
    [SugarColumn(Length = 500, IsNullable = true)]
    public string? SpecSnapshotJson { get; set; }

    public int SortOrder { get; set; }
}
