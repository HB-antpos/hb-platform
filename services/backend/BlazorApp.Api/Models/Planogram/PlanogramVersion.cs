using BlazorApp.Shared.Helper;
using SqlSugar;

namespace BlazorApp.Api.Models.Planogram;

/// <summary>
/// HBweb 库 dbo.PlanogramVersion：发布快照。LayoutJson 含头、明细、所用货架 / 挂钩型号与商品参数的完整快照，
/// 以后型号或参数改了，历史版本的 PDF 仍能按当时数据重出。
/// 表由迁移 20261104.001-planogram 创建，不进 tableTypes（启动不得代建表）。批 3 才有写入路径。
/// </summary>
[SugarTable("PlanogramVersion")]
internal sealed class PlanogramVersion
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string Guid { get; set; } = UuidHelper.GenerateUuid7();

    [SugarColumn(Length = 50)]
    public string PlanogramGuid { get; set; } = string.Empty;

    public int Version { get; set; }

    /// <summary>完整布局快照。库里是 nvarchar(max)（由迁移建表）；这里不写 ColumnDataType，免得 SQLite 测试库建表失败。</summary>
    public string LayoutJson { get; set; } = string.Empty;

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? PublishedBy { get; set; }

    public DateTime PublishedAt { get; set; }
}
