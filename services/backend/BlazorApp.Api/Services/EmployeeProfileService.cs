using System.Data;
using System.Net.Mail;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    public class EmployeeProfileService : IEmployeeProfileService
    {
        public const string PendingChangeConfirmationRequiredCode =
            "EMPLOYEE_PROFILE_PENDING_CHANGE_CONFIRMATION_REQUIRED";
        public const string BirthdayRequiresReviewCode = "BIRTHDAY_REQUIRES_REVIEW";
        public const string EmailRequiresVerificationCode = "EMAIL_REQUIRES_VERIFICATION";

        private readonly SqlSugarContext _context;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<EmployeeProfileService> _logger;
        private readonly TencentCloudUploadService? _uploadService;
        private readonly EmployeeProfileSensitiveChangeService? _sensitiveChangeService;
        private readonly IRoleService? _roleService;

        public EmployeeProfileService(
            SqlSugarContext context,
            ICurrentUserService currentUserService,
            ILogger<EmployeeProfileService> logger,
            TencentCloudUploadService? uploadService = null,
            EmployeeProfileSensitiveChangeService? sensitiveChangeService = null,
            IRoleService? roleService = null
        )
        {
            _context = context;
            _currentUserService = currentUserService;
            _logger = logger;
            _uploadService = uploadService;
            _sensitiveChangeService = sensitiveChangeService;
            _roleService = roleService;
        }

        public async Task<ApiResponse<PagedResult<EmployeeProfileListItemDto>>> GetAdminListAsync(
            EmployeeProfileQueryDto query
        )
        {
            try
            {
                var db = _context.Db;
                var baseQuery = db.Queryable<User>()
                    .LeftJoin<EmployeeProfile>(
                        (user, profile) =>
                            user.UserGUID == profile.UserGUID && !profile.IsDeleted
                    )
                    .Where((user, profile) => !user.IsDeleted);

                if (!string.IsNullOrWhiteSpace(query.Search))
                {
                    var keyword = query.Search.Trim();
                    baseQuery = baseQuery.Where(
                        (user, profile) =>
                            user.Username.Contains(keyword)
                            || user.Email.Contains(keyword)
                            || (user.FullName != null && user.FullName.Contains(keyword))
                            || (profile.Address != null && profile.Address.Contains(keyword))
                            || (profile.IdentityId != null && profile.IdentityId.Contains(keyword))
                    );
                }

                if (query.HasProfile.HasValue)
                {
                    if (query.HasProfile.Value)
                    {
                        baseQuery = baseQuery.Where((user, profile) => profile.EmployeeInfoId > 0);
                    }
                    else
                    {
                        baseQuery = baseQuery.Where((user, profile) => profile.EmployeeInfoId == 0);
                    }
                }

                var employeeType = ParseEmployeeType(query.EmployeeType);
                if (employeeType.HasValue)
                {
                    baseQuery = baseQuery.Where(
                        (user, profile) => profile.EmployeeType == employeeType.Value
                    );
                }

                var total = await baseQuery.CountAsync();
                var rows = await baseQuery
                    .OrderBy((user, profile) => user.Username)
                    .Select(
                        (user, profile) =>
                            new
                            {
                                profile.EmployeeInfoId,
                                user.UserGUID,
                                user.Username,
                                user.Email,
                                user.FullName,
                                profile.Phone,
                                profile.BankBSB,
                                profile.BankACC,
                                profile.SuperannuationCompanyName,
                                profile.SuperannuationCompanyCode,
                                profile.SuperannuationAccount,
                                profile.Gender,
                                profile.EmployeeType,
                                profile.Birthday,
                                profile.AvatarUrl,
                                profile.UpdatedAt,
                            }
                    )
                    .Skip((query.Page - 1) * query.PageSize)
                    .Take(query.PageSize)
                    .ToListAsync();

                var items = rows
                    .Select(row => new EmployeeProfileListItemDto
                    {
                        EmployeeInfoId = row.EmployeeInfoId > 0 ? row.EmployeeInfoId : null,
                        UserGUID = row.UserGUID,
                        Username = row.Username,
                        Email = row.Email,
                        FullName = row.FullName,
                        HasProfile = row.EmployeeInfoId > 0,
                        Phone = row.Phone,
                        BankBsb = row.BankBSB,
                        // 管理列表只返回摘要；完整账号仅允许通过授权详情接口读取。
                        BankAccountNumber = MaskAccount(row.BankACC),
                        SuperannuationCompanyName = row.SuperannuationCompanyName,
                        SuperannuationCompanyCode = row.SuperannuationCompanyCode,
                        SuperannuationAccountNumber = MaskAccount(row.SuperannuationAccount),
                        Gender = FormatGender(row.Gender),
                        EmployeeType = FormatEmployeeType(row.EmployeeType),
                        Birthday = row.Birthday,
                        AvatarUrl = row.AvatarUrl,
                        UpdatedAt = row.UpdatedAt,
                    })
                    .ToList();

                return ApiResponse<PagedResult<EmployeeProfileListItemDto>>.OK(
                    new PagedResult<EmployeeProfileListItemDto>
                    {
                        Items = items,
                        Total = total,
                        Page = query.Page,
                        PageSize = query.PageSize,
                    },
                    "获取员工个人信息列表成功"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取员工个人信息列表失败");
                return ApiResponse<PagedResult<EmployeeProfileListItemDto>>.Error(
                    "获取员工个人信息列表失败",
                    "GET_EMPLOYEE_PROFILES_FAILED"
                );
            }
        }

        public Task<ApiResponse<EmployeeProfileDetailDto>> GetAdminDetailAsync(string userGuid)
        {
            return GetByUserGuidAsync(userGuid);
        }

        public async Task<ApiResponse<EmployeeProfileDetailDto>> UpsertAdminAsync(
            string userGuid,
            EmployeeProfileUpsertDto dto
        )
        {
            return await UpsertForUserAsync(userGuid, dto, allowLegacyImageUrls: true, isAdmin: true);
        }

        public async Task<ApiResponse<EmployeeProfileDetailDto>> GetSelfAsync()
        {
            var userGuid = _currentUserService.GetCurrentUserGuid();
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return ApiResponse<EmployeeProfileDetailDto>.Error("未找到当前用户", "CURRENT_USER_NOT_FOUND");
            }

            try
            {
                var db = _context.Db;
                var user = await db.Queryable<User>()
                    .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                if (user == null)
                {
                    return ApiResponse<EmployeeProfileDetailDto>.Error("用户不存在", "USER_NOT_FOUND");
                }

                var profile = await db.Queryable<EmployeeProfile>()
                    .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                if (profile != null)
                {
                    // 关键逻辑：已有有效资料只读返回，不等待首建所需的敏感变更与生命周期锁。
                    return ApiResponse<EmployeeProfileDetailDto>.OK(
                        MapDetail(user, profile),
                        "获取员工个人信息成功"
                    );
                }

                EmployeeProfileDataRemovalPlan? removalPlan = null;
                {
                    // 关键逻辑：资料首建与用户删除统一按 sensitive-change → lifecycle → transaction 排序。
                    await using var sensitiveChangeLock = await EmployeeProfileMediaLock.AcquireAsync(
                        db,
                        userGuid,
                        "sensitive-change",
                        _logger
                    );
                    await using var lifecycleLock = await EmployeeProfileMediaLock
                        .AcquireProfileLifecycleAsync(db, userGuid, _logger);
                    await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
                    try
                    {
                        // 等锁期间用户可能已被物理删除、资料也可能已由并发请求创建，必须在事务内重读。
                        user = await db.Queryable<User>()
                            .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                        if (user == null)
                        {
                            await db.Ado.RollbackTranAsync();
                            return ApiResponse<EmployeeProfileDetailDto>.Error("用户不存在", "USER_NOT_FOUND");
                        }

                        profile = await db.Queryable<EmployeeProfile>()
                            .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                        if (profile == null)
                        {
                            var actor = _currentUserService.GetCurrentUsername();
                            var now = DateTime.UtcNow;
                            var deletedProfile = await db.Queryable<EmployeeProfile>()
                                .FirstAsync(item => item.UserGUID == userGuid);
                            if (_sensitiveChangeService is not null)
                            {
                                removalPlan = await _sensitiveChangeService
                                    .PrepareProfileDataRemovalWithinTransactionAsync(
                                        userGuid,
                                        deletedProfile,
                                        actor
                                    );
                            }

                            profile = await CreateSelfProfileIfMissingAsync(
                                db,
                                userGuid,
                                actor,
                                now,
                                deletedProfile
                            );
                        }

                        await db.Ado.CommitTranAsync();
                    }
                    catch
                    {
                        await db.Ado.RollbackTranAsync();
                        throw;
                    }
                }

                if (removalPlan is not null && _sensitiveChangeService is not null)
                {
                    try
                    {
                        await _sensitiveChangeService.CleanupProfileDataRemovalAsync(
                            userGuid,
                            removalPlan
                        );
                    }
                    catch (Exception cleanupException)
                    {
                        // COS 清理失败不影响已提交的资料恢复；后台/重试链路可继续处理。
                        _logger.LogWarning(
                            cleanupException,
                            "清理员工资料历史对象失败，UserGUID: {UserGUID}",
                            userGuid
                        );
                    }
                }

                return ApiResponse<EmployeeProfileDetailDto>.OK(
                    MapDetail(user, profile),
                    "获取员工个人信息成功"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取当前员工个人信息失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<EmployeeProfileDetailDto>.Error(
                    "获取员工个人信息失败",
                    "GET_EMPLOYEE_PROFILE_FAILED"
                );
            }
        }

        public async Task<ApiResponse<EmployeeProfileDetailDto>> UpsertSelfAsync(EmployeeProfileUpsertDto dto)
        {
            var userGuid = _currentUserService.GetCurrentUserGuid();
            return await UpsertForUserAsync(userGuid, dto, allowLegacyImageUrls: false, isAdmin: false);
        }

        private async Task<ApiResponse<EmployeeProfileDetailDto>> GetByUserGuidAsync(string userGuid)
        {
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return ApiResponse<EmployeeProfileDetailDto>.Error("未找到当前用户", "CURRENT_USER_NOT_FOUND");
            }

            try
            {
                var db = _context.Db;
                var user = await db.Queryable<User>()
                    .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);

                if (user == null)
                {
                    return ApiResponse<EmployeeProfileDetailDto>.Error("用户不存在", "USER_NOT_FOUND");
                }

                var profile = await db.Queryable<EmployeeProfile>()
                    .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);

                return ApiResponse<EmployeeProfileDetailDto>.OK(MapDetail(user, profile), "获取员工个人信息成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取员工个人信息失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<EmployeeProfileDetailDto>.Error(
                    "获取员工个人信息失败",
                    "GET_EMPLOYEE_PROFILE_FAILED"
                );
            }
        }

        private async Task<EmployeeProfile> CreateSelfProfileIfMissingAsync(
            ISqlSugarClient db,
            string userGuid,
            string actor,
            DateTime now,
            EmployeeProfile? deletedProfile
        )
        {
            if (deletedProfile != null)
            {
                var reactivatedProfile = new EmployeeProfile
                {
                    EmployeeInfoId = deletedProfile.EmployeeInfoId,
                    UserGUID = userGuid,
                    CreatedAt = now,
                    CreatedBy = actor,
                    UpdatedAt = now,
                    UpdatedBy = actor,
                    IsDeleted = false,
                    SensitiveRevision = 0,
                };

                // 关键逻辑：UserGUID 唯一键不包含软删除标记，必须复用旧行并清空资料，避免旧敏感数据重新可见。
                var reactivated = await db.Updateable<EmployeeProfile>()
                    .SetColumns(item => item.BankBSB == null)
                    .SetColumns(item => item.BankACC == null)
                    .SetColumns(item => item.SuperannuationCompanyName == null)
                    .SetColumns(item => item.SuperannuationCompanyCode == null)
                    .SetColumns(item => item.SuperannuationAccount == null)
                    .SetColumns(item => item.Phone == null)
                    .SetColumns(item => item.Birthday == null)
                    .SetColumns(item => item.Gender == null)
                    .SetColumns(item => item.EmployeeType == null)
                    .SetColumns(item => item.AvatarUrl == null)
                    .SetColumns(item => item.IdentityId == null)
                    .SetColumns(item => item.IdentityType == null)
                    .SetColumns(item => item.IdentityPhotoUrl == null)
                    .SetColumns(item => item.IdentityPhotoObjectKey == null)
                    .SetColumns(item => item.Address == null)
                    .SetColumns(item => item.SensitiveRevision == 0)
                    .SetColumns(item => item.CreatedAt == now)
                    .SetColumns(item => item.CreatedBy == actor)
                    .SetColumns(item => item.UpdatedAt == now)
                    .SetColumns(item => item.UpdatedBy == actor)
                    .SetColumns(item => item.IsDeleted == false)
                    .Where(item =>
                        item.EmployeeInfoId == deletedProfile.EmployeeInfoId && item.IsDeleted
                    )
                    .ExecuteCommandAsync();
                if (reactivated == 1)
                {
                    return reactivatedProfile;
                }

                var winner = await db.Queryable<EmployeeProfile>()
                    .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                if (winner != null)
                {
                    return winner;
                }

                throw new InvalidOperationException("恢复软删除员工资料时状态已变化");
            }

            var profile = new EmployeeProfile
            {
                UserGUID = userGuid,
                CreatedAt = now,
                CreatedBy = actor,
                UpdatedAt = now,
                UpdatedBy = actor,
                IsDeleted = false,
                SensitiveRevision = 0,
            };

            try
            {
                profile.EmployeeInfoId = await db.Insertable(profile).ExecuteReturnIdentityAsync();
                return profile;
            }
            catch (Exception ex) when (IsUniqueConstraintViolation(ex))
            {
                // 跨进程竞争由唯一键兜底；冲突后回读胜出的有效资料，保持 GET 幂等。
                var winner = await db.Queryable<EmployeeProfile>()
                    .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                if (winner != null)
                {
                    return winner;
                }

                throw;
            }
        }

        private static bool IsUniqueConstraintViolation(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                var message = current.Message;
                if (message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("2601", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("2627", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<ApiResponse<EmployeeProfileDetailDto>> UpsertForUserAsync(
            string userGuid,
            EmployeeProfileUpsertDto dto,
            bool allowLegacyImageUrls,
            bool isAdmin
        )
        {
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return ApiResponse<EmployeeProfileDetailDto>.Error("未找到当前用户", "CURRENT_USER_NOT_FOUND");
            }

            try
            {
                string? requestedEmail = null;
                if (dto.HasEmail)
                {
                    requestedEmail = dto.Email?.Trim();
                    if (!IsValidEmail(requestedEmail))
                    {
                        return ApiResponse<EmployeeProfileDetailDto>.Error("邮箱格式无效", "INVALID_EMAIL");
                    }
                }
                var db = _context.Db;
                var user = await db.Queryable<User>()
                    .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);

                if (user == null)
                {
                    return ApiResponse<EmployeeProfileDetailDto>.Error("用户不存在", "USER_NOT_FOUND");
                }

                var actor = _currentUserService.GetCurrentUsername();
                var now = DateTime.UtcNow;
                EmployeeProfile? profile;
                EmployeeProfileSensitiveChangeUpsertDto? legacySensitiveDto = null;
                var supersededKeys = new List<string>();
                if (isAdmin)
                {
                    // 关键逻辑：管理员必须先取得敏感资料锁，再在事务内读取最新版本。
                    await using var adminSensitiveLock = await EmployeeProfileMediaLock.AcquireAsync(
                        db,
                        userGuid,
                        "sensitive-change",
                        _logger
                    );
                    await using var adminLifecycleLock = await EmployeeProfileMediaLock
                        .AcquireProfileLifecycleAsync(db, userGuid, _logger);
                    await db.Ado.BeginTranAsync();
                    try
                    {
                        user = await db.Queryable<User>()
                            .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                        if (user == null)
                        {
                            await db.Ado.RollbackTranAsync();
                            return ApiResponse<EmployeeProfileDetailDto>.Error(
                                "用户不存在",
                                "USER_NOT_FOUND"
                            );
                        }

                        profile = await db.Queryable<EmployeeProfile>()
                            .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                        var sensitiveChanged = HasSensitiveChanges(profile, dto);
                        var currentSensitiveRevision = profile?.SensitiveRevision ?? 0;
                        if (sensitiveChanged
                            && dto.ExpectedSensitiveRevision.HasValue
                            && dto.ExpectedSensitiveRevision.Value != currentSensitiveRevision)
                        {
                            // 敏感表单 CAS 与写入共用锁和事务；过期表单不得顺带写入非敏感字段。
                            await db.Ado.RollbackTranAsync();
                            return ApiResponse<EmployeeProfileDetailDto>.Error(
                                "正式敏感资料已变化，请刷新后重试",
                                EmployeeProfileSensitiveChangeService.VersionConflictCode
                            );
                        }
                        var hasPendingSensitiveChange = sensitiveChanged
                            && await db.Queryable<EmployeeProfileSensitiveChangeRequest>()
                                .AnyAsync(item =>
                                    item.UserGUID == userGuid
                                    && item.Status == EmployeeProfileSensitiveChangeStatus.Pending
                                );
                        if (hasPendingSensitiveChange
                            && dto.ConfirmSupersedePendingSensitiveChangeRequest != true)
                        {
                            // 确认检查与后续写入共用敏感资料锁和事务，失败时不得写入任何资料。
                            await db.Ado.RollbackTranAsync();
                            return ApiResponse<EmployeeProfileDetailDto>.Error(
                                "存在待审核敏感资料变更，请确认后重试",
                                PendingChangeConfirmationRequiredCode
                            );
                        }
                        if (profile == null)
                        {
                            profile = new EmployeeProfile
                            {
                                UserGUID = userGuid,
                                CreatedAt = now,
                                CreatedBy = actor,
                                UpdatedAt = now,
                                UpdatedBy = actor,
                            };
                            ApplyChanges(
                                profile,
                                dto,
                                userGuid,
                                actor,
                                now,
                                isCreate: true,
                                allowLegacyImageUrls,
                                allowSensitiveChanges: true
                            );
                            profile.SensitiveRevision = sensitiveChanged ? 1 : 0;
                            profile.EmployeeInfoId = await db.Insertable(profile)
                                .ExecuteReturnIdentityAsync();
                        }
                        else
                        {
                            var baseRevision = profile.SensitiveRevision;
                            ApplyChanges(
                                profile,
                                dto,
                                userGuid,
                                actor,
                                now,
                                isCreate: false,
                                allowLegacyImageUrls,
                                allowSensitiveChanges: true
                            );
                            var nextRevision = sensitiveChanged
                                ? baseRevision + 1
                                : baseRevision;
                            var update = db.Updateable<EmployeeProfile>()
                                .SetColumns(item => item.Phone == profile.Phone)
                                .SetColumns(item => item.BankBSB == profile.BankBSB)
                                .SetColumns(item => item.BankACC == profile.BankACC)
                                .SetColumns(item => item.SuperannuationCompanyName == profile.SuperannuationCompanyName)
                                .SetColumns(item => item.SuperannuationCompanyCode == profile.SuperannuationCompanyCode)
                                .SetColumns(item => item.SuperannuationAccount == profile.SuperannuationAccount)
                                .SetColumns(item => item.Birthday == profile.Birthday)
                                .SetColumns(item => item.Gender == profile.Gender)
                                .SetColumns(item => item.EmployeeType == profile.EmployeeType)
                                .SetColumns(item => item.AvatarUrl == profile.AvatarUrl)
                                .SetColumns(item => item.IdentityType == profile.IdentityType)
                                .SetColumns(item => item.IdentityId == profile.IdentityId)
                                .SetColumns(item => item.Address == profile.Address)
                                .SetColumns(item => item.UpdatedAt == now)
                                .SetColumns(item => item.UpdatedBy == actor)
                                .SetColumns(item => item.IsDeleted == false)
                                .SetColumns(item => item.SensitiveRevision == nextRevision);
                            if (string.IsNullOrWhiteSpace(profile.IdentityPhotoObjectKey))
                            {
                                update = update.SetColumns(item =>
                                    item.IdentityPhotoUrl == profile.IdentityPhotoUrl
                                );
                            }
                            var changed = await update
                                .Where(item => item.EmployeeInfoId == profile.EmployeeInfoId
                                    && item.SensitiveRevision == baseRevision)
                                .ExecuteCommandAsync();
                            if (changed != 1)
                            {
                                throw new InvalidOperationException("管理员保存时敏感资料版本已改变");
                            }
                            profile.SensitiveRevision = nextRevision;
                        }
                        if (dto.HasEmail && !string.Equals(user.Email, requestedEmail, StringComparison.Ordinal))
                        {
                            var duplicate = await db.Queryable<User>().AnyAsync(item =>
                                item.UserGUID != userGuid && !item.IsDeleted && item.Email == requestedEmail);
                            if (duplicate)
                            {
                                await db.Ado.RollbackTranAsync();
                                return ApiResponse<EmployeeProfileDetailDto>.Error("邮箱已存在", "EMAIL_EXISTS");
                            }
                            await db.Updateable<User>()
                                .SetColumns(item => item.Email == requestedEmail!)
                                .SetColumns(item => item.UpdatedAt == now)
                                .SetColumns(item => item.UpdatedBy == actor)
                                .Where(item => item.UserGUID == userGuid && !item.IsDeleted)
                                .ExecuteCommandAsync();
                            // 发往旧邮箱的设置密码验证码、进行中的换邮箱验证码随之作废。
                            await PasswordResetService.InvalidateOutstandingCodesAsync(db, userGuid, now);
                            user.Email = requestedEmail!;
                        }
                        if (sensitiveChanged && _sensitiveChangeService is not null)
                        {
                            // 管理员直改与待审申请失效必须处在同一事务内。
                            supersededKeys = await _sensitiveChangeService
                                .SupersedePendingWithinTransactionAsync(userGuid, actor);
                        }
                        await db.Ado.CommitTranAsync();
                    }
                    catch (Exception ex) when (IsUniqueConstraintViolation(ex))
                    {
                        await db.Ado.RollbackTranAsync();
                        return ApiResponse<EmployeeProfileDetailDto>.Error("邮箱已被其他用户使用", "EMAIL_EXISTS");
                    }
                    catch
                    {
                        await db.Ado.RollbackTranAsync();
                        throw;
                    }
                }
                else
                {
                    // 自助保存与管理员保存共用生命周期锁，职位权限判断必须基于锁内最新资料。
                    await using var selfProfileLock = await EmployeeProfileMediaLock
                        .AcquireProfileLifecycleAsync(db, userGuid, _logger);
                    await db.Ado.BeginTranAsync();
                    try
                    {
                        // 锁内重读用户，避免用户在锁外查询后被删除或邮箱被并发修改。
                        user = await db.Queryable<User>()
                            .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                        if (user == null)
                        {
                            await db.Ado.RollbackTranAsync();
                            return ApiResponse<EmployeeProfileDetailDto>.Error("用户不存在", "USER_NOT_FOUND");
                        }
                        profile = await db.Queryable<EmployeeProfile>()
                            .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                        if (dto.HasBirthday && profile?.Birthday?.Date != dto.Birthday?.Date)
                        {
                            // 生日属于敏感资料：员工自助保存不能直接改，须在敏感资料中提交审核。
                            // 旧版客户端会回传当前生日，等值时照常保存其余字段。
                            await db.Ado.RollbackTranAsync();
                            return ApiResponse<EmployeeProfileDetailDto>.Error(
                                "生日需要提交审核，请更新 App 后在敏感资料中修改",
                                BirthdayRequiresReviewCode
                            );
                        }
                        if (dto.HasEmail
                            && !string.Equals(user.Email?.Trim(), requestedEmail, StringComparison.OrdinalIgnoreCase))
                        {
                            // 邮箱是找回密码的渠道：员工自助更换须先给新邮箱发验证码（api/Auth/email-change）。
                            // 旧版客户端会回传当前邮箱，等值（忽略大小写）时照常保存其余字段。
                            await db.Ado.RollbackTranAsync();
                            return ApiResponse<EmployeeProfileDetailDto>.Error(
                                "更换邮箱需要先验证新邮箱，请更新 App 后在「绑定邮箱」中修改",
                                EmailRequiresVerificationCode
                            );
                        }
                        var existingEmployeeType = profile?.EmployeeType;
                        var createdProfile = false;
                        if (profile == null)
                        {
                            profile = new EmployeeProfile
                            {
                                UserGUID = userGuid,
                                CreatedAt = now,
                                CreatedBy = actor,
                                UpdatedAt = now,
                                UpdatedBy = actor,
                            };
                            ApplyChanges(
                                profile,
                                dto,
                                userGuid,
                                actor,
                                now,
                                isCreate: true,
                                allowLegacyImageUrls: false,
                                allowSensitiveChanges: false
                            );
                            try
                            {
                                profile.EmployeeInfoId = await db.Insertable(profile)
                                    .ExecuteReturnIdentityAsync();
                                createdProfile = true;
                            }
                            catch (Exception ex) when (IsUniqueConstraintViolation(ex))
                            {
                                profile = await db.Queryable<EmployeeProfile>()
                                    .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                                if (profile == null)
                                {
                                    throw;
                                }
                            }
                        }
                        // 权限与写入共用锁和事务，避免并发请求依据旧职位类型放行。
                        if (dto.HasEmploymentType
                            && ParseEmployeeType(dto.EmploymentType) != existingEmployeeType)
                        {
                            var hasPermission = _roleService is not null
                                && (await _roleService.UserHasPermissionAsync(
                                    userGuid,
                                    Permissions.EmployeeProfiles.EditPositionType
                                )).Data == true;
                            if (!hasPermission)
                            {
                                await db.Ado.RollbackTranAsync();
                                return ApiResponse<EmployeeProfileDetailDto>.Error(
                                    "无权修改职位类型",
                                    "FORBIDDEN"
                                );
                            }
                        }
                        if (!createdProfile)
                        {
                            // 员工自助保存只更新非敏感白名单列，绝不写回敏感字段（含生日）或 revision。
                            var profileUpdate = db.Updateable<EmployeeProfile>()
                                .SetColumns(item => item.Phone == Normalize(dto.Phone))
                                .SetColumns(item => item.Gender == ParseGender(dto.Gender))
                                .SetColumns(item => item.Address == Normalize(dto.Address))
                                .SetColumns(item => item.UpdatedAt == now)
                                .SetColumns(item => item.UpdatedBy == actor)
                                .SetColumns(item => item.IsDeleted == false);
                            if (dto.HasEmploymentType)
                            {
                                profileUpdate = profileUpdate.SetColumns(item => item.EmployeeType == ParseEmployeeType(dto.EmploymentType));
                            }
                            await profileUpdate.Where(item => item.EmployeeInfoId == profile.EmployeeInfoId).ExecuteCommandAsync();
                            profile = await db.Queryable<EmployeeProfile>()
                                .FirstAsync(item => item.EmployeeInfoId == profile.EmployeeInfoId);
                        }
                        if (HasLegacySensitivePayload(dto) && HasLegacySensitiveChanges(profile, dto))
                        {
                            // 旧 App 会在普通保存中回传完整敏感字段；等值回传不得覆盖真实待审申请。
                            legacySensitiveDto = BuildLegacySensitiveSnapshot(profile, dto);
                        }
                        await db.Ado.CommitTranAsync();
                    }
                    catch (Exception ex) when (IsUniqueConstraintViolation(ex))
                    {
                        await db.Ado.RollbackTranAsync();
                        return ApiResponse<EmployeeProfileDetailDto>.Error("邮箱已被其他用户使用", "EMAIL_EXISTS");
                    }
                    catch
                    {
                        await db.Ado.RollbackTranAsync();
                        throw;
                    }
                }

                if (isAdmin && supersededKeys.Count > 0 && _sensitiveChangeService is not null)
                {
                    await _sensitiveChangeService.CleanupSupersededObjectsAsync(userGuid, supersededKeys);
                }
                if (legacySensitiveDto is not null && _sensitiveChangeService is not null)
                {
                    // 旧客户端仍可提交同一 DTO，但敏感字段只能进入审批快照。
                    await _sensitiveChangeService.UpsertSelfAsync(legacySensitiveDto);
                }

                return ApiResponse<EmployeeProfileDetailDto>.OK(
                    MapDetail(user, profile),
                    "保存员工个人信息成功"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存员工个人信息失败，UserGUID: {UserGUID}", userGuid);
                return ApiResponse<EmployeeProfileDetailDto>.Error(
                    "保存员工个人信息失败",
                    "UPSERT_EMPLOYEE_PROFILE_FAILED"
                );
            }
        }

        private static void ApplyChanges(
            EmployeeProfile profile,
            EmployeeProfileUpsertDto dto,
            string userGuid,
            string actor,
            DateTime now,
            bool isCreate,
            bool allowLegacyImageUrls,
            bool allowSensitiveChanges
        )
        {
            profile.UserGUID = userGuid;
            profile.Phone = Normalize(dto.Phone);
            if (allowSensitiveChanges)
            {
                profile.BankBSB = Normalize(dto.BankBsb);
                profile.BankACC = Normalize(dto.BankAccountNumber);
                profile.SuperannuationCompanyName = Normalize(dto.SuperannuationCompanyName);
                profile.SuperannuationCompanyCode = Normalize(dto.SuperannuationCompanyCode);
                profile.SuperannuationAccount = Normalize(dto.SuperannuationAccountNumber);
            }
            if (allowSensitiveChanges)
            {
                profile.Birthday = dto.Birthday?.Date;
            }
            profile.Gender = ParseGender(dto.Gender);
            if (dto.HasEmploymentType)
            {
                profile.EmployeeType = ParseEmployeeType(dto.EmploymentType);
            }
            if (allowLegacyImageUrls)
            {
                profile.AvatarUrl = Normalize(dto.AvatarUrl);
                if (string.IsNullOrWhiteSpace(profile.IdentityPhotoObjectKey))
                {
                    // 历史记录仍允许管理端维护 URL；托管私有图只能通过专用媒体接口更改。
                    profile.IdentityPhotoUrl = Normalize(dto.IdentityPhotoUrl);
                }
            }
            if (allowSensitiveChanges)
            {
                profile.IdentityType = Normalize(dto.IdentityType);
                profile.IdentityId = Normalize(dto.IdentityId);
            }
            profile.Address = Normalize(dto.Address);
            profile.UpdatedAt = now;
            profile.UpdatedBy = actor;
            profile.IsDeleted = false;

            if (isCreate)
            {
                profile.CreatedAt = now;
                profile.CreatedBy = actor;
            }
        }

        private EmployeeProfileDetailDto MapDetail(User user, EmployeeProfile? profile)
        {
            string? identityPhotoUrl = profile?.IdentityPhotoUrl;
            DateTime? identityPhotoUrlExpiresAt = null;
            if (!string.IsNullOrWhiteSpace(profile?.IdentityPhotoObjectKey) && _uploadService is not null)
            {
                var signed = _uploadService.GetSignedDownload(profile.IdentityPhotoObjectKey, 300);
                identityPhotoUrl = signed.Url;
                identityPhotoUrlExpiresAt = signed.ExpiresAtUtc;
            }
            return new EmployeeProfileDetailDto
            {
                EmployeeInfoId = profile?.EmployeeInfoId,
                UserGUID = user.UserGUID,
                Username = user.Username,
                Email = user.Email,
                FullName = user.FullName,
                Phone = profile?.Phone,
                BankBsb = profile?.BankBSB,
                BankAccountNumber = profile?.BankACC,
                SuperannuationCompanyName = profile?.SuperannuationCompanyName,
                SuperannuationCompanyCode = profile?.SuperannuationCompanyCode,
                SuperannuationAccountNumber = profile?.SuperannuationAccount,
                Birthday = profile?.Birthday,
                Gender = FormatGender(profile?.Gender),
                EmploymentType = FormatEmployeeType(profile?.EmployeeType),
                AvatarUrl = profile?.AvatarUrl,
                IdentityId = profile?.IdentityId,
                IdentityType = profile?.IdentityType,
                IdentityPhotoUrl = identityPhotoUrl,
                IdentityPhotoUrlExpiresAt = identityPhotoUrlExpiresAt,
                Address = profile?.Address,
                CreatedAt = profile?.CreatedAt,
                CreatedBy = profile?.CreatedBy,
                UpdatedAt = profile?.UpdatedAt,
                UpdatedBy = profile?.UpdatedBy,
                SensitiveRevision = profile?.SensitiveRevision ?? 0,
            };
        }

        private static string? Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static bool IsValidEmail(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 254)
            {
                return false;
            }

            try
            {
                var address = new MailAddress(value);
                return string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static string? MaskAccount(string? value)
        {
            var normalized = Normalize(value);
            return normalized is null
                ? null
                : $"****{normalized[^Math.Min(4, normalized.Length)..]}";
        }

        private static bool HasLegacySensitivePayload(EmployeeProfileUpsertDto dto) =>
            dto.BankBsb is not null
            || dto.BankAccountNumber is not null
            || dto.SuperannuationCompanyName is not null
            || dto.SuperannuationCompanyCode is not null
            || dto.SuperannuationAccountNumber is not null
            || dto.IdentityType is not null
            || dto.IdentityId is not null;

        private static bool HasLegacySensitiveChanges(
            EmployeeProfile? profile,
            EmployeeProfileUpsertDto dto
        ) =>
            (dto.BankBsb is not null
                && !string.Equals(profile?.BankBSB, Normalize(dto.BankBsb), StringComparison.Ordinal))
            || (dto.BankAccountNumber is not null
                && !string.Equals(profile?.BankACC, Normalize(dto.BankAccountNumber), StringComparison.Ordinal))
            || (dto.SuperannuationCompanyName is not null
                && !string.Equals(profile?.SuperannuationCompanyName, Normalize(dto.SuperannuationCompanyName), StringComparison.Ordinal))
            || (dto.SuperannuationCompanyCode is not null
                && !string.Equals(profile?.SuperannuationCompanyCode, Normalize(dto.SuperannuationCompanyCode), StringComparison.Ordinal))
            || (dto.SuperannuationAccountNumber is not null
                && !string.Equals(profile?.SuperannuationAccount, Normalize(dto.SuperannuationAccountNumber), StringComparison.Ordinal))
            || (dto.IdentityType is not null
                && !string.Equals(profile?.IdentityType, Normalize(dto.IdentityType), StringComparison.Ordinal))
            || (dto.IdentityId is not null
                && !string.Equals(profile?.IdentityId, Normalize(dto.IdentityId), StringComparison.Ordinal));

        // 管理员直改生日免审，但同样递增 revision 并作废员工的待审申请，避免旧申请审批后覆盖新生日。
        private static bool HasSensitiveChanges(EmployeeProfile? profile, EmployeeProfileUpsertDto dto) =>
            profile?.Birthday?.Date != dto.Birthday?.Date
            || !string.Equals(profile?.BankBSB, Normalize(dto.BankBsb), StringComparison.Ordinal)
            || !string.Equals(profile?.BankACC, Normalize(dto.BankAccountNumber), StringComparison.Ordinal)
            || !string.Equals(profile?.SuperannuationCompanyName, Normalize(dto.SuperannuationCompanyName), StringComparison.Ordinal)
            || !string.Equals(profile?.SuperannuationCompanyCode, Normalize(dto.SuperannuationCompanyCode), StringComparison.Ordinal)
            || !string.Equals(profile?.SuperannuationAccount, Normalize(dto.SuperannuationAccountNumber), StringComparison.Ordinal)
            || !string.Equals(profile?.IdentityType, Normalize(dto.IdentityType), StringComparison.Ordinal)
            || !string.Equals(profile?.IdentityId, Normalize(dto.IdentityId), StringComparison.Ordinal)
            || (string.IsNullOrWhiteSpace(profile?.IdentityPhotoObjectKey)
                && !string.Equals(profile?.IdentityPhotoUrl, Normalize(dto.IdentityPhotoUrl), StringComparison.Ordinal));

        private static EmployeeProfileSensitiveChangeUpsertDto BuildLegacySensitiveSnapshot(
            EmployeeProfile? profile,
            EmployeeProfileUpsertDto dto
        ) => new()
        {
            BankBsb = dto.BankBsb ?? profile?.BankBSB,
            BankAccountNumber = dto.BankAccountNumber ?? profile?.BankACC,
            SuperannuationCompanyName = dto.SuperannuationCompanyName ?? profile?.SuperannuationCompanyName,
            SuperannuationCompanyCode = dto.SuperannuationCompanyCode ?? profile?.SuperannuationCompanyCode,
            SuperannuationAccountNumber = dto.SuperannuationAccountNumber ?? profile?.SuperannuationAccount,
            IdentityType = dto.IdentityType ?? profile?.IdentityType,
            IdentityId = dto.IdentityId ?? profile?.IdentityId,
        };

        private static EmployeeGender? ParseGender(string? value)
        {
            return Normalize(value)?.ToLowerInvariant() switch
            {
                null => null,
                "" => null,
                "unknown" => EmployeeGender.Unknown,
                "male" => EmployeeGender.Male,
                "female" => EmployeeGender.Female,
                "other" => EmployeeGender.Other,
                _ => null,
            };
        }

        private static EmployeeType? ParseEmployeeType(string? value)
        {
            return Normalize(value)?.ToLowerInvariant() switch
            {
                null => null,
                "" => null,
                "fulltime" => EmployeeType.FullTime,
                "full_time" => EmployeeType.FullTime,
                "full-time" => EmployeeType.FullTime,
                "parttime" => EmployeeType.PartTime,
                "part_time" => EmployeeType.PartTime,
                "part-time" => EmployeeType.PartTime,
                "casual" => EmployeeType.Temporary,
                "temporary" => EmployeeType.Temporary,
                _ => null,
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

        private static string? FormatEmployeeType(EmployeeType? value)
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
