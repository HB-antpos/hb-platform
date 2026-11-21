using System;
using System.Linq;
using System.Threading.Tasks;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// 打卡设备 ID 与「共用设备」标记：打卡请求带的设备 ID 要落库（去空白、截到列宽），
    /// 店长看打卡列表时同一台手机登录过多个账号的打卡带标记；标记查询出错不能拖垮打卡列表。
    /// 夹具与造数方法共用 AttendanceReactServiceTests。
    /// </summary>
    public sealed partial class AttendanceReactServiceTests
    {
        private const string SharedPhone = "hbmobile-shared-phone";

        [Fact]
        public async Task PunchAsync_StoresTrimmedDeviceIdFromRequest()
        {
            await SeedStoreScopeAsync();
            var issuedAt = TruncateToMilliseconds(DateTime.UtcNow);
            var request = CreateQrPunchRequest(issuedAt, Guid.NewGuid());
            request.DeviceId = "  hbmobile-abc-123  ";
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.PunchAsync(request);

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal("hbmobile-abc-123", result.Data!.DeviceId);
            var stored = await _db.Queryable<AttendancePunch>()
                .FirstAsync(item => item.PunchGuid == result.Data.PunchGuid);
            Assert.Equal("hbmobile-abc-123", stored.DeviceId);
        }

        [Fact]
        public async Task PunchAsync_WhenDeviceIdIsBlank_StoresNull()
        {
            await SeedStoreScopeAsync();
            var request = CreateQrPunchRequest(TruncateToMilliseconds(DateTime.UtcNow), Guid.NewGuid());
            request.DeviceId = "   ";
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.PunchAsync(request);

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Null(result.Data!.DeviceId);
        }

        [Fact]
        public async Task PunchAsync_WhenDeviceIdIsTooLong_TruncatesToColumnWidthInsteadOfFailingThePunch()
        {
            await SeedStoreScopeAsync();
            var request = CreateQrPunchRequest(TruncateToMilliseconds(DateTime.UtcNow), Guid.NewGuid());
            // 列宽 100：超长值不能让整笔打卡因为「字符串被截断」落库失败。
            request.DeviceId = new string('x', 150);
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.PunchAsync(request);

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal(100, result.Data!.DeviceId!.Length);
        }

        [Fact]
        public async Task GetPunchesAsync_FlagsPunchesFromDeviceUsedByMultipleAccounts()
        {
            await SeedStoreScopeAsync();
            await SeedDeviceFlagLoginsAsync();
            var shared = CreateStoredPunchForSchedule(
                "shared-in", null!, "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0));
            shared.DeviceId = SharedPhone;
            var own = CreateStoredPunchForSchedule(
                "own-in", null!, "BRI", "manager-user", "ClockIn", new DateTime(2026, 5, 18, 9, 5, 0));
            own.DeviceId = "hbmobile-own-phone";
            var legacy = CreateStoredPunchForSchedule(
                "legacy-in", null!, "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 19, 9, 0, 0));
            await _db.Insertable(new[] { shared, own, legacy }).ExecuteCommandAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .GetPunchesAsync(new AttendancePunchQueryDto { StoreCode = "BRI" });

            Assert.True(result.Success, result.Message);
            var byGuid = result.Data!.ToDictionary(item => item.PunchGuid);
            Assert.Equal(2, byGuid["shared-in"].SharedDeviceAccountCount);
            Assert.Null(byGuid["own-in"].SharedDeviceAccountCount);
            // 旧版 App 打的卡没有设备 ID：证据不足，不标记。
            Assert.Null(byGuid["legacy-in"].SharedDeviceAccountCount);
        }

        [Fact]
        public async Task GetPunchesAsync_WhenLoginEvidenceQueryFails_StillReturnsPunchesWithoutFlag()
        {
            await SeedStoreScopeAsync();
            var shared = CreateStoredPunchForSchedule(
                "shared-in", null!, "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0));
            shared.DeviceId = SharedPhone;
            await _db.Insertable(shared).ExecuteCommandAsync();
            // 模拟登录审计表不可用：标记只是辅助线索，不能让店长连打卡列表都看不了。
            _db.Ado.ExecuteCommand("DROP TABLE UserLoginDeviceRecord");

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .GetPunchesAsync(new AttendancePunchQueryDto { StoreCode = "BRI" });

            Assert.True(result.Success, result.Message);
            var punch = Assert.Single(result.Data!);
            Assert.Equal("shared-in", punch.PunchGuid);
            Assert.Null(punch.SharedDeviceAccountCount);
        }

        [Fact]
        public async Task GetMyTodayAsync_DoesNotExposeSharedDeviceFlagToTheEmployee()
        {
            // 标记是给店长复核用的线索，员工自己的视图不能带，否则等于提示「系统在盯着共用手机」。
            await SeedStoreScopeAsync();
            await SeedDeviceFlagLoginsAsync();
            var shared = CreateStoredPunchForSchedule(
                "shared-in", null!, "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0));
            shared.DeviceId = SharedPhone;
            await _db.Insertable(shared).ExecuteCommandAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");

            Assert.True(result.Success, result.Message);
            // 先确认今日视图里确实有这条带设备 ID 的打卡，避免对空集合的断言空转。
            Assert.Contains(result.Data!.Punches, punch => punch.PunchGuid == "shared-in");
            Assert.All(result.Data.Punches, punch => Assert.Null(punch.SharedDeviceAccountCount));
        }

        private async Task SeedDeviceFlagLoginsAsync()
        {
            // staff-user 与 manager-user 先后在同一台手机上登录过（打卡日 2026-05-18 之前）。
            await _db.Insertable(new[]
            {
                new UserLoginDeviceRecord
                {
                    HardwareId = SharedPhone,
                    UserGuid = "manager-user",
                    LoginSource = "AppLogin",
                    LoginAtUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                    CreatedAt = DateTime.UtcNow,
                },
                new UserLoginDeviceRecord
                {
                    HardwareId = SharedPhone,
                    UserGuid = "staff-user",
                    LoginSource = "AppLogin",
                    LoginAtUtc = new DateTime(2026, 5, 17, 20, 0, 0, DateTimeKind.Utc),
                    CreatedAt = DateTime.UtcNow,
                },
                new UserLoginDeviceRecord
                {
                    HardwareId = "hbmobile-own-phone",
                    UserGuid = "manager-user",
                    LoginSource = "AppLogin",
                    LoginAtUtc = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
                    CreatedAt = DateTime.UtcNow,
                },
            }).ExecuteCommandAsync();
        }
    }
}
