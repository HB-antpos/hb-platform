using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Models.Planogram;

/// <summary>
/// HBweb 库 dbo.ProductDisplaySpec：商品陈列参数，按 ProductCode（业务键）与 Product 一对一。
/// 商品主档没有长宽高字段，所以单独建表，不动商品表。
/// 表由迁移 20261104.001-planogram 创建，不进 tableTypes（启动不得代建表）。
/// </summary>
[SugarTable("ProductDisplaySpec")]
internal sealed class ProductDisplaySpec : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>包装宽（mm）。</summary>
    public int WidthMm { get; set; }

    /// <summary>包装高（mm）。</summary>
    public int HeightMm { get; set; }

    /// <summary>包装厚（mm），用于建议每钩挂数。</summary>
    public int DepthMm { get; set; }

    /// <summary>吊孔距包装顶边（mm），默认 10。</summary>
    public int HangHoleOffsetMm { get; set; } = 10;

    /// <summary>0 无（只能放层板，二期） / 1 圆孔 / 2 欧式挂孔。</summary>
    public byte HangHoleType { get; set; } = 1;

    /// <summary>单件重（克），可空。</summary>
    [SugarColumn(IsNullable = true)]
    public int? UnitWeightG { get; set; }

    /// <summary>默认挂钩型号 Guid，可空。</summary>
    [SugarColumn(Length = 50, IsNullable = true)]
    public string? DefaultHookGuid { get; set; }

    /// <summary>按挂钩型号手填的每钩挂数覆盖，形如 {"&lt;hookGuid&gt;": 16}；空 = 全部用建议值。</summary>
    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? HookQtyOverridesJson { get; set; }

    /// <summary>1 人工量取 / 2 供应商资料 / 3 同套装主档。</summary>
    public byte Source { get; set; } = 1;

    [SugarColumn(Length = 500, IsNullable = true)]
    public string? Notes { get; set; }
}
