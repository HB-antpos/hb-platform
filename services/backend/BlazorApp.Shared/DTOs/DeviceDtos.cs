using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 设备数据传输对象 - 与前端DeviceData格式对应
    /// </summary>
    public class DeviceDataDto
    {
        /// <summary>
        /// 设备ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 系统设备编号
        /// </summary>
        public string SystemDeviceNumber { get; set; } = string.Empty;

        /// <summary>
        /// 设备授权码
        /// </summary>
        public string AuthCode { get; set; } = string.Empty;

        /// <summary>
        /// 设备状态
        /// </summary>
        public int Status { get; set; }

        /// <summary>
        /// 设备硬件识别码
        /// </summary>
        public string HardwareId { get; set; } = string.Empty;

        /// <summary>
        /// 设备类型
        /// </summary>
        public string DeviceType { get; set; } = string.Empty;

        /// <summary>
        /// 设备系统
        /// </summary>
        public string DeviceSystem { get; set; } = string.Empty;

        /// <summary>
        /// 分店代码
        /// </summary>
        public string? StoreCode { get; set; }

        /// <summary>
        /// 分店名称
        /// </summary>
        public string? StoreName { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 更新时间
        /// </summary>
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// 设备注册响应数据传输对象
    /// </summary>
    public class DeviceRegistrationResponseDto
    {
        /// <summary>
        /// 设备ID
        /// </summary>
        public int DeviceId { get; set; }

        /// <summary>
        /// 系统设备编号
        /// </summary>
        public string SystemDeviceNumber { get; set; } = string.Empty;

        /// <summary>
        /// 设备授权码
        /// </summary>
        public string AuthCode { get; set; } = string.Empty;

        /// <summary>
        /// 设备状态
        /// </summary>
        public int Status { get; set; }

        /// <summary>
        /// 设备状态描述
        /// </summary>
        public string StatusDescription { get; set; } = string.Empty;
    }

    /// <summary>
    /// 设备列表项数据传输对象
    /// </summary>
    public class DeviceListItemDto
    {
        /// <summary>
        /// 设备ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 设备硬件识别码
        /// </summary>
        public string HardwareId { get; set; } = string.Empty;

        /// <summary>
        /// 系统设备编号
        /// </summary>
        public string SystemDeviceNumber { get; set; } = string.Empty;

        /// <summary>
        /// 设备类型
        /// </summary>
        public string DeviceType { get; set; } = string.Empty;

        /// <summary>
        /// 设备系统
        /// </summary>
        public string DeviceSystem { get; set; } = string.Empty;

        /// <summary>
        /// 分店代码
        /// </summary>
        public string? StoreCode { get; set; }

        /// <summary>
        /// 设备状态
        /// </summary>
        public int Status { get; set; }

        /// <summary>
        /// 设备状态描述
        /// </summary>
        public string StatusDescription { get; set; } = string.Empty;

        /// <summary>
        /// 是否允许开始新交易
        /// </summary>
        public bool AllowTransactions { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 最后修改时间
        /// </summary>
        public DateTime? LastModified { get; set; }

        /// <summary>
        /// 创建人
        /// </summary>
        public string? CreatedBy { get; set; }

        /// <summary>
        /// 最后修改人
        /// </summary>
        public string? LastModifiedBy { get; set; }

        /// <summary>
        /// 是否在线
        /// </summary>
        public bool IsOnline { get; set; }

        /// <summary>
        /// 最后心跳时间
        /// </summary>
        public DateTime? LastHeartbeatAt { get; set; }

        /// <summary>
        /// 当前收银员ID
        /// </summary>
        public string? CurrentCashierId { get; set; }

        /// <summary>
        /// 当前收银员姓名
        /// </summary>
        public string? CurrentCashierName { get; set; }

        /// <summary>
        /// 收银员登录时间
        /// </summary>
        public DateTime? CashierLoginAt { get; set; }

        /// <summary>
        /// 客户端程序版本（WPF 随心跳上报），未上报时为空
        /// </summary>
        public string? AppVersion { get; set; }

        /// <summary>
        /// 收银机自开机起的秒数（WPF 随心跳上报），未上报时为空
        /// </summary>
        public long? SystemUptimeSeconds { get; set; }

        /// <summary>
        /// WPF 进程工作集（MB），未上报时为空
        /// </summary>
        public int? ProcessWorkingSetMb { get; set; }

        /// <summary>
        /// WPF 进程句柄数，未上报时为空
        /// </summary>
        public int? ProcessHandleCount { get; set; }

        /// <summary>
        /// RustDesk 客户端版本（远程维护登记时上报），没有远程维护记录时为空；仅分页列表填充
        /// </summary>
        public string? RustDeskClientVersion { get; set; }

        /// <summary>
        /// WPF 更新策略当前的目标版本；仅 Windows POS 设备且有策略时填充，其余为空
        /// </summary>
        public string? TargetAppVersion { get; set; }

        /// <summary>
        /// 版本状态：latest 已是策略版本 / behind 待升级 / ahead 高于策略版本 /
        /// notTargeted 不在策略放量范围 / unreported 未上报版本；非 Windows POS 设备为空
        /// </summary>
        public string? VersionState { get; set; }

        /// <summary>
        /// 设备最近一次请求实际走的 POS API 入口（edge / aws / direct），后台「入口」列展示用；未上报（老客户端 / 尚未经新入口）时为空
        /// </summary>
        public string? LastApiEntry { get; set; }

        /// <summary>
        /// <see cref="LastApiEntry"/> 最近一次写入时间（UTC）；POS API 节流写入，约每 10 分钟刷新，未上报时为空
        /// </summary>
        public DateTime? LastApiEntryAt { get; set; }
    }

    /// <summary>
    /// 设备运行状态上报DTO
    /// </summary>
    public class DeviceRuntimeStatusUpdateDto
    {
        /// <summary>
        /// 是否在线
        /// </summary>
        public bool IsOnline { get; set; }

        /// <summary>
        /// 当前收银员ID
        /// </summary>
        public string? CurrentCashierId { get; set; }

        /// <summary>
        /// 当前收银员姓名
        /// </summary>
        public string? CurrentCashierName { get; set; }
    }

    /// <summary>
    /// 设备注册请求DTO
    /// </summary>
    public class DeviceRegistrationRequestDto
    {
        /// <summary>
        /// 设备硬件识别码
        /// </summary>
        [Required(ErrorMessage = "设备硬件识别码不能为空")]
        [StringLength(100, ErrorMessage = "设备硬件识别码长度不能超过100个字符")]
        public string HardwareId { get; set; } = string.Empty;

        /// <summary>
        /// 设备类型
        /// </summary>
        [Required(ErrorMessage = "设备类型不能为空")]
        [StringLength(20, ErrorMessage = "设备类型长度不能超过20个字符")]
        public string DeviceType { get; set; } = string.Empty;

        /// <summary>
        /// 设备系统
        /// </summary>
        [Required(ErrorMessage = "设备系统不能为空")]
        [StringLength(20, ErrorMessage = "设备系统长度不能超过20个字符")]
        public string DeviceSystem { get; set; } = string.Empty;

        /// <summary>
        /// 分店代码（可选）
        /// </summary>
        [StringLength(50, ErrorMessage = "分店代码长度不能超过50个字符")]
        public string? StoreCode { get; set; }
    }

    /// <summary>
    /// 设备授权验证请求DTO
    /// </summary>
    public class DeviceAuthValidationRequestDto
    {
        /// <summary>
        /// 设备硬件识别码
        /// </summary>
        [Required(ErrorMessage = "设备硬件识别码不能为空")]
        public string HardwareId { get; set; } = string.Empty;

        /// <summary>
        /// 授权码
        /// </summary>
        [Required(ErrorMessage = "授权码不能为空")]
        public string AuthCode { get; set; } = string.Empty;

        /// <summary>
        /// 后台注册设备号，App 端可直接带回便于登录审计。
        /// </summary>
        public string? SystemDeviceNumber { get; set; }

        /// <summary>
        /// 设备系统，例如 iOS 或 Android。
        /// </summary>
        public string? DeviceSystem { get; set; }

        /// <summary>
        /// 设备登录时采集的纬度。
        /// </summary>
        public double? LocationLatitude { get; set; }

        /// <summary>
        /// 设备登录时采集的经度。
        /// </summary>
        public double? LocationLongitude { get; set; }

        /// <summary>
        /// 设备登录时采集的位置精度，单位米。
        /// </summary>
        public double? LocationAccuracy { get; set; }

        /// <summary>
        /// 设备登录时定位权限状态，服务端要求为 granted。
        /// </summary>
        public string? LocationPermissionStatus { get; set; }

        /// <summary>
        /// 设备登录时定位采集时间。
        /// </summary>
        public DateTime? LocationCapturedAtUtc { get; set; }
    }

    /// <summary>
    /// 设备解绑请求DTO
    /// </summary>
    public class DeviceUnbindRequestDto
    {
        /// <summary>
        /// 设备硬件识别码
        /// </summary>
        [Required(ErrorMessage = "设备硬件识别码不能为空")]
        public string HardwareId { get; set; } = string.Empty;

        /// <summary>
        /// 授权码
        /// </summary>
        [Required(ErrorMessage = "授权码不能为空")]
        public string AuthCode { get; set; } = string.Empty;
    }

    /// <summary>
    /// 设备授权验证响应DTO
    /// </summary>
    public class DeviceAuthValidationResponseDto
    {
        /// <summary>
        /// 验证是否成功
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// 授权码过期时间
        /// </summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>
        /// 附加信息
        /// </summary>
        public string? AdditionalInfo { get; set; }
    }

    /// <summary>
    /// 设备统计信息DTO
    /// </summary>
    public class DeviceStatisticsDto
    {
        /// <summary>
        /// 总设备数
        /// </summary>
        public int TotalDevices { get; set; }

        /// <summary>
        /// 启用设备数
        /// </summary>
        public int ActiveDevices { get; set; }

        /// <summary>
        /// 禁用设备数
        /// </summary>
        public int DisabledDevices { get; set; }

        /// <summary>
        /// 待确认设备数
        /// </summary>
        public int PendingDevices { get; set; }

        /// <summary>
        /// 锁定设备数
        /// </summary>
        public int LockedDevices { get; set; }

        /// <summary>
        /// 未注册设备数
        /// </summary>
        public int UnregisteredDevices { get; set; }

        /// <summary>
        /// 设备类型统计
        /// </summary>
        public List<DeviceTypeStatDto> DeviceTypeStats { get; set; } = new();

        /// <summary>
        /// 设备系统统计
        /// </summary>
        public List<DeviceSystemStatDto> DeviceSystemStats { get; set; } = new();
    }

    /// <summary>
    /// 设备类型统计DTO
    /// </summary>
    public class DeviceTypeStatDto
    {
        /// <summary>
        /// 设备类型
        /// </summary>
        public string DeviceType { get; set; } = string.Empty;

        /// <summary>
        /// 数量
        /// </summary>
        public int Count { get; set; }
    }

    /// <summary>
    /// 设备系统统计DTO
    /// </summary>
    public class DeviceSystemStatDto
    {
        /// <summary>
        /// 设备系统
        /// </summary>
        public string DeviceSystem { get; set; } = string.Empty;

        /// <summary>
        /// 数量
        /// </summary>
        public int Count { get; set; }
    }

    /// <summary>
    /// 设备查询参数DTO
    /// </summary>
    public class DeviceQueryDto : PagedQuery
    {
        /// <summary>
        /// 分店代码
        /// </summary>
        public string? StoreCode { get; set; }

        /// <summary>
        /// 设备类型
        /// </summary>
        public string? DeviceType { get; set; }

        /// <summary>
        /// 设备状态
        /// </summary>
        public int? Status { get; set; }

        /// <summary>
        /// 关键词搜索（设备硬件识别码、系统设备编号、分店代码、备注）
        /// </summary>
        public string? Keyword { get; set; }
    }

    /// <summary>
    /// 设备注册列表的版本汇总：当前策略目标版本、各版本设备数与各状态设备数。
    /// 与分页列表使用同一套筛选条件（分店、类型、系统、状态、关键词），但不含版本条件本身，
    /// 这样点选某个版本后其余版本仍可切换。
    /// </summary>
    public class DeviceVersionSummaryDto
    {
        /// <summary>WPF 更新策略当前目标版本；没有策略时为空。</summary>
        public string? TargetVersion { get; set; }

        /// <summary>策略放量范围：all / stores / devices；没有策略时为空。</summary>
        public string? TargetScope { get; set; }

        /// <summary>筛选条件下的设备总数。</summary>
        public int Total { get; set; }

        /// <summary>按版本分组，版本号从新到旧，未上报垫底。</summary>
        public List<DeviceVersionSummaryItemDto> Items { get; set; } = new();

        public DeviceVersionStateCountsDto States { get; set; } = new();
    }

    public class DeviceVersionSummaryItemDto
    {
        /// <summary>规范化后的版本号；该组设备都没有上报版本时为空。</summary>
        public string? AppVersion { get; set; }

        public int Total { get; set; }
    }

    public class DeviceVersionStateCountsDto
    {
        public int Latest { get; set; }

        public int Behind { get; set; }

        public int Ahead { get; set; }

        public int NotTargeted { get; set; }

        public int Unreported { get; set; }

        /// <summary>不参与版本状态判断的设备（非 Windows POS，或没有策略）。</summary>
        public int NotApplicable { get; set; }
    }
}
