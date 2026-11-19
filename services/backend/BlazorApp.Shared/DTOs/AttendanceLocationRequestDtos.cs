namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 创建位置请求（POST api/react/v1/attendance/location-requests）。
    /// 需要权限 Attendance.Punch.RequestLocation；店长只能请求自己管理分店的员工，管理员不限分店。
    /// </summary>
    public class CreateAttendanceLocationRequestDto
    {
        /// <summary>目标员工当前上班所在的门店（范围校验以此为准）。</summary>
        public string? StoreCode { get; set; }

        public string? TargetUserGuid { get; set; }
    }

    /// <summary>
    /// 请求视图（Web 列表、员工待办、创建 / 响应结果共用）。
    /// Status 是对外的「有效状态」：Pending / Responded / Expired / Cancelled，Expired 由服务端按失效时间推导。
    /// 员工待办接口只返回 Pending 且未过期且仍在班中的请求；坐标字段只在 Responded 后有值。
    /// </summary>
    public class AttendanceLocationRequestDto
    {
        public string RequestGuid { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public string TargetUserGuid { get; set; } = string.Empty;
        public string? TargetUserName { get; set; }
        public string RequestedByUserGuid { get; set; } = string.Empty;
        public string? RequestedByName { get; set; }
        public string Status { get; set; } = string.Empty;

        /// <summary>Cancelled 时的原因短码，目前只有 ClockedOut。</summary>
        public string? StatusReason { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime? RespondedAtUtc { get; set; }

        public string? SampleGuid { get; set; }
        public double? LocationLatitude { get; set; }
        public double? LocationLongitude { get; set; }
        public double? LocationAccuracy { get; set; }
        public DateTime? LocationCapturedAtUtc { get; set; }

        /// <summary>仅响应接口使用：这次响应是对已响应请求的幂等重放（没有再写样本）。</summary>
        public bool AlreadyResponded { get; set; }
    }

    /// <summary>Web 端请求历史查询；只返回调用者管辖分店内的记录，按创建时间倒序。</summary>
    public class AttendanceLocationRequestQueryDto
    {
        public string? StoreCode { get; set; }
        public string? TargetUserGuid { get; set; }

        /// <summary>只看最近 N 条，默认 50，上限 200。</summary>
        public int? Limit { get; set; }
    }

    /// <summary>
    /// 员工响应位置请求（POST api/react/v1/attendance/my/location-requests/{requestGuid}/respond）。
    /// 必须是响应时在前台取的新鲜定位：采集时间不得早于请求创建时间。
    /// </summary>
    public class RespondAttendanceLocationRequestDto
    {
        public string? HardwareId { get; set; }
        public string? SystemDeviceNumber { get; set; }
        public string? DeviceSystem { get; set; }
        public double? LocationLatitude { get; set; }
        public double? LocationLongitude { get; set; }
        public double? LocationAccuracy { get; set; }
        public bool? IsMocked { get; set; }

        /// <summary>必须为 granted（只需「使用 App 期间」权限）。</summary>
        public string? LocationPermissionStatus { get; set; }

        public DateTime? LocationCapturedAtUtc { get; set; }
    }
}
