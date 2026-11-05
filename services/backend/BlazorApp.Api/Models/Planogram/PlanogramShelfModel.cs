using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Models.Planogram;

/// <summary>
/// HBweb 库 dbo.PlanogramShelfModel：货架型号（洞洞板孔阵）。
/// 表由迁移 20261104.001-planogram 创建，不进 tableTypes（启动不得代建表）。
/// 继承 BaseEntity 只为复用审计列（CreatedAt / CreatedBy / UpdatedAt / UpdatedBy）与 SqlSugarContext 的自动填充；
/// 型号是硬删除（未被引用时），IsDeleted 恒为 false。
/// </summary>
[SugarTable("PlanogramShelfModel")]
internal sealed class PlanogramShelfModel : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string Guid { get; set; } = UuidHelper.GenerateUuid7();

    /// <summary>型号编码，全表唯一（如 G-S-PEG-90x30x120）。</summary>
    [SugarColumn(Length = 50)]
    public string Code { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>1 单面 / 2 双面（双面二期才按面编辑）。</summary>
    public byte SideType { get; set; } = 1;

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? Color { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? Source { get; set; }

    /// <summary>节宽（mm）。</summary>
    public int BayWidthMm { get; set; }

    /// <summary>总高（mm）。</summary>
    public int TotalHeightMm { get; set; }

    /// <summary>底板深（mm）。</summary>
    public int BaseDepthMm { get; set; }

    /// <summary>底板面离地高（mm）；板面高 = 总高 − 本值。</summary>
    public int BaseTopHeightMm { get; set; }

    /// <summary>立柱宽（mm）；板面宽 = 节宽 − 本值。</summary>
    public int UprightWidthMm { get; set; }

    /// <summary>横向孔距（mm），允许 25.4 这类英寸换算值。</summary>
    [SugarColumn(Length = 6, DecimalDigits = 2)]
    public decimal HolePitchXMm { get; set; }

    /// <summary>纵向孔距（mm）。</summary>
    [SugarColumn(Length = 6, DecimalDigits = 2)]
    public decimal HolePitchYMm { get; set; }

    [SugarColumn(Length = 6, DecimalDigits = 2)]
    public decimal HoleDiameterMm { get; set; }

    /// <summary>首孔距板左边（mm）。</summary>
    [SugarColumn(Length = 6, DecimalDigits = 2)]
    public decimal FirstHoleLeftMm { get; set; }

    /// <summary>首孔距板顶（mm）。</summary>
    [SugarColumn(Length = 6, DecimalDigits = 2)]
    public decimal FirstHoleTopMm { get; set; }

    /// <summary>横向孔数：缺省 / 0 时由服务按板面尺寸自动换算，也允许手改。</summary>
    public int HoleCols { get; set; }

    /// <summary>纵向孔数。</summary>
    public int HoleRows { get; set; }

    /// <summary>1 自板顶起算 / 2 自底板起算；只影响显示编号，内部仍存自顶行号。</summary>
    public byte RowNumberingFrom { get; set; } = 1;

    /// <summary>停用后已有方案可看、不可新建。</summary>
    public bool IsActive { get; set; } = true;
}
