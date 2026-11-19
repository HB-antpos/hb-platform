using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using SqlSugar;

namespace BlazorApp.Api.Services;

/// <summary>
/// iPad OTA 覆盖层（分店 / 设备）：在全局基线 rollout 之上，把某个分店或某台 iPad 单独指到另一个已登记的 OTA 版本。
/// 决策顺序见 <see cref="GetDecisionAsync"/>；这里负责覆盖的增删改查、设备选项与设备身份解析。
/// </summary>
public sealed partial class PosIpadOtaPolicyService
{
    private const string AssignmentLockResource = "app-update-policy:ota-assignment:" + ProductionEnvironment;
    private const int MaxAssignmentRows = 500;

    public async Task<ApiResponse<List<PosIpadOtaAssignmentDto>>> GetAssignmentsAsync()
    {
        var rows = await db.Queryable<PosIpadOtaAssignment>()
            .Where(item => item.Environment == ProductionEnvironment && !item.IsDeleted)
            .OrderByDescending(item => item.PolicyVersion)
            .Take(MaxAssignmentRows)
            .ToListAsync();
        return ApiResponse<List<PosIpadOtaAssignmentDto>>.OK(await MapAssignmentsAsync(rows));
    }

    public async Task<ApiResponse<PosIpadOtaAssignmentDto>> CreateAssignmentAsync(
        PosIpadOtaAssignmentRequest request,
        string currentUser
    )
    {
        var input = await ValidateAssignmentInputAsync(request, requireTarget: true);
        if (input.Error is not null)
        {
            return input.Error;
        }

        ApiResponse<PosIpadOtaAssignmentDto>? failure = null;
        PosIpadOtaAssignment? created = null;
        var transaction = await db.Ado.UseTranAsync(async () =>
        {
            await AppUpdatePolicyMutationLock.AcquireAsync(db, AssignmentLockResource);

            // 同一目标同时只允许一条启用的覆盖；锁内重查，避免并发创建绕过唯一索引的友好提示。
            if (request.Enabled && await HasEnabledAssignmentAsync(input))
            {
                failure = AssignmentError(
                    "OTA_ASSIGNMENT_TARGET_EXISTS",
                    "该目标已有启用的覆盖，请直接修改它"
                );
                return;
            }

            var now = DateTime.UtcNow;
            var user = NormalizeUser(currentUser);
            created = new PosIpadOtaAssignment
            {
                Id = Guid.NewGuid(),
                Environment = ProductionEnvironment,
                TargetType = input.TargetType,
                StoreGuid = input.StoreGuid,
                DeviceRegistrationId = input.DeviceRegistrationId,
                ReleaseId = input.Release!.Id,
                ForceUpdate = request.ForceUpdate,
                ReleaseMessage = input.ReleaseMessage,
                Note = input.Note,
                ExpiresAtUtc = input.ExpiresAtUtc,
                Enabled = request.Enabled,
                PolicyVersion = await NextAssignmentPolicyVersionAsync(),
                CreatedAt = now,
                CreatedBy = user,
                UpdatedAt = now,
                UpdatedBy = user,
                IsDeleted = false,
            };
            await db.Insertable(created).ExecuteCommandAsync();
        });

        if (failure is not null)
        {
            return failure;
        }

        if (!transaction.IsSuccess || created is null)
        {
            logger.LogError(transaction.ErrorException, "iPad OTA 覆盖创建事务失败");
            return AssignmentError("OTA_ASSIGNMENT_SAVE_FAILED", "iPad OTA 覆盖保存失败");
        }

        return ApiResponse<PosIpadOtaAssignmentDto>.OK(
            (await MapAssignmentsAsync([created]))[0]
        );
    }

