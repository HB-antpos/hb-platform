using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.Attendance;

/// <summary>
/// 「服务端要求员工取一次高精度位置」的集中口径：有效期、限频、响应时间容差、错误码，以及纯函数规则。
/// 数值全部集中在这里（不散落在服务里），要调整只改这一处；不碰数据库，方便单测。
/// </summary>
public static class AttendanceLocationRequestRules
{
    /// <summary>请求默认有效期：员工在此之前没有响应就视为「未响应」。</summary>
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromMinutes(15);

    /// <summary>同一员工两次请求的最小间隔：防止骚扰。含已响应 / 已作废的请求，从创建时间起算。</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 响应里的定位采集时间可以比请求创建时间早多少（设备时钟与服务器的轻微偏差）。
    /// 更早的点不是「响应这次请求」取的，拒绝：防止拿请求之前的旧位置交差。
    /// </summary>
    public static readonly TimeSpan CapturedBeforeRequestTolerance = TimeSpan.FromSeconds(30);

    /// <summary>响应里的定位采集时间可以比服务器时间晚多少（设备时钟轻微超前），与班中批量上传同一口径。</summary>
    public static readonly TimeSpan CapturedFutureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 请求通知在员工收件箱里的保留时长。故意远长于请求有效期：通知是员工可查的留痕
    /// （谁在什么时候请求了位置），不能随请求过期就从收件箱消失。
    /// </summary>
    public static readonly TimeSpan NotificationRetention = TimeSpan.FromDays(30);

    public const int DefaultListLimit = 50;
    public const int MaxListLimit = 200;

    /// <summary>响应后写入班中样本的事件类型；Web「班中定位样本」抽屉据此标注「服务端请求」。</summary>
    public const string SampleEventType = "ServerRequest";

    /// <summary>通知创建者标识（AppNotification.CreatedBy）。</summary>
    public const string NotificationCreator = "system:attendance-location-request";

    public static class ErrorCodes
    {
        public const string StoreRequired = "STORE_REQUIRED";
        public const string TargetUserRequired = "TARGET_USER_REQUIRED";
        public const string CannotRequestSelf = "CANNOT_REQUEST_SELF";

        /// <summary>目标员工当前没有处在上班中（创建时）/ 响应到达时已下班（响应时）。</summary>
        public const string ActiveShiftRequired = "ACTIVE_SHIFT_REQUIRED";

        public const string Pending = "LOCATION_REQUEST_PENDING";
        public const string RateLimited = "LOCATION_REQUEST_RATE_LIMITED";
        public const string NotFound = "LOCATION_REQUEST_NOT_FOUND";
        public const string Expired = "LOCATION_REQUEST_EXPIRED";
        public const string Cancelled = "LOCATION_REQUEST_CANCELLED";

        /// <summary>响应的定位采集时间早于请求创建时间：不是为这次请求取的。</summary>
        public const string LocationStale = "LOCATION_STALE";

        public const string SampleInFuture = "SAMPLE_IN_FUTURE";
    }

    /// <summary>对外展示的有效状态：存储为 Pending 但已过失效时间的，对外显示为 Expired（「已过期」不落库）。</summary>
    public static string ResolveEffectiveStatus(string? storedStatus, DateTime expiresAtUtc, DateTime nowUtc)
    {
        if (string.Equals(storedStatus, AttendanceLocationRequestStatuses.Pending, StringComparison.Ordinal))
        {
            return expiresAtUtc <= nowUtc
                ? AttendanceLocationRequestStatuses.Expired
                : AttendanceLocationRequestStatuses.Pending;
        }

        return storedStatus ?? string.Empty;
    }

    /// <summary>通知幂等键：同一请求只会发布一条通知。</summary>
    public static string DedupeKey(string requestGuid) => $"attendance:location-request:{requestGuid}";

    /// <summary>限频剩余时间（向上取整到分钟，至少 1 分钟），用于错误提示。</summary>
    public static int RemainingWholeMinutes(DateTime lastCreatedAtUtc, DateTime nowUtc)
    {
        var remaining = MinInterval - (nowUtc - lastCreatedAtUtc);
        return remaining <= TimeSpan.Zero ? 1 : (int)Math.Ceiling(remaining.TotalMinutes);
    }
}
