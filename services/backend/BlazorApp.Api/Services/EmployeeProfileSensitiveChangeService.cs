using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using System.Data;
using System.Security.Claims;
using System.Text.Json;

namespace BlazorApp.Api.Services;

internal sealed record EmployeeProfileObjectCleanupTarget(string Kind, string ObjectKey);

internal sealed record EmployeeProfileDataRemovalPlan(
    IReadOnlyList<string> SupersededPendingKeys,
    IReadOnlyList<EmployeeProfileObjectCleanupTarget> DetachedProfileObjects
);

public sealed class EmployeeProfileSensitiveChangeService
{
    public const string VersionConflictCode = "EMPLOYEE_PROFILE_SENSITIVE_VERSION_CONFLICT";
    public const string ReviewScopeForbiddenCode =
        "EMPLOYEE_PROFILE_SENSITIVE_REVIEW_SCOPE_FORBIDDEN";

    private static readonly string[] StoreManagerRoleAliases = ["StoreManager", "店长", "经理"];
    private static readonly string[] ProtectedTargetRoleAliases =
    [
        .. Permissions.SuperAdminRoleNames,
        "WarehouseManager", "仓库经理", "StoreManager", "店长", "经理",
    ];

    private const string BirthdayFieldName = "birthday";

    private static readonly string[] SensitiveFieldNames =
    [
        BirthdayFieldName,
        "bankBsb",
        "bankAccountNumber",
        "superannuationCompanyName",
        "superannuationCompanyCode",
        "superannuationAccountNumber",
        "identityType",
        "identityId",
        "identityPhotoUrl",
    ];

    private readonly SqlSugarContext _context;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<EmployeeProfileSensitiveChangeService> _logger;
    private readonly ICurrentUserManageableStoreScopeService _manageableStoreScope;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TencentCloudUploadService? _storage;

    public EmployeeProfileSensitiveChangeService(
        SqlSugarContext context,
        ICurrentUserService currentUser,
        ILogger<EmployeeProfileSensitiveChangeService> logger,
        ICurrentUserManageableStoreScopeService manageableStoreScope,
        IHttpContextAccessor httpContextAccessor,
        TencentCloudUploadService? storage = null
    )
    {
        _context = context;
        _currentUser = currentUser;
        _logger = logger;
        _manageableStoreScope = manageableStoreScope;
        _httpContextAccessor = httpContextAccessor;
        _storage = storage;
    }

    public async Task<ApiResponse<EmployeeProfileSensitiveChangeDetailDto?>> GetSelfAsync()
    {
        var userGuid = _currentUser.GetCurrentUserGuid();
        if (string.IsNullOrWhiteSpace(userGuid))
        {
            return ApiResponse<EmployeeProfileSensitiveChangeDetailDto?>.Error(
                "未找到当前用户",
                "CURRENT_USER_NOT_FOUND"
            );
        }
        var request = await _context.Db.Queryable<EmployeeProfileSensitiveChangeRequest>()
            .Where(item => item.UserGUID == userGuid)
            .OrderBy(item => item.SubmittedAt, SqlSugar.OrderByType.Desc)
            .FirstAsync();
        var profile = request is null
            ? null
            : await _context.Db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
        return ApiResponse<EmployeeProfileSensitiveChangeDetailDto?>.OK(
            request is null ? null : MapDetail(request, profile)
        );
    }

    public async Task<ApiResponse<EmployeeProfileSensitiveChangeDetailDto>> UpsertSelfAsync(
        EmployeeProfileSensitiveChangeUpsertDto dto
    )
    {
        var userGuid = _currentUser.GetCurrentUserGuid();
        if (string.IsNullOrWhiteSpace(userGuid))
        {
            return ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.Error(
                "未找到当前用户",
                "CURRENT_USER_NOT_FOUND"
            );
        }
        return await ReplacePendingAsync(userGuid, dto, identityPhotoObjectKey: null, preservePendingPhoto: true);
    }

    /// <summary>证件图 complete/delete 只更新待审快照，正式资料保持不变。</summary>
    public async Task<ApiResponse<EmployeeProfileSensitiveChangeDetailDto>> ReplacePendingIdentityPhotoAsync(
        string identityPhotoObjectKey
    )
    {
        var userGuid = _currentUser.GetCurrentUserGuid();
        if (string.IsNullOrWhiteSpace(userGuid))
        {
            return ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.Error(
                "未找到当前用户",
                "CURRENT_USER_NOT_FOUND"
            );
        }
        var snapshot = await BuildCurrentSnapshotAsync(userGuid);
        return await ReplacePendingAsync(
            userGuid,
            snapshot,
            Normalize(identityPhotoObjectKey),
            preservePendingPhoto: false
        );
    }

    public async Task<ApiResponse<EmployeeProfileSensitiveChangeDetailDto>> DeletePendingIdentityPhotoAsync()
    {
        var userGuid = _currentUser.GetCurrentUserGuid();
        if (string.IsNullOrWhiteSpace(userGuid))
        {
            return ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.Error(
                "未找到当前用户",
                "CURRENT_USER_NOT_FOUND"
            );
        }
        var snapshot = await BuildCurrentSnapshotAsync(userGuid);
        return await ReplacePendingAsync(userGuid, snapshot, identityPhotoObjectKey: null, preservePendingPhoto: false);
    }

    /// <summary>
    /// 员工撤回本人待审申请。与驳回共用同一把媒体锁、Serializable 事务和 Status==Pending 条件更新，
    /// 撤回后不存在 Pending，员工可以重新提交。
    /// </summary>
    public async Task<ApiResponse<EmployeeProfileSensitiveChangeDetailDto>> WithdrawSelfAsync(
        EmployeeProfileSensitiveWithdrawDto? dto = null
    )
    {
        var userGuid = _currentUser.GetCurrentUserGuid();
        if (string.IsNullOrWhiteSpace(userGuid))
        {
            return ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.Error(
                "未找到当前用户",
                "CURRENT_USER_NOT_FOUND"
            );
        }
        var db = _context.Db;
        var actor = _currentUser.GetCurrentUsername();
        var now = DateTime.UtcNow;
        EmployeeProfileSensitiveChangeRequest? request;
        EmployeeProfile? profile;
        await using (await EmployeeProfileMediaLock.AcquireAsync(
            db,
            userGuid,
            "sensitive-change",
            _logger
        ))
        {
            await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
            try
            {
                // 关键逻辑：锁内重读本人当前待审申请，不能撤回取得锁之前读到的旧快照。
                request = await db.Queryable<EmployeeProfileSensitiveChangeRequest>()
                    .FirstAsync(item => item.UserGUID == userGuid
                        && item.Status == EmployeeProfileSensitiveChangeStatus.Pending);
                if (request is null
                    || (dto?.RequestId is int expectedId && expectedId != request.RequestId))
                {
                    // 没有待审申请，或页面上的申请已被审核/覆盖：统一按「申请已处理」返回，交由客户端刷新。
                    await db.Ado.RollbackTranAsync();
                    return RequestNotPending();
                }
                profile = await db.Queryable<EmployeeProfile>()
                    .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
                var changed = await db.Updateable<EmployeeProfileSensitiveChangeRequest>()
                    .SetColumns(item => new EmployeeProfileSensitiveChangeRequest
                    {
                        Status = EmployeeProfileSensitiveChangeStatus.Withdrawn,
                        // 撤回复用审核时间/操作人两列记录终结时间与撤回人，不写审核原因。
                        ReviewedAt = now,
                        ReviewedBy = actor,
                    })
                    .Where(item => item.RequestId == request.RequestId
                        && item.UserGUID == userGuid
                        && item.Status == EmployeeProfileSensitiveChangeStatus.Pending)
                    .ExecuteCommandAsync();
                if (changed != 1)
                {
                    await db.Ado.RollbackTranAsync();
                    return RequestNotPending();
                }
                // 待审证件照随申请终结进入清理队列；提交后的即时删除失败时由后台 worker 重试。
                await ScheduleTicketCleanupAsync(
                    request.RequestId,
                    request.IdentityPhotoObjectKey,
                    userGuid
                );
                await db.Ado.CommitTranAsync();
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }
        }
        request.Status = EmployeeProfileSensitiveChangeStatus.Withdrawn;
        request.ReviewedAt = now;
        request.ReviewedBy = actor;
        await CleanupObjectAsync(request.IdentityPhotoObjectKey, null, userGuid, "撤回待审证件照", request.RequestId);
        return ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.OK(MapDetail(request, profile), "申请已撤回");
    }

