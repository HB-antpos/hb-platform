using System.Globalization;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Services;

/// <summary>
/// Mobile 安卓原生「最低支持构建号」强制更新策略。
/// 写入沿用 iOS 原生策略的事务 applock、幂等与乐观锁；判定只给出 none / required，
/// 可选更新仍由匿名 android-latest 流程负责。
/// </summary>
public sealed class MobileAndroidNativeUpdatePolicyService(
    ISqlSugarClient db,
    IMobileAppBuildService mobileAppBuildService,
    ILogger<MobileAndroidNativeUpdatePolicyService> logger,
    IOptions<MobileAppBuildOptions>? mobileAppBuildOptions = null
) : IMobileAndroidNativeUpdatePolicyService
{
    private const string PolicyKey = AppUpdateApps.MobileAndroid;
    private const string MutationLockResource = "app-update-policy:native:mobile-android";
    private const string PublicProfile = "production";
    private const int ReleaseMessageMaxLength = 1000;

    private readonly MobileAppBuildOptions _mobileAppBuildOptions =
        mobileAppBuildOptions?.Value ?? new MobileAppBuildOptions();

    public async Task<ApiResponse<MobileAndroidNativeUpdatePolicyDto>> GetPolicyAsync()
    {
        var policy = await LoadPolicyAsync();
        var latest = await LoadLatestPublicBuildAsync();
        return ApiResponse<MobileAndroidNativeUpdatePolicyDto>.OK(MapPolicy(policy, latest));
    }

    public async Task<ApiResponse<MobileAndroidNativeUpdatePolicyDto>> SetPolicyAsync(
        MobileAndroidNativeUpdatePolicyRequest request,
        string currentUser
    )
    {
        if (!request.ExpectedPolicyVersion.HasValue)
        {
            var current = await LoadPolicyAsync();
            return PolicyVersionError(
                AppUpdatePolicyErrorCodes.VersionRequired,
                request.ExpectedPolicyVersion,
                current?.PolicyVersion ?? 0
            );
        }

        var releaseMessage = NormalizeOptional(request.ReleaseMessage);
        if (releaseMessage?.Length > ReleaseMessageMaxLength)
        {
            return ValidationError(
                MobileAndroidNativeUpdatePolicyErrorCodes.ReleaseMessageTooLong,
                "更新说明不能超过 1000 个字符"
            );
        }

        if (request.Enabled && request.MinimumSupportedBuildNumber is null)
        {
            return ValidationError(
                MobileAndroidNativeUpdatePolicyErrorCodes.MinimumBuildRequired,
                "启用强制更新必须填写最低支持构建号"
            );
        }

        if (request.MinimumSupportedBuildNumber < 1)
        {
            return ValidationError(
                MobileAndroidNativeUpdatePolicyErrorCodes.MinimumBuildInvalid,
                "最低支持构建号必须是正整数"
            );
        }

        var latest = await LoadLatestPublicBuildAsync();
        if (
            request.Enabled
            && (latest is null || request.MinimumSupportedBuildNumber > latest.AppBuildVersion)
        )
        {
            // 关键防线：最低构建号不能高于当前可安装的公开包，否则所有设备会被锁死在一个装不上的版本。
            return ValidationError(
                MobileAndroidNativeUpdatePolicyErrorCodes.MinimumBuildAboveLatest,
                latest is null
                    ? "当前没有可公开安装的 production 安卓包，无法启用强制更新"
                    : $"最低支持构建号不能高于当前公开安装包的构建号 {latest.AppBuildVersion}"
            );
        }

        // 停用时清空最低构建号与说明，避免残留值在下次启用时被误用。
        var minimumBuildNumber = request.Enabled ? request.MinimumSupportedBuildNumber : null;
        var savedReleaseMessage = request.Enabled ? releaseMessage : null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            MobileAndroidNativeUpdatePolicyDto? saved = null;
            ApiResponse<MobileAndroidNativeUpdatePolicyDto>? mutationError = null;
            var transaction = await db.Ado.UseTranAsync(async () =>
            {
                await AppUpdatePolicyMutationLock.AcquireAsync(db, MutationLockResource);
                var existing = await LoadPolicyAsync();
                var actualPolicyVersion = existing?.PolicyVersion ?? 0;

                // 内容与库内相同（含"无行且提交停用"）时幂等返回，不推进 PolicyVersion。
                if (
                    existing is null
                        ? !request.Enabled
                        : IsSamePolicy(existing, request.Enabled, minimumBuildNumber, savedReleaseMessage)
                )
                {
                    saved = MapPolicy(existing, latest);
                    return;
                }

                if (request.ExpectedPolicyVersion.Value != actualPolicyVersion)
                {
                    mutationError = PolicyVersionError(
                        AppUpdatePolicyErrorCodes.VersionConflict,
                        request.ExpectedPolicyVersion,
                        actualPolicyVersion
                    );
                    return;
                }

                var now = DateTime.UtcNow;
                var user = NormalizeUser(currentUser);
                var entity = existing ?? new MobileAndroidNativeUpdatePolicy
                {
                    Id = Guid.NewGuid(),
                    PolicyKey = PolicyKey,
                    CreatedAt = now,
                    CreatedBy = user,
                    IsDeleted = false,
                };
                entity.Enabled = request.Enabled;
                entity.MinimumSupportedBuildNumber = minimumBuildNumber;
                entity.ReleaseMessage = savedReleaseMessage;
                entity.PolicyVersion = actualPolicyVersion + 1;
                entity.UpdatedAt = now;
                entity.UpdatedBy = user;

                if (existing is null)
                {
                    await db.Insertable(entity).ExecuteCommandAsync();
                }
                else
                {
                    await db.Updateable(entity).ExecuteCommandAsync();
                }

                saved = MapPolicy(entity, latest);
            });

            if (mutationError is not null)
            {
                return mutationError;
            }

            if (transaction.IsSuccess && saved is not null)
            {
                return ApiResponse<MobileAndroidNativeUpdatePolicyDto>.OK(saved);
            }

            // 首次插入时两个实例可能同时撞唯一索引；锁内重读一次即可收敛到幂等或版本冲突。
            if (
                attempt == 0
                && AppUpdatePolicyMutationLock.IsUniqueConflict(transaction.ErrorException)
            )
            {
                logger.LogInformation(
                    transaction.ErrorException,
                    "Mobile 安卓原生策略首次并发写入冲突，锁内重读后重试"
                );
                continue;
            }

            logger.LogError(transaction.ErrorException, "Mobile 安卓原生策略事务保存失败");
            return ApiResponse<MobileAndroidNativeUpdatePolicyDto>.Error(
                "Mobile 安卓原生策略保存失败",
                MobileAndroidNativeUpdatePolicyErrorCodes.SaveFailed
            );
        }

        throw new InvalidOperationException("Mobile 安卓原生策略重试状态无效");
    }

    public async Task<MobileAndroidNativeUpdateDecisionDto> GetDecisionAsync(string? build)
    {
        var policy = await LoadPolicyAsync();
        if (policy is null)
        {
            // 无策略：policyVersion = "none"，其余字段全部 null。
            return new MobileAndroidNativeUpdateDecisionDto();
        }

        var latest = await LoadLatestPublicBuildAsync();
        var decision = new MobileAndroidNativeUpdateDecisionDto
        {
            State = AppUpdateStates.None,
            PolicyVersion = policy.PolicyVersion.ToString(CultureInfo.InvariantCulture),
            MinimumSupportedBuildNumber = policy.MinimumSupportedBuildNumber,
            LatestVersion = latest?.AppVersion,
            LatestBuildNumber = latest?.AppBuildVersion,
            ReleaseMessage = policy.ReleaseMessage,
        };

        // required 必须同时满足：启用、有最低构建号、设备构建号合法且更低、公开包已达到最低构建号。
        // 其余一律 none（含 build 缺失或非法的 fail-open），保证不会把设备拦在一个装不上的版本前。
        if (
            policy.Enabled
            && policy.MinimumSupportedBuildNumber is { } minimum
            && TryParsePositiveInt(build, out var installedBuild)
            && installedBuild < minimum
            && latest is not null
            && latest.AppBuildVersion >= minimum
        )
        {
            decision.State = AppUpdateStates.Required;
        }

        return decision;
    }

    private async Task<MobileAndroidNativeUpdatePolicy?> LoadPolicyAsync() =>
        await db.Queryable<MobileAndroidNativeUpdatePolicy>()
            .FirstAsync(item => item.PolicyKey == PolicyKey && !item.IsDeleted);

    /// <summary>
    /// 复用匿名 android-latest（profile=production、integrity=sha256-v1）的同一筛选：
    /// 自动更新开关关闭时视为没有公开包；versionCode 无法解析为正整数时同样视为不可用。
    /// </summary>
    private async Task<MobileAndroidLatestBuildDto?> LoadLatestPublicBuildAsync()
    {
        if (!_mobileAppBuildOptions.PublicAndroidUpdatesEnabled)
        {
            return null;
        }

        var result = await mobileAppBuildService.GetLatestPublicMobileAndroidAsync(PublicProfile);
        if (!result.Success || result.Data is null)
        {
            return null;
        }

        if (!TryParsePositiveInt(result.Data.AppBuildVersion, out var versionCode))
        {
            logger.LogWarning(
                "公开安卓包 versionCode 无法解析为正整数，EasBuildId: {EasBuildId}, AppBuildVersion: {AppBuildVersion}",
                result.Data.EasBuildId,
                result.Data.AppBuildVersion
            );
            return null;
        }

        return new MobileAndroidLatestBuildDto
        {
            EasBuildId = result.Data.EasBuildId,
            AppVersion = result.Data.AppVersion,
            AppBuildVersion = versionCode,
            CompletedAt = AsUtc(result.Data.CompletedAt),
        };
    }

    private static MobileAndroidNativeUpdatePolicyDto MapPolicy(
        MobileAndroidNativeUpdatePolicy? policy,
        MobileAndroidLatestBuildDto? latest
    ) =>
        policy is null
            ? new MobileAndroidNativeUpdatePolicyDto { LatestBuild = latest }
            : new MobileAndroidNativeUpdatePolicyDto
            {
                Enabled = policy.Enabled,
                MinimumSupportedBuildNumber = policy.MinimumSupportedBuildNumber,
                ReleaseMessage = policy.ReleaseMessage,
                PolicyVersion = policy.PolicyVersion,
                UpdatedAt = AsUtc(policy.UpdatedAt),
                UpdatedBy = policy.UpdatedBy,
                LatestBuild = latest,
            };

    private static bool IsSamePolicy(
        MobileAndroidNativeUpdatePolicy existing,
        bool enabled,
        int? minimumBuildNumber,
        string? releaseMessage
    ) =>
        existing.Enabled == enabled
        && existing.MinimumSupportedBuildNumber == minimumBuildNumber
        && string.Equals(existing.ReleaseMessage, releaseMessage, StringComparison.Ordinal);

    /// <summary>
    /// 只接受纯数字且大于 0 的 Int32；前后空白、符号、小数、溢出都视为非法。
    /// </summary>
    private static bool TryParsePositiveInt(string? value, out int parsed) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
        && parsed > 0;

    // 构建完成时间与策略更新时间都按 UTC 写入，读回后补上 Kind 让 JSON 带 Z。
    private static DateTime? AsUtc(DateTime? value) =>
        value is { Kind: DateTimeKind.Unspecified } unspecified
            ? DateTime.SpecifyKind(unspecified, DateTimeKind.Utc)
            : value?.ToUniversalTime();

    private static ApiResponse<MobileAndroidNativeUpdatePolicyDto> ValidationError(
        string errorCode,
        string message
    ) => ApiResponse<MobileAndroidNativeUpdatePolicyDto>.Error(message, errorCode);

    private static ApiResponse<MobileAndroidNativeUpdatePolicyDto> PolicyVersionError(
        string errorCode,
        long? expectedPolicyVersion,
        long actualPolicyVersion
    ) =>
        ApiResponse<MobileAndroidNativeUpdatePolicyDto>.Error(
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

    private static string NormalizeUser(string? value) => NormalizeOptional(value) ?? "System";

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
