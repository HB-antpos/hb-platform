using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using BlazorApp.Api.Models;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests
{
    public sealed class EmployeeProfileServiceTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteConnection _sqliteConnection;
        private readonly SqlSugarClient _db;

        public EmployeeProfileServiceTests()
        {
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
                typeof(User), typeof(EmployeeProfile), typeof(CashRegisterUser),
                typeof(CashierBarcodeReservation), typeof(EmployeeCashierBarcode),
                typeof(EmployeeCashierBarcodePrintAttempt), typeof(EmployeeImageUploadTicket),
                typeof(EmployeeProfileSensitiveChangeRequest), typeof(UserPasswordResetCode)
            );
        }

        [Fact]
        public async Task GetAdminListAsync_MasksAccountNumbers_ButDetailKeepsFullValues()
        {
            await SeedUsersAsync();
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                BankACC = "123456789",
                SuperannuationAccount = "SUPER98765",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var service = CreateService("user-self", "admin");

            var list = await service.GetAdminListAsync(new EmployeeProfileQueryDto());
            var detail = await service.GetAdminDetailAsync("user-self");

            var item = Assert.Single(list.Data!.Items!, row => row.UserGUID == "user-self");
            Assert.Equal("****6789", item.BankAccountNumber);
            Assert.Equal("****8765", item.SuperannuationAccountNumber);
            Assert.Equal("123456789", detail.Data!.BankAccountNumber);
            Assert.Equal("SUPER98765", detail.Data.SuperannuationAccountNumber);
        }

        [Fact]
        public async Task GetSelfAsync_WhenProfileIsMissing_CreatesOnlyAnEmptyProfileForCurrentUser()
        {
            await SeedUsersAsync();

            var result = await CreateService("user-self", "self_user").GetSelfAsync();

            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            var profile = Assert.Single(await _db.Queryable<EmployeeProfile>().ToListAsync());
            Assert.Equal("user-self", profile.UserGUID);
            Assert.Equal("self_user", profile.CreatedBy);
            Assert.Equal("self_user", profile.UpdatedBy);
            Assert.NotEqual(default, profile.CreatedAt);
            Assert.NotNull(profile.UpdatedAt);
            Assert.False(profile.IsDeleted);
            Assert.Equal(0, profile.SensitiveRevision);
            Assert.Null(profile.Phone);
            Assert.Null(profile.BankBSB);
            Assert.Null(profile.BankACC);
            Assert.Null(profile.SuperannuationCompanyName);
            Assert.Null(profile.SuperannuationCompanyCode);
            Assert.Null(profile.SuperannuationAccount);
            Assert.Null(profile.Birthday);
            Assert.Null(profile.Gender);
            Assert.Null(profile.EmployeeType);
            Assert.Null(profile.AvatarUrl);
            Assert.Null(profile.IdentityType);
            Assert.Null(profile.IdentityId);
            Assert.Null(profile.IdentityPhotoUrl);
            Assert.Null(profile.IdentityPhotoObjectKey);
            Assert.Null(profile.Address);
            Assert.Equal(profile.EmployeeInfoId, result.Data!.EmployeeInfoId);
        }

        [Fact]
        public async Task GetSelfAsync_WhenOnlyDeletedProfileExists_ReusesAndResetsItWithoutExposingOldData()
        {
            await SeedUsersAsync();
            var oldCreatedAt = DateTime.UtcNow.AddDays(-2);
            var oldUpdatedAt = DateTime.UtcNow.AddDays(-1);
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                Phone = "0400000000",
                BankBSB = "123-456",
                BankACC = "old-bank-account",
                SuperannuationCompanyName = "Old Super",
                SuperannuationCompanyCode = "SUPER-CODE",
                SuperannuationAccount = "old-super-account",
                Birthday = DateTime.UtcNow.AddYears(-30),
                Gender = EmployeeGender.Female,
                EmployeeType = EmployeeType.FullTime,
                AvatarUrl = "https://old/avatar.jpg",
                IdentityType = "passport",
                IdentityId = "old-identity-id",
                IdentityPhotoUrl = "https://old/identity.jpg",
                IdentityPhotoObjectKey = "employee-profiles/user-self/identity/old.jpg",
                Address = "Old address",
                SensitiveRevision = 9,
                IsDeleted = true,
                CreatedAt = oldCreatedAt,
                CreatedBy = "old-creator",
                UpdatedAt = oldUpdatedAt,
                UpdatedBy = "old-updater",
            }).ExecuteCommandAsync();
            var deleted = await _db.Queryable<EmployeeProfile>().FirstAsync();

            var adminDetail = await CreateService("admin-user", "admin").GetAdminDetailAsync("user-self");
            Assert.True(adminDetail.Success);
            Assert.Null(adminDetail.Data!.EmployeeInfoId);
            Assert.True((await _db.Queryable<EmployeeProfile>().FirstAsync()).IsDeleted);

            var result = await CreateService("user-self", "self_user").GetSelfAsync();

            Assert.True(result.Success);
            var profile = Assert.Single(await _db.Queryable<EmployeeProfile>().ToListAsync());
            Assert.Equal(deleted.EmployeeInfoId, profile.EmployeeInfoId);
            Assert.Equal("user-self", profile.UserGUID);
            Assert.False(profile.IsDeleted);
            Assert.Equal(0, profile.SensitiveRevision);
            Assert.True(profile.CreatedAt > oldCreatedAt);
            Assert.Equal("self_user", profile.CreatedBy);
            Assert.True(profile.UpdatedAt > oldUpdatedAt);
            Assert.Equal("self_user", profile.UpdatedBy);
            Assert.Null(profile.Phone);
            Assert.Null(profile.BankBSB);
            Assert.Null(profile.BankACC);
            Assert.Null(profile.SuperannuationCompanyName);
            Assert.Null(profile.SuperannuationCompanyCode);
            Assert.Null(profile.SuperannuationAccount);
            Assert.Null(profile.Birthday);
            Assert.Null(profile.Gender);
            Assert.Null(profile.EmployeeType);
            Assert.Null(profile.AvatarUrl);
            Assert.Null(profile.IdentityType);
            Assert.Null(profile.IdentityId);
            Assert.Null(profile.IdentityPhotoUrl);
            Assert.Null(profile.IdentityPhotoObjectKey);
            Assert.Null(profile.Address);
            Assert.Equal(profile.EmployeeInfoId, result.Data!.EmployeeInfoId);
            Assert.Null(result.Data.BankAccountNumber);
            Assert.Null(result.Data.IdentityId);
        }

        [Fact]
        public async Task GetSelfAsync_WhenReadConcurrently_CreatesOnlyOneProfile()
        {
            await SeedUsersAsync();
            using var firstDb = CreateAdditionalDb();
            using var secondDb = CreateAdditionalDb();
            var first = CreateService("user-self", "self_user", firstDb).GetSelfAsync();
            var second = CreateService("user-self", "self_user", secondDb).GetSelfAsync();

            var results = await Task.WhenAll(first, second);

            Assert.All(results, result => Assert.True(result.Success));
            var profile = Assert.Single(await _db.Queryable<EmployeeProfile>()
                .Where(item => item.UserGUID == "user-self")
                .ToListAsync());
            Assert.All(results, result => Assert.Equal(profile.EmployeeInfoId, result.Data!.EmployeeInfoId));
        }

        [Fact]
        public async Task GetSelfAsync_WhenProfileAlreadyExists_DoesNotModifyIt()
        {
            await SeedUsersAsync();
            var createdAt = DateTime.UtcNow.AddDays(-2);
            var updatedAt = DateTime.UtcNow.AddDays(-1);
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                Phone = "0400000000",
                BankACC = "existing-sensitive-value",
                Address = "Existing address",
                SensitiveRevision = 7,
                CreatedAt = createdAt,
                CreatedBy = "creator",
                UpdatedAt = updatedAt,
                UpdatedBy = "updater",
            }).ExecuteCommandAsync();

            var result = await CreateService("user-self", "self_user").GetSelfAsync();

            Assert.True(result.Success);
            var profile = Assert.Single(await _db.Queryable<EmployeeProfile>().ToListAsync());
            Assert.Equal("0400000000", profile.Phone);
            Assert.Equal("existing-sensitive-value", profile.BankACC);
            Assert.Equal("Existing address", profile.Address);
            Assert.Equal(7, profile.SensitiveRevision);
            Assert.Equal(createdAt, profile.CreatedAt);
            Assert.Equal("creator", profile.CreatedBy);
            Assert.Equal(updatedAt, profile.UpdatedAt);
            Assert.Equal("updater", profile.UpdatedBy);
        }

        [Fact]
        public async Task GetAdminDetailAsync_WhenProfileIsMissing_DoesNotCreateOne()
        {
            await SeedUsersAsync();

            var result = await CreateService("admin-user", "admin").GetAdminDetailAsync("user-self");

            Assert.True(result.Success);
            Assert.Null(result.Data!.EmployeeInfoId);
            Assert.Equal(0, await _db.Queryable<EmployeeProfile>().CountAsync());
        }

        [Fact]
        public async Task GetSelfAsync_WhenCurrentUserOrUserRecordIsMissing_DoesNotCreateAProfile()
        {
            var currentUserMissing = await CreateService(string.Empty, "self_user").GetSelfAsync();
            var userMissing = await CreateService("missing-user", "self_user").GetSelfAsync();

            Assert.False(currentUserMissing.Success);
            Assert.Equal("CURRENT_USER_NOT_FOUND", currentUserMissing.ErrorCode);
            Assert.False(userMissing.Success);
            Assert.Equal("USER_NOT_FOUND", userMissing.ErrorCode);
            Assert.Equal(0, await _db.Queryable<EmployeeProfile>().CountAsync());
        }

        [Fact]
        public async Task UpsertSelfAsync_WhenPayloadContainsAnotherUserGuid_OnlyUpdatesCurrentUser()
        {
            await SeedUsersAsync();

            await _db.Insertable(
                new EmployeeProfile
                {
                    EmployeeInfoId = 1,
                    UserGUID = "user-self",
                    Address = "Old self address",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                }
            ).ExecuteCommandAsync();

            await _db.Insertable(
                new EmployeeProfile
                {
                    EmployeeInfoId = 2,
                    UserGUID = "user-other",
                    Address = "Original other address",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                }
            ).ExecuteCommandAsync();

            var service = CreateService("user-self", "self_user");

            var result = await service.UpsertSelfAsync(
                new EmployeeProfileUpsertDto
                {
                    UserGUID = "user-other",
                    Address = "Updated by self endpoint",
                    Gender = "female",
                    EmploymentType = "partTime",
                }
            );

            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            Assert.Equal("user-self", result.Data!.UserGUID);

            var selfProfile = await _db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == "user-self");
            var otherProfile = await _db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == "user-other");

            Assert.Equal("Updated by self endpoint", selfProfile.Address);
            Assert.Equal(EmployeeGender.Female, selfProfile.Gender);
            Assert.Equal(EmployeeType.PartTime, selfProfile.EmployeeType);
            Assert.Equal("Original other address", otherProfile.Address);
        }

        [Fact]
        public async Task UpsertSelfAsync_WhenEmailAndEmploymentTypeAreOmitted_PreservesThemAndSavesPhone()
        {
            await SeedUsersAsync();
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                Phone = "0400000000",
                EmployeeType = EmployeeType.FullTime,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var result = await CreateService("user-self", "self_user").UpsertSelfAsync(new()
            {
                Phone = "0499999999",
            });

            Assert.True(result.Success);
            var user = await _db.Queryable<User>().FirstAsync(item => item.UserGUID == "user-self");
            var profile = await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "user-self");
            Assert.Equal("self@example.com", user.Email);
            Assert.Equal("0499999999", profile.Phone);
            Assert.Equal(EmployeeType.FullTime, profile.EmployeeType);
        }

        [Fact]
        public void EmployeeProfileUpsertDto_TracksJsonFieldPresence()
        {
            var omitted = JsonSerializer.Deserialize<EmployeeProfileUpsertDto>("{}");
            var explicitNull = JsonSerializer.Deserialize<EmployeeProfileUpsertDto>(
                "{\"Email\":null,\"EmploymentType\":null}"
            );

            Assert.NotNull(omitted);
            Assert.False(omitted!.HasEmail);
            Assert.False(omitted.HasEmploymentType);
            Assert.NotNull(explicitNull);
            Assert.True(explicitNull!.HasEmail);
            Assert.True(explicitNull.HasEmploymentType);
        }

        [Fact]
        public async Task UpsertSelfAsync_WhenEmailChanges_RequiresVerificationWithoutPartialWrite()
        {
            await SeedUsersAsync();
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                Phone = "0400000000",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            // 邮箱是找回密码渠道：自助资料保存不能直接改，须走 api/Auth/email-change 验证新邮箱。
            var result = await CreateService("user-self", "self_user").UpsertSelfAsync(new()
            {
                Email = "self-new@example.com",
                Phone = "0499999999",
            });

            Assert.False(result.Success);
            Assert.Equal(EmployeeProfileService.EmailRequiresVerificationCode, result.ErrorCode);
            Assert.Equal("self@example.com", (await _db.Queryable<User>().FirstAsync(item => item.UserGUID == "user-self")).Email);
            Assert.Equal("0400000000", (await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "user-self")).Phone);
        }

        [Fact]
        public async Task UpsertSelfAsync_WhenLegacyClientEchoesCurrentEmail_SavesOtherFields()
        {
            await SeedUsersAsync();

            // 旧版客户端会回传当前邮箱（大小写、空格可能不同），等值时照常保存其余字段。
            var result = await CreateService("user-self", "self_user").UpsertSelfAsync(new()
            {
                UserGUID = "user-other",
                Email = "  SELF@example.com  ",
                Phone = "0499999999",
            });

            Assert.True(result.Success, result.Message);
            Assert.Equal("self@example.com", (await _db.Queryable<User>().FirstAsync(item => item.UserGUID == "user-self")).Email);
            Assert.Equal("other@example.com", (await _db.Queryable<User>().FirstAsync(item => item.UserGUID == "user-other")).Email);
            Assert.Equal("0499999999", (await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "user-self")).Phone);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-an-email")]
        [InlineData("a@")]
        public async Task UpsertSelfAsync_WhenEmailIsInvalid_ReturnsValidationErrorAndDoesNotWrite(string? email)
        {
            await SeedUsersAsync();
            var result = await CreateService("user-self", "self_user").UpsertSelfAsync(new()
            {
                Email = email,
                Phone = "0499999999",
            });

            Assert.False(result.Success);
            Assert.Equal("INVALID_EMAIL", result.ErrorCode);
            Assert.Equal("self@example.com", (await _db.Queryable<User>().FirstAsync(item => item.UserGUID == "user-self")).Email);
            Assert.Null(await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "user-self"));
        }

        [Fact]
        public async Task UpsertSelfAsync_WhenEmailIsTooLong_ReturnsValidationError()
        {
            await SeedUsersAsync();
            var result = await CreateService("user-self", "self_user").UpsertSelfAsync(new()
            {
                Email = new string('a', 250) + "@x.com",
            });

            Assert.False(result.Success);
            Assert.Equal("INVALID_EMAIL", result.ErrorCode);
        }

        [Fact]
        public async Task UpsertSelfAsync_WhenPositionChangesWithoutPermission_ReturnsForbiddenWithoutOtherChanges()
        {
            await SeedUsersAsync();
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                Phone = "0400000000",
                EmployeeType = EmployeeType.FullTime,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var result = await CreateService("user-self", "self_user", _db, omitRoleService: true).UpsertSelfAsync(new()
            {
                EmploymentType = "partTime",
                Phone = "0499999999",
            });

            Assert.False(result.Success);
            Assert.Equal("FORBIDDEN", result.ErrorCode);
            var profile = await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "user-self");
            Assert.Equal("0400000000", profile.Phone);
            Assert.Equal(EmployeeType.FullTime, profile.EmployeeType);
            Assert.Equal("self@example.com", (await _db.Queryable<User>().FirstAsync(item => item.UserGUID == "user-self")).Email);
        }

        [Fact]
        public async Task UpsertSelfAsync_WhenRoleServiceDeniesPositionChange_ReturnsForbidden()
        {
            await SeedUsersAsync();
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                EmployeeType = EmployeeType.FullTime,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var role = new Mock<IRoleService>();
            role.Setup(mock => mock.UserHasPermissionAsync("user-self", Permissions.EmployeeProfiles.EditPositionType))
                .ReturnsAsync(ApiResponse<bool>.OK(false));

            var result = await CreateService("user-self", "self_user", _db, roleService: role.Object).UpsertSelfAsync(new()
            {
                EmploymentType = "partTime",
            });

            Assert.False(result.Success);
            Assert.Equal("FORBIDDEN", result.ErrorCode);
        }

        [Fact]
        public async Task UpsertSelfAsync_WhenPositionAliasIsEquivalent_DoesNotRequirePermission()
        {
            await SeedUsersAsync();
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                EmployeeType = EmployeeType.FullTime,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var role = new Mock<IRoleService>();
            var result = await CreateService("user-self", "self_user", _db, roleService: role.Object).UpsertSelfAsync(new()
            {
                EmploymentType = "full_time",
                Phone = "0499999999",
            });

            Assert.True(result.Success);
            role.Verify(mock => mock.UserHasPermissionAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.Equal("0499999999", (await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "user-self")).Phone);
        }

        [Fact]
        public async Task UpsertSelfAsync_WhenPositionPermissionGranted_UpdatesPosition()
        {
            await SeedUsersAsync();
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                EmployeeType = EmployeeType.FullTime,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var role = new Mock<IRoleService>();
            role.Setup(mock => mock.UserHasPermissionAsync("user-self", Permissions.EmployeeProfiles.EditPositionType))
                .ReturnsAsync(ApiResponse<bool>.OK(true));

            var result = await CreateService("user-self", "self_user", _db, roleService: role.Object).UpsertSelfAsync(new()
            {
                EmploymentType = "temporary",
            });

            Assert.True(result.Success);
            Assert.Equal(EmployeeType.Temporary, (await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "user-self")).EmployeeType);
        }

        [Fact]
        public async Task UpsertSelfAsync_UsesNonSensitiveWhitelist_AndPreservesAdminSensitiveData()
        {
            await SeedUsersAsync();
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                BankACC = "admin-new",
                SensitiveRevision = 4,
                Address = "old address",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            using var selfDb = CreateAdditionalDb();
            var updates = new List<string>();
            selfDb.Aop.OnLogExecuting = (sql, _) =>
            {
                if (sql.Contains("EmployeeProfile", StringComparison.OrdinalIgnoreCase)
                    && sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                {
                    updates.Add(sql);
                }
            };
            var result = await CreateService("user-self", "self_user", selfDb).UpsertSelfAsync(new()
            {
                Address = "self address",
                ConfirmSupersedePendingSensitiveChangeRequest = true,
            });

            Assert.True(result.Success);
            var profile = await _db.Queryable<EmployeeProfile>().FirstAsync();
            Assert.Equal("self address", profile.Address);
            Assert.Equal("admin-new", profile.BankACC);
            Assert.Equal(4, profile.SensitiveRevision);
            var updateSql = Assert.Single(updates);
            Assert.DoesNotContain("BankACC", updateSql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SensitiveRevision", updateSql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("IdentityId", updateSql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task UpsertAdminAsync_WhenAnotherAdminCommitsWhileWaiting_UsesLatestRevision()
        {
            await SeedUsersAsync();
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                BankACC = "formal-old",
                SensitiveRevision = 3,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            using var adminADb = CreateAdditionalDb();
            using var adminBDb = CreateAdditionalDb();
            var adminARead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            adminADb.Aop.OnLogExecuted = (sql, _) =>
            {
                if (sql.Contains("EmployeeProfile", StringComparison.OrdinalIgnoreCase)
                    && sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    adminARead.TrySetResult();
                }
            };

            var adminBLock = await EmployeeProfileMediaLock.AcquireAsync(
                adminBDb,
                "user-self",
                "sensitive-change"
            );
            var adminASave = CreateService("user-self", "admin-a", adminADb).UpsertAdminAsync(
                "user-self",
                new EmployeeProfileUpsertDto { BankAccountNumber = "admin-a" }
            );
            await Task.Delay(200);
            var readBeforeAdminBReleasedLock = adminARead.Task.IsCompleted;
            await adminBDb.Updateable<EmployeeProfile>()
                .SetColumns(item => item.BankACC == "admin-b")
                .SetColumns(item => item.SensitiveRevision == 4)
                .Where(item => item.UserGUID == "user-self")
                .ExecuteCommandAsync();
            await adminBLock.DisposeAsync();

            Assert.True((await adminASave).Success);
            Assert.False(readBeforeAdminBReleasedLock);
            var profile = await _db.Queryable<EmployeeProfile>().FirstAsync();
            Assert.Equal("admin-a", profile.BankACC);
            Assert.Equal(5, profile.SensitiveRevision);
        }

        [Fact]
        public async Task UpsertAdminAsync_WhenPendingRequestAppearsWhileWaiting_RequiresAtomicConfirmation()
        {
            await SeedUsersAsync();
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                BankACC = "formal-old",
                Address = "old address",
                SensitiveRevision = 3,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            using var adminDb = CreateAdditionalDb();
            using var writerDb = CreateAdditionalDb();
            var writerLock = await EmployeeProfileMediaLock.AcquireAsync(
                writerDb,
                "user-self",
                "sensitive-change"
            );
            var service = CreateService(
                "admin-user",
                "admin",
                adminDb,
                includeSensitiveChangeService: true
            );

            var blockedSave = service.UpsertAdminAsync(
                "user-self",
                new EmployeeProfileUpsertDto { BankAccountNumber = "admin-new" }
            );
            await Task.Delay(200);
            await writerDb.Insertable(new EmployeeProfileSensitiveChangeRequest
            {
                UserGUID = "user-self",
                BankAccountNumber = "employee-proposed",
                Status = EmployeeProfileSensitiveChangeStatus.Pending,
                BaseSensitiveRevision = 3,
                SubmittedAt = DateTime.UtcNow,
                SubmittedBy = "employee",
            }).ExecuteCommandAsync();
            await writerLock.DisposeAsync();

            var confirmationRequired = await blockedSave;
            Assert.False(confirmationRequired.Success);
            Assert.Equal(
                EmployeeProfileService.PendingChangeConfirmationRequiredCode,
                confirmationRequired.ErrorCode
            );
            var unchanged = await _db.Queryable<EmployeeProfile>().FirstAsync();
            Assert.Equal("formal-old", unchanged.BankACC);
            Assert.Equal("old address", unchanged.Address);
            Assert.Equal(3, unchanged.SensitiveRevision);

            var nonSensitiveSave = await service.UpsertAdminAsync(
                "user-self",
                new EmployeeProfileUpsertDto
                {
                    BankAccountNumber = "formal-old",
                    Address = "new address",
                }
            );
            Assert.True(nonSensitiveSave.Success);
            Assert.Equal(
                EmployeeProfileSensitiveChangeStatus.Pending,
                (await _db.Queryable<EmployeeProfileSensitiveChangeRequest>().FirstAsync()).Status
            );

            var confirmedSave = await service.UpsertAdminAsync(
                "user-self",
                new EmployeeProfileUpsertDto
                {
                    BankAccountNumber = "admin-new",
                    Address = "new address",
                    ConfirmSupersedePendingSensitiveChangeRequest = true,
                    ExpectedSensitiveRevision = 3,
                }
            );
            Assert.True(confirmedSave.Success);
            var saved = await _db.Queryable<EmployeeProfile>().FirstAsync();
            Assert.Equal("admin-new", saved.BankACC);
            Assert.Equal(4, saved.SensitiveRevision);
            Assert.Equal(
                EmployeeProfileSensitiveChangeStatus.Superseded,
                (await _db.Queryable<EmployeeProfileSensitiveChangeRequest>().FirstAsync()).Status
            );
        }

        [Fact]
        public async Task UpsertAdminAsync_WhenEmailChanges_UpdatesDirectlyAndInvalidatesOutstandingCodes()
        {
            await SeedUsersAsync();
            await _db.Insertable(new UserPasswordResetCode
            {
                UserGUID = "user-self",
                Purpose = UserPasswordResetCode.PurposeInvite,
                CodeHash = "x",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
                CreatedAtUtc = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            // 管理员后台直改邮箱不需要验证，但发往旧邮箱的验证码必须随之作废。
            var result = await CreateService("admin-user", "admin").UpsertAdminAsync("user-self", new()
            {
                Email = "admin-set@example.com",
            });

            Assert.True(result.Success, result.Message);
            Assert.Equal("admin-set@example.com", (await _db.Queryable<User>().FirstAsync(item => item.UserGUID == "user-self")).Email);
            Assert.False(await _db.Queryable<UserPasswordResetCode>().AnyAsync(item => item.ConsumedAtUtc == null));
        }

        [Fact]
        public async Task UpsertAdminAsync_UsesOptionalSensitiveRevisionCasOnlyForSensitiveChanges()
        {
            await SeedUsersAsync();
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                BankACC = "formal-v4",
                Address = "old address",
                SensitiveRevision = 4,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var service = CreateService("admin-user", "admin");
            var detail = await service.GetAdminDetailAsync("user-self");
            Assert.Equal(4, detail.Data!.SensitiveRevision);

            var nonSensitive = await service.UpsertAdminAsync("user-self", new()
            {
                BankAccountNumber = "formal-v4",
                Address = "new address",
                ExpectedSensitiveRevision = 3,
            });
            Assert.True(nonSensitive.Success);

            var stale = await service.UpsertAdminAsync("user-self", new()
            {
                BankAccountNumber = "stale-write",
                Address = "must-not-write",
                ExpectedSensitiveRevision = 3,
            });
            Assert.False(stale.Success);
            Assert.Equal(EmployeeProfileSensitiveChangeService.VersionConflictCode, stale.ErrorCode);
            var unchanged = await _db.Queryable<EmployeeProfile>().FirstAsync();
            Assert.Equal("formal-v4", unchanged.BankACC);
            Assert.Equal("new address", unchanged.Address);
            Assert.Equal(4, unchanged.SensitiveRevision);

            var current = await service.UpsertAdminAsync("user-self", new()
            {
                BankAccountNumber = "current-write",
                Address = "current address",
                ExpectedSensitiveRevision = 4,
            });
            Assert.True(current.Success);
            Assert.Equal(5, current.Data!.SensitiveRevision);
        }

        [Fact]
        public async Task UpsertSelfAsync_WhenPayloadContainsImageUrls_PreservesSavedImages()
        {
            await SeedUsersAsync();
            await _db.Insertable(
                new EmployeeProfile
                {
                    EmployeeInfoId = 1,
                    UserGUID = "user-self",
                    AvatarUrl = "https://saved/avatar.jpg",
                    IdentityPhotoUrl = "https://legacy/identity.jpg",
                    IdentityPhotoObjectKey = "employee-profiles/user-self/identity/saved.jpg",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                }
            ).ExecuteCommandAsync();

            var result = await CreateService("user-self", "self_user").UpsertSelfAsync(
                new EmployeeProfileUpsertDto
                {
                    Address = "New address",
                    AvatarUrl = "https://temporary/avatar.jpg?signature=bad",
                    IdentityPhotoUrl = "https://temporary/identity.jpg?signature=bad",
                }
            );

            Assert.True(result.Success);
            var profile = await _db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == "user-self");
            Assert.Equal("https://saved/avatar.jpg", profile.AvatarUrl);
            Assert.Equal("https://legacy/identity.jpg", profile.IdentityPhotoUrl);
            Assert.Equal("employee-profiles/user-self/identity/saved.jpg", profile.IdentityPhotoObjectKey);
        }

        [Fact]
        public async Task UpsertAdminAsync_WhenPrivateIdentityIsManaged_PreservesStableAssociation()
        {
            await SeedUsersAsync();
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-other",
                AvatarUrl = "https://old/avatar.jpg",
                IdentityPhotoUrl = "https://old/identity.jpg",
                IdentityPhotoObjectKey = "employee-profiles/user-other/identity/private.jpg",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var result = await CreateService("user-self", "admin").UpsertAdminAsync(
                "user-other",
                new EmployeeProfileUpsertDto
                {
                    Address = "admin changed address",
                    AvatarUrl = "https://new/avatar.jpg",
                    IdentityPhotoUrl = "https://temporary/identity.jpg?sign=expired",
                }
            );

            Assert.True(result.Success);
            var profile = await _db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == "user-other");
            Assert.Equal("https://new/avatar.jpg", profile.AvatarUrl);
            Assert.Equal("https://old/identity.jpg", profile.IdentityPhotoUrl);
            Assert.Equal("employee-profiles/user-other/identity/private.jpg", profile.IdentityPhotoObjectKey);
            Assert.Equal("admin changed address", profile.Address);
        }

        [Fact]
        public async Task UpsertAdminAsync_WhenIdentityIsLegacy_AllowsLegacyUrlEditing()
        {
            await SeedUsersAsync();
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-other",
                IdentityPhotoUrl = "https://old/identity.jpg",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var result = await CreateService("user-self", "admin").UpsertAdminAsync(
                "user-other",
                new EmployeeProfileUpsertDto { IdentityPhotoUrl = "https://new/identity.jpg" }
            );

            Assert.True(result.Success);
            var profile = await _db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == "user-other");
            Assert.Equal("https://new/identity.jpg", profile.IdentityPhotoUrl);
            Assert.Null(profile.IdentityPhotoObjectKey);
        }

        [Fact]
        public async Task CashierBarcode_RefreshNeverReusesHistoryAndKeepsOneActiveRecord()
        {
            var service = CreateBarcodeService("user-self", "self_user");

            var first = await service.RefreshAsync();
            var second = await service.RefreshAsync();

            Assert.True(first.Success);
            Assert.True(second.Success);
            Assert.NotEqual(first.Data!.Barcode, second.Data!.Barcode);
            Assert.Equal(13, second.Data.Barcode!.Length);
            Assert.Equal(
                1,
                await _db.Queryable<EmployeeCashierBarcode>()
                    .CountAsync(item => item.UserGUID == "user-self" && item.Status)
            );
            Assert.Equal(
                2,
                await _db.Queryable<EmployeeCashierBarcode>()
                    .CountAsync(item => item.UserGUID == "user-self")
            );
        }

        [Fact]
        public async Task CashierBarcode_GetOnlyReturnsCurrentUsersActiveBarcode()
        {
            const string selfBarcode = "2911111111118";
            const string otherBarcode = "2922222222226";
            await CreateBarcodeService("user-self", "self_user", () => selfBarcode).RefreshAsync();
            await CreateBarcodeService("user-other", "other_user", () => otherBarcode).RefreshAsync();

            var result = await CreateBarcodeService("user-self", "self_user").GetAsync();

            Assert.True(result.Success);
            Assert.Equal(selfBarcode, result.Data!.Barcode);
            Assert.NotEqual(otherBarcode, result.Data.Barcode);
        }

        [Fact]
        public async Task CashierBarcode_RefreshOnlyDeactivatesCurrentUsersBarcode()
        {
            const string oldSelfBarcode = "2911111111118";
            const string newSelfBarcode = "2933333333334";
            const string otherBarcode = "2922222222226";
            await CreateBarcodeService("user-self", "self_user", () => oldSelfBarcode).RefreshAsync();
            await CreateBarcodeService("user-other", "other_user", () => otherBarcode).RefreshAsync();

            var result = await CreateBarcodeService(
                "user-self",
                "self_user",
                () => newSelfBarcode
            ).RefreshAsync();

            Assert.True(result.Success);
            Assert.Equal(newSelfBarcode, result.Data!.Barcode);
            var other = await _db.Queryable<EmployeeCashierBarcode>()
                .SingleAsync(item => item.UserGUID == "user-other" && item.Status);
            Assert.Equal(otherBarcode, other.Barcode);
        }

        [Fact]
        public async Task CashierBarcode_ConfirmPrintCannotUseAnotherUsersBarcode()
        {
            const string selfBarcode = "2911111111118";
            const string otherBarcode = "2922222222226";
            await CreateBarcodeService("user-self", "self_user", () => selfBarcode).RefreshAsync();
            await CreateBarcodeService("user-other", "other_user", () => otherBarcode).RefreshAsync();

            var result = await CreateBarcodeService("user-self", "self_user").ConfirmPrintAsync(
                new EmployeeCashierBarcodePrintConfirmationRequest
                {
                    Barcode = otherBarcode,
                    PrintAttemptId = Guid.NewGuid(),
                }
            );

            Assert.False(result.Success);
            Assert.Equal("CASHIER_BARCODE_CHANGED", result.Code);
            var other = await _db.Queryable<EmployeeCashierBarcode>()
                .SingleAsync(item => item.UserGUID == "user-other" && item.Status);
            Assert.Equal(0, other.PrintCount);
            Assert.Equal(0, await _db.Queryable<EmployeeCashierBarcodePrintAttempt>().CountAsync());
        }

        [Fact]
        public async Task CashierBarcode_RefreshDeactivatesActiveLegacyBarcodeForSameUser()
        {
            await _db.Insertable(new CashRegisterUser
            {
                HGUID = "legacy-self",
                UserGUID = "user-self",
                UserBarcode = "LEGACY-CODE",
                StoreCode = "S1",
                OperatorUser = "self_user",
                LoginRole = "2",
                Remark = string.Empty,
                Status = true,
                Creator = "seed",
                LastModifier = "seed",
                CreateDate = DateTime.UtcNow,
                LastModifyDate = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var result = await CreateBarcodeService("user-self", "self_user").RefreshAsync();

            Assert.True(result.Success);
            Assert.False((await _db.Queryable<CashRegisterUser>()
                .FirstAsync(item => item.HGUID == "legacy-self")).Status);
        }

        [Fact]
        public async Task CashierBarcode_ConfirmPrintIncrementsCurrentActiveBarcode()
        {
            var service = CreateBarcodeService("user-self", "self_user");
            await service.RefreshAsync();

            var current = await service.GetAsync();
            var result = await service.ConfirmPrintAsync(
                new EmployeeCashierBarcodePrintConfirmationRequest
                {
                    Barcode = current.Data!.Barcode!,
                    PrintAttemptId = Guid.NewGuid(),
                }
            );

            Assert.True(result.Success);
            Assert.Equal(1, result.Data!.PrintCount);
        }

        [Fact]
        public async Task CashierBarcode_ConfirmOldBarcodeAfterRefresh_DoesNotIncrementNewBarcode()
        {
            var service = CreateBarcodeService("user-self", "self_user");
            var oldBarcode = (await service.RefreshAsync()).Data!.Barcode!;
            var currentBarcode = (await service.RefreshAsync()).Data!.Barcode!;

            var result = await service.ConfirmPrintAsync(
                new EmployeeCashierBarcodePrintConfirmationRequest
                {
                    Barcode = oldBarcode,
                    PrintAttemptId = Guid.NewGuid(),
                }
            );

            Assert.False(result.Success);
            Assert.Equal("CASHIER_BARCODE_CHANGED", result.Code);
            var current = await _db.Queryable<EmployeeCashierBarcode>()
                .FirstAsync(item => item.Barcode == currentBarcode);
            Assert.Equal(0, current.PrintCount);
        }

        [Fact]
        public async Task CashierBarcode_WhenReservationCollides_RollsBackAndRetriesWithNewBarcode()
        {
            const string reserved = "2900000000001";
            const string available = "2912345678906";
            await _db.Insertable(new CashierBarcodeReservation
            {
                Barcode = reserved,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var candidates = new Queue<string>(new[] { reserved, available });
            var service = CreateBarcodeService(
                "user-self",
                "self_user",
                () => candidates.Dequeue()
            );

            var result = await service.RefreshAsync();

            Assert.True(result.Success);
            Assert.Equal(available, result.Data!.Barcode);
            Assert.Equal(2, await _db.Queryable<CashierBarcodeReservation>().CountAsync());
            Assert.Equal(1, await _db.Queryable<EmployeeCashierBarcode>().CountAsync());
        }

        [Fact]
        public async Task CashierBarcode_LegacyFullSyncDelete_DoesNotDeleteEmployeeBarcode()
        {
            var service = CreateBarcodeService("user-self", "self_user");
            var created = await service.RefreshAsync();
            await _db.Deleteable<CashRegisterUser>().ExecuteCommandAsync();

            var afterLegacyDelete = await service.GetAsync();

            Assert.True(afterLegacyDelete.Success);
            Assert.True(afterLegacyDelete.Data!.Exists);
            Assert.Equal(created.Data!.Barcode, afterLegacyDelete.Data.Barcode);
        }

        [Fact]
        public async Task CashierBarcode_PrintAttemptIsIdempotent()
        {
            var service = CreateBarcodeService("user-self", "self_user");
            var barcode = (await service.RefreshAsync()).Data!.Barcode!;
            var attemptId = Guid.NewGuid();
            var request = new EmployeeCashierBarcodePrintConfirmationRequest
            {
                Barcode = barcode,
                PrintAttemptId = attemptId,
            };

            var first = await service.ConfirmPrintAsync(request);
            var retry = await service.ConfirmPrintAsync(request);
            var second = await service.ConfirmPrintAsync(
                new EmployeeCashierBarcodePrintConfirmationRequest
                {
                    Barcode = barcode,
                    PrintAttemptId = Guid.NewGuid(),
                }
            );

            Assert.Equal(1, first.Data!.PrintCount);
            Assert.Equal(1, retry.Data!.PrintCount);
            Assert.Equal(2, second.Data!.PrintCount);
            Assert.Equal(2, await _db.Queryable<EmployeeCashierBarcodePrintAttempt>().CountAsync());
        }

        [Fact]
        public async Task CashierBarcode_RetryConfirmedAttemptAfterRefresh_ReturnsChanged()
        {
            var service = CreateBarcodeService("user-self", "self_user");
            var oldBarcode = (await service.RefreshAsync()).Data!.Barcode!;
            var request = new EmployeeCashierBarcodePrintConfirmationRequest
            {
                Barcode = oldBarcode,
                PrintAttemptId = Guid.NewGuid(),
            };
            Assert.True((await service.ConfirmPrintAsync(request)).Success);
            var current = (await service.RefreshAsync()).Data!.Barcode!;

            var retry = await service.ConfirmPrintAsync(request);

            Assert.False(retry.Success);
            Assert.Equal("CASHIER_BARCODE_CHANGED", retry.Code);
            Assert.Equal(current, retry.Data?.Barcode);
        }

        [Fact]
        public async Task DeleteIdentity_WhenCosDeleteFails_PreservesDatabaseAssociation()
        {
            await SeedUsersAsync();
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                IdentityPhotoObjectKey = "employee-profiles/user-self/identity/old.jpg",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var storage = new FakeTencentCloudUploadService { DeleteSucceeds = false };
            var service = CreateMediaService("user-self", "self_user", storage);

            var result = await service.DeleteAsync("identity");

            Assert.False(result.Success);
            var profile = await _db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == "user-self");
            Assert.Equal("employee-profiles/user-self/identity/old.jpg", profile.IdentityPhotoObjectKey);
        }

        [Fact]
        public async Task CompleteImage_WhenBytesAreForged_PreservesOldImage()
        {
            await SeedUsersAsync();
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                AvatarUrl = "https://saved/avatar.jpg",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var key = "pending/employee-profiles/user-self/avatar/new.jpg";
            await _db.Insertable(new EmployeeImageUploadTicket
            {
                PendingObjectKey = key,
                UserGUID = "user-self",
                Kind = "avatar",
                ContentType = "image/jpeg",
                FileSize = 18,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            }).ExecuteCommandAsync();
            var storage = new FakeTencentCloudUploadService
            {
                Metadata = new TencentCloudUploadService.CosObjectMetadata
                {
                    ContentLength = 18,
                    ContentType = "image/jpeg",
                    Owner = "user-self",
                    Kind = "avatar",
                    DeclaredContentType = "image/jpeg",
                    DeclaredFileSize = 18,
                },
                Bytes = System.Text.Encoding.UTF8.GetBytes("this is not a jpeg"),
            };
            var service = CreateMediaService("user-self", "self_user", storage);

            var result = await service.CompleteAsync(
                new EmployeeImageCompleteRequest { Kind = "avatar", ObjectKey = key }
            );

            Assert.False(result.Success);
            var profile = await _db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == "user-self");
            Assert.Equal("https://saved/avatar.jpg", profile.AvatarUrl);
            Assert.Contains(key, storage.DeletedKeys);
        }

        [Fact]
        public async Task CreateUploadSignature_CreatesPrivatePendingTicketAndLimitsOutstandingUploads()
        {
            var storage = new FakeTencentCloudUploadService();
            var service = CreateMediaService("user-self", "self_user", storage);
            var request = new EmployeeImageUploadSignatureRequest
            {
                Kind = "avatar",
                FileName = "avatar.jpg",
                ContentType = "image/jpeg",
                FileSize = 100,
            };

            var first = await service.CreateUploadSignatureAsync(request);

            Assert.True(first.Success);
            Assert.StartsWith("pending/employee-profiles/user-self/avatar/", first.Data!.ObjectKey);
            Assert.Equal("private", first.Data.Headers["x-cos-acl"]);
            Assert.Equal(1, await _db.Queryable<EmployeeImageUploadTicket>().CountAsync());

            for (var index = 0; index < 2; index++)
            {
                await service.CreateUploadSignatureAsync(request);
            }
            var limited = await service.CreateUploadSignatureAsync(request);
            Assert.False(limited.Success);
            Assert.Equal("TOO_MANY_PENDING_UPLOADS", limited.Code);
        }

        [Fact]
        public async Task CreateUploadSignature_ConcurrentRequests_StillEnforcesPerUserLimit()
        {
            var service = CreateMediaService(
                "user-self",
                "self_user",
                new FakeTencentCloudUploadService()
            );
            var request = new EmployeeImageUploadSignatureRequest
            {
                Kind = "avatar",
                FileName = "avatar.jpg",
                ContentType = "image/jpeg",
                FileSize = 100,
            };

            var results = await Task.WhenAll(
                Enumerable.Range(0, 4).Select(_ => service.CreateUploadSignatureAsync(request))
            );

            Assert.Equal(3, results.Count(result => result.Success));
            Assert.Single(results, result => result.Code == "TOO_MANY_PENDING_UPLOADS");
            Assert.Equal(3, await _db.Queryable<EmployeeImageUploadTicket>().CountAsync());
        }

        [Fact]
        public async Task PendingUploadCleanup_DeletesOnlyExpiredPendingTickets()
        {
            var now = DateTime.UtcNow;
            await _db.Insertable(new[]
            {
                new EmployeeImageUploadTicket
                {
                    PendingObjectKey = "pending/employee-profiles/user-self/avatar/expired.jpg",
                    UserGUID = "user-self", Kind = "avatar", ContentType = "image/jpeg",
                    FileSize = 10, CreatedAt = now.AddHours(-1), ExpiresAt = now.AddMinutes(-1),
                },
                new EmployeeImageUploadTicket
                {
                    PendingObjectKey = "pending/employee-profiles/user-self/avatar/active.jpg",
                    UserGUID = "user-self", Kind = "avatar", ContentType = "image/jpeg",
                    FileSize = 10, CreatedAt = now, ExpiresAt = now.AddMinutes(10),
                },
            }).ExecuteCommandAsync();
            var storage = new FakeTencentCloudUploadService();

            await EmployeeImageUploadCleanup.CleanupExpiredAsync(
                _db,
                storage,
                NullLogger.Instance,
                now,
                CancellationToken.None
            );

            Assert.Contains("pending/employee-profiles/user-self/avatar/expired.jpg", storage.DeletedKeys);
            var cleaned = await _db.Queryable<EmployeeImageUploadTicket>()
                .FirstAsync(item => item.PendingObjectKey.Contains("expired"));
            Assert.Equal(EmployeeImageUploadStatus.Failed, cleaned.Status);
            Assert.Equal(2, await _db.Queryable<EmployeeImageUploadTicket>().CountAsync());
        }

        [Fact]
        public async Task CompleteImage_ConcurrentRetry_PromotesOnceAndReturnsIdempotently()
        {
            await SeedUsersAsync();
            var (key, png, storage) = await SeedValidIdentityTicketAsync();
            storage.PromoteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storage.ReleasePromote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = CreateMediaService("user-self", "self_user", storage);
            var request = new EmployeeImageCompleteRequest { Kind = "identity", ObjectKey = key };

            var first = service.CompleteAsync(request);
            await storage.PromoteEntered.Task;
            var retry = service.CompleteAsync(request);
            storage.ReleasePromote.SetResult();
            var results = await Task.WhenAll(first, retry);

            Assert.All(results, result => Assert.True(result.Success));
            Assert.Equal(1, storage.PromoteCount);
            var ticket = await _db.Queryable<EmployeeImageUploadTicket>().FirstAsync();
            Assert.Equal(EmployeeImageUploadStatus.Completed, ticket.Status);
            Assert.NotNull(ticket.CompletedAt);
            Assert.Equal(png.Length, ticket.FileSize);
        }

        [Fact]
        public async Task CompleteImage_RacingDelete_IsSerializedWithoutFormalOrphan()
        {
            await SeedUsersAsync();
            var (key, _, storage) = await SeedValidIdentityTicketAsync();
            storage.PromoteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storage.ReleasePromote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = CreateMediaService("user-self", "self_user", storage);

            var complete = service.CompleteAsync(
                new EmployeeImageCompleteRequest { Kind = "identity", ObjectKey = key }
            );
            await storage.PromoteEntered.Task;
            var delete = service.DeleteAsync("identity");
            storage.ReleasePromote.SetResult();

            Assert.True((await complete).Success);
            Assert.True((await delete).Success);
            var ticket = await _db.Queryable<EmployeeImageUploadTicket>().FirstAsync();
            var profile = await _db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == "user-self");
            Assert.Null(profile.IdentityPhotoObjectKey);
            Assert.Contains(ticket.FinalObjectKey!, storage.DeletedKeys);
        }

        [Fact]
        public async Task PendingUploadCleanup_RecoversPromotedAndProcessingTickets()
        {
            await SeedUsersAsync();
            var now = DateTime.UtcNow;
            const string linkedFinal = "employee-profiles/user-self/identity/linked.png";
            const string orphanFinal = "employee-profiles/user-self/avatar/orphan.png";
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                IdentityPhotoObjectKey = linkedFinal,
                CreatedAt = now,
                UpdatedAt = now,
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                new EmployeeImageUploadTicket
                {
                    PendingObjectKey = "pending/" + linkedFinal,
                    FinalObjectKey = linkedFinal,
                    UserGUID = "user-self", Kind = "identity", ContentType = "image/png",
                    FileSize = 10, CreatedAt = now.AddHours(-1), ExpiresAt = now.AddMinutes(-1),
                    Status = EmployeeImageUploadStatus.Promoted, PromotedAt = now.AddMinutes(-20),
                    StageChangedAt = now.AddMinutes(-20),
                },
                new EmployeeImageUploadTicket
                {
                    PendingObjectKey = "pending/" + orphanFinal,
                    FinalObjectKey = orphanFinal,
                    UserGUID = "user-self", Kind = "avatar", ContentType = "image/png",
                    FileSize = 10, CreatedAt = now.AddHours(-1), ExpiresAt = now.AddMinutes(-1),
                    Status = EmployeeImageUploadStatus.Processing,
                    ProcessingStartedAt = now.AddMinutes(-20),
                    StageChangedAt = now.AddMinutes(-20),
                },
            }).ExecuteCommandAsync();
            var storage = new FakeTencentCloudUploadService();

            await EmployeeImageUploadCleanup.CleanupExpiredAsync(
                _db, storage, NullLogger.Instance, now, CancellationToken.None
            );

            var linked = await _db.Queryable<EmployeeImageUploadTicket>()
                .FirstAsync(item => item.FinalObjectKey == linkedFinal);
            var orphan = await _db.Queryable<EmployeeImageUploadTicket>()
                .FirstAsync(item => item.FinalObjectKey == orphanFinal);
            Assert.Equal(EmployeeImageUploadStatus.Completed, linked.Status);
            Assert.Equal(EmployeeImageUploadStatus.Failed, orphan.Status);
            Assert.DoesNotContain(linkedFinal, storage.DeletedKeys);
            Assert.Contains(orphanFinal, storage.DeletedKeys);
        }

        [Fact]
        public async Task PendingUploadCleanup_ProcessingJustCrossedSignatureExpiry_DoesNotClean()
        {
            var now = DateTime.UtcNow;
            const string pendingKey = "pending/employee-profiles/user-self/identity/active.png";
            await _db.Insertable(new EmployeeImageUploadTicket
            {
                PendingObjectKey = pendingKey,
                FinalObjectKey = pendingKey["pending/".Length..],
                UserGUID = "user-self",
                Kind = "identity",
                ContentType = "image/png",
                FileSize = 10,
                CreatedAt = now.AddMinutes(-20),
                ExpiresAt = now.AddSeconds(-1),
                Status = EmployeeImageUploadStatus.Processing,
                ProcessingStartedAt = now.AddMinutes(-1),
                StageChangedAt = now.AddMinutes(-1),
            }).ExecuteCommandAsync();
            var storage = new FakeTencentCloudUploadService();

            await EmployeeImageUploadCleanup.CleanupExpiredAsync(
                _db, storage, NullLogger.Instance, now, CancellationToken.None
            );

            var ticket = await _db.Queryable<EmployeeImageUploadTicket>().FirstAsync();
            Assert.Equal(EmployeeImageUploadStatus.Processing, ticket.Status);
            Assert.Empty(storage.DeletedKeys);
        }

        [Fact]
        public async Task CompleteImage_WhenPromotedCasLoses_DoesNotReturnSuccessOrLinkMissingObject()
        {
            await SeedUsersAsync();
            var (key, _, storage) = await SeedValidIdentityTicketAsync();
            storage.OnPromote = async () => await _db.Updateable<EmployeeImageUploadTicket>()
                .SetColumns(item => new EmployeeImageUploadTicket
                {
                    Status = EmployeeImageUploadStatus.Cleaning,
                    StageChangedAt = DateTime.UtcNow,
                })
                .Where(item => item.PendingObjectKey == key)
                .ExecuteCommandAsync();

            var result = await CreateMediaService("user-self", "self_user", storage)
                .CompleteAsync(new EmployeeImageCompleteRequest
                {
                    Kind = "identity",
                    ObjectKey = key,
                });

            Assert.False(result.Success);
            Assert.Equal("IMAGE_UPLOAD_STATE_CONFLICT", result.Code);
            Assert.Null(await _db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == "user-self"));
        }

        [Fact]
        public async Task PendingUploadCleanup_CompletedTicket_RecoversPreviousObjectCleanup()
        {
            await SeedUsersAsync();
            var now = DateTime.UtcNow;
            const string currentKey = "employee-profiles/user-self/identity/current.png";
            const string previousKey = "employee-profiles/user-self/identity/previous.png";
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                IdentityPhotoObjectKey = currentKey,
                CreatedAt = now,
                UpdatedAt = now,
            }).ExecuteCommandAsync();
            await _db.Insertable(new EmployeeImageUploadTicket
            {
                PendingObjectKey = "pending/" + currentKey,
                FinalObjectKey = currentKey,
                PreviousObjectKey = previousKey,
                PreviousObjectCleanupStatus = EmployeeImageObjectCleanupStatus.Pending,
                UserGUID = "user-self",
                Kind = "identity",
                ContentType = "image/png",
                FileSize = 10,
                CreatedAt = now.AddMinutes(-20),
                ExpiresAt = now.AddMinutes(-5),
                Status = EmployeeImageUploadStatus.Completed,
                CompletedAt = now.AddMinutes(-1),
                StageChangedAt = now.AddMinutes(-1),
            }).ExecuteCommandAsync();
            var storage = new FakeTencentCloudUploadService();

            await EmployeeImageUploadCleanup.CleanupExpiredAsync(
                _db, storage, NullLogger.Instance, now, CancellationToken.None
            );

            var ticket = await _db.Queryable<EmployeeImageUploadTicket>().FirstAsync();
            Assert.Contains(previousKey, storage.DeletedKeys);
            Assert.DoesNotContain(currentKey, storage.DeletedKeys);
            Assert.Equal(
                EmployeeImageObjectCleanupStatus.Completed,
                ticket.PreviousObjectCleanupStatus
            );
        }

        [Fact]
        public async Task PendingUploadCleanup_CompletedTicket_ReclaimsExpiredPreviousCleanupLease()
        {
            await SeedUsersAsync();
            var now = DateTime.UtcNow;
            const string currentKey = "employee-profiles/user-self/identity/current-lease.png";
            const string previousKey = "employee-profiles/user-self/identity/previous-lease.png";
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                IdentityPhotoObjectKey = currentKey,
                CreatedAt = now,
                UpdatedAt = now,
            }).ExecuteCommandAsync();
            await _db.Insertable(new EmployeeImageUploadTicket
            {
                PendingObjectKey = "pending/" + currentKey,
                FinalObjectKey = currentKey,
                PreviousObjectKey = previousKey,
                PreviousObjectCleanupStatus = EmployeeImageObjectCleanupStatus.Processing,
                PreviousObjectCleanupStartedAt = now.AddMinutes(-20),
                UserGUID = "user-self",
                Kind = "identity",
                ContentType = "image/png",
                FileSize = 10,
                CreatedAt = now.AddMinutes(-30),
                ExpiresAt = now.AddMinutes(-15),
                Status = EmployeeImageUploadStatus.Completed,
                CompletedAt = now.AddMinutes(-10),
                StageChangedAt = now.AddMinutes(-10),
            }).ExecuteCommandAsync();
            var storage = new FakeTencentCloudUploadService();

            await EmployeeImageUploadCleanup.CleanupExpiredAsync(
                _db, storage, NullLogger.Instance, now, CancellationToken.None
            );

            var ticket = await _db.Queryable<EmployeeImageUploadTicket>().FirstAsync();
            Assert.Contains(previousKey, storage.DeletedKeys);
            Assert.Equal(
                EmployeeImageObjectCleanupStatus.Completed,
                ticket.PreviousObjectCleanupStatus
            );
            Assert.Null(ticket.PreviousObjectCleanupStartedAt);
        }

        [Fact]
        public async Task PendingUploadCleanup_RacingComplete_WaitsAndKeepsCompletedFinalObject()
        {
            await SeedUsersAsync();
            var (key, _, storage) = await SeedValidIdentityTicketAsync();
            storage.PromoteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            storage.ReleasePromote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = CreateMediaService("user-self", "self_user", storage);
            var complete = service.CompleteAsync(
                new EmployeeImageCompleteRequest { Kind = "identity", ObjectKey = key }
            );
            await storage.PromoteEntered.Task;

            var cleanup = EmployeeImageUploadCleanup.CleanupExpiredAsync(
                _db,
                storage,
                NullLogger.Instance,
                DateTime.UtcNow.AddHours(1),
                CancellationToken.None
            );
            storage.ReleasePromote.SetResult();
            await Task.WhenAll(complete, cleanup);
            var completeResult = await complete;

            var ticket = await _db.Queryable<EmployeeImageUploadTicket>().FirstAsync();
            Assert.True(completeResult.Success);
            Assert.Equal(EmployeeImageUploadStatus.Completed, ticket.Status);
            Assert.DoesNotContain(ticket.FinalObjectKey!, storage.DeletedKeys);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CompleteIdentity_ReplacesAssociationAndObservesOldObjectCleanup(
            bool deleteSucceeds
        )
        {
            await SeedUsersAsync();
            const string oldKey = "employee-profiles/user-self/identity/old.png";
            const string newKey = "pending/employee-profiles/user-self/identity/new.png";
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                IdentityPhotoObjectKey = oldKey,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
            );
            var storage = new FakeTencentCloudUploadService
            {
                DeleteSucceeds = deleteSucceeds,
                Bytes = png,
                Metadata = CreateMetadata("user-self", "identity", "image/png", png.Length),
            };
            await _db.Insertable(new EmployeeImageUploadTicket
            {
                PendingObjectKey = newKey,
                UserGUID = "user-self",
                Kind = "identity",
                ContentType = "image/png",
                FileSize = png.Length,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            }).ExecuteCommandAsync();
            var logger = new CaptureLogger<EmployeeProfileMediaService>();
            var service = CreateMediaService("user-self", "self_user", storage, logger);

            var result = await service.CompleteAsync(
                new EmployeeImageCompleteRequest { Kind = "identity", ObjectKey = newKey }
            );

            Assert.True(result.Success);
            Assert.Contains(oldKey, storage.DeletedKeys);
            var profile = await _db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == "user-self");
            Assert.StartsWith("employee-profiles/user-self/identity/", profile.IdentityPhotoObjectKey);
            if (!deleteSucceeds)
            {
                Assert.Contains(
                    logger.Messages,
                    message => message.Contains(oldKey) && message.Contains("user-self")
                );
            }
        }

        [Fact]
        public async Task CompleteIdentity_DoesNotDeleteCrossUserOldObject()
        {
            await SeedUsersAsync();
            const string crossUserKey = "employee-profiles/user-other/identity/old.png";
            const string newKey = "pending/employee-profiles/user-self/identity/new.png";
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = "user-self",
                IdentityPhotoObjectKey = crossUserKey,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
            );
            var storage = new FakeTencentCloudUploadService
            {
                Bytes = png,
                Metadata = CreateMetadata("user-self", "identity", "image/png", png.Length),
            };
            await _db.Insertable(new EmployeeImageUploadTicket
            {
                PendingObjectKey = newKey,
                UserGUID = "user-self",
                Kind = "identity",
                ContentType = "image/png",
                FileSize = png.Length,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            }).ExecuteCommandAsync();

            var result = await CreateMediaService("user-self", "self_user", storage)
                .CompleteAsync(
                    new EmployeeImageCompleteRequest { Kind = "identity", ObjectKey = newKey }
                );

            Assert.True(result.Success);
            Assert.DoesNotContain(crossUserKey, storage.DeletedKeys);
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

        private EmployeeProfileService CreateService(string userGuid, string username)
            => CreateService(userGuid, username, _db);

        private EmployeeProfileService CreateService(
            string userGuid,
            string username,
            ISqlSugarClient db,
            bool includeSensitiveChangeService = false,
            IRoleService? roleService = null,
            bool omitRoleService = false
        )
        {
            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(CreateClaims(userGuid, username), "TestAuth")
                    ),
                },
            };

            var currentUserService = new CurrentUserService(httpContextAccessor);
            var context = CreateSqlSugarContext(db);

            var sensitiveChangeService = includeSensitiveChangeService
                ? new EmployeeProfileSensitiveChangeService(
                    context,
                    currentUserService,
                    NullLogger<EmployeeProfileSensitiveChangeService>.Instance,
                    Mock.Of<ICurrentUserManageableStoreScopeService>(),
                    httpContextAccessor
                )
                : null;

            if (roleService is null)
            {
                var roleMock = new Mock<IRoleService>();
                roleMock
                    .Setup(mock => mock.UserHasPermissionAsync(
                        It.IsAny<string>(),
                        Permissions.EmployeeProfiles.EditPositionType))
                    .ReturnsAsync(ApiResponse<bool>.OK(true));
                roleService = roleMock.Object;
            }

            return new EmployeeProfileService(
                context,
                currentUserService,
                NullLogger<EmployeeProfileService>.Instance,
                sensitiveChangeService: sensitiveChangeService,
                roleService: omitRoleService ? null : roleService
            );
        }

        private SqlSugarClient CreateAdditionalDb() => new(new ConnectionConfig
        {
            ConnectionString = _sqliteConnection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });

        [Fact]
        public async Task AdminRefreshForUser_RequiresMatchingExpectedAndDisablesLegacy()
        {
            await SeedUsersAsync();
            await _db.Insertable(new CashRegisterUser { UserGUID = "user-other", UserBarcode = "9988776655443", Status = true, OperatorUser = "seed", LoginRole = "cashier", Remark = "", Creator = "seed", CreateDate = DateTime.UtcNow, LastModifier = "seed", LastModifyDate = DateTime.UtcNow }).ExecuteCommandAsync();
            var generated = new Queue<string>(new[] { "2912345678901", "2912345678902" });
            var service = CreateBarcodeService("admin", "admin", () => generated.Dequeue());

            var first = await service.RefreshForUserAsync("user-other", null);
            Assert.True(first.Success);
            Assert.Equal("2912345678901", first.Data!.Barcode);
            Assert.False((await _db.Queryable<CashRegisterUser>().FirstAsync(x => x.UserGUID == "user-other")).Status);

            var reset = await service.RefreshForUserAsync("user-other", first.Data.Barcode);
            Assert.True(reset.Success);
            Assert.Equal("2912345678902", reset.Data!.Barcode);
            var activeCodes = await _db.Queryable<EmployeeCashierBarcode>()
                .Where(x => x.UserGUID == "user-other" && x.Status)
                .ToListAsync();
            Assert.Single(activeCodes);
            Assert.Equal("2912345678902", activeCodes[0].Barcode);

            var stale = await service.RefreshForUserAsync("user-other", "2912345678901");
            Assert.False(stale.Success);
            Assert.Equal("CASHIER_BARCODE_CHANGED", stale.ErrorCode);
            Assert.Null(stale.Data);
        }

        [Fact]
        public async Task AdminRefreshForUser_IsolatesTargetAndRejectsInactiveUserWithoutMutation()
        {
            await SeedUsersAsync();
            var existing = new EmployeeCashierBarcode
            {
                UserGUID = "user-other",
                Barcode = "9988776655443",
                Status = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
                UpdatedBy = "seed",
            };
            await _db.Insertable(existing).ExecuteCommandAsync();
            var service = CreateBarcodeService("admin", "admin", () => "2912345678901");

            var targetResult = await service.RefreshForUserAsync("user-self", null);
            Assert.True(targetResult.Success);
            Assert.Equal("2912345678901", targetResult.Data!.Barcode);
            Assert.Equal("9988776655443", (await _db.Queryable<EmployeeCashierBarcode>()
                .Where(x => x.UserGUID == "user-other" && x.Status).FirstAsync()).Barcode);

            await _db.Updateable<User>().SetColumns(x => x.IsActive == false)
                .Where(x => x.UserGUID == "user-other").ExecuteCommandAsync();
            var inactive = await service.RefreshForUserAsync("user-other", "9988776655443");
            Assert.False(inactive.Success);
            Assert.Equal("USER_INACTIVE", inactive.ErrorCode);
            Assert.Equal("9988776655443", (await _db.Queryable<EmployeeCashierBarcode>()
                .Where(x => x.UserGUID == "user-other" && x.Status).FirstAsync()).Barcode);
        }

        [Fact]
        public void AdminRefreshForUser_DistinguishesMissingExpectedFromExplicitNull()
        {
            var request = new AdminCashierBarcodeRefreshRequest();
            Assert.False(request.HasExpectedBarcode);
            request.ExpectedBarcode = null;
            Assert.True(request.HasExpectedBarcode);
            Assert.Null(request.ExpectedBarcode);
        }

        [Fact]
        public void UsersController_ExposesAdminCashierBarcodeRoutes()
        {
            var get = typeof(UsersController).GetMethod(nameof(UsersController.GetUserCashierBarcode));
            var post = typeof(UsersController).GetMethod(nameof(UsersController.RefreshUserCashierBarcode));
            Assert.Equal("guid/{guid}/cashier-barcode", get?.GetCustomAttribute<HttpGetAttribute>()?.Template);
            Assert.Equal("guid/{guid}/cashier-barcode/refresh", post?.GetCustomAttribute<HttpPostAttribute>()?.Template);
        }

        private EmployeeCashierBarcodeService CreateBarcodeService(
            string userGuid,
            string username,
            Func<string>? barcodeFactory = null
        )
        {
            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(CreateClaims(userGuid, username), "TestAuth")
                    ),
                },
            };
            return new EmployeeCashierBarcodeService(
                CreateSqlSugarContext(_db),
                new CurrentUserService(httpContextAccessor),
                barcodeFactory
            );
        }

        private EmployeeProfileMediaService CreateMediaService(
            string userGuid,
            string username,
            TencentCloudUploadService storage,
            ILogger<EmployeeProfileMediaService>? logger = null
        )
        {
            var accessor = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(CreateClaims(userGuid, username), "TestAuth")
                    ),
                },
            };
            return new EmployeeProfileMediaService(
                CreateSqlSugarContext(_db),
                new CurrentUserService(accessor),
                storage,
                logger ?? NullLogger<EmployeeProfileMediaService>.Instance
            );
        }

        private static TencentCloudUploadService.CosObjectMetadata CreateMetadata(
            string owner,
            string kind,
            string contentType,
            long size
        ) => new()
        {
            ContentLength = size,
            ContentType = contentType,
            Owner = owner,
            Kind = kind,
            DeclaredContentType = contentType,
            DeclaredFileSize = size,
        };

        private async Task<(string Key, byte[] Bytes, FakeTencentCloudUploadService Storage)>
            SeedValidIdentityTicketAsync()
        {
            const string key = "pending/employee-profiles/user-self/identity/concurrent.png";
            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
            );
            await _db.Insertable(new EmployeeImageUploadTicket
            {
                PendingObjectKey = key,
                FinalObjectKey = key["pending/".Length..],
                UserGUID = "user-self",
                Kind = "identity",
                ContentType = "image/png",
                FileSize = png.Length,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
                Status = EmployeeImageUploadStatus.Pending,
            }).ExecuteCommandAsync();
            return (
                key,
                png,
                new FakeTencentCloudUploadService
                {
                    Bytes = png,
                    Metadata = CreateMetadata("user-self", "identity", "image/png", png.Length),
                }
            );
        }

        private async Task SeedUsersAsync()
        {
            await _db.Insertable(
                new[]
                {
                    new User
                    {
                        UserGUID = "user-self",
                        Username = "self_user",
                        Email = "self@example.com",
                        PasswordHash = "hashed",
                        FullName = "Self User",
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    },
                    new User
                    {
                        UserGUID = "user-other",
                        Username = "other_user",
                        Email = "other@example.com",
                        PasswordHash = "hashed",
                        FullName = "Other User",
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    },
                }
            ).ExecuteCommandAsync();
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

        private static IEnumerable<Claim> CreateClaims(string userGuid, string username)
        {
            yield return new Claim("userGuid", userGuid);
            yield return new Claim("userId", userGuid);
            yield return new Claim(ClaimTypes.NameIdentifier, userGuid);
            yield return new Claim(ClaimTypes.Name, username);
        }

        private sealed class FakeTencentCloudUploadService : TencentCloudUploadService
        {
            public bool DeleteSucceeds { get; set; } = true;
            public byte[] Bytes { get; set; } = Array.Empty<byte>();
            public CosObjectMetadata Metadata { get; set; } = new();
            public List<string> DeletedKeys { get; } = new();
            public int PromoteCount { get; private set; }
            public TaskCompletionSource? PromoteEntered { get; set; }
            public TaskCompletionSource? ReleasePromote { get; set; }
            public Func<Task>? OnPromote { get; set; }

            public FakeTencentCloudUploadService()
                : base(
                    Options.Create(new TencentCloudSettings
                    {
                        SecretId = "id",
                        SecretKey = "key",
                        BucketName = "bucket",
                        Region = "region",
                    }),
                    NullLogger<TencentCloudUploadService>.Instance,
                    new HttpClient()
                )
            { }

            public override Task<ApiResponse<CosObjectMetadata>> GetObjectMetadataAsync(
                string objectKey,
                CancellationToken cancellationToken = default
            ) => Task.FromResult(ApiResponse<CosObjectMetadata>.OK(Metadata));

            public override Task<ApiResponse<byte[]>> DownloadObjectBytesAsync(
                string objectKey,
                int maximumBytes,
                CancellationToken cancellationToken = default
            ) => Task.FromResult(ApiResponse<byte[]>.OK(Bytes));

            public override Task<ApiResponse<bool>> DeleteObjectAsync(
                string objectKey,
                CancellationToken cancellationToken = default
            )
            {
                DeletedKeys.Add(objectKey);
                return Task.FromResult(
                    DeleteSucceeds
                        ? ApiResponse<bool>.OK(true)
                        : ApiResponse<bool>.Error("delete failed", "COS_OBJECT_DELETE_FAILED")
                );
            }

            public override async Task<ApiResponse<bool>> PromoteObjectAsync(
                string sourceObjectKey,
                string targetObjectKey,
                string contentType,
                bool isPublic,
                CancellationToken cancellationToken = default
            )
            {
                PromoteCount++;
                if (OnPromote is not null)
                {
                    await OnPromote();
                }
                PromoteEntered?.TrySetResult();
                if (ReleasePromote is not null)
                {
                    await ReleasePromote.Task.WaitAsync(cancellationToken);
                }
                return ApiResponse<bool>.OK(true);
            }
        }

        private sealed class CaptureLogger<T> : ILogger<T>
        {
            public List<string> Messages { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) => Messages.Add(formatter(state, exception));
        }
    }
}
