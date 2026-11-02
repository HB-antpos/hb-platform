using SqlSugar;

namespace BlazorApp.Api.Models;

/// <summary>
/// HBweb 库 dbo.PosReceiptShareLink：POS 订单在线小票分享链接，每次生成一行。
/// 令牌明文不落库：公开接口按 SHA-256 哈希查找；店员再次打开弹层要重新显示同一条链接，
/// 所以另存一份 DataProtection 加密的密文，密钥环在应用服务器上，库泄露也还原不出可用链接。
/// 表由迁移 20261030.005-pos-receipt-share-link 创建，不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// </summary>
[SugarTable("PosReceiptShareLink")]
internal sealed class PosReceiptShareLink
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>POSM sales_order.OrderGuid（字符串形式的 GUID）。</summary>
    [SugarColumn(Length = 50)]
    public string OrderGuid { get; set; } = string.Empty;

    /// <summary>订单所属分店编号，便于按店审计；订单没有分店时为空。</summary>
    [SugarColumn(Length = 20, IsNullable = true)]
    public string? BranchCode { get; set; }

    /// <summary>SHA-256(令牌) 的小写十六进制，唯一。</summary>
    [SugarColumn(Length = 64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>DataProtection 加密的令牌密文，仅供员工端重新显示链接；用途隔离见 PosReceiptShareService。</summary>
    [SugarColumn(Length = 500)]
    public string TokenProtected { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>生成链接的员工用户名。</summary>
    [SugarColumn(Length = 100, IsNullable = true)]
    public string? CreatedBy { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>被「重新生成」撤销的时间；非空即失效。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>客人打开次数（仅统计成功读取小票的请求）。</summary>
    public int OpenCount { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LastOpenedAtUtc { get; set; }
}
