using SqlSugar;

namespace BlazorApp.Shared.Models;

[SugarTable("EmployeeProfileSensitiveChangeRequest")]
public sealed class EmployeeProfileSensitiveChangeRequest
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int RequestId { get; set; }

    [SugarColumn(IsNullable = false, Length = 50)]
    public string UserGUID { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, Length = 20)]
    public string? BankBsb { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? BankAccountNumber { get; set; }

    [SugarColumn(IsNullable = true, Length = 200)]
    public string? SuperannuationCompanyName { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? SuperannuationCompanyCode { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? SuperannuationAccountNumber { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? IdentityType { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? IdentityId { get; set; }

    /// <summary>
    /// 申请中的生日。只有 ChangedFieldsJson 含 birthday 时才代表员工的修改意图；
    /// 加列前的历史申请此列为空，审批时不得据此清空正式生日。
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "date")]
    public DateTime? Birthday { get; set; }

    [SugarColumn(IsNullable = true, Length = 500)]
    public string? IdentityPhotoObjectKey { get; set; }

    public bool RemoveIdentityPhoto { get; set; }

    [SugarColumn(IsNullable = true, Length = 1000)]
    public string? ChangedFieldsJson { get; set; }

    public EmployeeProfileSensitiveChangeStatus Status { get; set; }
    public int BaseSensitiveRevision { get; set; }
    public DateTime SubmittedAt { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? SubmittedBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? ReviewedAt { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? ReviewedBy { get; set; }

    [SugarColumn(IsNullable = true, Length = 1000)]
    public string? ReviewReason { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? SupersededAt { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? SupersededBy { get; set; }
}

public enum EmployeeProfileSensitiveChangeStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Superseded = 3,
}