    /// <summary>本人申请历史，按提交时间倒序；只返回状态、字段标识与审核信息，不返回任何敏感值。</summary>
    public async Task<ApiResponse<List<EmployeeProfileSensitiveChangeHistoryItemDto>>> GetSelfHistoryAsync(int take)
    {
        var userGuid = _currentUser.GetCurrentUserGuid();
        if (string.IsNullOrWhiteSpace(userGuid))
        {
            return ApiResponse<List<EmployeeProfileSensitiveChangeHistoryItemDto>>.Error(
                "未找到当前用户",
                "CURRENT_USER_NOT_FOUND"
            );
        }
        var limit = Math.Clamp(take, 1, 50);
        var requests = await _context.Db.Queryable<EmployeeProfileSensitiveChangeRequest>()
            .Where(item => item.UserGUID == userGuid)
            .OrderBy(item => item.SubmittedAt, SqlSugar.OrderByType.Desc)
            .OrderBy(item => item.RequestId, SqlSugar.OrderByType.Desc)
            .Take(limit)
            .ToListAsync();
        // 正式资料只用于兼容缺少字段快照的历史申请，绝不进入响应。
        var profile = requests.Any(item => string.IsNullOrWhiteSpace(item.ChangedFieldsJson))
            ? await _context.Db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted)
            : null;
        return ApiResponse<List<EmployeeProfileSensitiveChangeHistoryItemDto>>.OK(
            requests.Select(item => new EmployeeProfileSensitiveChangeHistoryItemDto
            {
                RequestId = item.RequestId,
                Status = FormatStatus(item.Status),
                ChangedFields = ResolveChangedFields(profile, item),
                SubmittedAt = item.SubmittedAt,
                // 被覆盖/作废的申请没有审核时间，用作废时间作为终结时间供时间线展示。
                ReviewedAt = item.ReviewedAt ?? item.SupersededAt,
                ReviewReason = item.ReviewReason,
            }).ToList()
        );
    }

    public async Task<ApiResponse<PagedResult<EmployeeProfileSensitiveChangeSummaryDto>>> GetAdminListAsync(
        EmployeeProfileSensitiveChangeQueryDto query
    ) => await GetReviewListAsync(query);

    public async Task<ApiResponse<PagedResult<EmployeeProfileSensitiveChangeSummaryDto>>> GetReviewListAsync(
        EmployeeProfileSensitiveChangeQueryDto query
    )
    {
        var access = await GetReviewAccessAsync();
        if (access is null)
        {
            return ReviewScopeForbidden<PagedResult<EmployeeProfileSensitiveChangeSummaryDto>>();
        }
        var db = _context.Db;
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        // Processed 表示「已处理」分段：除 Pending 外的全部终态（批准/驳回/作废/撤回）。
        var processedOnly = string.Equals(query.Status?.Trim(), "Processed", StringComparison.OrdinalIgnoreCase);
        var status = processedOnly ? null : ParseStatus(query.Status);
        var userGuidFilter = Normalize(query.UserGuid);
        var source = db.Queryable<EmployeeProfileSensitiveChangeRequest, User, EmployeeProfile>(
            (request, user, profile) => new object[]
            {
                SqlSugar.JoinType.Left,
                request.UserGUID == user.UserGUID,
                SqlSugar.JoinType.Left,
                request.UserGUID == profile.UserGUID && !profile.IsDeleted,
            }
        );
        if (!access.IsAdmin)
        {
            var protectedTargetRoleAliases = ProtectedTargetRoleAliases;
            // 关键逻辑：范围、自审和受保护角色全部在主查询分页前过滤，禁止把整店员工 GUID 拉入内存拼接大 IN。
            source = source.Where((request, user, profile) =>
                request.UserGUID != access.UserGuid
                && SqlSugar.SqlFunc.Subqueryable<UserStore>()
                    .Where(userStore =>
                        userStore.UserGUID == request.UserGUID
                        && !userStore.IsDeleted
                        && access.StoreGuids.Contains(userStore.StoreGUID))
                    .Any()
                && !SqlSugar.SqlFunc.Subqueryable<UserRole>()
                    .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
                    .Where((userRole, role) =>
                        userRole.UserGUID == request.UserGUID
                        && !userRole.IsDeleted
                        && !role.IsDeleted
                        && protectedTargetRoleAliases.Contains(role.RoleName))
                    .Any()
            );
        }
        if (status.HasValue)
        {
            source = source.Where((request, user, profile) => request.Status == status.Value);
        }
        else if (processedOnly)
        {
            source = source.Where((request, user, profile) =>
                request.Status != EmployeeProfileSensitiveChangeStatus.Pending);
        }
        if (userGuidFilter is not null)
        {
            // 关键逻辑：按员工过滤叠加在上方审核范围之后，超范围员工只会得到空列表，不泄露是否存在申请。
            source = source.Where((request, user, profile) => request.UserGUID == userGuidFilter);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            source = source.Where((request, user, profile) =>
                request.UserGUID.Contains(search)
                || user.Username.Contains(search)
                || (user.FullName != null && user.FullName.Contains(search))
            );
        }
        var total = await source.CountAsync();
        var rows = await source
            .OrderBy((request, user, profile) => request.SubmittedAt, SqlSugar.OrderByType.Desc)
            .Select((request, user, profile) => new { Request = request, user.Username, Profile = profile })
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        var storeLabels = await GetStoreLabelsAsync(
            rows.Select(row => row.Request.UserGUID).ToList(),
            access.IsAdmin ? null : access.StoreGuids
        );
        return ApiResponse<PagedResult<EmployeeProfileSensitiveChangeSummaryDto>>.OK(new()
        {
            Items = rows.Select(row =>
            {
                storeLabels.TryGetValue(row.Request.UserGUID, out var labels);
                return MapSummary(row.Request, row.Username, row.Profile, labels);
            }).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    public async Task<ApiResponse<EmployeeProfileSensitiveChangeDetailDto>> GetAdminDetailAsync(int requestId)
        => await GetReviewDetailAsync(requestId);

    public async Task<ApiResponse<EmployeeProfileSensitiveChangeDetailDto>> GetReviewDetailAsync(int requestId)
    {
        var access = await GetReviewAccessAsync();
        if (access is null)
        {
            return ReviewScopeForbidden<EmployeeProfileSensitiveChangeDetailDto>();
        }
        var request = await _context.Db.Queryable<EmployeeProfileSensitiveChangeRequest>()
            .FirstAsync(item => item.RequestId == requestId);
        if (request is null)
        {
            return ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.Error("申请不存在", "REQUEST_NOT_FOUND");
        }
        if (!await CanReviewTargetAsync(access, request.UserGUID))
        {
            // 对有审核范围但无权查看的目标统一伪装为不存在，避免通过 requestId 枚举员工。
            return RequestNotFound<EmployeeProfileSensitiveChangeDetailDto>();
        }
        var profile = await _context.Db.Queryable<EmployeeProfile>()
            .FirstAsync(item => item.UserGUID == request!.UserGUID && !item.IsDeleted);
        var username = await _context.Db.Queryable<User>()
            .Where(item => item.UserGUID == request.UserGUID)
            .Select(item => item.Username)
            .FirstAsync();
        var labels = (await GetStoreLabelsAsync(
            [request!.UserGUID],
            access.IsAdmin ? null : access.StoreGuids
        )).GetValueOrDefault(request.UserGUID);
        return ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.OK(
            MapDetail(request, profile, username, labels)
        );
    }

    public async Task<ApiResponse<EmployeeProfileSensitiveChangeDetailDto>> ApproveAsync(
        int requestId,
        EmployeeProfileSensitiveReviewDto dto
    )
    {
        var access = await GetReviewAccessAsync();
        if (access is null)
        {
            return ReviewScopeForbidden<EmployeeProfileSensitiveChangeDetailDto>();
        }
        var db = _context.Db;
        var userGuid = await db.Queryable<EmployeeProfileSensitiveChangeRequest>()
            .Where(item => item.RequestId == requestId)
            .Select(item => item.UserGUID)
            .FirstAsync();
        if (string.IsNullOrWhiteSpace(userGuid))
        {
            return ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.Error("申请不存在", "REQUEST_NOT_FOUND");
        }

        var actor = _currentUser.GetCurrentUsername();
        var now = DateTime.UtcNow;
        EmployeeProfileSensitiveChangeRequest request;
        EmployeeProfile profile;
        string? oldPhoto;
        await using (await EmployeeProfileMediaLock.AcquireAsync(
            db,
            userGuid,
            "sensitive-change",
            _logger
        ))
        {
            // 关键逻辑：审核范围、目标分店和受保护角色校验必须与批准写入处于不可穿透的原子区。
            await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
            try
            {
                // 关键逻辑：锁内事务重新读取申请与正式版本，不能使用取得锁之前的旧快照做审批。
                request = await db.Queryable<EmployeeProfileSensitiveChangeRequest>()
                    .FirstAsync(item => item.RequestId == requestId);
                if (request is null)
                {
                    await db.Ado.RollbackTranAsync();
                    return ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.Error(
                        "申请不存在",
                        "REQUEST_NOT_FOUND"
                    );
                }
                // 关键逻辑：等待媒体锁期间审核者主分店可能被撤销，锁内事务必须重新获取范围。
                var lockedAccess = await GetReviewAccessAsync();
                if (lockedAccess is null)
                {
                    await db.Ado.RollbackTranAsync();
                    return ReviewScopeForbidden<EmployeeProfileSensitiveChangeDetailDto>();
                }
                if (!await CanReviewTargetAsync(lockedAccess, request.UserGUID))
                {
                    await db.Ado.RollbackTranAsync();
                    return RequestNotFound<EmployeeProfileSensitiveChangeDetailDto>();
                }
                if (request.Status != EmployeeProfileSensitiveChangeStatus.Pending)
                {
                    await db.Ado.RollbackTranAsync();
                    return RequestNotPending();
                }
                profile = await db.Queryable<EmployeeProfile>()
                    .FirstAsync(item => item.UserGUID == request.UserGUID && !item.IsDeleted);
                if (profile is null || profile.SensitiveRevision != request.BaseSensitiveRevision)
                {
                    await db.Ado.RollbackTranAsync();
                    return VersionConflict();
                }

                oldPhoto = profile.IdentityPhotoObjectKey;
                var photoChanged = request.RemoveIdentityPhoto
                    ? HasFormalIdentityPhoto(profile)
                    : !NormalizedEquals(
                        profile.IdentityPhotoObjectKey,
                        request.IdentityPhotoObjectKey
                    );
                // 关键逻辑：只有申请明确携带生日变更才写回生日；加列前的历史申请生日为空，不能清空正式生日。
                var birthdayIncluded = RequestCarriesBirthday(profile, request);
                var birthdayChanged = birthdayIncluded && !DateEquals(profile.Birthday, request.Birthday);
                var sensitiveValuesChanged = HasSensitiveValueChanges(profile, request)
                    || birthdayChanged
                    || photoChanged;
                // 关键逻辑：revision 条件更新是防止锁外数据库写入静默覆盖资料的最终防线。
                int updated;
                if (sensitiveValuesChanged)
                {
                    var profileUpdate = db.Updateable<EmployeeProfile>()
                        .SetColumns(item => item.BankBSB == request.BankBsb)
                        .SetColumns(item => item.BankACC == request.BankAccountNumber)
                        .SetColumns(item => item.SuperannuationCompanyName == request.SuperannuationCompanyName)
                        .SetColumns(item => item.SuperannuationCompanyCode == request.SuperannuationCompanyCode)
                        .SetColumns(item => item.SuperannuationAccount == request.SuperannuationAccountNumber)
                        .SetColumns(item => item.IdentityType == request.IdentityType)
                        .SetColumns(item => item.IdentityId == request.IdentityId)
                        .SetColumns(item => item.SensitiveRevision == request.BaseSensitiveRevision + 1)
                        .SetColumns(item => item.UpdatedAt == now)
                        .SetColumns(item => item.UpdatedBy == actor);
                    if (birthdayIncluded)
                    {
                        profileUpdate = profileUpdate.SetColumns(item => item.Birthday == request.Birthday);
                    }
                    if (photoChanged)
                    {
                        // 证件照上传或显式删除时同步清理旧式 URL，普通字段审批不得误清。
                        profileUpdate = profileUpdate
                            .SetColumns(item => item.IdentityPhotoObjectKey == (
                                request.RemoveIdentityPhoto ? null : request.IdentityPhotoObjectKey
                            ))
                            .SetColumns(item => item.IdentityPhotoUrl == null);
                    }
                    updated = await profileUpdate
                        .Where(item => item.EmployeeInfoId == profile.EmployeeInfoId
                            && item.SensitiveRevision == request.BaseSensitiveRevision)
                        .ExecuteCommandAsync();
                }
                else
                {
                    // 等值申请仍执行 revision CAS，但不制造虚假的正式资料版本。
                    updated = await db.Updateable<EmployeeProfile>()
                        .SetColumns(item => item.SensitiveRevision == request.BaseSensitiveRevision)
                        .Where(item => item.EmployeeInfoId == profile.EmployeeInfoId
                            && item.SensitiveRevision == request.BaseSensitiveRevision)
                        .ExecuteCommandAsync();
                }
                if (updated != 1)
                {
                    throw new SensitiveRevisionConflictException();
                }
                var reviewed = await db.Updateable<EmployeeProfileSensitiveChangeRequest>()
                    .SetColumns(item => new EmployeeProfileSensitiveChangeRequest
                    {
                        Status = EmployeeProfileSensitiveChangeStatus.Approved,
                        ReviewedAt = now,
                        ReviewedBy = actor,
                        ReviewReason = Normalize(dto.Reason),
                    })
                    .Where(item => item.RequestId == requestId
                        && item.Status == EmployeeProfileSensitiveChangeStatus.Pending)
                    .ExecuteCommandAsync();
                if (reviewed != 1)
                {
                    await db.Ado.RollbackTranAsync();
                    return RequestNotPending();
                }
                if (!string.IsNullOrWhiteSpace(oldPhoto)
                    && !string.Equals(oldPhoto, request.IdentityPhotoObjectKey, StringComparison.Ordinal))
                {
                    await ScheduleTicketCleanupAsync(requestId, oldPhoto, request.UserGUID);
                }
                // 审批写入后在同一事务与媒体锁内重读，响应快照不得继续引用审批前的正式资料。
                profile = await db.Queryable<EmployeeProfile>()
                    .FirstAsync(item => item.UserGUID == request.UserGUID && !item.IsDeleted);
                await db.Ado.CommitTranAsync();
            }
            catch (SensitiveRevisionConflictException)
            {
                await db.Ado.RollbackTranAsync();
                return VersionConflict();
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }
        }
        request.Status = EmployeeProfileSensitiveChangeStatus.Approved;
        request.ReviewedAt = now;
        request.ReviewedBy = actor;
        request.ReviewReason = Normalize(dto.Reason);
        await CleanupObjectAsync(oldPhoto, request.IdentityPhotoObjectKey, request.UserGUID, "批准后旧正式证件照", requestId);
        return ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.OK(MapDetail(request, profile), "申请已批准");
    }

    public async Task<ApiResponse<EmployeeProfileSensitiveChangeDetailDto>> RejectAsync(
        int requestId,
        EmployeeProfileSensitiveRejectDto dto
    )
    {
        if (string.IsNullOrWhiteSpace(dto.Reason))
        {
            return ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.Error("拒绝原因必填", "VALIDATION_ERROR");
        }
        var access = await GetReviewAccessAsync();
        if (access is null)
        {
            return ReviewScopeForbidden<EmployeeProfileSensitiveChangeDetailDto>();
        }
        var db = _context.Db;
        var userGuid = await db.Queryable<EmployeeProfileSensitiveChangeRequest>()
            .Where(item => item.RequestId == requestId)
            .Select(item => item.UserGUID)
            .FirstAsync();
        if (string.IsNullOrWhiteSpace(userGuid))
        {
            return ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.Error("申请不存在", "REQUEST_NOT_FOUND");
        }
        var actor = _currentUser.GetCurrentUsername();
        var now = DateTime.UtcNow;
        EmployeeProfileSensitiveChangeRequest request;
        EmployeeProfile? profile;
        await using (await EmployeeProfileMediaLock.AcquireAsync(
            db,
            userGuid,
            "sensitive-change",
            _logger
        ))
        {
            // 关键逻辑：驳回也要锁定授权读取窗口，避免目标移店或提权后仍被旧权限处理。
            await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
            try
            {
                // 关键逻辑：拒绝也必须在共用锁内重读状态，不能处理已被覆盖的新旧申请快照。
                request = await db.Queryable<EmployeeProfileSensitiveChangeRequest>()
                    .FirstAsync(item => item.RequestId == requestId);
                if (request is null)
                {
                    await db.Ado.RollbackTranAsync();
                    return ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.Error(
                        "申请不存在",
                        "REQUEST_NOT_FOUND"
                    );
                }
                // 驳回同样在锁内重取最新审核范围，不能复用锁外缓存的 StoreGuids。
                var lockedAccess = await GetReviewAccessAsync();
                if (lockedAccess is null)
                {
                    await db.Ado.RollbackTranAsync();
                    return ReviewScopeForbidden<EmployeeProfileSensitiveChangeDetailDto>();
                }
                if (!await CanReviewTargetAsync(lockedAccess, request.UserGUID))
                {
                    await db.Ado.RollbackTranAsync();
                    return RequestNotFound<EmployeeProfileSensitiveChangeDetailDto>();
                }
                if (request.Status != EmployeeProfileSensitiveChangeStatus.Pending)
                {
                    await db.Ado.RollbackTranAsync();
                    return RequestNotPending();
                }
                profile = await db.Queryable<EmployeeProfile>()
                    .FirstAsync(item => item.UserGUID == request.UserGUID && !item.IsDeleted);
                var changed = await db.Updateable<EmployeeProfileSensitiveChangeRequest>()
                    .SetColumns(item => new EmployeeProfileSensitiveChangeRequest
                    {
                        Status = EmployeeProfileSensitiveChangeStatus.Rejected,
                        ReviewedAt = now,
                        ReviewedBy = actor,
                        ReviewReason = dto.Reason.Trim(),
                    })
                    .Where(item => item.RequestId == requestId
                        && item.Status == EmployeeProfileSensitiveChangeStatus.Pending)
                    .ExecuteCommandAsync();
                if (changed != 1)
                {
                    await db.Ado.RollbackTranAsync();
                    return RequestNotPending();
                }
                await ScheduleTicketCleanupAsync(
                    requestId,
                    request.IdentityPhotoObjectKey,
                    request.UserGUID
                );
                await db.Ado.CommitTranAsync();
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }
        }
        request.Status = EmployeeProfileSensitiveChangeStatus.Rejected;
        request.ReviewedAt = now;
        request.ReviewedBy = actor;
        request.ReviewReason = dto.Reason.Trim();
        await CleanupObjectAsync(request.IdentityPhotoObjectKey, null, request.UserGUID, "驳回待审证件照", requestId);
        return ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.OK(MapDetail(request, profile), "申请已拒绝");
    }

    /// <summary>管理员直接改正式资料后在同一数据库事务内调用，终结所有待审申请。</summary>
    public async Task<List<string>> SupersedePendingWithinTransactionAsync(string userGuid, string actor)
    {
        var pending = await _context.Db.Queryable<EmployeeProfileSensitiveChangeRequest>()
            .Where(item => item.UserGUID == userGuid
                && item.Status == EmployeeProfileSensitiveChangeStatus.Pending)
            .ToListAsync();
        if (pending.Count == 0)
        {
            return new();
        }
        var now = DateTime.UtcNow;
        await _context.Db.Updateable<EmployeeProfileSensitiveChangeRequest>()
            .SetColumns(item => new EmployeeProfileSensitiveChangeRequest
            {
                Status = EmployeeProfileSensitiveChangeStatus.Superseded,
                SupersededAt = now,
                SupersededBy = actor,
            })
            .Where(item => item.UserGUID == userGuid
                && item.Status == EmployeeProfileSensitiveChangeStatus.Pending)
            .ExecuteCommandAsync();
        foreach (var item in pending
            .Where(item => !string.IsNullOrWhiteSpace(item.IdentityPhotoObjectKey))
            .GroupBy(item => item.IdentityPhotoObjectKey!, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.SubmittedAt).First()))
        {
            await ScheduleTicketCleanupAsync(
                item.RequestId,
                item.IdentityPhotoObjectKey,
                item.UserGUID
            );
        }
        return pending.Select(item => item.IdentityPhotoObjectKey)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task CleanupSupersededObjectsAsync(string userGuid, IEnumerable<string> keys)
    {
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            var request = await _context.Db.Queryable<EmployeeProfileSensitiveChangeRequest>()
                .Where(item => item.UserGUID == userGuid
                    && item.Status == EmployeeProfileSensitiveChangeStatus.Superseded
                    && item.IdentityPhotoObjectKey == key)
                .OrderBy(item => item.SupersededAt, SqlSugar.OrderByType.Desc)
                .FirstAsync();
            await CleanupScheduledObjectBestEffortAsync(
                userGuid,
                new EmployeeProfileObjectCleanupTarget("identity", key),
                request?.RequestId,
                "管理员直改后待审证件照"
            );
        }
    }

    internal async Task<EmployeeProfileDataRemovalPlan> PrepareProfileDataRemovalWithinTransactionAsync(
        string userGuid,
        EmployeeProfile? profile,
        string actor
    )
    {
        var pendingKeys = await SupersedePendingWithinTransactionAsync(userGuid, actor);
        var seen = new HashSet<EmployeeProfileObjectCleanupTarget>();
        foreach (var key in pendingKeys)
        {
            seen.Add(new EmployeeProfileObjectCleanupTarget("identity", key));
        }

        var detached = new List<EmployeeProfileObjectCleanupTarget>();
        if (profile is not null)
        {
            AddDetachedTarget("identity", profile.IdentityPhotoObjectKey);
            if (_storage is not null
                && _storage.TryGetPublicObjectKey(profile.AvatarUrl, out var avatarObjectKey))
            {
                AddDetachedTarget("avatar", avatarObjectKey);
            }
        }

        foreach (var target in detached)
        {
            await ScheduleObjectCleanupWithinTransactionAsync(userGuid, target);
        }

        return new EmployeeProfileDataRemovalPlan(pendingKeys, detached);

        void AddDetachedTarget(string kind, string? objectKey)
        {
            if (string.IsNullOrWhiteSpace(objectKey)
                || !EmployeeProfileImageRules.OwnsObjectKey(objectKey, userGuid, kind))
            {
                return;
            }

            var target = new EmployeeProfileObjectCleanupTarget(kind, objectKey);
            if (seen.Add(target))
            {
                detached.Add(target);
            }
        }
    }

    internal async Task CleanupProfileDataRemovalAsync(
        string userGuid,
        EmployeeProfileDataRemovalPlan plan
    )
    {
        try
        {
            await CleanupSupersededObjectsAsync(userGuid, plan.SupersededPendingKeys);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "提交后清理已作废员工敏感资料对象失败。UserGUID: {UserGUID}", userGuid);
        }

        foreach (var target in plan.DetachedProfileObjects.Distinct())
        {
            await CleanupScheduledObjectBestEffortAsync(
                userGuid,
                target,
                requestId: null,
                "恢复软删除员工资料后的历史图片"
            );
        }
    }

    private async Task<ApiResponse<EmployeeProfileSensitiveChangeDetailDto>> ReplacePendingAsync(
        string userGuid,
        EmployeeProfileSensitiveChangeUpsertDto dto,
        string? identityPhotoObjectKey,
        bool preservePendingPhoto
    )
    {
        (
            ApiResponse<EmployeeProfileSensitiveChangeDetailDto> Response,
            EmployeeProfileDataRemovalPlan? RemovalPlan
        ) result;
        {
            await using var sensitiveLock = await EmployeeProfileMediaLock.AcquireAsync(
                _context.Db,
                userGuid,
                "sensitive-change",
                _logger
            );
            await using var lifecycleLock = await EmployeeProfileMediaLock
                .AcquireProfileLifecycleAsync(_context.Db, userGuid, _logger);
            result = await ReplacePendingLockedAsync(
                userGuid,
                dto,
                identityPhotoObjectKey,
                preservePendingPhoto
            );
        }

        if (result.RemovalPlan is not null)
        {
            await CleanupProfileDataRemovalAsync(userGuid, result.RemovalPlan);
        }
        return result.Response;
    }

    private async Task<(
        ApiResponse<EmployeeProfileSensitiveChangeDetailDto> Response,
        EmployeeProfileDataRemovalPlan? RemovalPlan
    )> ReplacePendingLockedAsync(
        string userGuid,
        EmployeeProfileSensitiveChangeUpsertDto dto,
        string? identityPhotoObjectKey,
        bool preservePendingPhoto
    )
    {
        var db = _context.Db;
        var actor = _currentUser.GetCurrentUsername();
        var now = DateTime.UtcNow;
        EmployeeProfileDataRemovalPlan? removalPlan = null;
        EmployeeProfile? profile = null;
        EmployeeProfileSensitiveChangeRequest? old = null;
        EmployeeProfileSensitiveChangeRequest? request = null;
        string? retainedPhoto = null;
        await db.Ado.BeginTranAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            // 生命周期锁只能串行化参与者；事务内仍须确认用户未被先行物理删除，避免创建孤立资料和申请。
            var user = await db.Queryable<User>()
                .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
            if (user is null)
            {
                await db.Ado.RollbackTranAsync();
                return (
                    ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.Error(
                        "用户不存在",
                        "USER_NOT_FOUND"
                    ),
                    null
                );
            }

            profile = await db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
            var currentSensitiveRevision = profile?.SensitiveRevision ?? 0;
            if (dto.ExpectedSensitiveRevision.HasValue
                && dto.ExpectedSensitiveRevision.Value != currentSensitiveRevision)
            {
                // 员工敏感表单也以打开时 revision 做 CAS；冲突发生在任何申请写入之前。
                await db.Ado.RollbackTranAsync();
                return (VersionConflict(), null);
            }

            if (profile is null)
            {
                (profile, removalPlan) = await EnsureProfileWithinTransactionAsync(
                    userGuid,
                    actor,
                    now
                );
            }
            old = await db.Queryable<EmployeeProfileSensitiveChangeRequest>()
                .FirstAsync(item => item.UserGUID == userGuid
                    && item.Status == EmployeeProfileSensitiveChangeStatus.Pending);
            retainedPhoto = preservePendingPhoto
                ? old is not null ? old.IdentityPhotoObjectKey : profile.IdentityPhotoObjectKey
                : identityPhotoObjectKey;
            var removeIdentityPhoto = preservePendingPhoto
                ? old?.RemoveIdentityPhoto ?? false
                : string.IsNullOrWhiteSpace(identityPhotoObjectKey);
            request = new EmployeeProfileSensitiveChangeRequest
            {
                UserGUID = userGuid,
                BankBsb = Normalize(dto.BankBsb),
                BankAccountNumber = Normalize(dto.BankAccountNumber),
                SuperannuationCompanyName = Normalize(dto.SuperannuationCompanyName),
                SuperannuationCompanyCode = Normalize(dto.SuperannuationCompanyCode),
                SuperannuationAccountNumber = Normalize(dto.SuperannuationAccountNumber),
                IdentityType = Normalize(dto.IdentityType),
                IdentityId = Normalize(dto.IdentityId),
                // 未传生日（旧版客户端或证件照流程）时沿用基线：待审申请里的生日意图优先，其次正式生日。
                Birthday = dto.HasBirthday
                    ? dto.Birthday?.Date
                    : old is not null && RequestCarriesBirthday(profile, old) ? old.Birthday : profile.Birthday?.Date,
                IdentityPhotoObjectKey = retainedPhoto,
                RemoveIdentityPhoto = removeIdentityPhoto,
                Status = EmployeeProfileSensitiveChangeStatus.Pending,
                BaseSensitiveRevision = profile.SensitiveRevision,
                SubmittedAt = now,
                SubmittedBy = actor,
            };
            // 关键逻辑：变更字段以提交瞬间的正式资料为基线固化，终态后不随正式资料继续漂移。
            request.ChangedFieldsJson = JsonSerializer.Serialize(
                GetChangedFields(profile, request, includeBirthday: true)
            );
            if (old is not null)
            {
                var superseded = await db.Updateable<EmployeeProfileSensitiveChangeRequest>()
                    .SetColumns(item => new EmployeeProfileSensitiveChangeRequest
                    {
                        Status = EmployeeProfileSensitiveChangeStatus.Superseded,
                        SupersededAt = now,
                        SupersededBy = actor,
                    })
                    .Where(item => item.RequestId == old.RequestId
                        && item.Status == EmployeeProfileSensitiveChangeStatus.Pending)
                    .ExecuteCommandAsync();
                if (superseded != 1)
                {
                    // CAS 失败说明旧申请已被其他写入终结；本次不能继续插入基于旧 revision 的新 Pending。
                    await db.Ado.RollbackTranAsync();
                    return (RequestNotPending(), null);
                }
                if (!string.IsNullOrWhiteSpace(old.IdentityPhotoObjectKey)
                    && !string.Equals(old.IdentityPhotoObjectKey, retainedPhoto, StringComparison.Ordinal))
                {
                    await ScheduleTicketCleanupAsync(
                        old.RequestId,
                        old.IdentityPhotoObjectKey,
                        old.UserGUID
                    );
                }
            }
            request.RequestId = await db.Insertable(request).ExecuteReturnIdentityAsync();
            if (old is not null && !string.IsNullOrWhiteSpace(retainedPhoto))
            {
                // 覆盖申请但复用同一待审证件照时，恢复清理责任必须随申请迁移。
                await db.Updateable<EmployeeImageUploadTicket>()
                    .SetColumns(item => item.SensitiveChangeRequestId == request.RequestId)
                    .Where(item => item.SensitiveChangeRequestId == old.RequestId
                        && item.UserGUID == userGuid
                        && item.Kind == "identity"
                        && item.Status == EmployeeImageUploadStatus.Completed
                        && item.FinalObjectKey == retainedPhoto)
                    .ExecuteCommandAsync();
            }
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
        if (old is not null
            && !string.IsNullOrWhiteSpace(old.IdentityPhotoObjectKey)
            && !string.Equals(old.IdentityPhotoObjectKey, retainedPhoto, StringComparison.Ordinal))
        {
            await CleanupObjectAsync(old.IdentityPhotoObjectKey, retainedPhoto, userGuid, "覆盖待审证件照", old.RequestId);
        }
        return (
            ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.OK(
                MapDetail(request, profile),
                "敏感资料变更已提交审批"
            ),
            removalPlan
        );
    }

    private async Task<EmployeeProfileSensitiveChangeUpsertDto> BuildCurrentSnapshotAsync(string userGuid)
    {
        var pending = await _context.Db.Queryable<EmployeeProfileSensitiveChangeRequest>()
            .FirstAsync(item => item.UserGUID == userGuid
                && item.Status == EmployeeProfileSensitiveChangeStatus.Pending);
        var profile = await _context.Db.Queryable<EmployeeProfile>()
            .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
        if (pending is not null)
        {
            return new()
            {
                Birthday = RequestCarriesBirthday(profile, pending) ? pending.Birthday : profile?.Birthday?.Date,
                BankBsb = pending.BankBsb,
                BankAccountNumber = pending.BankAccountNumber,
                SuperannuationCompanyName = pending.SuperannuationCompanyName,
                SuperannuationCompanyCode = pending.SuperannuationCompanyCode,
                SuperannuationAccountNumber = pending.SuperannuationAccountNumber,
                IdentityType = pending.IdentityType,
                IdentityId = pending.IdentityId,
            };
        }
        return new()
        {
            Birthday = profile?.Birthday?.Date,
            BankBsb = profile?.BankBSB,
            BankAccountNumber = profile?.BankACC,
            SuperannuationCompanyName = profile?.SuperannuationCompanyName,
            SuperannuationCompanyCode = profile?.SuperannuationCompanyCode,
            SuperannuationAccountNumber = profile?.SuperannuationAccount,
            IdentityType = profile?.IdentityType,
            IdentityId = profile?.IdentityId,
        };
    }

    private async Task<(EmployeeProfile Profile, EmployeeProfileDataRemovalPlan? RemovalPlan)>
        EnsureProfileWithinTransactionAsync(string userGuid, string actor, DateTime now)
    {
        var profile = await _context.Db.Queryable<EmployeeProfile>()
            .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
        if (profile is not null)
        {
            return (profile, null);
        }

        var deletedProfile = await _context.Db.Queryable<EmployeeProfile>()
            .FirstAsync(item => item.UserGUID == userGuid);
        if (deletedProfile is not null)
        {
            var removalPlan = await PrepareProfileDataRemovalWithinTransactionAsync(
                userGuid,
                deletedProfile,
                actor
            );
            return (
                await ReactivateProfileWithinTransactionAsync(
                    deletedProfile,
                    userGuid,
                    actor,
                    now
                ),
                removalPlan
            );
        }

        profile = new EmployeeProfile
        {
            UserGUID = userGuid,
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
            UpdatedBy = actor,
        };
        try
        {
            profile.EmployeeInfoId = await _context.Db.Insertable(profile).ExecuteReturnIdentityAsync();
            return (profile, null);
        }
        catch (Exception ex) when (EmployeeCashierBarcodeService.IsUniqueConstraintViolation(ex))
        {
            // 数据库唯一键兜底跨进程竞态；冲突后必须回读胜出行，不能遗留孤立申请。
            var winner = await _context.Db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
            if (winner is not null)
            {
                return (winner, null);
            }

            deletedProfile = await _context.Db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == userGuid);
            if (deletedProfile is null)
            {
                throw;
            }
            var removalPlan = await PrepareProfileDataRemovalWithinTransactionAsync(
                userGuid,
                deletedProfile,
                actor
            );
            return (
                await ReactivateProfileWithinTransactionAsync(
                    deletedProfile,
                    userGuid,
                    actor,
                    now
                ),
                removalPlan
            );
        }
    }

    private async Task<EmployeeProfile> ReactivateProfileWithinTransactionAsync(
        EmployeeProfile deletedProfile,
        string userGuid,
        string actor,
        DateTime now
    )
    {
        var reactivated = await _context.Db.Updateable<EmployeeProfile>()
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
            .Where(item => item.EmployeeInfoId == deletedProfile.EmployeeInfoId && item.IsDeleted)
            .ExecuteCommandAsync();
        if (reactivated != 1)
        {
            var winner = await _context.Db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
            return winner ?? throw new InvalidOperationException("恢复软删除员工资料时状态已变化");
        }

        return new EmployeeProfile
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
    }

    private async Task CleanupObjectAsync(
        string? key,
        string? retainedKey,
        string userGuid,
        string context,
        int? requestId = null
    )
    {
        if (_storage is null
            || string.IsNullOrWhiteSpace(key)
            || string.Equals(key, retainedKey, StringComparison.Ordinal)
            || !EmployeeProfileImageRules.OwnsObjectKey(key, userGuid, "identity"))
        {
            return;
        }
        var profile = await _context.Db.Queryable<EmployeeProfile>()
            .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
        if (string.Equals(profile?.IdentityPhotoObjectKey, key, StringComparison.Ordinal))
        {
            // 待审快照可能复用当前正式证件照；终结申请绝不能删除仍被正式资料引用的对象。
            if (requestId.HasValue)
            {
                await MarkTicketCleanupCompletedAsync(requestId.Value, key);
            }
            return;
        }
        var result = await _storage.DeleteObjectAsync(key, CancellationToken.None);
        if (!result.Success)
        {
            _logger.LogWarning("{Context}清理失败。UserGUID: {UserGUID}, ObjectKey: {ObjectKey}", context, userGuid, key);
        }
        else if (requestId.HasValue)
        {
            await MarkTicketCleanupCompletedAsync(requestId.Value, key);
        }
    }

    private Task<int> MarkTicketCleanupCompletedAsync(int requestId, string objectKey) =>
        _context.Db.Updateable<EmployeeImageUploadTicket>()
            .SetColumns(item => new EmployeeImageUploadTicket
            {
                PreviousObjectCleanupStatus = EmployeeImageObjectCleanupStatus.Completed,
                PreviousObjectCleanupStartedAt = null,
            })
            .Where(item => item.SensitiveChangeRequestId == requestId
                && item.PreviousObjectKey == objectKey)
            .ExecuteCommandAsync();

    private async Task CleanupScheduledObjectBestEffortAsync(
        string userGuid,
        EmployeeProfileObjectCleanupTarget target,
        int? requestId,
        string context
    )
    {
        try
        {
            if (_storage is null
                || string.IsNullOrWhiteSpace(target.ObjectKey)
                || !EmployeeProfileImageRules.OwnsObjectKey(
                    target.ObjectKey,
                    userGuid,
                    target.Kind
                ))
            {
                return;
            }

            await using var mediaLock = await EmployeeProfileMediaLock.AcquireAsync(
                _context.Db,
                userGuid,
                target.Kind,
                _logger
            );
            var profile = await _context.Db.Queryable<EmployeeProfile>()
                .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
            EmployeeProfileSensitiveChangeRequest? pending = null;
            if (target.Kind == "identity")
            {
                pending = await _context.Db.Queryable<EmployeeProfileSensitiveChangeRequest>()
                    .FirstAsync(item => item.UserGUID == userGuid
                        && item.Status == EmployeeProfileSensitiveChangeStatus.Pending
                        && item.IdentityPhotoObjectKey == target.ObjectKey);
            }

            var isReferenced = target.Kind == "identity"
                ? string.Equals(
                    profile?.IdentityPhotoObjectKey,
                    target.ObjectKey,
                    StringComparison.Ordinal
                ) || pending is not null
                : profile is not null
                    && _storage.TryGetPublicObjectKey(profile.AvatarUrl, out var currentObjectKey)
                    && string.Equals(currentObjectKey, target.ObjectKey, StringComparison.Ordinal);
            if (isReferenced)
            {
                await MarkScheduledCleanupCompletedAsync(userGuid, target, requestId);
                return;
            }

            var result = await _storage.DeleteObjectAsync(target.ObjectKey, CancellationToken.None);
            if (!result.Success)
            {
                _logger.LogWarning(
                    "{Context}清理失败，保留 Pending 票据供后台重试。UserGUID: {UserGUID}, Kind: {Kind}, ObjectKey: {ObjectKey}",
                    context,
                    userGuid,
                    target.Kind,
                    target.ObjectKey
                );
                return;
            }

            await MarkScheduledCleanupCompletedAsync(userGuid, target, requestId);
        }
        catch (Exception ex)
        {
            // 提交后的 COS/票据清理只能尽力执行，不能让已成功的 GET 或删除接口反向失败。
            _logger.LogWarning(
                ex,
                "{Context}清理异常，保留 Pending 票据供后台重试。UserGUID: {UserGUID}, Kind: {Kind}, ObjectKey: {ObjectKey}",
                context,
                userGuid,
                target.Kind,
                target.ObjectKey
            );
        }
    }

    private Task<int> MarkScheduledCleanupCompletedAsync(
        string userGuid,
        EmployeeProfileObjectCleanupTarget target,
        int? requestId
    )
    {
        var update = _context.Db.Updateable<EmployeeImageUploadTicket>()
            .SetColumns(item => new EmployeeImageUploadTicket
            {
                PreviousObjectCleanupStatus = EmployeeImageObjectCleanupStatus.Completed,
                PreviousObjectCleanupStartedAt = null,
            })
            .Where(item => item.UserGUID == userGuid
                && item.Kind == target.Kind
                && item.Status == EmployeeImageUploadStatus.Completed
                && item.PreviousObjectKey == target.ObjectKey);
        if (requestId.HasValue)
        {
            update = update.Where(item => item.SensitiveChangeRequestId == requestId.Value);
        }
        return update.ExecuteCommandAsync();
    }

    private async Task<int> ScheduleTicketCleanupAsync(
        int requestId,
        string? objectKey,
        string userGuid
    ) => await ScheduleObjectCleanupWithinTransactionAsync(
        userGuid,
        new EmployeeProfileObjectCleanupTarget("identity", objectKey ?? string.Empty),
        requestId
    );

    private async Task<int> ScheduleObjectCleanupWithinTransactionAsync(
        string userGuid,
        EmployeeProfileObjectCleanupTarget target,
        int? requestId = null
    )
    {
        if (string.IsNullOrWhiteSpace(target.ObjectKey)
            || !EmployeeProfileImageRules.OwnsObjectKey(target.ObjectKey, userGuid, target.Kind))
        {
            return 0;
        }

        var schedule = _context.Db.Updateable<EmployeeImageUploadTicket>()
            .SetColumns(item => new EmployeeImageUploadTicket
            {
                PreviousObjectKey = target.ObjectKey,
                PreviousObjectCleanupStatus = EmployeeImageObjectCleanupStatus.Pending,
                PreviousObjectCleanupStartedAt = null,
            })
            .Where(item => item.UserGUID == userGuid
                && item.Kind == target.Kind
                && item.Status == EmployeeImageUploadStatus.Completed
                && (item.PreviousObjectKey == null
                    || item.PreviousObjectKey == ""
                    || item.PreviousObjectKey == target.ObjectKey
                    || item.PreviousObjectCleanupStatus == EmployeeImageObjectCleanupStatus.None
                    || item.PreviousObjectCleanupStatus == EmployeeImageObjectCleanupStatus.Completed));
        schedule = requestId.HasValue
            ? schedule.Where(item => item.SensitiveChangeRequestId == requestId.Value)
            : schedule.Where(item => item.FinalObjectKey == target.ObjectKey);
        var scheduled = await schedule.ExecuteCommandAsync();
        if (scheduled > 0)
        {
            return scheduled;
        }

        // 正式图片可能来自旧链路而没有上传票据；补建恢复票据，确保 COS 短暂失败后可重试。
        var now = DateTime.UtcNow;
        var recoveryTicket = new EmployeeImageUploadTicket
        {
            PendingObjectKey = $"cleanup/employee-profiles/{userGuid}/{target.Kind}/{Guid.NewGuid():N}",
            UserGUID = userGuid,
            Kind = target.Kind,
            ContentType = "application/octet-stream",
            FileSize = 0,
            CreatedAt = now,
            ExpiresAt = now,
            Status = EmployeeImageUploadStatus.Completed,
            CompletedAt = now,
            StageChangedAt = now,
            PreviousObjectKey = target.ObjectKey,
            PreviousObjectCleanupStatus = EmployeeImageObjectCleanupStatus.Pending,
            SensitiveChangeRequestId = requestId,
        };
        return await _context.Db.Insertable(recoveryTicket).ExecuteCommandAsync();
    }

    private EmployeeProfileSensitiveChangeDetailDto MapDetail(
        EmployeeProfileSensitiveChangeRequest request,
        EmployeeProfile? profile,
        string? username = null,
        StoreLabels? storeLabels = null
    )
    {
        var dto = new EmployeeProfileSensitiveChangeDetailDto
        {
            RequestId = request.RequestId,
            UserGuid = request.UserGUID,
            Username = username,
            StoreCodes = storeLabels?.Codes ?? [],
            StoreNames = storeLabels?.Names ?? [],
            Status = FormatStatus(request.Status),
            BankBsb = request.BankBsb,
            BankAccountNumber = request.BankAccountNumber,
            SuperannuationCompanyName = request.SuperannuationCompanyName,
            SuperannuationCompanyCode = request.SuperannuationCompanyCode,
            SuperannuationAccountNumber = request.SuperannuationAccountNumber,
            IdentityType = request.IdentityType,
            IdentityId = request.IdentityId,
            // 未变更生日的申请（含加列前的历史申请）展示正式生日，避免把空值误显示为「改成空」。
            Birthday = RequestCarriesBirthday(profile, request) ? request.Birthday : profile?.Birthday,
            HasIdentityPhoto = !string.IsNullOrWhiteSpace(request.IdentityPhotoObjectKey),
            BaseSensitiveRevision = request.BaseSensitiveRevision,
            SubmittedAt = request.SubmittedAt,
            SubmittedBy = request.SubmittedBy,
            ReviewedAt = request.ReviewedAt,
            ReviewedBy = request.ReviewedBy,
            ReviewReason = request.ReviewReason,
            // 详情只暴露受控字段标识；历史记录缺少快照时才动态回退。
            ChangedFields = ResolveChangedFields(profile, request),
            CurrentSnapshot = MapCurrentSnapshot(profile),
        };
        if (_storage is not null
            && request.Status == EmployeeProfileSensitiveChangeStatus.Pending
            && !string.IsNullOrWhiteSpace(request.IdentityPhotoObjectKey))
        {
            // 关键逻辑：待审对象在驳回或作废后可能已被删除，终态详情不得继续签发访问地址。
            var signed = _storage.GetSignedDownload(request.IdentityPhotoObjectKey, 300);
            dto.IdentityPhotoUrl = signed.Url;
            dto.IdentityPhotoUrlExpiresAt = signed.ExpiresAtUtc;
        }
        return dto;
    }

    private static EmployeeProfileSensitiveChangeSummaryDto MapSummary(
        EmployeeProfileSensitiveChangeRequest request,
        string? username,
        EmployeeProfile? profile,
        StoreLabels? storeLabels = null
    ) => new()
    {
        RequestId = request.RequestId,
        UserGuid = request.UserGUID,
        Username = username,
        StoreCodes = storeLabels?.Codes ?? [],
        StoreNames = storeLabels?.Names ?? [],
        Status = FormatStatus(request.Status),
        BaseSensitiveRevision = request.BaseSensitiveRevision,
        SubmittedAt = request.SubmittedAt,
        ReviewedAt = request.ReviewedAt,
        // 列表只返回受控字段标识；完整正式值和申请值只在服务端内存中参与比较。
        ChangedFields = ResolveChangedFields(profile, request),
    };

    private EmployeeProfileSensitiveSnapshotDto MapCurrentSnapshot(EmployeeProfile? profile)
    {
        var snapshot = new EmployeeProfileSensitiveSnapshotDto
        {
            BankBsb = profile?.BankBSB,
            BankAccountNumber = profile?.BankACC,
            SuperannuationCompanyName = profile?.SuperannuationCompanyName,
            SuperannuationCompanyCode = profile?.SuperannuationCompanyCode,
            SuperannuationAccountNumber = profile?.SuperannuationAccount,
            IdentityType = profile?.IdentityType,
            IdentityId = profile?.IdentityId,
            Birthday = profile?.Birthday,
            HasIdentityPhoto = HasFormalIdentityPhoto(profile),
        };
        if (_storage is not null && !string.IsNullOrWhiteSpace(profile?.IdentityPhotoObjectKey))
        {
            snapshot.IdentityPhotoUrl = _storage.GetSignedDownload(profile.IdentityPhotoObjectKey, 300).Url;
        }
        // legacy URL 未经过当前对象存储签名链验证，审核接口只返回照片存在标记，不原样透传。
        return snapshot;
    }

    private async Task<ReviewAccess?> GetReviewAccessAsync()
    {
        var scope = await _manageableStoreScope.GetScopeAsync();
        var user = _httpContextAccessor.HttpContext?.User;
        if (!scope.IsAllowed || !scope.IsAuthenticated || user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        // scope.IsAdmin 同时包含仓库经理；必须再核对真实角色，不能据此放宽敏感资料审核。
        if (HasAnyRole(user, Permissions.SuperAdminRoleNames))
        {
            return new ReviewAccess(true, scope.UserGuid, []);
        }
        if (!HasAnyRole(user, StoreManagerRoleAliases) || scope.StoreGuids.Count == 0)
        {
            return null;
        }
        return new ReviewAccess(false, scope.UserGuid, scope.StoreGuids);
    }

    private async Task<bool> CanReviewTargetAsync(ReviewAccess access, string targetUserGuid)
    {
        if (access.IsAdmin)
        {
            return true;
        }
        if (targetUserGuid.Equals(access.UserGuid, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var inManagedStore = await _context.Db.Queryable<UserStore>()
            .AnyAsync(item => item.UserGUID == targetUserGuid
                && !item.IsDeleted
                && access.StoreGuids.Contains(item.StoreGUID));
        if (!inManagedStore)
        {
            return false;
        }
        return !(await GetProtectedTargetUserGuidsAsync([targetUserGuid])).Contains(targetUserGuid);
    }

    private async Task<HashSet<string>> GetProtectedTargetUserGuidsAsync(IReadOnlyCollection<string> userGuids)
    {
        if (userGuids.Count == 0)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
        var roleRows = await _context.Db.Queryable<UserRole, Role>((userRole, role) =>
                userRole.RoleGUID == role.RoleGUID)
            .Where((userRole, role) =>
                userGuids.Contains(userRole.UserGUID)
                && !userRole.IsDeleted
                && !role.IsDeleted)
            .Select((userRole, role) => new { userRole.UserGUID, role.RoleName })
            .ToListAsync();
        return roleRows
            .Where(item => ProtectedTargetRoleAliases.Contains(
                item.RoleName,
                StringComparer.OrdinalIgnoreCase
            ))
            .Select(item => item.UserGUID)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, StoreLabels>> GetStoreLabelsAsync(
        IReadOnlyCollection<string> userGuids,
        IReadOnlyList<string>? allowedStoreGuids
    )
    {
        if (userGuids.Count == 0)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
        var query = _context.Db.Queryable<UserStore, Store>((userStore, store) =>
                userStore.StoreGUID == store.StoreGUID)
            .Where((userStore, store) =>
                userGuids.Contains(userStore.UserGUID) && !userStore.IsDeleted && !store.IsDeleted);
        if (allowedStoreGuids is not null)
        {
            query = query.Where((userStore, store) => allowedStoreGuids.Contains(userStore.StoreGUID));
        }
        var rows = await query.Select((userStore, store) => new
        {
            userStore.UserGUID,
            store.StoreCode,
            store.StoreName,
        }).ToListAsync();
        return rows.GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new StoreLabels(
                    group.Select(item => item.StoreCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    group.Select(item => item.StoreName).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                ),
                StringComparer.OrdinalIgnoreCase
            );
    }

    private static bool HasAnyRole(ClaimsPrincipal user, IReadOnlyCollection<string> roles) =>
        user.Claims.Any(claim =>
            (claim.Type.Equals(ClaimTypes.Role, StringComparison.OrdinalIgnoreCase)
                || claim.Type.Equals("role", StringComparison.OrdinalIgnoreCase))
            && roles.Contains(claim.Value, StringComparer.OrdinalIgnoreCase)
        );

    private static ApiResponse<T> ReviewScopeForbidden<T>() =>
        ApiResponse<T>.Error("当前账号无权审核该员工的敏感资料", ReviewScopeForbiddenCode);

    private static ApiResponse<T> RequestNotFound<T>() =>
        ApiResponse<T>.Error("申请不存在", "REQUEST_NOT_FOUND");

    private sealed record ReviewAccess(bool IsAdmin, string UserGuid, IReadOnlyList<string> StoreGuids);
    private sealed record StoreLabels(List<string> Codes, List<string> Names);

    private static List<string> ResolveChangedFields(
        EmployeeProfile? profile,
        EmployeeProfileSensitiveChangeRequest request
    )
    {
        if (string.IsNullOrWhiteSpace(request.ChangedFieldsJson))
        {
            // 兼容加列前的历史申请；新申请均读取持久化快照。历史申请不含生日意图，不参与生日比较。
            return GetChangedFields(profile, request, includeBirthday: false);
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<List<string>>(request.ChangedFieldsJson);
            if (persisted is null)
            {
                return [];
            }

            // 不直接回传数据库 JSON，避免损坏或被篡改的内容泄露敏感值。
            var persistedSet = persisted.ToHashSet(StringComparer.Ordinal);
            return SensitiveFieldNames.Where(persistedSet.Contains).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<string> GetChangedFields(
        EmployeeProfile? profile,
        EmployeeProfileSensitiveChangeRequest request,
        bool includeBirthday
    )
    {
        var fields = new List<string>();
        if (includeBirthday && !DateEquals(profile?.Birthday, request.Birthday))
        {
            fields.Add(BirthdayFieldName);
        }
        AddChanged(fields, "bankBsb", profile?.BankBSB, request.BankBsb);
        AddChanged(fields, "bankAccountNumber", profile?.BankACC, request.BankAccountNumber);
        AddChanged(fields, "superannuationCompanyName", profile?.SuperannuationCompanyName, request.SuperannuationCompanyName);
        AddChanged(fields, "superannuationCompanyCode", profile?.SuperannuationCompanyCode, request.SuperannuationCompanyCode);
        AddChanged(fields, "superannuationAccountNumber", profile?.SuperannuationAccount, request.SuperannuationAccountNumber);
        AddChanged(fields, "identityType", profile?.IdentityType, request.IdentityType);
        AddChanged(fields, "identityId", profile?.IdentityId, request.IdentityId);
        if (request.RemoveIdentityPhoto)
        {
            if (HasFormalIdentityPhoto(profile))
            {
                fields.Add("identityPhotoUrl");
            }
        }
        else if (!NormalizedEquals(profile?.IdentityPhotoObjectKey, request.IdentityPhotoObjectKey))
        {
            fields.Add("identityPhotoUrl");
        }
        return fields;
    }

    /// <summary>申请是否携带生日修改意图，以提交时固化的变更字段为准。</summary>
    private static bool RequestCarriesBirthday(
        EmployeeProfile? profile,
        EmployeeProfileSensitiveChangeRequest request
    ) => ResolveChangedFields(profile, request).Contains(BirthdayFieldName, StringComparer.Ordinal);

    private static bool DateEquals(DateTime? left, DateTime? right) => left?.Date == right?.Date;

    private static void AddChanged(List<string> fields, string field, string? current, string? proposed)
    {
        if (!NormalizedEquals(current, proposed))
        {
            fields.Add(field);
        }
    }

    private static ApiResponse<EmployeeProfileSensitiveChangeDetailDto> VersionConflict() =>
        ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.Error(
            "正式敏感资料已被管理员更新，请重新提交申请",
            VersionConflictCode
        );

    private static ApiResponse<EmployeeProfileSensitiveChangeDetailDto> RequestNotPending() =>
        ApiResponse<EmployeeProfileSensitiveChangeDetailDto>.Error(
            "申请已处理",
            "REQUEST_NOT_PENDING"
        );

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool NormalizedEquals(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static bool HasFormalIdentityPhoto(EmployeeProfile? profile) =>
        !string.IsNullOrWhiteSpace(profile?.IdentityPhotoObjectKey)
        || !string.IsNullOrWhiteSpace(profile?.IdentityPhotoUrl);

    private static bool HasSensitiveValueChanges(
        EmployeeProfile profile,
        EmployeeProfileSensitiveChangeRequest request
    ) =>
        !NormalizedEquals(profile.BankBSB, request.BankBsb)
        || !NormalizedEquals(profile.BankACC, request.BankAccountNumber)
        || !NormalizedEquals(profile.SuperannuationCompanyName, request.SuperannuationCompanyName)
        || !NormalizedEquals(profile.SuperannuationCompanyCode, request.SuperannuationCompanyCode)
        || !NormalizedEquals(profile.SuperannuationAccount, request.SuperannuationAccountNumber)
        || !NormalizedEquals(profile.IdentityType, request.IdentityType)
        || !NormalizedEquals(profile.IdentityId, request.IdentityId)
        || !NormalizedEquals(profile.IdentityPhotoObjectKey, request.IdentityPhotoObjectKey);

    private static string FormatStatus(EmployeeProfileSensitiveChangeStatus status) =>
        status.ToString();

    private static EmployeeProfileSensitiveChangeStatus? ParseStatus(string? status) =>
        Enum.TryParse<EmployeeProfileSensitiveChangeStatus>(status, true, out var parsed)
            ? parsed
            : null;

    private sealed class SensitiveRevisionConflictException : Exception
    {
    }
}