    public async Task<ApiResponse<PosIpadOtaAssignmentDto>> UpdateAssignmentAsync(
        Guid id,
        PosIpadOtaAssignmentRequest request,
        string currentUser
    )
    {
        if (!request.ExpectedPolicyVersion.HasValue)
        {
            return AssignmentVersionError(
                AppUpdatePolicyErrorCodes.VersionRequired,
                request.ExpectedPolicyVersion,
                0
            );
        }

        // 修改时目标不可变：只认版本、投放方式、到期时间与启停，目标由库内原行决定。
        var input = await ValidateAssignmentInputAsync(request, requireTarget: false);
        if (input.Error is not null)
        {
            return input.Error;
        }

        ApiResponse<PosIpadOtaAssignmentDto>? failure = null;
        PosIpadOtaAssignment? saved = null;
        var transaction = await db.Ado.UseTranAsync(async () =>
        {
            await AppUpdatePolicyMutationLock.AcquireAsync(db, AssignmentLockResource);
            var row = await db.Queryable<PosIpadOtaAssignment>()
                .FirstAsync(item =>
                    item.Id == id
                    && item.Environment == ProductionEnvironment
                    && !item.IsDeleted
                );
            if (row is null)
            {
                failure = AssignmentError("OTA_ASSIGNMENT_NOT_FOUND", "覆盖不存在或已被删除");
                return;
            }

            if (row.PolicyVersion != request.ExpectedPolicyVersion.Value)
            {
                failure = AssignmentVersionError(
                    AppUpdatePolicyErrorCodes.VersionConflict,
                    request.ExpectedPolicyVersion,
                    row.PolicyVersion
                );
                return;
            }

            // 重新启用一条已停用的覆盖时，目标上不能已经有另一条启用的。
            if (request.Enabled && !row.Enabled)
            {
                var sameTarget = row.TargetType == PosIpadOtaAssignmentTargetTypes.Store
                    ? await db.Queryable<PosIpadOtaAssignment>()
                        .AnyAsync(item =>
                            item.Id != row.Id
                            && item.Environment == ProductionEnvironment
                            && item.Enabled
                            && !item.IsDeleted
                            && item.TargetType == PosIpadOtaAssignmentTargetTypes.Store
                            && item.StoreGuid == row.StoreGuid
                        )
                    : await db.Queryable<PosIpadOtaAssignment>()
                        .AnyAsync(item =>
                            item.Id != row.Id
                            && item.Environment == ProductionEnvironment
                            && item.Enabled
                            && !item.IsDeleted
                            && item.TargetType == PosIpadOtaAssignmentTargetTypes.Device
                            && item.DeviceRegistrationId == row.DeviceRegistrationId
                        );
                if (sameTarget)
                {
                    failure = AssignmentError(
                        "OTA_ASSIGNMENT_TARGET_EXISTS",
                        "该目标已有启用的覆盖，不能重复启用"
                    );
                    return;
                }
            }

            row.ReleaseId = input.Release!.Id;
            row.ForceUpdate = request.ForceUpdate;
            row.ReleaseMessage = input.ReleaseMessage;
            row.Note = input.Note;
            row.ExpiresAtUtc = input.ExpiresAtUtc;
            row.Enabled = request.Enabled;
            row.PolicyVersion = await NextAssignmentPolicyVersionAsync();
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = NormalizeUser(currentUser);
            await db.Updateable(row).ExecuteCommandAsync();
            saved = row;
        });

        if (failure is not null)
        {
            return failure;
        }

        if (!transaction.IsSuccess || saved is null)
        {
            logger.LogError(transaction.ErrorException, "iPad OTA 覆盖修改事务失败");
            return AssignmentError("OTA_ASSIGNMENT_SAVE_FAILED", "iPad OTA 覆盖保存失败");
        }

        return ApiResponse<PosIpadOtaAssignmentDto>.OK(
            (await MapAssignmentsAsync([saved]))[0]
        );
    }

    public async Task<ApiResponse<bool>> DeleteAssignmentAsync(
        Guid id,
        long? expectedPolicyVersion,
        string currentUser
    )
    {
        if (!expectedPolicyVersion.HasValue)
        {
            return ApiResponse<bool>.Error(
                "expectedPolicyVersion 不能为空",
                AppUpdatePolicyErrorCodes.VersionRequired
            );
        }

        ApiResponse<bool>? failure = null;
        var transaction = await db.Ado.UseTranAsync(async () =>
        {
            await AppUpdatePolicyMutationLock.AcquireAsync(db, AssignmentLockResource);
            var row = await db.Queryable<PosIpadOtaAssignment>()
                .FirstAsync(item =>
                    item.Id == id
                    && item.Environment == ProductionEnvironment
                    && !item.IsDeleted
                );
            if (row is null)
            {
                // 已删除视为幂等成功，重复点击删除不报错。
                return;
            }

            if (row.PolicyVersion != expectedPolicyVersion.Value)
            {
                failure = ApiResponse<bool>.Error(
                    "更新策略版本已变化，请刷新后重试",
                    AppUpdatePolicyErrorCodes.VersionConflict,
                    new
                    {
                        ExpectedPolicyVersion = expectedPolicyVersion,
                        ActualPolicyVersion = row.PolicyVersion,
                    }
                );
                return;
            }

            // 软删除：同时置为停用，既不再参与决策，也释放该目标的「唯一启用」名额。
            row.Enabled = false;
            row.IsDeleted = true;
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = NormalizeUser(currentUser);
            await db.Updateable(row).ExecuteCommandAsync();
        });

        if (failure is not null)
        {
            return failure;
        }

        if (!transaction.IsSuccess)
        {
            logger.LogError(transaction.ErrorException, "iPad OTA 覆盖删除事务失败");
            return ApiResponse<bool>.Error("iPad OTA 覆盖删除失败", "OTA_ASSIGNMENT_SAVE_FAILED");
        }

        return ApiResponse<bool>.OK(true);
    }

