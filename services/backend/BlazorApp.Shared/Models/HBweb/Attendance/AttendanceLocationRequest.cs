using SqlSugar;

namespace BlazorApp.Shared.Models
{
    /// <summary>
    /// 「服务端要求员工取一次高精度位置」的请求记录，同时是审计留痕：谁请求、请求谁、何时、结果。
    /// 只在目标员工已上班期间可创建；员工在 App 前台响应后，取到的点写入班中样本表 AttendanceLocationSample，
    /// 并用 <see cref="SampleGuid"/> 与本记录关联（样本的 ClientSampleId = RequestGuid，借唯一索引做幂等）。
    /// 表由独立版本号迁移 20261119.002-attendance-location-request 建出，实体不进 SqlSugarContext 的 tableTypes。
    /// </summary>
    [SugarTable("AttendanceLocationRequest")]
    public class AttendanceLocationRequest : BaseEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public int Id { get; set; }

        /// <summary>对外标识（推送、接口、样本幂等键共用）。</summary>
        [SugarColumn(IsNullable = false, Length = 50)]
        public string RequestGuid { get; set; } = Guid.NewGuid().ToString();

        /// <summary>请求发生的门店（目标员工当时上班所在的门店）。</summary>
        [SugarColumn(IsNullable = false, Length = 50)]
        public string StoreCode { get; set; } = string.Empty;

        /// <summary>被请求的员工。</summary>
        [SugarColumn(IsNullable = false, Length = 50)]
        public string TargetUserGuid { get; set; } = string.Empty;

        /// <summary>发起请求的人（店长 / 管理员）。</summary>
        [SugarColumn(IsNullable = false, Length = 50)]
        public string RequestedByUserGuid { get; set; } = string.Empty;

        /// <summary>
        /// 存储状态：Pending / Responded / Cancelled。「已过期」不落库，读取时按 ExpiresAtUtc 推导，
        /// 所以不需要后台任务去翻状态（见 AttendanceLocationRequestRules.ResolveEffectiveStatus）。
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 20)]
        public string Status { get; set; } = AttendanceLocationRequestStatuses.Pending;

        /// <summary>状态原因（短码）：Cancelled 时为 ClockedOut（响应时发现员工已下班）等；其余为空。</summary>
        [SugarColumn(IsNullable = true, Length = 30)]
        public string? StatusReason { get; set; }

        /// <summary>请求失效时间（UTC）；员工在此之前没有响应就视为「未响应」。</summary>
        [SugarColumn(IsNullable = false)]
        public DateTime ExpiresAtUtc { get; set; }

        /// <summary>员工响应（服务端收到并写入样本）的时间。</summary>
        [SugarColumn(IsNullable = true)]
        public DateTime? RespondedAtUtc { get; set; }

        /// <summary>响应后关联的班中样本 SampleGuid；未响应为空。</summary>
        [SugarColumn(IsNullable = true, Length = 50)]
        public string? SampleGuid { get; set; }
    }

    public static class AttendanceLocationRequestStatuses
    {
        public const string Pending = "Pending";
        public const string Responded = "Responded";

        /// <summary>仅用于对外展示的推导状态：Pending 且已过 ExpiresAtUtc。不会写进数据库。</summary>
        public const string Expired = "Expired";

        public const string Cancelled = "Cancelled";
    }

    public static class AttendanceLocationRequestStatusReasons
    {
        /// <summary>响应到达时员工已下班：不再取、不再收，请求作废。</summary>
        public const string ClockedOut = "ClockedOut";
    }
}
