using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// 「服务端要求员工取一次高精度位置」：创建（范围 / 只限已上班 / 限频 / 通知）、历史、员工待办、员工响应。
    /// 夹具与造数方法共用 AttendanceReactServiceTests；通知落库到真实的通知发布器（SQLite）。
    /// 固定的「服务器当前时间」：2026-05-18 04:00Z = 布里斯班本地 14:00（BRI 店无 TimeZoneId，按店名推导为 Australia/Brisbane）。
    /// </summary>
    public sealed partial class AttendanceReactServiceTests
    {
        private static readonly DateTime LocationRequestNow = new(2026, 5, 18, 4, 0, 0, DateTimeKind.Utc);

        // 默认 null：不接通知平台；通知用例通过 UseLocationRequestNotifications 注入。
        private IAttendanceLocationRequestNotifier? _locationRequestNotifier;
        private ApprovalPublisherProbe? _locationRequestPublisherProbe;

        // ---------------------------------------------------------------------------------
        // 创建
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task CreateLocationRequestAsync_店长请求自己分店已上班的员工_落审计行并只通知该员工()
        {
            await SeedLocationRequestScenarioAsync();
            UseLocationRequestNotifications();
            await SetApprovalUserFullNameAsync("manager-user", "Mia Manager");

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .CreateLocationRequestAsync(LocationCreateDto());

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            var dto = result.Data!;
            Assert.Equal(AttendanceLocationRequestStatuses.Pending, dto.Status);
            Assert.Equal("BRI", dto.StoreCode);
            Assert.Equal("Brisbane", dto.StoreName);
            Assert.Equal("staff-user", dto.TargetUserGuid);
            Assert.Equal("manager-user", dto.RequestedByUserGuid);
            Assert.Equal("Mia Manager", dto.RequestedByName);
            Assert.Equal(LocationRequestNow, dto.CreatedAtUtc);
            // 默认 15 分钟有效。
            Assert.Equal(LocationRequestNow.AddMinutes(15), dto.ExpiresAtUtc);
            Assert.Null(dto.LocationLatitude);

            // 审计行：谁请求、请求谁、门店、时间都在表里。
            var row = Assert.Single(await _db.Queryable<AttendanceLocationRequest>().ToListAsync());
            Assert.Equal(dto.RequestGuid, row.RequestGuid);
            Assert.Equal("manager-user", row.RequestedByUserGuid);
            Assert.Equal("staff-user", row.TargetUserGuid);
            Assert.Equal(AttendanceLocationRequestStatuses.Pending, row.Status);
            Assert.Null(row.SampleGuid);

            // 通知：只发给被请求的员工；可见推送（高优先级）；Data 只放标识。
            var notification = Assert.Single(await LocationRequestNotificationsAsync());
            Assert.Equal("attendance.location-request", notification.Type);
            Assert.Equal("Location request", notification.Title);
            Assert.Equal(
                "Mia Manager asked you to share your current location. Open the app within 15 minutes to respond.",
                notification.Body);
            Assert.Equal(AppNotificationPriorities.High, notification.Priority);
            Assert.Equal("BRI", notification.StoreCode);
            Assert.Equal($"attendance:location-request:{dto.RequestGuid}", notification.DedupeKey);
            Assert.Equal(
                ["staff-user"],
                (await _db.Queryable<AppNotificationRecipient>()
                    .Where(item => item.NotificationId == notification.NotificationId)
                    .ToListAsync()).Select(item => item.UserGUID).ToList());
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(notification.DataJson!)!;
            Assert.Equal(["expiresAtUtc", "requestGuid", "storeCode"], data.Keys.OrderBy(key => key, StringComparer.Ordinal));
            Assert.Equal(dto.RequestGuid, data["requestGuid"]);
            Assert.Equal("BRI", data["storeCode"]);
            Assert.Equal(dto.ExpiresAtUtc, DateTime.Parse(data["expiresAtUtc"], null, System.Globalization.DateTimeStyles.RoundtripKind));
            // 铁律：发布只能在业务事务之外。
            Assert.All(_locationRequestPublisherProbe!.Calls, call => Assert.False(call.InTransaction));
        }

        [Fact]
        public async Task CreateLocationRequestAsync_通知在收件箱的保留时长远长于请求有效期_过期后仍可查()
        {
            await SeedLocationRequestScenarioAsync();
            UseLocationRequestNotifications();

            Assert.True((await CreateService("manager-user", "manager", "StoreManager")
                .CreateLocationRequestAsync(LocationCreateDto())).Success);

            var notification = Assert.Single(await LocationRequestNotificationsAsync());
            // 留痕：请求过期后员工仍能在通知收件箱里看到「谁在什么时候请求了位置」。
            Assert.Equal(LocationRequestNow.AddDays(30), notification.ExpiresAtUtc);
            Assert.True(notification.ExpiresAtUtc > LocationRequestNow.Add(AttendanceLocationRequestRules.DefaultValidity));
        }

        [Fact]
        public async Task CreateLocationRequestAsync_管理员不限分店_可请求任意分店已上班的员工()
        {
            await SeedLocationRequestScenarioAsync();
            await InsertBatchPunchesAsync(CreateBatchPunch(
                "other-in", "ClockIn", LocationRequestNow.AddHours(-1), "staff-user", "OTHER"));

            var result = await CreateService("admin-user", "admin", "Admin")
                .CreateLocationRequestAsync(LocationCreateDto(storeCode: "OTHER"));

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal("OTHER", result.Data!.StoreCode);
        }

        [Fact]
        public async Task CreateLocationRequestAsync_店长请求不在自己管辖范围的分店_被拒且不留请求不发通知()
        {
            await SeedLocationRequestScenarioAsync();
            UseLocationRequestNotifications();
            await InsertBatchPunchesAsync(CreateBatchPunch(
                "other-in", "ClockIn", LocationRequestNow.AddHours(-1), "staff-user", "OTHER"));

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .CreateLocationRequestAsync(LocationCreateDto(storeCode: "OTHER"));

            Assert.False(result.Success);
            Assert.Equal("FORBIDDEN_STORE", result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationRequest>().CountAsync());
            Assert.Empty(await LocationRequestNotificationsAsync());
        }

        [Fact]
        public async Task CreateLocationRequestAsync_目标员工当前没有上班_被拒()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationRequestNow);
            UseLocationRequestNotifications();
            // 上班后已下班：最近一条有效打卡是下班卡。
            await InsertBatchPunchesAsync(
                CreateBatchPunch("req-in", "ClockIn", LocationRequestNow.AddHours(-3)),
                CreateBatchPunch("req-out", "ClockOut", LocationRequestNow.AddHours(-1)));

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .CreateLocationRequestAsync(LocationCreateDto());

            Assert.False(result.Success);
            Assert.Equal("ACTIVE_SHIFT_REQUIRED", result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationRequest>().CountAsync());
            Assert.Empty(await LocationRequestNotificationsAsync());
        }

        [Fact]
        public async Task CreateLocationRequestAsync_目标员工从未打卡_同样被拒()
        {
            await SeedStoreScopeAsync();
            _timeProvider.SetUtcNow(LocationRequestNow);

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .CreateLocationRequestAsync(LocationCreateDto());

            Assert.Equal("ACTIVE_SHIFT_REQUIRED", result.ErrorCode);
        }

        [Theory]
        [InlineData(null, "staff-user", "STORE_REQUIRED")]
        [InlineData("  ", "staff-user", "STORE_REQUIRED")]
        [InlineData("BRI", null, "TARGET_USER_REQUIRED")]
        [InlineData("BRI", " ", "TARGET_USER_REQUIRED")]
        [InlineData("BRI", "manager-user", "CANNOT_REQUEST_SELF")]
        public async Task CreateLocationRequestAsync_参数不合法_返回明确错误码(
            string? storeCode, string? targetUserGuid, string expectedCode)
        {
            await SeedLocationRequestScenarioAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .CreateLocationRequestAsync(new CreateAttendanceLocationRequestDto
                {
                    StoreCode = storeCode,
                    TargetUserGuid = targetUserGuid,
                });

            Assert.False(result.Success);
            Assert.Equal(expectedCode, result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationRequest>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationRequestAsync_已有未过期的待响应请求_被拒为PENDING()
        {
            await SeedLocationRequestScenarioAsync();
            UseLocationRequestNotifications();
            var manager = CreateService("manager-user", "manager", "StoreManager");
            Assert.True((await manager.CreateLocationRequestAsync(LocationCreateDto())).Success);

            // 再过 1 分钟：既在限频窗口内，又有未响应请求——更具体的原因是「还在等员工响应」。
            _timeProvider.SetUtcNow(LocationRequestNow.AddMinutes(1));
            var second = await manager.CreateLocationRequestAsync(LocationCreateDto());

            Assert.False(second.Success);
            Assert.Equal("LOCATION_REQUEST_PENDING", second.ErrorCode);
            Assert.Equal(1, await _db.Queryable<AttendanceLocationRequest>().CountAsync());
            Assert.Single(await LocationRequestNotificationsAsync());
        }

        [Fact]
        public async Task CreateLocationRequestAsync_十分钟内只能被请求一次_已响应也算_满十分钟后放行()
        {
            await SeedLocationRequestScenarioAsync();
            // 注意：HttpContextAccessor 的 HttpContext 是静态 AsyncLocal，后创建的服务会覆盖先创建的当前用户，
            // 所以不要持有 manager 实例跨过别的 CreateService 调用——每次调用前现建。
            var first = (await CreateService("manager-user", "manager", "StoreManager")
                .CreateLocationRequestAsync(LocationCreateDto())).Data!;
            _timeProvider.SetUtcNow(LocationRequestNow.AddMinutes(1));
            Assert.True((await CreateService("staff-user", "staff", "StoreStaff")
                .RespondLocationRequestAsync(first.RequestGuid, LocationRespondDto(_timeProvider.GetUtcNow().UtcDateTime))).Success);

            // 创建后第 5 分钟：已响应，没有待响应请求，但仍在 10 分钟限频窗口内，提示剩余 5 分钟。
            _timeProvider.SetUtcNow(LocationRequestNow.AddMinutes(5));
            var limited = await CreateService("manager-user", "manager", "StoreManager")
                .CreateLocationRequestAsync(LocationCreateDto());
            Assert.False(limited.Success);
            Assert.Equal("LOCATION_REQUEST_RATE_LIMITED", limited.ErrorCode);
            Assert.Contains("10 分钟内只能被请求一次", limited.Message);
            Assert.Contains("5 分钟后再试", limited.Message);

            // 满 10 分钟（边界：恰好 10 分钟已不在窗口内）放行。
            _timeProvider.SetUtcNow(LocationRequestNow.AddMinutes(10));
            var allowed = await CreateService("manager-user", "manager", "StoreManager")
                .CreateLocationRequestAsync(LocationCreateDto());
            Assert.True(allowed.Success, $"{allowed.ErrorCode}: {allowed.Message}");
            Assert.Equal(2, await _db.Queryable<AttendanceLocationRequest>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationRequestAsync_上一条请求未响应并已过期_过了限频窗口即可再请求()
        {
            await SeedLocationRequestScenarioAsync();
            var manager = CreateService("manager-user", "manager", "StoreManager");
            Assert.True((await manager.CreateLocationRequestAsync(LocationCreateDto())).Success);

            // 第 16 分钟：上一条（15 分钟有效）已过期、也已出限频窗口。
            _timeProvider.SetUtcNow(LocationRequestNow.AddMinutes(16));
            var second = await manager.CreateLocationRequestAsync(LocationCreateDto());

            Assert.True(second.Success, $"{second.ErrorCode}: {second.Message}");
        }

        [Fact]
        public async Task CreateLocationRequestAsync_限频按员工计_不影响别的员工()
        {
            await SeedLocationRequestScenarioAsync();
            await InsertBatchPunchesAsync(CreateBatchPunch(
                "manager-in", "ClockIn", LocationRequestNow.AddHours(-1), "manager-user", "BRI"));
            var admin = CreateService("admin-user", "admin", "Admin");
            Assert.True((await admin.CreateLocationRequestAsync(LocationCreateDto())).Success);

            var other = await admin.CreateLocationRequestAsync(LocationCreateDto(target: "manager-user"));

            Assert.True(other.Success, $"{other.ErrorCode}: {other.Message}");
        }

        [Fact]
        public async Task CreateLocationRequestAsync_通知发布失败_请求照常创建()
        {
            await SeedLocationRequestScenarioAsync();
            UseLocationRequestNotifications(_ => new ThrowingNotificationPublisher());

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .CreateLocationRequestAsync(LocationCreateDto());

            // 推送不保证送达：失败不能让请求本身失败，员工下次进前台仍会拉到待办。
            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal(1, await _db.Queryable<AttendanceLocationRequest>().CountAsync());
            Assert.Empty(await LocationRequestNotificationsAsync());
        }

        [Fact]
        public async Task CreateLocationRequestAsync_未接通知平台_请求照常创建()
        {
            await SeedLocationRequestScenarioAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .CreateLocationRequestAsync(LocationCreateDto());

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
        }

        // ---------------------------------------------------------------------------------
        // 历史列表
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task GetLocationRequestsAsync_店长只看自己管辖分店_管理员看全部_未响应且过期的显示为Expired()
        {
            await SeedLocationRequestScenarioAsync();
            await SeedLocationRequestRowAsync("bri-old", "BRI", "staff-user", "manager-user", LocationRequestNow.AddHours(-3));
            await SeedLocationRequestRowAsync("other-row", "OTHER", "staff-user", "manager-user", LocationRequestNow.AddHours(-2));
            await SeedLocationRequestRowAsync("bri-new", "BRI", "staff-user", "manager-user", LocationRequestNow.AddMinutes(-1));

            var manager = await CreateService("manager-user", "manager", "StoreManager")
                .GetLocationRequestsAsync(new AttendanceLocationRequestQueryDto());
            var admin = await CreateService("admin-user", "admin", "Admin")
                .GetLocationRequestsAsync(new AttendanceLocationRequestQueryDto());

            Assert.True(manager.Success, manager.Message);
            // 按创建时间倒序；店长看不到 OTHER 店的记录。
            Assert.Equal(["bri-new", "bri-old"], manager.Data!.Select(item => item.RequestGuid).ToList());
            Assert.Equal(["bri-new", "other-row", "bri-old"], admin.Data!.Select(item => item.RequestGuid).ToList());
            // 创建 1 分钟前、15 分钟有效 → 仍是待响应；3 小时前的已过期。
            Assert.Equal(AttendanceLocationRequestStatuses.Pending, manager.Data![0].Status);
            Assert.Equal(AttendanceLocationRequestStatuses.Expired, manager.Data![1].Status);
            Assert.Equal("Brisbane", manager.Data[0].StoreName);
        }

        [Fact]
        public async Task GetLocationRequestsAsync_店长指定不属于自己的分店_被拒()
        {
            await SeedLocationRequestScenarioAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .GetLocationRequestsAsync(new AttendanceLocationRequestQueryDto { StoreCode = "OTHER" });

            Assert.False(result.Success);
            Assert.Equal("FORBIDDEN_STORE", result.ErrorCode);
        }

        [Fact]
        public async Task GetLocationRequestsAsync_可按员工过滤_limit有上下限()
        {
            await SeedLocationRequestScenarioAsync();
            for (var index = 0; index < 3; index++)
            {
                await SeedLocationRequestRowAsync(
                    $"staff-{index}", "BRI", "staff-user", "manager-user", LocationRequestNow.AddHours(-10 + index));
            }

            await SeedLocationRequestRowAsync("mgr-row", "BRI", "manager-user", "admin-user", LocationRequestNow.AddHours(-5));
            var manager = CreateService("manager-user", "manager", "StoreManager");

            var byTarget = await manager.GetLocationRequestsAsync(new AttendanceLocationRequestQueryDto
            {
                TargetUserGuid = "staff-user",
                Limit = 2,
            });
            var floored = await manager.GetLocationRequestsAsync(new AttendanceLocationRequestQueryDto { Limit = 0 });

            Assert.Equal(["staff-2", "staff-1"], byTarget.Data!.Select(item => item.RequestGuid).ToList());
            Assert.Single(floored.Data!); // limit 下限 1
        }

        // ---------------------------------------------------------------------------------
        // 员工待办
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task GetMyPendingLocationRequestsAsync_只返回自己的_未过期_仍在班中的待响应请求()
        {
            await SeedLocationRequestScenarioAsync();
            await SeedLocationRequestRowAsync("mine-live", "BRI", "staff-user", "manager-user", LocationRequestNow.AddMinutes(-3));
            await SeedLocationRequestRowAsync("mine-expired", "BRI", "staff-user", "manager-user", LocationRequestNow.AddMinutes(-40));
            await SeedLocationRequestRowAsync("mine-responded", "BRI", "staff-user", "manager-user", LocationRequestNow.AddMinutes(-2), status: "Responded");
            await SeedLocationRequestRowAsync("others", "BRI", "manager-user", "admin-user", LocationRequestNow.AddMinutes(-1));

            var result = await CreateService("staff-user", "staff", "StoreStaff").GetMyPendingLocationRequestsAsync();

            Assert.True(result.Success, result.Message);
            var item = Assert.Single(result.Data!);
            Assert.Equal("mine-live", item.RequestGuid);
            Assert.Equal(AttendanceLocationRequestStatuses.Pending, item.Status);
            Assert.Equal(LocationRequestNow.AddMinutes(-3).AddMinutes(15), item.ExpiresAtUtc);
        }

        [Fact]
        public async Task GetMyPendingLocationRequestsAsync_员工下班后不再有待办()
        {
            await SeedLocationRequestScenarioAsync();
            await SeedLocationRequestRowAsync("mine-live", "BRI", "staff-user", "manager-user", LocationRequestNow.AddMinutes(-3));
            var staff = CreateService("staff-user", "staff", "StoreStaff");
            Assert.Single((await staff.GetMyPendingLocationRequestsAsync()).Data!);

            await InsertBatchPunchesAsync(CreateBatchPunch("req-out", "ClockOut", LocationRequestNow.AddMinutes(-1)));

            Assert.Empty((await staff.GetMyPendingLocationRequestsAsync()).Data!);
        }

        // ---------------------------------------------------------------------------------
        // 员工响应
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task RespondLocationRequestAsync_在班中且未过期_写入班中样本并与请求关联()
        {
            await SeedLocationRequestScenarioAsync();
            var request = await CreateRequestByManagerAsync();
            _timeProvider.SetUtcNow(LocationRequestNow.AddMinutes(2));
            var capturedAt = LocationRequestNow.AddMinutes(2).AddSeconds(-3);

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .RespondLocationRequestAsync(request.RequestGuid, LocationRespondDto(capturedAt));

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            var dto = result.Data!;
            Assert.Equal(AttendanceLocationRequestStatuses.Responded, dto.Status);
            Assert.False(dto.AlreadyResponded);
            Assert.Equal(LocationRequestNow.AddMinutes(2), dto.RespondedAtUtc);
            Assert.Equal(-27.4705, dto.LocationLatitude);
            Assert.Equal(153.0260, dto.LocationLongitude);
            Assert.Equal(9.5, dto.LocationAccuracy);

            // 样本：写入班中样本表（Web「班中定位样本」抽屉可见），ClientSampleId = RequestGuid，事件类型 ServerRequest。
            var sample = Assert.Single(await _db.Queryable<AttendanceLocationSample>().ToListAsync());
            Assert.Equal("staff-user", sample.UserGuid);
            Assert.Equal("BRI", sample.StoreCode);
            Assert.Equal("ServerRequest", sample.EventType);
            Assert.Equal(request.RequestGuid, sample.ClientSampleId);
            Assert.Equal(capturedAt, sample.LocationCapturedAtUtc);
            Assert.Equal("granted", sample.LocationPermissionStatus);
            Assert.Equal("hbmobile-test-device", sample.HardwareId);
            Assert.Equal(dto.SampleGuid, sample.SampleGuid);

            var row = await _db.Queryable<AttendanceLocationRequest>().SingleAsync(item => item.RequestGuid == request.RequestGuid);
            Assert.Equal(AttendanceLocationRequestStatuses.Responded, row.Status);
            Assert.Equal(sample.SampleGuid, row.SampleGuid);
            Assert.Equal(LocationRequestNow.AddMinutes(2), row.RespondedAtUtc);

            // 响应之后不再算待办。
            Assert.Empty((await CreateService("staff-user", "staff", "StoreStaff").GetMyPendingLocationRequestsAsync()).Data!);

            // 店长在班中定位样本查询里能看到这个点（Web 抽屉用的就是这个接口）。
            var samples = await CreateService("manager-user", "manager", "StoreManager")
                .GetLocationSamplesAsync(new AttendanceLocationSampleQueryDto
                {
                    StoreCode = "BRI",
                    UserGuid = "staff-user",
                    FromDate = new DateTime(2026, 5, 18),
                    ToDate = new DateTime(2026, 5, 18),
                });
            Assert.Equal("ServerRequest", Assert.Single(samples.Data!).EventType);
        }

        [Fact]
        public async Task RespondLocationRequestAsync_重复响应_幂等_不再写样本()
        {
            await SeedLocationRequestScenarioAsync();
            var request = await CreateRequestByManagerAsync();
            _timeProvider.SetUtcNow(LocationRequestNow.AddMinutes(1));
            var staff = CreateService("staff-user", "staff", "StoreStaff");
            var respond = LocationRespondDto(LocationRequestNow.AddMinutes(1));
            var first = await staff.RespondLocationRequestAsync(request.RequestGuid, respond);

            // 客户端没收到响应，原样重试：直接返回结果，标记 AlreadyResponded，样本仍只有一条。
            _timeProvider.SetUtcNow(LocationRequestNow.AddMinutes(2));
            var retry = await staff.RespondLocationRequestAsync(request.RequestGuid, respond);

            Assert.True(retry.Success, $"{retry.ErrorCode}: {retry.Message}");
            Assert.True(retry.Data!.AlreadyResponded);
            Assert.Equal(first.Data!.SampleGuid, retry.Data.SampleGuid);
            Assert.Equal(first.Data.RespondedAtUtc, retry.Data.RespondedAtUtc);
            Assert.Equal(1, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task RespondLocationRequestAsync_响应里程序崩溃只落了样本没翻状态_重试复用已有样本()
        {
            await SeedLocationRequestScenarioAsync();
            var request = await CreateRequestByManagerAsync();
            _timeProvider.SetUtcNow(LocationRequestNow.AddMinutes(1));
            // 模拟上次响应写了样本（ClientSampleId = RequestGuid）、但请求还停在 Pending。
            await _db.Insertable(new AttendanceLocationSample
            {
                SampleGuid = "half-written-sample",
                UserGuid = "staff-user",
                StoreCode = "BRI",
                EventType = "ServerRequest",
                LocationLatitude = -27.4705,
                LocationLongitude = 153.0260,
                LocationCapturedAtUtc = LocationRequestNow.AddMinutes(1),
                ClientSampleId = request.RequestGuid,
                CreatedAt = LocationRequestNow.AddMinutes(1),
            }).ExecuteCommandAsync();

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .RespondLocationRequestAsync(request.RequestGuid, LocationRespondDto(LocationRequestNow.AddMinutes(1)));

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal("half-written-sample", result.Data!.SampleGuid);
            Assert.Equal(1, await _db.Queryable<AttendanceLocationSample>().CountAsync());
            Assert.Equal(
                AttendanceLocationRequestStatuses.Responded,
                (await _db.Queryable<AttendanceLocationRequest>().SingleAsync(item => item.RequestGuid == request.RequestGuid)).Status);
        }

        [Fact]
        public async Task RespondLocationRequestAsync_请求已过期_被拒且不写样本()
        {
            await SeedLocationRequestScenarioAsync();
            var request = await CreateRequestByManagerAsync();
            // 恰好到失效时间即视为过期。
            _timeProvider.SetUtcNow(request.ExpiresAtUtc);

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .RespondLocationRequestAsync(request.RequestGuid, LocationRespondDto(request.ExpiresAtUtc));

            Assert.False(result.Success);
            Assert.Equal("LOCATION_REQUEST_EXPIRED", result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
            Assert.Equal(
                AttendanceLocationRequestStatuses.Pending,
                (await _db.Queryable<AttendanceLocationRequest>().SingleAsync(item => item.RequestGuid == request.RequestGuid)).Status);
        }

        [Fact]
        public async Task RespondLocationRequestAsync_响应到达时员工已下班_不再收_请求作废并记原因()
        {
            await SeedLocationRequestScenarioAsync();
            var request = await CreateRequestByManagerAsync();
            _timeProvider.SetUtcNow(LocationRequestNow.AddMinutes(3));
            await InsertBatchPunchesAsync(CreateBatchPunch("req-out", "ClockOut", LocationRequestNow.AddMinutes(2)));
            var staff = CreateService("staff-user", "staff", "StoreStaff");

            var result = await staff.RespondLocationRequestAsync(request.RequestGuid, LocationRespondDto(LocationRequestNow.AddMinutes(3)));

            Assert.False(result.Success);
            Assert.Equal("ACTIVE_SHIFT_REQUIRED", result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
            var row = await _db.Queryable<AttendanceLocationRequest>().SingleAsync(item => item.RequestGuid == request.RequestGuid);
            Assert.Equal(AttendanceLocationRequestStatuses.Cancelled, row.Status);
            Assert.Equal("ClockedOut", row.StatusReason);

            // 作废后再响应仍被拒，不会因为员工又上班就复活。
            await InsertBatchPunchesAsync(CreateBatchPunch("req-in-2", "ClockIn", LocationRequestNow.AddMinutes(3)));
            var again = await staff.RespondLocationRequestAsync(request.RequestGuid, LocationRespondDto(LocationRequestNow.AddMinutes(3)));
            Assert.Equal("LOCATION_REQUEST_CANCELLED", again.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());

            // Web 历史里显示为已取消 + 已下班。
            var listed = (await CreateService("manager-user", "manager", "StoreManager")
                .GetLocationRequestsAsync(new AttendanceLocationRequestQueryDto())).Data!.Single();
            Assert.Equal("Cancelled", listed.Status);
            Assert.Equal("ClockedOut", listed.StatusReason);
        }

        [Fact]
        public async Task RespondLocationRequestAsync_定位采集时间早于请求创建_被拒为LOCATION_STALE_容忍30秒()
        {
            await SeedLocationRequestScenarioAsync();
            var request = await CreateRequestByManagerAsync();
            _timeProvider.SetUtcNow(LocationRequestNow.AddMinutes(1));
            var staff = CreateService("staff-user", "staff", "StoreStaff");

            var stale = await staff.RespondLocationRequestAsync(
                request.RequestGuid, LocationRespondDto(LocationRequestNow.AddSeconds(-31)));
            Assert.Equal("LOCATION_STALE", stale.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());

            // 恰好在容忍边界内（请求创建前 30 秒）放行：设备时钟与服务器的轻微偏差。
            var withinTolerance = await staff.RespondLocationRequestAsync(
                request.RequestGuid, LocationRespondDto(LocationRequestNow.AddSeconds(-30)));
            Assert.True(withinTolerance.Success, $"{withinTolerance.ErrorCode}: {withinTolerance.Message}");
        }

        [Fact]
        public async Task RespondLocationRequestAsync_定位采集时间晚于服务器时间超过5分钟_被拒()
        {
            await SeedLocationRequestScenarioAsync();
            var request = await CreateRequestByManagerAsync();
            _timeProvider.SetUtcNow(LocationRequestNow.AddMinutes(1));

            var result = await CreateService("staff-user", "staff", "StoreStaff").RespondLocationRequestAsync(
                request.RequestGuid, LocationRespondDto(LocationRequestNow.AddMinutes(1).AddMinutes(5).AddSeconds(1)));

            Assert.Equal("SAMPLE_IN_FUTURE", result.ErrorCode);
        }

        [Theory]
        [InlineData("denied")]
        [InlineData(null)]
        public async Task RespondLocationRequestAsync_没有定位权限_被拒为LOCATION_REQUIRED(string? permissionStatus)
        {
            await SeedLocationRequestScenarioAsync();
            var request = await CreateRequestByManagerAsync();
            _timeProvider.SetUtcNow(LocationRequestNow.AddMinutes(1));

            var result = await CreateService("staff-user", "staff", "StoreStaff").RespondLocationRequestAsync(
                request.RequestGuid, LocationRespondDto(LocationRequestNow.AddMinutes(1), permissionStatus));

            Assert.Equal("LOCATION_REQUIRED", result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task RespondLocationRequestAsync_缺坐标_被拒为LOCATION_REQUIRED()
        {
            await SeedLocationRequestScenarioAsync();
            var request = await CreateRequestByManagerAsync();
            _timeProvider.SetUtcNow(LocationRequestNow.AddMinutes(1));
            var respond = LocationRespondDto(LocationRequestNow.AddMinutes(1));
            respond.LocationLatitude = null;

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .RespondLocationRequestAsync(request.RequestGuid, respond);

            Assert.Equal("LOCATION_REQUIRED", result.ErrorCode);
        }

        [Fact]
        public async Task RespondLocationRequestAsync_别人的请求或不存在的请求_都返回NOT_FOUND_不泄露存在性()
        {
            await SeedLocationRequestScenarioAsync();
            var request = await CreateRequestByManagerAsync();
            _timeProvider.SetUtcNow(LocationRequestNow.AddMinutes(1));
            var respond = LocationRespondDto(LocationRequestNow.AddMinutes(1));

            // 发给 staff-user 的请求，manager-user 不能代为响应。
            var notMine = await CreateService("manager-user", "manager", "StoreManager")
                .RespondLocationRequestAsync(request.RequestGuid, respond);
            var missing = await CreateService("staff-user", "staff", "StoreStaff")
                .RespondLocationRequestAsync("no-such-request", respond);
            var blank = await CreateService("staff-user", "staff", "StoreStaff")
                .RespondLocationRequestAsync("  ", respond);

            Assert.Equal("LOCATION_REQUEST_NOT_FOUND", notMine.ErrorCode);
            Assert.Equal("LOCATION_REQUEST_NOT_FOUND", missing.ErrorCode);
            Assert.Equal("LOCATION_REQUEST_NOT_FOUND", blank.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        // ---------------------------------------------------------------------------------
        // 授权元数据
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData(nameof(ReactAttendanceController.CreateLocationRequest), typeof(HttpPostAttribute), "location-requests", Permissions.Attendance.Punch.RequestLocation)]
        [InlineData(nameof(ReactAttendanceController.GetLocationRequests), typeof(HttpGetAttribute), "location-requests", Permissions.Attendance.Punch.RequestLocation)]
        [InlineData(nameof(ReactAttendanceController.GetMyPendingLocationRequests), typeof(HttpGetAttribute), "my/location-requests/pending", Permissions.Attendance.Punch.Self)]
        [InlineData(nameof(ReactAttendanceController.RespondLocationRequest), typeof(HttpPostAttribute), "my/location-requests/{requestGuid}/respond", Permissions.Attendance.Punch.Self)]
        public void LocationRequestEndpoints_路由与权限策略符合设计(
            string methodName, Type httpAttribute, string template, string policy)
        {
            var method = typeof(ReactAttendanceController).GetMethod(methodName)!;

            var http = (HttpMethodAttribute)method.GetCustomAttributes(httpAttribute, inherit: false).Single();
            Assert.Equal(template, http.Template);
            Assert.Equal(policy, method.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        }

        // ---------------------------------------------------------------------------------
        // 造数与辅助
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// 通用场景：BRI 店（店长 manager-user、员工 staff-user）与 OTHER 店；固定服务器时间；staff-user 在 BRI 店已上班 2 小时；
        /// 管理员账号 admin-user（用户表里有一行，供姓名解析）。
        /// </summary>
        private async Task SeedLocationRequestScenarioAsync()
        {
            await SeedStoreScopeAsync();
            await _db.Insertable(new User
            {
                UserGUID = "admin-user",
                Username = "admin",
                Email = "admin@example.com",
                PasswordHash = "hash",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(LocationRequestNow);
            await InsertBatchPunchesAsync(CreateBatchPunch("req-in", "ClockIn", LocationRequestNow.AddHours(-2)));
        }

        private async Task<AttendanceLocationRequestDto> CreateRequestByManagerAsync()
        {
            var created = await CreateService("manager-user", "manager", "StoreManager")
                .CreateLocationRequestAsync(LocationCreateDto());
            Assert.True(created.Success, $"{created.ErrorCode}: {created.Message}");
            return created.Data!;
        }

        private static CreateAttendanceLocationRequestDto LocationCreateDto(
            string storeCode = "BRI", string target = "staff-user") =>
            new() { StoreCode = storeCode, TargetUserGuid = target };

        private static RespondAttendanceLocationRequestDto LocationRespondDto(
            DateTime capturedAtUtc, string? permission = "granted") =>
            new()
            {
                HardwareId = "hbmobile-test-device",
                SystemDeviceNumber = "SYS-001",
                DeviceSystem = "iOS",
                LocationLatitude = -27.4705,
                LocationLongitude = 153.0260,
                LocationAccuracy = 9.5,
                IsMocked = false,
                LocationPermissionStatus = permission,
                LocationCapturedAtUtc = DateTime.SpecifyKind(capturedAtUtc, DateTimeKind.Utc),
            };

        /// <summary>直接造一行请求（绕过创建校验），用于历史 / 待办用例。</summary>
        private async Task SeedLocationRequestRowAsync(
            string requestGuid,
            string storeCode,
            string targetUserGuid,
            string requestedByUserGuid,
            DateTime createdAtUtc,
            string status = "Pending")
        {
            await _db.Insertable(new AttendanceLocationRequest
            {
                RequestGuid = requestGuid,
                StoreCode = storeCode,
                TargetUserGuid = targetUserGuid,
                RequestedByUserGuid = requestedByUserGuid,
                Status = status,
                ExpiresAtUtc = createdAtUtc.Add(AttendanceLocationRequestRules.DefaultValidity),
                CreatedAt = createdAtUtc,
            }).ExecuteCommandAsync();
        }

        private void UseLocationRequestNotifications(Func<INotificationPublisher, INotificationPublisher>? decoratePublisher = null)
        {
            var context = CreateSqlSugarContext(_db);
            INotificationPublisher publisher = new NotificationPublisher(
                context,
                // 位置请求只发给指定用户（ForUsers），解析器用不到角色服务。
                new NotificationRecipientResolver(
                    context,
                    new Mock<IRoleService>().Object,
                    NullLogger<NotificationRecipientResolver>.Instance),
                new NoopRealtimeDispatcher(),
                _timeProvider,
                NullLogger<NotificationPublisher>.Instance);
            if (decoratePublisher != null)
            {
                publisher = decoratePublisher(publisher);
            }

            _locationRequestPublisherProbe = new ApprovalPublisherProbe(publisher, _db);
            _locationRequestNotifier = new AttendanceLocationRequestNotifier(
                context,
                _locationRequestPublisherProbe,
                NullLogger<AttendanceLocationRequestNotifier>.Instance);
        }

        private async Task<List<AppNotification>> LocationRequestNotificationsAsync() =>
            await _db.Queryable<AppNotification>()
                .Where(item => item.Type == AppNotificationTypes.AttendanceLocationRequest)
                .ToListAsync();
    }

    /// <summary>位置请求口径的纯函数：有效状态推导、限频提示、通知文案与 Data。</summary>
    public sealed class AttendanceLocationRequestRulesTests
    {
        private static readonly DateTime Now = new(2026, 5, 18, 4, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void 口径默认值_有效期15分钟_限频10分钟_收件箱留痕远长于有效期()
        {
            Assert.Equal(TimeSpan.FromMinutes(15), AttendanceLocationRequestRules.DefaultValidity);
            Assert.Equal(TimeSpan.FromMinutes(10), AttendanceLocationRequestRules.MinInterval);
            Assert.True(AttendanceLocationRequestRules.NotificationRetention > AttendanceLocationRequestRules.DefaultValidity);
            Assert.Equal("ServerRequest", AttendanceLocationRequestRules.SampleEventType);
        }

        [Theory]
        [InlineData("Pending", 1, "Pending")]
        [InlineData("Pending", 0, "Expired")] // 恰好到失效时间即过期
        [InlineData("Pending", -1, "Expired")]
        [InlineData("Responded", -60, "Responded")] // 已响应的不会变成过期
        [InlineData("Cancelled", -60, "Cancelled")]
        public void ResolveEffectiveStatus_只有未响应且过了失效时间的才显示为Expired(
            string stored, int expiresInSeconds, string expected)
        {
            Assert.Equal(
                expected,
                AttendanceLocationRequestRules.ResolveEffectiveStatus(stored, Now.AddSeconds(expiresInSeconds), Now));
        }

        [Theory]
        [InlineData(0, 10)]
        [InlineData(5, 5)]
        [InlineData(9, 1)]
        [InlineData(10, 1)] // 已满窗口：至少提示 1 分钟，不出现 0 或负数
        [InlineData(300, 1)]
        public void RemainingWholeMinutes_向上取整且至少1分钟(int elapsedMinutes, int expected)
        {
            Assert.Equal(
                expected,
                AttendanceLocationRequestRules.RemainingWholeMinutes(Now.AddMinutes(-elapsedMinutes), Now));
        }

        [Fact]
        public void 通知正文_英文_含请求人姓名与时限_不含坐标_Data只含标识()
        {
            var body = AttendanceLocationRequestNotifier.BuildBody("Mia Manager", TimeSpan.FromMinutes(15));
            Assert.Equal(
                "Mia Manager asked you to share your current location. Open the app within 15 minutes to respond.",
                body);

            var data = AttendanceLocationRequestNotifier.BuildData(new AttendanceLocationRequest
            {
                RequestGuid = "req-1",
                StoreCode = "BRI",
                ExpiresAtUtc = Now,
            });
            Assert.Equal(["expiresAtUtc", "requestGuid", "storeCode"], data.Keys.OrderBy(key => key, StringComparer.Ordinal));
            Assert.Equal("2026-05-18T04:00:00.0000000Z", data["expiresAtUtc"]);
        }

        [Fact]
        public void 通知类型常量_与移动端路由约定一致()
        {
            Assert.Equal("attendance.location-request", AppNotificationTypes.AttendanceLocationRequest);
            Assert.Equal("attendance:location-request:abc", AttendanceLocationRequestRules.DedupeKey("abc"));
        }
    }
}
