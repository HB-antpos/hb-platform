using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// App 系统推送设备：一个登录账号在一台设备上的 Expo 推送令牌。
/// 推送走 Expo 推送服务（不直连 APNs / FCM），令牌格式为 <c>ExponentPushToken[...]</c> 或 <c>ExpoPushToken[...]</c>。
/// <para>
/// <b>活动令牌全库唯一</b>（过滤唯一索引：<see cref="ExpoPushToken"/> WHERE <see cref="RevokedAtUtc"/> IS NULL）：
/// 门店手持会换人登录，同一令牌被另一个账号注册时，旧关联写 <see cref="RevokedAtUtc"/>（<c>reassigned</c>）并为新账号新建一行，
/// 因此上一个人的通知不会再发到下一个人手里。撤销是软撤销，行保留 90 天后才清理。
/// </para>
/// 表由独立迁移 20261017.001-app-push 创建，实体不进自动建表清单（tableTypes）；表结构以迁移为准。
/// </summary>
[SugarTable("AppPushDevice")]
public sealed class AppPushDevice
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string PushDeviceId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string UserGUID { get; set; } = string.Empty;

    /// <summary>见 <see cref="AppPushPlatforms"/>。</summary>
    [SugarColumn(IsNullable = false, Length = 16)]
    public string Platform { get; set; } = string.Empty;

    /// <summary>Expo 推送令牌（敏感：持有它即可向该设备发推送；日志与审计不得记录明文）。</summary>
    [SugarColumn(IsNullable = false, Length = 200)]
    public string ExpoPushToken { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? AppVersion { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? RuntimeVersion { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>最近一次注册 / 刷新令牌的时间；每个账号超过活动设备上限时，撤销最久未活跃的。</summary>
    [SugarColumn(IsNullable = false)]
    public DateTime LastSeenAtUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>撤销原因短码，见 <see cref="AppPushRevokeReasons"/>。</summary>
    [SugarColumn(IsNullable = true, Length = 32)]
    public string? RevokeReason { get; set; }
}

/// <summary>
/// App 推送发件箱：一条通知 × 一台设备 = 一行，与通知落库在同一个事务里写入，由后台任务按 Expo 推送服务的协议发送。
/// 进程重启不丢；发送失败按指数退避重试，达到上限标记失败。唯一键 (NotificationId, PushDeviceId) 保证同一通知不会对同一设备重复入箱。
/// 表由独立迁移 20261017.001-app-push 创建。
/// </summary>
[SugarTable("AppPushDelivery")]
public sealed class AppPushDelivery
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string DeliveryId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 32)]
    public string NotificationId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 32)]
    public string PushDeviceId { get; set; } = string.Empty;

    /// <summary>冗余接收人，便于排障按人统计。</summary>
    [SugarColumn(IsNullable = false, Length = 50)]
    public string UserGUID { get; set; } = string.Empty;

    /// <summary>见 <see cref="AppPushDeliveryStates"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public int State { get; set; } = AppPushDeliveryStates.Pending;

    /// <summary>已发送次数（每次向 Expo 发起请求并得到失败或成功结论都 +1）。</summary>
    [SugarColumn(IsNullable = false)]
    public int Attempts { get; set; }

    /// <summary>Pending 时 = 下次尝试时间；Sent 时复用为「回执到期时间」（到点才向 Expo 取回执）。</summary>
    [SugarColumn(IsNullable = false)]
    public DateTime NextAttemptAtUtc { get; set; }

    /// <summary>领取租约：到期前别的实例不会再领取同一行；崩溃后到期即可被重新领取。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? LockedUntilUtc { get; set; }

    /// <summary>Expo 返回的 ticket id（回执按它查）。</summary>
    [SugarColumn(IsNullable = true, Length = 64)]
    public string? TicketId { get; set; }

    /// <summary>只存短错误码（如 DeviceNotRegistered、http-503、stale），不存异常消息、令牌或回包内容。</summary>
    [SugarColumn(IsNullable = true, Length = 64)]
    public string? LastError { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? SentAtUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? ReceiptCheckedAtUtc { get; set; }
}

public static class AppPushPlatforms
{
    public const string Ios = "ios";
    public const string Android = "android";

    public static bool IsValid(string? platform) => platform is Ios or Android;
}

/// <summary>发件箱状态。0 / 1 是进行中，2 / 3 / 4 是已结束（可被清理任务删除）。</summary>
public static class AppPushDeliveryStates
{
    /// <summary>待发送（含退避等待重试）。</summary>
    public const int Pending = 0;

    /// <summary>Expo 已受理（拿到 ticket），等待回执。</summary>
    public const int Sent = 1;

    /// <summary>回执确认已交给 APNs / FCM（或回执窗口已过、不再追）。</summary>
    public const int Delivered = 2;

    /// <summary>失败：达到重试上限、令牌失效或 Expo 拒绝。</summary>
    public const int Failed = 3;

    /// <summary>主动跳过：通知已撤回 / 过期、设备已撤销、记录过于陈旧。</summary>
    public const int Skipped = 4;

    public static bool IsFinished(int state) => state is Delivered or Failed or Skipped;
}

/// <summary>设备撤销原因短码（写入 <see cref="AppPushDevice.RevokeReason"/>）。</summary>
public static class AppPushRevokeReasons
{
    /// <summary>用户退出登录，App 主动撤销。</summary>
    public const string Logout = "logout";

    /// <summary>Expo 报告令牌已失效（卸载 App、重装、关闭通知权限后重置等）。</summary>
    public const string DeviceNotRegistered = "device-not-registered";

    /// <summary>同一令牌被另一个账号注册（手持换人登录），旧关联转给新账号。</summary>
    public const string Reassigned = "reassigned";

    /// <summary>该账号活动设备超过上限，撤销最久未活跃的。</summary>
    public const string Limit = "limit";
}
