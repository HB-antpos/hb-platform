using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.Constants;
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

        private readonly IPasswordResetService? _passwordResetService;

        public StoreUserReactService(
            SqlSugarContext context,
            ILogger<StoreUserReactService> logger,
            ICurrentUserManageableStoreScopeService scopeService,
            IPasswordResetService? passwordResetService = null
        )
        {
            _db = context.Db;
            _logger = logger;
            _scopeService = scopeService;
            _passwordResetService = passwordResetService;
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

                // 关键逻辑：员工管理只列本店店员 + 登录店长本人。店长本人行靠「本人 + 店长角色」命中；
                // 管理员不是店长，不会混入本人行。其他店长（含同时挂了店员角色的）在下方整体剔除。
                var managerRoleNames = Permissions.StoreManagerRoleNames;
                var selfGuid = scope.IsStoreManager && !scope.IsAdmin ? scope.UserGuid : string.Empty;

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
                        && (
                            r.RoleName == StoreStaffRoleName
                            || (u.UserGUID == selfGuid && managerRoleNames.Contains(r.RoleName))
                        )
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
                            || (u.GivenName != null && u.GivenName.Contains(keyword!))
                            || (u.MiddleName != null && u.MiddleName.Contains(keyword!))
                            || (u.FamilyName != null && u.FamilyName.Contains(keyword!))
                            || (u.Nickname != null && u.Nickname.Contains(keyword!))
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
                        u.GivenName,
                        u.MiddleName,
                        u.FamilyName,
                        u.Nickname,
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

                // 剔除「同时拥有店长/管理员角色」的其他账号：只看店员角色那一行判断不出，需单独查角色。
                if (rows.Count > 0)
                {
                    var rowUserGuids = rows.Select(item => item.UserGUID).Distinct().ToList();
                    var highRoleNames = Permissions.HighPrivilegeRoleNames;
                    var highRoleUserGuids = (await _db.Queryable<UserRole>()
                            .InnerJoin<Role>((ur, r) => ur.RoleGUID == r.RoleGUID)
                            .Where((ur, r) =>
                                rowUserGuids.Contains(ur.UserGUID)
                                && !ur.IsDeleted
                                && !r.IsDeleted
                                && highRoleNames.Contains(r.RoleName)
                            )
                            .Select((ur, r) => ur.UserGUID)
                            .ToListAsync())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    rows = rows
                        .Where(item =>
                            !highRoleUserGuids.Contains(item.UserGUID)
                            || string.Equals(item.UserGUID, selfGuid, StringComparison.OrdinalIgnoreCase)
                        )
                        .ToList();
                }

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
                            GivenName = first.GivenName,
                            MiddleName = first.MiddleName,
                            FamilyName = first.FamilyName,
                            Nickname = first.Nickname,
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
                // 同样一次批量查询标注「不允许登录」的账号；无标记＝允许登录。
                var loginRestricted = await UserLoginRestrictions.GetRestrictedUserGuidsAsync(
                    _db,
                    grouped.Select(item => item.UserGuid).ToList()
                );
                foreach (var item in grouped)
                {
                    item.MustChangePassword = mustChange.Contains(item.UserGuid);
                    item.LoginEnabled = !loginRestricted.Contains(item.UserGuid);
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

                // 未传 LoginEnabled 视为允许登录，保持旧客户端（Web 等）行为不变。
                var loginEnabled = dto.LoginEnabled ?? true;
                // 不允许登录的账号不生成可用密码：既不要求密码，也不发初始密码邮件。
                var sendSetupEmail = loginEnabled && dto.SendPasswordSetupEmail == true;
                if (sendSetupEmail && !PasswordResetService.IsDeliverableEmail(dto.Email))
                {
                    return ApiResponse<StoreUserDetailDto>.Error("用邮件发送初始密码时必须填写员工邮箱", "VALIDATION_ERROR");
                }
                if (loginEnabled && !sendSetupEmail && string.IsNullOrWhiteSpace(dto.Password))
                {
                    return ApiResponse<StoreUserDetailDto>.Error("密码不能为空", "VALIDATION_ERROR");
                }

                var username = dto.Username.Trim().ToLowerInvariant();
                var email = ResolveEmail(dto.Email, username, targetStore.StoreCode);
                // 用户名、邮箱都不能与已有账号重复（口径同 UserIdentityUniqueness：含软删除账号、忽略大小写）。
                var conflictError = UserIdentityUniqueness.ToError<StoreUserDetailDto>(
                    await UserIdentityUniqueness.FindConflictAsync(_db, username, email)
                );
                if (conflictError != null)
                {
                    return conflictError;
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
                    // 邮件发送初始密码时，建号这一步先写入一个谁都不知道的随机密码；
                    // 提交后由 SendInitialPasswordAsync 换成系统生成的初始密码并发到员工邮箱。
                    // 不允许登录且未给密码时同样写入随机密码；之后开启登录须再给员工发初始密码。
                    PasswordHash = sendSetupEmail || string.IsNullOrWhiteSpace(dto.Password)
                        ? PasswordHasher.HashPassword(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)))
                        : PasswordHasher.HashSubmittedPassword(dto.Password!, dto.PasswordFormat),
                    IsActive = dto.Status == 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedBy = createdBy,
                    UpdatedBy = createdBy,
                };

                // 姓名：新拆分字段任一非空则存各段并合成姓名；都没传则沿用 dto.FullName（旧客户端行为不变）。
                var nameError = UserNameParts.ApplyOnCreate(
                    user,
                    new UserNameInput(
                        dto.FullName?.Trim(),
                        dto.GivenName,
                        dto.MiddleName,
                        dto.FamilyName,
                        dto.Nickname
                    )
                );
                if (nameError != null)
                {
                    return ApiResponse<StoreUserDetailDto>.Error(nameError, "VALIDATION_ERROR");
                }

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
                    if (!loginEnabled)
                    {
                        // 与建号同事务写入「不允许登录」标记，账号从创建那一刻起就无法登录。
                        await UserLoginRestrictions.RestrictAsync(_db, userGuid, createdBy, now);
                    }
                    else if (sendSetupEmail || (dto.RequirePasswordChange ?? true))
                    {
                        // 与建号同事务：员工拿到初始密码后首次登录必须先改成自己的密码。
                        // 邮件发送初始密码时无条件要求改密，不受 RequirePasswordChange 影响。
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
                if (sendSetupEmail && detail != null)
                {
                    // 账号已提交成功；发信失败不回滚建号，返回原因让店长稍后重发。
                    var invite = _passwordResetService == null
                        ? ApiResponse<PasswordSetupEmailResultDto>.Error("邮件服务未配置", "ACCOUNT_EMAIL_NOT_CONFIGURED")
                        : await _passwordResetService.SendInitialPasswordAsync(userGuid, createdBy);
                    if (invite.Success)
                    {
                        detail.PasswordSetupEmail = invite.Data;
                    }
                    else
                    {
                        detail.PasswordSetupEmailError = invite.Message;
                    }
                }
                return ApiResponse<StoreUserDetailDto>.OK(detail!, "创建店员成功");
            }
            catch (Exception ex)
            {
                // 并发建号时后到者撞唯一索引：重新判定是哪个字段重复，给出字段级提示而不是笼统失败。
                var conflictError = await UserIdentityUniqueness.TryMapWriteFailureAsync<StoreUserDetailDto>(
                    _db,
                    ex,
                    dto.Username,
                    dto.Email
                );
                if (conflictError != null)
                {
                    return conflictError;
                }

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
                var protectedTarget = await RejectProtectedTargetAsync<StoreUserDetailDto>(userGuid, scope);
                if (protectedTarget != null)
                {
                    return protectedTarget;
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

                // 排除自己：保持原用户名 / 邮箱保存不算重复。
                var conflictError = UserIdentityUniqueness.ToError<StoreUserDetailDto>(
                    await UserIdentityUniqueness.FindConflictAsync(_db, nextUsername, nextEmail, userGuid)
                );
                if (conflictError != null)
                {
                    return conflictError;
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

                    var emailChanged = !string.Equals(user.Email?.Trim(), nextEmail, StringComparison.OrdinalIgnoreCase);
                    user.Username = nextUsername;
                    user.Email = nextEmail;
                    // 关键逻辑：此处是整实体 Updateable，user 必须是刚从库里读出的完整实体，
                    // 这样老版移动端不带新字段时，四个拆分列会按库里原值写回，不会被刷成 null。
                    // 新字段 null=不改、空白=清空；旧客户端只发 FullName 时按旧行为设置并清理失配的拆分字段。
                    var nameError = UserNameParts.ApplyOnUpdate(
                        user,
                        new UserNameInput(
                            dto.FullName?.Trim(),
                            dto.GivenName,
                            dto.MiddleName,
                            dto.FamilyName,
                            dto.Nickname
                        )
                    );
                    if (nameError != null)
                    {
                        await _db.Ado.RollbackTranAsync();
                        return ApiResponse<StoreUserDetailDto>.Error(nameError, "VALIDATION_ERROR");
                    }
                    user.IsActive = dto.Status == 1;
                    user.UpdatedAt = now;
                    user.UpdatedBy = updatedBy;
                    await _db.Updateable(user).ExecuteCommandAsync();
                    if (emailChanged)
                    {
                        // 邮箱换了，发往旧邮箱的设置密码验证码不能再用。
                        await PasswordResetService.InvalidateOutstandingCodesAsync(_db, userGuid, now);
                    }
                    await UpsertEmployeeProfilePhoneAsync(userGuid, dto.Phone, updatedBy, now);
                    if (dto.LoginEnabled.HasValue)
                    {
                        // 与资料更新同事务切换登录权限；未传则不动，旧客户端编辑资料不会误改。
                        if (dto.LoginEnabled.Value)
                        {
                            await UserLoginRestrictions.AllowAsync(_db, userGuid);
                        }
                        else
                        {
                            await UserLoginRestrictions.RestrictAsync(_db, userGuid, updatedBy, now);
                        }
                    }
                    // 关键逻辑：编辑只改账号资料，不动分店关联。LoadManagedUserAsync 已确认员工属于目标分店；
                    // 旧实现会硬删该员工全部分店关联再插回一条非主分店记录，导致多分店员工丢失其他分店和主分店标记。

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
                // 并发改名撞唯一索引时（事务已回滚），重新判定重复字段给出友好提示。
                var conflictError = await UserIdentityUniqueness.TryMapWriteFailureAsync<StoreUserDetailDto>(
                    _db,
                    ex,
                    dto.Username,
                    dto.Email,
                    userGuid
                );
                if (conflictError != null)
                {
                    return conflictError;
                }

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
                var protectedTarget = await RejectProtectedTargetAsync<bool>(userGuid, scope);
                if (protectedTarget != null)
                {
                    return protectedTarget;
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
                var protectedTarget = await RejectProtectedTargetAsync<bool>(userGuid, scope);
                if (protectedTarget != null)
                {
                    return protectedTarget;
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
            // 与列表同口径：非管理员读不到其他店长 / 管理员的详情（即使对方同时挂着 StoreStaff 角色）。
            if (!scope.IsAdmin
                && !userGuid.Equals(scope.UserGuid, StringComparison.OrdinalIgnoreCase)
                && await UserAccessMutationSecurity.IsHighPrivilegeTargetAsync(_db, userGuid))
            {
                return null;
            }

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
                    u.GivenName,
                    u.MiddleName,
                    u.FamilyName,
                    u.Nickname,
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
                        GivenName = first.GivenName,
                        MiddleName = first.MiddleName,
                        FamilyName = first.FamilyName,
                        Nickname = first.Nickname,
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
                detail.LoginEnabled = !await UserLoginRestrictions.IsRestrictedAsync(_db, detail.UserGuid);
            }

            return detail;
        }

        /// <summary>店长给本店店员发设置密码邮件（新建后重发，或替代手动重置密码）。</summary>
        public async Task<ApiResponse<PasswordSetupEmailResultDto>> SendPasswordSetupEmailAsync(
            string userGuid,
            SendStoreUserPasswordSetupEmailDto dto,
            string requestedBy
        )
        {
            try
            {
                var scope = await _scopeService.GetScopeAsync();
                if (!scope.IsAllowed)
                {
                    return ApiResponse<PasswordSetupEmailResultDto>.Error(scope.Message, "FORBIDDEN");
                }

                var current = await LoadManagedUserAsync(userGuid, scope, dto.StoreCode);
                if (current == null)
                {
                    return await BuildMissingUserResponseAsync<PasswordSetupEmailResultDto>(
                        userGuid,
                        "未找到可管理的店员账号"
                    );
                }
                var protectedTarget = await RejectProtectedTargetAsync<PasswordSetupEmailResultDto>(userGuid, scope);
                if (protectedTarget != null)
                {
                    return protectedTarget;
                }
                if (_passwordResetService == null)
                {
                    return ApiResponse<PasswordSetupEmailResultDto>.Error("邮件服务未配置", "ACCOUNT_EMAIL_NOT_CONFIGURED");
                }

                if (!string.IsNullOrWhiteSpace(dto.Email))
                {
                    // 老账号补邮箱：先写入员工邮箱再发验证码，一步完成。验证码只发到这个邮箱，
                    // 只有邮箱本人收得到、设得了密码，所以店长写入邮箱不需要另做验证。
                    var saved = await SaveStaffEmailAsync(userGuid, dto.Email, requestedBy);
                    if (saved != null)
                    {
                        return saved;
                    }
                }

                // 新建员工页的「重新发送」要求再发初始密码（InitialPassword）；
                // 其余入口（重置密码、补邮箱）仍发验证码，员工自己设置。
                return dto.InitialPassword == true
                    ? await _passwordResetService.SendInitialPasswordAsync(userGuid, requestedBy)
                    : await _passwordResetService.SendInviteAsync(userGuid, requestedBy, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送店员设置密码邮件失败，UserGuid: {UserGuid}", userGuid);
                return ApiResponse<PasswordSetupEmailResultDto>.Error("发送设置密码邮件失败", "SEND_PASSWORD_SETUP_EMAIL_FAILED");
            }
        }

        /// <summary>写入店员邮箱（已通过范围与目标保护校验）；成功返回 null，失败返回错误响应。</summary>
        private async Task<ApiResponse<PasswordSetupEmailResultDto>?> SaveStaffEmailAsync(
            string userGuid,
            string email,
            string updatedBy
        )
        {
            var normalized = AccountEmailChangeService.NormalizeTargetEmail(email);
            if (normalized == null)
            {
                return ApiResponse<PasswordSetupEmailResultDto>.Error("请输入员工本人的有效邮箱", "INVALID_EMAIL");
            }

            var now = DateTime.UtcNow;
            await _db.Ado.BeginTranAsync();
            try
            {
                var user = await _db.Queryable<User>()
                    .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                if (user == null)
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<PasswordSetupEmailResultDto>.Error("用户不存在", "USER_NOT_FOUND");
                }
                if (string.Equals(user.Email?.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    // 邮箱没变：直接重发即可。
                    await _db.Ado.RollbackTranAsync();
                    return null;
                }
                var duplicate = await _db.Queryable<User>().AnyAsync(item =>
                    item.UserGUID != userGuid && !item.IsDeleted && item.Email.ToLower() == normalized);
                if (duplicate)
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<PasswordSetupEmailResultDto>.Error("该邮箱已被其他账号使用", "EMAIL_EXISTS");
                }
                await _db.Updateable<User>()
                    .SetColumns(item => item.Email == normalized)
                    .SetColumns(item => item.UpdatedAt == now)
                    .SetColumns(item => item.UpdatedBy == updatedBy)
                    .Where(item => item.UserGUID == userGuid && !item.IsDeleted)
                    .ExecuteCommandAsync();
                await PasswordResetService.InvalidateOutstandingCodesAsync(_db, userGuid, now);
                await _db.Ado.CommitTranAsync();
                return null;
            }
            catch
            {
                await _db.Ado.RollbackTranAsync();
                throw;
            }
        }

        /// <summary>
        /// 写操作的目标保护：店长不能改本人账号（改本人密码须走需要旧密码的改密接口），
        /// 也不能改同时持有高权限角色或用户管理类权限的账号（例如兼任店员的管理员或外店店长）。
        /// LoadManagedUserAsync 只校验「店员角色 + 本店」，这里补齐与店员条码服务一致的排除规则。
        /// </summary>
        private async Task<ApiResponse<T>?> RejectProtectedTargetAsync<T>(
            string userGuid,
            CurrentUserManageableStoreScope scope
        )
        {
            if (scope.IsAdmin)
            {
                return null;
            }
            if (userGuid.Equals(scope.UserGuid, StringComparison.OrdinalIgnoreCase))
            {
                return ApiResponse<T>.Error("不能在员工列表中修改本人账号", "FORBIDDEN");
            }
            if (await UserAccessMutationSecurity.IsHighPrivilegeTargetAsync(_db, userGuid))
            {
                return ApiResponse<T>.Error("没有权限管理该账号", "FORBIDDEN");
            }
            return null;
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
