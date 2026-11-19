using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 「服务端要求员工取一次高精度位置」：店长 / 管理员对已上班的员工发起请求 → 员工在 App 前台响应 → 位置写入班中样本。
    /// 设计要点：
    /// - 只限目标员工已上班期间：创建时复核一次，响应到达时再复核一次（下班后不再取、不再收）；
    /// - 请求带有效期；「已过期」不落库，读取时按失效时间推导，所以不需要后台任务；
    /// - 后台取不到位置（App 已不再声明后台定位），所以不唤醒、不发静默推送：创建后只发一条可见通知，
    ///   员工点通知或下次进前台时才消费待办——推送不保证送达，进前台消费是兜底；
    /// - 防骚扰与留痕：同一员工限频（含未过期的待响应请求）；每条请求就是一行审计（谁请求、请求谁、何时、结果）。
    /// 口径常量与错误码集中在 <see cref="AttendanceLocationRequestRules"/>。
    /// </summary>
    public partial class AttendanceReactService
    {
        public async Task<ApiResponse<AttendanceLocationRequestDto>> CreateLocationRequestAsync(
            CreateAttendanceLocationRequestDto request
        )
        {
            var requesterGuid = ResolveCurrentUserGuid();
            var storeCode = request?.StoreCode?.Trim();
            if (string.IsNullOrWhiteSpace(storeCode))
            {
                return ApiResponse<AttendanceLocationRequestDto>.Error(
                    "请选择分店后再请求位置", AttendanceLocationRequestRules.ErrorCodes.StoreRequired);
            }

            var targetUserGuid = request!.TargetUserGuid?.Trim();
            if (string.IsNullOrWhiteSpace(targetUserGuid))
            {
                return ApiResponse<AttendanceLocationRequestDto>.Error(
                    "请选择要请求位置的员工", AttendanceLocationRequestRules.ErrorCodes.TargetUserRequired);
            }

            if (string.Equals(requesterGuid, targetUserGuid, StringComparison.OrdinalIgnoreCase))
            {
                return ApiResponse<AttendanceLocationRequestDto>.Error(
                    "不能请求自己的位置", AttendanceLocationRequestRules.ErrorCodes.CannotRequestSelf);
            }

            // 范围：店长只能请求自己管辖分店的员工，管理员不限分店（与班中定位样本查询同一个范围服务）。
            var access = await ResolveManagedStoreAccessAsync(storeCode);
            if (!access.Success)
            {
                return ApiResponse<AttendanceLocationRequestDto>.Error(access.Message, access.ErrorCode);
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;

            // 只限已上班期间：目标员工在该店此刻必须处于上班中（最近一条有效打卡是上班卡）。
            if (!await HasActiveLocationSampleShiftAsync(targetUserGuid, storeCode, now))
            {
                return ApiResponse<AttendanceLocationRequestDto>.Error(
                    "该员工当前没有处在上班中，无法请求位置",
                    AttendanceLocationRequestRules.ErrorCodes.ActiveShiftRequired);
            }

            // 防骚扰：已有未过期的待响应请求，或距上次请求不足最小间隔，都拒绝并给出明确错误。
            // 先后两次并发创建理论上可能同时通过（没有跨行唯一约束）；后果只是多发一条请求，限频是防骚扰而非安全边界，不为此加锁。
            var lookback = now - (AttendanceLocationRequestRules.MinInterval > AttendanceLocationRequestRules.DefaultValidity
                ? AttendanceLocationRequestRules.MinInterval
                : AttendanceLocationRequestRules.DefaultValidity);
            var recent = await _db.Queryable<AttendanceLocationRequest>()
                .Where(item => !item.IsDeleted && item.TargetUserGuid == targetUserGuid && item.CreatedAt > lookback)
                .OrderByDescending(item => item.CreatedAt)
                .ToListAsync();
            if (recent.Any(item =>
                    item.Status == AttendanceLocationRequestStatuses.Pending && item.ExpiresAtUtc > now))
            {
                return ApiResponse<AttendanceLocationRequestDto>.Error(
                    "该员工已有尚未响应的位置请求，请等待其响应或请求过期",
                    AttendanceLocationRequestRules.ErrorCodes.Pending);
            }

            var latest = recent.FirstOrDefault(item =>
                item.CreatedAt > now - AttendanceLocationRequestRules.MinInterval);
            if (latest != null)
            {
                var minutes = AttendanceLocationRequestRules.RemainingWholeMinutes(latest.CreatedAt, now);
                return ApiResponse<AttendanceLocationRequestDto>.Error(
                    $"同一员工 {(int)AttendanceLocationRequestRules.MinInterval.TotalMinutes} 分钟内只能被请求一次，请 {minutes} 分钟后再试",
                    AttendanceLocationRequestRules.ErrorCodes.RateLimited);
            }

            var model = new AttendanceLocationRequest
            {
                RequestGuid = Guid.NewGuid().ToString(),
                StoreCode = storeCode,
                TargetUserGuid = targetUserGuid,
                RequestedByUserGuid = requesterGuid,
                Status = AttendanceLocationRequestStatuses.Pending,
                ExpiresAtUtc = now.Add(AttendanceLocationRequestRules.DefaultValidity),
                CreatedAt = now,
                CreatedBy = _currentUserService.GetCurrentUsername(),
                UpdatedAt = now,
                UpdatedBy = _currentUserService.GetCurrentUsername(),
            };
            await _db.Insertable(model).ExecuteCommandAsync();

            _logger.LogInformation(
                "位置请求已创建 RequestGuid={RequestGuid} StoreCode={StoreCode} TargetUserGuid={TargetUserGuid} RequestedByUserGuid={RequestedByUserGuid} ExpiresAtUtc={ExpiresAtUtc:o}",
                model.RequestGuid,
                model.StoreCode,
                model.TargetUserGuid,
                model.RequestedByUserGuid,
                model.ExpiresAtUtc);

            // 通知在请求落库之后发（发布器自带事务，不能嵌在业务事务里）；失败只记日志，不改变请求结果。
            if (_locationRequestNotifier != null)
            {
                await _locationRequestNotifier.NotifyRequestedAsync(model);
            }

            var dtos = await BuildLocationRequestDtosAsync(new List<AttendanceLocationRequest> { model }, now);
            return ApiResponse<AttendanceLocationRequestDto>.OK(dtos[0], "位置请求已发送");
        }

        public async Task<ApiResponse<List<AttendanceLocationRequestDto>>> GetLocationRequestsAsync(
            AttendanceLocationRequestQueryDto query
        )
        {
            query ??= new AttendanceLocationRequestQueryDto();
            var storeAccess = await ResolveManagedStoreAccessAsync(query.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<List<AttendanceLocationRequestDto>>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            var limit = Math.Clamp(
                query.Limit ?? AttendanceLocationRequestRules.DefaultListLimit,
                1,
                AttendanceLocationRequestRules.MaxListLimit);
            var requestedStore = query.StoreCode?.Trim();
            var requestedTarget = query.TargetUserGuid?.Trim();

            // 范围口径与班中定位样本查询一致：指定分店按该店；不指定时，受限账号只看自己管辖的分店。
            var rows = await _db.Queryable<AttendanceLocationRequest>()
                .Where(item => !item.IsDeleted)
                .WhereIF(!string.IsNullOrWhiteSpace(requestedStore), item => item.StoreCode == requestedStore!)
                .WhereIF(
                    string.IsNullOrWhiteSpace(requestedStore) && storeAccess.StoreCodes.Count > 0,
                    item => storeAccess.StoreCodes.Contains(item.StoreCode))
                .WhereIF(!string.IsNullOrWhiteSpace(requestedTarget), item => item.TargetUserGuid == requestedTarget!)
                // 同一创建时刻可能有多条，追加 Id 作为次级排序，保证顺序稳定。
                .OrderByDescending(item => item.CreatedAt)
                .OrderByDescending(item => item.Id)
                .Take(limit)
                .ToListAsync();

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            return ApiResponse<List<AttendanceLocationRequestDto>>.OK(await BuildLocationRequestDtosAsync(rows, now));
        }

        public async Task<ApiResponse<List<AttendanceLocationRequestDto>>> GetMyPendingLocationRequestsAsync()
        {
            var userGuid = ResolveCurrentUserGuid();
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            var rows = await _db.Queryable<AttendanceLocationRequest>()
                .Where(item =>
                    !item.IsDeleted
                    && item.TargetUserGuid == userGuid
                    && item.Status == AttendanceLocationRequestStatuses.Pending
                    && item.ExpiresAtUtc > now)
                .OrderByDescending(item => item.CreatedAt)
                .ToListAsync();

            // 仍在班中才有待办：员工下班后不再取、不再收。同一门店只判定一次。
            var stillOnShift = new List<AttendanceLocationRequest>();
            var shiftByStore = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (!shiftByStore.TryGetValue(row.StoreCode, out var active))
                {
                    active = await HasActiveLocationSampleShiftAsync(userGuid, row.StoreCode, now);
                    shiftByStore[row.StoreCode] = active;
                }

                if (active)
                {
                    stillOnShift.Add(row);
                }
            }

            return ApiResponse<List<AttendanceLocationRequestDto>>.OK(
                await BuildLocationRequestDtosAsync(stillOnShift, now));
        }

        public async Task<ApiResponse<AttendanceLocationRequestDto>> RespondLocationRequestAsync(
            string requestGuid,
            RespondAttendanceLocationRequestDto request
        )
        {
            var userGuid = ResolveCurrentUserGuid();
            var guid = requestGuid?.Trim();
            if (string.IsNullOrWhiteSpace(guid) || request == null)
            {
                return NotFoundLocationRequest();
            }

            // 只能响应发给自己的请求；别人的请求与不存在的请求同样返回 NOT_FOUND，不泄露存在性。
            var row = await _db.Queryable<AttendanceLocationRequest>()
                .Where(item => !item.IsDeleted && item.RequestGuid == guid && item.TargetUserGuid == userGuid)
                .FirstAsync();
            if (row == null)
            {
                return NotFoundLocationRequest();
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;

            // 幂等：已响应的请求再次响应（客户端没收到上次的响应）直接返回结果，不再写样本。
            if (row.Status == AttendanceLocationRequestStatuses.Responded)
            {
                return await RespondedLocationRequestResultAsync(row, now, alreadyResponded: true);
            }

            if (row.Status == AttendanceLocationRequestStatuses.Cancelled)
            {
                return ApiResponse<AttendanceLocationRequestDto>.Error(
                    "该位置请求已作废", AttendanceLocationRequestRules.ErrorCodes.Cancelled);
            }

            if (row.ExpiresAtUtc <= now)
            {
                return ApiResponse<AttendanceLocationRequestDto>.Error(
                    "该位置请求已过期", AttendanceLocationRequestRules.ErrorCodes.Expired);
            }

            var location = RequiredLocationValidator.ValidateCoordinates(
                request.LocationLatitude,
                request.LocationLongitude,
                request.LocationPermissionStatus,
                request.LocationCapturedAtUtc,
                "响应位置请求需要位置信息");
            if (!location.Success)
            {
                return ApiResponse<AttendanceLocationRequestDto>.Error(location.Message, location.ErrorCode);
            }

            // 采集时间以服务端时钟为准：不能晚于现在（容忍设备时钟轻微超前），也不能早于请求创建时间——
            // 早于请求的点不是为这次请求取的，拒绝拿旧位置交差。
            var capturedAt = NormalizeBatchCapturedAtUtc(request.LocationCapturedAtUtc!.Value);
            if (capturedAt > now.Add(AttendanceLocationRequestRules.CapturedFutureTolerance))
            {
                return ApiResponse<AttendanceLocationRequestDto>.Error(
                    "定位采集时间晚于服务器时间", AttendanceLocationRequestRules.ErrorCodes.SampleInFuture);
            }

            if (capturedAt < row.CreatedAt.Subtract(AttendanceLocationRequestRules.CapturedBeforeRequestTolerance))
            {
                return ApiResponse<AttendanceLocationRequestDto>.Error(
                    "定位需要在收到请求之后重新获取", AttendanceLocationRequestRules.ErrorCodes.LocationStale);
            }

            // 服务端收到响应时再复核一次：仍在班中才收。下班后不再收，请求同时作废（带原因，供 Web 展示）。
            if (!await HasActiveLocationSampleShiftAsync(userGuid, row.StoreCode, now))
            {
                await _db.Updateable<AttendanceLocationRequest>()
                    .SetColumns(item => new AttendanceLocationRequest
                    {
                        Status = AttendanceLocationRequestStatuses.Cancelled,
                        StatusReason = AttendanceLocationRequestStatusReasons.ClockedOut,
                        UpdatedAt = now,
                    })
                    .Where(item => item.Id == row.Id && item.Status == AttendanceLocationRequestStatuses.Pending)
                    .ExecuteCommandAsync();
                _logger.LogInformation(
                    "位置请求因员工已下班作废 RequestGuid={RequestGuid} StoreCode={StoreCode}",
                    row.RequestGuid,
                    row.StoreCode);
                return ApiResponse<AttendanceLocationRequestDto>.Error(
                    "已下班，位置请求已作废，无需再上报位置",
                    AttendanceLocationRequestRules.ErrorCodes.ActiveShiftRequired);
            }

            var sampleGuid = await InsertLocationRequestSampleAsync(userGuid, row, request, capturedAt, now);

            // 条件更新：只有仍是 Pending 的才翻成 Responded。并发的重复响应里输的一方看到 0 行受影响，改读最新状态。
            var updated = await _db.Updateable<AttendanceLocationRequest>()
                .SetColumns(item => new AttendanceLocationRequest
                {
                    Status = AttendanceLocationRequestStatuses.Responded,
                    RespondedAtUtc = now,
                    SampleGuid = sampleGuid,
                    UpdatedAt = now,
                })
                .Where(item => item.Id == row.Id && item.Status == AttendanceLocationRequestStatuses.Pending)
                .ExecuteCommandAsync();
            if (updated == 0)
            {
                var latest = await _db.Queryable<AttendanceLocationRequest>()
                    .Where(item => item.Id == row.Id)
                    .FirstAsync();
                if (latest?.Status == AttendanceLocationRequestStatuses.Responded)
                {
                    return await RespondedLocationRequestResultAsync(latest, now, alreadyResponded: true);
                }

                return ApiResponse<AttendanceLocationRequestDto>.Error(
                    "该位置请求已作废", AttendanceLocationRequestRules.ErrorCodes.Cancelled);
            }

            _logger.LogInformation(
                "位置请求已响应 RequestGuid={RequestGuid} StoreCode={StoreCode} SampleGuid={SampleGuid}",
                row.RequestGuid,
                row.StoreCode,
                sampleGuid);

            row.Status = AttendanceLocationRequestStatuses.Responded;
            row.RespondedAtUtc = now;
            row.SampleGuid = sampleGuid;
            return await RespondedLocationRequestResultAsync(row, now, alreadyResponded: false);
        }

        private static ApiResponse<AttendanceLocationRequestDto> NotFoundLocationRequest() =>
            ApiResponse<AttendanceLocationRequestDto>.Error(
                "未找到该位置请求", AttendanceLocationRequestRules.ErrorCodes.NotFound);

        private async Task<ApiResponse<AttendanceLocationRequestDto>> RespondedLocationRequestResultAsync(
            AttendanceLocationRequest row,
            DateTime now,
            bool alreadyResponded)
        {
            var dto = (await BuildLocationRequestDtosAsync(new List<AttendanceLocationRequest> { row }, now))[0];
            dto.AlreadyResponded = alreadyResponded;
            return ApiResponse<AttendanceLocationRequestDto>.OK(dto, alreadyResponded ? "位置已上报" : "位置已上报，谢谢");
        }

        /// <summary>
        /// 把响应的点写入班中样本（EventType=ServerRequest），返回样本 SampleGuid。
        /// ClientSampleId 直接用 RequestGuid：样本表有 (UserGuid, ClientSampleId) 过滤唯一索引，
        /// 同一请求被并发或重试响应多次也只会落一条样本——沿用批量上传按 ClientSampleId 幂等的思路。
        /// </summary>
        private async Task<string> InsertLocationRequestSampleAsync(
            string userGuid,
            AttendanceLocationRequest row,
            RespondAttendanceLocationRequestDto request,
            DateTime capturedAtUtc,
            DateTime now)
        {
            var sample = new AttendanceLocationSample
            {
                SampleGuid = Guid.NewGuid().ToString(),
                UserGuid = userGuid,
                StoreCode = row.StoreCode,
                HardwareId = NormalizeOptional(request.HardwareId, 100),
                SystemDeviceNumber = NormalizeOptional(request.SystemDeviceNumber, 100),
                DeviceSystem = NormalizeOptional(request.DeviceSystem, 30),
                EventType = AttendanceLocationRequestRules.SampleEventType,
                LocationLatitude = request.LocationLatitude!.Value,
                LocationLongitude = request.LocationLongitude!.Value,
                LocationAccuracyMeters = request.LocationAccuracy,
                LocationPermissionStatus = NormalizeOptional(request.LocationPermissionStatus, 30),
                LocationCapturedAtUtc = capturedAtUtc,
                ClientSampleId = row.RequestGuid,
                IsMocked = request.IsMocked,
                CreatedAt = now,
                CreatedBy = _currentUserService.GetCurrentUsername(),
            };

            try
            {
                await _db.Insertable(sample).ExecuteCommandAsync();
                return sample.SampleGuid;
            }
            catch (Exception exception) when (
                AttendancePunchPersistenceException.IsUniqueConstraintViolation(exception))
            {
                // 同一请求已写过样本（并发或上次响应只落了一半）：取已有的那条，保持幂等。
                var existing = await _db.Queryable<AttendanceLocationSample>()
                    .Where(item => item.UserGuid == userGuid && item.ClientSampleId == row.RequestGuid)
                    .Select(item => item.SampleGuid)
                    .FirstAsync();
                return existing ?? sample.SampleGuid;
            }
        }

        /// <summary>请求行 → 对外视图：推导有效状态、补人名 / 店名，已响应的补上样本坐标。</summary>
        private async Task<List<AttendanceLocationRequestDto>> BuildLocationRequestDtosAsync(
            IReadOnlyList<AttendanceLocationRequest> rows,
            DateTime now)
        {
            if (rows.Count == 0)
            {
                return new List<AttendanceLocationRequestDto>();
            }

            var names = await ResolveUserDisplayNamesAsync(
                rows.SelectMany(item => new[] { item.TargetUserGuid, item.RequestedByUserGuid }));

            var storeCodes = rows.Select(item => item.StoreCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var storeNames = (await _db.Queryable<Store>()
                    .Where(item => storeCodes.Contains(item.StoreCode))
                    .Select(item => new { item.StoreCode, item.StoreName })
                    .ToListAsync())
                .GroupBy(item => item.StoreCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().StoreName, StringComparer.OrdinalIgnoreCase);

            var sampleGuids = rows
                .Where(item => !string.IsNullOrWhiteSpace(item.SampleGuid))
                .Select(item => item.SampleGuid!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var samples = sampleGuids.Count == 0
                ? new Dictionary<string, AttendanceLocationSample>(StringComparer.Ordinal)
                : (await _db.Queryable<AttendanceLocationSample>()
                        .Where(item => sampleGuids.Contains(item.SampleGuid))
                        .ToListAsync())
                    .ToDictionary(item => item.SampleGuid, StringComparer.Ordinal);

            return rows.Select(item =>
            {
                samples.TryGetValue(item.SampleGuid ?? string.Empty, out var sample);
                return new AttendanceLocationRequestDto
                {
                    RequestGuid = item.RequestGuid,
                    StoreCode = item.StoreCode,
                    StoreName = storeNames.GetValueOrDefault(item.StoreCode),
                    TargetUserGuid = item.TargetUserGuid,
                    TargetUserName = names.GetValueOrDefault(item.TargetUserGuid),
                    RequestedByUserGuid = item.RequestedByUserGuid,
                    RequestedByName = names.GetValueOrDefault(item.RequestedByUserGuid),
                    Status = AttendanceLocationRequestRules.ResolveEffectiveStatus(item.Status, item.ExpiresAtUtc, now),
                    StatusReason = item.StatusReason,
                    CreatedAtUtc = DateTime.SpecifyKind(item.CreatedAt, DateTimeKind.Utc),
                    ExpiresAtUtc = DateTime.SpecifyKind(item.ExpiresAtUtc, DateTimeKind.Utc),
                    RespondedAtUtc = item.RespondedAtUtc.HasValue
                        ? DateTime.SpecifyKind(item.RespondedAtUtc.Value, DateTimeKind.Utc)
                        : null,
                    SampleGuid = item.SampleGuid,
                    LocationLatitude = sample?.LocationLatitude,
                    LocationLongitude = sample?.LocationLongitude,
                    LocationAccuracy = sample?.LocationAccuracyMeters,
                    LocationCapturedAtUtc = sample == null
                        ? null
                        : DateTime.SpecifyKind(sample.LocationCapturedAtUtc, DateTimeKind.Utc),
                };
            }).ToList();
        }
    }
}
