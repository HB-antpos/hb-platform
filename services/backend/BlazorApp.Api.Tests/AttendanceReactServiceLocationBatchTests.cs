using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// 班中定位批量上传（POST attendance/location-samples/batch）：按 ClientSampleId 幂等、逐点结果、
    /// 48 小时补传窗口、班次有效性「相对采集时刻」判定。夹具与造数方法共用 AttendanceReactServiceTests。
    /// BRI 店（无 TimeZoneId）按店名推导为 Australia/Brisbane（UTC+10，无夏令时）。
    /// </summary>
    public sealed partial class AttendanceReactServiceTests
    {
        // 固定的「服务器当前时间」：2026-05-18 04:00Z = 布里斯班本地 14:00，本地当日 0 点 = 2026-05-17 14:00Z。
        private static readonly DateTime LocationBatchNow = new(2026, 5, 18, 4, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenAllSamplesInsideShift_AcceptsAndStoresEveryPoint()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            await InsertBatchPunchesAsync(CreateBatchPunch("batch-in", "ClockIn", LocationBatchNow.AddHours(-3)));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var first = BatchItem("11111111-1111-1111-1111-111111111111", LocationBatchNow.AddMinutes(-90));
            first.IsMocked = true;
            var second = BatchItem("22222222-2222-2222-2222-222222222222", LocationBatchNow.AddMinutes(-60), "ShiftHeartbeat");
            var third = BatchItem("33333333-3333-3333-3333-333333333333", LocationBatchNow.AddMinutes(-5));
            third.EventType = null; // 缺省事件类型落为 ShiftSample

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(first, second, third));

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal(
                new[] { first.ClientSampleId, second.ClientSampleId, third.ClientSampleId },
                result.Data!.Accepted);
            Assert.Empty(result.Data.Duplicates);
            Assert.Empty(result.Data.Rejected);

            var stored = await _db.Queryable<AttendanceLocationSample>()
                .OrderBy(item => item.LocationCapturedAtUtc)
                .ToListAsync();
            Assert.Equal(3, stored.Count);
            Assert.All(stored, row =>
            {
                Assert.Equal("staff-user", row.UserGuid);
                Assert.Equal("BRI", row.StoreCode);
                Assert.Equal("hbmobile-test-device", row.HardwareId);
                Assert.Equal("SYS-001", row.SystemDeviceNumber);
                Assert.Equal("iOS", row.DeviceSystem);
                Assert.Equal("granted", row.LocationPermissionStatus);
                Assert.Equal(-27.4705, row.LocationLatitude);
                Assert.Equal(153.0260, row.LocationLongitude);
                Assert.Equal(18.2, row.LocationAccuracyMeters);
            });
            Assert.Equal(first.ClientSampleId, stored[0].ClientSampleId);
            Assert.True(stored[0].IsMocked);
            Assert.Equal("ShiftSample", stored[0].EventType);
            Assert.Equal(second.ClientSampleId, stored[1].ClientSampleId);
            Assert.False(stored[1].IsMocked);
            Assert.Equal("ShiftHeartbeat", stored[1].EventType);
            Assert.Equal("ShiftSample", stored[2].EventType);
            // 采集时间原样入库（不是上传时间）。
            Assert.Equal(LocationBatchNow.AddMinutes(-90), stored[0].LocationCapturedAtUtc);
            Assert.Equal(3, stored.Select(row => row.SampleGuid).Distinct().Count());
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenResubmitted_ReturnsDuplicatesWithoutInsertingAgain()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            await InsertBatchPunchesAsync(CreateBatchPunch("batch-in", "ClockIn", LocationBatchNow.AddHours(-3)));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var request = CreateBatchRequest(
                BatchItem("dup-a", LocationBatchNow.AddMinutes(-30)),
                BatchItem("dup-b", LocationBatchNow.AddMinutes(-20)));
            Assert.True((await service.CreateLocationSamplesBatchAsync(request)).Success);

            // 客户端没收到响应，原样重试；其间又追加了一个新点。
            request.Samples!.Add(BatchItem("dup-c", LocationBatchNow.AddMinutes(-10)));
            var retry = await service.CreateLocationSamplesBatchAsync(request);

            Assert.True(retry.Success);
            Assert.Equal(new[] { "dup-c" }, retry.Data!.Accepted);
            Assert.Equal(new[] { "dup-a", "dup-b" }, retry.Data.Duplicates);
            Assert.Empty(retry.Data.Rejected);
            Assert.Equal(3, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenAlreadyStoredPointIsNowOlderThanWindow_StillReportsDuplicate()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            await InsertBatchPunchesAsync(CreateBatchPunch("batch-in", "ClockIn", LocationBatchNow.AddHours(-3)));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var request = CreateBatchRequest(BatchItem("old-dup", LocationBatchNow.AddMinutes(-30)));
            Assert.True((await service.CreateLocationSamplesBatchAsync(request)).Success);

            // 两天后才重试同一个点：幂等优先，已入库的点是 Duplicates 而不是 SAMPLE_TOO_OLD。
            _timeProvider.SetUtcNow(LocationBatchNow.AddDays(3));
            var retry = await service.CreateLocationSamplesBatchAsync(request);

            Assert.Equal(new[] { "old-dup" }, retry.Data!.Duplicates);
            Assert.Empty(retry.Data.Accepted);
            Assert.Empty(retry.Data.Rejected);
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenSameIdRepeatsInRequest_FirstIsAcceptedRestAreDuplicates()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            await InsertBatchPunchesAsync(CreateBatchPunch("batch-in", "ClockIn", LocationBatchNow.AddHours(-3)));
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                BatchItem("AAAA", LocationBatchNow.AddMinutes(-40)),
                BatchItem("bbbb", LocationBatchNow.AddMinutes(-30)),
                BatchItem("aaaa", LocationBatchNow.AddMinutes(-20)), // 与第一个仅大小写不同，同一个 ID
                BatchItem("AAAA", LocationBatchNow.AddMinutes(-10))));

            Assert.Equal(new[] { "AAAA", "bbbb" }, result.Data!.Accepted);
            Assert.Equal(new[] { "aaaa", "AAAA" }, result.Data.Duplicates);
            Assert.Empty(result.Data.Rejected);
            var stored = await _db.Queryable<AttendanceLocationSample>().ToListAsync();
            Assert.Equal(2, stored.Count);
            // 入库的是第一个点（按正常流程处理的那个），其采集时间为 -40 分钟。
            Assert.Equal(
                LocationBatchNow.AddMinutes(-40),
                stored.Single(row => row.ClientSampleId == "AAAA").LocationCapturedAtUtc);
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenFirstOfRepeatedIdIsRejected_LaterRepeatsAreStillDuplicates()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            await InsertBatchPunchesAsync(CreateBatchPunch("batch-in", "ClockIn", LocationBatchNow.AddHours(-3)));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var bad = BatchItem("rep-1", LocationBatchNow.AddMinutes(-40));
            bad.LocationLatitude = 95;

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                bad,
                BatchItem("rep-1", LocationBatchNow.AddMinutes(-30))));

            // 契约：同请求内重复的 ID，第一个按正常流程（被拒），其后的归入 Duplicates。
            Assert.Empty(result.Data!.Accepted);
            Assert.Equal(new[] { "rep-1" }, result.Data.Duplicates);
            Assert.Equal("LOCATION_REQUIRED", Assert.Single(result.Data.Rejected).ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenSameClientIdUsedByAnotherUser_DoesNotConflict()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            await InsertBatchPunchesAsync(
                CreateBatchPunch("staff-in", "ClockIn", LocationBatchNow.AddHours(-3)),
                CreateBatchPunch("manager-in", "ClockIn", LocationBatchNow.AddHours(-3), "manager-user"));
            var item = BatchItem("shared-id", LocationBatchNow.AddMinutes(-30));

            var staff = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateLocationSamplesBatchAsync(CreateBatchRequest(item));
            var manager = await CreateService("manager-user", "manager", "StoreManager")
                .CreateLocationSamplesBatchAsync(CreateBatchRequest(item));

            Assert.Equal(new[] { "shared-id" }, staff.Data!.Accepted);
            Assert.Equal(new[] { "shared-id" }, manager.Data!.Accepted);
            Assert.Equal(2, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenConcurrentRequestInsertsSameIdFirst_TreatsAsDuplicateAndKeepsOthers()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            await InsertBatchPunchesAsync(CreateBatchPunch("batch-in", "ClockIn", LocationBatchNow.AddHours(-3)));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var raceInjected = false;
            // 模拟并发：服务端「先查再插」之间，另一个请求（另一条连接）抢先把 race-1 入库，
            // 于是本请求对 race-1 的插入撞上过滤唯一索引，必须归入 Duplicates，且不影响后面的点。
            _db.Aop.OnLogExecuting = (sql, _) =>
            {
                if (raceInjected
                    || !sql.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase)
                    || !sql.Contains("AttendanceLocationSample", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                raceInjected = true;
                using var other = new SqliteConnection($"Data Source={_dbPath}");
                other.Open();
                using var command = other.CreateCommand();
                command.CommandText =
                    "INSERT INTO AttendanceLocationSample "
                    + "(SampleGuid, UserGuid, StoreCode, EventType, LocationLatitude, LocationLongitude, "
                    + "LocationCapturedAtUtc, CreatedAt, ClientSampleId, IsDeleted) "
                    + "VALUES ('racer-sample', 'staff-user', 'BRI', 'ShiftSample', -27.4, 153.0, "
                    + "'2026-05-18 03:00:00', '2026-05-18 03:00:00', 'race-1', 0)";
                command.ExecuteNonQuery();
            };

            try
            {
                var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                    BatchItem("race-1", LocationBatchNow.AddMinutes(-30)),
                    BatchItem("race-2", LocationBatchNow.AddMinutes(-20)),
                    BatchItem("race-3", LocationBatchNow.AddMinutes(-10))));

                Assert.True(raceInjected);
                Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
                Assert.Equal(new[] { "race-2", "race-3" }, result.Data!.Accepted);
                Assert.Equal(new[] { "race-1" }, result.Data.Duplicates);
                Assert.Empty(result.Data.Rejected);
            }
            finally
            {
                _db.Aop.OnLogExecuting = null;
            }

            var stored = await _db.Queryable<AttendanceLocationSample>().ToListAsync();
            Assert.Equal(3, stored.Count); // 抢先入库的 1 条 + 本请求的 2 条
            Assert.Single(stored, row => row.ClientSampleId == "race-1");
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenLocationInvalid_RejectsOnlyThosePointsWithLocationRequired()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            await InsertBatchPunchesAsync(CreateBatchPunch("batch-in", "ClockIn", LocationBatchNow.AddHours(-3)));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var captured = LocationBatchNow.AddMinutes(-30);
            var noLatitude = BatchItem("no-lat", captured);
            noLatitude.LocationLatitude = null;
            var noLongitude = BatchItem("no-lon", captured);
            noLongitude.LocationLongitude = null;
            var latitudeOutOfRange = BatchItem("lat-91", captured);
            latitudeOutOfRange.LocationLatitude = 91;
            var longitudeOutOfRange = BatchItem("lon-181", captured);
            longitudeOutOfRange.LocationLongitude = -180.5;
            var noCapturedAt = BatchItem("no-time", captured);
            noCapturedAt.LocationCapturedAtUtc = null;

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                noLatitude,
                noLongitude,
                latitudeOutOfRange,
                longitudeOutOfRange,
                noCapturedAt,
                BatchItem("good", captured)));

            Assert.Equal(new[] { "good" }, result.Data!.Accepted);
            Assert.Equal(
                new[] { "no-lat", "no-lon", "lat-91", "lon-181", "no-time" },
                result.Data.Rejected.Select(item => item.ClientSampleId));
            Assert.All(result.Data.Rejected, item =>
            {
                Assert.Equal("LOCATION_REQUIRED", item.ErrorCode);
                Assert.False(string.IsNullOrWhiteSpace(item.Message));
            });
            Assert.Equal(1, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Theory]
        [InlineData("denied")]
        [InlineData("undetermined")]
        [InlineData("")]
        [InlineData(null)]
        public async Task CreateLocationSamplesBatchAsync_WhenPermissionIsNotGranted_RejectsWithLocationRequired(
            string? permissionStatus)
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            await InsertBatchPunchesAsync(CreateBatchPunch("batch-in", "ClockIn", LocationBatchNow.AddHours(-3)));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var item = BatchItem("no-permission", LocationBatchNow.AddMinutes(-30));
            item.LocationPermissionStatus = permissionStatus;

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(item));

            var rejected = Assert.Single(result.Data!.Rejected);
            Assert.Equal("no-permission", rejected.ClientSampleId);
            Assert.Equal("LOCATION_REQUIRED", rejected.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenCapturedTimeOutsideWindow_RejectsTooOldAndInFuture()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            // 上班卡在 3 小时前、一直没下班：保证班次对这些点不是障碍，被拒的原因只可能是采集时间。
            await InsertBatchPunchesAsync(CreateBatchPunch("batch-in", "ClockIn", LocationBatchNow.AddHours(-3)));
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                BatchItem("too-old", LocationBatchNow.AddHours(-48).AddSeconds(-1)),
                BatchItem("future", LocationBatchNow.AddMinutes(5).AddSeconds(1)),
                BatchItem("future-edge", LocationBatchNow.AddMinutes(5))));

            Assert.Equal(
                new[]
                {
                    ("too-old", "SAMPLE_TOO_OLD"),
                    ("future", "SAMPLE_IN_FUTURE"),
                },
                result.Data!.Rejected.Select(item => (item.ClientSampleId, item.ErrorCode)));
            // 恰好晚 5 分钟仍在容忍范围内，且此时上班卡早于它，所以入库。
            Assert.Equal(new[] { "future-edge" }, result.Data.Accepted);
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenCapturedExactlyAtWindowOldestEdge_AcceptsBackfill()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            // 48 小时前整点 = 2026-05-16 04:00Z（本地 14:00），上班卡 13:30 本地，同一天、10 小时以内。
            var oldest = LocationBatchNow.AddHours(-48);
            await InsertBatchPunchesAsync(CreateBatchPunch("old-in", "ClockIn", oldest.AddMinutes(-30)));
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                BatchItem("edge", oldest),
                BatchItem("edge-plus", oldest.AddMinutes(20))));

            // 与单条接口的 30 分钟新鲜度限制不同：批量补传可以回溯到 48 小时前。
            Assert.Equal(new[] { "edge", "edge-plus" }, result.Data!.Accepted);
            Assert.Empty(result.Data.Rejected);
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenNoPunchesAtAll_RejectsActiveShiftRequired()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                BatchItem("no-shift-1", LocationBatchNow.AddMinutes(-30)),
                BatchItem("no-shift-2", LocationBatchNow.AddMinutes(-10))));

            Assert.True(result.Success);
            Assert.Empty(result.Data!.Accepted);
            Assert.All(result.Data.Rejected, item => Assert.Equal("ACTIVE_SHIFT_REQUIRED", item.ErrorCode));
            Assert.Equal(2, result.Data.Rejected.Count);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenSampleCapturedAfterClockOut_RejectsButTailBeforeClockOutIsAccepted()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            // 08:00 上班（本地 08:00 = 前一日 22:00Z），12:00 下班（02:00Z），休息后 13:00 再次上班（03:00Z）；
            // 现在 14:00 本地，客户端在下班打卡成功「之后」才上传上午的尾批点，下午的点也一起补传。
            var clockIn1 = LocationBatchNow.AddHours(-6); // 22:00Z
            var clockOut1 = LocationBatchNow.AddHours(-2); // 02:00Z
            var clockIn2 = LocationBatchNow.AddHours(-1); // 03:00Z
            await InsertBatchPunchesAsync(
                CreateBatchPunch("seg1-in", "ClockIn", clockIn1),
                CreateBatchPunch("seg1-out", "ClockOut", clockOut1),
                CreateBatchPunch("seg2-in", "ClockIn", clockIn2));
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                BatchItem("morning-1", clockIn1.AddMinutes(30)),        // 班内，下班卡之后才上传 → 接受
                BatchItem("morning-tail", clockOut1.AddSeconds(-1)),    // 下班前一秒的尾点 → 接受
                BatchItem("at-clock-out", clockOut1),                   // 恰在下班卡时刻：最后一条有效打卡是 ClockOut → 拒绝
                BatchItem("on-break", clockOut1.AddMinutes(30)),        // 休息期间 → 拒绝
                BatchItem("before-return", clockIn2.AddSeconds(-1)),    // 复工前一秒 → 拒绝
                BatchItem("afternoon", clockIn2.AddMinutes(20))));      // 复工之后 → 接受

            Assert.Equal(
                new[] { "morning-1", "morning-tail", "afternoon" },
                result.Data!.Accepted);
            Assert.Equal(
                new[] { "at-clock-out", "on-break", "before-return" },
                result.Data.Rejected.Select(item => item.ClientSampleId));
            Assert.All(result.Data.Rejected, item => Assert.Equal("ACTIVE_SHIFT_REQUIRED", item.ErrorCode));
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenClockInIsSupersededByClockOut_RejectsSamples()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            var staleClockIn = CreateBatchPunch("stale-in", "ClockIn", LocationBatchNow.AddHours(-1));
            var replacementClockOut = CreateBatchPunch("replacement-out", "ClockOut", LocationBatchNow.AddHours(-1).AddMinutes(-1));
            replacementClockOut.SupersedesPunchGuid = staleClockIn.PunchGuid;
            await InsertBatchPunchesAsync(staleClockIn, replacementClockOut);
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                BatchItem("superseded", LocationBatchNow.AddMinutes(-20))));

            Assert.Equal("ACTIVE_SHIFT_REQUIRED", Assert.Single(result.Data!.Rejected).ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Theory]
        [InlineData("staff-user", "OTHER")]
        [InlineData("manager-user", "BRI")]
        public async Task CreateLocationSamplesBatchAsync_WhenClockInBelongsToOtherScope_RejectsSamples(
            string punchUserGuid,
            string punchStoreCode)
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            await InsertBatchPunchesAsync(
                CreateBatchPunch("other-scope-in", "ClockIn", LocationBatchNow.AddHours(-3), punchUserGuid, punchStoreCode));
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                BatchItem("other-scope", LocationBatchNow.AddMinutes(-20))));

            Assert.Equal("ACTIVE_SHIFT_REQUIRED", Assert.Single(result.Data!.Rejected).ErrorCode);
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenShiftSpansTenHoursOrStoreLocalMidnight_AppliesPerSampleWindow()
        {
            await SeedStoreScopeAsync();
            await _db.Updateable<Store>()
                .SetColumns(item => item.TimeZoneId == "Australia/Sydney")
                .Where(item => item.StoreCode == "BRI")
                .ExecuteCommandAsync();
            // 悉尼 2026-01 为 UTC+11：本地 23:55（01-01）= 12:55Z。上班后一直没下班（跨午夜忘记打卡）。
            var now = new DateTime(2026, 1, 2, 3, 0, 0, DateTimeKind.Utc);
            _timeProvider.SetUtcNow(now);
            var clockIn = CreateBatchPunch("sydney-in", "ClockIn", new DateTime(2026, 1, 1, 12, 55, 0, DateTimeKind.Utc));
            clockIn.StoreTimeZone = "Australia/Sydney";
            await InsertBatchPunchesAsync(clockIn);
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                BatchItem("before-midnight", new DateTime(2026, 1, 1, 12, 58, 0, DateTimeKind.Utc)),  // 本地 23:58（01-01）→ 同一本地日，接受
                BatchItem("after-midnight", new DateTime(2026, 1, 1, 13, 10, 0, DateTimeKind.Utc)),   // 本地 00:10（01-02）→ 越过本地午夜，拒绝
                BatchItem("next-morning", new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc))));    // 本地 09:00（01-02）且距上班卡 9 小时，仍越过本地午夜 → 拒绝

            Assert.Equal(new[] { "before-midnight" }, result.Data!.Accepted);
            Assert.Equal(
                new[] { "after-midnight", "next-morning" },
                result.Data.Rejected.Select(item => item.ClientSampleId));
            Assert.All(result.Data.Rejected, item => Assert.Equal("ACTIVE_SHIFT_REQUIRED", item.ErrorCode));
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenSameInstantsInBrisbaneStore_LocalDayBoundaryFollowsStoreTimeZone()
        {
            await SeedStoreScopeAsync(); // BRI = Brisbane（UTC+10）：同样的 UTC 时刻在这里都还是 01-01 本地日。
            var now = new DateTime(2026, 1, 2, 3, 0, 0, DateTimeKind.Utc);
            _timeProvider.SetUtcNow(now);
            await InsertBatchPunchesAsync(
                CreateBatchPunch("brisbane-in", "ClockIn", new DateTime(2026, 1, 1, 12, 55, 0, DateTimeKind.Utc)));
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                BatchItem("a", new DateTime(2026, 1, 1, 12, 58, 0, DateTimeKind.Utc)),
                BatchItem("b", new DateTime(2026, 1, 1, 13, 10, 0, DateTimeKind.Utc)),   // 布里斯班本地 23:10，仍在同一天
                BatchItem("c", new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc))));  // 本地 01-02 08:00：已过本地午夜，且上班已 9 小时 → 拒绝

            Assert.Equal(new[] { "a", "b" }, result.Data!.Accepted);
            Assert.Equal("c", Assert.Single(result.Data.Rejected).ClientSampleId);
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenOpenClockInExceedsTenHours_RejectsLaterSamples()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            // 上班卡 2026-05-17 14:30Z（布里斯班本地 00:30，刚过本地午夜 30 分钟）：采集点距上班卡满 10 小时即失效。
            var clockIn = LocationBatchNow.AddHours(-13).AddMinutes(-30); // 14:30Z
            await InsertBatchPunchesAsync(CreateBatchPunch("long-in", "ClockIn", clockIn));
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                // 恰好 10 小时：窗口下限（不含）= 上班卡时刻，上班卡不在窗口内 → 拒绝
                BatchItem("exactly-10h", clockIn.AddHours(10)),
                BatchItem("just-inside", clockIn.AddHours(10).AddSeconds(-1)),  // 9:59:59 → 接受
                BatchItem("beyond-10h", clockIn.AddHours(10).AddSeconds(1))));  // 10:00:01 → 拒绝

            Assert.Equal(new[] { "just-inside" }, result.Data!.Accepted);
            Assert.Equal(
                new[] { "exactly-10h", "beyond-10h" },
                result.Data.Rejected.Select(item => item.ClientSampleId));
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenMixedOutcomes_ReturnsEachIdInRequestOrder()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            await InsertBatchPunchesAsync(CreateBatchPunch("batch-in", "ClockIn", LocationBatchNow.AddHours(-3)));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            Assert.True((await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                BatchItem("stored-before", LocationBatchNow.AddMinutes(-60))))).Success);

            var notGranted = BatchItem("denied", LocationBatchNow.AddMinutes(-50));
            notGranted.LocationPermissionStatus = "denied";
            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                BatchItem("new-1", LocationBatchNow.AddMinutes(-55)),
                BatchItem("stored-before", LocationBatchNow.AddMinutes(-60)),
                notGranted,
                BatchItem("new-2", LocationBatchNow.AddMinutes(-45)),
                BatchItem("new-1", LocationBatchNow.AddMinutes(-44)),
                BatchItem("future", LocationBatchNow.AddHours(1)),
                BatchItem("too-old", LocationBatchNow.AddDays(-5)),
                BatchItem("before-shift", LocationBatchNow.AddHours(-4))));

            Assert.Equal(new[] { "new-1", "new-2" }, result.Data!.Accepted);
            Assert.Equal(new[] { "stored-before", "new-1" }, result.Data.Duplicates);
            Assert.Equal(
                new[]
                {
                    ("denied", "LOCATION_REQUIRED"),
                    ("future", "SAMPLE_IN_FUTURE"),
                    ("too-old", "SAMPLE_TOO_OLD"),
                    ("before-shift", "ACTIVE_SHIFT_REQUIRED"),
                },
                result.Data.Rejected.Select(item => (item.ClientSampleId, item.ErrorCode)));
            Assert.Equal(3, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenClientSampleIdInvalid_RejectsWithClientSampleIdInvalid()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            await InsertBatchPunchesAsync(CreateBatchPunch("batch-in", "ClockIn", LocationBatchNow.AddHours(-3)));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var captured = LocationBatchNow.AddMinutes(-30);
            var maxLengthId = new string('x', 50);
            var tooLongId = new string('y', 51);

            var request = CreateBatchRequest(
                BatchItem("", captured),
                BatchItem("   ", captured),
                BatchItem(null, captured),
                BatchItem(tooLongId, captured),
                BatchItem(maxLengthId, captured));
            request.Samples!.Add(null); // JSON 里的 null 元素

            var result = await service.CreateLocationSamplesBatchAsync(request);

            Assert.True(result.Success);
            Assert.Equal(new[] { maxLengthId }, result.Data!.Accepted);
            Assert.Equal(5, result.Data.Rejected.Count);
            Assert.All(result.Data.Rejected, item => Assert.Equal("CLIENT_SAMPLE_ID_INVALID", item.ErrorCode));
            Assert.Equal(tooLongId, result.Data.Rejected[3].ClientSampleId);
            Assert.Equal(1, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenExactlyTwoHundredSamples_AcceptsAll()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            await InsertBatchPunchesAsync(CreateBatchPunch("batch-in", "ClockIn", LocationBatchNow.AddHours(-4)));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var items = Enumerable.Range(0, 200)
                .Select(index => BatchItem($"bulk-{index:000}", LocationBatchNow.AddMinutes(-200 + index)))
                .ToArray();

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(items));

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal(200, result.Data!.Accepted.Count);
            Assert.Empty(result.Data.Rejected);
            Assert.Equal(200, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenMoreThanTwoHundredOrEmpty_ReturnsTooManySamples()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            await InsertBatchPunchesAsync(CreateBatchPunch("batch-in", "ClockIn", LocationBatchNow.AddHours(-4)));
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var tooMany = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                Enumerable.Range(0, 201)
                    .Select(index => BatchItem($"over-{index:000}", LocationBatchNow.AddMinutes(-10)))
                    .ToArray()));
            var empty = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest());
            var missing = CreateBatchRequest();
            missing.Samples = null;
            var nullSamples = await service.CreateLocationSamplesBatchAsync(missing);

            foreach (var response in new[] { tooMany, empty, nullSamples })
            {
                Assert.False(response.Success);
                Assert.Equal("TOO_MANY_SAMPLES", response.ErrorCode);
                Assert.Null(response.Data);
            }
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenStoreMissingOrNotRelated_FailsWholeRequest()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            await InsertBatchPunchesAsync(CreateBatchPunch("batch-in", "ClockIn", LocationBatchNow.AddHours(-3)));
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var noStore = CreateBatchRequest(BatchItem("no-store", LocationBatchNow.AddMinutes(-10)));
            noStore.StoreCode = "  ";
            var storeRequired = await service.CreateLocationSamplesBatchAsync(noStore);
            var foreign = CreateBatchRequest(BatchItem("foreign", LocationBatchNow.AddMinutes(-10)));
            foreign.StoreCode = "OTHER"; // staff-user 只关联了 BRI
            var forbidden = await service.CreateLocationSamplesBatchAsync(foreign);

            Assert.False(storeRequired.Success);
            Assert.Equal("STORE_REQUIRED", storeRequired.ErrorCode);
            Assert.False(forbidden.Success);
            Assert.Equal("FORBIDDEN_STORE", forbidden.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_WhenAlsoUsingLegacySingleEndpoint_SingleRowsKeepNullClientIdAndDoNotCollide()
        {
            await SeedStoreScopeAsync();
            var capturedAt = DateTime.UtcNow;
            _timeProvider.SetUtcNow(capturedAt);
            // 单条接口的新鲜度校验用的是真实系统时钟，所以这里以真实当前时间造数；
            // 上班卡只提前 4 分钟，样本都在其后（避开布里斯班本地午夜前后几分钟的窗口边界）。
            await InsertBatchPunchesAsync(CreateBatchPunch("batch-in", "ClockIn", capturedAt.AddMinutes(-4)));
            var service = CreateService("staff-user", "staff", "StoreStaff");

            // 旧版单条接口不带 ClientSampleId：过滤唯一索引不约束 NULL，同一用户可以连续上传多条。
            var first = await service.CreateLocationSampleAsync(CreateLocationSampleRequest(capturedAt.AddMinutes(-2)));
            var second = await service.CreateLocationSampleAsync(CreateLocationSampleRequest(capturedAt.AddMinutes(-1)));
            var batch = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                BatchItem("with-id", capturedAt.AddMinutes(-3))));

            Assert.True(first.Success, first.Message);
            Assert.True(second.Success, second.Message);
            Assert.Equal(new[] { "with-id" }, batch.Data!.Accepted);
            var stored = await _db.Queryable<AttendanceLocationSample>().ToListAsync();
            Assert.Equal(3, stored.Count);
            Assert.Equal(2, stored.Count(row => row.ClientSampleId == null && row.IsMocked == null));
        }

        [Fact]
        public async Task CreateLocationSamplesBatchAsync_ShiftDecisionMatchesLegacySingleCheckForEveryCapturedTime()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationBatchNow);
            // 跨两个本地日、多段上下班、含被顶替的上班卡：批量判定必须与历史单条判定逐点一致。
            // 全部为布里斯班（UTC+10）：本地 5/17 00:00 = 5/16 14:00Z，本地 5/18 00:00 = 5/17 14:00Z。
            var anchor = new DateTime(2026, 5, 16, 0, 0, 0, DateTimeKind.Utc);
            var superseded = CreateBatchPunch("p-superseded-in", "ClockIn", anchor.AddDays(1).AddHours(16));
            var replacement = CreateBatchPunch("p-replacement-out", "ClockOut", anchor.AddDays(1).AddHours(16).AddMinutes(10));
            replacement.SupersedesPunchGuid = superseded.PunchGuid;
            await InsertBatchPunchesAsync(
                CreateBatchPunch("p1-in", "ClockIn", anchor.AddHours(23)),                // 5/16 23:00Z
                CreateBatchPunch("p2-out", "ClockOut", anchor.AddDays(1).AddHours(3)),    // 5/17 03:00Z
                CreateBatchPunch("p3-in", "ClockIn", anchor.AddDays(1).AddHours(4)),      // 5/17 04:00Z
                CreateBatchPunch("p4-out", "ClockOut", anchor.AddDays(1).AddHours(8)),    // 5/17 08:00Z
                CreateBatchPunch("p5-in", "ClockIn", anchor.AddDays(1).AddHours(13).AddMinutes(30)), // 5/17 13:30Z（本地 23:30，跨午夜）
                CreateBatchPunch("p6-out", "ClockOut", anchor.AddDays(2).AddMinutes(30)), // 5/18 00:30Z
                superseded,                                                                // 5/17 16:00Z 的上班卡被下面的下班卡顶替
                replacement,
                CreateBatchPunch("p7-in", "ClockIn", anchor.AddDays(2).AddHours(1)));     // 5/18 01:00Z，至今未下班
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var singleCheck = typeof(AttendanceReactService).GetMethod(
                "HasActiveLocationSampleShiftAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            // 从 47 小时前到现在，每 20 分钟一个采样点（141 个，≤ 200），整体错开 7 分钟：
            // 采样时刻不与任何打卡时刻重合（打卡都在整点/半点/十分），因为单条判定的上界是在 SQLite 里按文本比较的，
            // 与打卡时刻「恰好相等」的点会因为小数位格式不同而误判；相等边界另由规则单测和批量用例（下班卡时刻）覆盖。
            var captures = Enumerable.Range(0, 141)
                .Select(index => LocationBatchNow.AddHours(-47).AddMinutes(7 + index * 20))
                .ToList();
            var expected = new Dictionary<string, bool>();
            foreach (var capturedAt in captures)
            {
                expected[$"s-{capturedAt:yyyyMMddHHmm}"] = await (Task<bool>)singleCheck.Invoke(
                    service, new object[] { "staff-user", "BRI", capturedAt })!;
            }

            var result = await service.CreateLocationSamplesBatchAsync(CreateBatchRequest(
                captures.Select(capturedAt => BatchItem($"s-{capturedAt:yyyyMMddHHmm}", capturedAt)).ToArray()));

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            var accepted = result.Data!.Accepted.ToHashSet();
            Assert.Equal(captures.Count, accepted.Count + result.Data.Rejected.Count);
            Assert.All(result.Data.Rejected, item => Assert.Equal("ACTIVE_SHIFT_REQUIRED", item.ErrorCode));
            foreach (var (id, singleSaysActive) in expected)
            {
                Assert.True(
                    singleSaysActive == accepted.Contains(id),
                    $"{id}: 单条判定={singleSaysActive}，批量接受={accepted.Contains(id)}");
            }
            // 防止空转：场景里既有有效点也有被拒点。
            Assert.Contains(true, expected.Values);
            Assert.Contains(false, expected.Values);
        }

        [Fact]
        public void BatchEndpoint_UsesSamePunchSelfPolicyAsSingleEndpoint()
        {
            var controller = typeof(ReactAttendanceController);
            var single = controller.GetMethod(nameof(ReactAttendanceController.CreateLocationSample))!;
            var batch = controller.GetMethod(nameof(ReactAttendanceController.CreateLocationSamplesBatch))!;

            var batchRoute = batch.GetCustomAttribute<HttpPostAttribute>()!;
            Assert.Equal("location-samples/batch", batchRoute.Template);
            Assert.Equal(
                single.GetCustomAttribute<AuthorizeAttribute>()!.Policy,
                batch.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
            Assert.Equal(Permissions.Attendance.Punch.Self, batch.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        }

        private async Task InsertBatchPunchesAsync(params AttendancePunch[] punches) =>
            await _db.Insertable(punches).ExecuteCommandAsync();

        /// <summary>按 UTC 时刻造打卡（CreateStoredPunch 以布里斯班本地时间入参，UTC = 本地 - 10 小时）。</summary>
        private static AttendancePunch CreateBatchPunch(
            string punchGuid,
            string punchType,
            DateTime punchTimeUtc,
            string userGuid = "staff-user",
            string storeCode = "BRI")
        {
            var punch = CreateStoredPunch(punchGuid, punchType, punchTimeUtc.AddHours(10));
            punch.PunchTimeUtc = DateTime.SpecifyKind(punchTimeUtc, DateTimeKind.Utc);
            punch.UserGuid = userGuid;
            punch.StoreCode = storeCode;
            return punch;
        }

        private static AttendanceLocationSampleBatchRequestDto CreateBatchRequest(
            params AttendanceLocationSampleBatchItemDto[] samples)
        {
            return new AttendanceLocationSampleBatchRequestDto
            {
                StoreCode = "BRI",
                HardwareId = "hbmobile-test-device",
                SystemDeviceNumber = "SYS-001",
                DeviceSystem = "iOS",
                Samples = new List<AttendanceLocationSampleBatchItemDto?>(samples),
            };
        }

        private static AttendanceLocationSampleBatchItemDto BatchItem(
            string? clientSampleId,
            DateTime capturedAtUtc,
            string eventType = "ShiftSample")
        {
            return new AttendanceLocationSampleBatchItemDto
            {
                ClientSampleId = clientSampleId,
                EventType = eventType,
                LocationLatitude = -27.4705,
                LocationLongitude = 153.0260,
                LocationAccuracy = 18.2,
                IsMocked = false,
                LocationPermissionStatus = "granted",
                LocationCapturedAtUtc = DateTime.SpecifyKind(capturedAtUtc, DateTimeKind.Utc),
            };
        }
    }

    /// <summary>班次有效性纯函数：单条与批量共用，不依赖数据库，直接验证规则本身。</summary>
    public sealed class AttendanceLocationSampleShiftRulesTests
    {
        private static readonly DateTime Captured = new(2026, 5, 18, 4, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime WindowStart = Captured.AddHours(-10);

        private static readonly IReadOnlySet<string> NoneSuperseded = new HashSet<string>();

        private static AttendancePunch Punch(int id, string guid, string type, DateTime utc) =>
            new()
            {
                Id = id,
                PunchGuid = guid,
                PunchType = type,
                PunchTimeUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc),
            };

        [Fact]
        public void IsShiftActiveAt_OnlyLatestPunchBeforeCaptureMatters()
        {
            var punches = new[]
            {
                Punch(1, "in", "ClockIn", Captured.AddHours(-5)),
                Punch(2, "out", "ClockOut", Captured.AddHours(-2)),
            };

            // 下班卡之后采集 → 无效；下班卡之前（上传晚于下班也一样）→ 有效。
            Assert.False(AttendanceLocationSampleShiftRules.IsShiftActiveAt(punches, NoneSuperseded, Captured, WindowStart));
            Assert.True(AttendanceLocationSampleShiftRules.IsShiftActiveAt(
                punches, NoneSuperseded, Captured.AddHours(-3), Captured.AddHours(-3).AddHours(-10)));
        }

        [Fact]
        public void IsShiftActiveAt_PunchAtCaptureInstantCounts_AndLaterPunchIsIgnored()
        {
            var clockOutAtCapture = new[]
            {
                Punch(1, "in", "ClockIn", Captured.AddHours(-1)),
                Punch(2, "out", "ClockOut", Captured),
            };
            var clockOutAfterCapture = new[]
            {
                Punch(1, "in", "ClockIn", Captured.AddHours(-1)),
                Punch(2, "out", "ClockOut", Captured.AddTicks(1)),
            };

            Assert.False(AttendanceLocationSampleShiftRules.IsShiftActiveAt(clockOutAtCapture, NoneSuperseded, Captured, WindowStart));
            Assert.True(AttendanceLocationSampleShiftRules.IsShiftActiveAt(clockOutAfterCapture, NoneSuperseded, Captured, WindowStart));
        }

        [Fact]
        public void IsShiftActiveAt_WindowLowerBoundIsExclusive()
        {
            var atBoundary = new[] { Punch(1, "in", "ClockIn", WindowStart) };
            var justInside = new[] { Punch(1, "in", "ClockIn", WindowStart.AddTicks(1)) };

            Assert.False(AttendanceLocationSampleShiftRules.IsShiftActiveAt(atBoundary, NoneSuperseded, Captured, WindowStart));
            Assert.True(AttendanceLocationSampleShiftRules.IsShiftActiveAt(justInside, NoneSuperseded, Captured, WindowStart));
        }

        [Fact]
        public void IsShiftActiveAt_SupersededPunchIsSkippedAndNextEffectiveOneDecides()
        {
            var punches = new[]
            {
                Punch(1, "in-1", "ClockIn", Captured.AddHours(-4)),
                Punch(2, "in-2", "ClockIn", Captured.AddHours(-1)),
                Punch(3, "out-1", "ClockOut", Captured.AddHours(-2)),
            };

            // 最新的 in-2 被顶替后，回落到 out-1（下班）→ 无效；out-1 也被顶替则回落到 in-1 → 有效。
            Assert.False(AttendanceLocationSampleShiftRules.IsShiftActiveAt(
                punches, new HashSet<string> { "in-2" }, Captured, WindowStart));
            Assert.True(AttendanceLocationSampleShiftRules.IsShiftActiveAt(
                punches, new HashSet<string> { "in-2", "out-1" }, Captured, WindowStart));
        }

        [Fact]
        public void IsShiftActiveAt_SameInstantTiesBreakByHigherId()
        {
            var inThenOut = new[]
            {
                Punch(5, "in", "ClockIn", Captured.AddHours(-1)),
                Punch(6, "out", "ClockOut", Captured.AddHours(-1)),
            };
            var outThenIn = new[]
            {
                Punch(5, "out", "ClockOut", Captured.AddHours(-1)),
                Punch(6, "in", "ClockIn", Captured.AddHours(-1)),
            };

            Assert.False(AttendanceLocationSampleShiftRules.IsShiftActiveAt(inThenOut, NoneSuperseded, Captured, WindowStart));
            Assert.True(AttendanceLocationSampleShiftRules.IsShiftActiveAt(outThenIn, NoneSuperseded, Captured, WindowStart));
        }

        [Fact]
        public void IsShiftActiveAt_NoPunchesInWindow_ReturnsFalse()
        {
            Assert.False(AttendanceLocationSampleShiftRules.IsShiftActiveAt(
                Array.Empty<AttendancePunch>(), NoneSuperseded, Captured, WindowStart));
        }

        [Fact]
        public void ResolveWindowStartUtc_TakesTheLaterOfLocalDayStartAndTenHoursBefore()
        {
            var tenHoursBefore = Captured.AddHours(-10);

            // 本地当日 0 点晚于「前 10 小时」（刚过午夜不久）→ 取本地当日 0 点；否则取前 10 小时。
            Assert.Equal(
                Captured.AddHours(-2),
                AttendanceLocationSampleShiftRules.ResolveWindowStartUtc(Captured, Captured.AddHours(-2)));
            Assert.Equal(
                tenHoursBefore,
                AttendanceLocationSampleShiftRules.ResolveWindowStartUtc(Captured, Captured.AddHours(-14)));
        }
    }
}
