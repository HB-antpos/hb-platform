using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    public class StoreUserReactService : IStoreUserReactService
    {
        private const string StoreStaffRoleName = "StoreStaff";

        private readonly ISqlSugarClient _db;
        private readonly ILogger<StoreUserReactService> _logger;
        private readonly ICurrentUserManageableStoreScopeService _scopeService;

        public StoreUserReactService(
            SqlSugarContext context,
            ILogger<StoreUserReactService> logger,
            ICurrentUserManageableStoreScopeService scopeService
        )
        {
            _db = context.Db;
            _logger = logger;
            _scopeService = scopeService;
        }

        public async Task<GridResponseDto<StoreUserListDto>> GetGridDataAsync(
            StoreUserGridRequestDto request
        )
        {
            try
            {
                var scope = await _scopeService.GetScopeAsync();
                if (!scope.IsAllowed)
                {
                    return GridResponseDto<StoreUserListDto>.Error(scope.Message);
                }

                var normalizedStoreCode = request.StoreCode?.Trim();

                if (
                    !string.IsNullOrWhiteSpace(normalizedStoreCode)
                    && !scope.CanAccessStoreCode(normalizedStoreCode)
                )
                {
                    return GridResponseDto<StoreUserListDto>.Error("没有权限查看该分店店员");
                }

                var keyword = request.Keyword?.Trim();
                var statusFilter = request.Status;

                var rows = await _db.Queryable<User>()
                    .InnerJoin<UserRole>((u, ur) => u.UserGUID == ur.UserGUID)
                    .InnerJoin<Role>((u, ur, r) => ur.RoleGUID == r.RoleGUID)
                    .InnerJoin<UserStore>((u, ur, r, us) => u.UserGUID == us.UserGUID)
                    .InnerJoin<Store>((u, ur, r, us, s) => us.StoreGUID == s.StoreGUID)
                    .LeftJoin<EmployeeProfile>((u, ur, r, us, s, profile) => u.UserGUID == profile.UserGUID)
                    .Where((u, ur, r, us, s) =>
                        !u.IsDeleted
                        && !ur.IsDeleted
                        && !r.IsDeleted
                        && !us.IsDeleted
                        && !s.IsDeleted
                        && r.RoleName == StoreStaffRoleName
                    )
                    .WhereIF(
                        !scope.IsAdmin && string.IsNullOrWhiteSpace(normalizedStoreCode),
                        (u, ur, r, us, s, profile) => scope.StoreGuids.Contains(s.StoreGUID)
                    )
                    .WhereIF(
                        !string.IsNullOrWhiteSpace(normalizedStoreCode),
                        (u, ur, r, us, s, profile) => s.StoreCode == normalizedStoreCode
                    )
                    .WhereIF(
                        !string.IsNullOrWhiteSpace(keyword),
                        (u, ur, r, us, s, profile) =>
                            u.Username.Contains(keyword!)
                            || (u.FullName != null && u.FullName.Contains(keyword!))
                            || (u.Email != null && u.Email.Contains(keyword!))
                    )
                    .WhereIF(statusFilter == 0, (u, ur, r, us, s, profile) => !u.IsActive)
                    .WhereIF(statusFilter == 1, (u, ur, r, us, s, profile) => u.IsActive)
                    .OrderBy((u, ur, r, us, s, profile) => u.FullName)
                    .OrderBy((u, ur, r, us, s, profile) => u.Username)
                    .Select((u, ur, r, us, s, profile) => new
                    {
                        u.UserGUID,
                        u.Username,
                        u.FullName,
                        u.Email,
                        profile.Phone,
                        Status = u.IsActive ? 1 : 0,
                        StoreGuid = s.StoreGUID,
                        StoreCode = s.StoreCode,
                        StoreName = s.StoreName,
                        RoleName = r.RoleName,
                        LastLoginTime = u.LastLoginAt,
                        LastLoginIp = u.LastLoginIp,
                        u.CreatedAt,
                        u.UpdatedAt,
                        Birthday = profile.Birthday,
                        profile.Gender,
                        EmployeeType = profile.EmployeeType,
                    })
                    .ToListAsync();

                var grouped = rows
                    .GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                    {
                        var first = group.First();
                        return new StoreUserListDto
                        {
                            UserGuid = first.UserGUID,
                            Username = first.Username,
                            FullName = first.FullName,
                            Email = first.Email,
                            Phone = first.Phone,
                            Status = first.Status,
                            StoreGuid = first.StoreGuid,
                            StoreCode = first.StoreCode,
                            StoreName = first.StoreName,
                            RoleNames = group
                            .Select(item => item.RoleName)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                            Birthday = first.Birthday,
                            Gender = FormatGender(first.Gender),
                            EmploymentType = FormatEmploymentType(first.EmployeeType),
                            LastLoginTime = first.LastLoginTime,
                            LastLoginIp = first.LastLoginIp,
                            CreatedAt = first.CreatedAt,
                            UpdatedAt = first.UpdatedAt,
                        };
                    })
                    .OrderBy(item => item.FullName ?? item.Username, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                // 一次批量查询标注「待首次改密」，供列表显示状态与筛选。
                var mustChange = await UserPasswordChangeRequirements.GetRequiredUserGuidsAsync(
                    _db,
                    grouped.Select(item => item.UserGuid).ToList()
                );
                foreach (var item in grouped)
                {
                    item.MustChangePassword = mustChange.Contains(item.UserGuid);
                }

                return GridResponseDto<StoreUserListDto>.OK(grouped, grouped.Count, "获取店员列表成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取店员列表失败");
                return GridResponseDto<StoreUserListDto>.Error("获取店员列表失败");
            }
        }

        public async Task<ApiResponse<StoreUserDetailDto>> GetByUserGuidAsync(
            string userGuid,
            string? storeCode
        )
        {
            try
            {
                var scope = await _scopeService.GetScopeAsync();
                if (!scope.IsAllowed)
                {
                    return ApiResponse<StoreUserDetailDto>.Error(scope.Message, "FORBIDDEN");
                }

                var userRecord = await LoadManagedUserAsync(userGuid, scope, storeCode);
                if (userRecord == null)
                {
                    return await BuildMissingUserResponseAsync<StoreUserDetailDto>(
                        userGuid,
                        "未找到可管理的店员账号"
                    );
                }

                return ApiResponse<StoreUserDetailDto>.OK(userRecord, "获取店员详情成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取店员详情失败，UserGuid: {UserGuid}", userGuid);
                return ApiResponse<StoreUserDetailDto>.Error("获取店员详情失败", "GET_STORE_USER_FAILED");
            }
        }

        public async Task<ApiResponse<StoreUserDetailDto>> CreateAsync(
            CreateStoreUserDto dto,
            string createdBy
        )
        {
            try
            {
                var scope = await _scopeService.GetScopeAsync();
                if (!scope.IsAllowed)
                {
                    return ApiResponse<StoreUserDetailDto>.Error(scope.Message, "FORBIDDEN");
                }

                var targetStore = await ResolveTargetStoreAsync(dto.StoreCode, scope);
                if (targetStore == null)
                {
                    return ApiResponse<StoreUserDetailDto>.Error("没有权限为该分店创建店员", "FORBIDDEN");
                }

                var username = dto.Username.Trim().ToLowerInvariant();
                var email = ResolveEmail(dto.Email, username, targetStore.StoreCode);
                var existingUser = await _db.Queryable<User>()
                    .Where(u => !u.IsDeleted && (u.Username.ToLower() == username || u.Email == email))
                    .FirstAsync();

                if (existingUser != null)
                {
                    if (existingUser.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                    {
                        return ApiResponse<StoreUserDetailDto>.Error("用户名已存在", "USERNAME_EXISTS");
                    }

                    return ApiResponse<StoreUserDetailDto>.Error("邮箱已存在", "EMAIL_EXISTS");
                }

                var storeStaffRole = await GetStoreStaffRoleAsync();
                if (storeStaffRole == null)
                {
                    return ApiResponse<StoreUserDetailDto>.Error("未找到 StoreStaff 角色", "ROLE_NOT_FOUND");
                }

                var userGuid = Guid.NewGuid().ToString();
                var now = DateTime.UtcNow;
                var user = new User
                {
                    UserGUID = userGuid,
                    Username = username,
                    Email = email,
                    PasswordHash = PasswordHasher.HashSubmittedPassword(dto.Password, dto.PasswordFormat),
                    FullName = dto.FullName?.Trim(),
                    IsActive = dto.Status == 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedBy = createdBy,
                    UpdatedBy = createdBy,
                };

                await _db.Ado.BeginTranAsync();
                try
                {
                    await _db.Insertable(user).ExecuteCommandAsync();
                    await _db.Insertable(
                        new UserRole
                        {
                            UserRoleGUID = Guid.NewGuid().ToString(),
                            UserGUID = userGuid,
                            RoleGUID = storeStaffRole.RoleGUID,
                            CreatedAt = now,
                            UpdatedAt = now,
                            CreatedBy = createdBy,
                            UpdatedBy = createdBy,
                        }
                    ).ExecuteCommandAsync();
                    await _db.Insertable(
                        new UserStore
                        {
                            UserStoreGUID = Guid.NewGuid().ToString(),
                            UserGUID = userGuid,
                            StoreGUID = targetStore.StoreGUID,
                            IsPrimary = false,
                            AssignedAt = now,
                            AssignedByGUID = scope.UserGuid,
                            CreatedAt = now,
                            UpdatedAt = now,
                            CreatedBy = createdBy,
                            UpdatedBy = createdBy,
                        }
                    ).ExecuteCommandAsync();
                    await _db.Insertable(
                        new EmployeeProfile
                        {
                            UserGUID = userGuid,
                            Phone = Normalize(dto.Phone),
                            EmployeeType = ParseEmployeeTypeOrDefault(dto.EmploymentType),
                            CreatedAt = now,
                            UpdatedAt = now,
                            CreatedBy = createdBy,
                            UpdatedBy = createdBy,
                        }
                    ).ExecuteCommandAsync();
                    if (dto.RequirePasswordChange ?? true)
                    {
                        // 与建号同事务：员工拿到初始密码后首次登录必须先改成自己的密码。
                        await UserPasswordChangeRequirements.RequireAsync(
                            _db,
                            userGuid,
                            UserPasswordChangeRequirement.ReasonCreated,
                            createdBy,
                            now
                        );
                    }

                    await _db.Ado.CommitTranAsync();
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync();
                    throw;
                }

                var detail = await LoadManagedUserAsync(userGuid, scope, targetStore.StoreCode);
                return ApiResponse<StoreUserDetailDto>.OK(detail!, "创建店员成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建店员失败，Username: {Username}", dto.Username);
                return ApiResponse<StoreUserDetailDto>.Error("创建店员失败", "CREATE_STORE_USER_FAILED");
            }
        }

        public async Task<ApiResponse<StoreUserDetailDto>> UpdateAsync(
            string userGuid,
            UpdateStoreUserDto dto,
            string updatedBy
        )
        {
            try
            {
                var scope = await _scopeService.GetScopeAsync();
                if (!scope.IsAllowed)
                {
                    return ApiResponse<StoreUserDetailDto>.Error(scope.Message, "FORBIDDEN");
                }

                var current = await LoadManagedUserAsync(userGuid, scope, dto.StoreCode);
                if (current == null)
                {
                    return await BuildMissingUserResponseAsync<StoreUserDetailDto>(
                        userGuid,
                        "未找到可管理的店员账号"
                    );
                }

                var targetStore = await ResolveTargetStoreAsync(dto.StoreCode, scope);
                if (targetStore == null)
                {
                    return ApiResponse<StoreUserDetailDto>.Error("没有权限编辑该分店店员", "FORBIDDEN");
                }

                var user = await _db.Queryable<User>()
                    .Where(item => item.UserGUID == userGuid && !item.IsDeleted)
                    .FirstAsync();
                if (user == null)
                {
                    return ApiResponse<StoreUserDetailDto>.Error("用户不存在", "USER_NOT_FOUND");
                }

                var nextUsername = string.IsNullOrWhiteSpace(dto.Username)
                    ? user.Username
                    : dto.Username.Trim().ToLowerInvariant();
                var nextEmail = ResolveEmail(dto.Email, nextUsername, targetStore.StoreCode, user.Email);

                var existingUser = await _db.Queryable<User>()
                    .Where(item =>
                        item.UserGUID != userGuid
                        && !item.IsDeleted
                        && (item.Username.ToLower() == nextUsername || item.Email == nextEmail)
                    )
                    .FirstAsync();
                if (existingUser != null)
                {
                    if (existingUser.Username.Equals(nextUsername, StringComparison.OrdinalIgnoreCase))
                    {
                        return ApiResponse<StoreUserDetailDto>.Error("用户名已存在", "USERNAME_EXISTS");
                    }

                    return ApiResponse<StoreUserDetailDto>.Error("邮箱已存在", "EMAIL_EXISTS");
                }

                var now = DateTime.UtcNow;
                // 关键逻辑：店员电话补档可能首建 EmployeeProfile，必须先取得资料生命周期锁。
                await using var lifecycleLock = await EmployeeProfileMediaLock
                    .AcquireProfileLifecycleAsync(_db, userGuid, _logger);
                await _db.Ado.BeginTranAsync();
                try
                {
                    user = await _db.Queryable<User>()
                        .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                    if (user is null)
                    {
                        await _db.Ado.RollbackTranAsync();
                        return ApiResponse<StoreUserDetailDto>.Error("用户不存在", "USER_NOT_FOUND");
                    }

                    user.Username = nextUsername;
                    user.Email = nextEmail;
                    user.FullName = dto.FullName?.Trim();
                    user.IsActive = dto.Status == 1;
                    user.UpdatedAt = now;
                    user.UpdatedBy = updatedBy;
                    await _db.Updateable(user).ExecuteCommandAsync();
                    await UpsertEmployeeProfilePhoneAsync(userGuid, dto.Phone, updatedBy, now);

                    await _db.Deleteable<UserStore>()
                        .Where(item => item.UserGUID == userGuid)
                        .ExecuteCommandAsync();
                    await _db.Insertable(
                        new UserStore
                        {
                            UserStoreGUID = Guid.NewGuid().ToString(),
                            UserGUID = userGuid,
                            StoreGUID = targetStore.StoreGUID,
                            IsPrimary = false,
                            AssignedAt = now,
                            AssignedByGUID = scope.UserGuid,
                            CreatedAt = now,
                            UpdatedAt = now,
                            CreatedBy = updatedBy,
                            UpdatedBy = updatedBy,
                        }
                    ).ExecuteCommandAsync();

                    await _db.Ado.CommitTranAsync();
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync();
                    throw;
                }

                var detail = await LoadManagedUserAsync(userGuid, scope, targetStore.StoreCode);
                return ApiResponse<StoreUserDetailDto>.OK(detail!, "更新店员成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新店员失败，UserGuid: {UserGuid}", userGuid);
                return ApiResponse<StoreUserDetailDto>.Error("更新店员失败", "UPDATE_STORE_USER_FAILED");
            }
        }

        public async Task<ApiResponse<bool>> UpdateStatusAsync(
            string userGuid,
            UpdateStoreUserStatusDto dto,
            string updatedBy
        )
        {
            try
            {
                var scope = await _scopeService.GetScopeAsync();
                if (!scope.IsAllowed)
                {
                    return ApiResponse<bool>.Error(scope.Message, "FORBIDDEN");
                }

                var current = await LoadManagedUserAsync(userGuid, scope, dto.StoreCode);
                if (current == null)
                {
                    return await BuildMissingUserResponseAsync<bool>(userGuid, "未找到可管理的店员账号");
                }

                var nextIsActive = dto.Status == 1;
                var now = DateTime.UtcNow;
                var result = await _db.Updateable<User>()
                    .SetColumns(item => item.IsActive == nextIsActive)
                    .SetColumns(item => item.UpdatedAt == now)
                    .SetColumns(item => item.UpdatedBy == updatedBy)
                    .Where(item => item.UserGUID == userGuid)
                    .ExecuteCommandAsync();

                return result > 0
                    ? ApiResponse<bool>.OK(true, "店员状态更新成功")
                    : ApiResponse<bool>.Error("用户不存在", "USER_NOT_FOUND");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新店员状态失败，UserGuid: {UserGuid}", userGuid);
                return ApiResponse<bool>.Error("更新店员状态失败", "UPDATE_STORE_USER_STATUS_FAILED");
            }
        }

        public async Task<ApiResponse<bool>> UpdatePasswordAsync(
            string userGuid,
            UpdateStoreUserPasswordDto dto,
            string updatedBy
        )
        {
            try
            {
                var scope = await _scopeService.GetScopeAsync();
                if (!scope.IsAllowed)
                {
                    return ApiResponse<bool>.Error(scope.Message, "FORBIDDEN");
                }

                var current = await LoadManagedUserAsync(userGuid, scope, dto.StoreCode);
                if (current == null)
                {
                    return await BuildMissingUserResponseAsync<bool>(userGuid, "未找到可管理的店员账号");
                }

                var now = DateTime.UtcNow;
                int result;
                await _db.Ado.BeginTranAsync();
                try
                {
                    result = await _db.Updateable<User>()
                        .SetColumns(item => new User
                        {
                            PasswordHash = PasswordHasher.HashSubmittedPassword(dto.NewPassword, dto.PasswordFormat),
                            UpdatedAt = now,
                            UpdatedBy = updatedBy,
                        })
                        .Where(item => item.UserGUID == userGuid)
                        .ExecuteCommandAsync();
                    if (result > 0 && (dto.RequirePasswordChange ?? true))
                    {
                        // 重置后的密码由店长掌握，员工下次进入 App 须先改成自己的密码。
                        await UserPasswordChangeRequirements.RequireAsync(
                            _db,
                            userGuid,
                            UserPasswordChangeRequirement.ReasonReset,
                            updatedBy,
                            now
                        );
                    }
                    await _db.Ado.CommitTranAsync();
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync();
                    throw;
                }

                return result > 0
                    ? ApiResponse<bool>.OK(true, "店员密码重置成功")
                    : ApiResponse<bool>.Error("用户不存在", "USER_NOT_FOUND");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重置店员密码失败，UserGuid: {UserGuid}", userGuid);
                return ApiResponse<bool>.Error("重置店员密码失败", "RESET_STORE_USER_PASSWORD_FAILED");
            }
        }

        private async Task<StoreUserDetailDto?> LoadManagedUserAsync(
            string userGuid,
            CurrentUserManageableStoreScope scope,
            string? storeCode
        )
        {
            var normalizedStoreCode = storeCode?.Trim();
            var rows = await _db.Queryable<User>()
                .InnerJoin<UserRole>((u, ur) => u.UserGUID == ur.UserGUID)
                .InnerJoin<Role>((u, ur, r) => ur.RoleGUID == r.RoleGUID)
                .InnerJoin<UserStore>((u, ur, r, us) => u.UserGUID == us.UserGUID)
                .InnerJoin<Store>((u, ur, r, us, s) => us.StoreGUID == s.StoreGUID)
                .LeftJoin<EmployeeProfile>((u, ur, r, us, s, profile) => u.UserGUID == profile.UserGUID)
                .Where((u, ur, r, us, s) =>
                    !u.IsDeleted
                    && !ur.IsDeleted
                    && !r.IsDeleted
                    && !us.IsDeleted
                    && !s.IsDeleted
                    && u.UserGUID == userGuid
                    && r.RoleName == StoreStaffRoleName
                )
                .WhereIF(
                    !scope.IsAdmin,
                    (u, ur, r, us, s, profile) => scope.StoreGuids.Contains(s.StoreGUID)
                )
                .WhereIF(
                    !string.IsNullOrWhiteSpace(normalizedStoreCode),
                    (u, ur, r, us, s, profile) => s.StoreCode == normalizedStoreCode
                )
                .Select((u, ur, r, us, s, profile) => new
                {
                    u.UserGUID,
                    u.Username,
                    u.FullName,
                    u.Email,
                    profile.Phone,
                    Status = u.IsActive ? 1 : 0,
                    StoreGuid = s.StoreGUID,
                    StoreCode = s.StoreCode,
                    StoreName = s.StoreName,
                    RoleName = r.RoleName,
                    LastLoginTime = u.LastLoginAt,
                    LastLoginIp = u.LastLoginIp,
                    u.CreatedAt,
                    u.UpdatedAt,
                    Birthday = profile.Birthday,
                    profile.Gender,
                    EmployeeType = profile.EmployeeType,
                    profile.AvatarUrl,
                    profile.IdentityId,
                    profile.Address,
                    BankBsb = profile.BankBSB,
                    BankAccountNumber = profile.BankACC,
                    profile.SuperannuationCompanyName,
                    profile.SuperannuationCompanyCode,
                    SuperannuationAccountNumber = profile.SuperannuationAccount,
                })
                .ToListAsync();

            var detail = rows
                .GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var first = group.First();
                    return new StoreUserDetailDto
                    {
                        UserGuid = first.UserGUID,
                        Username = first.Username,
                        FullName = first.FullName,
                        Email = first.Email,
                        Phone = first.Phone,
                        Status = first.Status,
                        StoreGuid = first.StoreGuid,
                        StoreCode = first.StoreCode,
                        StoreName = first.StoreName,
                        RoleNames = group
                            .Select(item => item.RoleName)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                        LastLoginTime = first.LastLoginTime,
                        LastLoginIp = first.LastLoginIp,
                        Birthday = first.Birthday,
                        Gender = FormatGender(first.Gender),
                        EmploymentType = FormatEmploymentType(first.EmployeeType),
                        AvatarUrl = first.AvatarUrl,
                        IdentityId = first.IdentityId,
                        Address = first.Address,
                        BankBsb = first.BankBsb,
                        BankAccountNumber = first.BankAccountNumber,
                        SuperannuationCompanyName = first.SuperannuationCompanyName,
                        SuperannuationCompanyCode = first.SuperannuationCompanyCode,
                        SuperannuationAccountNumber = first.SuperannuationAccountNumber,
                        CreatedAt = first.CreatedAt,
                        UpdatedAt = first.UpdatedAt,
                    };
                })
                .FirstOrDefault();
            if (detail != null)
            {
                detail.MustChangePassword = await UserPasswordChangeRequirements.IsRequiredAsync(_db, detail.UserGuid);
            }

            return detail;
        }

        private async Task<Role?> GetStoreStaffRoleAsync()
        {
            return await _db.Queryable<Role>()
                .Where(item => !item.IsDeleted && item.IsActive && item.RoleName == StoreStaffRoleName)
                .FirstAsync();
        }

        private async Task UpsertEmployeeProfilePhoneAsync(
            string userGuid,
            string? phone,
            string updatedBy,
            DateTime now
        )
        {
            var normalizedPhone = Normalize(phone);
            var profile = await _db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);

            if (profile == null)
            {
                try
                {
                    await _db.Insertable(
                        new EmployeeProfile
                        {
                            UserGUID = userGuid,
                            Phone = normalizedPhone,
                            CreatedAt = now,
                            UpdatedAt = now,
                            CreatedBy = updatedBy,
                            UpdatedBy = updatedBy,
                        }
                    ).ExecuteCommandAsync();
                    return;
                }
                catch (Exception ex) when (EmployeeCashierBarcodeService.IsUniqueConstraintViolation(ex))
                {
                    // 旧节点或外部写入绕过 lifecycle 锁时，以唯一键赢家为准后继续更新电话。
                    profile = await _db.Queryable<EmployeeProfile>()
                        .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                    if (profile is null)
                    {
                        throw;
                    }
                }
            }

            profile.Phone = normalizedPhone;
            profile.UpdatedAt = now;
            profile.UpdatedBy = updatedBy;
            await _db.Updateable(profile)
                .UpdateColumns(item => new { item.Phone, item.UpdatedAt, item.UpdatedBy })
                .ExecuteCommandAsync();
        }

        private async Task<ApiResponse<T>> BuildMissingUserResponseAsync<T>(
            string userGuid,
            string notFoundMessage
        )
        {
            var exists = await StoreStaffUserExistsAsync(userGuid);
            return exists
                ? ApiResponse<T>.Error("没有权限管理该店员账号", "FORBIDDEN")
                : ApiResponse<T>.Error(notFoundMessage, "USER_NOT_FOUND");
        }

        private async Task<bool> StoreStaffUserExistsAsync(string userGuid)
        {
            return await _db.Queryable<User>()
                .InnerJoin<UserRole>((u, ur) => u.UserGUID == ur.UserGUID)
                .InnerJoin<Role>((u, ur, r) => ur.RoleGUID == r.RoleGUID)
                .Where((u, ur, r) =>
                    !u.IsDeleted
                    && !ur.IsDeleted
                    && !r.IsDeleted
                    && u.UserGUID == userGuid
                    && r.RoleName == StoreStaffRoleName
                )
                .AnyAsync();
        }

        private async Task<Store?> ResolveTargetStoreAsync(
            string storeCode,
            CurrentUserManageableStoreScope scope
        )
        {
            var normalizedStoreCode = storeCode.Trim();
            if (string.IsNullOrWhiteSpace(normalizedStoreCode))
            {
                return null;
            }

            if (!scope.CanAccessStoreCode(normalizedStoreCode))
            {
                return null;
            }

            return await _db.Queryable<Store>()
                .Where(item => !item.IsDeleted && item.StoreCode == normalizedStoreCode)
                .FirstAsync();
        }

        private static string ResolveEmail(
            string? inputEmail,
            string username,
            string storeCode,
            string? fallbackEmail = null
        )
        {
            if (!string.IsNullOrWhiteSpace(inputEmail))
            {
                return inputEmail.Trim().ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(fallbackEmail))
            {
                return fallbackEmail.Trim().ToLowerInvariant();
            }

            return $"{username}@{storeCode.ToLowerInvariant()}.store.local";
        }

        private static string? Normalize(string? value)
        {
            var normalized = value?.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static EmployeeType ParseEmployeeTypeOrDefault(string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "fulltime" or "full_time" or "full-time" => EmployeeType.FullTime,
                "parttime" or "part_time" or "part-time" => EmployeeType.PartTime,
                "temporary" or "casual" => EmployeeType.Temporary,
                _ => EmployeeType.Temporary,
            };
        }

        private static string? FormatGender(EmployeeGender? value)
        {
            return value switch
            {
                EmployeeGender.Unknown => "unknown",
                EmployeeGender.Male => "male",
                EmployeeGender.Female => "female",
                EmployeeGender.Other => "other",
                _ => null,
            };
        }

        private static string? FormatEmploymentType(EmployeeType? value)
        {
            return value switch
            {
                EmployeeType.FullTime => "fullTime",
                EmployeeType.PartTime => "partTime",
                EmployeeType.Temporary => "casual",
                _ => null,
            };
        }
    }
}
