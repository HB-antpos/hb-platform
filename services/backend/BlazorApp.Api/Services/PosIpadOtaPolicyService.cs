using System.Globalization;
using System.Text.RegularExpressions;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using SqlSugar;

namespace BlazorApp.Api.Services;

public sealed partial class PosIpadOtaPolicyService(
    ISqlSugarClient db,
    ILogger<PosIpadOtaPolicyService> logger,
    // 设备注册信息在 POSM 库；未注入时设备覆盖层自动关闭，行为与改造前一致（单测与旧调用方不受影响）。
    ISqlSugarClient? posmDb = null
) : IPosIpadOtaPolicyService
{
    private const string ProductionEnvironment = "production";
    private const string ReleaseChannelPrefix = "pos-ipad-release-";
    private static readonly Regex ChannelPattern = new(
        "^pos-ipad-release-[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$",
        RegexOptions.Compiled
    );
    private static readonly Regex RuntimeVersionPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._/-]{0,119}$",
        RegexOptions.Compiled
    );
    private static readonly Regex GitCommitPattern = new(
        "^[0-9a-f]{7,120}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    public async Task<ApiResponse<List<PosIpadOtaReleaseDto>>> GetReleasesAsync()
    {
        var releases = await db.Queryable<PosIpadOtaRelease>()
            .Where(item => item.Environment == ProductionEnvironment && !item.IsDeleted)
            .OrderByDescending(item => item.PublishedAtUtc)
            .Take(200)
            .ToListAsync();
        return ApiResponse<List<PosIpadOtaReleaseDto>>.OK(releases.Select(MapRelease).ToList());
    }

    public async Task<ApiResponse<PosIpadOtaChannelPreflightDto>> PreflightReleaseChannelAsync(
        PosIpadOtaChannelPreflightRequest request
    )
    {
        if (!TryNormalizeReleaseChannel(request.Channel, out var channel))
        {
            return ApiResponse<PosIpadOtaChannelPreflightDto>.Error(
                "OTA channel 无效",
                "OTA_CHANNEL_INVALID"
            );
        }

        var registered = await db.Queryable<PosIpadOtaRelease>()
            .AnyAsync(item =>
                item.Environment == ProductionEnvironment
                && item.Channel == channel
                && !item.IsDeleted
            );
        if (registered)
        {
            return ApiResponse<PosIpadOtaChannelPreflightDto>.Error(
                "OTA channel 已登记",
                "OTA_CHANNEL_ALREADY_REGISTERED"
            );
        }

        return ApiResponse<PosIpadOtaChannelPreflightDto>.OK(
            new PosIpadOtaChannelPreflightDto
            {
                Channel = channel,
                Available = true,
            }
        );
    }

    public async Task<ApiResponse<PosIpadOtaReleaseDto>> CreateReleaseAsync(
        PosIpadOtaReleaseCreateRequest request,
        string currentUser
    )
    {
        var updateGroupId = NormalizeGuid(request.UpdateGroupId);
        var iosUpdateId = NormalizeGuid(request.IosUpdateId);
        if (updateGroupId is null || iosUpdateId is null)
        {
            return ReleaseError(
                "OTA_RELEASE_ID_INVALID",
                "Update group ID 和 iOS update ID 必须是 UUID"
            );
        }

        if (!TryNormalizeReleaseChannel(request.Channel, out var channel))
        {
            return ReleaseError("OTA_CHANNEL_INVALID", "OTA channel 无效");
        }

        var runtimeVersion = NormalizeOptional(request.RuntimeVersion);
        if (runtimeVersion is null || !RuntimeVersionPattern.IsMatch(runtimeVersion))
        {
            return ReleaseError("OTA_RUNTIME_INVALID", "OTA runtimeVersion 无效");
        }

        var gitCommit = NormalizeOptional(request.GitCommitHash)?.ToLowerInvariant();
        if (gitCommit is not null && !GitCommitPattern.IsMatch(gitCommit))
        {
            return ReleaseError("OTA_GIT_COMMIT_INVALID", "Git commit hash 无效");
        }

        var dashboardUrl = NormalizeOptional(request.DashboardUrl);
        if (dashboardUrl is not null && !TryNormalizeHttpsUrl(dashboardUrl, out dashboardUrl))
        {
            return ReleaseError("OTA_DASHBOARD_URL_INVALID", "OTA dashboard URL 必须使用 HTTPS");
        }

        PosIpadOtaRelease? rollbackOf = null;
        if (request.IsRollback)
        {
            if (request.RollbackOfReleaseId is null)
            {
                return ReleaseError(
                    "OTA_ROLLBACK_SOURCE_REQUIRED",
                    "回退发布必须指定原发布"
                );
            }

            rollbackOf = await db.Queryable<PosIpadOtaRelease>()
                .FirstAsync(item =>
                    item.Id == request.RollbackOfReleaseId.Value
                    && item.Environment == ProductionEnvironment
                    && !item.IsDeleted
                );
            if (rollbackOf is null)
            {
                return ReleaseError(
                    "OTA_ROLLBACK_SOURCE_INVALID",
                    "回退原发布不存在"
                );
            }
        }
        else if (request.RollbackOfReleaseId is not null)
        {
            return ReleaseError(
                "OTA_ROLLBACK_SOURCE_UNEXPECTED",
                "非回退发布不能指定回退原发布"
            );
        }

        var explicitPublishedAt = request.PublishedAtUtc.HasValue;
        var publishedAt = explicitPublishedAt
            ? NormalizeUtc(request.PublishedAtUtc!.Value)
            : DateTime.UtcNow;
        var facts = new NormalizedOtaReleaseFacts(
            updateGroupId,
            iosUpdateId,
            channel,
            runtimeVersion!,
            gitCommit,
            dashboardUrl,
            publishedAt,
            explicitPublishedAt,
            request.IsRollback,
            rollbackOf?.Id
        );
        var existing = await FindReleaseConflictsAsync(
            updateGroupId,
            iosUpdateId,
            channel
        );
        if (existing.Count > 0)
        {
            if (
                existing.Count == 1
                && IsSameReleaseFacts(existing[0], facts)
            )
            {
                return ApiResponse<PosIpadOtaReleaseDto>.OK(
                    MapRelease(existing[0]),
                    "OTA 发布事实已登记"
                );
            }

            return ReleaseError(
                "OTA_RELEASE_CONFLICT",
                "Update group 或 iOS update ID 已登记不同的不可变发布事实"
            );
        }

        var now = DateTime.UtcNow;
        var entity = new PosIpadOtaRelease
        {
            Id = Guid.NewGuid(),
            Environment = ProductionEnvironment,
            UpdateGroupId = facts.UpdateGroupId,
            IosUpdateId = facts.IosUpdateId,
            Channel = facts.Channel,
            RuntimeVersion = facts.RuntimeVersion,
            GitCommitHash = facts.GitCommitHash,
            DashboardUrl = facts.DashboardUrl,
            PublishedAtUtc = facts.PublishedAtUtc,
            IsRollback = facts.IsRollback,
            RollbackOfReleaseId = facts.RollbackOfReleaseId,
            CreatedAt = now,
            CreatedBy = NormalizeUser(currentUser),
            UpdatedAt = null,
            IsDeleted = false,
        };

        try
        {
            await db.Insertable(entity).ExecuteCommandAsync();
        }
        catch (Exception ex) when (AppUpdatePolicyMutationLock.IsUniqueConflict(ex))
        {
            logger.LogInformation(ex, "iPad OTA 发布事实并发重复登记，转为读取既有记录");
            existing = await FindReleaseConflictsAsync(
                updateGroupId,
                iosUpdateId,
                channel
            );
            if (
                existing.Count == 1
                && IsSameReleaseFacts(existing[0], facts)
            )
            {
                return ApiResponse<PosIpadOtaReleaseDto>.OK(
                    MapRelease(existing[0]),
                    "OTA 发布事实已登记"
                );
            }

            return ReleaseError(
                "OTA_RELEASE_CONFLICT",
                "Update group 或 iOS update ID 已登记不同的不可变发布事实"
            );
        }

        return ApiResponse<PosIpadOtaReleaseDto>.OK(
            MapRelease(entity),
            "iPad OTA 发布事实登记成功"
        );
    }

    public async Task<ApiResponse<PosIpadOtaRolloutDto>> GetRolloutAsync()
    {
        var rollout = await db.Queryable<PosIpadOtaRollout>()
            .Where(item => item.Environment == ProductionEnvironment && !item.IsDeleted)
            .OrderByDescending(item => item.PolicyVersion)
            .FirstAsync();
        return ApiResponse<PosIpadOtaRolloutDto>.OK(
            rollout is null
                ? EmptyRollout()
                : await MapRolloutAsync(rollout)
        );
    }

    public async Task<ApiResponse<PosIpadOtaRolloutDto>> SetRolloutAsync(
        PosIpadOtaRolloutRequest request,
        string currentUser
    )
    {
        if (!request.ExpectedPolicyVersion.HasValue)
        {
            var current = await db.Queryable<PosIpadOtaRollout>()
                .Where(item =>
                    item.Environment == ProductionEnvironment && !item.IsDeleted
                )
                .OrderByDescending(item => item.PolicyVersion)
                .FirstAsync();
            return RolloutVersionError(
                AppUpdatePolicyErrorCodes.VersionRequired,
                request.ExpectedPolicyVersion,
                current?.PolicyVersion ?? 0
            );
        }

        if (!request.Enabled)
        {
            return await DisableRolloutAsync(
                currentUser,
                request.ExpectedPolicyVersion.Value
            );
        }

        if (request.ReleaseId is null)
        {
            return RolloutError(
                "OTA_RELEASE_REQUIRED",
                "启用 rollout 必须选择已登记的 OTA 发布"
            );
        }

        var message = NormalizeOptional(request.ReleaseMessage);
        if (message?.Length > 1000)
        {
            return RolloutError(
                "RELEASE_MESSAGE_TOO_LONG",
                "更新说明不能超过 1000 个字符"
            );
        }

        PosIpadOtaRolloutDto? saved = null;
        ApiResponse<PosIpadOtaRolloutDto>? validationError = null;
        var transaction = await db.Ado.UseTranAsync(async () =>
        {
            await AppUpdatePolicyMutationLock.AcquireAsync(
                db,
                $"app-update-policy:ota-rollout:{ProductionEnvironment}"
            );
            var release = await db.Queryable<PosIpadOtaRelease>()
                .FirstAsync(item =>
                    item.Id == request.ReleaseId.Value
                    && item.Environment == ProductionEnvironment
                    && !item.IsDeleted
                );
            if (release is null)
            {
                validationError = RolloutError(
                    "OTA_RELEASE_INVALID",
                    "OTA 发布不存在"
                );
                return;
            }

            var targetValidation = await ValidateTargetsAsync(
                request.TargetScope,
                request.TargetStoreGuids
            );
            if (!targetValidation.Success)
            {
                validationError = RolloutError(
                    targetValidation.ErrorCode!,
                    targetValidation.Message!
                );
                return;
            }

            var rows = await db.Queryable<PosIpadOtaRollout>()
                .Where(item => item.Environment == ProductionEnvironment && !item.IsDeleted)
                .ToListAsync();
            var activeRows = rows.Where(item => item.Enabled).ToList();
            var actualPolicyVersion = rows.Count == 0
                ? 0
                : rows.Max(item => item.PolicyVersion);
            if (activeRows.Count == 1)
            {
                var active = activeRows[0];
                var activeTargets = await db.Queryable<PosIpadOtaRolloutTarget>()
                    .Where(item =>
                        item.RolloutId == active.Id && !item.IsDeleted
                    )
                    .Select(item => item.StoreGuid)
                    .ToListAsync();
                if (
                    IsSameRollout(
                        active,
                        activeTargets,
                        release.Id,
                        request.ForceUpdate,
                        targetValidation.TargetScope,
                        targetValidation.StoreGuids,
                        message
                    )
                )
                {
                    saved = await MapRolloutAsync(active, release);
                    return;
                }
            }

            if (request.ExpectedPolicyVersion.Value != actualPolicyVersion)
            {
                validationError = RolloutVersionError(
                    AppUpdatePolicyErrorCodes.VersionConflict,
                    request.ExpectedPolicyVersion,
                    actualPolicyVersion
                );
                return;
            }

            var now = DateTime.UtcNow;
            foreach (var active in activeRows)
            {
                active.Enabled = false;
                active.UpdatedAt = now;
                active.UpdatedBy = NormalizeUser(currentUser);
            }

            if (activeRows.Count > 0)
            {
                await db.Updateable(activeRows).ExecuteCommandAsync();
            }

            var rollout = new PosIpadOtaRollout
            {
                Id = Guid.NewGuid(),
                Environment = ProductionEnvironment,
                ReleaseId = release.Id,
                ForceUpdate = request.ForceUpdate,
                TargetScope = targetValidation.TargetScope,
                ReleaseMessage = message,
                Enabled = true,
                PolicyVersion = rows.Count == 0 ? 1 : rows.Max(item => item.PolicyVersion) + 1,
                CreatedAt = now,
                CreatedBy = NormalizeUser(currentUser),
                UpdatedAt = now,
                UpdatedBy = NormalizeUser(currentUser),
                IsDeleted = false,
            };
            await db.Insertable(rollout).ExecuteCommandAsync();

            if (targetValidation.StoreGuids.Count > 0)
            {
                var targets = targetValidation.StoreGuids.Select(storeGuid =>
                    new PosIpadOtaRolloutTarget
                    {
                        Id = Guid.NewGuid(),
                        RolloutId = rollout.Id,
                        StoreGuid = storeGuid,
                        CreatedAt = now,
                        CreatedBy = NormalizeUser(currentUser),
                        UpdatedAt = null,
                        IsDeleted = false,
                    }
                ).ToList();
                await db.Insertable(targets).ExecuteCommandAsync();
            }

            saved = await MapRolloutAsync(rollout, release);
        });

        if (validationError is not null)
        {
            return validationError;
        }

        if (!transaction.IsSuccess || saved is null)
        {
            logger.LogError(transaction.ErrorException, "iPad OTA rollout 事务保存失败");
            return RolloutError(
                "OTA_ROLLOUT_SAVE_FAILED",
                "iPad OTA rollout 保存失败"
            );
        }

        return ApiResponse<PosIpadOtaRolloutDto>.OK(saved);
    }

    public async Task<PosIpadOtaDecisionDto> GetDecisionAsync(
        PosIpadOtaDecisionRequest request
    )
    {
        var store = await ResolveActiveStoreAsync(request.StoreCode);
        if (store is null)
        {
            return NoOtaDecision();
        }

        // 关键：三层按 设备 → 分店 → 全局基线 依次查找，先命中的层就是最终结论，
        // 即使该层结论是「已是目标版本 / runtime 不符」也不再回落到下一层，
        // 否则被钉在旧版本的设备一到目标就会立刻被基线拉走。
        var layer = await ResolveDecisionLayerAsync(request, store);
        if (layer is null)
        {
            return NoOtaDecision();
        }

        var release = await db.Queryable<PosIpadOtaRelease>()
            .FirstAsync(item =>
                item.Id == layer.ReleaseId
                && item.Environment == ProductionEnvironment
                && !item.IsDeleted
            );
        if (
            release is null
            || !string.Equals(
                NormalizeOptional(request.RuntimeVersion),
                release.RuntimeVersion,
                StringComparison.Ordinal
            )
            || string.Equals(
                NormalizeOptional(request.CurrentUpdateId),
                release.IosUpdateId,
                StringComparison.OrdinalIgnoreCase
            )
            || string.Equals(
                NormalizeOptional(request.CurrentUpdateGroupId),
                release.UpdateGroupId,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return NoOtaDecision();
        }

        return new PosIpadOtaDecisionDto
        {
            State = layer.ForceUpdate
                ? AppUpdateStates.Required
                : AppUpdateStates.Optional,
            PolicyVersion = layer.PolicyVersion.ToString(CultureInfo.InvariantCulture),
            Channel = release.Channel,
            RuntimeVersion = release.RuntimeVersion,
            IosUpdateId = release.IosUpdateId,
            UpdateGroupId = release.UpdateGroupId,
            ReleaseMessage = layer.ReleaseMessage,
        };
    }

    private async Task<OtaDecisionLayer?> ResolveDecisionLayerAsync(
        PosIpadOtaDecisionRequest request,
        Store store
    )
    {
        var now = DateTime.UtcNow;

        // ① 设备覆盖：同硬件、同分店的所有注册记录都算同一台设备（重新激活会产生新注册 ID）。
        var deviceIds = await ResolveDeviceRegistrationIdsAsync(request, store);
        if (deviceIds.Count > 0)
        {
            var deviceAssignment = await db.Queryable<PosIpadOtaAssignment>()
                .Where(item =>
                    item.Environment == ProductionEnvironment
                    && item.Enabled
                    && !item.IsDeleted
                    && item.TargetType == PosIpadOtaAssignmentTargetTypes.Device
                    && item.DeviceRegistrationId != null
                    && deviceIds.Contains(item.DeviceRegistrationId.Value)
                    && (item.ExpiresAtUtc == null || item.ExpiresAtUtc > now)
                )
                .OrderByDescending(item => item.PolicyVersion)
                .FirstAsync();
            if (deviceAssignment is not null)
            {
                return OtaDecisionLayer.From(deviceAssignment);
            }
        }

        // ② 分店覆盖
        var storeAssignment = await db.Queryable<PosIpadOtaAssignment>()
            .Where(item =>
                item.Environment == ProductionEnvironment
                && item.Enabled
                && !item.IsDeleted
                && item.TargetType == PosIpadOtaAssignmentTargetTypes.Store
                && item.StoreGuid == store.StoreGUID
                && (item.ExpiresAtUtc == null || item.ExpiresAtUtc > now)
            )
            .OrderByDescending(item => item.PolicyVersion)
            .FirstAsync();
        if (storeAssignment is not null)
        {
            return OtaDecisionLayer.From(storeAssignment);
        }

        // ③ 全局基线（原有 rollout，行为不变）
        var rollout = await db.Queryable<PosIpadOtaRollout>()
            .FirstAsync(item =>
                item.Environment == ProductionEnvironment
                && item.Enabled
                && !item.IsDeleted
            );
        if (rollout is null || !await MatchesTargetAsync(rollout, store.StoreGUID))
        {
            return null;
        }

        return OtaDecisionLayer.From(rollout);
    }

    private async Task<ApiResponse<PosIpadOtaRolloutDto>> DisableRolloutAsync(
        string currentUser,
        long expectedPolicyVersion
    )
    {
        PosIpadOtaRolloutDto? saved = null;
        ApiResponse<PosIpadOtaRolloutDto>? validationError = null;
        var transaction = await db.Ado.UseTranAsync(async () =>
        {
            await AppUpdatePolicyMutationLock.AcquireAsync(
                db,
                $"app-update-policy:ota-rollout:{ProductionEnvironment}"
            );
            var rows = await db.Queryable<PosIpadOtaRollout>()
                .Where(item =>
                    item.Environment == ProductionEnvironment && !item.IsDeleted
                )
                .ToListAsync();
            var activeRows = rows
                .Where(item => item.Enabled)
                .OrderByDescending(item => item.PolicyVersion)
                .ToList();
            var actualPolicyVersion = rows.Count == 0
                ? 0
                : rows.Max(item => item.PolicyVersion);
            if (activeRows.Count == 0)
            {
                var latest = rows
                    .OrderByDescending(item => item.PolicyVersion)
                    .FirstOrDefault();
                saved = latest is null
                    ? EmptyRollout()
                    : await MapRolloutAsync(latest);
                return;
            }

            if (expectedPolicyVersion != actualPolicyVersion)
            {
                validationError = RolloutVersionError(
                    AppUpdatePolicyErrorCodes.VersionConflict,
                    expectedPolicyVersion,
                    actualPolicyVersion
                );
                return;
            }

            var source = activeRows[0];
            var sourceTargets = await db.Queryable<PosIpadOtaRolloutTarget>()
                .Where(item =>
                    item.RolloutId == source.Id && !item.IsDeleted
                )
                .Select(item => item.StoreGuid)
                .ToListAsync();
            var now = DateTime.UtcNow;
            foreach (var active in activeRows)
            {
                active.Enabled = false;
                active.UpdatedAt = now;
                active.UpdatedBy = NormalizeUser(currentUser);
            }
            await db.Updateable(activeRows).ExecuteCommandAsync();

            var disabledEvent = new PosIpadOtaRollout
            {
                Id = Guid.NewGuid(),
                Environment = ProductionEnvironment,
                ReleaseId = source.ReleaseId,
                ForceUpdate = source.ForceUpdate,
                TargetScope = source.TargetScope,
                ReleaseMessage = source.ReleaseMessage,
                Enabled = false,
                PolicyVersion = rows.Max(item => item.PolicyVersion) + 1,
                CreatedAt = now,
                CreatedBy = NormalizeUser(currentUser),
                UpdatedAt = now,
                UpdatedBy = NormalizeUser(currentUser),
                IsDeleted = false,
            };
            await db.Insertable(disabledEvent).ExecuteCommandAsync();
            if (sourceTargets.Count > 0)
            {
                var targets = sourceTargets.Select(storeGuid =>
                    new PosIpadOtaRolloutTarget
                    {
                        Id = Guid.NewGuid(),
                        RolloutId = disabledEvent.Id,
                        StoreGuid = storeGuid,
                        CreatedAt = now,
                        CreatedBy = NormalizeUser(currentUser),
                        UpdatedAt = null,
                        IsDeleted = false,
                    }
                ).ToList();
                await db.Insertable(targets).ExecuteCommandAsync();
            }

            saved = await MapRolloutAsync(disabledEvent);
        });

        if (validationError is not null)
        {
            return validationError;
        }

        if (!transaction.IsSuccess || saved is null)
        {
            logger.LogError(transaction.ErrorException, "iPad OTA rollout 停用事务保存失败");
            return RolloutError(
                "OTA_ROLLOUT_SAVE_FAILED",
                "iPad OTA rollout 停用失败"
            );
        }

        return ApiResponse<PosIpadOtaRolloutDto>.OK(saved);
    }

    private async Task<TargetValidation> ValidateTargetsAsync(
        string? requestedScope,
        IReadOnlyCollection<string>? requestedStoreGuids
    )
    {
        var scope = NormalizeOptional(requestedScope)?.ToLowerInvariant()
            ?? AppUpdateTargetScopes.All;
        if (scope == AppUpdateTargetScopes.All)
        {
            return TargetValidation.All;
        }

        if (scope != AppUpdateTargetScopes.Stores)
        {
            return TargetValidation.Fail(
                "TARGET_SCOPE_INVALID",
                "目标范围必须是 all 或 stores"
            );
        }

        var requested = (requestedStoreGuids ?? Array.Empty<string>())
            .Select(NormalizeOptional)
            .Where(item => item is not null)
            .Select(item => NormalizeStoreGuid(item!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requested.Count == 0)
        {
            return TargetValidation.Fail(
                "TARGET_STORES_REQUIRED",
                "按分店投放时至少选择一个活动分店"
            );
        }

        var activeStores = await db.Queryable<Store>()
            .Where(item =>
                item.IsActive
                && !item.IsDeleted
                && requested.Contains(item.StoreGUID)
            )
            .ToListAsync();
        var canonical = activeStores.ToDictionary(
            item => item.StoreGUID,
            item => item.StoreGUID,
            StringComparer.OrdinalIgnoreCase
        );
        if (requested.Any(item => !canonical.ContainsKey(item)))
        {
            return TargetValidation.Fail(
                "TARGET_STORES_INVALID",
                "目标分店必须全部处于活动状态"
            );
        }

        return TargetValidation.Stores(requested.Select(item => canonical[item]).ToList());
    }

    private async Task<bool> MatchesTargetAsync(PosIpadOtaRollout rollout, string storeGuid)
    {
        if (rollout.TargetScope == AppUpdateTargetScopes.All)
        {
            return true;
        }

        if (rollout.TargetScope != AppUpdateTargetScopes.Stores)
        {
            return false;
        }

        return await db.Queryable<PosIpadOtaRolloutTarget>()
            .AnyAsync(item =>
                item.RolloutId == rollout.Id
                && item.StoreGuid == storeGuid
                && !item.IsDeleted
        );
    }

    private async Task<Store?> ResolveActiveStoreAsync(string? storeCode)
    {
        var normalized = NormalizeOptional(storeCode);
        if (normalized is null)
        {
            return null;
        }

        var normalizedLower = normalized.ToLowerInvariant();
        return await db.Queryable<Store>()
            .FirstAsync(item =>
                item.IsActive
                && !item.IsDeleted
                && item.StoreCode.ToLower() == normalizedLower
            );
    }

    private async Task<List<PosIpadOtaRelease>> FindReleaseConflictsAsync(
        string updateGroupId,
        string iosUpdateId,
        string channel
    ) =>
        // 每个发布事实同时独占 group、iOS update 和 channel；任一维度复用都必须核对完整事实。
        await db.Queryable<PosIpadOtaRelease>()
            .Where(item =>
                item.Environment == ProductionEnvironment
                && (
                    item.UpdateGroupId == updateGroupId
                    || item.IosUpdateId == iosUpdateId
                    || item.Channel == channel
                )
                && !item.IsDeleted
            )
            .ToListAsync();

    private static bool IsSameReleaseFacts(
        PosIpadOtaRelease existing,
        NormalizedOtaReleaseFacts expected
    ) =>
        string.Equals(
            existing.UpdateGroupId,
            expected.UpdateGroupId,
            StringComparison.Ordinal
        )
        && string.Equals(
            existing.IosUpdateId,
            expected.IosUpdateId,
            StringComparison.Ordinal
        )
        && string.Equals(existing.Channel, expected.Channel, StringComparison.Ordinal)
        && string.Equals(
            existing.RuntimeVersion,
            expected.RuntimeVersion,
            StringComparison.Ordinal
        )
        && string.Equals(
            existing.GitCommitHash,
            expected.GitCommitHash,
            StringComparison.OrdinalIgnoreCase
        )
        && string.Equals(
            existing.DashboardUrl,
            expected.DashboardUrl,
            StringComparison.Ordinal
        )
        && (
            !expected.HasExplicitPublishedAt
            || NormalizeUtc(existing.PublishedAtUtc)
                == expected.PublishedAtUtc
        )
        && existing.IsRollback == expected.IsRollback
        && existing.RollbackOfReleaseId == expected.RollbackOfReleaseId;

    private static bool IsSameRollout(
        PosIpadOtaRollout existing,
        IReadOnlyCollection<string> existingTargets,
        Guid releaseId,
        bool forceUpdate,
        string targetScope,
        IReadOnlyCollection<string> targetStoreGuids,
        string? releaseMessage
    ) =>
        existing.Enabled
        && existing.ReleaseId == releaseId
        && existing.ForceUpdate == forceUpdate
        && string.Equals(
            existing.TargetScope,
            targetScope,
            StringComparison.Ordinal
        )
        && string.Equals(
            existing.ReleaseMessage,
            releaseMessage,
            StringComparison.Ordinal
        )
        && HaveSameStores(existingTargets, targetStoreGuids);

    private static bool HaveSameStores(
        IEnumerable<string> left,
        IEnumerable<string> right
    ) =>
        new HashSet<string>(left, StringComparer.OrdinalIgnoreCase).SetEquals(right);

    private async Task<PosIpadOtaRolloutDto> MapRolloutAsync(
        PosIpadOtaRollout rollout,
        PosIpadOtaRelease? release = null
    )
    {
        release ??= await db.Queryable<PosIpadOtaRelease>()
            .FirstAsync(item => item.Id == rollout.ReleaseId && !item.IsDeleted);
        var targets = await db.Queryable<PosIpadOtaRolloutTarget>()
            .Where(item => item.RolloutId == rollout.Id && !item.IsDeleted)
            .ToListAsync();
        return new PosIpadOtaRolloutDto
        {
            Id = rollout.Id,
            Enabled = rollout.Enabled,
            PolicyVersion = rollout.PolicyVersion,
            ReleaseId = rollout.ReleaseId,
            ForceUpdate = rollout.ForceUpdate,
            TargetScope = rollout.TargetScope,
            TargetStoreGuids = targets.Select(item => item.StoreGuid).ToList(),
            ReleaseMessage = rollout.ReleaseMessage,
            Release = release is null ? null : MapRelease(release),
            UpdatedAt = rollout.UpdatedAt,
            UpdatedBy = rollout.UpdatedBy,
        };
    }

    private static PosIpadOtaReleaseDto MapRelease(PosIpadOtaRelease item) =>
        new()
        {
            Id = item.Id,
            Environment = item.Environment,
            UpdateGroupId = item.UpdateGroupId,
            IosUpdateId = item.IosUpdateId,
            Channel = item.Channel,
            RuntimeVersion = item.RuntimeVersion,
            GitCommitHash = item.GitCommitHash,
            DashboardUrl = item.DashboardUrl,
            PublishedAtUtc = item.PublishedAtUtc,
            IsRollback = item.IsRollback,
            RollbackOfReleaseId = item.RollbackOfReleaseId,
            CreatedAt = item.CreatedAt,
            CreatedBy = item.CreatedBy,
        };

    private static PosIpadOtaRolloutDto EmptyRollout() => new();

    private static PosIpadOtaDecisionDto NoOtaDecision() => new();

    private static string? NormalizeGuid(string? value) =>
        Guid.TryParse(NormalizeOptional(value), out var parsed)
            ? parsed.ToString()
            : null;

    private static bool TryNormalizeReleaseChannel(string? value, out string channel)
    {
        channel = NormalizeOptional(value)?.ToLowerInvariant() ?? string.Empty;
        return channel.Length <= 120
            && channel.StartsWith(ReleaseChannelPrefix, StringComparison.Ordinal)
            && ChannelPattern.IsMatch(channel);
    }

    private static string NormalizeStoreGuid(string value) =>
        Guid.TryParse(value, out var parsed) ? parsed.ToString() : value.Trim();

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private static bool TryNormalizeHttpsUrl(string value, out string normalized)
    {
        normalized = string.Empty;
        if (
            !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
        )
        {
            return false;
        }

        normalized = uri.ToString();
        return true;
    }

    private static string NormalizeUser(string? value) =>
        NormalizeOptional(value) ?? "System";

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ApiResponse<PosIpadOtaReleaseDto> ReleaseError(
        string code,
        string message
    ) => ApiResponse<PosIpadOtaReleaseDto>.Error(message, code);

    private static ApiResponse<PosIpadOtaRolloutDto> RolloutError(
        string code,
        string message
    ) => ApiResponse<PosIpadOtaRolloutDto>.Error(message, code);

    private static ApiResponse<PosIpadOtaRolloutDto> RolloutVersionError(
        string errorCode,
        long? expectedPolicyVersion,
        long actualPolicyVersion
    ) =>
        ApiResponse<PosIpadOtaRolloutDto>.Error(
            errorCode == AppUpdatePolicyErrorCodes.VersionRequired
                ? "expectedPolicyVersion 不能为空"
                : "更新策略版本已变化，请刷新后重试",
            errorCode,
            new
            {
                ExpectedPolicyVersion = expectedPolicyVersion,
                ActualPolicyVersion = actualPolicyVersion,
            }
        );

    /// <summary>三层决策里被选中的那一层，统一成决策需要的四个字段。</summary>
    private sealed record OtaDecisionLayer(
        Guid ReleaseId,
        bool ForceUpdate,
        string? ReleaseMessage,
        long PolicyVersion
    )
    {
        public static OtaDecisionLayer From(PosIpadOtaRollout rollout) =>
            new(rollout.ReleaseId, rollout.ForceUpdate, rollout.ReleaseMessage, rollout.PolicyVersion);

        public static OtaDecisionLayer From(PosIpadOtaAssignment assignment) =>
            new(
                assignment.ReleaseId,
                assignment.ForceUpdate,
                assignment.ReleaseMessage,
                assignment.PolicyVersion
            );
    }

    private sealed record NormalizedOtaReleaseFacts(
        string UpdateGroupId,
        string IosUpdateId,
        string Channel,
        string RuntimeVersion,
        string? GitCommitHash,
        string? DashboardUrl,
        DateTime PublishedAtUtc,
        bool HasExplicitPublishedAt,
        bool IsRollback,
        Guid? RollbackOfReleaseId
    );

    private sealed record TargetValidation(
        bool Success,
        string TargetScope,
        List<string> StoreGuids,
        string? ErrorCode,
        string? Message
    )
    {
        public static TargetValidation All { get; } =
            new(true, AppUpdateTargetScopes.All, new(), null, null);

        public static TargetValidation Stores(List<string> storeGuids) =>
            new(true, AppUpdateTargetScopes.Stores, storeGuids, null, null);

        public static TargetValidation Fail(string code, string message) =>
            new(false, string.Empty, new(), code, message);
    }
}
