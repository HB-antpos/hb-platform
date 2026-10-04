using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests
{
    public sealed class StoreUsersReactServiceTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteConnection _sqliteConnection;
        private readonly SqlSugarClient _db;

        public StoreUsersReactServiceTests()
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
                typeof(User),
                typeof(Role),
                typeof(UserRole),
                typeof(Store),
                typeof(UserStore),
                typeof(EmployeeProfile),
                typeof(UserPasswordChangeRequirement)
            );
        }

        [Fact]
        public async Task CreateAsync_WhenStoreManagerTargetsNonPrimaryStore_ReturnsScopeError()
        {
            await SeedStoreUserDataAsync();
            var service = CreateService("manager-1", "StoreManager");

            var result = await service.CreateAsync(
                new CreateStoreUserDto
                {
                    Username = "new_staff",
                    Email = "new_staff@example.com",
                    Password = "Secret123",
                    FullName = "New Staff",
                    StoreCode = "S002",
                    Status = 1,
                },
                "manager-1"
            );

            Assert.False(result.Success);
            Assert.Contains("分店", result.Message ?? string.Empty);
        }

        [Fact]
        public async Task CreateAsync_WhenStoreManagerUsesManagedStore_AssignsStoreStaffRole()
        {
            await SeedStoreUserDataAsync();
            var service = CreateService("manager-1", "StoreManager");

            var result = await service.CreateAsync(
                new CreateStoreUserDto
                {
                    Username = "staff_created",
                    Email = "staff_created@example.com",
                    Password = "Secret123",
                    FullName = "Created Staff",
                    Phone = "0412000111",
                    StoreCode = "S001",
                    Status = 1,
                },
                "manager-1"
            );

            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            Assert.Equal("store-1", result.Data!.StoreGuid);
            Assert.Equal("0412000111", result.Data.Phone);

            var gridResult = await service.GetGridDataAsync(new StoreUserGridRequestDto
            {
                StoreCode = "S001",
            });
            Assert.Contains(gridResult.Items!, item =>
                item.UserGuid == result.Data.UserGuid && item.Phone == "0412000111"
            );

            var createdUserStore = await _db.Queryable<UserStore>()
                .FirstAsync(item => item.UserGUID == result.Data.UserGuid);
            Assert.NotNull(createdUserStore);
            Assert.False(createdUserStore!.IsPrimary);

            var createdRoleNames = await _db.Queryable<UserRole>()
                .InnerJoin<Role>((ur, r) => ur.RoleGUID == r.RoleGUID)
                .Where((ur, r) => ur.UserGUID == result.Data.UserGuid)
                .Select((ur, r) => r.RoleName)
                .ToListAsync();
            Assert.Equal(new[] { "StoreStaff" }, createdRoleNames);

            var createdProfile = await _db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == result.Data.UserGuid);
            Assert.NotNull(createdProfile);
            Assert.Equal(EmployeeType.Temporary, createdProfile!.EmployeeType);

            // 未传 RequirePasswordChange 时默认要求首次登录改密，列表与详情都能看到。
            var requirement = await _db.Queryable<UserPasswordChangeRequirement>()
                .FirstAsync(item => item.UserGUID == result.Data.UserGuid);
            Assert.NotNull(requirement);
            Assert.Equal(UserPasswordChangeRequirement.ReasonCreated, requirement!.Reason);
            Assert.Equal("manager-1", requirement.RequiredBy);
            Assert.True(result.Data.MustChangePassword);
            Assert.True(gridResult.Items!.Single(item => item.UserGuid == result.Data.UserGuid).MustChangePassword);
            Assert.False(gridResult.Items!.Single(item => item.UserGuid == "staff-1").MustChangePassword);
        }

        [Fact]
        public async Task CreateAsync_WhenRequirePasswordChangeFalse_DoesNotMarkAccount()
        {
            await SeedStoreUserDataAsync();
            var service = CreateService("manager-1", "StoreManager");

            var result = await service.CreateAsync(
                new CreateStoreUserDto
                {
                    Username = "staff_no_force",
                    Password = "Secret123",
                    StoreCode = "S001",
                    Status = 1,
                    RequirePasswordChange = false,
                },
                "manager-1"
            );

            Assert.True(result.Success);
            Assert.False(result.Data!.MustChangePassword);
            Assert.False(await _db.Queryable<UserPasswordChangeRequirement>().AnyAsync());
        }

        [Fact]
        public async Task UpdatePasswordAsync_WhenStoreManagerResetsManagedStaff_RequiresPasswordChange()
        {
            await SeedStoreUserDataAsync();
            var service = CreateService("manager-1", "StoreManager");

            var result = await service.UpdatePasswordAsync(
                "staff-1",
                new UpdateStoreUserPasswordDto
                {
                    StoreCode = "S001",
                    NewPassword = "Changed123",
                    PasswordFormat = "raw",
                },
                "manager-1"
            );

            Assert.True(result.Success);
            var requirement = await _db.Queryable<UserPasswordChangeRequirement>()
                .FirstAsync(item => item.UserGUID == "staff-1");
            Assert.NotNull(requirement);
            Assert.Equal(UserPasswordChangeRequirement.ReasonReset, requirement!.Reason);
        }

        [Fact]
        public async Task GetGridDataAsync_WhenStoreManager_OnlyReturnsManagedStoreStaff()
        {
            await SeedStoreUserDataAsync();
            var service = CreateService("manager-1", "StoreManager");

            var result = await service.GetGridDataAsync(new StoreUserGridRequestDto
            {
                StoreCode = "S001",
            });

            Assert.True(result.Success);
            Assert.Single(result.Items!);
            Assert.All(result.Items!, item => Assert.Equal("store-1", item.StoreGuid));
            Assert.All(result.Items!, item => Assert.Contains("StoreStaff", item.RoleNames));
        }

        [Fact]
        public async Task GetGridDataAsync_WhenStoreManagerOmitsStore_ReturnsManagedStoreStaff()
        {
            await SeedStoreUserDataAsync();
            var service = CreateService("manager-1", "StoreManager");

            var result = await service.GetGridDataAsync(new StoreUserGridRequestDto());

            Assert.True(result.Success);
            var staff = Assert.Single(result.Items!);
            Assert.Equal("staff-1", staff.UserGuid);
            Assert.Equal("S001", staff.StoreCode);
            Assert.Equal("store-1", staff.StoreGuid);
            Assert.Equal(new DateTime(1992, 7, 15), staff.Birthday);
            Assert.Equal("female", staff.Gender);
            Assert.Equal("partTime", staff.EmploymentType);
        }

        [Fact]
        public async Task GetGridDataAsync_WhenAdminOmitsStore_ReturnsAllStoreStaff()
        {
            await SeedStoreUserDataAsync();
            var service = CreateService("admin-1", "Admin");

            var result = await service.GetGridDataAsync(new StoreUserGridRequestDto());

            Assert.True(result.Success);
            Assert.Equal(2, result.Items!.Count);
            Assert.Contains(result.Items!, item => item.UserGuid == "staff-1" && item.StoreCode == "S001");
            Assert.Contains(result.Items!, item => item.UserGuid == "staff-2" && item.StoreCode == "S002");
        }

        [Fact]
        public async Task GetGridDataAsync_WhenStoreManagerTargetsNonPrimaryStore_ReturnsScopeError()
        {
            await SeedStoreUserDataAsync();
            var service = CreateService("manager-1", "StoreManager");

            var result = await service.GetGridDataAsync(new StoreUserGridRequestDto
            {
                StoreCode = "S002",
            });

            Assert.False(result.Success);
            Assert.Contains("权限", result.Message ?? string.Empty);
        }

        [Fact]
        public async Task UpdateStatusAsync_WhenStoreManagerDisablesManagedStoreStaff_GridReturnsDisabled()
        {
            await SeedStoreUserDataAsync();
            var service = CreateService("manager-1", "StoreManager");

            var updateResult = await service.UpdateStatusAsync(
                "staff-1",
                new UpdateStoreUserStatusDto
                {
                    StoreCode = "S001",
                    Status = 0,
                },
                "manager-1"
            );

            Assert.True(updateResult.Success);

            var gridResult = await service.GetGridDataAsync(new StoreUserGridRequestDto
            {
                StoreCode = "S001",
            });

            Assert.True(gridResult.Success);
            var staff = Assert.Single(gridResult.Items!);
            Assert.Equal("staff-1", staff.UserGuid);
            Assert.Equal(0, staff.Status);
        }

        [Fact]
        public async Task GetByUserGuidAsync_WhenStoreManagerTargetsManagedStore_ReturnsProfileDetails()
        {
            await SeedStoreUserDataAsync();
            var service = CreateService("manager-1", "StoreManager");

            var result = await service.GetByUserGuidAsync("staff-1", "S001");

            Assert.True(result.Success);
            Assert.NotNull(result.Data);

            var detail = result.Data!;
            Assert.Equal("staff-1", detail.UserGuid);
            Assert.Equal("staff_1", detail.Username);
            Assert.Equal("Store 1", detail.StoreName);
            Assert.Contains("StoreStaff", detail.RoleNames);
            Assert.Equal(new DateTime(1992, 7, 15), detail.Birthday);
            Assert.Equal("female", detail.Gender);
            Assert.Equal("partTime", detail.EmploymentType);
            Assert.Equal("https://example.com/avatar/staff-1.png", detail.AvatarUrl);
            Assert.Equal("ID-STAFF-1", detail.IdentityId);
            Assert.Equal("123 Queen St", detail.Address);
            Assert.Equal("062000", detail.BankBsb);
            Assert.Equal("12345678", detail.BankAccountNumber);
            Assert.Equal("Aware Super", detail.SuperannuationCompanyName);
            Assert.Equal("AWARE", detail.SuperannuationCompanyCode);
            Assert.Equal("SUPER-001", detail.SuperannuationAccountNumber);
        }

        [Fact]
        public async Task GetByUserGuidAsync_WhenStoreManagerTargetsUnmanagedStore_ReturnsForbidden()
        {
            await SeedStoreUserDataAsync();
            var service = CreateService("manager-1", "StoreManager");

            var result = await service.GetByUserGuidAsync("staff-2", "S002");

            Assert.False(result.Success);
            Assert.Equal("FORBIDDEN", result.Code);
        }

        [Fact]
        public async Task UpdatePasswordAsync_WhenStoreManagerTargetsForeignStore_ReturnsScopeError()
        {
            await SeedStoreUserDataAsync();
            var service = CreateService("manager-1", "StoreManager");

            var result = await service.UpdatePasswordAsync(
                "staff-2",
                new UpdateStoreUserPasswordDto
                {
                    StoreCode = "S001",
                    NewPassword = "Changed123",
                },
                "manager-1"
            );

            Assert.False(result.Success);
            Assert.Contains("权限", result.Message ?? string.Empty);
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

        private StoreUserReactService CreateService(string userGuid, params string[] roles)
        {
            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(CreateClaims(userGuid, roles), "TestAuth")
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

            return new StoreUserReactService(
                context,
                NullLogger<StoreUserReactService>.Instance,
                scopeService
            );
        }

        private async Task SeedStoreUserDataAsync()
        {
            await _db.Insertable(
                new[]
                {
                    new Store
                    {
                        StoreGUID = "store-1",
                        StoreCode = "S001",
                        StoreName = "Store 1",
                        IsActive = true,
                    },
                    new Store
                    {
                        StoreGUID = "store-2",
                        StoreCode = "S002",
                        StoreName = "Store 2",
                        IsActive = true,
                    },
                }
            ).ExecuteCommandAsync();

            await _db.Insertable(
                new[]
                {
                    new Role { RoleGUID = "role-admin", RoleName = "Admin", IsActive = true },
                    new Role
                    {
                        RoleGUID = "role-manager",
                        RoleName = "StoreManager",
                        IsActive = true,
                    },
                    new Role
                    {
                        RoleGUID = "role-staff",
                        RoleName = "StoreStaff",
                        IsActive = true,
                    },
                }
            ).ExecuteCommandAsync();

            await _db.Insertable(
                new[]
                {
                    CreateUser("manager-1", "manager_1", "manager1@example.com"),
                    CreateUser("staff-1", "staff_1", "staff1@example.com"),
                    CreateUser("staff-2", "staff_2", "staff2@example.com"),
                    CreateUser("admin-1", "admin_1", "admin1@example.com"),
                }
            ).ExecuteCommandAsync();

            await _db.Insertable(
                new[]
                {
                    CreateUserRole("manager-1", "role-manager"),
                    CreateUserRole("staff-1", "role-staff"),
                    CreateUserRole("staff-2", "role-staff"),
                    CreateUserRole("admin-1", "role-admin"),
                }
            ).ExecuteCommandAsync();

            await _db.Insertable(
                new[]
                {
                    CreateUserStore("manager-1", "store-1", true),
                    CreateUserStore("manager-1", "store-2", false),
                    CreateUserStore("staff-1", "store-1", false),
                    CreateUserStore("staff-2", "store-2", false),
                    CreateUserStore("admin-1", "store-2", true),
                }
            ).ExecuteCommandAsync();

            await _db.Insertable(
                new[]
                {
                    new EmployeeProfile
                    {
                        UserGUID = "staff-1",
                        Birthday = new DateTime(1992, 7, 15),
                        Gender = EmployeeGender.Female,
                        EmployeeType = EmployeeType.PartTime,
                        AvatarUrl = "https://example.com/avatar/staff-1.png",
                        IdentityId = "ID-STAFF-1",
                        Address = "123 Queen St",
                        BankBSB = "062000",
                        BankACC = "12345678",
                        SuperannuationCompanyName = "Aware Super",
                        SuperannuationCompanyCode = "AWARE",
                        SuperannuationAccount = "SUPER-001",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    },
                    new EmployeeProfile
                    {
                        UserGUID = "staff-2",
                        Birthday = new DateTime(1990, 1, 1),
                        Gender = EmployeeGender.Male,
                        EmployeeType = EmployeeType.FullTime,
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

        private static IEnumerable<Claim> CreateClaims(string userGuid, IEnumerable<string> roles)
        {
            yield return new Claim("userGuid", userGuid);
            yield return new Claim("userId", userGuid);
            yield return new Claim(ClaimTypes.NameIdentifier, userGuid);
            yield return new Claim(ClaimTypes.Name, userGuid);

            foreach (var role in roles)
            {
                yield return new Claim(ClaimTypes.Role, role);
            }
        }

        private static User CreateUser(string userGuid, string username, string email)
        {
            return new User
            {
                UserGUID = userGuid,
                Username = username,
                Email = email,
                PasswordHash = "hashed",
                FullName = username,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
        }

        private static UserRole CreateUserRole(string userGuid, string roleGuid)
        {
            return new UserRole
            {
                UserRoleGUID = Guid.NewGuid().ToString(),
                UserGUID = userGuid,
                RoleGUID = roleGuid,
                AssignedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
        }

        private static UserStore CreateUserStore(string userGuid, string storeGuid, bool isPrimary)
        {
            return new UserStore
            {
                UserStoreGUID = Guid.NewGuid().ToString(),
                UserGUID = userGuid,
                StoreGUID = storeGuid,
                IsPrimary = isPrimary,
                AssignedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
        }
    }
}
