using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 班中定位「完整轨迹」批量上传：客户端本地攒点、批量上传、可补传，按 ClientSampleId 幂等。
    /// 与旧版单条接口（CreateLocationSampleAsync，30 分钟新鲜度限制）并存，旧版 App 行为不变。
    /// 班次有效性判定与单条接口共用 <see cref="AttendanceLocationSampleShiftRules"/>。
    /// </summary>
    public partial class AttendanceReactService
    {
        /// <summary>单次批量上传的采样点数量上限（含）。</summary>
        internal const int LocationSampleBatchMaxSamples = 200;

        // 客户端补传窗口：采集时间不能早于当前 48 小时，也不能晚于当前 5 分钟以上（容忍设备时钟轻微超前）。
        private static readonly TimeSpan LocationSampleBatchMaxAge = TimeSpan.FromHours(48);
        private static readonly TimeSpan LocationSampleBatchMaxFutureSkew = TimeSpan.FromMinutes(5);

        // ClientSampleId 列是 nvarchar(50)，超长直接判无效，绝不截断（截断会让不同 ID 撞键）。
        private const int ClientSampleIdMaxLength = 50;
        private const string DefaultBatchEventType = "ShiftSample";

        private enum LocationSampleBatchOutcome
        {
            Pending,
            Accepted,
            Duplicate,
            Rejected,
        }

        /// <summary>一个请求里的单个采样点在各阶段的处理状态（按请求顺序保留，最后统一汇总成结果）。</summary>
        private sealed class LocationSampleBatchEntry
        {
            /// <summary>响应里回显的 ID（客户端原样发送的字符串，客户端据此从本地队列删除）。</summary>
            public string EchoId { get; init; } = string.Empty;

            /// <summary>去空白后的幂等键；ID 无效的点为 null。</summary>
            public string? Key { get; init; }

            public AttendanceLocationSampleBatchItemDto? Item { get; init; }
            public LocationSampleBatchOutcome Outcome { get; set; } = LocationSampleBatchOutcome.Pending;
            public string ErrorCode { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public DateTime CapturedAtUtc { get; set; }
            public DateTime WindowStartUtc { get; set; }

            public void Reject(string errorCode, string message)
            {
                Outcome = LocationSampleBatchOutcome.Rejected;
                ErrorCode = errorCode;
                Message = message;
            }
        }

        public async Task<ApiResponse<AttendanceLocationSampleBatchResultDto>> CreateLocationSamplesBatchAsync(
            AttendanceLocationSampleBatchRequestDto request
        )
        {
            var userGuid = ResolveCurrentUserGuid();
            var storeCode = request?.StoreCode?.Trim();
            if (string.IsNullOrWhiteSpace(storeCode))
            {
                return ApiResponse<AttendanceLocationSampleBatchResultDto>.Error(
                    "请选择分店后再上传定位", "STORE_REQUIRED");
            }

            var items = request!.Samples;
            if (items == null || items.Count == 0 || items.Count > LocationSampleBatchMaxSamples)
            {
                return ApiResponse<AttendanceLocationSampleBatchResultDto>.Error(
                    $"单次需上传 1～{LocationSampleBatchMaxSamples} 个定位点",
                    "TOO_MANY_SAMPLES");
            }

            var access = await ResolveRelatedStoreAccessAsync(userGuid, storeCode);
            if (!access.Success)
            {
                return ApiResponse<AttendanceLocationSampleBatchResultDto>.Error(
                    access.Message, access.ErrorCode);
            }

            var entries = BuildLocationSampleBatchEntries(items);

            await MarkExistingLocationSampleDuplicatesAsync(userGuid, entries);
            ValidateLocationSampleBatchEntries(entries, _timeProvider.GetUtcNow().UtcDateTime);
            await RejectSamplesWithoutActiveShiftAsync(userGuid, storeCode, entries);
            await InsertLocationSampleBatchEntriesAsync(userGuid, storeCode, request, entries);

            var result = new AttendanceLocationSampleBatchResultDto();
            foreach (var entry in entries)
            {
                switch (entry.Outcome)
                {
                    case LocationSampleBatchOutcome.Accepted:
                        result.Accepted.Add(entry.EchoId);
                        break;
                    case LocationSampleBatchOutcome.Duplicate:
                        result.Duplicates.Add(entry.EchoId);
                        break;
                    default:
                        result.Rejected.Add(new AttendanceLocationSampleBatchRejectedDto
                        {
                            ClientSampleId = entry.EchoId,
                            ErrorCode = entry.ErrorCode,
                            Message = entry.Message,
                        });
                        break;
                }
            }

            _logger.LogInformation(
                "班中定位批量上传 StoreCode={StoreCode} Total={Total} Accepted={Accepted} Duplicates={Duplicates} Rejected={Rejected}",
                storeCode,
                entries.Count,
                result.Accepted.Count,
                result.Duplicates.Count,
                result.Rejected.Count);
            return ApiResponse<AttendanceLocationSampleBatchResultDto>.OK(result, "定位批量上传完成");
        }

        /// <summary>
        /// 第 1 步：逐点检查 ClientSampleId。无效 ID 直接拒绝；同一请求内重复的 ID，第一个按正常流程，其后的归入 Duplicates。
        /// ID 比较一律忽略大小写（与 SQL Server 默认不区分大小写的唯一索引口径一致）。
        /// </summary>
        private static List<LocationSampleBatchEntry> BuildLocationSampleBatchEntries(
            IReadOnlyList<AttendanceLocationSampleBatchItemDto?> items)
        {
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<LocationSampleBatchEntry>(items.Count);
            foreach (var item in items)
            {
                var rawId = item?.ClientSampleId ?? string.Empty;
                var key = rawId.Trim();
                if (item == null || key.Length == 0 || key.Length > ClientSampleIdMaxLength)
                {
                    var invalid = new LocationSampleBatchEntry { EchoId = rawId, Item = item };
                    invalid.Reject("CLIENT_SAMPLE_ID_INVALID", $"采样点 ID 必须为 1～{ClientSampleIdMaxLength} 个字符");
                    entries.Add(invalid);
                    continue;
                }

                var entry = new LocationSampleBatchEntry { EchoId = rawId, Key = key, Item = item };
                if (!seenKeys.Add(key))
                {
                    entry.Outcome = LocationSampleBatchOutcome.Duplicate;
                }

                entries.Add(entry);
            }

            return entries;
        }

        /// <summary>
        /// 第 2 步：一次查询该用户已入库的 ClientSampleId，命中的点视为幂等成功（Duplicates）。
        /// 不过滤 IsDeleted：唯一索引不区分软删，已软删的行同样会占住这个 ID。
        /// 放在时间/班次校验之前——重试一个早已入库的点，不应因为它现在「太旧」而被报成拒绝。
        /// </summary>
        private async Task MarkExistingLocationSampleDuplicatesAsync(
            string userGuid,
            List<LocationSampleBatchEntry> entries)
        {
            var pending = entries.Where(entry => entry.Outcome == LocationSampleBatchOutcome.Pending).ToList();
            if (pending.Count == 0)
            {
                return;
            }

            var keys = pending.Select(entry => entry.Key!).ToList();
            var existingIds = await _db.Queryable<AttendanceLocationSample>()
                .Where(item => item.UserGuid == userGuid && keys.Contains(item.ClientSampleId!))
                .Select(item => item.ClientSampleId)
                .ToListAsync();
            var existing = existingIds
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in pending.Where(entry => existing.Contains(entry.Key!)))
            {
                entry.Outcome = LocationSampleBatchOutcome.Duplicate;
            }
        }

        /// <summary>
        /// 第 3 步：逐点校验定位（坐标/权限/采集时间齐全）与采集时间窗口。
        /// 窗口以服务端时钟为准：早于当前 48 小时 → SAMPLE_TOO_OLD，晚于当前 5 分钟以上 → SAMPLE_IN_FUTURE。
        /// </summary>
        private static void ValidateLocationSampleBatchEntries(
            List<LocationSampleBatchEntry> entries,
            DateTime nowUtc)
        {
            foreach (var entry in entries.Where(entry => entry.Outcome == LocationSampleBatchOutcome.Pending))
            {
                var item = entry.Item!;
                var location = RequiredLocationValidator.ValidateCoordinates(
                    item.LocationLatitude,
                    item.LocationLongitude,
                    item.LocationPermissionStatus,
                    item.LocationCapturedAtUtc,
                    "班中定位需要位置信息");
                if (!location.Success)
                {
                    entry.Reject(location.ErrorCode, location.Message);
                    continue;
                }

                var capturedAt = NormalizeBatchCapturedAtUtc(item.LocationCapturedAtUtc!.Value);
                if (capturedAt > nowUtc.Add(LocationSampleBatchMaxFutureSkew))
                {
                    entry.Reject("SAMPLE_IN_FUTURE", "定位采集时间晚于服务器时间");
                    continue;
                }

                if (capturedAt < nowUtc.Subtract(LocationSampleBatchMaxAge))
                {
                    entry.Reject("SAMPLE_TOO_OLD", "定位采集时间已超过 48 小时，无法补传");
                    continue;
                }

                entry.CapturedAtUtc = capturedAt;
            }
        }

        /// <summary>
        /// 第 4 步：逐点判定采集时刻是否处在有效班次内。
        /// 不对每个点各查一次库：一次性加载覆盖 [最早窗口下限, 最晚采集时刻] 的候选打卡及其被顶替关系，在内存里按每个点各自的窗口判定。
        /// 判定是「相对采集时刻」的（不是相对当前时刻）：下班卡之后才上传的尾批点，只要采集时刻在下班卡之前就有效。
        /// </summary>
        private async Task RejectSamplesWithoutActiveShiftAsync(
            string userGuid,
            string storeCode,
            List<LocationSampleBatchEntry> entries)
        {
            var toCheck = entries.Where(entry => entry.Outcome == LocationSampleBatchOutcome.Pending).ToList();
            if (toCheck.Count == 0)
            {
                return;
            }

            var storeTimeZone = await ResolveStoreTimeZoneAsync(storeCode, null);
            foreach (var entry in toCheck)
            {
                entry.WindowStartUtc = ResolveLocationSampleWindowStartUtc(entry.CapturedAtUtc, storeTimeZone);
            }

            var punches = await LoadLocationSampleCandidatePunchesAsync(
                userGuid,
                storeCode,
                toCheck.Min(entry => entry.WindowStartUtc),
                toCheck.Max(entry => entry.CapturedAtUtc));
            var supersededPunchGuids = punches.Count == 0
                ? new HashSet<string>(StringComparer.Ordinal)
                : await LoadSupersededPunchGuidsAsync(punches);

            foreach (var entry in toCheck)
            {
                if (!AttendanceLocationSampleShiftRules.IsShiftActiveAt(
                        punches,
                        supersededPunchGuids,
                        entry.CapturedAtUtc,
                        entry.WindowStartUtc))
                {
                    entry.Reject("ACTIVE_SHIFT_REQUIRED", "该采集时间没有有效的班中打卡，无法上传定位");
                }
            }
        }

        /// <summary>
        /// 第 5 步：逐条插入通过校验的点。单点失败不影响其他点：
        /// 唯一键冲突（并发重复提交，或另一请求刚好先入库）归入 Duplicates，由数据库的过滤唯一索引兜底。
        /// 其他异常（连接中断、超时等基础设施故障）不吞掉——直接向上抛成 5xx，客户端保留队列重试；
        /// 已入库的点重试时会被识别为 Duplicates，所以整体仍是幂等的。
        /// 采样点最多 200 个、通常远少于此，逐条插入的往返开销可接受，换来确切的逐点结果，无需处理批量插入的部分失败语义。
        /// </summary>
        private async Task InsertLocationSampleBatchEntriesAsync(
            string userGuid,
            string storeCode,
            AttendanceLocationSampleBatchRequestDto request,
            List<LocationSampleBatchEntry> entries)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var createdBy = _currentUserService.GetCurrentUsername();
            var hardwareId = NormalizeOptional(request.HardwareId, 100);
            var systemDeviceNumber = NormalizeOptional(request.SystemDeviceNumber, 100);
            var deviceSystem = NormalizeOptional(request.DeviceSystem, 30);

            foreach (var entry in entries.Where(entry => entry.Outcome == LocationSampleBatchOutcome.Pending))
            {
                var item = entry.Item!;
                var sample = new AttendanceLocationSample
                {
                    SampleGuid = Guid.NewGuid().ToString(),
                    UserGuid = userGuid,
                    StoreCode = storeCode,
                    HardwareId = hardwareId,
                    SystemDeviceNumber = systemDeviceNumber,
                    DeviceSystem = deviceSystem,
                    EventType = NormalizeOptional(item.EventType, 30) ?? DefaultBatchEventType,
                    LocationLatitude = item.LocationLatitude!.Value,
                    LocationLongitude = item.LocationLongitude!.Value,
                    LocationAccuracyMeters = item.LocationAccuracy,
                    LocationPermissionStatus = NormalizeOptional(item.LocationPermissionStatus, 30),
                    LocationCapturedAtUtc = entry.CapturedAtUtc,
                    ClientSampleId = entry.Key,
                    IsMocked = item.IsMocked,
                    CreatedAt = now,
                    CreatedBy = createdBy,
                };

                try
                {
                    // 班中定位样本按追加写入，不反向修改打卡记录，避免轨迹和打卡状态耦合。
                    await _db.Insertable(sample).ExecuteCommandAsync();
                    entry.Outcome = LocationSampleBatchOutcome.Accepted;
                }
                catch (Exception exception) when (
                    AttendancePunchPersistenceException.IsUniqueConstraintViolation(exception))
                {
                    entry.Outcome = LocationSampleBatchOutcome.Duplicate;
                }
            }
        }

        /// <summary>
        /// 班次有效窗口下限（不含）：门店本地当日 0 点与采集时刻前 10 小时取较晚者。
        /// 单条与批量上传共用，口径与历史单条判定完全一致。
        /// </summary>
        private static DateTime ResolveLocationSampleWindowStartUtc(DateTime capturedAtUtc, string storeTimeZone)
        {
            var localDayStartUtc = ConvertStoreLocalToUtc(
                ConvertUtcToStoreLocal(capturedAtUtc, storeTimeZone).Date,
                storeTimeZone
            );
            return AttendanceLocationSampleShiftRules.ResolveWindowStartUtc(capturedAtUtc, localDayStartUtc);
        }

        /// <summary>
        /// 加载某用户在某门店、PunchTimeUtc 落在 (windowStartUtc, capturedAtUtcUpperBound] 内的未删除打卡。
        /// 单条传入该点的窗口，批量传入「所有点的最早窗口下限 / 最晚采集时刻」，再由纯函数按各点自己的窗口筛选。
        /// </summary>
        private async Task<List<AttendancePunch>> LoadLocationSampleCandidatePunchesAsync(
            string userGuid,
            string storeCode,
            DateTime windowStartUtc,
            DateTime capturedAtUtcUpperBound)
        {
            return await _db.Queryable<AttendancePunch>()
                .Where(item =>
                    !item.IsDeleted
                    && item.UserGuid == userGuid
                    && item.StoreCode == storeCode
                    && item.PunchTimeUtc > windowStartUtc
                    && item.PunchTimeUtc <= capturedAtUtcUpperBound
                )
                .ToListAsync();
        }

        /// <summary>
        /// 候选打卡里哪些被别的有效打卡顶替了（SupersedesPunchGuid 指向它们）。
        /// 顶替卡可以是任何用户/门店/时间的未删除打卡，与历史单条判定口径一致，所以这里不再加时间或用户过滤。
        /// 候选打卡仅限同一用户一个时间窗内的打卡，数量很小，直接用 IN 列表即可。
        /// </summary>
        private async Task<HashSet<string>> LoadSupersededPunchGuidsAsync(IReadOnlyCollection<AttendancePunch> punches)
        {
            var candidatePunchGuids = punches.Select(item => item.PunchGuid).ToList();
            var supersededPunchGuids = await _db.Queryable<AttendancePunch>()
                .Where(item =>
                    !item.IsDeleted
                    && item.SupersedesPunchGuid != null
                    && candidatePunchGuids.Contains(item.SupersedesPunchGuid)
                )
                .Select(item => item.SupersedesPunchGuid!)
                .ToListAsync();
            return supersededPunchGuids.ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>
        /// 采集时间归一为 UTC：Utc 原样；Local（带偏移量的 ISO 时间被解析成本地）按偏移转换；
        /// Unspecified（无时区后缀）按 UTC 解释，与现有单条接口的 NormalizeUtc 口径一致。
        /// </summary>
        private static DateTime NormalizeBatchCapturedAtUtc(DateTime value) =>
            value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
