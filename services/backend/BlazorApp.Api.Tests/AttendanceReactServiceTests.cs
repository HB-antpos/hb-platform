using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Api.Security;
using AttendanceQrKeyDataProtection = BlazorApp.Api.Security.AttendanceQrKeyDataProtection;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Security;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests
{
    public sealed class AttendanceReactServiceTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteConnection _sqliteConnection;
        private readonly SqlSugarClient _db;
        private readonly byte[] _attendanceKey = RandomNumberGenerator.GetBytes(32);
        private readonly IDataProtectionProvider _attendanceDataProtectionProvider =
            new EphemeralDataProtectionProvider();
        private readonly AttendanceQrKeyProtector _attendanceProtector;
        private readonly AttendancePunchAuthorizationProtector _punchAuthorizationProtector;
        private readonly MutableTimeProvider _timeProvider = new(DateTimeOffset.UtcNow);
        // 授权桩默认放行全部权限；个别用例把权限码加入此集合来模拟「未授予」。
        private readonly HashSet<string> _deniedPermissions = new(StringComparer.Ordinal);

        public AttendanceReactServiceTests()
        {
            _attendanceProtector = AttendanceQrKeyDataProtection.CreateProtector(
                _attendanceDataProtectionProvider);
            _punchAuthorizationProtector = AttendancePunchAuthorizationDataProtection.CreateProtector(
                _attendanceDataProtectionProvider);
            _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
            _sqliteConnection = new SqliteConnection($"Data Source={_dbPath}");
            _sqliteConnection.Open();

            _db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _sqliteConnection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            });

            _db.CodeFirst.InitTables(
                typeof(User),
                typeof(Store),
                typeof(UserStore),
                typeof(AttendanceSchedule),
                typeof(AttendanceAvailability),
                typeof(AttendancePunch),
                typeof(AttendanceLocationSample),
                typeof(AttendanceApproval),
                typeof(AttendancePunchAdjustment),
                typeof(AttendanceStoreHoliday),
                typeof(AttendanceLeaveRequest),
                typeof(AttendanceSettings),
                typeof(EmployeeProfile),
                typeof(AttendancePosQrKey)
                ,typeof(Role)
                ,typeof(UserRole)
            );
            _db.Ado.ExecuteCommand(
                "CREATE UNIQUE INDEX UX_Test_AttendanceApproval_Pending_Source "
                + "ON AttendanceApproval(SourceType, SourceGuid) "
                + "WHERE ReviewStatus = 'Pending' AND IsDeleted = 0");
            _db.Ado.ExecuteCommand(
                "CREATE UNIQUE INDEX UX_Test_AttendancePunch_AdjustmentGuid "
                + "ON AttendancePunch(AdjustmentGuid) "
                + "WHERE AdjustmentGuid IS NOT NULL AND IsDeleted = 0");
            _db.Ado.ExecuteCommand(
                "CREATE UNIQUE INDEX UX_Test_AttendancePunch_SupersedesPunchGuid "
                + "ON AttendancePunch(SupersedesPunchGuid) "
                + "WHERE SupersedesPunchGuid IS NOT NULL AND IsDeleted = 0");
            _db.Ado.ExecuteCommand(
                "CREATE UNIQUE INDEX UX_Test_AttendancePunchAdjustment_ActiveOriginal "
                + "ON AttendancePunchAdjustment(OriginalPunchGuid) "
                + "WHERE OriginalPunchGuid IS NOT NULL AND IsDeleted = 0 "
                + "AND Status IN ('Pending', 'Applied')");
            SeedAttendanceSigningKeyAsync().GetAwaiter().GetResult();
        }

        [Fact]
        public async Task PunchAsync_WhenQrTokenMissing_ReturnsQrRequired()
        {
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.PunchAsync(new AttendancePunchRequestDto());

            Assert.False(result.Success);
            Assert.Equal("QR_REQUIRED", result.ErrorCode);
        }

        [Fact]
        public async Task ResolveAttendanceQrAsync_ValidToken_ReturnsTrustedDeviceAndExpiry()
        {
            await SeedStoreScopeAsync();
            var issuedAt = new DateTime(
                DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond,
                DateTimeKind.Utc);
            _timeProvider.SetUtcNow(issuedAt.AddSeconds(10));
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.ResolveAttendanceQrAsync(new AttendanceQrResolveRequestDto
            {
                QrToken = CreateQrToken(issuedAt),
            });

            Assert.True(result.Success);
            Assert.Equal("BRI", result.Data!.StoreCode);
            Assert.Equal("POS-001", result.Data.DeviceCode);
            Assert.Equal("Brisbane", result.Data.StoreName);
            Assert.Equal(issuedAt.AddSeconds(15), result.Data.ExpiresAtUtc);
            Assert.False(string.IsNullOrWhiteSpace(result.Data.PunchAuthorizationToken));
            Assert.Equal(
                _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(2),
                result.Data.PunchAuthorizationExpiresAtUtc);
        }

        [Fact]
        public async Task PunchAsync_WithValidAuthorization_AllowsResolvedQrAfterQrExpiry()
        {
            await SeedStoreScopeAsync();
            var issuedAt = TruncateToMilliseconds(DateTime.UtcNow);
            var tokenId = Guid.NewGuid();
            var request = CreateQrPunchRequest(issuedAt, tokenId);
            var service = CreateService("staff-user", "staff", "StoreStaff");
            _timeProvider.SetUtcNow(issuedAt.AddSeconds(10));
            var resolved = await service.ResolveAttendanceQrAsync(new AttendanceQrResolveRequestDto
            {
                QrToken = request.QrToken,
            });

            _timeProvider.SetUtcNow(issuedAt.AddSeconds(45));
            request.LocationCapturedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            request.PunchAuthorizationToken = resolved.Data!.PunchAuthorizationToken;
            var result = await service.PunchAsync(request);

            Assert.True(result.Success);
            Assert.Equal(tokenId.ToString(), result.Data!.QrTokenId);
        }

        [Fact]
        public async Task PunchAsync_WithExpiredAuthorization_ReturnsAuthorizationExpired()
        {
            await SeedStoreScopeAsync();
            var issuedAt = TruncateToMilliseconds(DateTime.UtcNow);
            var request = CreateQrPunchRequest(issuedAt, Guid.NewGuid());
            var service = CreateService("staff-user", "staff", "StoreStaff");
            _timeProvider.SetUtcNow(issuedAt.AddSeconds(10));
            var resolved = await service.ResolveAttendanceQrAsync(new AttendanceQrResolveRequestDto
            {
                QrToken = request.QrToken,
            });

            _timeProvider.SetUtcNow(resolved.Data!.PunchAuthorizationExpiresAtUtc);
            request.LocationCapturedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            request.PunchAuthorizationToken = resolved.Data.PunchAuthorizationToken;
            var result = await service.PunchAsync(request);

            Assert.False(result.Success);
            Assert.Equal("ATTENDANCE_PUNCH_AUTHORIZATION_EXPIRED", result.ErrorCode);
        }

        [Fact]
        public async Task PunchAsync_WithTamperedOrChangedBindingAuthorization_ReturnsAuthorizationInvalid()
        {
            await SeedStoreScopeAsync();
            await _db.Insertable(new User
            {
                UserGUID = "other-user",
                Username = "other",
                Email = "other@example.com",
                PasswordHash = "hash",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            await _db.Insertable(new UserStore
            {
                UserStoreGUID = "other-store-bri",
                UserGUID = "other-user",
                StoreGUID = "store-bri",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var issuedAt = TruncateToMilliseconds(DateTime.UtcNow);
            var originalRequest = CreateQrPunchRequest(issuedAt, Guid.NewGuid());
            var service = CreateService("staff-user", "staff", "StoreStaff");
            _timeProvider.SetUtcNow(issuedAt.AddSeconds(5));
            var resolved = await service.ResolveAttendanceQrAsync(new AttendanceQrResolveRequestDto
            {
                QrToken = originalRequest.QrToken,
            });

            var tamperedRequest = CreateQrPunchRequest(issuedAt, Guid.NewGuid());
            tamperedRequest.QrToken = originalRequest.QrToken;
            tamperedRequest.PunchAuthorizationToken = MutateProtectedToken(
                resolved.Data!.PunchAuthorizationToken);
            var tampered = await service.PunchAsync(tamperedRequest);

            var changedQrRequest = CreateQrPunchRequest(issuedAt, Guid.NewGuid());
            changedQrRequest.PunchAuthorizationToken = resolved.Data.PunchAuthorizationToken;
            var changedQr = await service.PunchAsync(changedQrRequest);

            originalRequest.PunchAuthorizationToken = resolved.Data.PunchAuthorizationToken;
            var changedUser = await CreateService("other-user", "other", "StoreStaff")
                .PunchAsync(originalRequest);

            Assert.Equal("ATTENDANCE_PUNCH_AUTHORIZATION_INVALID", tampered.ErrorCode);
            Assert.Equal("ATTENDANCE_PUNCH_AUTHORIZATION_INVALID", changedQr.ErrorCode);
            Assert.Equal("ATTENDANCE_PUNCH_AUTHORIZATION_INVALID", changedUser.ErrorCode);
        }

        [Fact]
        public async Task PunchAsync_ExpiredAuthorizationForExistingPunch_RemainsIdempotent()
        {
            await SeedStoreScopeAsync();
            var issuedAt = TruncateToMilliseconds(DateTime.UtcNow);
            var request = CreateQrPunchRequest(issuedAt, Guid.NewGuid());
            var service = CreateService("staff-user", "staff", "StoreStaff");
            _timeProvider.SetUtcNow(issuedAt.AddSeconds(5));
            var resolved = await service.ResolveAttendanceQrAsync(new AttendanceQrResolveRequestDto
            {
                QrToken = request.QrToken,
            });
            request.PunchAuthorizationToken = resolved.Data!.PunchAuthorizationToken;
            var first = await service.PunchAsync(request);

            _timeProvider.SetUtcNow(resolved.Data.PunchAuthorizationExpiresAtUtc);
            var duplicate = await service.PunchAsync(request);

            Assert.True(first.Success);
            Assert.True(duplicate.Success);
            Assert.Equal(first.Data!.PunchGuid, duplicate.Data!.PunchGuid);
        }

        [Theory]
        [InlineData("OTHER", "POS-001")]
        [InlineData("BRI", "POS-002")]
        public void AttendancePunchAuthorization_ChangedStoreOrDevice_IsInvalid(
            string storeCode,
            string deviceCode)
        {
            var now = TruncateToMilliseconds(DateTime.UtcNow);
            var token = CreateQrToken(now);
            Assert.True(AttendanceQrTokenCodec.TryDecryptIdentity(
                token, _attendanceKey, out var payload, out var kid, out _));
            var authorization = _punchAuthorizationProtector.Issue(
                "staff-user", token, kid, payload!, now);
            var changedPayload = new AttendanceQrTokenPayload
            {
                TokenId = payload!.TokenId,
                StoreCode = storeCode,
                DeviceCode = deviceCode,
                IssuedAtUtc = payload.IssuedAtUtc,
            };

            var validation = _punchAuthorizationProtector.Validate(
                authorization.Token,
                "staff-user",
                token,
                kid,
                changedPayload,
                now.AddSeconds(30));
            var changedKeyValidation = _punchAuthorizationProtector.Validate(
                authorization.Token,
                "staff-user",
                token,
                "K2",
                payload,
                now.AddSeconds(30));

            Assert.Equal(AttendancePunchAuthorizationValidationResult.Invalid, validation);
            Assert.Equal(
                AttendancePunchAuthorizationValidationResult.Invalid,
                changedKeyValidation);
        }

        [Fact]
        public async Task ResolveAttendanceQrAsync_WhenActiveKeyAlgorithmDrifts_RejectsBeforeDecrypt()
        {
            await SeedStoreScopeAsync();
            await _db.Updateable<AttendancePosQrKey>()
                .SetColumns(item => item.Algorithm == "ES256")
                .SetColumns(item => item.ProtectedKey == "not-protected-data")
                .Where(item => item.Kid == "K1")
                .ExecuteCommandAsync();

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .ResolveAttendanceQrAsync(new AttendanceQrResolveRequestDto
                {
                    QrToken = CreateQrToken(DateTime.UtcNow),
                });

            Assert.False(result.Success);
            Assert.Equal("ATTENDANCE_QR_KEY_INVALID", result.ErrorCode);
        }

        [Theory]
        [InlineData(false, "POS_DEVICE_DISABLED")]
        [InlineData(true, "FORBIDDEN_STORE")]
        public async Task ResolveAttendanceQrAsync_DisabledPosOrStoreForbidden_Rejects(
            bool posActive,
            string expectedError)
        {
            await SeedStoreScopeAsync();
            var userGuid = posActive ? "outside-user" : "staff-user";
            var result = await CreateService(userGuid, "user", posActive, "StoreStaff")
                .ResolveAttendanceQrAsync(new AttendanceQrResolveRequestDto
                {
                    QrToken = CreateQrToken(DateTime.UtcNow),
                });

            Assert.False(result.Success);
            Assert.Equal(expectedError, result.ErrorCode);
        }

        [Fact]
        public async Task PunchAsync_WithValidQrToken_AutomaticallyClocksInThenOutAndCompletesDay()
        {
            await SeedStoreScopeAsync();
            var now = DateTime.UtcNow;
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var clockInRequest = CreateQrPunchRequest(now, Guid.NewGuid());
            // Expo 严格白名单不发送客户端权限状态，服务端只验证真实坐标与采集时间。
            clockInRequest.LocationPermissionStatus = null;
            var clockIn = await service.PunchAsync(clockInRequest);
            var duplicate = await service.PunchAsync(clockInRequest);
            _timeProvider.SetUtcNow(now.AddSeconds(31));
            var expiredDuplicate = await service.PunchAsync(clockInRequest);
            var forgedRequest = CreateQrPunchRequest(now, Guid.NewGuid());
            var signatureSeparator = clockInRequest.QrToken!.LastIndexOf('.');
            var signatureOffset = signatureSeparator + 1;
            forgedRequest.QrToken = clockInRequest.QrToken[..signatureOffset]
                + (clockInRequest.QrToken[signatureOffset] == 'A' ? 'B' : 'A')
                + clockInRequest.QrToken[(signatureOffset + 1)..];
            _timeProvider.SetUtcNow(now.AddSeconds(31));
            var forged = await service.PunchAsync(forgedRequest);
            var clockOut = await service.PunchAsync(CreateQrPunchRequest(now, Guid.NewGuid()));
            var complete = await service.PunchAsync(CreateQrPunchRequest(now, Guid.NewGuid()));

            Assert.True(clockIn.Success);
            Assert.Equal("ClockIn", clockIn.Data!.PunchType);
            Assert.Equal("PosQr", clockIn.Data.Source);
            Assert.Equal("POS-001", clockIn.Data.PosDeviceCode);
            Assert.Equal("staff", clockIn.Data.EmployeeName);
            Assert.Equal("Brisbane", clockIn.Data.StoreName);
            Assert.Equal(clockIn.Data.PunchTimeUtc, clockIn.Data.ServerTimeUtc);
            Assert.Equal(clockIn.Data.PunchGuid, duplicate.Data!.PunchGuid);
            Assert.Equal(clockIn.Data.PunchGuid, expiredDuplicate.Data!.PunchGuid);
            Assert.False(forged.Success);
            Assert.Equal("ATTENDANCE_QR_AUTH_INVALID", forged.ErrorCode);
            Assert.True(clockOut.Success);
            Assert.Equal("ClockOut", clockOut.Data!.PunchType);
            Assert.False(complete.Success);
            Assert.Equal("DAY_COMPLETE", complete.ErrorCode);
        }

        [Fact]
        public async Task PunchAsync_WithRevokedOrUnknownKey_Rejects()
        {
            await SeedStoreScopeAsync();
            var now = DateTime.UtcNow;
            var service = CreateService("staff-user", "staff", "StoreStaff");
            await _db.Updateable<AttendancePosQrKey>()
                .SetColumns(item => item.Status == "Revoked")
                .Where(item => item.Kid == "K1")
                .ExecuteCommandAsync();

            var revoked = await service.PunchAsync(CreateQrPunchRequest(now, Guid.NewGuid()));
            var unknownRequest = CreateQrPunchRequest(now, Guid.NewGuid());
            unknownRequest.QrToken = AttendanceQrTokenCodec.Encrypt(new AttendanceQrTokenPayload
            {
                TokenId = Guid.NewGuid(), StoreCode = "BRI", DeviceCode = "POS-001", IssuedAtUtc = now,
            }, "K2", RandomNumberGenerator.GetBytes(32));
            var unknown = await service.PunchAsync(unknownRequest);

            Assert.Equal("ATTENDANCE_QR_KEY_REVOKED", revoked.ErrorCode);
            Assert.Equal("ATTENDANCE_QR_KEY_UNKNOWN", unknown.ErrorCode);
        }

        [Fact]
        public async Task PunchAsync_WhenProtectedKeyCannotBeDecrypted_ReturnsSecurityError()
        {
            await SeedStoreScopeAsync();
            await _db.Updateable<AttendancePosQrKey>()
                .SetColumns(item => item.ProtectedKey == "not-protected-data")
                .Where(item => item.Kid == "K1")
                .ExecuteCommandAsync();

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .PunchAsync(CreateQrPunchRequest(DateTime.UtcNow, Guid.NewGuid()));

            Assert.False(result.Success);
            Assert.Equal("ATTENDANCE_QR_KEY_DECRYPT_FAILED", result.ErrorCode);
            Assert.DoesNotContain("not-protected-data", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task PunchAsync_WhenActiveKeyAlgorithmDrifts_RejectsBeforeDecrypt()
        {
            await SeedStoreScopeAsync();
            await _db.Updateable<AttendancePosQrKey>()
                .SetColumns(item => item.Algorithm == string.Empty)
                .SetColumns(item => item.ProtectedKey == "not-protected-data")
                .Where(item => item.Kid == "K1")
                .ExecuteCommandAsync();

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .PunchAsync(CreateQrPunchRequest(DateTime.UtcNow, Guid.NewGuid()));

            Assert.False(result.Success);
            Assert.Equal("ATTENDANCE_QR_KEY_INVALID", result.ErrorCode);
        }

        [Fact]
        public async Task PunchAsync_WithExpiredNotActiveOrMismatchedToken_Rejects()
        {
            await SeedStoreScopeAsync();
            var issuedAt = DateTime.UtcNow;
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var expiredRequest = CreateQrPunchRequest(issuedAt, Guid.NewGuid());
            _timeProvider.SetUtcNow(issuedAt.AddSeconds(15));
            var expired = await service.PunchAsync(expiredRequest);
            var notActiveRequest = CreateQrPunchRequest(issuedAt, Guid.NewGuid());
            _timeProvider.SetUtcNow(issuedAt.AddSeconds(-1));
            var notActive = await service.PunchAsync(notActiveRequest);
            var mismatchRequest = CreateQrPunchRequest(issuedAt, Guid.NewGuid());
            mismatchRequest.QrToken = CreateQrToken(issuedAt, deviceCode: "POS-002");
            _timeProvider.SetUtcNow(issuedAt);
            var mismatch = await service.PunchAsync(mismatchRequest);
            var storeMismatchRequest = CreateQrPunchRequest(issuedAt, Guid.NewGuid());
            storeMismatchRequest.QrToken = CreateQrToken(issuedAt, storeCode: "OTHER");
            var storeMismatch = await service.PunchAsync(storeMismatchRequest);

            Assert.Equal("ATTENDANCE_QR_EXPIRED", expired.ErrorCode);
            Assert.Equal("ATTENDANCE_QR_NOT_ACTIVE", notActive.ErrorCode);
            Assert.Equal("ATTENDANCE_QR_DEVICE_MISMATCH", mismatch.ErrorCode);
            Assert.Equal("ATTENDANCE_QR_DEVICE_MISMATCH", storeMismatch.ErrorCode);
        }

        [Fact]
        public async Task PunchAsync_WhenPosDisabledOrEmployeeHasNoStoreAccess_Rejects()
        {
            await SeedStoreScopeAsync();
            var now = DateTime.UtcNow;
            var disabled = await CreateService("staff-user", "staff", false, "StoreStaff")
                .PunchAsync(CreateQrPunchRequest(now, Guid.NewGuid()));
            var forbidden = await CreateService("outside-user", "outside", "StoreStaff")
                .PunchAsync(CreateQrPunchRequest(now, Guid.NewGuid()));

            Assert.Equal("POS_DEVICE_DISABLED", disabled.ErrorCode);
            Assert.Equal("FORBIDDEN_STORE", forbidden.ErrorCode);
        }

        [Fact]
        public async Task PunchAsync_TwoEmployeesCanUseSameQrToken()
        {
            await SeedStoreScopeAsync();
            await _db.Insertable(new User
            {
                UserGUID = "second-user",
                Username = "second",
                Email = "second@example.com",
                PasswordHash = "hash",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            await _db.Insertable(new UserStore
            {
                UserStoreGUID = "second-store-bri",
                UserGUID = "second-user",
                StoreGUID = "store-bri",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var now = DateTime.UtcNow;
            var request = CreateQrPunchRequest(now, Guid.NewGuid());

            var first = await CreateService("staff-user", "staff", "StoreStaff").PunchAsync(request);
            var second = await CreateService("second-user", "second", "StoreStaff").PunchAsync(request);

            Assert.True(first.Success);
            Assert.True(second.Success);
            Assert.NotEqual(first.Data!.PunchGuid, second.Data!.PunchGuid);
        }

        [Theory]
        [InlineData(2601)]
        [InlineData(2627)]
        public void AttendancePunchPersistenceException_DetectsDirectAndWrappedUniqueSqlErrors(int number)
        {
            var sqlException = CreateSqlException(number);
            var sugarWrapper = new SqlSugarException("SqlSugar insert failed");
            typeof(Exception).GetField("_innerException", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(sugarWrapper, sqlException);

            Assert.True(AttendancePunchPersistenceException.IsUniqueConstraintViolation(sqlException));
            Assert.True(AttendancePunchPersistenceException.IsUniqueConstraintViolation(sugarWrapper));
        }

        [Fact]
        public void AttendancePunchPersistenceException_DoesNotHideOtherDatabaseErrors()
        {
            Assert.False(AttendancePunchPersistenceException.IsUniqueConstraintViolation(CreateSqlException(50000)));
            Assert.False(AttendancePunchPersistenceException.IsUniqueConstraintViolation(
                new SqlSugarException("connection failed")));
        }

        [Fact]
        public async Task PunchAsync_PersistsPunchAndApprovalInsideOneTransaction()
        {
            var source = await File.ReadAllTextAsync(Path.Combine(
                FindRepoRoot(),
                "services/backend/BlazorApp.Api/Services/React/AttendanceReactService.cs"));

            Assert.Contains("BeginTranAsync", source);
            Assert.Contains("CommitTranAsync", source);
            Assert.Contains("RollbackTranAsync", source);
        }

        [Fact]
        public async Task ReviewApprovalAsync_ExplicitlyUsesSerializableTransaction()
        {
            var source = await File.ReadAllTextAsync(Path.Combine(
                FindRepoRoot(),
                "services/backend/BlazorApp.Api/Services/React/AttendanceReactService.cs"));
            var methodStart = source.IndexOf(
                "private async Task<ApiResponse<AttendanceApprovalDto>> ReviewApprovalAsync",
                StringComparison.Ordinal);
            var methodEnd = source.IndexOf(
                "private async Task CreatePendingApprovalAsync",
                methodStart,
                StringComparison.Ordinal);

            Assert.True(methodStart >= 0 && methodEnd > methodStart);
            var methodSource = source[methodStart..methodEnd];
            Assert.Contains("BeginTranAsync(IsolationLevel.Serializable)", methodSource);
        }

        [Theory]
        [InlineData("Punch", false)]
        [InlineData("Leave", false)]
        [InlineData("PunchAdjustment", false)]
        [InlineData("MissingClockOut", true)]
        [InlineData("Overtime", true)]
        public void BuildPendingApprovalExistenceQuery_SqlServer方言_不生成布尔Or条件(
            string sourceType,
            bool expectsPendingFilter)
        {
            using var sqlServerDb = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString =
                    "Server=127.0.0.1;Database=HBweb;User Id=test;Password=test;TrustServerCertificate=True;",
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            });

            var query = AttendanceReactService.BuildPendingApprovalExistenceQuery(
                sqlServerDb,
                sourceType,
                "source-1");
            var sql = query.ToSql();

            Assert.Equal(
                expectsPendingFilter,
                sql.Value.Any(parameter => Equals(parameter.Value, "Pending")));
            Assert.DoesNotContain(" OR ", sql.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sql.Value, parameter => parameter.Value is bool);
        }

        [Fact]
        public async Task GetSchedulesAsync_WhenStoreManagerRequestsUnmanagedStore_ReturnsForbidden()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.GetSchedulesAsync(new AttendanceScheduleQueryDto
            {
                StoreCode = "OTHER",
                WeekStartDate = new DateTime(2026, 5, 18),
            });

            Assert.False(result.Success);
            Assert.Equal("FORBIDDEN_STORE", result.ErrorCode);
        }

        [Fact]
        public async Task GetSchedulesAsync_WithPunches_ReturnsRosterFieldsWithoutAttendanceDetails()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(new[]
            {
                CreateStoredPunch("roster-in", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)),
                CreateStoredPunch("roster-out", "ClockOut", new DateTime(2026, 5, 18, 17, 30, 0)),
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .GetSchedulesAsync(new AttendanceScheduleQueryDto
                {
                    StoreCode = "BRI",
                    WeekStartDate = new DateTime(2026, 5, 18),
                });

            Assert.True(result.Success, result.Message);
            var schedule = Assert.Single(result.Data!);
            Assert.Empty(schedule.Segments);
            Assert.Equal(0, schedule.WorkedMinutes);
            Assert.Equal(0, schedule.CandidateOvertimeMinutes);
            Assert.Equal(0, await _db.Queryable<AttendanceApproval>().CountAsync());
        }

        [Fact]
        public async Task GetAttendanceRecordsAsync_PaginatesBeforePopulatingAttendanceDetails()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("record-1", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active");
            await SeedScheduleAsync("record-2", "BRI", "staff-user", new DateTime(2026, 5, 19), "Active");
            await SeedScheduleAsync("record-3", "BRI", "staff-user", new DateTime(2026, 5, 20), "Active");
            await _db.Insertable(new[]
            {
                CreateStoredPunchForSchedule("record-2-in", "record-2", "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 19, 9, 0, 0)),
                CreateStoredPunchForSchedule("record-2-out", "record-2", "BRI", "staff-user", "ClockOut", new DateTime(2026, 5, 19, 17, 0, 0)),
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 20, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .GetAttendanceRecordsAsync(new AttendanceScheduleQueryDto
                {
                    StoreCode = "BRI",
                    WeekStartDate = new DateTime(2026, 5, 18),
                    Page = 2,
                    PageSize = 1,
                });

            Assert.True(result.Success, result.Message);
            Assert.Equal(3, result.Data!.Total);
            Assert.Equal(2, result.Data.Page);
            Assert.Equal(1, result.Data.PageSize);
            var schedule = Assert.Single(result.Data.Items!);
            Assert.Equal("record-2", schedule.ScheduleGuid);
            Assert.Equal(480, schedule.WorkedMinutes);
            Assert.Single(schedule.Segments);
        }

        [Fact]
        public async Task CreateMyAvailabilityAsync_AllowsMultipleSegmentsWithinWeek()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.CreateMyAvailabilityAsync(
                new CreateAttendanceAvailabilityDto
                {
                    StoreCode = "BRI",
                    WeekStartDate = new DateTime(2026, 5, 18),
                    Segments = new List<AttendanceAvailabilitySegmentDto>
                    {
                        new()
                        {
                            AvailableDate = new DateTime(2026, 5, 18),
                            StartTime = new TimeSpan(9, 0, 0),
                            EndTime = new TimeSpan(12, 0, 0),
                        },
                        new()
                        {
                            AvailableDate = new DateTime(2026, 5, 20),
                            StartTime = new TimeSpan(14, 0, 0),
                            EndTime = new TimeSpan(18, 0, 0),
                        },
                    },
                }
            );

            Assert.True(result.Success);
            Assert.Equal(2, result.Data!.Count);
        }

        [Fact]
        public async Task CreateScheduleAsync_WhenSameStoreUserDateOverlaps_ReturnsConflict()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("manager-user", "manager", "StoreManager");

            var first = await service.CreateScheduleAsync(new CreateAttendanceScheduleDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                WorkDate = new DateTime(2026, 5, 18),
                StartTime = new TimeSpan(9, 0, 0),
                EndTime = new TimeSpan(13, 0, 0),
            });
            var second = await service.CreateScheduleAsync(new CreateAttendanceScheduleDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                WorkDate = new DateTime(2026, 5, 18),
                StartTime = new TimeSpan(12, 0, 0),
                EndTime = new TimeSpan(16, 0, 0),
            });

            Assert.True(first.Success);
            Assert.False(second.Success);
            Assert.Equal("SCHEDULE_OVERLAP", second.ErrorCode);
        }

        [Fact]
        public async Task CreateScheduleAsync_WhenStatusIsMissing_SavesDraft()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.CreateScheduleAsync(new CreateAttendanceScheduleDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                WorkDate = new DateTime(2026, 5, 18),
                StartTime = new TimeSpan(9, 0, 0),
                EndTime = new TimeSpan(17, 0, 0),
            });

            Assert.True(result.Success);
            Assert.Equal("Draft", result.Data!.Status);
            Assert.Equal("Draft", await _db.Queryable<AttendanceSchedule>()
                .Where(item => item.ScheduleGuid == result.Data.ScheduleGuid)
                .Select(item => item.Status)
                .FirstAsync());
        }

        [Fact]
        public async Task CreateScheduleAsync_WhenStatusIsActive_SavesDraft()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.CreateScheduleAsync(new CreateAttendanceScheduleDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                WorkDate = new DateTime(2026, 5, 18),
                StartTime = new TimeSpan(9, 0, 0),
                EndTime = new TimeSpan(17, 0, 0),
                Status = "Active",
            });

            Assert.True(result.Success);
            Assert.Equal("Draft", result.Data!.Status);
            Assert.Equal("Draft", await _db.Queryable<AttendanceSchedule>()
                .Where(item => item.ScheduleGuid == result.Data.ScheduleGuid)
                .Select(item => item.Status)
                .FirstAsync());
        }

        [Fact]
        public async Task PublishWeekAsync_WhenStoreManagerPublishesManagedStore_ActivatesOnlyDraftSchedulesInWeek()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("draft-in-week", "BRI", "staff-user", new DateTime(2026, 5, 18), "Draft");
            await SeedScheduleAsync("active-in-week", "BRI", "staff-user", new DateTime(2026, 5, 19), "Active");
            await SeedScheduleAsync("cancelled-in-week", "BRI", "staff-user", new DateTime(2026, 5, 20), "Cancelled");
            await SeedScheduleAsync("draft-outside-week", "BRI", "staff-user", new DateTime(2026, 5, 25), "Draft");
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.PublishWeekAsync(new PublishAttendanceWeekDto
            {
                StoreCode = "BRI",
                WeekStartDate = new DateTime(2026, 5, 18),
            });

            Assert.True(result.Success);
            Assert.Equal(1, result.Data);
            Assert.Equal("Active", await GetScheduleStatusAsync("draft-in-week"));
            Assert.Equal("Active", await GetScheduleStatusAsync("active-in-week"));
            Assert.Equal("Cancelled", await GetScheduleStatusAsync("cancelled-in-week"));
            Assert.Equal("Draft", await GetScheduleStatusAsync("draft-outside-week"));
        }

        [Fact]
        public async Task PublishWeekAsync_WhenStoreManagerRequestsUnmanagedStore_ReturnsForbidden()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("other-draft", "OTHER", "staff-user", new DateTime(2026, 5, 18), "Draft");
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.PublishWeekAsync(new PublishAttendanceWeekDto
            {
                StoreCode = "OTHER",
                WeekStartDate = new DateTime(2026, 5, 18),
            });

            Assert.False(result.Success);
            Assert.Equal("FORBIDDEN_STORE", result.ErrorCode);
            Assert.Equal("Draft", await GetScheduleStatusAsync("other-draft"));
        }

        [Fact]
        public async Task GetMyWeekAsync_ReturnsOnlyCurrentUsersRelatedStoreActiveSchedules()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("my-active", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active");
            await SeedScheduleAsync("my-draft", "BRI", "staff-user", new DateTime(2026, 5, 19), "Draft");
            await SeedScheduleAsync("manager-active", "BRI", "manager-user", new DateTime(2026, 5, 20), "Active");
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.GetMyWeekAsync(new DateTime(2026, 5, 18), "BRI");

            Assert.True(result.Success);
            var schedule = Assert.Single(result.Data!);
            Assert.Equal("my-active", schedule.ScheduleGuid);
            Assert.True(schedule.IsMine);
            Assert.Equal("Active", schedule.Status);
        }

        [Fact]
        public async Task GetMyTodayAsync_WhenWorkDateProvided_ReturnsSelectedDateData()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("selected-day", "BRI", "staff-user", new DateTime(2026, 5, 19), "Active");
            await SeedScheduleAsync("other-day", "BRI", "staff-user", new DateTime(2026, 5, 20), "Active");
            await _db.Insertable(new AttendancePunch
            {
                PunchGuid = "selected-punch",
                StoreCode = "BRI",
                UserGuid = "staff-user",
                WorkDate = new DateTime(2026, 5, 19),
                PunchType = "ClockIn",
                PunchTimeUtc = DateTime.Parse("2026-05-18T23:00:00Z").ToUniversalTime(),
                PunchTimeLocal = new DateTime(2026, 5, 19, 9, 0, 0),
                Status = "Normal",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            await _db.Insertable(new AttendanceStoreHoliday
            {
                HolidayGuid = "selected-holiday",
                StoreCode = "BRI",
                HolidayDate = new DateTime(2026, 5, 19),
                HolidayName = "Selected holiday",
                BusinessStatus = "Open",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.GetMyTodayAsync(new DateTime(2026, 5, 19), "BRI");

            Assert.True(result.Success);
            Assert.Equal("2026-05-19", result.Data!.WorkDate.ToString("yyyy-MM-dd"));
            Assert.Equal("selected-day", Assert.Single(result.Data.Schedules).ScheduleGuid);
            Assert.Equal("selected-punch", Assert.Single(result.Data.Punches).PunchGuid);
            Assert.Equal("selected-holiday", Assert.Single(result.Data.Holidays).HolidayGuid);
        }

        [Fact]
        public async Task GetMyTodayAsync_MultipleSegments_DerivesBreakAndOvertime()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var workDate = new DateTime(2026, 5, 18);
            await _db.Insertable(new[]
            {
                CreateStoredPunch("in-1", "ClockIn", workDate.AddHours(8).AddMinutes(42)),
                CreateStoredPunch("out-1", "ClockOut", workDate.AddHours(12)),
                CreateStoredPunch("in-2", "ClockIn", workDate.AddHours(13)),
                CreateStoredPunch("out-2", "ClockOut", workDate.AddHours(17).AddMinutes(22)),
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .GetMyTodayAsync(workDate, "BRI");

            Assert.True(result.Success);
            var schedule = Assert.Single(result.Data!.Schedules);
            Assert.Equal("Completed", schedule.ScheduleState);
            Assert.Equal(2, schedule.CompletedSegmentCount);
            Assert.Equal(460, schedule.WorkedMinutes);
            Assert.Equal(60, schedule.BreakMinutes);
            Assert.Equal(30, schedule.CandidateOvertimeMinutes);
            Assert.Equal("Break", schedule.Segments[0].Status);
            Assert.Equal("Final", schedule.Segments[1].Status);
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_StaffWithinTwoDays_CreatesPendingApproval()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("staff-user", "staff", "StoreStaff"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 9, 0, 0),
                    Reason = "忘记打卡",
                });

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal("Pending", result.Data!.Status);
            Assert.Equal(1, await _db.Queryable<AttendanceApproval>()
                .CountAsync(item => item.SourceType == "PunchAdjustment"));
            Assert.Equal(0, await _db.Queryable<AttendancePunch>().CountAsync());
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_RequestedUtc优先且按门店本地时间匹配排班和落库同一瞬间()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var requestedUtc = new DateTime(2026, 5, 17, 23, 0, 0, DateTimeKind.Utc);

            var result = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("staff-user", "staff", "StoreStaff"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    PunchType = "ClockIn",
                    // UTC 对应 Brisbane 2026-05-18 09:00；故意传冲突旧字段以验证 UTC 优先。
                    RequestedPunchTimeLocal = new DateTime(2030, 1, 1, 9, 0, 0),
                    RequestedPunchTimeUtc = requestedUtc,
                    Reason = "跨时区设备补卡",
                });

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            var stored = await _db.Queryable<AttendancePunchAdjustment>()
                .FirstAsync(item => item.AdjustmentGuid == result.Data!.AdjustmentGuid);
            Assert.Equal("schedule-1", stored.ScheduleGuid);
            Assert.Equal(requestedUtc, stored.RequestedPunchTimeUtc);
            Assert.Equal(new DateTime(2026, 5, 18, 9, 0, 0), stored.RequestedPunchTimeLocal);
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_LegacyLocalOnly仍按门店本地时间兼容()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var requestedLocal = new DateTime(2026, 5, 18, 9, 0, 0);

            var result = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("staff-user", "staff", "StoreStaff"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = requestedLocal,
                    Reason = "旧客户端补卡",
                });

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            var stored = await _db.Queryable<AttendancePunchAdjustment>()
                .FirstAsync(item => item.AdjustmentGuid == result.Data!.AdjustmentGuid);
            Assert.Equal(requestedLocal, stored.RequestedPunchTimeLocal);
            Assert.Equal(new DateTime(2026, 5, 17, 23, 0, 0, DateTimeKind.Utc), stored.RequestedPunchTimeUtc);
        }

        [Theory]
        [InlineData(2026, 10, 4, 2, 30, "Sydney 夏令时跳过时段")]
        [InlineData(2026, 4, 5, 2, 30, "Sydney 夏令时回拨重叠时段")]
        public async Task CreateMyPunchAdjustmentAsync_LegacySydney无效或歧义本地时间_返回结构化错误且不落库(
            int year,
            int month,
            int day,
            int hour,
            int minute,
            string scenario
        )
        {
            await SeedStoreScopeAsync();
            await _db.Updateable<Store>()
                .SetColumns(item => item.Address == "Sydney NSW 2000")
                .Where(item => item.StoreCode == "BRI")
                .ExecuteCommandAsync();
            var workDate = new DateTime(year, month, day);
            await SeedScheduleAsync("sydney-schedule", "BRI", "staff-user", workDate, "Active");
            _timeProvider.SetUtcNow(workDate.AddHours(6));

            ApiResponse<AttendancePunchAdjustmentDto>? result = null;
            var exception = await Record.ExceptionAsync(async () =>
            {
                result = await CreateService("staff-user", "staff", "StoreStaff")
                    .CreateMyPunchAdjustmentAsync(new CreateAttendancePunchAdjustmentDto
                    {
                        StoreCode = "BRI",
                        ScheduleGuid = "sydney-schedule",
                        PunchType = "ClockIn",
                        // 不提供 UTC，明确覆盖旧客户端的门店 local-only 输入。
                        RequestedPunchTimeLocal = new DateTime(year, month, day, hour, minute, 0),
                        Reason = scenario,
                    });
            });

            Assert.Null(exception);
            Assert.NotNull(result);
            Assert.False(result!.Success);
            Assert.Equal("PUNCH_TIME_INVALID", result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendancePunchAdjustment>().CountAsync());
            Assert.Equal(0, await _db.Queryable<AttendanceApproval>().CountAsync());
            Assert.Equal(0, await _db.Queryable<AttendancePunch>().CountAsync());
        }

        [Fact]
        public async Task ApproveAsync_补卡审批使用结构化AdjustmentUtc而不是重新解析Local文本()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var requestedUtc = new DateTime(2026, 5, 17, 23, 0, 0, DateTimeKind.Utc);
            var created = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("staff-user", "staff", "StoreStaff"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2030, 1, 1, 9, 0, 0),
                    RequestedPunchTimeUtc = requestedUtc,
                    Reason = "审批 UTC 契约",
                });
            Assert.True(created.Success, $"{created.ErrorCode}: {created.Message}");
            var approval = await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.SourceGuid == created.Data!.AdjustmentGuid);

            var approved = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "同意" });

            Assert.True(approved.Success, $"{approved.ErrorCode}: {approved.Message}");
            var appliedPunch = await _db.Queryable<AttendancePunch>()
                .FirstAsync(item => item.AdjustmentGuid == created.Data!.AdjustmentGuid);
            Assert.Equal(requestedUtc, appliedPunch.PunchTimeUtc);
            Assert.Equal(new DateTime(2026, 5, 18, 9, 0, 0), appliedPunch.PunchTimeLocal);
        }

        [Fact]
        public async Task PreviewMyPunchAdjustmentAsync_ValidRequest_ReturnsAuthoritativeDelta()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .PreviewMyPunchAdjustmentAsync(new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 8, 40, 0),
                    Reason = "补上班卡",
                });

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.True(result.Data!.IsValid);
            Assert.False(string.IsNullOrWhiteSpace(result.Data.PreviewRevision));
            Assert.Equal(15, result.Data.CandidateOvertimeMinutesDelta);
            Assert.False(result.Data.WouldAutoApprove);
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_WhenPunchesChangeAfterPreview_ReturnsStaleRevision()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var request = new CreateAttendancePunchAdjustmentDto
            {
                StoreCode = "BRI",
                ScheduleGuid = "schedule-1",
                PunchType = "ClockIn",
                RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 11, 0, 0),
                Reason = "预览后状态变化",
            };

            var preview = await service.PreviewMyPunchAdjustmentAsync(request);
            Assert.True(preview.Success, preview.Message);
            request.PreviewRevision = preview.Data!.PreviewRevision;
            await _db.Insertable(new[]
            {
                CreateStoredPunch("state-in", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)),
                CreateStoredPunch("state-out", "ClockOut", new DateTime(2026, 5, 18, 10, 0, 0)),
            }).ExecuteCommandAsync();

            var created = await service.CreateMyPunchAdjustmentAsync(request);

            Assert.False(created.Success);
            Assert.Equal("ADJUSTMENT_PREVIEW_STALE", created.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendancePunchAdjustment>().CountAsync());
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_WhenScheduleChangesAfterPreview_ReturnsStaleRevision()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var request = new CreateAttendancePunchAdjustmentDto
            {
                StoreCode = "BRI",
                ScheduleGuid = "schedule-1",
                PunchType = "ClockIn",
                RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 9, 0, 0),
                Reason = "排班调整后提交",
            };

            var preview = await service.PreviewMyPunchAdjustmentAsync(request);
            Assert.True(preview.Success, preview.Message);
            request.PreviewRevision = preview.Data!.PreviewRevision;
            var revisedEndTime = new TimeSpan(16, 0, 0);
            await _db.Updateable<AttendanceSchedule>()
                .SetColumns(item => item.EndTime == revisedEndTime)
                .Where(item => item.ScheduleGuid == "schedule-1")
                .ExecuteCommandAsync();

            var created = await service.CreateMyPunchAdjustmentAsync(request);

            Assert.False(created.Success);
            Assert.Equal("ADJUSTMENT_PREVIEW_STALE", created.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendancePunchAdjustment>().CountAsync());
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_WhenEffectivePunchApprovalStatusChanges_DoesNotInvalidatePreview()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(CreateStoredPunch(
                "revision-status-in", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)))
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var request = new CreateAttendancePunchAdjustmentDto
            {
                StoreCode = "BRI",
                ScheduleGuid = "schedule-1",
                PunchType = "ClockOut",
                RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 17, 0, 0),
                Reason = "审批状态变化不应改变有效卡序",
            };
            var preview = await service.PreviewMyPunchAdjustmentAsync(request);
            Assert.True(preview.Success, preview.Message);
            request.PreviewRevision = preview.Data!.PreviewRevision;
            await _db.Updateable<AttendancePunch>()
                .SetColumns(item => item.Status == "Approved")
                .Where(item => item.PunchGuid == "revision-status-in")
                .ExecuteCommandAsync();

            var created = await service.CreateMyPunchAdjustmentAsync(request);

            Assert.True(created.Success, $"{created.ErrorCode}: {created.Message}");
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_WhenSupersededPunchChanges_DoesNotInvalidatePreview()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var superseded = CreateStoredPunch(
                "revision-old-in", "ClockIn", new DateTime(2026, 5, 18, 9, 10, 0));
            var replacement = CreateStoredPunch(
                "revision-replacement-in", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0));
            replacement.SupersedesPunchGuid = superseded.PunchGuid;
            await _db.Insertable(new[] { superseded, replacement }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var request = new CreateAttendancePunchAdjustmentDto
            {
                StoreCode = "BRI",
                ScheduleGuid = "schedule-1",
                PunchType = "ClockOut",
                RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 17, 0, 0),
                Reason = "旧卡变化不应改变有效卡序",
            };
            var preview = await service.PreviewMyPunchAdjustmentAsync(request);
            Assert.True(preview.Success, preview.Message);
            request.PreviewRevision = preview.Data!.PreviewRevision;
            var changedLocal = new DateTime(2026, 5, 18, 12, 0, 0);
            var changedUtc = new DateTime(2026, 5, 18, 2, 0, 0, DateTimeKind.Utc);
            await _db.Updateable<AttendancePunch>()
                .SetColumns(item => item.PunchTimeLocal == changedLocal)
                .SetColumns(item => item.PunchTimeUtc == changedUtc)
                .SetColumns(item => item.Status == "Approved")
                .Where(item => item.PunchGuid == superseded.PunchGuid)
                .ExecuteCommandAsync();

            var created = await service.CreateMyPunchAdjustmentAsync(request);

            Assert.True(created.Success, $"{created.ErrorCode}: {created.Message}");
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_WhenSettingsChangeAfterPreview_ReturnsStaleRevision()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var request = new CreateAttendancePunchAdjustmentDto
            {
                StoreCode = "BRI",
                ScheduleGuid = "schedule-1",
                PunchType = "ClockIn",
                RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 9, 0, 0),
                Reason = "设置变化后提交",
            };
            var preview = await service.PreviewMyPunchAdjustmentAsync(request);
            Assert.True(preview.Success, preview.Message);
            request.PreviewRevision = preview.Data!.PreviewRevision;
            await _db.Updateable<AttendanceSettings>()
                .SetColumns(item => item.LateGraceMinutes == item.LateGraceMinutes + 1)
                .Where(item => !item.IsDeleted)
                .ExecuteCommandAsync();

            var created = await service.CreateMyPunchAdjustmentAsync(request);

            Assert.False(created.Success);
            Assert.Equal("ADJUSTMENT_PREVIEW_STALE", created.ErrorCode);
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_WithoutPreviewRevision_ReturnsStale()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateMyPunchAdjustmentAsync(new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 9, 0, 0),
                    Reason = "未先预览",
                });

            Assert.False(result.Success);
            Assert.Equal("ADJUSTMENT_PREVIEW_STALE", result.ErrorCode);
        }

        [Fact]
        public async Task GetPendingApprovalsAsync_PopulatesScheduleWorkDateAndHistoricalOriginalPunchTimeInBatches()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("display-overtime", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active");
            await SeedScheduleAsync("display-missing", "BRI", "staff-user", new DateTime(2026, 5, 19), "Active");
            var originalLocal = new DateTime(2026, 5, 18, 9, 12, 0);
            await _db.Insertable(new AttendancePunch
            {
                PunchGuid = "display-original-punch",
                ScheduleGuid = "display-overtime",
                StoreCode = "BRI",
                UserGuid = "staff-user",
                WorkDate = originalLocal.Date,
                StoreTimeZone = "Australia/Brisbane",
                PunchType = "ClockIn",
                PunchTimeLocal = originalLocal,
                PunchTimeUtc = new DateTime(2026, 5, 17, 23, 12, 0, DateTimeKind.Utc),
                IsDeleted = true,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            await _db.Insertable(new AttendancePunchAdjustment
            {
                AdjustmentGuid = "display-adjustment",
                StoreCode = "BRI",
                UserGuid = "staff-user",
                ScheduleGuid = "display-overtime",
                OriginalPunchGuid = "display-original-punch",
                PunchType = "ClockIn",
                RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 9, 0, 0),
                RequestedPunchTimeUtc = new DateTime(2026, 5, 17, 23, 0, 0, DateTimeKind.Utc),
                Reason = "保留原始原因",
                Status = "Pending",
                RequestedByUserGuid = "staff-user",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                new AttendanceApproval
                {
                    ApprovalGuid = "display-overtime-approval",
                    SourceType = "Overtime",
                    SourceGuid = "display-overtime",
                    StoreCode = "BRI",
                    ApplicantUserGuid = "staff-user",
                    ReviewStatus = "Pending",
                    CandidateOvertimeMinutes = 30,
                    CreatedAt = DateTime.UtcNow,
                },
                new AttendanceApproval
                {
                    ApprovalGuid = "display-missing-approval",
                    SourceType = "MissingClockOut",
                    SourceGuid = "display-missing",
                    StoreCode = "BRI",
                    ApplicantUserGuid = "staff-user",
                    ReviewStatus = "Pending",
                    CreatedAt = DateTime.UtcNow,
                },
                new AttendanceApproval
                {
                    ApprovalGuid = "display-adjustment-approval",
                    SourceType = "PunchAdjustment",
                    SourceGuid = "display-adjustment",
                    StoreCode = "BRI",
                    ApplicantUserGuid = "staff-user",
                    ReviewStatus = "Pending",
                    CreatedAt = DateTime.UtcNow,
                },
            }).ExecuteCommandAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .GetPendingApprovalsAsync(new AttendanceApprovalQueryDto { StoreCode = "BRI" });

            Assert.True(result.Success, result.Message);
            var overtime = Assert.Single(result.Data!, item => item.ApprovalGuid == "display-overtime-approval");
            var missing = Assert.Single(result.Data!, item => item.ApprovalGuid == "display-missing-approval");
            var adjustment = Assert.Single(result.Data!, item => item.ApprovalGuid == "display-adjustment-approval");
            Assert.Equal(new DateTime(2026, 5, 18), overtime.WorkDate);
            Assert.Equal(new DateTime(2026, 5, 19), missing.WorkDate);
            Assert.NotNull(adjustment.Adjustment);
            Assert.Equal(originalLocal, adjustment.Adjustment!.OriginalPunchTimeLocal);
            Assert.Contains("保留原始原因", adjustment.Detail);
        }

        [Fact]
        public async Task PunchAsync_ActiveSchedule_AllowsTwoSegmentsAndTreatsMiddleOutAsBreak()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var firstIn = await service.PunchAsync(CreatePunchRequest("ClockIn", "2026-05-17T22:42:00Z"));
            var breakOut = await service.PunchAsync(CreatePunchRequest("ClockOut", "2026-05-18T02:00:00Z"));
            var secondIn = await service.PunchAsync(CreatePunchRequest("ClockIn", "2026-05-18T03:00:00Z"));
            var finalOut = await service.PunchAsync(CreatePunchRequest("ClockOut", "2026-05-18T07:22:00Z"));
            var overflow = await service.PunchAsync(CreatePunchRequest("ClockIn", "2026-05-18T07:23:00Z"));

            Assert.True(firstIn.Success, firstIn.Message);
            Assert.True(breakOut.Success, breakOut.Message);
            Assert.Equal("Break", breakOut.Data!.Status);
            Assert.True(breakOut.Data.IsBreakBoundary);
            Assert.True(secondIn.Success, secondIn.Message);
            Assert.Equal("Normal", secondIn.Data!.Status);
            Assert.True(finalOut.Success, finalOut.Message);
            Assert.Equal("Final", finalOut.Data!.SegmentStatus);
            Assert.False(overflow.Success);
            Assert.Equal("SEGMENT_LIMIT_REACHED", overflow.ErrorCode);
            var overtime = await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.SourceType == "Overtime");
            Assert.Equal(30, overtime.CandidateOvertimeMinutes);
            Assert.Equal("Pending", overtime.ReviewStatus);
            Assert.All(
                await _db.Queryable<AttendancePunch>().ToListAsync(),
                item => Assert.Equal("schedule-1", item.ScheduleGuid));
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_ManagerOlderThanTwoDays_IsRejected()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("old-schedule", "BRI", "manager-user", new DateTime(2026, 5, 15), "Active");
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("manager-user", "manager", "StoreManager"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "old-schedule",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 15, 9, 0, 0),
                    Reason = "补卡",
                });

            Assert.False(result.Success);
            Assert.Equal("ADJUSTMENT_WINDOW_EXPIRED", result.ErrorCode);
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_ManagerSelfDirect_AutoAppliesFullOvertime()
        {
            await SeedStoreScopeAsync();
            await SeedStoreManagerRoleAsync("manager-user");
            await SeedScheduleAsync("manager-schedule", "BRI", "manager-user", new DateTime(2026, 5, 18), "Active");
            await _db.Insertable(new AttendancePunch
            {
                PunchGuid = "manager-out",
                ScheduleGuid = "manager-schedule",
                StoreCode = "BRI",
                UserGuid = "manager-user",
                WorkDate = new DateTime(2026, 5, 18),
                StoreTimeZone = "Australia/Brisbane",
                PunchType = "ClockOut",
                PunchTimeLocal = new DateTime(2026, 5, 18, 17, 22, 30),
                PunchTimeUtc = new DateTime(2026, 5, 18, 7, 22, 30, DateTimeKind.Utc),
                Status = "Normal",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("manager-user", "manager", "StoreManager"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "manager-schedule",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 8, 42, 0),
                    Reason = "直接补卡",
                });

            Assert.True(result.Success, result.Message);
            Assert.Equal("Applied", result.Data!.Status);
            Assert.True(result.Data.IsManagerSelfDirect);
            var overtime = await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.SourceType == "Overtime");
            Assert.Equal("Approved", overtime.ReviewStatus);
            Assert.Equal(45, overtime.CandidateOvertimeMinutes);
            Assert.Equal(45, overtime.ApprovedOvertimeMinutes);
            Assert.Equal("system", overtime.ReviewerUserGuid);
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_ManagerWithoutAdjustPermission_GoesToReview()
        {
            await SeedStoreScopeAsync();
            await SeedStoreManagerRoleAsync("manager-user");
            await SeedScheduleAsync("manager-schedule", "BRI", "manager-user", new DateTime(2026, 5, 18), "Active");
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            _deniedPermissions.Add(Permissions.Attendance.Punch.AdjustManagedStore);

            var result = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("manager-user", "manager", "StoreManager"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "manager-schedule",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 9, 0, 0),
                    Reason = "忘记打卡",
                });

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal("Pending", result.Data!.Status);
            Assert.False(result.Data.IsManagerSelfDirect);
            Assert.Equal(0, await _db.Queryable<AttendancePunch>().CountAsync());
        }

        [Fact]
        public async Task CreateManagedPunchAdjustmentAsync_补录员工上班_直接生效并记录操作人()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var service = CreateService("manager-user", "manager", "StoreManager");
            var request = new CreateManagedAttendancePunchAdjustmentDto
            {
                UserGuid = "staff-user",
                StoreCode = "BRI",
                ScheduleGuid = "schedule-1",
                PunchType = "ClockIn",
                RequestedPunchTimeUtc = new DateTimeOffset(2026, 5, 17, 23, 0, 0, TimeSpan.Zero),
                Reason = "员工漏打卡",
            };

            var preview = await service.PreviewManagedPunchAdjustmentAsync(request);
            Assert.True(preview.Success, $"{preview.ErrorCode}: {preview.Message}");
            Assert.True(preview.Data!.WouldAutoApprove);
            request.PreviewRevision = preview.Data.PreviewRevision;
            var result = await service.CreateManagedPunchAdjustmentAsync(request);

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal("Applied", result.Data!.Status);
            Assert.Equal("staff-user", result.Data.UserGuid);
            Assert.Equal("manager-user", result.Data.RequestedByUserGuid);
            Assert.Equal("manager-user", result.Data.ReviewedByUserGuid);
            Assert.False(result.Data.IsManagerSelfDirect);
            var punch = await _db.Queryable<AttendancePunch>().SingleAsync();
            Assert.Equal("staff-user", punch.UserGuid);
            Assert.Equal("ManualAdjustment", punch.Source);
            Assert.Equal(result.Data.AdjustmentGuid, punch.AdjustmentGuid);
            Assert.Equal(0, await _db.Queryable<AttendanceApproval>()
                .CountAsync(item => item.SourceType == "PunchAdjustment"));
        }

        [Fact]
        public async Task PreviewManagedPunchAdjustmentAsync_非管理分店_拒绝()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("other-schedule", "OTHER", "staff-user", new DateTime(2026, 5, 18), "Active");
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .PreviewManagedPunchAdjustmentAsync(new CreateManagedAttendancePunchAdjustmentDto
                {
                    UserGuid = "staff-user",
                    StoreCode = "OTHER",
                    ScheduleGuid = "other-schedule",
                    PunchType = "ClockIn",
                    RequestedPunchTimeUtc = new DateTimeOffset(2026, 5, 17, 23, 0, 0, TimeSpan.Zero),
                    Reason = "员工漏打卡",
                });

            Assert.False(result.Success);
            Assert.Equal("FORBIDDEN_STORE", result.ErrorCode);
        }

        [Fact]
        public async Task PreviewManagedPunchAdjustmentAsync_晚于当前时间_拒绝()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            // 布里斯班本地 5-18 12:00，补 17:00 下班属于未来时间。
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 2, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .PreviewManagedPunchAdjustmentAsync(new CreateManagedAttendancePunchAdjustmentDto
                {
                    UserGuid = "staff-user",
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    PunchType = "ClockOut",
                    RequestedPunchTimeUtc = new DateTimeOffset(2026, 5, 18, 7, 0, 0, TimeSpan.Zero),
                    Reason = "员工漏打卡",
                });

            Assert.False(result.Success);
            Assert.Equal("PUNCH_TIME_IN_FUTURE", result.ErrorCode);
        }

        [Fact]
        public async Task PreviewManagedPunchAdjustmentAsync_周三不能再改上周()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("last-sunday", "BRI", "staff-user", new DateTime(2026, 5, 17), "Active");
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 20, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .PreviewManagedPunchAdjustmentAsync(new CreateManagedAttendancePunchAdjustmentDto
                {
                    UserGuid = "staff-user",
                    StoreCode = "BRI",
                    ScheduleGuid = "last-sunday",
                    PunchType = "ClockIn",
                    RequestedPunchTimeUtc = new DateTimeOffset(2026, 5, 16, 23, 0, 0, TimeSpan.Zero),
                    Reason = "员工漏打卡",
                });

            Assert.False(result.Success);
            Assert.Equal("ADJUSTMENT_WINDOW_EXPIRED", result.ErrorCode);
        }

        [Fact]
        public async Task CopyScheduleWeekAsync_CopiesAsDraftAndSkipsOccupiedDays()
        {
            await SeedStoreScopeAsync();
            // 来源周 5-11：员工周一、店长周二；目标周 5-18 员工周一已有 schedule-1。
            await SeedScheduleAsync("src-staff-mon", "BRI", "staff-user", new DateTime(2026, 5, 11), "Active");
            await SeedScheduleAsync("src-manager-tue", "BRI", "manager-user", new DateTime(2026, 5, 12), "Active",
                new TimeSpan(10, 0, 0), new TimeSpan(18, 0, 0));
            await SeedScheduleAsync("src-cancelled", "BRI", "staff-user", new DateTime(2026, 5, 13), "Cancelled");
            await SeedScheduleAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .CopyScheduleWeekAsync(new CopyAttendanceScheduleWeekDto
                {
                    StoreCode = "BRI",
                    SourceWeekStartDate = new DateTime(2026, 5, 11),
                    TargetWeekStartDate = new DateTime(2026, 5, 18),
                });

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal(1, result.Data!.CreatedCount);
            Assert.Equal(1, result.Data.SkippedCount);
            var copied = await _db.Queryable<AttendanceSchedule>()
                .SingleAsync(item => item.UserGuid == "manager-user" && item.WorkDate == new DateTime(2026, 5, 19));
            Assert.Equal("Draft", copied.Status);
            Assert.Equal(new TimeSpan(10, 0, 0), copied.StartTime);
            Assert.Equal(new TimeSpan(18, 0, 0), copied.EndTime);
        }

        [Fact]
        public async Task CopyScheduleWeekAsync_OtherStore_IsForbidden()
        {
            await SeedStoreScopeAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .CopyScheduleWeekAsync(new CopyAttendanceScheduleWeekDto
                {
                    StoreCode = "OTHER",
                    SourceWeekStartDate = new DateTime(2026, 5, 11),
                    TargetWeekStartDate = new DateTime(2026, 5, 18),
                });

            Assert.False(result.Success);
            Assert.Equal(0, await _db.Queryable<AttendanceSchedule>().CountAsync());
        }

        [Fact]
        public async Task GetWeekSchedulesAsync_ApprovedLeave_AnnotatesSchedule()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SeedScheduleAsync("schedule-2", "BRI", "staff-user", new DateTime(2026, 5, 20), "Active");
            await _db.Insertable(new[]
            {
                new AttendanceLeaveRequest
                {
                    LeaveGuid = "leave-approved",
                    StoreCode = "BRI",
                    UserGuid = "staff-user",
                    LeaveType = "SickLeave",
                    StartDate = new DateTime(2026, 5, 18),
                    EndDate = new DateTime(2026, 5, 19),
                    Status = "Approved",
                    CreatedAt = DateTime.UtcNow,
                },
                new AttendanceLeaveRequest
                {
                    LeaveGuid = "leave-pending",
                    StoreCode = "BRI",
                    UserGuid = "staff-user",
                    LeaveType = "AnnualLeave",
                    StartDate = new DateTime(2026, 5, 20),
                    EndDate = new DateTime(2026, 5, 20),
                    Status = "Pending",
                    CreatedAt = DateTime.UtcNow,
                },
            }).ExecuteCommandAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .GetWeekSchedulesAsync(new AttendanceScheduleQueryDto
                {
                    StoreCode = "BRI",
                    WeekStartDate = new DateTime(2026, 5, 18),
                });

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            var onLeave = Assert.Single(result.Data!, item => item.ScheduleGuid == "schedule-1");
            Assert.Equal("SickLeave", onLeave.LeaveType);
            Assert.Equal("leave-approved", onLeave.LeaveGuid);
            var pendingOnly = Assert.Single(result.Data!, item => item.ScheduleGuid == "schedule-2");
            Assert.Null(pendingOnly.LeaveType);
        }

        [Theory]
        [InlineData("2010-05-18", "2026-05-18", 16)]
        [InlineData("2010-05-19", "2026-05-18", 15)]
        [InlineData("2008-05-18", "2026-05-18", null)]
        [InlineData("2008-05-19", "2026-05-18", 17)]
        [InlineData("2030-01-01", "2026-05-18", null)]
        public void ResolveMinorAge_OnlyReturnsAgeUnder18(string birthday, string today, int? expected)
        {
            Assert.Equal(
                expected,
                AttendanceReactService.ResolveMinorAge(DateTime.Parse(birthday), DateTime.Parse(today)));
        }

        [Fact]
        public void ScheduleViewStore_IsImpliedByEditManagedStore()
        {
            // 能编辑排班就能看排班；反向不成立，查看权限不能借此获得编辑权。
            Assert.Contains(
                Permissions.Attendance.Schedule.EditManagedStore,
                Permissions.GetEquivalentPermissionCodes(Permissions.Attendance.Schedule.ViewStore));
            Assert.DoesNotContain(
                Permissions.Attendance.Schedule.ViewStore,
                Permissions.GetEquivalentPermissionCodes(Permissions.Attendance.Schedule.EditManagedStore));
        }

        [Fact]
        public async Task GetStoreEmployeesAsync_ReturnsStoreStaffWithoutContactFields()
        {
            await SeedStoreScopeAsync();
            await _db.Insertable(new Role
            {
                RoleGUID = "store-staff-role",
                RoleName = "StoreStaff",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            await _db.Insertable(new UserRole
            {
                UserRoleGUID = "staff-user-store-staff",
                UserGUID = "staff-user",
                RoleGUID = "store-staff-role",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            await SeedEmployeeProfileAsync("staff-user", EmployeeType.PartTime);
            await _db.Updateable<EmployeeProfile>()
                .SetColumns(item => item.Birthday == new DateTime(2010, 6, 1))
                .Where(item => item.UserGUID == "staff-user")
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 2, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .GetStoreEmployeesAsync("BRI");

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            var employee = Assert.Single(result.Data!);
            Assert.Equal("staff-user", employee.UserGuid);
            Assert.Equal("partTime", employee.EmploymentType);
            Assert.Equal(15, employee.Age);
        }

        [Fact]
        public async Task GetStoreEmployeesAsync_WithoutAnyAttendanceManagementPermission_IsForbidden()
        {
            await SeedStoreScopeAsync();
            foreach (var permission in new[]
            {
                Permissions.Attendance.Schedule.ViewStore,
                Permissions.Attendance.Schedule.EditManagedStore,
                Permissions.Attendance.Leave.ViewManagedStore,
                Permissions.Attendance.Leave.ReviewManagedStore,
                Permissions.Attendance.Admin.View,
            })
            {
                _deniedPermissions.Add(permission);
            }

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .GetStoreEmployeesAsync("BRI");

            Assert.False(result.Success);
            Assert.Equal("FORBIDDEN", result.ErrorCode);
        }

        [Fact]
        public async Task GetMyPunchAdjustmentsAsync_RejectedAdjustment_ReturnsRemarkAndReviewerName()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Updateable<User>()
                .SetColumns(item => item.FullName == "店长 Sean")
                .Where(item => item.UserGUID == "manager-user")
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var created = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("staff-user", "staff", "StoreStaff"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 9, 0, 0),
                    Reason = "忘记打卡",
                });
            Assert.True(created.Success, $"{created.ErrorCode}: {created.Message}");
            var approval = await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.SourceGuid == created.Data!.AdjustmentGuid);
            var rejected = await CreateService("manager-user", "manager", "StoreManager")
                .RejectAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "监控显示 9:31 到店" });
            Assert.True(rejected.Success, $"{rejected.ErrorCode}: {rejected.Message}");

            var mine = await CreateService("staff-user", "staff", "StoreStaff").GetMyPunchAdjustmentsAsync();

            var item = Assert.Single(mine.Data!);
            Assert.Equal("Rejected", item.Status);
            Assert.Equal("监控显示 9:31 到店", item.ReviewRemark);
            Assert.Equal("店长 Sean", item.ReviewedByName);
        }

        [Theory]
        // 2026-05-18 是周一：周一、周二可改上周一（5-11）起，周三起只能改本周。
        [InlineData("2026-05-18", "2026-05-11", true)]
        [InlineData("2026-05-19", "2026-05-11", true)]
        [InlineData("2026-05-18", "2026-05-10", false)]
        [InlineData("2026-05-20", "2026-05-17", false)]
        [InlineData("2026-05-20", "2026-05-18", true)]
        [InlineData("2026-05-24", "2026-05-18", true)]
        [InlineData("2026-05-20", "2026-05-21", false)]
        public void IsWithinManagedAdjustmentWindow_按单周工资周期与周二宽限(
            string today,
            string workDate,
            bool expected)
        {
            Assert.Equal(
                expected,
                AttendanceReactService.IsWithinManagedAdjustmentWindow(
                    DateTime.Parse(workDate),
                    DateTime.Parse(today)));
        }

        [Fact]
        public async Task ApplyPunchAdjustmentAsync_CancelsOriginalPendingPunchApprovalAndItCannotBeApproved()
        {
            await SeedStoreScopeAsync();
            await SeedStoreManagerRoleAsync("manager-user");
            await SeedScheduleAsync("manager-adjustment", "BRI", "manager-user", new DateTime(2026, 5, 18), "Active");
            await _db.Insertable(CreateStoredPunchForSchedule(
                "manager-late-in", "manager-adjustment", "BRI", "manager-user", "ClockIn",
                new DateTime(2026, 5, 18, 9, 10, 0))).ExecuteCommandAsync();
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "manager-late-approval",
                SourceType = "Punch",
                SourceGuid = "manager-late-in",
                StoreCode = "BRI",
                ApplicantUserGuid = "manager-user",
                ReviewStatus = "Pending",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var adjustment = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("manager-user", "manager", "StoreManager"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "manager-adjustment",
                    OriginalPunchGuid = "manager-late-in",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 8, 55, 0),
                    Reason = "修正迟到卡",
                });

            Assert.True(adjustment.Success, adjustment.Message);
            Assert.Equal("Applied", adjustment.Data!.Status);
            Assert.Equal("Cancelled", await _db.Queryable<AttendanceApproval>()
                .Where(item => item.ApprovalGuid == "manager-late-approval")
                .Select(item => item.ReviewStatus)
                .FirstAsync());
            var approvalAttempt = await CreateService("admin-user", "admin", "Admin")
                .ApproveAsync("manager-late-approval", new ReviewAttendanceApprovalDto { ReviewRemark = "不应批准" });
            Assert.False(approvalAttempt.Success);
            Assert.Equal("APPROVAL_ALREADY_REVIEWED", approvalAttempt.ErrorCode);
        }

        [Fact]
        public async Task ApproveAsync_WhenPunchWasSuperseded_CancelsStalePunchApproval()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(new[]
            {
                CreateStoredPunch("stale-punch", "ClockIn", new DateTime(2026, 5, 18, 9, 10, 0)),
                new AttendancePunch
                {
                    PunchGuid = "replacement-punch",
                    ScheduleGuid = "schedule-1",
                    StoreCode = "BRI",
                    UserGuid = "staff-user",
                    WorkDate = new DateTime(2026, 5, 18),
                    StoreTimeZone = "Australia/Brisbane",
                    PunchType = "ClockIn",
                    PunchTimeLocal = new DateTime(2026, 5, 18, 8, 55, 0),
                    PunchTimeUtc = new DateTime(2026, 5, 17, 22, 55, 0, DateTimeKind.Utc),
                    SupersedesPunchGuid = "stale-punch",
                    Status = "Normal",
                    CreatedAt = DateTime.UtcNow,
                },
            }).ExecuteCommandAsync();
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "stale-punch-approval",
                SourceType = "Punch",
                SourceGuid = "stale-punch",
                StoreCode = "BRI",
                ApplicantUserGuid = "staff-user",
                ReviewStatus = "Pending",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync("stale-punch-approval", new ReviewAttendanceApprovalDto { ReviewRemark = "不应批准" });

            Assert.False(result.Success);
            Assert.Equal("PUNCH_APPROVAL_STALE", result.ErrorCode);
            Assert.Equal("Cancelled", await _db.Queryable<AttendanceApproval>()
                .Where(item => item.ApprovalGuid == "stale-punch-approval")
                .Select(item => item.ReviewStatus)
                .FirstAsync());
        }

        [Fact]
        public async Task ApproveAsync_WhenLatePunchIsNoLongerLate_CancelsStalePunchApproval()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(CreateStoredPunch(
                "schedule-changed-late", "ClockIn", new DateTime(2026, 5, 18, 9, 10, 0)))
                .ExecuteCommandAsync();
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "schedule-changed-late-approval",
                SourceType = "Punch",
                SourceGuid = "schedule-changed-late",
                StoreCode = "BRI",
                ApplicantUserGuid = "staff-user",
                ReviewStatus = "Pending",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var revisedStart = new TimeSpan(9, 30, 0);
            await _db.Updateable<AttendanceSchedule>()
                .SetColumns(item => item.StartTime == revisedStart)
                .Where(item => item.ScheduleGuid == "schedule-1")
                .ExecuteCommandAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(
                    "schedule-changed-late-approval",
                    new ReviewAttendanceApprovalDto { ReviewRemark = "不应批准" });

            Assert.False(result.Success);
            Assert.Equal("PUNCH_APPROVAL_STALE", result.ErrorCode);
            Assert.Equal("Cancelled", await _db.Queryable<AttendanceApproval>()
                .Where(item => item.ApprovalGuid == "schedule-changed-late-approval")
                .Select(item => item.ReviewStatus)
                .FirstAsync());
        }

        [Theory]
        [InlineData(false, 5)]
        [InlineData(true, 15)]
        public async Task ApproveAsync_WhenLateRuleOrGraceChangesBeforeClaim_ReReadsSettingsAndCancelsStale(
            bool requireApprovalForLate,
            int lateGraceMinutes)
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await CreateService("staff-user", "staff", "StoreStaff").GetSettingsAsync();
            await _db.Insertable(CreateStoredPunch(
                "settings-changed-late", "ClockIn", new DateTime(2026, 5, 18, 9, 10, 0)))
                .ExecuteCommandAsync();
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "settings-changed-late-approval",
                SourceType = "Punch",
                SourceGuid = "settings-changed-late",
                StoreCode = "BRI",
                ApplicantUserGuid = "staff-user",
                ReviewStatus = "Pending",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            await _db.Updateable<AttendanceSettings>()
                .SetColumns(item => item.RequireApprovalForLate == requireApprovalForLate)
                .SetColumns(item => item.LateGraceMinutes == lateGraceMinutes)
                .Where(item => !item.IsDeleted)
                .ExecuteCommandAsync();
            System.Data.IsolationLevel? observedIsolation = null;
            _db.Aop.OnLogExecuted = (sql, _) =>
            {
                if (sql.Contains("AttendanceSettings", StringComparison.OrdinalIgnoreCase)
                    && _db.Ado.Transaction != null)
                {
                    observedIsolation = _db.Ado.Transaction.IsolationLevel;
                }
            };

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(
                    "settings-changed-late-approval",
                    new ReviewAttendanceApprovalDto { ReviewRemark = "不应批准" });

            Assert.False(result.Success);
            Assert.Equal("PUNCH_APPROVAL_STALE", result.ErrorCode);
            Assert.Equal(System.Data.IsolationLevel.Serializable, observedIsolation);
            Assert.Equal("Cancelled", await _db.Queryable<AttendanceApproval>()
                .Where(item => item.ApprovalGuid == "settings-changed-late-approval")
                .Select(item => item.ReviewStatus)
                .FirstAsync());
        }

        [Fact]
        public async Task ApproveAsync_WhenSegmentLimitChangesBeforeClaim_ReReadsRoleAndUserStore()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(new[]
            {
                CreateStoredPunch("segment-limit-in-1", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)),
                CreateStoredPunch("segment-limit-out-1", "ClockOut", new DateTime(2026, 5, 18, 12, 0, 0)),
                CreateStoredPunch("segment-limit-in-2", "ClockIn", new DateTime(2026, 5, 18, 13, 0, 0)),
                CreateStoredPunch("segment-limit-out-2", "ClockOut", new DateTime(2026, 5, 18, 16, 0, 0)),
            }).ExecuteCommandAsync();
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "segment-limit-early-approval",
                SourceType = "Punch",
                SourceGuid = "segment-limit-out-2",
                StoreCode = "BRI",
                ApplicantUserGuid = "staff-user",
                ReviewStatus = "Pending",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            await SeedStoreManagerRoleAsync("staff-user");
            await _db.Updateable<UserStore>()
                .SetColumns(item => item.IsPrimary == true)
                .Where(item => item.UserStoreGUID == "staff-store-bri")
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 6, 10, 0, DateTimeKind.Utc));

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(
                    "segment-limit-early-approval",
                    new ReviewAttendanceApprovalDto { ReviewRemark = "不应批准" });

            Assert.False(result.Success);
            Assert.Equal("PUNCH_APPROVAL_STALE", result.ErrorCode);
            Assert.Equal("Cancelled", await _db.Queryable<AttendanceApproval>()
                .Where(item => item.ApprovalGuid == "segment-limit-early-approval")
                .Select(item => item.ReviewStatus)
                .FirstAsync());
        }

        [Fact]
        public async Task GetMyTodayAsync_WhenFormerFinalEarlyLeaveBecomesBreak_CancelsPendingApproval()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(new[]
            {
                CreateStoredPunch("former-final-in", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)),
                CreateStoredPunch("former-final-out", "ClockOut", new DateTime(2026, 5, 18, 16, 0, 0)),
                CreateStoredPunch("later-segment-in", "ClockIn", new DateTime(2026, 5, 18, 16, 30, 0)),
                CreateStoredPunch("later-segment-out", "ClockOut", new DateTime(2026, 5, 18, 17, 0, 0)),
            }).ExecuteCommandAsync();
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "former-final-approval",
                SourceType = "Punch",
                SourceGuid = "former-final-out",
                StoreCode = "BRI",
                ApplicantUserGuid = "staff-user",
                ReviewStatus = "Pending",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");

            Assert.True(result.Success, result.Message);
            Assert.Equal("Cancelled", await _db.Queryable<AttendanceApproval>()
                .Where(item => item.ApprovalGuid == "former-final-approval")
                .Select(item => item.ReviewStatus)
                .FirstAsync());
        }

        [Fact]
        public async Task ApproveAsync_WhenMissingClockOutWasRestored_CancelsStaleApproval()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(new[]
            {
                CreateStoredPunch("restored-in", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)),
                CreateStoredPunch("restored-out", "ClockOut", new DateTime(2026, 5, 18, 17, 0, 0)),
            }).ExecuteCommandAsync();
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "restored-missing-approval",
                SourceType = "MissingClockOut",
                SourceGuid = "schedule-1",
                StoreCode = "BRI",
                ApplicantUserGuid = "staff-user",
                ReviewStatus = "Pending",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync("restored-missing-approval", new ReviewAttendanceApprovalDto { ReviewRemark = "不应批准" });

            Assert.False(result.Success);
            Assert.Equal("MISSING_CLOCK_OUT_RESOLVED", result.ErrorCode);
            Assert.Equal("Cancelled", await _db.Queryable<AttendanceApproval>()
                .Where(item => item.ApprovalGuid == "restored-missing-approval")
                .Select(item => item.ReviewStatus)
                .FirstAsync());
        }

        [Fact]
        public async Task GetMyTodayAsync_ManagerWithAnyActiveManagementRelation_GetsThreeSegmentLimit()
        {
            await SeedStoreScopeAsync();
            await SeedStoreManagerRoleAsync("manager-user");
            await _db.Updateable<UserStore>()
                .SetColumns(item => item.IsPrimary == false)
                .Where(item => item.UserStoreGUID == "manager-store-bri")
                .ExecuteCommandAsync();
            await SeedManagerStoreAccessAsync("manager-user", "store-other");
            await SeedScheduleAsync("manager-schedule", "BRI", "manager-user", new DateTime(2026, 5, 18), "Active");
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");

            Assert.True(result.Success, result.Message);
            Assert.Equal(3, Assert.Single(result.Data!.Schedules).SegmentLimit);
        }

        [Fact]
        public async Task GetMyTodayAsync_SelectedStore_ReturnsAllRelatedStorePunchStates()
        {
            await SeedStoreScopeAsync();
            await _db.Insertable(new UserStore
            {
                UserStoreGUID = "staff-store-other",
                UserGUID = "staff-user",
                StoreGUID = "store-other",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            await SeedScheduleAsync();
            await SeedScheduleAsync("other-schedule", "OTHER", "staff-user", new DateTime(2026, 5, 18), "Active");
            await _db.Insertable(new AttendancePunch
            {
                PunchGuid = "other-open",
                ScheduleGuid = "other-schedule",
                StoreCode = "OTHER",
                UserGuid = "staff-user",
                WorkDate = new DateTime(2026, 5, 18),
                StoreTimeZone = "Australia/Brisbane",
                PunchType = "ClockIn",
                PunchTimeLocal = new DateTime(2026, 5, 18, 9, 0, 0),
                PunchTimeUtc = new DateTime(2026, 5, 17, 23, 0, 0, DateTimeKind.Utc),
                Status = "Normal",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");
            await CreateService("staff-user", "staff", "StoreStaff")
                .GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");

            Assert.True(result.Success, result.Message);
            Assert.Equal("schedule-1", Assert.Single(result.Data!.Schedules).ScheduleGuid);
            Assert.Contains(result.Data.StorePunchStates, item => item.StoreCode == "BRI");
            var otherState = Assert.Single(result.Data.StorePunchStates, item => item.StoreCode == "OTHER");
            Assert.True(otherState.HasMissingClockOut);
            Assert.Equal("MissingClockOut", otherState.State);
            Assert.Equal(1, await _db.Queryable<AttendanceApproval>()
                .CountAsync(item => item.SourceType == "MissingClockOut"
                    && item.SourceGuid == "other-schedule"));
        }

        [Fact]
        public async Task GetMyTodayAsync_AdminRequestedStoreIncludesRequestedScheduleOutsideRelatedScope()
        {
            await SeedStoreScopeAsync();
            await SeedManagerStoreAccessAsync("admin-user", "store-other");
            await SeedScheduleAsync("admin-bri", "BRI", "admin-user", new DateTime(2026, 5, 18), "Active");
            await SeedScheduleAsync("admin-other", "OTHER", "admin-user", new DateTime(2026, 5, 18), "Active");
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var service = CreateService("admin-user", "admin", "Admin");
            var result = await service.GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");
            var allStoresResult = await service.GetMyTodayAsync(new DateTime(2026, 5, 18));

            Assert.True(result.Success, result.Message);
            Assert.Equal("admin-bri", Assert.Single(result.Data!.Schedules).ScheduleGuid);
            Assert.DoesNotContain(result.Data.StorePunchStates, item => item.StoreCode == "BRI");
            Assert.Contains(result.Data.StorePunchStates, item => item.StoreCode == "OTHER");
            Assert.True(allStoresResult.Success, allStoresResult.Message);
            Assert.Equal(2, allStoresResult.Data!.Schedules.Count);
        }

        [Fact]
        public async Task GetMyTodayAsync_SameStoreMultipleSchedules_AggregatesAnomalyStateOnce()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("early-complete", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(6, 0, 0), new TimeSpan(8, 0, 0));
            await SeedScheduleAsync("late-missing", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0));
            await _db.Insertable(new[]
            {
                CreateStoredPunchForSchedule("early-in", "early-complete", "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 6, 0, 0)),
                CreateStoredPunchForSchedule("early-out", "early-complete", "BRI", "staff-user", "ClockOut", new DateTime(2026, 5, 18, 8, 0, 0)),
                CreateStoredPunchForSchedule("late-in", "late-missing", "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)),
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");

            Assert.True(result.Success, result.Message);
            var state = Assert.Single(result.Data!.StorePunchStates, item => item.StoreCode == "BRI");
            Assert.True(state.HasOpenSegment);
            Assert.True(state.HasMissingClockOut);
            Assert.Equal("MissingClockOut", state.State);
            Assert.Equal("late-missing", state.ScheduleGuid);
        }

        [Fact]
        public async Task GetMyTodayAsync_BoundaryPunches_IncludeAuthoritativeExceptionMinutes()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(new[]
            {
                CreateStoredPunch("minutes-in", "ClockIn", new DateTime(2026, 5, 18, 9, 7, 0)),
                CreateStoredPunch("minutes-out", "ClockOut", new DateTime(2026, 5, 18, 16, 41, 0)),
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");

            Assert.True(result.Success, result.Message);
            var segment = Assert.Single(Assert.Single(result.Data!.Schedules).Segments);
            Assert.Equal(7, segment.ClockIn!.LateMinutes);
            Assert.Equal(0, segment.ClockIn.EarlyArrivalMinutes);
            Assert.Equal(19, segment.ClockOut!.EarlyLeaveMinutes);
            Assert.Equal(0, segment.ClockOut.LateDepartureMinutes);
        }

        [Fact]
        public async Task GetMyTodayAsync_EarlyBreakWithoutReturn_FinalizesAndReconcilesApprovalIdempotently()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(new[]
            {
                CreateStoredPunch("early-in", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)),
                CreateStoredPunch("early-out", "ClockOut", new DateTime(2026, 5, 18, 12, 0, 0)),
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var first = await service.GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");
            var second = await service.GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");

            Assert.True(first.Success, first.Message);
            Assert.True(second.Success, second.Message);
            var schedule = Assert.Single(first.Data!.Schedules);
            Assert.Equal("Completed", schedule.ScheduleState);
            Assert.Equal("EarlyLeave", Assert.Single(schedule.Segments).ClockOut!.Status);
            Assert.Equal("EarlyLeave", Assert.Single(first.Data.Punches, item => item.PunchGuid == "early-out").Status);
            Assert.Equal(1, await _db.Queryable<AttendanceApproval>()
                .CountAsync(item => item.SourceType == "Punch" && item.SourceGuid == "early-out"));
        }

        [Fact]
        public async Task GetMyTodayAsync_FinalEarlyLeaveWithReviewedApproval_DoesNotReopenPending()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(new[]
            {
                CreateStoredPunch("reviewed-in", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)),
                CreateStoredPunch("reviewed-out", "ClockOut", new DateTime(2026, 5, 18, 12, 0, 0)),
            }).ExecuteCommandAsync();
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "reviewed-early-leave",
                SourceType = "Punch",
                SourceGuid = "reviewed-out",
                StoreCode = "BRI",
                ApplicantUserGuid = "staff-user",
                ReviewerUserGuid = "manager-user",
                ReviewStatus = "Approved",
                ReviewedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, await _db.Queryable<AttendanceApproval>()
                .CountAsync(item => item.SourceType == "Punch" && item.SourceGuid == "reviewed-out"));
            Assert.Equal(0, await _db.Queryable<AttendanceApproval>()
                .CountAsync(item => item.SourceType == "Punch"
                    && item.SourceGuid == "reviewed-out"
                    && item.ReviewStatus == "Pending"));
        }

        [Fact]
        public async Task PreviewMyPunchAdjustmentAsync_WhenAnotherStoreSegmentOverlaps_ReturnsConflict()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(new AttendanceSchedule
            {
                ScheduleGuid = "other-schedule",
                StoreCode = "OTHER",
                UserGuid = "staff-user",
                WorkDate = new DateTime(2026, 5, 18),
                StartTime = new TimeSpan(10, 0, 0),
                EndTime = new TimeSpan(11, 0, 0),
                Status = "Active",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                CreateStoredPunch("bri-out", "ClockOut", new DateTime(2026, 5, 18, 10, 30, 0)),
                new AttendancePunch
                {
                    PunchGuid = "other-in", ScheduleGuid = "other-schedule", StoreCode = "OTHER",
                    UserGuid = "staff-user", WorkDate = new DateTime(2026, 5, 18),
                    StoreTimeZone = "Australia/Brisbane", PunchType = "ClockIn",
                    PunchTimeLocal = new DateTime(2026, 5, 18, 10, 0, 0),
                    PunchTimeUtc = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc), Status = "Normal",
                },
                new AttendancePunch
                {
                    PunchGuid = "other-out", ScheduleGuid = "other-schedule", StoreCode = "OTHER",
                    UserGuid = "staff-user", WorkDate = new DateTime(2026, 5, 18),
                    StoreTimeZone = "Australia/Brisbane", PunchType = "ClockOut",
                    PunchTimeLocal = new DateTime(2026, 5, 18, 11, 0, 0),
                    PunchTimeUtc = new DateTime(2026, 5, 18, 1, 0, 0, DateTimeKind.Utc), Status = "Normal",
                },
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .PreviewMyPunchAdjustmentAsync(new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 9, 30, 0),
                    Reason = "补卡",
                });

            Assert.False(result.Success);
            Assert.Equal("PUNCH_TIME_OVERLAP", result.ErrorCode);
        }

        [Fact]
        public async Task PreviewMyPunchAdjustmentAsync_SeesAdjacentPerthBusinessDateForCrossStoreOverlap()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync(
                "bri-boundary", "BRI", "staff-user", new DateTime(2026, 5, 19), "Active",
                TimeSpan.Zero, new TimeSpan(4, 0, 0));
            await SeedScheduleAsync(
                "perth-boundary", "OTHER", "staff-user", new DateTime(2026, 5, 18), "Active",
                new TimeSpan(22, 0, 0), new TimeSpan(23, 30, 0));
            await _db.Insertable(new[]
            {
                CreateStoredPunchForSchedule(
                    "bri-boundary-in", "bri-boundary", "BRI", "staff-user", "ClockIn",
                    new DateTime(2026, 5, 19, 0, 0, 0)),
                new AttendancePunch
                {
                    PunchGuid = "perth-boundary-in",
                    ScheduleGuid = "perth-boundary",
                    StoreCode = "OTHER",
                    UserGuid = "staff-user",
                    WorkDate = new DateTime(2026, 5, 18),
                    StoreTimeZone = "Australia/Perth",
                    PunchType = "ClockIn",
                    PunchTimeLocal = new DateTime(2026, 5, 18, 22, 0, 0),
                    PunchTimeUtc = new DateTime(2026, 5, 18, 14, 0, 0, DateTimeKind.Utc),
                    Status = "Normal",
                    CreatedAt = DateTime.UtcNow,
                },
                new AttendancePunch
                {
                    PunchGuid = "perth-boundary-out",
                    ScheduleGuid = "perth-boundary",
                    StoreCode = "OTHER",
                    UserGuid = "staff-user",
                    WorkDate = new DateTime(2026, 5, 18),
                    StoreTimeZone = "Australia/Perth",
                    PunchType = "ClockOut",
                    PunchTimeLocal = new DateTime(2026, 5, 18, 23, 30, 0),
                    PunchTimeUtc = new DateTime(2026, 5, 18, 15, 30, 0, DateTimeKind.Utc),
                    Status = "Normal",
                    CreatedAt = DateTime.UtcNow,
                },
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 15, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .PreviewMyPunchAdjustmentAsync(new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "bri-boundary",
                    PunchType = "ClockOut",
                    // Brisbane 01:00 = UTC 15:00；Perth 的前一业务日班段尚未结束。
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 19, 1, 0, 0),
                    Reason = "跨时区边界补卡",
                });

            Assert.False(result.Success);
            Assert.Equal("PUNCH_TIME_OVERLAP", result.ErrorCode);
        }

        [Fact]
        public async Task ApproveAsync_WhenApplicantReviewsOwnOvertime_ReturnsForbidden()
        {
            await SeedStoreScopeAsync();
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "self-overtime",
                SourceType = "Overtime",
                SourceGuid = "schedule-1",
                StoreCode = "BRI",
                ApplicantUserGuid = "manager-user",
                ReviewStatus = "Pending",
                CandidateOvertimeMinutes = 30,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync("self-overtime", new ReviewAttendanceApprovalDto
                {
                    ApprovedOvertimeMinutes = 15,
                    ReviewRemark = "调整",
                });

            Assert.False(result.Success);
            Assert.Equal("SELF_REVIEW_FORBIDDEN", result.ErrorCode);
        }

        [Fact]
        public async Task PunchAsync_TwoSchedulesAtBoundary_ClosesOpenMorningSchedule()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("morning", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(9, 0, 0), new TimeSpan(12, 0, 0));
            await SeedScheduleAsync("afternoon", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(13, 0, 0), new TimeSpan(17, 0, 0));
            await _db.Insertable(CreateStoredPunchForSchedule(
                "morning-in", "morning", "BRI", "staff-user", "ClockIn",
                new DateTime(2026, 5, 18, 9, 0, 0))).ExecuteCommandAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.PunchAsync(CreatePunchRequest("ClockOut", "2026-05-18T02:00:00Z"));

            Assert.True(result.Success, result.Message);
            Assert.Equal("ClockOut", result.Data!.PunchType);
            Assert.Equal("morning", result.Data.ScheduleGuid);
        }

        [Fact]
        public async Task PunchAsync_TwoSchedulesAtBoundary_SelectsUnfinishedAfternoonAfterMorningCompleted()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("morning", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(9, 0, 0), new TimeSpan(12, 0, 0));
            await SeedScheduleAsync("afternoon", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(13, 0, 0), new TimeSpan(17, 0, 0));
            await _db.Insertable(new[]
            {
                CreateStoredPunchForSchedule("morning-in", "morning", "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)),
                CreateStoredPunchForSchedule("morning-out", "morning", "BRI", "staff-user", "ClockOut", new DateTime(2026, 5, 18, 12, 0, 0)),
            }).ExecuteCommandAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.PunchAsync(CreatePunchRequest("ClockIn", "2026-05-18T02:00:00Z"));

            Assert.True(result.Success, result.Message);
            Assert.Equal("ClockIn", result.Data!.PunchType);
            Assert.Equal("afternoon", result.Data.ScheduleGuid);
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_WhenOriginalHasPendingAdjustment_ReturnsConflict()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(CreateStoredPunch("original-in", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)))
                .ExecuteCommandAsync();
            await _db.Insertable(new AttendancePunchAdjustment
            {
                AdjustmentGuid = "existing-adjustment",
                StoreCode = "BRI",
                UserGuid = "staff-user",
                ScheduleGuid = "schedule-1",
                OriginalPunchGuid = "original-in",
                PunchType = "ClockIn",
                RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 8, 55, 0),
                RequestedPunchTimeUtc = new DateTime(2026, 5, 17, 22, 55, 0, DateTimeKind.Utc),
                Reason = "已有申请",
                Status = "Pending",
                RequestedByUserGuid = "staff-user",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("staff-user", "staff", "StoreStaff")
                .CreateMyPunchAdjustmentAsync(new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI",
                    ScheduleGuid = "schedule-1",
                    OriginalPunchGuid = "original-in",
                    PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 8, 50, 0),
                    Reason = "重复申请",
                });

            Assert.False(result.Success);
            Assert.Equal("ADJUSTMENT_ALREADY_EXISTS", result.ErrorCode);
        }

        [Fact]
        public async Task ApproveAsync_WhenOriginalWasSupersededAfterSubmission_RollsBackClaim()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(CreateStoredPunch("original-in", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)))
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var created = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("staff-user", "staff", "StoreStaff"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI", ScheduleGuid = "schedule-1", OriginalPunchGuid = "original-in",
                    PunchType = "ClockIn", RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 8, 55, 0), Reason = "调整",
                });
            Assert.True(created.Success, created.Message);
            await _db.Insertable(new AttendancePunch
            {
                PunchGuid = "competing-adjustment", ScheduleGuid = "schedule-1", StoreCode = "BRI",
                UserGuid = "staff-user", WorkDate = new DateTime(2026, 5, 18), StoreTimeZone = "Australia/Brisbane",
                PunchType = "ClockIn", PunchTimeLocal = new DateTime(2026, 5, 18, 8, 58, 0),
                PunchTimeUtc = new DateTime(2026, 5, 17, 22, 58, 0, DateTimeKind.Utc), Status = "Normal",
                Source = "ManualAdjustment", SupersedesPunchGuid = "original-in", AdjustmentGuid = "other-adjustment",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var approval = await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.SourceType == "PunchAdjustment");

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "同意" });

            Assert.False(result.Success);
            Assert.Equal("PUNCH_ALREADY_ADJUSTED", result.ErrorCode);
            Assert.Equal("Pending", await _db.Queryable<AttendanceApproval>()
                .Where(item => item.ApprovalGuid == approval.ApprovalGuid)
                .Select(item => item.ReviewStatus).FirstAsync());
        }

        [Fact]
        public async Task ApproveAsync_ConcurrentReview_OnlyOneClaimsPendingApproval()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(CreateStoredPunch(
                "concurrent-review-open", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)))
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "concurrent-review", SourceType = "MissingClockOut", SourceGuid = "schedule-1",
                StoreCode = "BRI", ApplicantUserGuid = "staff-user", ReviewStatus = "Pending", CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var service = CreateService("manager-user", "manager", "StoreManager");

            var results = await Task.WhenAll(
                service.ApproveAsync("concurrent-review", new ReviewAttendanceApprovalDto { ReviewRemark = "处理1" }),
                service.ApproveAsync("concurrent-review", new ReviewAttendanceApprovalDto { ReviewRemark = "处理2" }));

            Assert.Equal(1, results.Count(item => item.Success));
            Assert.Equal(1, results.Count(item => item.ErrorCode == "APPROVAL_ALREADY_REVIEWED"));
        }

        [Fact]
        public async Task ApproveAsync_PartialOvertimeApproval_DoesNotRegenerateProcessedMinutes()
        {
            var approval = await SeedCompletedOvertimeAndGetPendingAsync("partial-terminal");

            var reviewed = await CreateService("admin-user", "admin", "Admin")
                .ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto
                {
                    ApprovedOvertimeMinutes = 15,
                    ReviewRemark = "仅批准十五分钟",
                });
            var refreshed = await CreateService("staff-user", "staff", "StoreStaff")
                .GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");

            Assert.True(reviewed.Success, reviewed.Message);
            Assert.True(refreshed.Success, refreshed.Message);
            var approvals = await _db.Queryable<AttendanceApproval>()
                .Where(item => item.SourceType == "Overtime" && item.SourceGuid == "partial-terminal")
                .ToListAsync();
            var terminal = Assert.Single(approvals);
            Assert.Equal("Approved", terminal.ReviewStatus);
            Assert.Equal(30, terminal.CandidateOvertimeMinutes);
            Assert.Equal(15, terminal.ApprovedOvertimeMinutes);
            Assert.DoesNotContain(approvals, item => item.ReviewStatus == "Pending");
        }

        [Fact]
        public async Task RejectAsync_Overtime_DoesNotRegenerateProcessedMinutes()
        {
            var approval = await SeedCompletedOvertimeAndGetPendingAsync("rejected-terminal");

            var reviewed = await CreateService("admin-user", "admin", "Admin")
                .RejectAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto
                {
                    ReviewRemark = "不批准加班",
                });
            var refreshed = await CreateService("staff-user", "staff", "StoreStaff")
                .GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");

            Assert.True(reviewed.Success, reviewed.Message);
            Assert.True(refreshed.Success, refreshed.Message);
            var approvals = await _db.Queryable<AttendanceApproval>()
                .Where(item => item.SourceType == "Overtime" && item.SourceGuid == "rejected-terminal")
                .ToListAsync();
            var terminal = Assert.Single(approvals);
            Assert.Equal("Rejected", terminal.ReviewStatus);
            Assert.Equal(30, terminal.CandidateOvertimeMinutes);
            Assert.DoesNotContain(approvals, item => item.ReviewStatus == "Pending");
        }

        [Fact]
        public async Task ApproveAsync_WhenOvertimeCandidateChanged_RejectsStaleApprovalInsideDailyLock()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("stale-overtime", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active");
            await _db.Insertable(new[]
            {
                CreateStoredPunchForSchedule("stale-in", "stale-overtime", "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)),
                CreateStoredPunchForSchedule("stale-out", "stale-overtime", "BRI", "staff-user", "ClockOut", new DateTime(2026, 5, 18, 17, 16, 0)),
            }).ExecuteCommandAsync();
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "stale-approval",
                SourceType = "Overtime",
                SourceGuid = "stale-overtime",
                StoreCode = "BRI",
                ApplicantUserGuid = "staff-user",
                ReviewStatus = "Pending",
                CandidateOvertimeMinutes = 30,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("admin-user", "admin", "Admin")
                .ApproveAsync("stale-approval", new ReviewAttendanceApprovalDto
                {
                    ApprovedOvertimeMinutes = 30,
                    ReviewRemark = "按旧候选批准",
                });

            Assert.False(result.Success);
            Assert.Equal("OVERTIME_CANDIDATE_CHANGED", result.ErrorCode);
            var stored = await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.ApprovalGuid == "stale-approval");
            Assert.Equal("Pending", stored.ReviewStatus);
            Assert.Null(stored.ApprovedOvertimeMinutes);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ApproveAsync_WhenOvertimeScheduleIsInactiveOrDeleted_CancelsStaleApproval(
            bool isDeleted)
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync(
                "inactive-overtime",
                "BRI",
                "staff-user",
                new DateTime(2026, 5, 18),
                isDeleted ? "Active" : "Cancelled");
            if (isDeleted)
            {
                await _db.Updateable<AttendanceSchedule>()
                    .SetColumns(item => item.IsDeleted == true)
                    .Where(item => item.ScheduleGuid == "inactive-overtime")
                    .ExecuteCommandAsync();
            }
            await _db.Insertable(new[]
            {
                CreateStoredPunchForSchedule(
                    "inactive-overtime-in", "inactive-overtime", "BRI", "staff-user", "ClockIn",
                    new DateTime(2026, 5, 18, 9, 0, 0)),
                CreateStoredPunchForSchedule(
                    "inactive-overtime-out", "inactive-overtime", "BRI", "staff-user", "ClockOut",
                    new DateTime(2026, 5, 18, 17, 16, 0)),
            }).ExecuteCommandAsync();
            await _db.Insertable(new AttendanceApproval
            {
                ApprovalGuid = "inactive-overtime-approval",
                SourceType = "Overtime",
                SourceGuid = "inactive-overtime",
                StoreCode = "BRI",
                ApplicantUserGuid = "staff-user",
                ReviewStatus = "Pending",
                CandidateOvertimeMinutes = 15,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));

            var result = await CreateService("admin-user", "admin", "Admin")
                .ApproveAsync(
                    "inactive-overtime-approval",
                    new ReviewAttendanceApprovalDto
                    {
                        ApprovedOvertimeMinutes = 15,
                        ReviewRemark = "不应批准",
                    });

            Assert.False(result.Success);
            Assert.Equal("OVERTIME_APPROVAL_STALE", result.ErrorCode);
            Assert.Equal("Cancelled", await _db.Queryable<AttendanceApproval>()
                .Where(item => item.ApprovalGuid == "inactive-overtime-approval")
                .Select(item => item.ReviewStatus)
                .FirstAsync());
        }

        [Fact]
        public async Task UpdateScheduleAsync_WhenScheduleIsCancelled_CancelsAllPendingDerivedApprovals()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await SeedPendingScheduleDerivedApprovalsAsync("schedule-1", "cancel-schedule-punch");

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .UpdateScheduleAsync("schedule-1", new UpdateAttendanceScheduleDto
                {
                    WorkDate = new DateTime(2026, 5, 18),
                    StartTime = new TimeSpan(9, 0, 0),
                    EndTime = new TimeSpan(17, 0, 0),
                    Status = "Cancelled",
                });

            Assert.True(result.Success, result.Message);
            Assert.All(
                await _db.Queryable<AttendanceApproval>()
                    .Where(item => item.ApprovalGuid.StartsWith("schedule-1-derived-"))
                    .ToListAsync(),
                item => Assert.Equal("Cancelled", item.ReviewStatus));
        }

        [Fact]
        public async Task DeleteScheduleAsync_CancelsAllPendingDerivedApprovals()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("delete-schedule", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active");
            await SeedPendingScheduleDerivedApprovalsAsync("delete-schedule", "delete-schedule-punch");

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .DeleteScheduleAsync("delete-schedule");

            Assert.True(result.Success, result.Message);
            Assert.All(
                await _db.Queryable<AttendanceApproval>()
                    .Where(item => item.ApprovalGuid.StartsWith("delete-schedule-derived-"))
                    .ToListAsync(),
                item => Assert.Equal("Cancelled", item.ReviewStatus));
        }

        [Fact]
        public async Task PunchAsync_CrossStoreOpen_AllowsSecondStoreButRejectsOverlappingClose()
        {
            await SeedStoreScopeAsync();
            await _db.Insertable(new UserStore
            {
                UserStoreGUID = "staff-store-other",
                UserGUID = "staff-user",
                StoreGUID = "store-other",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            await SeedScheduleAsync("bri-open", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active");
            await SeedScheduleAsync("other-shift", "OTHER", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(10, 0, 0), new TimeSpan(17, 0, 0));
            await _db.Insertable(CreateStoredPunchForSchedule(
                "bri-in", "bri-open", "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)))
                .ExecuteCommandAsync();
            await SeedAttendanceSigningKeyAsync("K2", "OTHER", "POS-002");
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var otherIn = await service.PunchAsync(CreatePunchRequest(
                "ClockIn", "2026-05-18T00:00:00Z", storeCode: "OTHER", kid: "K2", deviceCode: "POS-002"));
            var otherOut = await service.PunchAsync(CreatePunchRequest(
                "ClockOut", "2026-05-18T01:00:00Z", storeCode: "OTHER", kid: "K2", deviceCode: "POS-002"));
            var briOut = await service.PunchAsync(CreatePunchRequest(
                "ClockOut", "2026-05-18T08:00:00Z"));

            Assert.True(otherIn.Success, otherIn.Message);
            Assert.Equal("ClockIn", otherIn.Data!.PunchType);
            Assert.True(otherOut.Success, otherOut.Message);
            Assert.False(briOut.Success);
            Assert.Equal("CROSS_STORE_PUNCH_OVERLAP", briOut.ErrorCode);
            Assert.Equal(1, await _db.Queryable<AttendancePunch>()
                .CountAsync(item => !item.IsDeleted && item.ScheduleGuid == "bri-open"));
            var today = await service.GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");
            var briState = Assert.Single(today.Data!.StorePunchStates, item => item.StoreCode == "BRI");
            Assert.True(briState.HasMissingClockOut);
        }

        [Fact]
        public void AttendanceDailyMutationLock_DifferentStoresAndBusinessDatesShareEmployeeResource()
        {
            Assert.Equal(
                AttendanceDailyMutationLock.BuildResource("staff-user", "BRI", new DateTime(2026, 5, 19)),
                AttendanceDailyMutationLock.BuildResource("staff-user", "OTHER", new DateTime(2026, 5, 18)));
        }

        [Fact]
        public async Task ManagerDirectEarlyOvertime_DoesNotAutoApproveLaterNormalLateOvertime()
        {
            await SeedStoreScopeAsync();
            await SeedStoreManagerRoleAsync("manager-user");
            await SeedScheduleAsync("manager-schedule", "BRI", "manager-user", new DateTime(2026, 5, 18), "Active");
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 17, 22, 42, 0, DateTimeKind.Utc));
            var service = CreateService("manager-user", "manager", "StoreManager");

            var direct = await SubmitPunchAdjustmentAfterPreviewAsync(service, new CreateAttendancePunchAdjustmentDto
            {
                StoreCode = "BRI", ScheduleGuid = "manager-schedule", PunchType = "ClockIn",
                RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 8, 42, 0), Reason = "直接补早班卡",
            });
            var finalOut = await service.PunchAsync(CreatePunchRequest("ClockOut", "2026-05-18T07:22:30Z"));

            Assert.True(direct.Success, direct.Message);
            Assert.True(finalOut.Success, finalOut.Message);
            var overtime = await _db.Queryable<AttendanceApproval>()
                .Where(item => item.SourceType == "Overtime" && item.SourceGuid == "manager-schedule")
                .OrderBy(item => item.CreatedAt).ToListAsync();
            Assert.Contains(overtime, item => item.ReviewStatus == "Approved" && item.ApprovedOvertimeMinutes == 15);
            Assert.Contains(overtime, item => item.ReviewStatus == "Pending" && item.CandidateOvertimeMinutes == 30);

            var today = await service.GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");
            var schedule = Assert.Single(today.Data!.Schedules);
            Assert.Equal(45, schedule.CandidateOvertimeMinutes);
            Assert.Equal(15, schedule.ApprovedOvertimeMinutes);
            Assert.Equal("Pending", schedule.OvertimeApprovalStatus);

            var pending = overtime.Single(item => item.ReviewStatus == "Pending");
            var approved = await CreateService("admin-user", "admin", "Admin")
                .ApproveAsync(pending.ApprovalGuid, new ReviewAttendanceApprovalDto
                {
                    ApprovedOvertimeMinutes = 30,
                    ReviewRemark = "批准剩余加班",
                });
            Assert.True(approved.Success, approved.Message);
            Assert.Equal(1, await _db.Queryable<AttendanceSchedule>().CountAsync(item =>
                !item.IsDeleted && item.Status == "Active" && item.ScheduleGuid == "manager-schedule"));
            var afterApproval = await CreateService("manager-user", "manager", "StoreManager")
                .GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");
            Assert.True(afterApproval.Success, afterApproval.Message);
            var approvedSchedule = Assert.Single(afterApproval.Data!.Schedules);
            Assert.Equal(45, approvedSchedule.CandidateOvertimeMinutes);
            Assert.Equal(45, approvedSchedule.ApprovedOvertimeMinutes);
            Assert.Equal("Approved", approvedSchedule.OvertimeApprovalStatus);
        }

        [Fact]
        public async Task GetMyTodayAsync_WhenAnomalyDisappears_CancelsPendingDerivedApprovals()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(CreateStoredPunch("open-in", "ClockIn", new DateTime(2026, 5, 18, 8, 40, 0)))
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            await service.GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");
            await _db.Insertable(CreateStoredPunch("final-out", "ClockOut", new DateTime(2026, 5, 18, 17, 0, 0)))
                .ExecuteCommandAsync();
            await _db.Updateable<AttendancePunch>()
                .SetColumns(item => item.PunchTimeLocal == new DateTime(2026, 5, 18, 9, 0, 0))
                .SetColumns(item => item.PunchTimeUtc == new DateTime(2026, 5, 17, 23, 0, 0, DateTimeKind.Utc))
                .Where(item => item.PunchGuid == "open-in")
                .ExecuteCommandAsync();

            await service.GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");

            Assert.Equal(0, await _db.Queryable<AttendanceApproval>()
                .CountAsync(item => item.ReviewStatus == "Pending"
                    && (item.SourceType == "MissingClockOut" || item.SourceType == "Overtime")));
            Assert.True(await _db.Queryable<AttendanceApproval>()
                .AnyAsync(item => item.ReviewStatus == "Cancelled"
                    && (item.SourceType == "MissingClockOut" || item.SourceType == "Overtime")));
        }

        [Fact]
        public async Task GetMyTodayAsync_WhenMissingClockOutReturns_CreatesNewPendingApproval()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(CreateStoredPunch("reopen-in", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)))
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var service = CreateService("staff-user", "staff", "StoreStaff");

            await service.GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");
            await _db.Insertable(CreateStoredPunch("temporary-out", "ClockOut", new DateTime(2026, 5, 18, 17, 0, 0)))
                .ExecuteCommandAsync();
            await service.GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");
            await _db.Updateable<AttendancePunch>()
                .SetColumns(item => item.IsDeleted == true)
                .Where(item => item.PunchGuid == "temporary-out")
                .ExecuteCommandAsync();

            await service.GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");

            Assert.Equal(1, await _db.Queryable<AttendanceApproval>().CountAsync(item =>
                item.SourceType == "MissingClockOut"
                && item.SourceGuid == "schedule-1"
                && item.ReviewStatus == "Cancelled"));
            Assert.Equal(1, await _db.Queryable<AttendanceApproval>().CountAsync(item =>
                item.SourceType == "MissingClockOut"
                && item.SourceGuid == "schedule-1"
                && item.ReviewStatus == "Pending"));
        }

        [Fact]
        public async Task PunchAsync_SameStoreSchedulesShareDailySegmentLimit()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("morning", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(8, 0, 0), new TimeSpan(10, 0, 0));
            await SeedScheduleAsync("afternoon", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(11, 0, 0), new TimeSpan(13, 0, 0));
            await SeedScheduleAsync("evening", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(17, 0, 0), new TimeSpan(20, 0, 0));
            await _db.Insertable(new[]
            {
                CreateStoredPunchForSchedule("morning-in", "morning", "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 8, 0, 0)),
                CreateStoredPunchForSchedule("morning-out", "morning", "BRI", "staff-user", "ClockOut", new DateTime(2026, 5, 18, 10, 0, 0)),
                CreateStoredPunchForSchedule("afternoon-in", "afternoon", "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 11, 0, 0)),
                CreateStoredPunchForSchedule("afternoon-out", "afternoon", "BRI", "staff-user", "ClockOut", new DateTime(2026, 5, 18, 13, 0, 0)),
            }).ExecuteCommandAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var result = await service.PunchAsync(CreatePunchRequest("ClockIn", "2026-05-18T08:00:00Z"));

            Assert.False(result.Success);
            Assert.Equal("SEGMENT_LIMIT_REACHED", result.ErrorCode);
        }

        [Fact]
        public async Task PunchAsync_ConcurrentDifferentQrTokens_OnlyOneClockInSucceeds()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var firstRequest = CreatePunchRequest("ClockIn", "2026-05-17T23:00:00Z");
            var secondRequest = CreatePunchRequest("ClockIn", "2026-05-17T23:00:00Z");
            var resource = AttendanceDailyMutationLock.BuildResource(
                "staff-user", "BRI", new DateTime(2026, 5, 18));
            var heldLock = await AttendanceDailyMutationLock.AcquireProcessAsync(resource);

            var pendingResults = Task.WhenAll(
                service.PunchAsync(firstRequest),
                service.PunchAsync(secondRequest));
            for (var attempt = 0;
                attempt < 1000 && AttendanceDailyMutationLock.GetProcessReferenceCount(resource) < 3;
                attempt++)
            {
                await Task.Yield();
            }
            Assert.Equal(3, AttendanceDailyMutationLock.GetProcessReferenceCount(resource));
            await heldLock.DisposeAsync();
            var results = await pendingResults;

            Assert.Equal(1, results.Count(item => item.Success));
            Assert.Equal(1, results.Count(item => item.ErrorCode == "PUNCH_CONCURRENT_CONFLICT"));
            Assert.Equal(1, await _db.Queryable<AttendancePunch>().CountAsync(item =>
                !item.IsDeleted
                && item.UserGuid == "staff-user"
                && item.StoreCode == "BRI"
                && item.PunchType == "ClockIn"));
        }

        [Fact]
        public async Task PunchAdjustment_DailySegmentLimitIsSharedPerStoreButIndependentAcrossStores()
        {
            await SeedStoreScopeAsync();
            await SeedManagerStoreAccessAsync("staff-user", "store-other");
            await SeedScheduleAsync("first", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(8, 0, 0), new TimeSpan(10, 0, 0));
            await SeedScheduleAsync("second", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(11, 0, 0), new TimeSpan(13, 0, 0));
            await SeedScheduleAsync("third", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(17, 0, 0), new TimeSpan(20, 0, 0));
            await SeedScheduleAsync("other-store", "OTHER", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(17, 0, 0), new TimeSpan(20, 0, 0));
            await _db.Insertable(new[]
            {
                CreateStoredPunchForSchedule("first-in", "first", "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 8, 0, 0)),
                CreateStoredPunchForSchedule("first-out", "first", "BRI", "staff-user", "ClockOut", new DateTime(2026, 5, 18, 10, 0, 0)),
                CreateStoredPunchForSchedule("second-in", "second", "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 11, 0, 0)),
                CreateStoredPunchForSchedule("second-out", "second", "BRI", "staff-user", "ClockOut", new DateTime(2026, 5, 18, 13, 0, 0)),
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var sameStore = await SubmitPunchAdjustmentAfterPreviewAsync(service, new CreateAttendancePunchAdjustmentDto
            {
                StoreCode = "BRI", ScheduleGuid = "third", PunchType = "ClockIn",
                RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 17, 0, 0), Reason = "第三段",
            });
            var otherStore = await SubmitPunchAdjustmentAfterPreviewAsync(service, new CreateAttendancePunchAdjustmentDto
            {
                StoreCode = "OTHER", ScheduleGuid = "other-store", PunchType = "ClockIn",
                RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 17, 0, 0), Reason = "跨店第一段",
            });

            Assert.False(sameStore.Success);
            Assert.Equal("SEGMENT_LIMIT_REACHED", sameStore.ErrorCode);
            Assert.True(otherStore.Success, otherStore.Message);
        }

        [Fact]
        public async Task ApproveAsync_WhenOtherScheduleConsumesDailyLimit_RollsBackClaim()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("first", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(8, 0, 0), new TimeSpan(10, 0, 0));
            await SeedScheduleAsync("second", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(11, 0, 0), new TimeSpan(13, 0, 0));
            await SeedScheduleAsync("third", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(17, 0, 0), new TimeSpan(20, 0, 0));
            await _db.Insertable(new[]
            {
                CreateStoredPunchForSchedule("first-in", "first", "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 8, 0, 0)),
                CreateStoredPunchForSchedule("first-out", "first", "BRI", "staff-user", "ClockOut", new DateTime(2026, 5, 18, 10, 0, 0)),
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var created = await SubmitPunchAdjustmentAfterPreviewAsync(
                CreateService("staff-user", "staff", "StoreStaff"),
                new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI", ScheduleGuid = "third", PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 17, 0, 0), Reason = "申请第二段",
                });
            Assert.True(created.Success, created.Message);
            await _db.Insertable(new[]
            {
                CreateStoredPunchForSchedule("second-in", "second", "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 11, 0, 0)),
                CreateStoredPunchForSchedule("second-out", "second", "BRI", "staff-user", "ClockOut", new DateTime(2026, 5, 18, 13, 0, 0)),
            }).ExecuteCommandAsync();
            var approval = await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.SourceType == "PunchAdjustment");

            var result = await CreateService("manager-user", "manager", "StoreManager")
                .ApproveAsync(approval.ApprovalGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "同意" });

            Assert.False(result.Success);
            Assert.Equal("SEGMENT_LIMIT_REACHED", result.ErrorCode);
            Assert.Equal("Pending", await _db.Queryable<AttendanceApproval>()
                .Where(item => item.ApprovalGuid == approval.ApprovalGuid)
                .Select(item => item.ReviewStatus).FirstAsync());
        }

        [Fact]
        public async Task ApproveAsync_ConcurrentAdjustmentsForLastSegment_OnlyOneApplies()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("first", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(8, 0, 0), new TimeSpan(10, 0, 0));
            await SeedScheduleAsync("second", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(11, 0, 0), new TimeSpan(13, 0, 0));
            await SeedScheduleAsync("third", "BRI", "staff-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(17, 0, 0), new TimeSpan(20, 0, 0));
            await _db.Insertable(new[]
            {
                CreateStoredPunchForSchedule("first-in", "first", "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 8, 0, 0)),
                CreateStoredPunchForSchedule("first-out", "first", "BRI", "staff-user", "ClockOut", new DateTime(2026, 5, 18, 10, 0, 0)),
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var staffService = CreateService("staff-user", "staff", "StoreStaff");
            var second = await SubmitPunchAdjustmentAfterPreviewAsync(staffService, new CreateAttendancePunchAdjustmentDto
            {
                StoreCode = "BRI", ScheduleGuid = "second", PunchType = "ClockIn",
                RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 11, 0, 0), Reason = "申请第二段A",
            });
            var third = await SubmitPunchAdjustmentAfterPreviewAsync(staffService, new CreateAttendancePunchAdjustmentDto
            {
                StoreCode = "BRI", ScheduleGuid = "third", PunchType = "ClockIn",
                RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 17, 0, 0), Reason = "申请第二段B",
            });
            Assert.True(second.Success, second.Message);
            Assert.True(third.Success, third.Message);
            var approvalGuids = await _db.Queryable<AttendanceApproval>()
                .Where(item => item.SourceType == "PunchAdjustment" && item.ReviewStatus == "Pending")
                .Select(item => item.ApprovalGuid)
                .ToListAsync();
            Assert.Equal(2, approvalGuids.Count);

            var manager = CreateService("manager-user", "manager", "StoreManager");
            var results = await Task.WhenAll(approvalGuids.Select(guid =>
                manager.ApproveAsync(guid, new ReviewAttendanceApprovalDto { ReviewRemark = "同意" })));

            Assert.Equal(1, results.Count(item => item.Success));
            Assert.Equal(1, results.Count(item => item.ErrorCode == "SEGMENT_LIMIT_REACHED"));
            Assert.Equal(1, await _db.Queryable<AttendancePunchAdjustment>()
                .CountAsync(item => item.Status == "Applied"));
            Assert.Equal(1, await _db.Queryable<AttendancePunchAdjustment>()
                .CountAsync(item => item.Status == "Pending"));
            Assert.Equal(1, await _db.Queryable<AttendanceApproval>()
                .CountAsync(item => item.SourceType == "PunchAdjustment" && item.ReviewStatus == "Pending"));
        }

        [Fact]
        public async Task CreateMyPunchAdjustmentAsync_ConcurrentManagerDirectForLastSegment_OnlyOneApplies()
        {
            await SeedStoreScopeAsync();
            await SeedStoreManagerRoleAsync("manager-user");
            await SeedScheduleAsync("first", "BRI", "manager-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(8, 0, 0), new TimeSpan(10, 0, 0));
            await SeedScheduleAsync("second", "BRI", "manager-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(11, 0, 0), new TimeSpan(13, 0, 0));
            await SeedScheduleAsync("third", "BRI", "manager-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(14, 0, 0), new TimeSpan(16, 0, 0));
            await SeedScheduleAsync("fourth", "BRI", "manager-user", new DateTime(2026, 5, 18), "Active", new TimeSpan(17, 0, 0), new TimeSpan(20, 0, 0));
            await _db.Insertable(new[]
            {
                CreateStoredPunchForSchedule("first-in", "first", "BRI", "manager-user", "ClockIn", new DateTime(2026, 5, 18, 8, 0, 0)),
                CreateStoredPunchForSchedule("first-out", "first", "BRI", "manager-user", "ClockOut", new DateTime(2026, 5, 18, 10, 0, 0)),
                CreateStoredPunchForSchedule("second-in", "second", "BRI", "manager-user", "ClockIn", new DateTime(2026, 5, 18, 11, 0, 0)),
                CreateStoredPunchForSchedule("second-out", "second", "BRI", "manager-user", "ClockOut", new DateTime(2026, 5, 18, 13, 0, 0)),
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var service = CreateService("manager-user", "manager", "StoreManager");
            var resource = AttendanceDailyMutationLock.BuildResource(
                "manager-user", "BRI", new DateTime(2026, 5, 18));
            var heldLock = await AttendanceDailyMutationLock.AcquireProcessAsync(resource);

            var pendingResults = Task.WhenAll(
                SubmitPunchAdjustmentAfterPreviewAsync(service, new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI", ScheduleGuid = "third", PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 14, 0, 0), Reason = "直接第三段A",
                }),
                SubmitPunchAdjustmentAfterPreviewAsync(service, new CreateAttendancePunchAdjustmentDto
                {
                    StoreCode = "BRI", ScheduleGuid = "fourth", PunchType = "ClockIn",
                    RequestedPunchTimeLocal = new DateTime(2026, 5, 18, 17, 0, 0), Reason = "直接第三段B",
                }));
            for (var attempt = 0;
                attempt < 1000 && AttendanceDailyMutationLock.GetProcessReferenceCount(resource) < 3;
                attempt++)
            {
                await Task.Yield();
            }
            Assert.Equal(3, AttendanceDailyMutationLock.GetProcessReferenceCount(resource));
            await heldLock.DisposeAsync();
            var results = await pendingResults;

            Assert.Equal(1, results.Count(item => item.Success));
            Assert.Equal(1, results.Count(item => item.ErrorCode == "SEGMENT_LIMIT_REACHED"));
            Assert.Equal(1, await _db.Queryable<AttendancePunchAdjustment>()
                .CountAsync(item => item.Status == "Applied"));
            Assert.Equal(1, await _db.Queryable<AttendancePunch>()
                .CountAsync(item => !item.IsDeleted && item.AdjustmentGuid != null));
        }

        [Fact]
        public async Task GetMyTodayAsync_ConcurrentReconcile_CreatesSinglePendingApproval()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(CreateStoredPunch("concurrent-open", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)))
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var service = CreateService("staff-user", "staff", "StoreStaff");

            await Task.WhenAll(
                service.GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI"),
                service.GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI"));

            Assert.Equal(1, await _db.Queryable<AttendanceApproval>()
                .CountAsync(item => item.SourceType == "MissingClockOut"
                    && item.SourceGuid == "schedule-1"
                    && item.ReviewStatus == "Pending"));
        }

        [Fact]
        public async Task GetMyTodayAsync_ReconcileAndClockOutInterleave_LeavesNoStaleMissingClockOutApproval()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync();
            await _db.Insertable(CreateStoredPunch(
                "interleave-open", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0))).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var resource = AttendanceDailyMutationLock.BuildResource(
                "staff-user", "BRI", new DateTime(2026, 5, 18));
            await using var heldLock = await AttendanceDailyMutationLock.AcquireProcessAsync(resource);

            var reconcileTask = service.GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");
            for (var attempt = 0;
                attempt < 1000 && AttendanceDailyMutationLock.GetProcessReferenceCount(resource) < 2;
                attempt++)
            {
                await Task.Yield();
            }
            Assert.Equal(2, AttendanceDailyMutationLock.GetProcessReferenceCount(resource));

            var clockOutTask = service.PunchAsync(CreatePunchRequest("ClockOut", "2026-05-18T08:00:00Z"));
            for (var attempt = 0;
                attempt < 1000 && AttendanceDailyMutationLock.GetProcessReferenceCount(resource) < 3;
                attempt++)
            {
                await Task.Yield();
            }
            Assert.Equal(3, AttendanceDailyMutationLock.GetProcessReferenceCount(resource));
            await heldLock.DisposeAsync();

            await Task.WhenAll(reconcileTask, clockOutTask);
            var reconciliation = await reconcileTask;
            var clockOut = await clockOutTask;

            Assert.True(reconciliation.Success, reconciliation.Message);
            Assert.True(clockOut.Success, clockOut.Message);
            Assert.Equal(0, await _db.Queryable<AttendanceApproval>()
                .CountAsync(item => item.SourceType == "MissingClockOut" && item.ReviewStatus == "Pending"));
            Assert.Equal(1, await _db.Queryable<AttendanceApproval>()
                .CountAsync(item => item.SourceType == "MissingClockOut" && item.ReviewStatus == "Cancelled"));
        }

        [Fact]
        public async Task PunchAsync_WhenOnlyDraftScheduleExists_TreatsPunchAsNoSchedule()
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync("draft-schedule", "BRI", "staff-user", new DateTime(2026, 5, 18), "Draft");
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.PunchAsync(CreatePunchRequest(
                "ClockIn",
                "2026-05-17T23:00:00Z"
            ));

            Assert.True(result.Success);
            Assert.Equal("NoSchedule", result.Data!.Status);
            Assert.Null(result.Data.ScheduleGuid);
        }

        [Theory]
        [InlineData("Australia/Brisbane", "2026-05-20T14:30:00Z", "2026-05-21")]
        [InlineData("Australia/Melbourne", "2026-05-20T14:30:00Z", "2026-05-21")]
        [InlineData("Unexpected/Zone", "2026-05-20T14:30:00Z", "2026-05-21")]
        public async Task PunchAsync_StoresLocalTimeAndWorkDateByResolvedStoreTimeZone(
            string storeTimeZone,
            string punchTimeUtc,
            string expectedWorkDate
        )
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");

            var result = await service.PunchAsync(CreatePunchRequest(
                "ClockIn",
                punchTimeUtc,
                storeTimeZone
            ));

            Assert.True(result.Success);
            Assert.Equal(expectedWorkDate, result.Data!.WorkDate.ToString("yyyy-MM-dd"));
            Assert.Equal("Australia/Brisbane", result.Data.StoreTimeZone);
        }

        [Fact]
        public async Task PunchAsync_WhenBrisbaneStoreTimeZoneMissing_UsesBrisbaneTimeZone()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var now = DateTime.Parse("2026-01-01T13:30:00Z").ToUniversalTime();
            _timeProvider.SetUtcNow(now);

            var result = await service.PunchAsync(new AttendancePunchRequestDto
            {
                QrToken = CreateQrToken(now),
                StoreCode = "BRI",
                PunchType = "ClockIn",
                PunchTimeUtc = now,
                LocationLatitude = -27.4698,
                LocationLongitude = 153.0251,
                LocationAccuracy = 12.5,
                LocationPermissionStatus = "granted",
                LocationCapturedAtUtc = DateTime.UtcNow,
            });

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal("Australia/Brisbane", result.Data!.StoreTimeZone);
            Assert.Equal("2026-01-01", result.Data.WorkDate.ToString("yyyy-MM-dd"));
        }

        [Fact]
        public async Task PunchAsync_WhenStoreIsDeleted_UsesDefaultStoreTimeZone()
        {
            await SeedStoreScopeAsync();
            await _db.Updateable<Store>()
                .SetColumns(item => item.IsDeleted == true)
                .Where(item => item.StoreCode == "BRI")
                .ExecuteCommandAsync();
            var now = DateTime.Parse("2026-01-01T13:30:00Z").ToUniversalTime();
            var request = CreateQrPunchRequest(now, Guid.NewGuid());
            // 定位采集时间需相对测试运行时保持新鲜；打卡时间仍固定用于验证跨午夜日期。
            request.LocationCapturedAtUtc = DateTime.UtcNow;

            var result = await CreateService("admin-user", "admin", "Admin")
                .PunchAsync(request);

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal("Australia/Sydney", result.Data!.StoreTimeZone);
            Assert.Equal("2026-01-02", result.Data.WorkDate.ToString("yyyy-MM-dd"));
        }

        [Fact]
        public async Task PunchAsync_WhenGenericStoreHasConfiguredBrisbaneTimeZone_StoresBrisbane()
        {
            await SeedStoreScopeAsync();
            var store = await _db.Queryable<Store>()
                .FirstAsync(item => item.StoreGUID == "store-bri");
            store!.StoreCode = "1001";
            store.StoreName = "Generic Store";
            store.Address = string.Empty;
            store.TimeZoneId = "Australia/Brisbane";
            await _db.Updateable(store).ExecuteCommandAsync();
            await _db.Updateable<AttendancePosQrKey>()
                .SetColumns(item => item.StoreCode == "1001")
                .Where(item => item.Kid == "K1")
                .ExecuteCommandAsync();
            var now = DateTime.Parse("2026-01-01T13:30:00Z").ToUniversalTime();
            _timeProvider.SetUtcNow(now);

            var result = await CreateService("staff-user", "staff", "StoreStaff").PunchAsync(
                CreatePunchRequest("ClockIn", now.ToString("O"), storeCode: "1001"));

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal("Australia/Brisbane", result.Data!.StoreTimeZone);
            Assert.Equal("2026-01-01", result.Data.WorkDate.ToString("yyyy-MM-dd"));
        }

        [Fact]
        public async Task PunchAsync_WhenConfiguredTimeZoneConflictsWithAddress_PrefersConfiguredValue()
        {
            await SeedStoreScopeAsync();
            var store = await _db.Queryable<Store>()
                .FirstAsync(item => item.StoreGUID == "store-bri");
            store!.Address = "1 Sydney Road, Sydney NSW 2000";
            store.TimeZoneId = "Australia/Brisbane";
            await _db.Updateable(store).ExecuteCommandAsync();
            var now = DateTime.Parse("2026-01-01T13:30:00Z").ToUniversalTime();
            _timeProvider.SetUtcNow(now);

            var result = await CreateService("staff-user", "staff", "StoreStaff").PunchAsync(
                CreatePunchRequest("ClockIn", now.ToString("O")));

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal("Australia/Brisbane", result.Data!.StoreTimeZone);
            Assert.Equal("2026-01-01", result.Data.WorkDate.ToString("yyyy-MM-dd"));
        }

        [Fact]
        public async Task PunchAsync_WhenStoreTimeZoneIsMissing_PreservesAddressInference()
        {
            await SeedStoreScopeAsync();
            var store = await _db.Queryable<Store>()
                .FirstAsync(item => item.StoreGUID == "store-bri");
            store!.Address = "1 Brisbane Road, Brisbane QLD 4000";
            store.TimeZoneId = null;
            await _db.Updateable(store).ExecuteCommandAsync();
            var now = DateTime.Parse("2026-01-01T13:30:00Z").ToUniversalTime();
            _timeProvider.SetUtcNow(now);

            var result = await CreateService("staff-user", "staff", "StoreStaff").PunchAsync(
                CreatePunchRequest("ClockIn", now.ToString("O")));

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
            Assert.Equal("Australia/Brisbane", result.Data!.StoreTimeZone);
        }

        [Theory]
        [InlineData("ClockIn", "2026-05-18T00:20:00Z", "Late", 1)]
        [InlineData("ClockOut", "2026-05-18T06:40:00Z", "Break", 0)]
        [InlineData("ClockIn", "2026-05-19T00:00:00Z", "NoSchedule", 1)]
        public async Task PunchAsync_WhenExceptionalStatus_CreatesPendingApproval(
            string punchType,
            string punchTimeUtc,
            string expectedStatus,
            int expectedApprovalCount
        )
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            await SeedScheduleAsync();
            var request = CreatePunchRequest(punchType, punchTimeUtc);
            if (punchType == "ClockOut")
            {
                await _db.Insertable(new AttendancePunch
                {
                    PunchGuid = Guid.NewGuid().ToString(),
                    ScheduleGuid = "schedule-1",
                    StoreCode = "BRI",
                    UserGuid = "staff-user",
                    WorkDate = DateTime.Parse(punchTimeUtc).ToUniversalTime().AddHours(10).Date,
                    StoreTimeZone = "Australia/Brisbane",
                    PunchType = "ClockIn",
                    PunchTimeUtc = DateTime.Parse(punchTimeUtc).ToUniversalTime().AddHours(-8),
                    PunchTimeLocal = DateTime.Parse(punchTimeUtc).ToUniversalTime().AddHours(2),
                    Status = "Normal",
                    CreatedAt = DateTime.UtcNow,
                }).ExecuteCommandAsync();
            }

            var result = await service.PunchAsync(request);

            Assert.True(result.Success);
            Assert.Equal(expectedStatus, result.Data!.Status);
            Assert.Equal(expectedApprovalCount, await _db.Queryable<AttendanceApproval>().CountAsync());
        }

        [Fact]
        public async Task PunchAsync_WhenLocationMissing_ReturnsLocationRequired()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var now = DateTime.UtcNow;
            _timeProvider.SetUtcNow(now);

            var result = await service.PunchAsync(new AttendancePunchRequestDto
            {
                QrToken = CreateQrToken(now),
                StoreCode = "BRI",
                StoreTimeZone = "Australia/Brisbane",
                PunchType = "ClockIn",
                PunchTimeUtc = DateTime.Parse("2026-05-18T00:00:00Z").ToUniversalTime(),
            });

            Assert.False(result.Success);
            Assert.Equal("LOCATION_REQUIRED", result.ErrorCode);
        }

        [Fact]
        public async Task PunchAsync_WhenLocationCapturedAtIsStale_ReturnsLocationRequired()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var now = DateTime.UtcNow;
            _timeProvider.SetUtcNow(now);

            var result = await service.PunchAsync(new AttendancePunchRequestDto
            {
                QrToken = CreateQrToken(now),
                StoreCode = "BRI",
                StoreTimeZone = "Australia/Brisbane",
                PunchType = "ClockIn",
                PunchTimeUtc = now,
                LocationLatitude = -27.4698,
                LocationLongitude = 153.0251,
                LocationAccuracy = 12.5,
                LocationPermissionStatus = "granted",
                LocationCapturedAtUtc = now.AddMinutes(-10),
            });

            Assert.False(result.Success);
            Assert.Equal("LOCATION_REQUIRED", result.ErrorCode);
            Assert.Equal("定位采集时间无效", result.Message);
        }

        [Fact]
        public async Task PunchAsync_WhenLocationProvided_StoresLocationAuditFields()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var capturedAt = DateTime.UtcNow;
            _timeProvider.SetUtcNow(capturedAt);

            var result = await service.PunchAsync(new AttendancePunchRequestDto
            {
                QrToken = CreateQrToken(capturedAt),
                StoreCode = "BRI",
                StoreTimeZone = "Australia/Brisbane",
                PunchType = "ClockIn",
                PunchTimeUtc = DateTime.Parse("2026-05-18T00:00:00Z").ToUniversalTime(),
                DeviceId = "hbmobile-test-device",
                LocationLatitude = -27.4698,
                LocationLongitude = 153.0251,
                LocationAccuracy = 12.5,
                LocationPermissionStatus = "granted",
                LocationCapturedAtUtc = capturedAt,
            });

            Assert.True(result.Success);
            Assert.Equal(-27.4698, result.Data!.LocationLatitude);
            Assert.Equal(153.0251, result.Data.LocationLongitude);
            Assert.Equal(12.5, result.Data.LocationAccuracy);
            Assert.Equal("granted", result.Data.LocationPermissionStatus);
            Assert.Equal(capturedAt, result.Data.LocationCapturedAtUtc);

            var stored = await _db.Queryable<AttendancePunch>()
                .FirstAsync(item => item.PunchGuid == result.Data.PunchGuid);
            Assert.NotNull(stored);
            Assert.Equal("hbmobile-test-device", stored.DeviceId);
            Assert.Equal(-27.4698, stored.LocationLatitude);
            Assert.Equal(153.0251, stored.LocationLongitude);
            Assert.Equal(12.5, stored.LocationAccuracyMeters);
            Assert.Equal(capturedAt, stored.LocationCapturedAtUtc);
        }

        [Fact]
        public async Task CreateLocationSampleAsync_WhenStoreAllowed_SavesShiftSample()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var capturedAt = DateTime.UtcNow;
            _timeProvider.SetUtcNow(capturedAt);
            var clockIn = CreateStoredPunch(
                "location-open-in",
                "ClockIn",
                capturedAt.AddHours(10)
            );
            clockIn.PunchTimeUtc = capturedAt;
            await _db.Insertable(clockIn).ExecuteCommandAsync();

            var result = await service.CreateLocationSampleAsync(
                CreateLocationSampleRequest(capturedAt)
            );

            Assert.True(result.Success);
            Assert.Equal("staff-user", result.Data!.UserGuid);
            Assert.Equal("BRI", result.Data.StoreCode);
            Assert.Equal("SYS-001", result.Data.SystemDeviceNumber);
            Assert.Equal("ShiftSample", result.Data.EventType);
            Assert.Equal(-27.4705, result.Data.LocationLatitude);
            Assert.Equal(153.0260, result.Data.LocationLongitude);
            Assert.Equal(18.2, result.Data.LocationAccuracy);
            Assert.Equal(capturedAt, result.Data.LocationCapturedAtUtc);

            var stored = await _db.Queryable<AttendanceLocationSample>()
                .FirstAsync(item => item.SampleGuid == result.Data.SampleGuid);
            Assert.NotNull(stored);
            Assert.Equal("hbmobile-test-device", stored.HardwareId);
            Assert.Equal("iOS", stored.DeviceSystem);
        }

        [Fact]
        public async Task CreateLocationSampleAsync_WhenSampleWasCapturedBeforeClockOut_AcceptsDelayedUpload()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var capturedAt = DateTime.UtcNow.AddMinutes(-10);
            _timeProvider.SetUtcNow(capturedAt.AddMinutes(10));
            var clockIn = CreateStoredPunch(
                "location-delayed-in",
                "ClockIn",
                capturedAt.AddHours(10)
            );
            clockIn.PunchTimeUtc = capturedAt;
            var clockOut = CreateStoredPunch(
                "location-delayed-out",
                "ClockOut",
                capturedAt.AddHours(10).AddMinutes(5)
            );
            clockOut.PunchTimeUtc = capturedAt.AddMinutes(5);
            await _db.Insertable(new[] { clockIn, clockOut }).ExecuteCommandAsync();

            var result = await service.CreateLocationSampleAsync(
                CreateLocationSampleRequest(capturedAt)
            );

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
        }

        [Fact]
        public async Task CreateLocationSampleAsync_WhenCapturedWithinTenHoursButUploadedLater_AcceptsSample()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var capturedAt = DateTime.UtcNow.AddMinutes(-2);
            _timeProvider.SetUtcNow(capturedAt.AddHours(12));
            var capturedAtLocal = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
                capturedAt,
                "Australia/Brisbane"
            );
            var clockIn = CreateStoredPunch(
                "location-delayed-boundary-in",
                "ClockIn",
                capturedAtLocal
            );
            clockIn.PunchTimeUtc = capturedAt;
            await _db.Insertable(clockIn).ExecuteCommandAsync();

            var result = await service.CreateLocationSampleAsync(
                CreateLocationSampleRequest(capturedAt)
            );

            Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}");
        }

        [Fact]
        public async Task CreateLocationSampleAsync_WhenNoOpenClockIn_RejectsSample()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 2, 0, 0, DateTimeKind.Utc));

            var result = await service.CreateLocationSampleAsync(
                CreateLocationSampleRequest(DateTime.UtcNow)
            );

            Assert.False(result.Success);
            Assert.Equal("ACTIVE_SHIFT_REQUIRED", result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationSampleAsync_WhenSamplePredatesOpenClockIn_RejectsSample()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var serverNow = DateTime.UtcNow;
            _timeProvider.SetUtcNow(serverNow);
            var clockIn = CreateStoredPunch(
                "location-after-sample-in",
                "ClockIn",
                serverNow.AddHours(10).AddMinutes(-5)
            );
            clockIn.PunchTimeUtc = serverNow.AddMinutes(-5);
            await _db.Insertable(clockIn).ExecuteCommandAsync();

            var result = await service.CreateLocationSampleAsync(
                CreateLocationSampleRequest(serverNow.AddMinutes(-10))
            );

            Assert.False(result.Success);
            Assert.Equal("ACTIVE_SHIFT_REQUIRED", result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Theory]
        [InlineData("staff-user", "OTHER")]
        [InlineData("manager-user", "BRI")]
        public async Task CreateLocationSampleAsync_WhenOpenClockInBelongsToOtherScope_RejectsSample(
            string punchUserGuid,
            string punchStoreCode
        )
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var capturedAt = DateTime.UtcNow;
            _timeProvider.SetUtcNow(capturedAt);
            var otherScopePunch = CreateStoredPunch(
                "location-other-scope-in",
                "ClockIn",
                capturedAt.AddHours(10).AddMinutes(-20)
            );
            otherScopePunch.UserGuid = punchUserGuid;
            otherScopePunch.StoreCode = punchStoreCode;
            otherScopePunch.PunchTimeUtc = capturedAt.AddMinutes(-20);
            await _db.Insertable(otherScopePunch).ExecuteCommandAsync();

            var result = await service.CreateLocationSampleAsync(
                CreateLocationSampleRequest(capturedAt)
            );

            Assert.False(result.Success);
            Assert.Equal("ACTIVE_SHIFT_REQUIRED", result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationSampleAsync_WhenClockOutFollowsClockIn_RejectsSample()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var capturedAt = DateTime.UtcNow;
            _timeProvider.SetUtcNow(capturedAt);
            var clockIn = CreateStoredPunch(
                "location-closed-in",
                "ClockIn",
                capturedAt.AddHours(8)
            );
            clockIn.PunchTimeUtc = capturedAt.AddHours(-2);
            var clockOut = CreateStoredPunch(
                "location-closed-out",
                "ClockOut",
                capturedAt.AddHours(9)
            );
            clockOut.PunchTimeUtc = capturedAt.AddHours(-1);
            await _db.Insertable(new[]
            {
                clockIn,
                clockOut,
            }).ExecuteCommandAsync();

            var result = await service.CreateLocationSampleAsync(
                CreateLocationSampleRequest(capturedAt)
            );

            Assert.False(result.Success);
            Assert.Equal("ACTIVE_SHIFT_REQUIRED", result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationSampleAsync_WhenOpenClockInExceedsTenHours_RejectsSample()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var capturedAt = DateTime.UtcNow;
            _timeProvider.SetUtcNow(capturedAt);
            var clockIn = CreateStoredPunch(
                "location-expired-in",
                "ClockIn",
                capturedAt.AddSeconds(-1)
            );
            clockIn.PunchTimeUtc = capturedAt.AddHours(-10).AddSeconds(-1);
            await _db.Insertable(clockIn).ExecuteCommandAsync();

            var result = await service.CreateLocationSampleAsync(
                CreateLocationSampleRequest(capturedAt)
            );

            Assert.False(result.Success);
            Assert.Equal("ACTIVE_SHIFT_REQUIRED", result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationSampleAsync_WhenOpenClockInCrossesStoreLocalMidnight_RejectsSample()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            await _db.Updateable<Store>()
                .SetColumns(item => item.TimeZoneId == "Australia/Sydney")
                .Where(item => item.StoreCode == "BRI")
                .ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 1, 1, 13, 5, 0, DateTimeKind.Utc));
            var priorDayClockIn = CreateStoredPunch(
                "location-prior-day-in",
                "ClockIn",
                new DateTime(2026, 1, 1, 23, 55, 0)
            );
            priorDayClockIn.StoreTimeZone = "Australia/Sydney";
            priorDayClockIn.PunchTimeUtc = new DateTime(
                2026,
                1,
                1,
                12,
                55,
                0,
                DateTimeKind.Utc
            );
            await _db.Insertable(priorDayClockIn).ExecuteCommandAsync();

            var result = await service.CreateLocationSampleAsync(
                CreateLocationSampleRequest(DateTime.UtcNow)
            );

            Assert.False(result.Success);
            Assert.Equal("ACTIVE_SHIFT_REQUIRED", result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        [Fact]
        public async Task CreateLocationSampleAsync_WhenNewerClockInWasSupersededByClockOut_RejectsSample()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("staff-user", "staff", "StoreStaff");
            var capturedAt = DateTime.UtcNow;
            _timeProvider.SetUtcNow(capturedAt);
            var staleClockIn = CreateStoredPunch(
                "location-superseded-in",
                "ClockIn",
                capturedAt.AddHours(9)
            );
            staleClockIn.PunchTimeUtc = capturedAt.AddHours(-1);
            var replacementClockOut = CreateStoredPunch(
                "location-replacement-out",
                "ClockOut",
                capturedAt.AddHours(8).AddMinutes(59)
            );
            replacementClockOut.PunchTimeUtc = capturedAt.AddHours(-1).AddMinutes(-1);
            replacementClockOut.SupersedesPunchGuid = staleClockIn.PunchGuid;
            await _db.Insertable(new[] { staleClockIn, replacementClockOut }).ExecuteCommandAsync();

            var result = await service.CreateLocationSampleAsync(
                CreateLocationSampleRequest(capturedAt)
            );

            Assert.False(result.Success);
            Assert.Equal("ACTIVE_SHIFT_REQUIRED", result.ErrorCode);
            Assert.Equal(0, await _db.Queryable<AttendanceLocationSample>().CountAsync());
        }

        private static AttendanceLocationSampleRequestDto CreateLocationSampleRequest(
            DateTime capturedAt
        )
        {
            return new AttendanceLocationSampleRequestDto
            {
                StoreCode = "BRI",
                HardwareId = "hbmobile-test-device",
                SystemDeviceNumber = "SYS-001",
                DeviceSystem = "iOS",
                EventType = "ShiftSample",
                LocationLatitude = -27.4705,
                LocationLongitude = 153.0260,
                LocationAccuracy = 18.2,
                LocationPermissionStatus = "granted",
                LocationCapturedAtUtc = capturedAt,
            };
        }

        [Fact]
        public async Task GetLocationSamplesAsync_WhenDateIsStoreLocalDate_ReturnsBrisbaneEarlySample()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("manager-user", "manager", "StoreManager");
            var capturedAtUtc = DateTime.Parse("2026-01-01T13:20:00Z").ToUniversalTime();

            await _db.Insertable(new AttendanceLocationSample
            {
                SampleGuid = "brisbane-early-sample",
                UserGuid = "staff-user",
                StoreCode = "BRI",
                EventType = "ShiftSample",
                LocationLatitude = -27.4705,
                LocationLongitude = 153.0260,
                LocationCapturedAtUtc = capturedAtUtc,
                CreatedAt = capturedAtUtc,
            }).ExecuteCommandAsync();

            var result = await service.GetLocationSamplesAsync(new AttendanceLocationSampleQueryDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                FromDate = new DateTime(2026, 1, 1),
                ToDate = new DateTime(2026, 1, 1),
            });

            Assert.True(result.Success);
            var sample = Assert.Single(result.Data!);
            Assert.Equal("brisbane-early-sample", sample.SampleGuid);
            Assert.Equal(capturedAtUtc, sample.LocationCapturedAtUtc);
        }

        [Fact]
        public async Task CreateManagedLeaveRequestAsync_WhenValidAnnualLeave_CreatesPendingLeaveAndApproval()
        {
            await SeedStoreScopeAsync();
            await SeedEmployeeProfileAsync("staff-user", EmployeeType.FullTime);
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.CreateManagedLeaveRequestAsync(new CreateManagedAttendanceLeaveRequestDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                LeaveType = "AnnualLeave",
                StartDate = new DateTime(2026, 5, 18),
                EndDate = new DateTime(2026, 5, 19),
                Reason = "Family trip",
            });

            Assert.True(result.Success);
            Assert.Equal("Pending", result.Data!.Status);
            Assert.Equal("staff-user", result.Data.UserGuid);

            var leave = await _db.Queryable<AttendanceLeaveRequest>()
                .FirstAsync(item => item.LeaveGuid == result.Data.LeaveGuid);
            Assert.NotNull(leave);
            Assert.Equal("Pending", leave.Status);

            var approval = await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.SourceType == "Leave" && item.SourceGuid == result.Data.LeaveGuid);
            Assert.NotNull(approval);
            Assert.Equal("Pending", approval.ReviewStatus);
            Assert.Equal("staff-user", approval.ApplicantUserGuid);
        }

        [Fact]
        public async Task CreateManagedLeaveRequestAsync_WhenEmployeeEmploymentTypeIsCasual_ReturnsValidationError()
        {
            await SeedStoreScopeAsync();
            await SeedEmployeeProfileAsync("staff-user", EmployeeType.Temporary);
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.CreateManagedLeaveRequestAsync(new CreateManagedAttendanceLeaveRequestDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                LeaveType = "AnnualLeave",
                StartDate = new DateTime(2026, 5, 18),
                EndDate = new DateTime(2026, 5, 18),
            });

            Assert.False(result.Success);
            Assert.Equal("INVALID_EMPLOYMENT_TYPE", result.ErrorCode);
        }

        [Fact]
        public async Task CreateManagedLeaveRequestAsync_WhenSickLeaveHasNoAttachment_ReturnsValidationError()
        {
            await SeedStoreScopeAsync();
            await SeedEmployeeProfileAsync("staff-user", EmployeeType.PartTime);
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.CreateManagedLeaveRequestAsync(new CreateManagedAttendanceLeaveRequestDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                LeaveType = "SickLeave",
                StartDate = new DateTime(2026, 5, 18),
                EndDate = new DateTime(2026, 5, 18),
            });

            Assert.False(result.Success);
            Assert.Equal("ATTACHMENT_REQUIRED", result.ErrorCode);
        }

        [Fact]
        public async Task CreateManagedLeaveRequestAsync_WhenEmployeeNotInRequestedStore_ReturnsForbidden()
        {
            await SeedStoreScopeAsync();
            await SeedEmployeeProfileAsync("staff-user", EmployeeType.FullTime);
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.CreateManagedLeaveRequestAsync(new CreateManagedAttendanceLeaveRequestDto
            {
                StoreCode = "OTHER",
                UserGuid = "staff-user",
                LeaveType = "AnnualLeave",
                StartDate = new DateTime(2026, 5, 18),
                EndDate = new DateTime(2026, 5, 18),
            });

            Assert.False(result.Success);
            Assert.Equal("FORBIDDEN_STORE", result.ErrorCode);
        }

        [Fact]
        public async Task GetLeaveAttachmentUploadSignatureAsync_WhenFileNameProvided_ReturnsAttendanceObjectKey()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.GetLeaveAttachmentUploadSignatureAsync(new DirectUploadRequest
            {
                FileName = "medical certificate (May).pdf",
                ContentType = "application/pdf",
                FileSize = 2048,
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            Assert.StartsWith("attendance/leave-attachments/", result.Data!.ObjectKey);
            Assert.EndsWith("-medical-certificate-May.pdf", result.Data.ObjectKey);
            Assert.Equal("application/pdf", result.Data.Headers["Content-Type"]);
            Assert.Contains(result.Data.ObjectKey, result.Data.Url);
            var signTimeMatch = Regex.Match(Uri.UnescapeDataString(result.Data.Url), @"q-sign-time=(\d+);(\d+)");
            Assert.True(signTimeMatch.Success);
            Assert.True(
                long.Parse(signTimeMatch.Groups[2].Value) - long.Parse(signTimeMatch.Groups[1].Value) <= 3600
            );
        }

        [Fact]
        public async Task CreateManagedLeaveRequestAsync_WhenSickLeaveAttachmentIsExternal_ReturnsValidationError()
        {
            await SeedStoreScopeAsync();
            await SeedEmployeeProfileAsync("staff-user", EmployeeType.PartTime);
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.CreateManagedLeaveRequestAsync(new CreateManagedAttendanceLeaveRequestDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                LeaveType = "SickLeave",
                StartDate = new DateTime(2026, 5, 18),
                EndDate = new DateTime(2026, 5, 18),
                AttachmentUrl = "https://example.com/medical.jpg",
            });

            Assert.False(result.Success);
            Assert.Equal("INVALID_ATTACHMENT_URL", result.ErrorCode);
        }

        [Fact]
        public async Task CreateScheduleAsync_WhenPartialHolidayExists_DoesNotBlockSchedule()
        {
            await SeedStoreScopeAsync();
            await _db.Insertable(new AttendanceStoreHoliday
            {
                HolidayGuid = "holiday-partial",
                StoreCode = "BRI",
                HolidayDate = new DateTime(2026, 5, 18),
                HolidayName = "Partial trading day",
                BusinessStatus = "Partial",
                OpenTime = new TimeSpan(10, 0, 0),
                CloseTime = new TimeSpan(16, 0, 0),
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.CreateScheduleAsync(new CreateAttendanceScheduleDto
            {
                StoreCode = "BRI",
                UserGuid = "staff-user",
                WorkDate = new DateTime(2026, 5, 18),
                StartTime = new TimeSpan(9, 0, 0),
                EndTime = new TimeSpan(17, 0, 0),
            });

            Assert.True(result.Success);
        }

        [Fact]
        public async Task BatchUpsertHolidaysAsync_WhenStoresAreManaged_CreatesHolidayForEachStore()
        {
            await SeedStoreScopeAsync();
            await SeedManagerStoreAccessAsync("manager-user", "store-other");
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.BatchUpsertHolidaysAsync(new BatchUpsertAttendanceStoreHolidayDto
            {
                StoreCodes = new List<string> { "BRI", "OTHER" },
                HolidayDate = new DateTime(2026, 12, 25),
                HolidayName = "Christmas Day",
                BusinessStatus = "Closed",
                IsPaidHoliday = true,
                Remark = "Batch created",
            });

            Assert.True(result.Success);
            Assert.Equal(2, result.Data!.CreatedCount);
            Assert.Equal(0, result.Data.UpdatedCount);
            Assert.Equal(2, result.Data.Items.Count);
            Assert.Equal(2, await CountActiveHolidaysAsync(new DateTime(2026, 12, 25)));
        }

        [Fact]
        public async Task BatchUpsertHolidaysAsync_WhenHolidayAlreadyExists_UpdatesExistingRow()
        {
            await SeedStoreScopeAsync();
            await _db.Insertable(new AttendanceStoreHoliday
            {
                HolidayGuid = "existing-holiday",
                StoreCode = "BRI",
                HolidayDate = new DateTime(2026, 12, 25),
                HolidayName = "Old Christmas",
                BusinessStatus = "Open",
                IsPaidHoliday = false,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.BatchUpsertHolidaysAsync(new BatchUpsertAttendanceStoreHolidayDto
            {
                StoreCodes = new List<string> { "BRI" },
                HolidayDate = new DateTime(2026, 12, 25),
                HolidayName = "Christmas Day",
                BusinessStatus = "Partial",
                OpenTime = new TimeSpan(10, 0, 0),
                CloseTime = new TimeSpan(16, 0, 0),
                IsPaidHoliday = true,
                Remark = "Updated",
            });

            Assert.True(result.Success);
            Assert.Equal(0, result.Data!.CreatedCount);
            Assert.Equal(1, result.Data.UpdatedCount);
            var row = await _db.Queryable<AttendanceStoreHoliday>()
                .FirstAsync(item => item.HolidayGuid == "existing-holiday");
            Assert.Equal("Christmas Day", row.HolidayName);
            Assert.Equal("Partial", row.BusinessStatus);
            Assert.True(row.IsPaidHoliday);
            Assert.Equal(1, await CountActiveHolidaysAsync(new DateTime(2026, 12, 25)));
        }

        [Fact]
        public async Task BatchUpsertHolidaysAsync_WhenAnyStoreIsForbidden_RollsBackEntireBatch()
        {
            await SeedStoreScopeAsync();
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.BatchUpsertHolidaysAsync(new BatchUpsertAttendanceStoreHolidayDto
            {
                StoreCodes = new List<string> { "BRI", "OTHER" },
                HolidayDate = new DateTime(2026, 12, 25),
                HolidayName = "Christmas Day",
                BusinessStatus = "Closed",
                IsPaidHoliday = true,
            });

            Assert.False(result.Success);
            Assert.Equal("FORBIDDEN_STORE", result.ErrorCode);
            Assert.Equal(0, await CountActiveHolidaysAsync(new DateTime(2026, 12, 25)));
        }

        [Fact]
        public async Task BatchUpsertHolidaysAsync_WhenDuplicateActiveRowsExist_UpdatesOneAndSoftDeletesExtras()
        {
            await SeedStoreScopeAsync();
            await _db.Insertable(new[]
            {
                new AttendanceStoreHoliday
                {
                    HolidayGuid = "duplicate-a",
                    StoreCode = "BRI",
                    HolidayDate = new DateTime(2026, 12, 25),
                    HolidayName = "Duplicate A",
                    BusinessStatus = "Open",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-2),
                },
                new AttendanceStoreHoliday
                {
                    HolidayGuid = "duplicate-b",
                    StoreCode = "BRI",
                    HolidayDate = new DateTime(2026, 12, 25),
                    HolidayName = "Duplicate B",
                    BusinessStatus = "Open",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                },
            }).ExecuteCommandAsync();
            var service = CreateService("manager-user", "manager", "StoreManager");

            var result = await service.BatchUpsertHolidaysAsync(new BatchUpsertAttendanceStoreHolidayDto
            {
                StoreCodes = new List<string> { "BRI" },
                HolidayDate = new DateTime(2026, 12, 25),
                HolidayName = "Christmas Day",
                BusinessStatus = "Closed",
                IsPaidHoliday = true,
            });

            Assert.True(result.Success);
            Assert.Equal(0, result.Data!.CreatedCount);
            Assert.Equal(1, result.Data.UpdatedCount);
            Assert.Equal(1, await CountActiveHolidaysAsync(new DateTime(2026, 12, 25)));
            Assert.Equal(1, await _db.Queryable<AttendanceStoreHoliday>()
                .Where(item =>
                    item.HolidayDate >= new DateTime(2026, 12, 25)
                    && item.HolidayDate < new DateTime(2026, 12, 26)
                    && item.IsDeleted
                )
                .CountAsync());
        }

        public void Dispose()
        {
            _db.Dispose();
            _sqliteConnection.Dispose();

            if (File.Exists(_dbPath))
            {
                SqliteTempFileCleanup.DeleteIfExists(_dbPath);
            }
        }

        private Task SeedAttendanceSigningKeyAsync() =>
            SeedAttendanceSigningKeyAsync("K1", "BRI", "POS-001");

        private async Task SeedAttendanceSigningKeyAsync(
            string kid,
            string storeCode,
            string deviceCode)
        {
            await _db.Insertable(new AttendancePosQrKey
            {
                Kid = kid,
                Algorithm = "A256GCM",
                ProtectedKey = _attendanceProtector.Protect(_attendanceKey),
                StoreCode = storeCode,
                DeviceCode = deviceCode,
                HardwareId = "HW-001",
                Status = "Active",
                RegisteredAtUtc = DateTime.UtcNow,
            }).ExecuteCommandAsync();
        }

        private AttendancePunchRequestDto CreateQrPunchRequest(DateTime now, Guid tokenId)
        {
            _timeProvider.SetUtcNow(now);
            return new AttendancePunchRequestDto
            {
                QrToken = AttendanceQrTokenCodec.Encrypt(new AttendanceQrTokenPayload
                {
                    TokenId = tokenId,
                    StoreCode = "BRI",
                    DeviceCode = "POS-001",
                    IssuedAtUtc = now,
                }, "K1", _attendanceKey),
                LocationLatitude = -27.4698,
                LocationLongitude = 153.0251,
                LocationAccuracy = 12.5,
                LocationPermissionStatus = "granted",
                LocationCapturedAtUtc = now,
            };
        }

        private AttendancePunchRequestDto CreatePunchRequest(
            string punchType,
            string punchTimeUtc,
            string storeTimeZone = "Australia/Brisbane",
            string storeCode = "BRI",
            string kid = "K1",
            string deviceCode = "POS-001"
        )
        {
            var punchAtUtc = DateTime.Parse(punchTimeUtc).ToUniversalTime();
            _timeProvider.SetUtcNow(punchAtUtc);
            return new AttendancePunchRequestDto
            {
                QrToken = CreateQrToken(punchAtUtc, storeCode, deviceCode, kid),
                StoreCode = storeCode,
                StoreTimeZone = storeTimeZone,
                PunchType = punchType,
                PunchTimeUtc = punchAtUtc,
                LocationLatitude = -27.4698,
                LocationLongitude = 153.0251,
                LocationAccuracy = 12.5,
                LocationPermissionStatus = "granted",
                LocationCapturedAtUtc = DateTime.UtcNow,
            };
        }

        private static async Task<ApiResponse<AttendancePunchAdjustmentDto>>
            SubmitPunchAdjustmentAfterPreviewAsync(
                AttendanceReactService service,
                CreateAttendancePunchAdjustmentDto request)
        {
            var preview = await service.PreviewMyPunchAdjustmentAsync(request);
            if (!preview.Success)
            {
                return ApiResponse<AttendancePunchAdjustmentDto>.Error(
                    preview.Message,
                    preview.ErrorCode);
            }

            request.PreviewRevision = preview.Data!.PreviewRevision;
            return await service.CreateMyPunchAdjustmentAsync(request);
        }

        private AttendanceReactService CreateService(string userGuid, string username, params string[] roles)
            => CreateService(userGuid, username, true, roles);

        private AttendanceReactService CreateService(
            string userGuid,
            string username,
            bool posDeviceActive,
            params string[] roles)
        {
            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(CreateClaims(userGuid, username, roles), "TestAuth")
                    ),
                },
            };
            var currentUserService = new CurrentUserService(httpContextAccessor);
            var context = CreateSqlSugarContext(_db);
            var scopeService = new CurrentUserManageableStoreScopeService(
                context,
                currentUserService,
                httpContextAccessor
            );
            var uploadService = new TencentCloudUploadService(
                Options.Create(new TencentCloudSettings
                {
                    SecretId = "secret-id",
                    SecretKey = "secret-key",
                    BucketName = "test-bucket-123",
                    Region = "ap-guangzhou",
                }),
                NullLogger<TencentCloudUploadService>.Instance,
                new HttpClient()
            );

            return new AttendanceReactService(
                context,
                currentUserService,
                scopeService,
                httpContextAccessor,
                NullLogger<AttendanceReactService>.Instance,
                uploadService,
                new StubAttendancePosDeviceStatusProvider(posDeviceActive),
                _timeProvider,
                _attendanceProtector,
                _punchAuthorizationProtector,
                new StubAuthorizationService(_deniedPermissions)
            );
        }

        private static DateTime TruncateToMilliseconds(DateTime value) =>
            new(value.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

        private static string MutateProtectedToken(string token)
        {
            var index = token.Length / 2;
            var replacement = token[index] == 'A' ? 'B' : 'A';
            return token[..index] + replacement + token[(index + 1)..];
        }

        private string CreateQrToken(
            DateTime now,
            string storeCode = "BRI",
            string deviceCode = "POS-001",
            string kid = "K1") =>
            AttendanceQrTokenCodec.Encrypt(new AttendanceQrTokenPayload
            {
                TokenId = Guid.NewGuid(),
                StoreCode = storeCode,
                DeviceCode = deviceCode,
                IssuedAtUtc = now,
            }, kid, _attendanceKey);

        private async Task<AttendanceApproval> SeedCompletedOvertimeAndGetPendingAsync(
            string scheduleGuid)
        {
            await SeedStoreScopeAsync();
            await SeedScheduleAsync(scheduleGuid, "BRI", "staff-user", new DateTime(2026, 5, 18), "Active");
            await _db.Insertable(new[]
            {
                CreateStoredPunchForSchedule($"{scheduleGuid}-in", scheduleGuid, "BRI", "staff-user", "ClockIn", new DateTime(2026, 5, 18, 9, 0, 0)),
                CreateStoredPunchForSchedule($"{scheduleGuid}-out", scheduleGuid, "BRI", "staff-user", "ClockOut", new DateTime(2026, 5, 18, 17, 23, 0)),
            }).ExecuteCommandAsync();
            _timeProvider.SetUtcNow(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc));
            var today = await CreateService("staff-user", "staff", "StoreStaff")
                .GetMyTodayAsync(new DateTime(2026, 5, 18), "BRI");
            Assert.True(today.Success, today.Message);
            return await _db.Queryable<AttendanceApproval>().FirstAsync(item =>
                item.SourceType == "Overtime"
                && item.SourceGuid == scheduleGuid
                && item.ReviewStatus == "Pending");
        }

        private sealed class StubAuthorizationService(IReadOnlySet<string> deniedPermissions)
            : IAuthorizationService
        {
            public Task<AuthorizationResult> AuthorizeAsync(
                ClaimsPrincipal user,
                object? resource,
                IEnumerable<IAuthorizationRequirement> requirements) =>
                Task.FromResult(AuthorizationResult.Success());

            public Task<AuthorizationResult> AuthorizeAsync(
                ClaimsPrincipal user,
                object? resource,
                string policyName) =>
                Task.FromResult(deniedPermissions.Contains(policyName)
                    ? AuthorizationResult.Failed()
                    : AuthorizationResult.Success());
        }

        private sealed class StubAttendancePosDeviceStatusProvider(bool isActive)
            : IAttendancePosDeviceStatusProvider
        {
            public Task<bool> IsActiveAsync(
                string deviceCode,
                string storeCode,
                string hardwareId,
                CancellationToken cancellationToken = default) => Task.FromResult(isActive);
        }

        private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
        {
            private DateTimeOffset _utcNow = utcNow;
            public override DateTimeOffset GetUtcNow() => _utcNow;
            public void SetUtcNow(DateTime utcNow) => _utcNow = new DateTimeOffset(utcNow.ToUniversalTime());
        }

        private static SqlException CreateSqlException(int number)
        {
            var error = (SqlError)RuntimeHelpers.GetUninitializedObject(typeof(SqlError));
            typeof(SqlError).GetField("_number", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(error, number);
            var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
            typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(errors, new object[] { error });
            var exception = (SqlException)RuntimeHelpers.GetUninitializedObject(typeof(SqlException));
            typeof(SqlException).GetField("_errors", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(exception, errors);
            return exception;
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "services")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录");
        }

        private async Task SeedStoreScopeAsync()
        {
            await _db.Insertable(
                new[]
                {
                    new Store
                    {
                        StoreGUID = "store-bri",
                        StoreCode = "BRI",
                        StoreName = "Brisbane",
                        CreatedAt = DateTime.UtcNow,
                    },
                    new Store
                    {
                        StoreGUID = "store-other",
                        StoreCode = "OTHER",
                        StoreName = "Other",
                        CreatedAt = DateTime.UtcNow,
                    },
                }
            ).ExecuteCommandAsync();

            await _db.Insertable(
                new[]
                {
                    new User
                    {
                        UserGUID = "manager-user",
                        Username = "manager",
                        Email = "manager@example.com",
                        PasswordHash = "hash",
                        CreatedAt = DateTime.UtcNow,
                    },
                    new User
                    {
                        UserGUID = "staff-user",
                        Username = "staff",
                        Email = "staff@example.com",
                        PasswordHash = "hash",
                        CreatedAt = DateTime.UtcNow,
                    },
                }
            ).ExecuteCommandAsync();

            await _db.Insertable(
                new[]
                {
                    new UserStore
                    {
                        UserStoreGUID = "manager-store-bri",
                        UserGUID = "manager-user",
                        StoreGUID = "store-bri",
                        IsPrimary = true,
                        CreatedAt = DateTime.UtcNow,
                    },
                    new UserStore
                    {
                        UserStoreGUID = "staff-store-bri",
                        UserGUID = "staff-user",
                        StoreGUID = "store-bri",
                        CreatedAt = DateTime.UtcNow,
                    },
                }
            ).ExecuteCommandAsync();
        }

        private async Task SeedManagerStoreAccessAsync(string userGuid, string storeGuid)
        {
            await _db.Insertable(new UserStore
            {
                UserStoreGUID = $"{userGuid}-{storeGuid}",
                UserGUID = userGuid,
                StoreGUID = storeGuid,
                IsPrimary = true,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
        }

        private async Task SeedStoreManagerRoleAsync(string userGuid)
        {
            await _db.Insertable(new Role
            {
                RoleGUID = "store-manager-role",
                RoleName = "StoreManager",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            await _db.Insertable(new UserRole
            {
                UserRoleGUID = $"{userGuid}-store-manager-role",
                UserGUID = userGuid,
                RoleGUID = "store-manager-role",
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
        }

        private async Task SeedEmployeeProfileAsync(string userGuid, EmployeeType? employeeType)
        {
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = userGuid,
                EmployeeType = employeeType,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
        }

        private async Task<int> CountActiveHolidaysAsync(DateTime holidayDate)
        {
            return await _db.Queryable<AttendanceStoreHoliday>()
                .Where(item =>
                    item.HolidayDate >= holidayDate.Date
                    && item.HolidayDate < holidayDate.Date.AddDays(1)
                    && !item.IsDeleted
                )
                .CountAsync();
        }

        private async Task SeedScheduleAsync()
        {
            await SeedScheduleAsync(
                "schedule-1",
                "BRI",
                "staff-user",
                new DateTime(2026, 5, 18),
                "Active"
            );
        }

        private async Task SeedScheduleAsync(
            string scheduleGuid,
            string storeCode,
            string userGuid,
            DateTime workDate,
            string status,
            TimeSpan? startTime = null,
            TimeSpan? endTime = null
        )
        {
            await _db.Insertable(new AttendanceSchedule
            {
                ScheduleGuid = scheduleGuid,
                StoreCode = storeCode,
                UserGuid = userGuid,
                WorkDate = workDate,
                StartTime = startTime ?? new TimeSpan(9, 0, 0),
                EndTime = endTime ?? new TimeSpan(17, 0, 0),
                Status = status,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
        }

        private async Task SeedPendingScheduleDerivedApprovalsAsync(
            string scheduleGuid,
            string punchGuid)
        {
            await _db.Insertable(CreateStoredPunchForSchedule(
                punchGuid,
                scheduleGuid,
                "BRI",
                "staff-user",
                "ClockIn",
                new DateTime(2026, 5, 18, 9, 10, 0)))
                .ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                new AttendanceApproval
                {
                    ApprovalGuid = $"{scheduleGuid}-derived-punch",
                    SourceType = "Punch",
                    SourceGuid = punchGuid,
                    StoreCode = "BRI",
                    ApplicantUserGuid = "staff-user",
                    ReviewStatus = "Pending",
                    CreatedAt = DateTime.UtcNow,
                },
                new AttendanceApproval
                {
                    ApprovalGuid = $"{scheduleGuid}-derived-overtime",
                    SourceType = "Overtime",
                    SourceGuid = scheduleGuid,
                    StoreCode = "BRI",
                    ApplicantUserGuid = "staff-user",
                    ReviewStatus = "Pending",
                    CandidateOvertimeMinutes = 15,
                    CreatedAt = DateTime.UtcNow,
                },
                new AttendanceApproval
                {
                    ApprovalGuid = $"{scheduleGuid}-derived-missing",
                    SourceType = "MissingClockOut",
                    SourceGuid = scheduleGuid,
                    StoreCode = "BRI",
                    ApplicantUserGuid = "staff-user",
                    ReviewStatus = "Pending",
                    CreatedAt = DateTime.UtcNow,
                },
            }).ExecuteCommandAsync();
        }

        private static AttendancePunch CreateStoredPunch(
            string punchGuid,
            string punchType,
            DateTime punchTimeLocal)
        {
            return new AttendancePunch
            {
                PunchGuid = punchGuid,
                ScheduleGuid = "schedule-1",
                StoreCode = "BRI",
                UserGuid = "staff-user",
                WorkDate = punchTimeLocal.Date,
                StoreTimeZone = "Australia/Brisbane",
                PunchType = punchType,
                PunchTimeLocal = punchTimeLocal,
                PunchTimeUtc = DateTime.SpecifyKind(punchTimeLocal.AddHours(-10), DateTimeKind.Utc),
                Status = "Normal",
                CreatedAt = DateTime.UtcNow,
            };
        }

        private static AttendancePunch CreateStoredPunchForSchedule(
            string punchGuid,
            string scheduleGuid,
            string storeCode,
            string userGuid,
            string punchType,
            DateTime punchTimeLocal)
        {
            return new AttendancePunch
            {
                PunchGuid = punchGuid,
                ScheduleGuid = scheduleGuid,
                StoreCode = storeCode,
                UserGuid = userGuid,
                WorkDate = punchTimeLocal.Date,
                StoreTimeZone = "Australia/Brisbane",
                PunchType = punchType,
                PunchTimeLocal = punchTimeLocal,
                PunchTimeUtc = DateTime.SpecifyKind(punchTimeLocal.AddHours(-10), DateTimeKind.Utc),
                Status = "Normal",
                CreatedAt = DateTime.UtcNow,
            };
        }

        private async Task<string> GetScheduleStatusAsync(string scheduleGuid)
        {
            return await _db.Queryable<AttendanceSchedule>()
                .Where(item => item.ScheduleGuid == scheduleGuid)
                .Select(item => item.Status)
                .FirstAsync();
        }

        private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
        {
            var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(
                typeof(SqlSugarContext)
            );

            var dbField = typeof(SqlSugarContext).GetField(
                "_db",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            dbField!.SetValue(context, db);

            return context;
        }

        private static IEnumerable<Claim> CreateClaims(
            string userGuid,
            string username,
            IEnumerable<string> roles
        )
        {
            yield return new Claim("userGuid", userGuid);
            yield return new Claim("userId", userGuid);
            yield return new Claim(ClaimTypes.NameIdentifier, userGuid);
            yield return new Claim(ClaimTypes.Name, username);

            foreach (var role in roles)
            {
                yield return new Claim(ClaimTypes.Role, role);
            }
        }
    }
}
