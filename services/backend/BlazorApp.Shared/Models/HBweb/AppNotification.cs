using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 通知平台的一条通知：内容只存一份，接收人另存在 <see cref="AppNotificationRecipient"/>。
/// 实时通道（SignalR）与后续的 App 推送都只是投递方式，站内消息以本表为准，客户端断线后按收件箱补齐。
/// </summary>
[SugarTable("AppNotification")]
public sealed class AppNotification
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string NotificationId { get; set; } = string.Empty;

    /// <summary>业务类型，见 <see cref="AppNotificationTypes"/>；客户端据此决定图标与跳转页面。</summary>
    [SugarColumn(IsNullable = false, Length = 64)]
    public string Type { get; set; } = string.Empty;

    /// <summary>见 <see cref="AppNotificationPriorities"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public int Priority { get; set; } = AppNotificationPriorities.Normal;

    [SugarColumn(IsNullable = false, Length = 200)]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 1000)]
    public string Body { get; set; } = string.Empty;

    /// <summary>跳转参数（JSON 对象，键值均为字符串，发布时限 4000 字符）；不放敏感数据，详情由客户端凭登录态另行拉取。</summary>
    [SugarColumn(IsNullable = true, Length = 4000)]
    public string? DataJson { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? StoreCode { get; set; }

    /// <summary>业务幂等键：同键重复发布只保留第一条，也是撤回时定位通知的依据。</summary>
    [SugarColumn(IsNullable = true, Length = 200)]
    public string? DedupeKey { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>业务已失效（如审批被系统自动取消）时撤回；撤回后不再出现在收件箱与未读数中。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? RevokedAtUtc { get; set; }

    [SugarColumn(IsNullable = false, Length = 100)]
    public string CreatedBy { get; set; } = "System";
}

/// <summary>通知接收人：每人一行，已读状态按人记录。</summary>
[SugarTable("AppNotificationRecipient")]
public sealed class AppNotificationRecipient
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string NotificationId { get; set; } = string.Empty;

    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string UserGUID { get; set; } = string.Empty;

    /// <summary>冗余通知创建时间，收件箱按人 + 时间倒序翻页时不必回表排序。</summary>
    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? ReadAtUtc { get; set; }
}

public static class AppNotificationPriorities
{
    public const int Normal = 0;

    /// <summary>需要立即注意（如黑名单告警）：客户端弹窗并提示音，App 推送走高优先级通道。</summary>
    public const int High = 1;

    public static bool IsValid(int priority) => priority is Normal or High;
}

/// <summary>通知业务类型。新增类型时客户端按未知类型兜底展示，不会因为不认识而丢弃。</summary>
public static class AppNotificationTypes
{
    /// <summary>后台「发一条测试通知给自己」，用于验证各端接收链路。</summary>
    public const string Test = "system.test";

    /// <summary>不受欢迎人员名单疑似命中（需人工复核）；只发给该店持有 FaceMatch.Unwelcome.Review 的人，文案不含姓名与事件细节。</summary>
    public const string FaceUnwelcomeHit = "face.unwelcome-hit";

    /// <summary>不受欢迎人员名单条目到期前 30 天的复核提醒（发给提交人与批准人，不含姓名与事件细节）。</summary>
    public const string FaceUnwelcomeExpiring = "face.unwelcome-expiring";

    /// <summary>考勤审批已提交（请假 / 补卡 / 用餐加工时），发给有权审该门店的审批人；文案为英文，不含原因、备注等个人信息。</summary>
    public const string ApprovalSubmitted = "approval.submitted";

    /// <summary>考勤审批已处理（通过 / 驳回），发给申请人；文案为英文，不含审批意见。</summary>
    public const string ApprovalDecided = "approval.decided";

    /// <summary>
    /// 门店价格更新合并推送：仓库调价后本店有商品需要改价或换货架标签（同一门店窗口内多条任务合并为一条）。
    /// 只发给该店持有 StoreProducts.PriceUpdates 的人；正文为英文，只带数量，不含商品与价格明细。
    /// </summary>
    public const string StorePriceUpdates = "store.price_updates";

    /// <summary>新品到店每日推送（门店本地 8 点）：只发给该店持有 Container.MobileNewProductsView 的人，Data 只带门店、本地日期与新增数量。</summary>
    public const string ContainerNewProducts = "container.new_products";
}
