using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using SqlSugar;
using Xunit;
using Evidence = BlazorApp.Api.Services.Attendance.AttendancePunchDeviceFlagEvaluator.DeviceLoginEvidence;

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// 打卡「共用设备」标记：同一台手机在打卡前 90 天内登录过 2 个及以上账号才标记；
    /// 门店登记设备（设备登录）、没有设备 ID、证据不足都不标记。
    /// </summary>
    public sealed class AttendancePunchDeviceFlagEvaluatorTests : IDisposable
    {
        private static readonly DateTime PunchAt = new(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc);
        private const string Device = "hbmobile-aaa-1111";

        private readonly string _dbPath;
        private readonly SqliteConnection _connection;
        private readonly SqlSugarClient _db;

        public AttendancePunchDeviceFlagEvaluatorTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();
            _db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _connection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            });
            _db.CodeFirst.InitTables(typeof(UserLoginDeviceRecord));
        }

        public void Dispose()
        {
            _db.Dispose();
            _connection.Dispose();
            if (File.Exists(_dbPath))
            {
                SqliteTempFileCleanup.DeleteIfExists(_dbPath);
            }
        }

        private static Evidence Login(
            string? userGuid,
            TimeSpan beforePunch,
            string source = "AppLogin",
            string hardwareId = Device) =>
            new(hardwareId, userGuid, source, PunchAt - beforePunch);

        [Fact]
        public void CountSharedAccounts_WhenAnotherAccountLoggedInOnSameDevice_ReturnsAccountCount()
        {
            var logins = new[]
            {
                Login("user-a", TimeSpan.FromDays(10)),
                Login("user-b", TimeSpan.FromMinutes(3)),
            };

            var count = AttendancePunchDeviceFlagEvaluator.CountSharedAccounts("user-b", Device, PunchAt, logins);

            Assert.Equal(2, count);
        }

        [Fact]
        public void CountSharedAccounts_WhenThreeAccountsSeen_CountsAll()
        {
            var logins = new[]
            {
                Login("user-a", TimeSpan.FromDays(20)),
                Login("user-b", TimeSpan.FromDays(5)),
                Login("user-c", TimeSpan.FromMinutes(2)),
            };

            Assert.Equal(3, AttendancePunchDeviceFlagEvaluator.CountSharedAccounts("user-c", Device, PunchAt, logins));
        }

        [Fact]
        public void CountSharedAccounts_WhenOnlyPunchUserLoggedIn_ReturnsNull()
        {
            var logins = new[]
            {
                Login("user-b", TimeSpan.FromDays(30)),
                Login("user-b", TimeSpan.FromMinutes(3)),
            };

            Assert.Null(AttendancePunchDeviceFlagEvaluator.CountSharedAccounts("user-b", Device, PunchAt, logins));
        }

        [Fact]
        public void CountSharedAccounts_WhenPunchUserHasNoLoginRecordButOtherAccountDoes_StillFlags()
        {
            // 打卡人是在旧版 App 登录的（登录没带设备 ID），但这台手机上出现过别人的账号：仍要标记。
            var logins = new[] { Login("user-a", TimeSpan.FromDays(3)) };

            Assert.Equal(2, AttendancePunchDeviceFlagEvaluator.CountSharedAccounts("user-b", Device, PunchAt, logins));
        }

        [Fact]
        public void CountSharedAccounts_WhenOtherLoginIsOlderThanLookBack_IsIgnored()
        {
            var logins = new[] { Login("user-a", TimeSpan.FromDays(91)) };

            Assert.Null(AttendancePunchDeviceFlagEvaluator.CountSharedAccounts("user-b", Device, PunchAt, logins));
        }

        [Fact]
        public void CountSharedAccounts_WhenOtherLoginIsJustInsideLookBack_IsCounted()
        {
            var logins = new[] { Login("user-a", TimeSpan.FromDays(89)) };

            Assert.Equal(2, AttendancePunchDeviceFlagEvaluator.CountSharedAccounts("user-b", Device, PunchAt, logins));
        }

        [Theory]
        [InlineData(3, true)]
        [InlineData(10, false)]
        public void CountSharedAccounts_LoginAfterPunchOnlyCountsWithinClockSkew(int minutesAfter, bool counted)
        {
            // 「之后」的登录：容差内算（时钟误差），超过容差说明是打卡之后才发生的事，不能回头给这次打卡定性。
            var logins = new[] { Login("user-a", TimeSpan.FromMinutes(-minutesAfter)) };

            var count = AttendancePunchDeviceFlagEvaluator.CountSharedAccounts("user-b", Device, PunchAt, logins);

            Assert.Equal(counted ? 2 : (int?)null, count);
        }

        [Fact]
        public void CountSharedAccounts_WhenDeviceIsStoreRegistered_NeverFlags()
        {
            // 设备登录记录很久以前也算（不受 90 天窗口限制）：门店登记设备本来就多人共用。
            var logins = new[]
            {
                Login(null, TimeSpan.FromDays(200), "DeviceLogin"),
                Login("user-a", TimeSpan.FromDays(10)),
                Login("user-b", TimeSpan.FromMinutes(3)),
            };

            Assert.Null(AttendancePunchDeviceFlagEvaluator.CountSharedAccounts("user-b", Device, PunchAt, logins));
        }

        [Fact]
        public void CountSharedAccounts_IgnoresOtherDevicesAndComparesHardwareIdCaseInsensitively()
        {
            var logins = new[]
            {
                Login("user-a", TimeSpan.FromDays(3), hardwareId: "hbmobile-other-2222"),
                Login("user-c", TimeSpan.FromDays(3), hardwareId: Device.ToUpperInvariant()),
            };

            Assert.Equal(2, AttendancePunchDeviceFlagEvaluator.CountSharedAccounts("user-b", Device, PunchAt, logins));
        }

        [Theory]
        [InlineData(null, "user-b")]
        [InlineData("", "user-b")]
        [InlineData("   ", "user-b")]
        [InlineData(Device, null)]
        [InlineData(Device, "")]
        public void CountSharedAccounts_WithoutDeviceIdOrUser_ReturnsNull(string? deviceId, string? userGuid)
        {
            var logins = new[] { Login("user-a", TimeSpan.FromDays(3)) };

            Assert.Null(AttendancePunchDeviceFlagEvaluator.CountSharedAccounts(userGuid, deviceId, PunchAt, logins));
        }

        [Fact]
        public async Task PopulateAsync_FlagsOnlyPunchesFromSharedDevices()
        {
            await InsertLoginAsync(Device, "user-a", PunchAt.AddDays(-10));
            await InsertLoginAsync(Device, "user-b", PunchAt.AddMinutes(-3));
            await InsertLoginAsync("hbmobile-solo-3333", "user-c", PunchAt.AddDays(-2));
            // 已软删除的登录记录不算证据。
            await InsertLoginAsync("hbmobile-solo-3333", "user-d", PunchAt.AddDays(-1), isDeleted: true);
            var shared = Punch("user-b", Device);
            var solo = Punch("user-c", "hbmobile-solo-3333");
            var noDevice = Punch("user-b", null);
            var unknownDevice = Punch("user-b", "hbmobile-never-seen");

            await AttendancePunchDeviceFlagEvaluator.PopulateAsync(
                _db,
                new[] { shared, solo, noDevice, unknownDevice });

            Assert.Equal(2, shared.SharedDeviceAccountCount);
            Assert.Null(solo.SharedDeviceAccountCount);
            Assert.Null(noDevice.SharedDeviceAccountCount);
            Assert.Null(unknownDevice.SharedDeviceAccountCount);
        }

        [Fact]
        public async Task PopulateAsync_StoreRegisteredDeviceLoginOlderThanWindow_StillExemptsDevice()
        {
            await InsertLoginAsync(Device, null, PunchAt.AddDays(-200), source: "DeviceLogin");
            await InsertLoginAsync(Device, "user-a", PunchAt.AddDays(-10));
            await InsertLoginAsync(Device, "user-b", PunchAt.AddMinutes(-3));
            var punch = Punch("user-b", Device);

            await AttendancePunchDeviceFlagEvaluator.PopulateAsync(_db, new[] { punch });

            Assert.Null(punch.SharedDeviceAccountCount);
        }

        [Fact]
        public async Task PopulateAsync_JudgesEachPunchAsOfItsOwnTime()
        {
            // 手机原本只有 B 在用；A 的账号在第一次打卡之后、第二次打卡之前才登录上来：
            // 第一次打卡当时手机上只有 B，不能事后给它定性；只有第二次打卡被标记。
            await InsertLoginAsync(Device, "user-b", PunchAt.AddDays(-5));
            await InsertLoginAsync(Device, "user-a", PunchAt.AddDays(1));
            var early = Punch("user-b", Device, PunchAt);
            var late = Punch("user-b", Device, PunchAt.AddDays(1).AddMinutes(10));

            await AttendancePunchDeviceFlagEvaluator.PopulateAsync(_db, new[] { early, late });

            Assert.Null(early.SharedDeviceAccountCount);
            Assert.Equal(2, late.SharedDeviceAccountCount);
        }

        [Fact]
        public async Task PopulateAsync_WithNoCandidates_DoesNotQuery()
        {
            // 表都没建也不会报错：没有带设备 ID 的打卡时直接返回，不碰数据库。
            using var emptyDb = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = "Data Source=:memory:",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            });

            await AttendancePunchDeviceFlagEvaluator.PopulateAsync(emptyDb, new[] { Punch("user-b", null) });
        }

        private static AttendancePunchDto Punch(string userGuid, string? deviceId, DateTime? at = null) => new()
        {
            PunchGuid = Guid.NewGuid().ToString(),
            UserGuid = userGuid,
            DeviceId = deviceId,
            PunchTimeUtc = at ?? PunchAt,
        };

        private Task<int> InsertLoginAsync(
            string hardwareId,
            string? userGuid,
            DateTime loginAtUtc,
            string source = "AppLogin",
            bool isDeleted = false) =>
            _db.Insertable(new UserLoginDeviceRecord
            {
                HardwareId = hardwareId,
                UserGuid = userGuid,
                LoginSource = source,
                LoginAtUtc = loginAtUtc,
                IsDeleted = isDeleted,
                CreatedAt = loginAtUtc,
            }).ExecuteCommandAsync();
    }
}
