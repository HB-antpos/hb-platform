using SqlSugar;

namespace BlazorApp.Shared.Models
{
    [SugarTable("AttendanceLocationSample")]
    public class AttendanceLocationSample : BaseEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public int Id { get; set; }

        [SugarColumn(IsNullable = false, Length = 50)]
        public string SampleGuid { get; set; } = Guid.NewGuid().ToString();

        [SugarColumn(IsNullable = false, Length = 50)]
        public string UserGuid { get; set; } = string.Empty;

        [SugarColumn(IsNullable = true, Length = 50)]
        public string? StoreCode { get; set; }

        [SugarColumn(IsNullable = true, Length = 100)]
        public string? HardwareId { get; set; }

        [SugarColumn(IsNullable = true, Length = 100)]
        public string? SystemDeviceNumber { get; set; }

        [SugarColumn(IsNullable = true, Length = 30)]
        public string? DeviceSystem { get; set; }

        [SugarColumn(IsNullable = false, Length = 30)]
        public string EventType { get; set; } = "ShiftInterval";

        [SugarColumn(IsNullable = false)]
        public double LocationLatitude { get; set; }

        [SugarColumn(IsNullable = false)]
        public double LocationLongitude { get; set; }

        [SugarColumn(IsNullable = true)]
        public double? LocationAccuracyMeters { get; set; }

        [SugarColumn(IsNullable = true, Length = 30)]
        public string? LocationPermissionStatus { get; set; }

        [SugarColumn(IsNullable = false)]
        public DateTime LocationCapturedAtUtc { get; set; }

        /// <summary>
        /// 客户端生成的采样点 ID（GUID），批量上传的幂等键。旧版单条上传不带，为 NULL。
        /// 数据库有过滤唯一索引 (UserGuid, ClientSampleId) WHERE ClientSampleId IS NOT NULL，
        /// 索引与本列由独立迁移 20261015.003 创建（nvarchar(50) NULL）。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 50)]
        public string? ClientSampleId { get; set; }

        /// <summary>设备上报该定位是否来自模拟定位（Mock Location）；旧版上传不带，为 NULL（未知）。</summary>
        [SugarColumn(IsNullable = true)]
        public bool? IsMocked { get; set; }
    }
}
