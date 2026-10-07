using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 存单与支出收据的图片。兼任上传票据：签发签名时先写一行 Pending，对象先传到私有的 cash/pending/ 下；
/// 提交存款或支出时服务端校验类型、大小、图片内容后转正到私有正式路径（Promoted），再挂到单据上（Linked）。
/// 图片全程私有，读取只走带过期时间的签名下载地址；没被确认的 pending 对象靠 COS 生命周期规则清理。
/// 实体不进 SqlSugarContext 的自动建表清单，由版本号迁移 20261008.001-store-cash-management 创建。
/// </summary>
[SugarTable("StoreCashAttachment")]
public sealed class StoreCashAttachment
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string AttachmentGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>Pending / Promoted / Linked。</summary>
    [SugarColumn(IsNullable = false, Length = 20)]
    public string Status { get; set; } = "Pending";

    [SugarColumn(IsNullable = false, Length = 300)]
    public string PendingObjectKey { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 300)]
    public string FinalObjectKey { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string ContentType { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public long FileSize { get; set; }

    /// <summary>Slip / Expense；未挂单据时为 NULL。</summary>
    [SugarColumn(IsNullable = true, Length = 20)]
    public string? OwnerType { get; set; }

    /// <summary>OwnerType = Slip 时是 SlipGuid，Expense 时是 ExpenseGuid。</summary>
    [SugarColumn(IsNullable = true, Length = 50)]
    public string? OwnerGuid { get; set; }

    [SugarColumn(IsNullable = false)]
    public int SortOrder { get; set; }

    [SugarColumn(IsNullable = false, Length = 50)]
    public string UploadedByUserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>票据有效期：超过后不能再被业务单据确认。</summary>
    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime ExpiresAtUtc { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? LinkedAtUtc { get; set; }
}
