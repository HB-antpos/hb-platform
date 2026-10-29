using SqlSugar;

namespace BlazorApp.Api.Models.Myob;

/// <summary>
/// HBweb 库 dbo.MyobConnection 的实体：一个 ABN 的 MYOB 公司文件一条连接。
/// 表由迁移 20261027.002-myob-connection 创建，因此刻意不登记进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// </summary>
[SugarTable("MyobConnection")]
internal sealed class MyobConnection
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string ConnectionGuid { get; set; } = Guid.NewGuid().ToString();

    /// <summary>MYOB 公司文件 ID（API 路径里的 cf_uri）；唯一，重新授权按它找回同一条连接。</summary>
    [SugarColumn(Length = 50)]
    public string BusinessId { get; set; } = string.Empty;

    [SugarColumn(Length = 200)]
    public string BusinessName { get; set; } = string.Empty;

    /// <summary>公司 ABN，只含数字；读不到时可手动填写。</summary>
    [SugarColumn(Length = 14, IsNullable = true)]
    public string? Abn { get; set; }

    /// <summary>Data Protection 加密后的 refresh token；断开连接时清空。绝不明文落库、绝不写日志。</summary>
    // 库里是 nvarchar(max)（由迁移建表）；这里不写 ColumnDataType，免得 SQLite 测试库建表失败。
    [SugarColumn(IsNullable = true)]
    public string? RefreshTokenProtected { get; set; }

    /// <summary>Active / NeedsReauth / Disconnected，见 <c>MyobConnectionStatuses</c>。</summary>
    [SugarColumn(Length = 20)]
    public string Status { get; set; } = "Active";

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? AuthorizedByUserGuid { get; set; }

    public DateTime AuthorizedAt { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LastRefreshedAt { get; set; }

    [SugarColumn(Length = 500, IsNullable = true)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? UpdatedAt { get; set; }
}
