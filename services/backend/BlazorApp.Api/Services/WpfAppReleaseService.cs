using System.Text.RegularExpressions;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    public class WpfAppReleaseService : IWpfAppReleaseService
    {
        private const long MaxInstallerFileBytes = 512L * 1024 * 1024;

        private static readonly HashSet<string> ReservedWindowsFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
        };

        private static readonly Regex VersionRegex = new(
            "^v?(?<major>\\d+)\\.(?<minor>\\d+)\\.(?<patch>\\d+)(?:\\.(?<build>\\d+))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        private static readonly Regex Sha256Regex = new(
            "^[a-f0-9]{64}$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        private readonly ISqlSugarClient _db;
        private readonly ISqlSugarClient? _posmDb;
        private readonly TencentCloudUploadService? _uploadService;
        private readonly ILogger<WpfAppReleaseService> _logger;

        public WpfAppReleaseService(
            ISqlSugarClient db,
            ILogger<WpfAppReleaseService> logger,
            ISqlSugarClient? posmDb = null
        )
        {
            _db = db;
            _logger = logger;
            _posmDb = posmDb;
        }

        public WpfAppReleaseService(
            ISqlSugarClient db,
            TencentCloudUploadService uploadService,
            ILogger<WpfAppReleaseService> logger,
            ISqlSugarClient? posmDb = null
        )
        {
            _db = db;
            _uploadService = uploadService;
            _logger = logger;
            _posmDb = posmDb;
        }

        public async Task<ApiResponse<PagedResult<WpfAppReleaseDto>>> GetReleasesAsync(
            WpfAppReleaseQuery query
        )
        {
            var page = Math.Max(query.Page, 1);
            var pageSize = Math.Clamp(query.PageSize, 1, 100);
            var channel = NormalizeChannel(query.Channel);
            var queryable = _db.Queryable<WpfAppRelease>().Where(x => x.Channel == channel && !x.IsDeleted);

            if (!query.IncludeDisabled)
            {
                queryable = queryable.Where(x => x.IsActive);
            }

            var total = await queryable.CountAsync();
            var items = await queryable
                .OrderByDescending(x => x.PublishedAt)
                .OrderByDescending(x => x.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
            var policy = await _db.Queryable<WpfUpdatePolicy>().FirstAsync(x => x.Channel == channel && !x.IsDeleted);
            var policyTargets = policy == null
                ? new List<WpfUpdatePolicyTarget>()
                : await _db.Queryable<WpfUpdatePolicyTarget>()
                    .Where(x => x.PolicyId == policy.Id && !x.IsDeleted)
                    .ToListAsync();
            var targetSummaries = await GetTargetSummariesAsync(policy?.TargetScope, policyTargets);

            return ApiResponse<PagedResult<WpfAppReleaseDto>>.OK(
                new PagedResult<WpfAppReleaseDto>
                {
                    Items = items
                        .Select(item => MapRelease(item, policy, policyTargets, targetSummaries))
                        .ToList(),
                    Total = total,
                    Page = page,
                    PageSize = pageSize,
                }
            );
        }

        public async Task<ApiResponse<WpfAppReleaseDto>> CreateReleaseAsync(
            WpfAppReleaseCreateRequest request,
            string currentUser
        )
        {
            var channel = NormalizeChannel(request.Channel);
            var fileName = NormalizeRequired(request.FileName);
            if (!TryNormalizeVersion(request.Version, out var version))
            {
                return ApiResponse<WpfAppReleaseDto>.Error(
                    "Version must match v?major.minor.patch[.build].",
                    "INVALID_VERSION"
                );
            }

            if (string.IsNullOrWhiteSpace(fileName) || request.FileSize <= 0)
            {
                return ApiResponse<WpfAppReleaseDto>.Error(
                    "File name and file size are required.",
                    "INVALID_RELEASE_FILE"
                );
            }

            if (request.FileSize > MaxInstallerFileBytes)
            {
                // 后端必须和客户端保持同一 512MB 上限，避免登记无法分发的安装包。
                return ApiResponse<WpfAppReleaseDto>.Error(
                    "Installer file must not exceed 512MB.",
                    "FILE_TOO_LARGE"
                );
            }

            if (
                !TryResolveInstallerTypeForCreate(
                    fileName,
                    request.InstallerType,
                    out var installerType
                )
            )
            {
                return ApiResponse<WpfAppReleaseDto>.Error(
                    "Only .exe and .msi installers are supported.",
                    "INVALID_INSTALLER_FILE"
                );
            }

            if (!TryNormalizeSha256(request.Sha256, out var sha256))
            {
                return ApiResponse<WpfAppReleaseDto>.Error(
                    "SHA256 must be a 64 character hex string.",
                    "INVALID_SHA256"
                );
            }

            // 发布记录统一按规范化版本生成对象路径，避免 v1.2.3 和 1.2.3 分裂。
            var objectKey = BuildCosObjectKey(channel, version, fileName);
            if (!TryNormalizeReleaseDownloadUrl(request.DownloadUrl, objectKey, out var downloadUrl))
            {
                return ApiResponse<WpfAppReleaseDto>.Error(
                    "Download URL must be absolute HTTPS, except local loopback for debugging.",
                    "INVALID_DOWNLOAD_URL"
                );
            }

            var existing = await _db
                .Queryable<WpfAppRelease>()
                .Where(x => x.Channel == channel)
                .ToListAsync();
            if (existing.Any(x => VersionsEqual(x.Version, version)))
            {
                return ApiResponse<WpfAppReleaseDto>.Error(
                    "WPF release version already exists.",
                    "WPF_RELEASE_EXISTS"
                );
            }

            if (request.IsActive)
            {
                // 中文注释：启用中的 COS 发布必须先确认对象存在且元数据一致，避免把空对象直接暴露给策略和客户端。
                var artifactValidationError = await ValidateManagedCosArtifactAsync(
                    downloadUrl,
                    objectKey,
                    request.FileSize,
                    sha256
                );
                if (artifactValidationError != null)
                {
                    return ApiResponse<WpfAppReleaseDto>.Error(
                        artifactValidationError.Value.Message,
                        artifactValidationError.Value.Code,
                        artifactValidationError.Value.Details
                    );
                }
            }

            var entity = new WpfAppRelease
            {
                Id = Guid.NewGuid(),
                Channel = channel,
                Version = version,
                FileName = fileName,
                FileSize = request.FileSize,
                Sha256 = sha256,
                DownloadUrl = downloadUrl,
                CosObjectKey = objectKey,
                InstallerType = installerType,
                InstallerArguments = NormalizeOptional(request.InstallerArguments),
                ReleaseNotes = NormalizeOptional(request.ReleaseNotes),
                IsActive = request.IsActive,
                PublishedAt = request.PublishedAt ?? DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = currentUser,
            };

            await _db.Insertable(entity).ExecuteCommandAsync();
            return ApiResponse<WpfAppReleaseDto>.OK(MapRelease(entity));
        }

        public async Task<ApiResponse<WpfAppReleaseDto>> UpdateReleaseAsync(
            Guid id,
            WpfAppReleaseUpdateRequest request,
            string currentUser
        )
        {
            var entity = await _db.Queryable<WpfAppRelease>().FirstAsync(x => x.Id == id && !x.IsDeleted);
            if (entity == null)
            {
                return ApiResponse<WpfAppReleaseDto>.Error(
                    "WPF release does not exist.",
                    "WPF_RELEASE_NOT_FOUND"
                );
            }

            var policy = await _db
                .Queryable<WpfUpdatePolicy>()
                .FirstAsync(x => x.Channel == entity.Channel && !x.IsDeleted);
            if (request.DownloadUrl != null)
            {
                var objectKey = NormalizeOptional(entity.CosObjectKey)
                    ?? BuildCosObjectKey(entity.Channel, entity.Version, entity.FileName);
                if (!TryNormalizeReleaseDownloadUrl(request.DownloadUrl, objectKey, out var downloadUrl))
                {
                    return ApiResponse<WpfAppReleaseDto>.Error(
                        "Download URL must be absolute HTTPS, except local loopback for debugging.",
                        "INVALID_DOWNLOAD_URL"
                    );
                }

                entity.DownloadUrl = downloadUrl;
            }

            if (request.Sha256 != null)
            {
                if (!TryNormalizeSha256(request.Sha256, out var sha256))
                {
                    return ApiResponse<WpfAppReleaseDto>.Error(
                        "SHA256 must be a 64 character hex string.",
                        "INVALID_SHA256"
                    );
                }

                entity.Sha256 = sha256;
            }

            if (request.InstallerType != null)
            {
                if (
                    !TryResolveInstallerTypeForExistingFile(
                        entity.FileName,
                        request.InstallerType,
                        out var installerType
                    )
                )
                {
                    return ApiResponse<WpfAppReleaseDto>.Error(
                        "Only .exe and .msi installers are supported.",
                        "INVALID_INSTALLER_FILE"
                    );
                }

                entity.InstallerType = installerType;
            }

            if (request.InstallerArguments != null)
            {
                entity.InstallerArguments = NormalizeOptional(request.InstallerArguments);
            }

            if (request.ReleaseNotes != null)
            {
                entity.ReleaseNotes = NormalizeOptional(request.ReleaseNotes);
            }

            if (request.IsActive.HasValue)
            {
                if (entity.IsActive && !request.IsActive.Value)
                {
                    if (
                        policy != null
                        && (
                            VersionsEqual(policy.TargetVersion, entity.Version)
                            || VersionsEqual(policy.MinimumSupportedVersion, entity.Version)
                        )
                    )
                    {
                        // 被策略引用的激活版本不能直接禁用，否则检查接口会失去有效目标。
                        return ApiResponse<WpfAppReleaseDto>.Error(
                            "The current release is still referenced by update policy.",
                            "WPF_RELEASE_REFERENCED_BY_POLICY"
                        );
                    }
                }
                else if (!entity.IsActive && request.IsActive.Value)
                {
                    var activeReleases = await _db
                        .Queryable<WpfAppRelease>()
                        .Where(x => x.Channel == entity.Channel && x.IsActive && !x.IsDeleted)
                        .ToListAsync();
                    if (
                        activeReleases.Any(
                            x => x.Id != entity.Id && VersionsEqual(x.Version, entity.Version)
                        )
                    )
                    {
                        // 恢复旧数据里的禁用重复版本前先做业务查重，避免直接撞上数据库唯一索引变成 500。
                        return ApiResponse<WpfAppReleaseDto>.Error(
                            "WPF release version already exists.",
                            "WPF_RELEASE_EXISTS"
                        );
                    }
                }

                entity.IsActive = request.IsActive.Value;
            }

            var shouldValidateManagedArtifact =
                entity.IsActive
                && (
                    request.DownloadUrl != null
                    || request.Sha256 != null
                    || request.IsActive == true
                );
            if (shouldValidateManagedArtifact)
            {
                var objectKey = NormalizeOptional(entity.CosObjectKey)
                    ?? BuildCosObjectKey(entity.Channel, entity.Version, entity.FileName);
                // 中文注释：更新后只要版本重新处于启用态，且本次改动触碰了可下载工件元数据，就按最终落库值重新校验；
                // 对非当前托管 COS 下载地址，这个校验会自动短路为 no-op，不会误伤本地 loopback 或外部调试地址。
                var artifactValidationError = await ValidateManagedCosArtifactAsync(
                    entity.DownloadUrl,
                    objectKey,
                    entity.FileSize,
                    entity.Sha256
                );
                if (artifactValidationError != null)
                {
                    return ApiResponse<WpfAppReleaseDto>.Error(
                        artifactValidationError.Value.Message,
                        artifactValidationError.Value.Code,
                        artifactValidationError.Value.Details
                    );
                }
            }

            entity.UpdatedAt = DateTime.UtcNow;
            entity.UpdatedBy = currentUser;
            await _db.Updateable(entity).ExecuteCommandAsync();
            return ApiResponse<WpfAppReleaseDto>.OK(MapRelease(entity));
        }

        public async Task<ApiResponse<WpfUpdatePolicyDto>> SetPolicyAsync(
            WpfUpdatePolicyRequest request,
            string currentUser
        )
        {
            var channel = NormalizeChannel(request.Channel);
            if (!TryNormalizeVersion(request.TargetVersion, out var targetVersion))
            {
                return ApiResponse<WpfUpdatePolicyDto>.Error(
                    "Version must match v?major.minor.patch[.build].",
                    "INVALID_VERSION"
                );
            }

            if (string.IsNullOrWhiteSpace(request.MinimumSupportedVersion))
            {
                // 更新策略必须带最低支持版本，否则直调接口会写入没有强更下限的发布策略。
                return ApiResponse<WpfUpdatePolicyDto>.Error(
                    "Minimum supported version is required.",
                    "MINIMUM_SUPPORTED_VERSION_REQUIRED"
                );
            }

            if (!TryNormalizeVersion(request.MinimumSupportedVersion, out var minimumVersion))
            {
                return ApiResponse<WpfUpdatePolicyDto>.Error(
                    "Version must match v?major.minor.patch[.build].",
                    "INVALID_VERSION"
                );
            }

            WpfVersion parsedMinimumVersion = default;
            var hasTargetVersion = TryParseVersion(targetVersion, out var parsedTargetVersion);
            var hasMinimumVersion = TryParseVersion(minimumVersion, out parsedMinimumVersion);
            if (!hasTargetVersion || !hasMinimumVersion)
            {
                return ApiResponse<WpfUpdatePolicyDto>.Error(
                    "Policy version must match v?major.minor.patch[.build].",
                    "INVALID_VERSION"
                );
            }

            if (parsedMinimumVersion.CompareTo(parsedTargetVersion) > 0)
            {
                // minimumSupportedVersion 高于 targetVersion 会让强更判断出现反常区间。
                return ApiResponse<WpfUpdatePolicyDto>.Error(
                    "Minimum supported version cannot be higher than target version.",
                    "INVALID_VERSION_RANGE"
                );
            }

            var target = await FindActiveReleaseAsync(channel, targetVersion);
            if (target == null)
            {
                return ApiResponse<WpfUpdatePolicyDto>.Error(
                    "Target release is not registered or is inactive.",
                    "TARGET_RELEASE_NOT_FOUND"
                );
            }

            var minimumRelease = await FindActiveReleaseAsync(channel, minimumVersion);
            if (minimumRelease == null)
            {
                // minimumSupportedVersion 不能引用不存在或已禁用的版本，否则强更下限会和实际可下载版本脱节。
                return ApiResponse<WpfUpdatePolicyDto>.Error(
                    "Minimum supported release is not registered or is inactive.",
                    "MINIMUM_SUPPORTED_RELEASE_NOT_FOUND"
                );
            }

            var targetObjectKey = NormalizeOptional(target.CosObjectKey)
                ?? BuildCosObjectKey(target.Channel, target.Version, target.FileName);
            var targetArtifactValidationError = await ValidateManagedCosArtifactAsync(
                target.DownloadUrl,
                targetObjectKey,
                target.FileSize,
                target.Sha256
            );
            if (targetArtifactValidationError != null)
            {
                return ApiResponse<WpfUpdatePolicyDto>.Error(
                    targetArtifactValidationError.Value.Message,
                    targetArtifactValidationError.Value.Code,
                    targetArtifactValidationError.Value.Details
                );
            }

            var existing = await _db
                .Queryable<WpfUpdatePolicy>()
                .FirstAsync(x => x.Channel == channel && !x.IsDeleted);
            if (!request.RollbackConfirmed && IsRollbackPolicyChange(targetVersion, existing))
            {
                // 现有策略向低版本回退必须二次确认，避免误触发终端降级。
                return ApiResponse<WpfUpdatePolicyDto>.Error(
                    "Rollback confirmation is required.",
                    "ROLLBACK_CONFIRMATION_REQUIRED"
                );
            }

            // 软删除策略不参与读取和回滚判断；写入时复用旧行，避免 Channel 唯一索引冲突。
            var deletedExisting = existing == null
                ? await _db
                    .Queryable<WpfUpdatePolicy>()
                    .FirstAsync(x => x.Channel == channel && x.IsDeleted)
                : null;
            var now = DateTime.UtcNow;
            var isNewPolicy = existing == null && deletedExisting == null;
            var entity =
                existing ?? deletedExisting ?? new WpfUpdatePolicy { Id = Guid.NewGuid(), CreatedAt = now };
            entity.Channel = channel;
            entity.TargetVersion = targetVersion;
            entity.MinimumSupportedVersion = minimumVersion;
            entity.ForceUpdate = request.ForceUpdate;
            entity.IsDeleted = false;
            entity.UpdatedAt = isNewPolicy ? null : now;
            entity.UpdatedBy = currentUser;

            if (isNewPolicy)
            {
                entity.CreatedBy = currentUser;
                await _db.Insertable(entity).ExecuteCommandAsync();
            }
            else
            {
                await _db.Updateable(entity).ExecuteCommandAsync();
            }

            return ApiResponse<WpfUpdatePolicyDto>.OK(MapPolicy(entity));
        }

        /// <summary>
        /// 在同一事务中保存渠道策略及其定向目标。旧 SetPolicyAsync 保留给既有调用方，避免改变其高影响语义。
        /// </summary>
        public async Task<ApiResponse<WpfUpdatePolicyDto>> SetTargetedPolicyAsync(
            WpfUpdatePolicyRequest request,
            string currentUser
        )
        {
            if (!TryBuildPolicyTargets(request, out var targetScope, out var targets, out var errorCode))
            {
                return ApiResponse<WpfUpdatePolicyDto>.Error(
                    "Target scope or target list is invalid.",
                    errorCode
                );
            }

            ApiResponse<WpfUpdatePolicyDto>? policyResult = null;
            var transaction = await _db.Ado.UseTranAsync(async () =>
            {
                // 沿用既有策略版本、回退确认和工件校验逻辑，目标替换与策略行写入由同一数据库事务提交。
                policyResult = await SetPolicyAsync(request, currentUser);
                if (policyResult?.Success != true || policyResult.Data == null)
                {
                    return;
                }

                var policy = await _db
                    .Queryable<WpfUpdatePolicy>()
                    .SingleAsync(x => x.Id == policyResult.Data.Id);
                policy.TargetScope = targetScope;
                policy.UpdatedAt = DateTime.UtcNow;
                policy.UpdatedBy = currentUser;
                await _db.Updateable(policy).ExecuteCommandAsync();

                await _db
                    .Deleteable<WpfUpdatePolicyTarget>()
                    .Where(x => x.PolicyId == policy.Id)
                    .ExecuteCommandAsync();
                if (targets.Count > 0)
                {
                    foreach (var target in targets)
                    {
                        target.PolicyId = policy.Id;
                        target.CreatedAt = DateTime.UtcNow;
                        target.CreatedBy = currentUser;
                    }

                    await _db.Insertable(targets).ExecuteCommandAsync();
                }

                policyResult = ApiResponse<WpfUpdatePolicyDto>.OK(MapPolicy(policy, targets));
            });

            if (policyResult?.Success != true)
            {
                return policyResult
                    ?? ApiResponse<WpfUpdatePolicyDto>.Error(
                        "WPF update policy could not be saved.",
                        "WPF_UPDATE_POLICY_SAVE_FAILED"
                    );
            }

            if (!transaction.IsSuccess)
            {
                _logger.LogError(transaction.ErrorException, "WPF 定向更新策略事务保存失败");
                return ApiResponse<WpfUpdatePolicyDto>.Error(
                    "WPF update policy could not be saved.",
                    "WPF_UPDATE_POLICY_SAVE_FAILED"
                );
            }

            var savedPolicy = policyResult.Data;
            if (savedPolicy == null)
            {
                return ApiResponse<WpfUpdatePolicyDto>.Error(
                    "WPF update policy could not be saved.",
                    "WPF_UPDATE_POLICY_SAVE_FAILED"
                );
            }

            try
            {
                var savedTargets = await _db.Queryable<WpfUpdatePolicyTarget>()
                    .Where(target => target.PolicyId == savedPolicy.Id && !target.IsDeleted)
                    .ToListAsync();
                ApplyTargetSummaries(
                    savedPolicy,
                    await GetTargetSummariesAsync(savedPolicy.TargetScope, savedTargets)
                );
            }
            catch (Exception)
            {
                // 策略与目标已在事务内提交，摘要读取失败只降级展示，不能把成功保存误报为失败。
                _logger.LogWarning(
                    "WPF 定向更新策略已保存，但目标摘要查询失败；策略 ID：{PolicyId}",
                    savedPolicy.Id
                );
            }

            return policyResult;
        }

        /// <summary>
        /// 读取指定渠道当前更新策略的目标版本与放量范围，用于设备列表判断「待升级 / 未放量」。
        /// 只读；渠道没有策略时返回 null。分店范围按当前有效分店的 StoreGUID 解析成分店代码，
        /// 与 <see cref="DoesPolicyTargetMatchAsync"/> 的口径一致。
        /// </summary>
        public async Task<WpfPolicyTargetSnapshot?> GetPolicyTargetSnapshotAsync(string? channel = null)
        {
            var normalizedChannel = NormalizeChannel(channel);
            var policy = await _db
                .Queryable<WpfUpdatePolicy>()
                .FirstAsync(x => x.Channel == normalizedChannel && !x.IsDeleted);
            if (policy == null || string.IsNullOrWhiteSpace(policy.TargetVersion))
            {
                return null;
            }

            var scope = NormalizeTargetScopeOrAll(policy.TargetScope);
            var targetVersion = NormalizeVersionOrOriginal(policy.TargetVersion);
            if (scope == "all")
            {
                return new WpfPolicyTargetSnapshot(targetVersion, scope);
            }

            var targets = await _db
                .Queryable<WpfUpdatePolicyTarget>()
                .Where(x => x.PolicyId == policy.Id && !x.IsDeleted)
                .ToListAsync();
            if (scope == "devices")
            {
                return new WpfPolicyTargetSnapshot(
                    targetVersion,
                    scope,
                    deviceIds: targets
                        .Select(target => target.DeviceRegistrationId ?? 0)
                        .Where(id => id > 0)
                );
            }

            var targetGuids = targets
                .Select(target => target.StoreGuid)
                .Where(guid => !string.IsNullOrWhiteSpace(guid))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stores = await _db
                .Queryable<Store>()
                .Where(store => store.IsActive && !store.IsDeleted)
                .ToListAsync();
            return new WpfPolicyTargetSnapshot(
                targetVersion,
                scope,
                storeCodes: stores
                    .Where(store => targetGuids.Contains(store.StoreGUID))
                    .Select(store => store.StoreCode)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
            );
        }

        /// <summary>
        /// 按严格认证出的设备身份执行定向检查；定向策略在身份缺失、无效或不匹配时安全地返回无更新。
        /// </summary>
        public async Task<ApiResponse<WpfUpdateCheckResponse>> CheckTargetedUpdateAsync(
            string? channel,
            string? currentVersion,
            WpfUpdateCheckDeviceIdentity? deviceIdentity
        )
        {
            var normalizedChannel = NormalizeChannel(channel);
            if (
                !TryNormalizeVersion(currentVersion, out var normalizedCurrent)
                || !TryParseVersion(normalizedCurrent, out _)
            )
            {
                return await CheckUpdateAsync(channel, currentVersion);
            }

            var policy = await _db
                .Queryable<WpfUpdatePolicy>()
                .FirstAsync(x => x.Channel == normalizedChannel && !x.IsDeleted);
            if (policy == null || IsAllTargetScope(policy.TargetScope))
            {
                return await CheckUpdateAsync(channel, currentVersion);
            }

            if (deviceIdentity == null || !await DoesPolicyTargetMatchAsync(policy, deviceIdentity))
            {
                return ApiResponse<WpfUpdateCheckResponse>.OK(
                    new WpfUpdateCheckResponse { CurrentVersion = normalizedCurrent }
                );
            }

            return await CheckUpdateAsync(channel, currentVersion);
        }

        public async Task<ApiResponse<WpfUpdateCheckResponse>> CheckUpdateAsync(
            string? channel,
            string? currentVersion
        )
        {
            var normalizedChannel = NormalizeChannel(channel);
            if (
                !TryNormalizeVersion(currentVersion, out var normalizedCurrent)
                || !TryParseVersion(normalizedCurrent, out var currentParsed)
            )
            {
                return ApiResponse<WpfUpdateCheckResponse>.Error(
                    "Current WPF version cannot be parsed.",
                    "INVALID_VERSION"
                );
            }

            var policy = await _db
                .Queryable<WpfUpdatePolicy>()
                .FirstAsync(x => x.Channel == normalizedChannel && !x.IsDeleted);
            if (policy == null)
            {
                return ApiResponse<WpfUpdateCheckResponse>.OK(
                    new WpfUpdateCheckResponse { CurrentVersion = normalizedCurrent }
                );
            }

            var policyTargetVersion = NormalizeVersionOrOriginal(policy.TargetVersion);
            var policyMinimumVersion = NormalizeOptionalVersionOrOriginal(
                policy.MinimumSupportedVersion
            );
            if (
                !TryParseVersion(policyTargetVersion, out var targetParsed)
                || (
                    !string.IsNullOrWhiteSpace(policyMinimumVersion)
                    && !TryParseVersion(policyMinimumVersion, out _)
                )
            )
            {
                return ApiResponse<WpfUpdateCheckResponse>.Error(
                    "Stored policy version cannot be parsed.",
                    "INVALID_VERSION"
                );
            }

            var target = await FindActiveReleaseAsync(normalizedChannel, policyTargetVersion);
            if (target == null)
            {
                return ApiResponse<WpfUpdateCheckResponse>.Error(
                    "Target release is not registered or is inactive.",
                    "TARGET_RELEASE_NOT_FOUND"
                );
            }

            var comparison = currentParsed.CompareTo(targetParsed);
            var updateAvailable = comparison != 0;
            var forceByMinimum = false;
            if (
                !string.IsNullOrWhiteSpace(policyMinimumVersion)
                && TryParseVersion(policyMinimumVersion, out var minimumParsed)
            )
            {
                forceByMinimum = currentParsed.CompareTo(minimumParsed) < 0;
            }

            // 只有在版本号可解析且和目标不同的情况下才判断更新/回退。
            var response = new WpfUpdateCheckResponse
            {
                UpdateAvailable = updateAvailable,
                ForceUpdate = updateAvailable && (policy.ForceUpdate || forceByMinimum),
                IsRollback = comparison > 0,
                CurrentVersion = normalizedCurrent,
                TargetVersion = policyTargetVersion,
                MinimumSupportedVersion = policyMinimumVersion,
                DownloadUrl = updateAvailable ? target.DownloadUrl : null,
                FileName = updateAvailable ? target.FileName : null,
                FileSize = updateAvailable ? target.FileSize : null,
                Sha256 = updateAvailable ? target.Sha256 : null,
                InstallerType = updateAvailable ? target.InstallerType : null,
                InstallerArguments = updateAvailable ? target.InstallerArguments : null,
                ReleaseNotes = updateAvailable ? target.ReleaseNotes : null,
            };

            return ApiResponse<WpfUpdateCheckResponse>.OK(response);
        }

        public async Task<ApiResponse<WpfAppReleaseUploadInitResponse>> CreateUploadInitAsync(
            WpfAppReleaseUploadInitRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (_uploadService == null)
            {
                _logger.LogWarning("WPF upload init failed because COS upload service is missing.");
                return ApiResponse<WpfAppReleaseUploadInitResponse>.Error(
                    "COS upload service is not configured.",
                    "COS_UPLOAD_SERVICE_NOT_CONFIGURED"
                );
            }

            if (!_uploadService.HasRequiredConfiguration())
            {
                _logger.LogWarning("WPF upload init failed because COS settings are incomplete.");
                return ApiResponse<WpfAppReleaseUploadInitResponse>.Error(
                    "COS upload service is not configured.",
                    "COS_UPLOAD_SERVICE_NOT_CONFIGURED"
                );
            }

            var channel = NormalizeChannel(request.Channel);
            var fileName = NormalizeRequired(request.FileName);
            if (!TryNormalizeVersion(request.Version, out var version))
            {
                return ApiResponse<WpfAppReleaseUploadInitResponse>.Error(
                    "Version must match v?major.minor.patch[.build].",
                    "INVALID_VERSION"
                );
            }

            if (string.IsNullOrWhiteSpace(fileName) || request.FileSize <= 0)
            {
                return ApiResponse<WpfAppReleaseUploadInitResponse>.Error(
                    "File name and file size are required.",
                    "INVALID_RELEASE_FILE"
                );
            }

            if (request.FileSize > MaxInstallerFileBytes)
            {
                return ApiResponse<WpfAppReleaseUploadInitResponse>.Error(
                    "Installer file must not exceed 512MB.",
                    "FILE_TOO_LARGE"
                );
            }

            if (!TryNormalizeSha256(request.Sha256, out var sha256))
            {
                // 中文注释：上传签名必须绑定页面计算出的 SHA256，否则后续 COS 元数据校验无法闭环。
                return ApiResponse<WpfAppReleaseUploadInitResponse>.Error(
                    "SHA256 must be a 64-character hex string.",
                    "INVALID_SHA256"
                );
            }

            if (!TryResolveInstallerTypeFromFileName(fileName, out _))
            {
                return ApiResponse<WpfAppReleaseUploadInitResponse>.Error(
                    "Only .exe and .msi installers are supported.",
                    "INVALID_INSTALLER_FILE"
                );
            }

            var existingReleases = await _db
                .Queryable<WpfAppRelease>()
                .Where(x => x.Channel == channel)
                .ToListAsync();
            if (existingReleases.Any(x => VersionsEqual(x.Version, version)))
            {
                // 上传初始化也必须按规范化版本去重，避免先覆盖 COS 再在创建记录时报冲突。
                return ApiResponse<WpfAppReleaseUploadInitResponse>.Error(
                    "WPF release version already exists.",
                    "WPF_RELEASE_EXISTS"
                );
            }

            var contentType = string.IsNullOrWhiteSpace(request.ContentType)
                ? "application/octet-stream"
                : request.ContentType.Trim();
            var objectKey = BuildCosObjectKey(channel, version, fileName);

            if (request.Multipart)
            {
                // WPF release 目前只定义了直传 + 登记的完整合同；multipart 字段公开但未闭环，先明确拒绝避免返回半成品上传状态。
                return ApiResponse<WpfAppReleaseUploadInitResponse>.Error(
                    "WPF release multipart upload is not supported.",
                    "WPF_MULTIPART_UPLOAD_UNSUPPORTED"
                );
            }

            var uploadHeaders = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(sha256))
            {
                // 中文注释：直传阶段把页面计算出的 SHA256 写入 COS 元数据，并参与签名，后续登记时才能校验对象与发布记录同源。
                uploadHeaders["x-cos-meta-sha256"] = sha256.ToLowerInvariant();
            }

            var direct = _uploadService.GetDirectUploadSignature(
                objectKey,
                contentType,
                additionalHeaders: uploadHeaders
            );
            var downloadUrl = BuildPublicDownloadUrlFromSignedUrl(direct.Url);
            return ApiResponse<WpfAppReleaseUploadInitResponse>.OK(
                new WpfAppReleaseUploadInitResponse
                {
                    ObjectKey = objectKey,
                    FileName = Path.GetFileName(fileName),
                    DownloadUrl = downloadUrl,
                    DirectUpload = direct,
                }
            );
        }

        public static string BuildCosObjectKey(string? channel, string version, string fileName)
        {
            return
                $"wpf-releases/{NormalizeChannel(channel)}/{NormalizeVersionOrOriginal(version)}/{NormalizeFileName(fileName)}";
        }

        private async Task<(string Code, string Message, string? Details)?> ValidateManagedCosArtifactAsync(
            string downloadUrl,
            string objectKey,
            long expectedFileSize,
            string expectedSha256
        )
        {
            if (
                _uploadService == null
                || !_uploadService.TryMatchPublicDownloadUrl(downloadUrl, objectKey, out _)
            )
            {
                return null;
            }

            var metadataResult = await _uploadService.GetObjectMetadataAsync(objectKey);
            if (!metadataResult.Success || metadataResult.Data == null)
            {
                return (
                    metadataResult.Code ?? "COS_OBJECT_METADATA_UNAVAILABLE",
                    metadataResult.Message ?? "COS object metadata validation failed.",
                    metadataResult.Details?.ToString()
                );
            }

            if (metadataResult.Data.ContentLength != expectedFileSize)
            {
                return (
                    "COS_OBJECT_SIZE_MISMATCH",
                    "COS object size does not match the release metadata.",
                    $"expected={expectedFileSize}, actual={metadataResult.Data.ContentLength?.ToString() ?? "null"}"
                );
            }

            if (string.IsNullOrWhiteSpace(metadataResult.Data.Sha256))
            {
                return (
                    "COS_OBJECT_METADATA_UNAVAILABLE",
                    "COS object SHA256 metadata is missing.",
                    "x-cos-meta-sha256"
                );
            }

            if (
                !string.Equals(
                    metadataResult.Data.Sha256,
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return (
                    "COS_OBJECT_SHA256_MISMATCH",
                    "COS object SHA256 does not match the release metadata.",
                    $"expected={expectedSha256}, actual={metadataResult.Data.Sha256}"
                );
            }

            return null;
        }

        private async Task<WpfAppRelease?> FindActiveReleaseAsync(string channel, string version)
        {
            var releases = await _db
                .Queryable<WpfAppRelease>()
                .Where(x => x.Channel == channel && x.IsActive && !x.IsDeleted)
                .ToListAsync();
            return releases.FirstOrDefault(x => VersionsEqual(x.Version, version));
        }

        private static bool IsRollbackPolicyChange(string targetVersion, WpfUpdatePolicy? existingPolicy)
        {
            if (!TryParseVersion(targetVersion, out var targetParsed))
            {
                return false;
            }

            return existingPolicy is not null
                && TryParseVersion(existingPolicy.TargetVersion, out var existingParsed)
                && targetParsed.CompareTo(existingParsed) < 0;
        }

        private static WpfAppReleaseDto MapRelease(
            WpfAppRelease release,
            WpfUpdatePolicy? policy = null,
            IReadOnlyCollection<WpfUpdatePolicyTarget>? targets = null,
            PolicyTargetSummaries? targetSummaries = null
        )
        {
            var isCurrent = VersionsEqual(release.Version, policy?.TargetVersion);
            var isRollback =
                policy is not null
                && TryParseVersion(release.Version, out var releaseParsed)
                && TryParseVersion(policy.TargetVersion, out var targetParsed)
                && releaseParsed.CompareTo(targetParsed) < 0;

            return new WpfAppReleaseDto
            {
                Id = release.Id,
                Channel = release.Channel,
                Version = NormalizeVersionOrOriginal(release.Version),
                FileName = release.FileName,
                FileSize = release.FileSize,
                Sha256 = release.Sha256,
                DownloadUrl = release.DownloadUrl,
                CosObjectKey = release.CosObjectKey,
                InstallerType = release.InstallerType,
                InstallerArguments = release.InstallerArguments,
                ReleaseNotes = release.ReleaseNotes,
                IsActive = release.IsActive,
                IsCurrent = isCurrent,
                // 列表页把低于当前策略目标的版本标成回退候选，避免后台策略链路和前端标记脱节。
                IsRollback = isRollback,
                // 强更开关属于当前策略，不属于单个发布行；每行都带上策略值，分页没包含当前目标时前端仍能恢复表单状态。
                ForceUpdate = policy?.ForceUpdate == true,
                MinimumSupportedVersion = NormalizeOptionalVersionOrOriginal(
                    policy?.MinimumSupportedVersion
                ),
                TargetVersion = NormalizeOptionalVersionOrOriginal(policy?.TargetVersion),
                TargetScope = NormalizeTargetScopeOrAll(policy?.TargetScope),
                TargetStoreGuids = GetStoreTargetGuids(policy?.TargetScope, targets),
                TargetDeviceRegistrationIds = GetDeviceTargetIds(policy?.TargetScope, targets),
                TargetStoreSummaries = CloneStoreSummaries(targetSummaries?.Stores),
                TargetDeviceSummaries = CloneDeviceSummaries(targetSummaries?.Devices),
                PolicyUpdatedAt = policy == null ? null : policy.UpdatedAt ?? policy.CreatedAt,
                PolicyUpdatedBy = policy?.UpdatedBy ?? policy?.CreatedBy,
                PublishedAt = release.PublishedAt,
                CreatedAt = release.CreatedAt,
                UpdatedAt = release.UpdatedAt,
            };
        }

        private static WpfUpdatePolicyDto MapPolicy(
            WpfUpdatePolicy policy,
            IReadOnlyCollection<WpfUpdatePolicyTarget>? targets = null,
            PolicyTargetSummaries? targetSummaries = null
        )
        {
            return new WpfUpdatePolicyDto
            {
                Id = policy.Id,
                Channel = policy.Channel,
                TargetVersion = NormalizeVersionOrOriginal(policy.TargetVersion),
                MinimumSupportedVersion = NormalizeOptionalVersionOrOriginal(
                    policy.MinimumSupportedVersion
                ),
                ForceUpdate = policy.ForceUpdate,
                CreatedAt = policy.CreatedAt,
                UpdatedAt = policy.UpdatedAt,
                TargetScope = NormalizeTargetScopeOrAll(policy.TargetScope),
                TargetStoreGuids = GetStoreTargetGuids(policy.TargetScope, targets),
                TargetDeviceRegistrationIds = GetDeviceTargetIds(policy.TargetScope, targets),
                TargetStoreSummaries = CloneStoreSummaries(targetSummaries?.Stores),
                TargetDeviceSummaries = CloneDeviceSummaries(targetSummaries?.Devices),
                PolicyUpdatedAt = policy.UpdatedAt ?? policy.CreatedAt,
                PolicyUpdatedBy = policy.UpdatedBy ?? policy.CreatedBy,
            };
        }

        private async Task<PolicyTargetSummaries> GetTargetSummariesAsync(
            string? targetScope,
            IReadOnlyCollection<WpfUpdatePolicyTarget>? targets
        )
        {
            var normalizedScope = NormalizeTargetScopeOrAll(targetScope);
            if (normalizedScope == "stores")
            {
                var storeGuids = GetStoreTargetGuids(normalizedScope, targets);
                if (storeGuids.Count == 0)
                {
                    return new PolicyTargetSummaries();
                }

                // 摘要读取不按启用或删除状态过滤：历史定向目标仍应保留可识别的安全展示信息。
                var matchingStores = await _db.Queryable<Store>()
                    .Where(store => storeGuids.Contains(store.StoreGUID))
                    .ToListAsync();
                var storesByGuid = matchingStores
                    .Where(store => !string.IsNullOrWhiteSpace(store.StoreGUID))
                    .GroupBy(store => store.StoreGUID, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                return new PolicyTargetSummaries
                {
                    Stores = storeGuids.Select(storeGuid =>
                    {
                        storesByGuid.TryGetValue(storeGuid, out var store);
                        return new WpfUpdateTargetStoreSummaryDto
                        {
                            StoreGuid = storeGuid,
                            StoreCode = store?.StoreCode,
                            StoreName = store?.StoreName,
                        };
                    }).ToList(),
                };
            }

            if (normalizedScope != "devices")
            {
                return new PolicyTargetSummaries();
            }

            var deviceIds = GetDeviceTargetIds(normalizedScope, targets);
            if (deviceIds.Count == 0 || _posmDb == null)
            {
                // POSM 连接不可用时仍必须保留目标注册 ID，不能把已选目标悄悄丢失。
                return new PolicyTargetSummaries
                {
                    Devices = deviceIds
                        .Select(id => new WpfUpdateTargetDeviceSummaryDto { DeviceRegistrationId = id })
                        .ToList(),
                };
            }

            // 不加 status/type/system 过滤，已禁用或历史设备也应能在策略只读摘要中被识别。
            var devices = await _posmDb.Queryable<POSM_设备注册信息表>()
                .Where(device => deviceIds.Contains(device.ID))
                .ToListAsync();
            var storeCodes = devices
                .Select(device => NormalizeOptional(device.分店代码))
                .Where(code => code is not null)
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var stores = storeCodes.Count == 0
                ? new List<Store>()
                : await _db.Queryable<Store>()
                    .Where(store => storeCodes.Contains(store.StoreCode))
                    .ToListAsync();
            var devicesById = devices
                .GroupBy(device => device.ID)
                .ToDictionary(group => group.Key, group => group.First());
            var storeNamesByCode = stores
                .Where(store => !string.IsNullOrWhiteSpace(store.StoreCode))
                .GroupBy(store => store.StoreCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().StoreName, StringComparer.OrdinalIgnoreCase);

            return new PolicyTargetSummaries
            {
                Devices = deviceIds.Select(deviceId =>
                {
                    devicesById.TryGetValue(deviceId, out var device);
                    var storeCode = NormalizeOptional(device?.分店代码);
                    return new WpfUpdateTargetDeviceSummaryDto
                    {
                        DeviceRegistrationId = deviceId,
                        SystemDeviceNumber = device?.系统设备编号,
                        StoreCode = storeCode,
                        StoreName = storeCode is not null
                            && storeNamesByCode.TryGetValue(storeCode, out var storeName)
                                ? storeName
                                : null,
                        Remarks = device?.备注,
                    };
                }).ToList(),
            };
        }

        private static void ApplyTargetSummaries(
            WpfUpdatePolicyDto policy,
            PolicyTargetSummaries targetSummaries
        )
        {
            policy.TargetStoreSummaries = CloneStoreSummaries(targetSummaries.Stores);
            policy.TargetDeviceSummaries = CloneDeviceSummaries(targetSummaries.Devices);
        }

        private static List<WpfUpdateTargetStoreSummaryDto> CloneStoreSummaries(
            IReadOnlyCollection<WpfUpdateTargetStoreSummaryDto>? source
        )
        {
            return (source ?? Array.Empty<WpfUpdateTargetStoreSummaryDto>())
                .Select(item => new WpfUpdateTargetStoreSummaryDto
                {
                    StoreGuid = item.StoreGuid,
                    StoreCode = item.StoreCode,
                    StoreName = item.StoreName,
                })
                .ToList();
        }

        private static List<WpfUpdateTargetDeviceSummaryDto> CloneDeviceSummaries(
            IReadOnlyCollection<WpfUpdateTargetDeviceSummaryDto>? source
        )
        {
            return (source ?? Array.Empty<WpfUpdateTargetDeviceSummaryDto>())
                .Select(item => new WpfUpdateTargetDeviceSummaryDto
                {
                    DeviceRegistrationId = item.DeviceRegistrationId,
                    SystemDeviceNumber = item.SystemDeviceNumber,
                    StoreCode = item.StoreCode,
                    StoreName = item.StoreName,
                    Remarks = item.Remarks,
                })
                .ToList();
        }

        private sealed class PolicyTargetSummaries
        {
            public List<WpfUpdateTargetStoreSummaryDto> Stores { get; init; } = new();
            public List<WpfUpdateTargetDeviceSummaryDto> Devices { get; init; } = new();
        }

        private async Task<bool> DoesPolicyTargetMatchAsync(
            WpfUpdatePolicy policy,
            WpfUpdateCheckDeviceIdentity deviceIdentity
        )
        {
            var targetScope = NormalizeTargetScopeOrAll(policy.TargetScope);
            var targets = await _db
                .Queryable<WpfUpdatePolicyTarget>()
                .Where(x => x.PolicyId == policy.Id && !x.IsDeleted)
                .ToListAsync();
            if (targetScope == "devices")
            {
                return targets.Any(
                    target =>
                        target.DeviceRegistrationId == deviceIdentity.DeviceRegistrationId
                );
            }

            if (targetScope != "stores" || string.IsNullOrWhiteSpace(deviceIdentity.StoreCode))
            {
                return false;
            }

            // 分店归属以设备本次严格认证时的当前分店代码为准，并在检查时动态解析 StoreGUID。
            var store = await _db.Queryable<Store>().FirstAsync(store =>
                store.StoreCode == deviceIdentity.StoreCode && store.IsActive && !store.IsDeleted
            );
            return store != null
                && targets.Any(target =>
                    string.Equals(target.StoreGuid, store.StoreGUID, StringComparison.OrdinalIgnoreCase)
                );
        }

        private static bool TryBuildPolicyTargets(
            WpfUpdatePolicyRequest request,
            out string targetScope,
            out List<WpfUpdatePolicyTarget> targets,
            out string errorCode
        )
        {
            targetScope = NormalizeTargetScopeOrAll(request.TargetScope);
            targets = new List<WpfUpdatePolicyTarget>();
            errorCode = string.Empty;
            var requestedScope = NormalizeOptional(request.TargetScope)?.ToLowerInvariant() ?? "all";
            if (requestedScope is not ("all" or "stores" or "devices"))
            {
                errorCode = "TARGET_SCOPE_INVALID";
                return false;
            }

            if (targetScope == "stores")
            {
                var storeGuids = request.TargetStoreGuids
                    .Select(NormalizeOptional)
                    .Where(value => value is not null)
                    .Select(value => value!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (storeGuids.Count == 0)
                {
                    errorCode = "TARGET_STORES_REQUIRED";
                    return false;
                }

                targets.AddRange(storeGuids.Select(value => new WpfUpdatePolicyTarget
                {
                    Id = Guid.NewGuid(),
                    StoreGuid = value,
                }));
            }
            else if (targetScope == "devices")
            {
                var deviceIds = request.TargetDeviceRegistrationIds
                    .Where(value => value > 0)
                    .Distinct()
                    .ToList();
                if (deviceIds.Count == 0)
                {
                    errorCode = "TARGET_DEVICES_REQUIRED";
                    return false;
                }

                targets.AddRange(deviceIds.Select(value => new WpfUpdatePolicyTarget
                {
                    Id = Guid.NewGuid(),
                    DeviceRegistrationId = value,
                }));
            }

            return true;
        }

        private static bool IsAllTargetScope(string? targetScope)
        {
            return NormalizeTargetScopeOrAll(targetScope) == "all";
        }

        private static string NormalizeTargetScopeOrAll(string? targetScope)
        {
            return NormalizeOptional(targetScope)?.ToLowerInvariant() switch
            {
                "stores" => "stores",
                "devices" => "devices",
                _ => "all",
            };
        }

        private static List<string> GetStoreTargetGuids(
            string? targetScope,
            IReadOnlyCollection<WpfUpdatePolicyTarget>? targets
        )
        {
            if (NormalizeTargetScopeOrAll(targetScope) != "stores")
            {
                return new List<string>();
            }

            return (targets ?? Array.Empty<WpfUpdatePolicyTarget>())
                .Select(target => NormalizeOptional(target.StoreGuid))
                .Where(value => value is not null)
                .Select(value => value!)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<int> GetDeviceTargetIds(
            string? targetScope,
            IReadOnlyCollection<WpfUpdatePolicyTarget>? targets
        )
        {
            if (NormalizeTargetScopeOrAll(targetScope) != "devices")
            {
                return new List<int>();
            }

            return (targets ?? Array.Empty<WpfUpdatePolicyTarget>())
                .Select(target => target.DeviceRegistrationId ?? 0)
                .Where(id => id > 0)
                .OrderBy(id => id)
                .ToList();
        }

        private static bool TryNormalizeVersion(string? version, out string normalizedVersion)
        {
            normalizedVersion = string.Empty;
            var candidate = NormalizeRequired(version);
            var match = VersionRegex.Match(candidate);
            if (!match.Success)
            {
                return false;
            }

            normalizedVersion = match.Groups["build"].Success
                ? $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{match.Groups["patch"].Value}.{match.Groups["build"].Value}"
                : $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{match.Groups["patch"].Value}";
            return true;
        }

        private static bool TryNormalizeOptionalVersion(
            string? version,
            out string? normalizedVersion
        )
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                normalizedVersion = null;
                return true;
            }

            if (TryNormalizeVersion(version, out var canonicalVersion))
            {
                normalizedVersion = canonicalVersion;
                return true;
            }

            normalizedVersion = null;
            return false;
        }

        private static string NormalizeVersionOrOriginal(string? version)
        {
            return TryNormalizeVersion(version, out var normalizedVersion)
                ? normalizedVersion
                : NormalizeRequired(version);
        }

        private static string? NormalizeOptionalVersionOrOriginal(string? version)
        {
            return string.IsNullOrWhiteSpace(version) ? null : NormalizeVersionOrOriginal(version);
        }

        private static bool VersionsEqual(string? left, string? right)
        {
            if (!TryParseVersion(left, out var leftParsed) || !TryParseVersion(right, out var rightParsed))
            {
                return string.Equals(
                    NormalizeRequired(left),
                    NormalizeRequired(right),
                    StringComparison.OrdinalIgnoreCase
                );
            }

            return leftParsed.CompareTo(rightParsed) == 0;
        }

        private static bool TryParseVersion(string? version, out WpfVersion parsed)
        {
            parsed = default;
            var normalized = NormalizeRequired(version);
            var match = VersionRegex.Match(normalized);
            if (!match.Success)
            {
                return false;
            }

            var build = 0;
            if (
                !TryParseVersionSegment(match.Groups["major"].Value, out var major)
                || !TryParseVersionSegment(match.Groups["minor"].Value, out var minor)
                || !TryParseVersionSegment(match.Groups["patch"].Value, out var patch)
                || (
                    match.Groups["build"].Success
                    && !TryParseVersionSegment(match.Groups["build"].Value, out build)
                )
            )
            {
                parsed = default;
                return false;
            }

            parsed = new WpfVersion(major, minor, patch, build);
            return true;
        }

        private static bool TryParseVersionSegment(string value, out int segment)
        {
            return int.TryParse(value, out segment);
        }

        private static string NormalizeChannel(string? value)
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? "production"
                : value.Trim().ToLowerInvariant();
            return NormalizeKeySegment(normalized, "production");
        }

        private static bool TryNormalizeInstallerType(string? value, out string installerType)
        {
            var normalized = NormalizeOptional(value)?.ToLowerInvariant();
            if (normalized is "msi" or "exe")
            {
                installerType = normalized;
                return true;
            }

            installerType = string.Empty;
            return false;
        }

        private static bool TryResolveInstallerTypeForCreate(
            string fileName,
            string? requestedInstallerType,
            out string installerType
        )
        {
            if (!TryResolveInstallerTypeFromFileName(fileName, out installerType))
            {
                return false;
            }

            var requested = NormalizeOptional(requestedInstallerType);
            if (requested is null)
            {
                return true;
            }

            return TryNormalizeInstallerType(requested, out var normalizedRequested)
                && string.Equals(
                    installerType,
                    normalizedRequested,
                    StringComparison.OrdinalIgnoreCase
                );
        }

        private static bool TryResolveInstallerTypeForExistingFile(
            string fileName,
            string? requestedInstallerType,
            out string installerType
        )
        {
            installerType = string.Empty;
            if (
                !TryResolveInstallerTypeFromFileName(fileName, out var fileInstallerType)
                || !TryNormalizeInstallerType(requestedInstallerType, out var requested)
            )
            {
                return false;
            }

            if (!string.Equals(fileInstallerType, requested, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            installerType = fileInstallerType;
            return true;
        }

        private static bool TryResolveInstallerTypeFromFileName(
            string fileName,
            out string installerType
        )
        {
            if (!IsSafeInstallerFileName(fileName))
            {
                installerType = string.Empty;
                return false;
            }

            var extension = Path.GetExtension(fileName)
                .TrimStart('.')
                .ToLowerInvariant();
            if (extension is "msi" or "exe")
            {
                installerType = extension;
                return true;
            }

            installerType = string.Empty;
            return false;
        }

        private static bool IsSafeInstallerFileName(string fileName)
        {
            if (
                string.IsNullOrWhiteSpace(fileName)
                || fileName.IndexOf('/') >= 0
                || fileName.IndexOf('\\') >= 0
                || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
                || ContainsWindowsInvalidFileNameCharacter(fileName)
                || fileName.EndsWith('.')
                || fileName.EndsWith(' ')
            )
            {
                return false;
            }

            var baseName = Path.GetFileNameWithoutExtension(fileName);
            if (
                string.IsNullOrWhiteSpace(baseName)
                || string.Equals(baseName, ".", StringComparison.Ordinal)
                || string.Equals(baseName, "..", StringComparison.Ordinal)
            )
            {
                return false;
            }

            // 中文注释：Windows 设备名带扩展也仍然保留，例如 CON.any.exe / NUL.v1.msi。
            return !IsReservedWindowsDeviceFileName(fileName);
        }

        private static bool ContainsWindowsInvalidFileNameCharacter(string fileName)
        {
            foreach (var ch in fileName)
            {
                if (char.IsControl(ch) || ch is '<' or '>' or ':' or '"' or '|' or '?' or '*')
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsReservedWindowsDeviceFileName(string fileName)
        {
            var firstDotIndex = fileName.IndexOf('.');
            var deviceName = firstDotIndex < 0 ? fileName : fileName[..firstDotIndex];
            return ReservedWindowsFileNames.Contains(deviceName.TrimEnd(' ', '.'));
        }

        private static string NormalizeFileName(string fileName)
        {
            var source = Path.GetFileName(fileName.Replace('\\', '/'));
            var chars = source
                .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')
                .ToArray();
            var normalized = new string(chars).Trim('-', '.', '_');
            return string.IsNullOrWhiteSpace(normalized) ? "wpf-release.bin" : normalized;
        }

        private static string NormalizeKeySegment(string? value, string fallback)
        {
            var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            var chars = source
                .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')
                .ToArray();
            var normalized = new string(chars).Trim('-', '.', '_');
            return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized.ToLowerInvariant();
        }

        private static string NormalizeRequired(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string? NormalizeOptional(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static bool TryNormalizeSha256(string? value, out string sha256)
        {
            sha256 = NormalizeRequired(value).ToLowerInvariant();
            return Sha256Regex.IsMatch(sha256);
        }

        private static bool TryNormalizeDownloadUrl(string? value, out string downloadUrl)
        {
            downloadUrl = NormalizeRequired(value);
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
            {
                return false;
            }

            // 生产更新源必须走 HTTPS，本地联调才允许 loopback HTTP。
            return uri.Scheme == Uri.UriSchemeHttps
                || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        }

        private bool TryNormalizeReleaseDownloadUrl(
            string? value,
            string objectKey,
            out string downloadUrl
        )
        {
            if (!TryNormalizeDownloadUrl(value, out downloadUrl))
            {
                return false;
            }

            if (_uploadService == null)
            {
                return true;
            }

            if (
                Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttp
                && uri.IsLoopback
            )
            {
                // 中文注释：本地联调的 loopback HTTP 先按基础规则放行，不参与 COS 公网地址精确匹配。
                return true;
            }

            // 中文注释：接入 COS 后，登记下载地址必须精确落到当前桶/地域和目标 objectKey，不能接受任意 HTTPS 链接。
            return _uploadService.TryMatchPublicDownloadUrl(downloadUrl, objectKey, out downloadUrl);
        }

        private static string BuildPublicDownloadUrlFromSignedUrl(string signedUrl)
        {
            if (!Uri.TryCreate(signedUrl, UriKind.Absolute, out var uri))
            {
                return signedUrl;
            }

            var builder = new UriBuilder(uri)
            {
                Query = string.Empty,
            };
            return builder.Uri.ToString();
        }

        private readonly record struct WpfVersion(int Major, int Minor, int Patch, int Build)
            : IComparable<WpfVersion>
        {
            public int CompareTo(WpfVersion other)
            {
                var major = Major.CompareTo(other.Major);
                if (major != 0)
                {
                    return major;
                }

                var minor = Minor.CompareTo(other.Minor);
                if (minor != 0)
                {
                    return minor;
                }

                var patch = Patch.CompareTo(other.Patch);
                return patch != 0 ? patch : Build.CompareTo(other.Build);
            }
        }
    }
}
