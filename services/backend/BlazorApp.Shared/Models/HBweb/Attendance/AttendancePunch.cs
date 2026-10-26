using SqlSugar;

namespace BlazorApp.Shared.Models
{
    [SugarTable("AttendancePunch")]
    public class AttendancePunch : BaseEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public int Id { get; set; }

        [SugarColumn(IsNullable = false, Length = 50)]
        public string PunchGuid { get; set; } = Guid.NewGuid().ToString();

        [SugarColumn(IsNullable = true, Length = 50)]
        public string? ScheduleGuid { get; set; }

        [SugarColumn(IsNullable = false, Length = 50)]
        public string StoreCode { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 50)]
        public string UserGuid { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false)]
        public DateTime WorkDate { get; set; }

        [SugarColumn(IsNullable = false, Length = 80)]
        public string StoreTimeZone { get; set; } = "Australia/Sydney";

        [SugarColumn(IsNullable = false, Length = 30)]
        public string PunchType { get; set; } = "ClockIn";

        [SugarColumn(IsNullable = false)]
        public DateTime PunchTimeUtc { get; set; }

        [SugarColumn(IsNullable = false)]
        public DateTime PunchTimeLocal { get; set; }

        [SugarColumn(IsNullable = false, Length = 30)]
        public string Status { get; set; } = "Normal";

        [SugarColumn(IsNullable = true, Length = 100)]
        public string? DeviceId { get; set; }

        [SugarColumn(IsNullable = true)]
        public double? LocationLatitude { get; set; }

        [SugarColumn(IsNullable = true)]
        public double? LocationLongitude { get; set; }

        [SugarColumn(IsNullable = true)]
        public double? LocationAccuracyMeters { get; set; }

        [SugarColumn(IsNullable = true, Length = 30)]
        public string? LocationPermissionStatus { get; set; }

        [SugarColumn(IsNullable = true)]
        public DateTime? LocationCapturedAtUtc { get; set; }

        [SugarColumn(IsNullable = false, Length = 30)]
        public string Source { get; set; } = "App";

        [SugarColumn(IsNullable = true, Length = 50)]
        public string? QrTokenId { get; set; }

        [SugarColumn(IsNullable = true, Length = 50)]
        public string? PosDeviceCode { get; set; }

        [SugarColumn(IsNullable = true, Length = 64)]
        public string? SigningKeyId { get; set; }

        [SugarColumn(IsNullable = true, Length = 50)]
        public string? SupersedesPunchGuid { get; set; }

        [SugarColumn(IsNullable = true, Length = 50)]
        public string? AdjustmentGuid { get; set; }

        [SugarColumn(IsNullable = true, Length = 500)]
        public string? Remark { get; set; }

        /// <summary>
        /// 午夜自动下班卡：门店时区 00:00 仍未闭合的班段由系统补这张下班卡（时间 = 当天 24:00）。
        /// 它闭合的班段不计薪、并视为漏下班；店长填实际下班时间（补卡覆盖本卡）后结案。列由迁移 20261026.001 添加。
        /// </summary>
        [SugarColumn(IsNullable = false)]
        public bool IsSystemAutoClockOut { get; set; }
    }
}