    public async Task<ApiResponse<PagedResult<PosIpadOtaTargetDeviceOptionDto>>> GetTargetDeviceOptionsAsync(
        string? storeGuid,
        string? keyword,
        int page,
        int pageSize
    )
    {
        var normalizedPage = Math.Max(page, 1);
        var normalizedPageSize = Math.Clamp(pageSize, 1, 100);
        if (posmDb is null)
        {
            return ApiResponse<PagedResult<PosIpadOtaTargetDeviceOptionDto>>.Error(
                "设备库不可用",
                "OTA_DEVICE_DB_UNAVAILABLE"
            );
        }

        var normalizedKeyword = NormalizeOptional(keyword);
        string? storeCode = null;
        var normalizedStoreGuid = NormalizeOptional(storeGuid);
        if (normalizedStoreGuid is not null)
        {
            var guid = NormalizeStoreGuid(normalizedStoreGuid);
            var store = await db.Queryable<Store>()
                .FirstAsync(item => item.StoreGUID == guid && !item.IsDeleted);
            if (store is null)
            {
                return ApiResponse<PagedResult<PosIpadOtaTargetDeviceOptionDto>>.OK(
                    new PagedResult<PosIpadOtaTargetDeviceOptionDto>
                    {
                        Items = new(),
                        Total = 0,
                        Page = normalizedPage,
                        PageSize = normalizedPageSize,
                    }
                );
            }

            storeCode = store.StoreCode;
        }

        // 每个硬件只取最新一条注册记录，且只列启用的 iPadOS POS。
        var query = posmDb.Queryable<POSM_设备注册信息表>()
            .Where(device =>
                device.设备硬件识别码.Trim() != string.Empty
                && !SqlFunc.Subqueryable<POSM_设备注册信息表>()
                    .Where(newer =>
                        newer.设备硬件识别码 == device.设备硬件识别码
                        && newer.ID > device.ID)
                    .Any()
                && device.设备状态 == (int)DeviceStatus.启用
                && device.设备类型 == "POS"
                && device.设备系统 == DeviceRegistrationDeviceSystems.IpadOs
            );
        if (storeCode is not null)
        {
            query = query.Where(device => device.分店代码 == storeCode);
        }

        if (normalizedKeyword is not null)
        {
            query = query.Where(device =>
                device.系统设备编号.Contains(normalizedKeyword)
                || (device.分店代码 ?? string.Empty).Contains(normalizedKeyword)
                || (device.备注 ?? string.Empty).Contains(normalizedKeyword)
            );
        }

        var total = await query.CountAsync();
        var devices = await query
            .OrderBy(device => device.分店代码)
            .OrderBy(device => device.系统设备编号)
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync();
        var storeNames = await LoadStoreNamesByCodeAsync(
            devices.Select(device => device.分店代码)
        );

        // 只返回展示用字段，不向后台界面泄露授权码或完整硬件识别码。
        var items = devices.Select(device => new PosIpadOtaTargetDeviceOptionDto
        {
            DeviceRegistrationId = device.ID,
            SystemDeviceNumber = device.系统设备编号,
            StoreCode = device.分店代码,
            StoreName = LookupStoreName(storeNames, device.分店代码),
            Remarks = device.备注,
            AppVersion = device.应用版本,
            IsOnline = device.是否在线,
            LastHeartbeatAt = device.最后心跳时间,
        }).ToList();

        return ApiResponse<PagedResult<PosIpadOtaTargetDeviceOptionDto>>.OK(
            new PagedResult<PosIpadOtaTargetDeviceOptionDto>
            {
                Items = items,
                Total = total,
                Page = normalizedPage,
                PageSize = normalizedPageSize,
            }
        );
    }

