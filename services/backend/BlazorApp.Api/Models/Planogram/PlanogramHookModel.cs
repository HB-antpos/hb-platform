using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Models.Planogram;

/// <summary>
/// HBweb 库 dbo.PlanogramHookModel：挂钩型号（双钩 / 单钩，上臂可带价签架）。
/// 表由迁移 20261104.001-planogram 创建，不进 tableTypes（启动不得代建表）。
/// 尺寸都是 decimal(6,2)：挂钩常按英寸标注（8 英寸 = 203.2 mm）。
/// </summary>
[SugarTable("PlanogramHookModel")]
internal sealed class PlanogramHookModel : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string Guid { get; set; } = UuidHelper.GenerateUuid7();

    /// <summary>型号编码，全表唯一（如 HK-D-PT-200）。</summary>
    [SugarColumn(Length = 50)]
    public string Code { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? Color { get; set; }

    /// <summary>1 单钩 / 2 双钩。</summary>
    public byte ProngCount { get; set; } = 2;

    /// <summary>上臂带价签架。</summary>
    public bool HasPriceTagArm { get; set; } = true;

    /// <summary>挂货臂长 L（mm）。</summary>
    [SugarColumn(Length = 6, DecimalDigits = 2)]
    public decimal ArmLengthMm { get; set; }

    /// <summary>钩高 H：挂货臂相对挂孔的下落距离（mm）。</summary>
    [SugarColumn(Length = 6, DecimalDigits = 2)]
    public decimal DropMm { get; set; }

    [SugarColumn(Length = 6, DecimalDigits = 2)]
    public decimal WireDiameterMm { get; set; }

    /// <summary>臂端不挂货的预留（mm），建议挂数 = ⌊(臂长 − 预留) ÷ 包装厚⌋。</summary>
    [SugarColumn(Length = 6, DecimalDigits = 2)]
    public decimal TipReserveMm { get; set; }

    /// <summary>价签架宽（mm）；不带价签臂时可为 0。</summary>
    [SugarColumn(Length = 6, DecimalDigits = 2)]
    public decimal TagWidthMm { get; set; }

    /// <summary>价签架高（mm）；不带价签臂时可为 0。</summary>
    [SugarColumn(Length = 6, DecimalDigits = 2)]
    public decimal TagHeightMm { get; set; }

    /// <summary>价签架底边高于挂货臂的距离（mm），默认 10；不带价签臂时可为 0。</summary>
    [SugarColumn(Length = 6, DecimalDigits = 2)]
    public decimal TagBottomAboveArmMm { get; set; } = 10m;

    /// <summary>横向占孔数（双钩 = 2）。</summary>
    public int HoleSpanX { get; set; } = 2;

    /// <summary>纵向占孔数。</summary>
    public int HoleSpanY { get; set; } = 1;

    /// <summary>承重（kg），可空。</summary>
    [SugarColumn(Length = 5, DecimalDigits = 2, IsNullable = true)]
    public decimal? LoadKg { get; set; }

    /// <summary>停用后已有方案可看、不可新建。</summary>
    public bool IsActive { get; set; } = true;
}