    /// <summary>
    /// 把设备认证身份还原成「这台 iPad 在 POSM 里的所有注册 ID」。
    /// 先校验最新注册记录与请求一致（设备编号、启用、iPadOS、分店），任何一项不符都不启用设备层；
    /// 再取同硬件同分店的全部注册 ID，让重新激活后的旧设备覆盖继续有效，而换店后不再命中。
    /// </summary>
    private async Task<List<int>> ResolveDeviceRegistrationIdsAsync(
        PosIpadOtaDecisionRequest request,
        Store store
    )
    {
        var deviceCode = NormalizeOptional(request.DeviceCode);
        var hardwareId = NormalizeOptional(request.HardwareId);
        if (posmDb is null || deviceCode is null || hardwareId is null)
        {
            return new List<int>();
        }

        var latest = await posmDb.Queryable<POSM_设备注册信息表>()
            .Where(item => item.设备硬件识别码 == hardwareId)
            .OrderByDescending(item => item.ID)
            .FirstAsync();
        if (
            latest is null
            || !string.Equals(latest.系统设备编号, deviceCode, StringComparison.OrdinalIgnoreCase)
            || latest.设备状态 != (int)DeviceStatus.启用
            || !string.Equals(latest.设备系统, DeviceRegistrationDeviceSystems.IpadOs, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                NormalizeOptional(latest.分店代码),
                store.StoreCode,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return new List<int>();
        }

        var latestStoreCode = latest.分店代码;
        return await posmDb.Queryable<POSM_设备注册信息表>()
            .Where(item => item.设备硬件识别码 == hardwareId && item.分店代码 == latestStoreCode)
            .Select(item => item.ID)
            .ToListAsync();
    }

    private async Task<AssignmentInput> ValidateAssignmentInputAsync(
        PosIpadOtaAssignmentRequest request,
        bool requireTarget
    )
    {
        if (request.ReleaseId is null)
        {
            return AssignmentInput.Fail(
                AssignmentError("OTA_RELEASE_REQUIRED", "覆盖必须选择已登记的 OTA 发布")
            );
        }

        var release = await db.Queryable<PosIpadOtaRelease>()
            .FirstAsync(item =>
                item.Id == request.ReleaseId.Value
                && item.Environment == ProductionEnvironment
                && !item.IsDeleted
            );
        if (release is null)
        {
            return AssignmentInput.Fail(AssignmentError("OTA_RELEASE_INVALID", "OTA 发布不存在"));
        }

        var message = NormalizeOptional(request.ReleaseMessage);
        if (message?.Length > 1000)
        {
            return AssignmentInput.Fail(
                AssignmentError("RELEASE_MESSAGE_TOO_LONG", "更新说明不能超过 1000 个字符")
            );
        }

        var note = NormalizeOptional(request.Note);
        if (note?.Length > 200)
        {
            return AssignmentInput.Fail(
                AssignmentError("OTA_ASSIGNMENT_NOTE_TOO_LONG", "备注不能超过 200 个字符")
            );
        }

        DateTime? expiresAt = request.ExpiresAtUtc.HasValue
            ? NormalizeUtc(request.ExpiresAtUtc.Value)
            : null;
        if (request.Enabled && expiresAt.HasValue && expiresAt.Value <= DateTime.UtcNow)
        {
            return AssignmentInput.Fail(
                AssignmentError("OTA_ASSIGNMENT_EXPIRES_INVALID", "到期时间必须晚于当前时间")
            );
        }

        var input = new AssignmentInput(
            null,
            string.Empty,
            null,
            null,
            release,
            message,
            note,
            expiresAt
        );
        if (!requireTarget)
        {
            return input;
        }

        var targetType = NormalizeOptional(request.TargetType)?.ToLowerInvariant();
        if (targetType == PosIpadOtaAssignmentTargetTypes.Store)
        {
            var requested = NormalizeOptional(request.StoreGuid);
            if (requested is null)
            {
                return AssignmentInput.Fail(
                    AssignmentError("TARGET_STORES_REQUIRED", "请选择目标分店")
                );
            }

            var guid = NormalizeStoreGuid(requested);
            var store = await db.Queryable<Store>()
                .FirstAsync(item => item.StoreGUID == guid && item.IsActive && !item.IsDeleted);
            if (store is null)
            {
                return AssignmentInput.Fail(
                    AssignmentError("TARGET_STORES_INVALID", "目标分店必须处于活动状态")
                );
            }

            return input with
            {
                TargetType = PosIpadOtaAssignmentTargetTypes.Store,
                StoreGuid = store.StoreGUID,
            };
        }

        if (targetType == PosIpadOtaAssignmentTargetTypes.Device)
        {
            if (request.DeviceRegistrationId is not > 0)
            {
                return AssignmentInput.Fail(
                    AssignmentError("TARGET_DEVICES_REQUIRED", "请选择目标设备")
                );
            }

            if (posmDb is null)
            {
                return AssignmentInput.Fail(
                    AssignmentError("OTA_DEVICE_DB_UNAVAILABLE", "设备库不可用")
                );
            }

            var deviceId = request.DeviceRegistrationId.Value;
            var device = await posmDb.Queryable<POSM_设备注册信息表>()
                .FirstAsync(item => item.ID == deviceId);
            if (
                device is null
                || device.设备状态 != (int)DeviceStatus.启用
                || !string.Equals(device.设备系统, DeviceRegistrationDeviceSystems.IpadOs, StringComparison.OrdinalIgnoreCase)
            )
            {
                return AssignmentInput.Fail(
                    AssignmentError("TARGET_DEVICES_INVALID", "目标设备必须是已启用的 iPad 设备")
                );
            }

            return input with
            {
                TargetType = PosIpadOtaAssignmentTargetTypes.Device,
                DeviceRegistrationId = deviceId,
            };
        }

        return AssignmentInput.Fail(
            AssignmentError("TARGET_SCOPE_INVALID", "目标类型必须是 store 或 device")
        );
    }

    private Task<bool> HasEnabledAssignmentAsync(AssignmentInput input) =>
        input.TargetType == PosIpadOtaAssignmentTargetTypes.Store
            ? db.Queryable<PosIpadOtaAssignment>()
                .AnyAsync(item =>
                    item.Environment == ProductionEnvironment
                    && item.Enabled
                    && !item.IsDeleted
                    && item.TargetType == PosIpadOtaAssignmentTargetTypes.Store
                    && item.StoreGuid == input.StoreGuid
                )
            : db.Queryable<PosIpadOtaAssignment>()
                .AnyAsync(item =>
                    item.Environment == ProductionEnvironment
                    && item.Enabled
                    && !item.IsDeleted
                    && item.TargetType == PosIpadOtaAssignmentTargetTypes.Device
                    && item.DeviceRegistrationId == input.DeviceRegistrationId
                );

    /// <summary>覆盖行的 PolicyVersion 在环境内单调递增（含已删除行，避免版本号回退被客户端缓存误判）。</summary>
    private async Task<long> NextAssignmentPolicyVersionAsync()
    {
        var versions = await db.Queryable<PosIpadOtaAssignment>()
            .Where(item => item.Environment == ProductionEnvironment)
            .Select(item => item.PolicyVersion)
            .ToListAsync();
        return versions.Count == 0 ? 1 : versions.Max() + 1;
    }

    private async Task<List<PosIpadOtaAssignmentDto>> MapAssignmentsAsync(
        IReadOnlyCollection<PosIpadOtaAssignment> rows
    )
    {
        if (rows.Count == 0)
        {
            return new List<PosIpadOtaAssignmentDto>();
        }

        var releaseIds = rows.Select(row => row.ReleaseId).Distinct().ToList();
        var releases = (await db.Queryable<PosIpadOtaRelease>()
                .Where(item => releaseIds.Contains(item.Id))
                .ToListAsync())
            .ToDictionary(item => item.Id);

        var storeGuids = rows
            .Where(row => row.StoreGuid is not null)
            .Select(row => row.StoreGuid!)
            .Distinct()
            .ToList();
        var storesByGuid = storeGuids.Count == 0
            ? new Dictionary<string, Store>(StringComparer.OrdinalIgnoreCase)
            : (await db.Queryable<Store>()
                    .Where(item => storeGuids.Contains(item.StoreGUID))
                    .ToListAsync())
                .ToDictionary(item => item.StoreGUID, StringComparer.OrdinalIgnoreCase);

        var deviceIds = rows
            .Where(row => row.DeviceRegistrationId.HasValue)
            .Select(row => row.DeviceRegistrationId!.Value)
            .Distinct()
            .ToList();
        var devicesById = deviceIds.Count == 0 || posmDb is null
            ? new Dictionary<int, POSM_设备注册信息表>()
            : (await posmDb.Queryable<POSM_设备注册信息表>()
                    .Where(item => deviceIds.Contains(item.ID))
                    .ToListAsync())
                .ToDictionary(item => item.ID);
        var storeNamesByCode = await LoadStoreNamesByCodeAsync(
            devicesById.Values.Select(device => device.分店代码)
        );

        var now = DateTime.UtcNow;
        return rows.Select(row =>
        {
            storesByGuid.TryGetValue(row.StoreGuid ?? string.Empty, out var store);
            POSM_设备注册信息表? device = null;
            if (row.DeviceRegistrationId.HasValue)
            {
                devicesById.TryGetValue(row.DeviceRegistrationId.Value, out device);
            }

            releases.TryGetValue(row.ReleaseId, out var release);
            return new PosIpadOtaAssignmentDto
            {
                Id = row.Id,
                TargetType = row.TargetType,
                StoreGuid = row.StoreGuid,
                StoreCode = store?.StoreCode ?? device?.分店代码,
                StoreName = store?.StoreName ?? LookupStoreName(storeNamesByCode, device?.分店代码),
                DeviceRegistrationId = row.DeviceRegistrationId,
                DeviceNumber = device?.系统设备编号,
                DeviceRemarks = device?.备注,
                DeviceAppVersion = device?.应用版本,
                Enabled = row.Enabled,
                ExpiresAtUtc = row.ExpiresAtUtc,
                IsExpired = row.ExpiresAtUtc.HasValue && row.ExpiresAtUtc.Value <= now,
                PolicyVersion = row.PolicyVersion,
                ReleaseId = row.ReleaseId,
                Release = release is null ? null : MapRelease(release),
                ForceUpdate = row.ForceUpdate,
                ReleaseMessage = row.ReleaseMessage,
                Note = row.Note,
                UpdatedAt = row.UpdatedAt,
                UpdatedBy = row.UpdatedBy,
            };
        }).ToList();
    }

    private async Task<Dictionary<string, string>> LoadStoreNamesByCodeAsync(
        IEnumerable<string?> storeCodes
    )
    {
        var codes = storeCodes
            .Select(NormalizeOptional)
            .Where(code => code is not null)
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var stores = await db.Queryable<Store>()
            .Where(item => codes.Contains(item.StoreCode) && !item.IsDeleted)
            .ToListAsync();
        return stores
            .GroupBy(item => item.StoreCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().StoreName,
                StringComparer.OrdinalIgnoreCase
            );
    }

    private static string? LookupStoreName(
        IReadOnlyDictionary<string, string> namesByCode,
        string? storeCode
    ) =>
        NormalizeOptional(storeCode) is { } code && namesByCode.TryGetValue(code, out var name)
            ? name
            : null;

    private static ApiResponse<PosIpadOtaAssignmentDto> AssignmentError(
        string code,
        string message
    ) => ApiResponse<PosIpadOtaAssignmentDto>.Error(message, code);

    private static ApiResponse<PosIpadOtaAssignmentDto> AssignmentVersionError(
        string errorCode,
        long? expectedPolicyVersion,
        long actualPolicyVersion
    ) =>
        ApiResponse<PosIpadOtaAssignmentDto>.Error(
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

    /// <summary>校验通过后的覆盖输入；Error 非空表示校验失败，直接把它返回给调用方。</summary>
    private sealed record AssignmentInput(
        ApiResponse<PosIpadOtaAssignmentDto>? Error,
        string TargetType,
        string? StoreGuid,
        int? DeviceRegistrationId,
        PosIpadOtaRelease? Release,
        string? ReleaseMessage,
        string? Note,
        DateTime? ExpiresAtUtc
    )
    {
        public static AssignmentInput Fail(ApiResponse<PosIpadOtaAssignmentDto> error) =>
            new(error, string.Empty, null, null, null, null, null, null);
    }
}
