using BlazorApp.Api.Data;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using BlazorApp.Shared.Security;
using SqlSugar;
using System.Data;

namespace BlazorApp.Api.Services.React;

public sealed class PaymentTerminalSettingsService(
    POSMSqlSugarContext posmContext,
    SqlSugarContext mainContext,
    ILogger<PaymentTerminalSettingsService> logger,
    ILinklyCloudTerminalCredentialProtector linklyCredentialProtector)
{
    private static readonly string[] PaymentEnvironments = ["Production", "Sandbox"];

    internal const string LinklyConfigurationModeUpdateLockSql = """
        SELECT TOP (1) [Environment], [StoreCode], [Mode],
               [LegacyPairingAttemptId], [LegacyPairingLeaseExpiresAt],
               [UpdatedAt], [UpdatedBy]
        FROM [dbo].[POSM_LinklyCloudConfigurationMode] WITH (UPDLOCK, HOLDLOCK)
        WHERE [StoreCode] = @StoreCode AND [Environment] = @Environment;
        """;

    public async Task<ApiResponse<PaymentTerminalSettingsDto>> GetSettingsAsync(
        string? storeCode = null,
        CancellationToken cancellationToken = default
    )
    {
        var stores = await GetStoreOptionsAsync();
        var selectedStoreCode = ResolveSelectedStoreCode(storeCode, stores);

        var settings = new PaymentTerminalSettingsDto
        {
            Square = await GetSquareStatusesAsync(),
            Stores = stores,
            SelectedStoreCode = selectedStoreCode,
            Linkly = string.IsNullOrWhiteSpace(selectedStoreCode)
                ? new List<LinklyCloudCredentialAdminDto>()
                : await GetLinklyStatusesAsync(selectedStoreCode),
        };

        logger.LogDebug("支付终端配置查询完成 StoreCode={StoreCode}", selectedStoreCode);
        return ApiResponse<PaymentTerminalSettingsDto>.OK(settings, "查询成功");
    }

    public async Task<ApiResponse<PaymentTerminalSettingsDto>> UpdateSquareTokenAsync(
        UpdateSquareTokenDto request,
        string? updatedBy,
        string? storeCode = null,
        CancellationToken cancellationToken = default
    )
    {
        var environment = NormalizeEnvironment(request.Environment);
        if (environment is null)
        {
            return ApiResponse<PaymentTerminalSettingsDto>.Error(
                "支付环境必须是 Production 或 Sandbox",
                "PAYMENT_ENVIRONMENT_INVALID"
            );
        }

        var now = DateTime.UtcNow;
        var updater = NormalizeOptional(updatedBy);
        var existingRows = await posmContext.Db.Queryable<PaymentSquareTokenRecord>()
            .Where(row => row.Environment == environment)
            .ToListAsync();

        if (request.ClearToken)
        {
            await posmContext.Db.Ado.UseTranAsync(async () =>
            {
                foreach (var row in existingRows)
                {
                    // 清除时同步置空密钥，避免被禁用行继续残留明文 token。
                    row.IsEnabled = false;
                    row.AccessToken = string.Empty;
                    row.UpdatedAt = now;
                    row.UpdatedBy = updater;
                    await posmContext.Db.Updateable(row).ExecuteCommandAsync();
                }
            });

            return await GetSettingsAsync(storeCode, cancellationToken);
        }

        var accessToken = NormalizeOptional(request.AccessToken);
        if (accessToken is null)
        {
            // 空 token 表示沿用现有配置；没有旧配置时保持未配置状态。
            return await GetSettingsAsync(storeCode, cancellationToken);
        }

        await posmContext.Db.Ado.UseTranAsync(async () =>
        {
            foreach (var row in existingRows.Where(row => row.IsEnabled))
            {
                row.IsEnabled = false;
                row.UpdatedAt = now;
                row.UpdatedBy = updater;
                await posmContext.Db.Updateable(row).ExecuteCommandAsync();
            }

            await posmContext.Db.Insertable(new PaymentSquareTokenRecord
            {
                Environment = environment,
                AccessToken = accessToken,
                IsEnabled = true,
                UpdatedAt = now,
                UpdatedBy = updater,
            }).ExecuteCommandAsync();
        });

        return await GetSettingsAsync(storeCode, cancellationToken);
    }

    public async Task<ApiResponse<LinklyTerminalManagementDto>> GetLinklyTerminalManagementAsync(
        string? storeCode,
        string? environment,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedStoreCode = NormalizeOptional(storeCode);
        if (normalizedStoreCode is null)
        {
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "门店编码不能为空",
                "LINKLY_STORE_CODE_REQUIRED"
            );
        }

        var normalizedEnvironment = NormalizeEnvironment(environment);
        if (normalizedEnvironment is null)
        {
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "支付环境必须是 Production 或 Sandbox",
                "PAYMENT_ENVIRONMENT_INVALID"
            );
        }

        return ApiResponse<LinklyTerminalManagementDto>.OK(
            await BuildLinklyTerminalManagementAsync(normalizedStoreCode, normalizedEnvironment),
            "查询成功"
        );
    }

    public async Task<ApiResponse<LinklyTerminalManagementDto>> CreateLinklyTerminalAsync(
        CreateLinklyTerminalDto request,
        string? updatedBy,
        CancellationToken cancellationToken = default
    )
    {
        var scope = NormalizeLinklyScope(request.StoreCode, request.Environment);
        if (scope.Error is not null)
        {
            return scope.Error;
        }

        var storeExists = await mainContext.Db.Queryable<Store>()
            .AnyAsync(store => store.StoreCode == scope.StoreCode && store.IsActive && !store.IsDeleted);
        if (!storeExists)
        {
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "未找到启用中的门店",
                "LINKLY_STORE_NOT_FOUND"
            );
        }

        var displayName = NormalizeOptional(request.DisplayName);
        var username = NormalizeOptional(request.Username);
        var password = NormalizeOptional(request.Password);
        var validationError = ValidateLinklyTerminalFields(request.LaneNo, displayName, username, password, true);
        if (validationError is not null)
        {
            return validationError;
        }

        var conflict = await FindLinklyTerminalConflictAsync(
            scope.StoreCode!,
            scope.Environment!,
            request.LaneNo,
            displayName!,
            username!,
            null
        );
        if (conflict is not null)
        {
            return conflict;
        }

        string protectedPassword;
        try
        {
            // 密码只在进入持久化边界前短暂存在，数据库绝不写入 Linkly 明文凭据。
            protectedPassword = linklyCredentialProtector.ProtectPassword(password!);
        }
        catch (Exception)
        {
            return LinklyCredentialProtectionFailure();
        }

        var now = DateTime.UtcNow;
        var updater = NormalizeOptional(updatedBy);
        try
        {
            await posmContext.Db.Ado.BeginTranAsync(IsolationLevel.Serializable);
            try
            {
                await posmContext.Db.Insertable(new PaymentLinklyTerminalRecord
                {
                    TerminalId = Guid.NewGuid(),
                    StoreCode = scope.StoreCode!,
                    Environment = scope.Environment!,
                    LaneNo = request.LaneNo,
                    DisplayName = displayName!,
                    Username = username!,
                    Password = protectedPassword,
                    CredentialProtectionVersion = LinklyCloudTerminalCredentialDataProtection.CurrentVersion,
                    PairingState = "Unpaired",
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedBy = updater,
                    UpdatedBy = updater,
                }).ExecuteCommandAsync();
                await EnsureLinklyConfigurationDraftAsync(scope.StoreCode!, scope.Environment!, updater, now);
                await posmContext.Db.Ado.CommitTranAsync();
            }
            catch
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                throw;
            }
        }
        catch (SqlSugarException exception) when (IsUniqueConstraintViolation(exception))
        {
            logger.LogWarning(
                "Linkly 终端并发新增冲突 StoreCode={StoreCode} Environment={Environment}",
                scope.StoreCode,
                scope.Environment
            );
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "Lane、用户名或终端名称已被其他终端使用",
                "LINKLY_TERMINAL_CONFLICT"
            );
        }

        return ApiResponse<LinklyTerminalManagementDto>.OK(
            await BuildLinklyTerminalManagementAsync(scope.StoreCode!, scope.Environment!),
            "保存成功"
        );
    }

    public async Task<ApiResponse<LinklyTerminalManagementDto>> UpdateLinklyTerminalAsync(
        Guid terminalId,
        UpdateLinklyTerminalDto request,
        string? updatedBy,
        CancellationToken cancellationToken = default
    )
    {
        if (terminalId == Guid.Empty)
        {
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "终端标识无效",
                "LINKLY_TERMINAL_ID_INVALID"
            );
        }

        var scope = NormalizeLinklyScope(request.StoreCode, request.Environment);
        if (scope.Error is not null)
        {
            return scope.Error;
        }

        var updater = NormalizeOptional(updatedBy);
        var submittedPassword = NormalizeOptional(request.Password);
        string? protectedSubmittedPassword = null;
        if (submittedPassword is not null)
        {
            try
            {
                // 只要管理端重新提交密码就强制轮换密文，不能把明文同现有密文比较。
                protectedSubmittedPassword = linklyCredentialProtector.ProtectPassword(submittedPassword);
            }
            catch (Exception)
            {
                return LinklyCredentialProtectionFailure();
            }
        }

        await posmContext.Db.Ado.BeginTranAsync(IsolationLevel.Serializable);
        try
        {
            // 配对与交易创建固定按“会话 -> 终端”加锁；管理端沿用同一顺序，避免并发时形成死锁环。
            var hasBlockingSession = await HasBlockingLinklySessionAsync(
                terminalId,
                scope.StoreCode!,
                scope.Environment!
            );
            // 再获取终端更新锁并读取当前凭据，避免并发编辑把刚清除的配对材料恢复。
            var existing = await WithLinklyUpdateLock(
                    posmContext.Db,
                    posmContext.Db.Queryable<PaymentLinklyTerminalRecord>()
                        .Where(row => row.TerminalId == terminalId
                            && row.StoreCode == scope.StoreCode
                            && row.Environment == scope.Environment)
                )
                .FirstAsync();
            if (existing is null)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return ApiResponse<LinklyTerminalManagementDto>.Error(
                    "未找到指定 Linkly 终端",
                    "LINKLY_TERMINAL_NOT_FOUND"
                );
            }

            var nextUpdatedAt = NextLinklyTerminalUpdatedAt(existing.UpdatedAt);

            var displayName = NormalizeOptional(request.DisplayName);
            var submittedUsername = NormalizeOptional(request.Username);
            var username = submittedUsername ?? existing.Username;
            var passwordForValidation = submittedPassword ?? existing.Password;
            var validationError = ValidateLinklyTerminalFields(
                request.LaneNo,
                displayName,
                username,
                passwordForValidation,
                false
            );
            if (validationError is not null)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return validationError;
            }

            var conflict = await FindLinklyTerminalConflictAsync(
                scope.StoreCode!,
                scope.Environment!,
                request.LaneNo,
                displayName!,
                username!,
                terminalId
            );
            if (conflict is not null)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return conflict;
            }

            var rotateSubmittedPassword = submittedPassword != null;
            var credentialChanged = (submittedUsername is not null
                    && !string.Equals(existing.Username, submittedUsername, StringComparison.Ordinal)
                )
                || rotateSubmittedPassword;
            if (credentialChanged && hasBlockingSession)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return ApiResponse<LinklyTerminalManagementDto>.Error(
                    "该终端仍有进行中或待客户端确认的交易，请先完成交易恢复",
                    "LINKLY_TERMINAL_SESSION_ACTIVE"
                );
            }

            // 只更新本请求负责的字段；凭据未变化时绝不回写 Secret/PosId/PairingState 的旧快照。
            var affected = await posmContext.Db.Updateable<PaymentLinklyTerminalRecord>()
                .SetColumns(row => new PaymentLinklyTerminalRecord
                {
                    LaneNo = request.LaneNo,
                    DisplayName = displayName!,
                    UpdatedAt = nextUpdatedAt,
                    UpdatedBy = updater,
                })
                .SetColumnsIF(credentialChanged, row => new PaymentLinklyTerminalRecord
                {
                    Username = username!,
                    Password = protectedSubmittedPassword ?? existing.Password,
                    CredentialProtectionVersion = !rotateSubmittedPassword
                        ? existing.CredentialProtectionVersion
                        : LinklyCloudTerminalCredentialDataProtection.CurrentVersion,
                    Secret = null,
                    PosId = null,
                    PairingState = "NeedsRepair",
                    LastHealthStatus = null,
                    LastHealthAt = null,
                })
                .Where(row => row.TerminalId == terminalId
                    && row.StoreCode == scope.StoreCode
                    && row.Environment == scope.Environment
                    && row.UpdatedAt == existing.UpdatedAt)
                .ExecuteCommandAsync();
            if (affected != 1)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return ApiResponse<LinklyTerminalManagementDto>.Error(
                    "终端配置已被其他操作更新，请刷新后重试",
                    "LINKLY_TERMINAL_REVISION_CONFLICT"
                );
            }

            await EnsureLinklyConfigurationDraftAsync(
                scope.StoreCode!,
                scope.Environment!,
                updater,
                nextUpdatedAt
            );
            await posmContext.Db.Ado.CommitTranAsync();
        }
        catch (SqlSugarException exception) when (IsUniqueConstraintViolation(exception))
        {
            await posmContext.Db.Ado.RollbackTranAsync();
            logger.LogWarning(
                "Linkly 终端并发编辑冲突 TerminalId={TerminalId}",
                terminalId
            );
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "Lane、用户名或终端名称已被其他终端使用",
                "LINKLY_TERMINAL_CONFLICT"
            );
        }
        catch
        {
            await posmContext.Db.Ado.RollbackTranAsync();
            throw;
        }

        return ApiResponse<LinklyTerminalManagementDto>.OK(
            await BuildLinklyTerminalManagementAsync(scope.StoreCode!, scope.Environment!),
            "保存成功"
        );
    }

    public async Task<ApiResponse<LinklyTerminalManagementDto>> SetLinklyDeviceSelectionAsync(
        string? deviceCode,
        UpdateLinklyDeviceSelectionDto request,
        string? updatedBy,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedDeviceCode = NormalizeOptional(deviceCode);
        if (normalizedDeviceCode is null)
        {
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "设备编号不能为空",
                "LINKLY_DEVICE_CODE_REQUIRED"
            );
        }

        var scope = NormalizeLinklyScope(request.StoreCode, request.Environment);
        if (scope.Error is not null)
        {
            return scope.Error;
        }

        var terminalExists = await posmContext.Db.Queryable<PaymentLinklyTerminalRecord>()
            .AnyAsync(row => row.TerminalId == request.TerminalId
                && row.StoreCode == scope.StoreCode
                && row.Environment == scope.Environment);
        if (!terminalExists)
        {
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "未找到该门店环境下的 Linkly 终端",
                "LINKLY_TERMINAL_NOT_FOUND"
            );
        }

        var device = await SelectLinklyDeviceCandidates(
                posmContext.Db.Queryable<POSM_设备注册信息表>()
                    .Where(row => row.系统设备编号 == normalizedDeviceCode
                        && row.分店代码 == scope.StoreCode
                        && row.设备类型 == "POS")
            )
            .FirstAsync();
        if (device is null || !device.Enabled)
        {
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "设备不存在、未启用或不允许交易",
                "LINKLY_DEVICE_NOT_AVAILABLE"
            );
        }

        // 快照只用于确定旧、新终端的锁集合；锁住选择后仍须重新比对，避免换线竞态。
        var selectionSnapshot = await posmContext.Db.Queryable<PaymentLinklyDeviceSelectionRecord>()
            .Where(row => row.StoreCode == scope.StoreCode
                && row.Environment == scope.Environment
                && row.DeviceCode == normalizedDeviceCode)
            .Select(row => new { row.TerminalId })
            .FirstAsync();
        var affectedTerminalIds = new[] { request.TerminalId, selectionSnapshot?.TerminalId ?? request.TerminalId }
            .Distinct().OrderBy(id => new System.Data.SqlTypes.SqlGuid(id)).ToArray();
        var now = DateTime.UtcNow;
        var updater = NormalizeOptional(updatedBy);
        await posmContext.Db.Ado.BeginTranAsync(IsolationLevel.Serializable);
        try
        {
            // 固定锁序：会话 -> 按编号排序的终端 -> 选择 -> 模式，覆盖换线两端。
            if (await HasBlockingLinklyDeviceSessionAsync(
                    normalizedDeviceCode,
                    scope.StoreCode!,
                    scope.Environment!))
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return ApiResponse<LinklyTerminalManagementDto>.Error(
                    "该 POS 仍有进行中或待确认的 Linkly 交易，请先完成恢复",
                    "LINKLY_TERMINAL_SESSION_ACTIVE"
                );
            }

            foreach (var terminalId in affectedTerminalIds)
            {
                if (await HasBlockingLinklySessionAsync(terminalId, scope.StoreCode!, scope.Environment!))
                {
                    await posmContext.Db.Ado.RollbackTranAsync();
                    return ApiResponse<LinklyTerminalManagementDto>.Error(
                        "原线路或目标线路仍有进行中或待确认的交易，请先完成交易恢复",
                        "LINKLY_TERMINAL_SESSION_ACTIVE"
                    );
                }
            }

            PaymentLinklyTerminalRecord? terminal = null;
            var lockedTerminals = new Dictionary<Guid, PaymentLinklyTerminalRecord>();
            foreach (var terminalId in affectedTerminalIds)
            {
                var lockedTerminal = await WithLinklyUpdateLock(
                        posmContext.Db,
                        posmContext.Db.Queryable<PaymentLinklyTerminalRecord>()
                            .Where(row => row.TerminalId == terminalId
                                && row.StoreCode == scope.StoreCode
                                && row.Environment == scope.Environment)
                    )
                    .FirstAsync();
                if (lockedTerminal is not null)
                {
                    lockedTerminals[terminalId] = lockedTerminal;
                }
                if (lockedTerminal?.PairingAttemptId is not null
                    && lockedTerminal.PairingLeaseExpiresAt > now)
                {
                    await posmContext.Db.Ado.RollbackTranAsync();
                    return ApiResponse<LinklyTerminalManagementDto>.Error(
                        "原线路或目标线路正在配对或检测，请等待操作结束后刷新线路",
                        "LINKLY_TERMINAL_SESSION_ACTIVE"
                    );
                }

                if (terminalId == request.TerminalId)
                {
                    terminal = lockedTerminal;
                }
            }
            if (terminal is null)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return ApiResponse<LinklyTerminalManagementDto>.Error(
                    "未找到该门店环境下的 Linkly 终端",
                    "LINKLY_TERMINAL_NOT_FOUND"
                );
            }

            var assignedToAnotherDevice = await WithLinklyUpdateLock(
                    posmContext.Db,
                    posmContext.Db.Queryable<PaymentLinklyDeviceSelectionRecord>()
                        .Where(row => row.StoreCode == scope.StoreCode
                            && row.Environment == scope.Environment
                            && row.TerminalId == request.TerminalId
                            && row.DeviceCode != normalizedDeviceCode)
                )
                .AnyAsync();
            if (assignedToAnotherDevice)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return LinklyTerminalAssignmentConflict();
            }

            var existing = await WithLinklyUpdateLock(
                    posmContext.Db,
                    posmContext.Db.Queryable<PaymentLinklyDeviceSelectionRecord>()
                        .Where(row => row.StoreCode == scope.StoreCode
                            && row.Environment == scope.Environment
                            && row.DeviceCode == normalizedDeviceCode)
                )
                .FirstAsync();

            if (existing?.TerminalId != selectionSnapshot?.TerminalId)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return LinklySelectionRevisionConflict();
            }

            var mode = await GetLinklyConfigurationModeForUpdateAsync(
                scope.StoreCode!,
                scope.Environment!
            );
            if (string.Equals(mode?.Mode, "Active", StringComparison.OrdinalIgnoreCase)
                && terminal.CredentialProtectionVersion
                    != LinklyCloudTerminalCredentialDataProtection.CurrentVersion)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return LinklyCredentialReentryRequired();
            }

            if (string.Equals(mode?.Mode, "Active", StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(terminal.PairingState, "Ready", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(terminal.Secret)
                    || string.IsNullOrWhiteSpace(terminal.PosId)))
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return ApiResponse<LinklyTerminalManagementDto>.Error(
                    "已启用配置只能选择已配对且可用的 Linkly 终端",
                    "LINKLY_TERMINAL_NOT_READY"
                );
            }

            if (existing is null)
            {
                if (request.ExpectedRevision is > 0)
                {
                    await posmContext.Db.Ado.RollbackTranAsync();
                    return LinklySelectionRevisionConflict();
                }

                await posmContext.Db.Insertable(new PaymentLinklyDeviceSelectionRecord
                {
                    StoreCode = scope.StoreCode!,
                    Environment = scope.Environment!,
                    DeviceCode = normalizedDeviceCode,
                    TerminalId = request.TerminalId,
                    Revision = LinklyCloudSelectionRevision.CreateInitial(),
                    UpdatedAt = now,
                    UpdatedBy = updater,
                }).ExecuteCommandAsync();
            }
            else
            {
                if (request.ExpectedRevision != existing.Revision)
                {
                    await posmContext.Db.Ado.RollbackTranAsync();
                    return LinklySelectionRevisionConflict();
                }

                var affected = await posmContext.Db.Updateable<PaymentLinklyDeviceSelectionRecord>()
                    .SetColumns(row => new PaymentLinklyDeviceSelectionRecord
                    {
                        TerminalId = request.TerminalId,
                        Revision = existing.Revision + 1,
                        UpdatedAt = now,
                        UpdatedBy = updater,
                    })
                    .Where(row => row.StoreCode == scope.StoreCode
                        && row.Environment == scope.Environment
                        && row.DeviceCode == normalizedDeviceCode
                        && row.Revision == existing.Revision)
                    .ExecuteCommandAsync();
                if (affected != 1)
                {
                    await posmContext.Db.Ado.RollbackTranAsync();
                    return LinklySelectionRevisionConflict();
                }
            }

            // 分配关系也是终端快照的一部分；旧、新线路都推进版本，阻止 POS 用旧快照完成 ABA 换线。
            foreach (var terminalId in affectedTerminalIds)
            {
                if (!lockedTerminals.TryGetValue(terminalId, out var lockedTerminal))
                {
                    await posmContext.Db.Ado.RollbackTranAsync();
                    return LinklySelectionRevisionConflict();
                }

                var terminalAffected = await posmContext.Db.Updateable<PaymentLinklyTerminalRecord>()
                    .SetColumns(row => new PaymentLinklyTerminalRecord
                    {
                        UpdatedAt = NextLinklyTerminalUpdatedAt(lockedTerminal.UpdatedAt),
                        UpdatedBy = updater,
                        LastHealthStatus = null,
                        LastHealthAt = null,
                    })
                    .Where(row => row.TerminalId == terminalId
                        && row.StoreCode == scope.StoreCode
                        && row.Environment == scope.Environment
                        && row.UpdatedAt == lockedTerminal.UpdatedAt)
                    .ExecuteCommandAsync();
                if (terminalAffected != 1)
                {
                    await posmContext.Db.Ado.RollbackTranAsync();
                    return LinklySelectionRevisionConflict();
                }
            }

            await EnsureLinklyConfigurationDraftAsync(scope.StoreCode!, scope.Environment!, updater, now);
            await posmContext.Db.Ado.CommitTranAsync();
        }
        catch (SqlSugarException exception) when (IsTerminalAssignmentConstraintViolation(exception))
        {
            await posmContext.Db.Ado.RollbackTranAsync();
            logger.LogWarning(
                "Linkly 终端已由其他 POS 占用 DeviceCode={DeviceCode}",
                normalizedDeviceCode
            );
            return LinklyTerminalAssignmentConflict();
        }
        catch (SqlSugarException exception) when (IsUniqueConstraintViolation(exception))
        {
            await posmContext.Db.Ado.RollbackTranAsync();
            logger.LogWarning(
                exception,
                "Linkly 设备选择发生并发冲突 DeviceCode={DeviceCode}",
                normalizedDeviceCode
            );
            return LinklySelectionRevisionConflict();
        }
        catch
        {
            await posmContext.Db.Ado.RollbackTranAsync();
            throw;
        }

        return ApiResponse<LinklyTerminalManagementDto>.OK(
            await BuildLinklyTerminalManagementAsync(scope.StoreCode!, scope.Environment!),
            "保存成功"
        );
    }

    public async Task<ApiResponse<LinklyTerminalManagementDto>> DeleteLinklyDeviceSelectionAsync(
        string? deviceCode,
        DeleteLinklyDeviceSelectionDto request,
        string? updatedBy,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedDeviceCode = NormalizeOptional(deviceCode);
        if (normalizedDeviceCode is null)
        {
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "设备编号不能为空",
                "LINKLY_DEVICE_CODE_REQUIRED"
            );
        }

        var scope = NormalizeLinklyScope(request.StoreCode, request.Environment);
        if (scope.Error is not null)
        {
            return scope.Error;
        }

        // 先读取目标终端仅用于建立固定锁序；事务内会再次用版本比对，不能信任该快照写入。
        var selectionSnapshot = await posmContext.Db.Queryable<PaymentLinklyDeviceSelectionRecord>()
            .Where(row => row.StoreCode == scope.StoreCode
                && row.Environment == scope.Environment
                && row.DeviceCode == normalizedDeviceCode)
            .Select(row => new { row.TerminalId })
            .FirstAsync();
        if (selectionSnapshot is null)
        {
            if (await HasBlockingLinklyDeviceSessionAsync(
                    normalizedDeviceCode,
                    scope.StoreCode!,
                    scope.Environment!))
            {
                return ApiResponse<LinklyTerminalManagementDto>.Error(
                    "该 POS 仍有进行中或待确认的 Linkly 交易，请先完成恢复",
                    "LINKLY_TERMINAL_SESSION_ACTIVE"
                );
            }

            return ApiResponse<LinklyTerminalManagementDto>.OK(
                await BuildLinklyTerminalManagementAsync(scope.StoreCode!, scope.Environment!),
                "该 POS 未绑定 Linkly 初始终端"
            );
        }

        var now = DateTime.UtcNow;
        var updater = NormalizeOptional(updatedBy);
        await posmContext.Db.Ado.BeginTranAsync(IsolationLevel.Serializable);
        try
        {
            // 解除分配也沿用“会话 -> 终端 -> 选择 -> 模式”锁序，避免未知结果交易被重放到另一台设备。
            if (await HasBlockingLinklyDeviceSessionAsync(
                    normalizedDeviceCode,
                    scope.StoreCode!,
                    scope.Environment!)
                || await HasBlockingLinklySessionAsync(
                    selectionSnapshot.TerminalId,
                    scope.StoreCode!,
                    scope.Environment!))
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return ApiResponse<LinklyTerminalManagementDto>.Error(
                    "该 POS 或 Linkly 终端仍有进行中或待确认的交易，请先完成恢复",
                    "LINKLY_TERMINAL_SESSION_ACTIVE"
                );
            }

            // 锁住与会话/配对操作同一实体终端；数据库外键保证选择指向的终端仍然存在。
            var terminal = await WithLinklyUpdateLock(
                    posmContext.Db,
                    posmContext.Db.Queryable<PaymentLinklyTerminalRecord>()
                        .Where(row => row.TerminalId == selectionSnapshot.TerminalId
                            && row.StoreCode == scope.StoreCode
                            && row.Environment == scope.Environment)
                )
                .FirstAsync();
            if (terminal?.PairingAttemptId is not null
                && terminal.PairingLeaseExpiresAt > now)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return ApiResponse<LinklyTerminalManagementDto>.Error(
                    "该 Linkly 终端仍有状态未知的配对或登录操作，请先完成恢复",
                    "LINKLY_TERMINAL_SESSION_ACTIVE"
                );
            }
            if (terminal is null)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return LinklySelectionRevisionConflict();
            }

            var existing = await WithLinklyUpdateLock(
                    posmContext.Db,
                    posmContext.Db.Queryable<PaymentLinklyDeviceSelectionRecord>()
                        .Where(row => row.StoreCode == scope.StoreCode
                            && row.Environment == scope.Environment
                            && row.DeviceCode == normalizedDeviceCode)
                )
                .FirstAsync();
            if (existing is null
                || existing.TerminalId != selectionSnapshot.TerminalId
                || existing.Revision != request.ExpectedRevision)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return LinklySelectionRevisionConflict();
            }

            var affected = await posmContext.Db.Deleteable<PaymentLinklyDeviceSelectionRecord>()
                .Where(row => row.StoreCode == scope.StoreCode
                    && row.Environment == scope.Environment
                    && row.DeviceCode == normalizedDeviceCode
                    && row.Revision == request.ExpectedRevision)
                .ExecuteCommandAsync();
            if (affected != 1)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return LinklySelectionRevisionConflict();
            }

            // 解绑后 owner 会消失，必须同时推进终端版本并清除旧线路健康结果。
            var terminalAffected = await posmContext.Db.Updateable<PaymentLinklyTerminalRecord>()
                .SetColumns(row => new PaymentLinklyTerminalRecord
                {
                    UpdatedAt = NextLinklyTerminalUpdatedAt(terminal.UpdatedAt),
                    UpdatedBy = updater,
                    LastHealthStatus = null,
                    LastHealthAt = null,
                })
                .Where(row => row.TerminalId == terminal.TerminalId
                    && row.StoreCode == scope.StoreCode
                    && row.Environment == scope.Environment
                    && row.UpdatedAt == terminal.UpdatedAt)
                .ExecuteCommandAsync();
            if (terminalAffected != 1)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return LinklySelectionRevisionConflict();
            }

            await EnsureLinklyConfigurationDraftAsync(scope.StoreCode!, scope.Environment!, updater, now);
            await posmContext.Db.Ado.CommitTranAsync();
        }
        catch
        {
            await posmContext.Db.Ado.RollbackTranAsync();
            throw;
        }

        return ApiResponse<LinklyTerminalManagementDto>.OK(
            await BuildLinklyTerminalManagementAsync(scope.StoreCode!, scope.Environment!),
            "已解除 POS 初始终端分配"
        );
    }

    public async Task<ApiResponse<LinklyTerminalManagementDto>> ActivateLinklyConfigurationAsync(
        ActivateLinklyConfigurationDto request,
        string? updatedBy,
        CancellationToken cancellationToken = default
    )
    {
        var scope = NormalizeLinklyScope(request.StoreCode, request.Environment);
        if (scope.Error is not null)
        {
            return scope.Error;
        }

        var now = DateTime.UtcNow;
        var updater = NormalizeOptional(updatedBy);
        await posmContext.Db.Ado.BeginTranAsync(IsolationLevel.Serializable);
        try
        {
            // 激活前先锁住整个门店环境的阻塞会话范围；Legacy/Draft 在途交易不能跨模式边界。
            if (await HasBlockingLinklyScopeSessionAsync(scope.StoreCode!, scope.Environment!))
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return ApiResponse<LinklyTerminalManagementDto>.Error(
                    "该门店仍有进行中或待确认的 Linkly 交易，请先完成恢复",
                    "LINKLY_TERMINAL_SESSION_ACTIVE"
                );
            }

            // 旧版本保存的是明文。迁移不自动读取或重写它，管理员必须主动重新录入凭据。
            var hasLegacyCredential = await WithLinklyUpdateLock(
                    posmContext.Db,
                    posmContext.Db.Queryable<PaymentLinklyTerminalRecord>()
                        .Where(row => row.StoreCode == scope.StoreCode
                            && row.Environment == scope.Environment
                            && row.CredentialProtectionVersion
                                != LinklyCloudTerminalCredentialDataProtection.CurrentVersion)
                )
                .AnyAsync();
            if (hasLegacyCredential)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return LinklyCredentialReentryRequired();
            }

            // 激活校验与 Mode 写入必须处于同一串行化事务，避免并发修改选择或配对状态后误激活。
            var management = await BuildLinklyTerminalManagementAsync(
                scope.StoreCode!,
                scope.Environment!,
                lockConfigurationModeForUpdate: true
            );
            var mode = await GetLinklyConfigurationModeForUpdateAsync(
                scope.StoreCode!,
                scope.Environment!
            );
            if (mode?.LegacyPairingAttemptId is not null
                && mode.LegacyPairingLeaseExpiresAt > now)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return ApiResponse<LinklyTerminalManagementDto>.Error(
                    "该门店仍有旧版 Linkly 配对正在进行或结果待确认，请稍后重试",
                    "LINKLY_CLOUD_LEGACY_PAIRING_IN_PROGRESS"
                );
            }

            var readyIds = management.Terminals
                .Where(terminal => terminal.PairingState == "Ready")
                .Select(terminal => terminal.TerminalId)
                .ToHashSet();
            if (readyIds.Count == 0)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return ApiResponse<LinklyTerminalManagementDto>.Error(
                    "至少需要一台已配对且可用的 Linkly 终端",
                    "LINKLY_READY_TERMINAL_REQUIRED"
                );
            }

            var missingSelection = management.Devices.Any(device =>
                device.Enabled
                && device.TerminalId.HasValue
                && !readyIds.Contains(device.TerminalId.Value)
            );
            if (missingSelection)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return ApiResponse<LinklyTerminalManagementDto>.Error(
                    "已选择 Linkly 的 POS 必须对应一台已配对终端",
                    "LINKLY_DEVICE_SELECTION_REQUIRED"
                );
            }

            var enabledTerminalIds = management.Devices
                .Where(device => device.Enabled && device.TerminalId.HasValue)
                .Select(device => device.TerminalId!.Value)
                .ToArray();
            if (enabledTerminalIds.Distinct().Count() != enabledTerminalIds.Length)
            {
                await posmContext.Db.Ado.RollbackTranAsync();
                return LinklyTerminalAssignmentConflict();
            }

            if (mode is null)
            {
                await posmContext.Db.Insertable(new PaymentLinklyConfigurationModeRecord
                {
                    StoreCode = scope.StoreCode!,
                    Environment = scope.Environment!,
                    Mode = "Active",
                    UpdatedAt = now,
                    UpdatedBy = updater,
                }).ExecuteCommandAsync();
            }
            else
            {
                mode.Mode = "Active";
                mode.LegacyPairingAttemptId = null;
                mode.LegacyPairingLeaseExpiresAt = null;
                mode.UpdatedAt = now;
                mode.UpdatedBy = updater;
                await posmContext.Db.Updateable(mode).ExecuteCommandAsync();
            }

            await posmContext.Db.Ado.CommitTranAsync();
        }
        catch
        {
            await posmContext.Db.Ado.RollbackTranAsync();
            throw;
        }

        return ApiResponse<LinklyTerminalManagementDto>.OK(
            await BuildLinklyTerminalManagementAsync(scope.StoreCode!, scope.Environment!),
            "Linkly 多终端配置已启用"
        );
    }

    private async Task<List<PaymentTerminalStoreOptionDto>> GetStoreOptionsAsync()
    {
        return await mainContext.Db.Queryable<Store>()
            .Where(store => store.IsActive && !store.IsDeleted)
            .OrderBy(store => store.StoreCode)
            .Select(store => new PaymentTerminalStoreOptionDto
            {
                StoreCode = store.StoreCode,
                StoreName = store.StoreName,
            })
            .ToListAsync();
    }

    private async Task<List<PaymentTerminalEnvironmentStatusDto>> GetSquareStatusesAsync()
    {
        var rows = await posmContext.Db.Queryable<PaymentSquareTokenRecord>()
            .Where(row => row.Environment == "Production" || row.Environment == "Sandbox")
            .ToListAsync();

        return PaymentEnvironments
            .Select(environment =>
            {
                var active = rows
                    .Where(row =>
                        row.Environment == environment
                        && row.IsEnabled
                        && !string.IsNullOrWhiteSpace(row.AccessToken)
                    )
                    .OrderByDescending(row => row.UpdatedAt)
                    .ThenByDescending(row => row.Id)
                    .FirstOrDefault();

                return new PaymentTerminalEnvironmentStatusDto
                {
                    Environment = environment,
                    Configured = active is not null,
                    Enabled = active?.IsEnabled ?? false,
                    UpdatedAtUtc = active?.UpdatedAt,
                    UpdatedBy = active?.UpdatedBy,
                };
            })
            .ToList();
    }

    private async Task<List<LinklyCloudCredentialAdminDto>> GetLinklyStatusesAsync(string storeCode)
    {
        var rows = (await posmContext.Db.Queryable<PaymentLinklyCredentialRecord>()
                .Where(row =>
                    row.StoreCode == storeCode
                    && (row.Environment == "Production" || row.Environment == "Sandbox")
                )
                .ToListAsync())
            .ToList();

        return PaymentEnvironments
            .Select(environment =>
            {
                var credential = rows
                    .Where(row => row.Environment == environment)
                    .OrderByDescending(row => row.UpdatedAt)
                    .ThenByDescending(row => row.Id)
                    .FirstOrDefault();

                return new LinklyCloudCredentialAdminDto
                {
                    StoreCode = storeCode,
                    Environment = environment,
                    Username = credential?.Username,
                    HasPassword = !string.IsNullOrWhiteSpace(credential?.Password),
                    UpdatedAtUtc = credential?.UpdatedAt,
                    UpdatedBy = credential?.UpdatedBy,
                };
            })
            .ToList();
    }

    private async Task<LinklyTerminalManagementDto> BuildLinklyTerminalManagementAsync(
        string storeCode,
        string environment,
        bool lockConfigurationModeForUpdate = false
    )
    {
        var terminalRows = await posmContext.Db.Queryable<PaymentLinklyTerminalRecord>()
            .Where(row => row.StoreCode == storeCode && row.Environment == environment)
            .OrderBy(row => row.LaneNo)
            .ToListAsync();
        var selectionRows = await posmContext.Db.Queryable<PaymentLinklyDeviceSelectionRecord>()
            .Where(row => row.StoreCode == storeCode && row.Environment == environment)
            .ToListAsync();
        var deviceRows = await SelectLinklyDeviceCandidates(
                posmContext.Db.Queryable<POSM_设备注册信息表>()
                    .Where(row => row.分店代码 == storeCode && row.设备类型 == "POS")
            )
            .ToListAsync();

        var selectionsByDevice = selectionRows.ToDictionary(
            row => row.DeviceCode,
            StringComparer.OrdinalIgnoreCase
        );
        var selectedCounts = selectionRows
            .GroupBy(row => row.TerminalId)
            .ToDictionary(group => group.Key, group => group.Count());

        var deviceCodes = deviceRows
            .Select(device => device.DeviceCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new LinklyTerminalManagementDto
        {
            StoreCode = storeCode,
            Environment = environment,
            Mode = lockConfigurationModeForUpdate
                ? (await GetLinklyConfigurationModeForUpdateAsync(storeCode, environment))?.Mode ?? "Legacy"
                : await GetLinklyConfigurationModeAsync(storeCode, environment),
            Terminals = terminalRows.Select(row => new LinklyTerminalAdminDto
            {
                TerminalId = row.TerminalId,
                StoreCode = row.StoreCode,
                Environment = row.Environment,
                LaneNo = row.LaneNo,
                DisplayName = row.DisplayName,
                UsernameMasked = MaskLinklyUsername(row.Username),
                // version 0 是历史明文，不视为可用凭据，避免管理员误以为可以安全启用。
                HasPassword = row.CredentialProtectionVersion
                        == LinklyCloudTerminalCredentialDataProtection.CurrentVersion
                    && !string.IsNullOrWhiteSpace(row.Password),
                PairingState = row.PairingState == "Ready"
                    && (row.CredentialProtectionVersion
                            != LinklyCloudTerminalCredentialDataProtection.CurrentVersion
                        || string.IsNullOrWhiteSpace(row.Secret)
                        || string.IsNullOrWhiteSpace(row.PosId))
                        ? "NeedsRepair"
                        : row.PairingState,
                LastHealthStatus = row.LastHealthStatus,
                LastHealthAtUtc = row.LastHealthAt,
                SelectedDeviceCount = selectedCounts.GetValueOrDefault(row.TerminalId),
                UpdatedAtUtc = row.UpdatedAt,
                UpdatedBy = row.UpdatedBy,
            }).ToList(),
            Devices = deviceRows.Select(device =>
                {
                    selectionsByDevice.TryGetValue(device.DeviceCode, out var selection);
                    return new LinklyTerminalDeviceAdminDto
                    {
                        DeviceCode = device.DeviceCode,
                        DeviceSystem = device.DeviceSystem,
                        Enabled = device.Enabled,
                        DeviceMissing = false,
                        TerminalId = selection?.TerminalId,
                        Revision = selection?.Revision ?? 0,
                    };
                })
                // 不能让已删除设备的选择从管理视图消失，否则它会永久占用一台实体终端。
                .Concat(selectionRows
                    .Where(selection => !deviceCodes.Contains(selection.DeviceCode))
                    .Select(selection => new LinklyTerminalDeviceAdminDto
                    {
                        DeviceCode = selection.DeviceCode,
                        DeviceSystem = string.Empty,
                        Enabled = false,
                        DeviceMissing = true,
                        TerminalId = selection.TerminalId,
                        Revision = selection.Revision,
                    }))
                .OrderBy(device => device.DeviceCode, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private async Task<string> GetLinklyConfigurationModeAsync(string storeCode, string environment)
    {
        var row = await posmContext.Db.Queryable<PaymentLinklyConfigurationModeRecord>()
            .Where(item => item.StoreCode == storeCode && item.Environment == environment)
            .FirstAsync();
        return row?.Mode ?? "Legacy";
    }

    private async Task<PaymentLinklyConfigurationModeRecord?> GetLinklyConfigurationModeForUpdateAsync(
        string storeCode,
        string environment
    )
    {
        if (posmContext.Db.CurrentConnectionConfig.DbType == SqlSugar.DbType.SqlServer)
        {
            return await posmContext.Db.Ado.SqlQuerySingleAsync<PaymentLinklyConfigurationModeRecord>(
                LinklyConfigurationModeUpdateLockSql,
                new SugarParameter("@StoreCode", storeCode),
                new SugarParameter("@Environment", environment)
            );
        }

        return await posmContext.Db.Queryable<PaymentLinklyConfigurationModeRecord>()
            .Where(row => row.StoreCode == storeCode && row.Environment == environment)
            .FirstAsync();
    }

    // 只有已确认且明确结束的会话可放行；未知状态即使误写 ACK 也不能释放线路。
    // SupervisorResolved 是 POS API 在主管结案时写入的终态，与 Linkly 终态同样放行（下面三个谓词口径一致）。
    private async Task<bool> HasBlockingLinklySessionAsync(
        Guid terminalId,
        string storeCode,
        string environment
    )
    {
        return await WithLinklyUpdateLock(
                posmContext.Db,
                posmContext.Db.Queryable<PaymentLinklyBackendSessionRecord>()
                    .Where(row => row.TerminalId == terminalId
                        && row.StoreCode == storeCode
                        && row.Environment == environment
                        && (row.IsActive
                            || row.ClientAcknowledgedAt == null
                            || (row.Status != "Completed"
                                && row.Status != "Cancelled"
                                && row.Status != "Failed"
                                && row.Status != "NotSubmitted"
                                && row.Status != "SupervisorResolved")))
            )
            .AnyAsync();
    }

    private async Task<bool> HasBlockingLinklyDeviceSessionAsync(
        string deviceCode,
        string storeCode,
        string environment
    )
    {
        return await WithLinklyUpdateLock(
                posmContext.Db,
                posmContext.Db.Queryable<PaymentLinklyBackendSessionRecord>()
                    .Where(row => row.DeviceCode == deviceCode
                        && row.StoreCode == storeCode
                        && row.Environment == environment
                        && (row.IsActive
                            || row.ClientAcknowledgedAt == null
                            || (row.Status != "Completed"
                                && row.Status != "Cancelled"
                                && row.Status != "Failed"
                                && row.Status != "NotSubmitted"
                                && row.Status != "SupervisorResolved")))
            )
            .AnyAsync();
    }

    private async Task<bool> HasBlockingLinklyScopeSessionAsync(
        string storeCode,
        string environment
    )
    {
        return await WithLinklyUpdateLock(
                posmContext.Db,
                posmContext.Db.Queryable<PaymentLinklyBackendSessionRecord>()
                    .Where(row => row.StoreCode == storeCode
                        && row.Environment == environment
                        && (row.IsActive
                            || row.ClientAcknowledgedAt == null
                            || (row.Status != "Completed"
                                && row.Status != "Cancelled"
                                && row.Status != "Failed"
                                && row.Status != "NotSubmitted"
                                && row.Status != "SupervisorResolved")))
            )
            .AnyAsync();
    }

    internal static ISugarQueryable<T> WithLinklyUpdateLock<T>(
        ISqlSugarClient db,
        ISugarQueryable<T> query
    )
    {
        return db.CurrentConnectionConfig.DbType == SqlSugar.DbType.SqlServer
            ? query.With(SqlWith.UpdLock)
            : query;
    }

    internal static ISugarQueryable<PaymentTerminalDeviceCandidate> SelectLinklyDeviceCandidates(
        ISugarQueryable<POSM_设备注册信息表> query
    )
    {
        return query.Select(row => new PaymentTerminalDeviceCandidate
        {
            DeviceCode = row.系统设备编号,
            DeviceSystem = row.设备系统,
            DeviceStatus = row.设备状态,
            AllowsTransactions = row.是否允许交易,
        });
    }

    private async Task EnsureLinklyConfigurationDraftAsync(
        string storeCode,
        string environment,
        string? updatedBy,
        DateTime now
    )
    {
        var mode = await GetLinklyConfigurationModeForUpdateAsync(storeCode, environment);
        if (mode is null)
        {
            await posmContext.Db.Insertable(new PaymentLinklyConfigurationModeRecord
            {
                StoreCode = storeCode,
                Environment = environment,
                Mode = "Draft",
                UpdatedAt = now,
                UpdatedBy = updatedBy,
            }).ExecuteCommandAsync();
            return;
        }

        if (mode.Mode == "Legacy")
        {
            mode.Mode = "Draft";
            mode.UpdatedAt = now;
            mode.UpdatedBy = updatedBy;
            await posmContext.Db.Updateable(mode).ExecuteCommandAsync();
        }
    }

    private async Task<ApiResponse<LinklyTerminalManagementDto>?> FindLinklyTerminalConflictAsync(
        string storeCode,
        string environment,
        int laneNo,
        string displayName,
        string username,
        Guid? excludedTerminalId
    )
    {
        var query = posmContext.Db.Queryable<PaymentLinklyTerminalRecord>()
            .Where(row => row.StoreCode == storeCode && row.Environment == environment);
        if (excludedTerminalId.HasValue)
        {
            var excludedId = excludedTerminalId.Value;
            query = query.Where(row => row.TerminalId != excludedId);
        }

        if (await query.AnyAsync(row => row.LaneNo == laneNo))
        {
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "该 Lane 编号已被其他终端使用",
                "LINKLY_TERMINAL_LANE_CONFLICT"
            );
        }

        if (await query.AnyAsync(row => row.Username == username))
        {
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "该 Linkly 用户名已被其他终端使用",
                "LINKLY_TERMINAL_USERNAME_CONFLICT"
            );
        }

        if (await query.AnyAsync(row => row.DisplayName == displayName))
        {
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "该终端名称已存在",
                "LINKLY_TERMINAL_DISPLAY_NAME_CONFLICT"
            );
        }

        return null;
    }

    private static (
        string? StoreCode,
        string? Environment,
        ApiResponse<LinklyTerminalManagementDto>? Error
    ) NormalizeLinklyScope(string? storeCode, string? environment)
    {
        var normalizedStoreCode = NormalizeOptional(storeCode);
        if (normalizedStoreCode is null)
        {
            return (
                null,
                null,
                ApiResponse<LinklyTerminalManagementDto>.Error(
                    "门店编码不能为空",
                    "LINKLY_STORE_CODE_REQUIRED"
                )
            );
        }

        var normalizedEnvironment = NormalizeEnvironment(environment);
        if (normalizedEnvironment is null)
        {
            return (
                normalizedStoreCode,
                null,
                ApiResponse<LinklyTerminalManagementDto>.Error(
                    "支付环境必须是 Production 或 Sandbox",
                    "PAYMENT_ENVIRONMENT_INVALID"
                )
            );
        }

        return (normalizedStoreCode, normalizedEnvironment, null);
    }

    private static ApiResponse<LinklyTerminalManagementDto>? ValidateLinklyTerminalFields(
        int laneNo,
        string? displayName,
        string? username,
        string? password,
        bool passwordRequired
    )
    {
        if (laneNo is < 1 or > 9999)
        {
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "Lane 编号必须在1到9999之间",
                "LINKLY_TERMINAL_LANE_INVALID"
            );
        }

        if (displayName is null)
        {
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "终端名称不能为空",
                "LINKLY_TERMINAL_DISPLAY_NAME_REQUIRED"
            );
        }

        if (username is null)
        {
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "Linkly 用户名不能为空",
                "LINKLY_USERNAME_REQUIRED"
            );
        }

        if (passwordRequired && password is null)
        {
            return ApiResponse<LinklyTerminalManagementDto>.Error(
                "Linkly 密码不能为空",
                "LINKLY_PASSWORD_REQUIRED"
            );
        }

        return null;
    }

    private static ApiResponse<LinklyTerminalManagementDto> LinklyCredentialProtectionFailure() =>
        ApiResponse<LinklyTerminalManagementDto>.Error(
            "Linkly 凭据无法安全保存，请稍后重试",
            "LINKLY_TERMINAL_CREDENTIAL_PROTECTION_FAILED"
        );

    private static ApiResponse<LinklyTerminalManagementDto> LinklyCredentialReentryRequired() =>
        ApiResponse<LinklyTerminalManagementDto>.Error(
            "检测到需要重新录入的历史 Linkly 凭据，请先保存该终端的新密码",
            "LINKLY_TERMINAL_CREDENTIAL_REENTRY_REQUIRED"
        );

    private static string MaskLinklyUsername(string username)
    {
        if (string.IsNullOrEmpty(username))
        {
            return string.Empty;
        }

        if (username.Length <= 9)
        {
            return new string('•', username.Length);
        }

        var prefixLength = Math.Min(4, username.Length - 3);
        var suffixLength = Math.Min(3, username.Length - prefixLength);
        var hiddenLength = username.Length - prefixLength - suffixLength;
        return string.Concat(
            username.AsSpan(0, prefixLength),
            new string('•', hiddenLength),
            username.AsSpan(username.Length - suffixLength, suffixLength)
        );
    }

    private static bool IsUniqueConstraintViolation(SqlSugarException exception)
    {
        var details = exception.ToString();
        return details.Contains("2601", StringComparison.OrdinalIgnoreCase)
            || details.Contains("2627", StringComparison.OrdinalIgnoreCase)
            || details.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalAssignmentConstraintViolation(SqlSugarException exception)
    {
        var details = exception.ToString();
        return details.Contains(
                "UX_POSM_LinklyCloudDeviceSelection_Scope_Terminal",
                StringComparison.OrdinalIgnoreCase)
            || details.Contains(
                "POSM_LinklyCloudDeviceSelection.Environment, POSM_LinklyCloudDeviceSelection.StoreCode, POSM_LinklyCloudDeviceSelection.TerminalId",
                StringComparison.OrdinalIgnoreCase);
    }

    private static ApiResponse<LinklyTerminalManagementDto> LinklyTerminalAssignmentConflict()
    {
        return ApiResponse<LinklyTerminalManagementDto>.Error(
            "该 Linkly 终端已分配给另一台 POS",
            "LINKLY_TERMINAL_ASSIGNMENT_CONFLICT"
        );
    }

    private static ApiResponse<LinklyTerminalManagementDto> LinklySelectionRevisionConflict()
    {
        return ApiResponse<LinklyTerminalManagementDto>.Error(
            "终端选择已变化，请刷新后重试",
            "LINKLY_SELECTION_REVISION_CONFLICT"
        );
    }

    private static DateTime NextLinklyTerminalUpdatedAt(DateTime current)
    {
        var now = DateTime.UtcNow;
        return now > current ? now : current.AddTicks(1);
    }

    private static string? ResolveSelectedStoreCode(
        string? requestedStoreCode,
        List<PaymentTerminalStoreOptionDto> stores
    )
    {
        var requested = NormalizeOptional(requestedStoreCode);
        if (requested is not null && stores.Any(store => store.StoreCode == requested))
        {
            return requested;
        }

        return stores.FirstOrDefault()?.StoreCode ?? requested;
    }

    private static string? NormalizeEnvironment(string? environment)
    {
        return (environment ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "PRODUCTION" => "Production",
            "SANDBOX" => "Sandbox",
            _ => null,
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    [SugarTable("POSM_SquareToken")]
    private sealed class PaymentSquareTokenRecord
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public long Id { get; set; }

        public string Environment { get; set; } = string.Empty;

        public string AccessToken { get; set; } = string.Empty;

        public bool IsEnabled { get; set; }

        public DateTime UpdatedAt { get; set; }

        public string? UpdatedBy { get; set; }
    }

    [SugarTable("POSM_LinklyCloudCredential")]
    private sealed class PaymentLinklyCredentialRecord
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public long Id { get; set; }

        public string StoreCode { get; set; } = string.Empty;

        public string Environment { get; set; } = string.Empty;

        public string Username { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        public DateTime UpdatedAt { get; set; }

        public string? UpdatedBy { get; set; }
    }

    [SugarTable("POSM_LinklyCloudTerminal")]
    private sealed class PaymentLinklyTerminalRecord
    {
        [SugarColumn(IsPrimaryKey = true)]
        public Guid TerminalId { get; set; }

        public string Environment { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public int LaneNo { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string? Secret { get; set; }
        public byte CredentialProtectionVersion { get; set; }
        public string? PosId { get; set; }
        public string PairingState { get; set; } = "Unpaired";
        public Guid? PairingAttemptId { get; set; }
        public DateTime? PairingLeaseExpiresAt { get; set; }
        public string? LastHealthStatus { get; set; }
        public DateTime? LastHealthAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public string? UpdatedBy { get; set; }
    }

    [SugarTable("POSM_LinklyCloudDeviceSelection")]
    private sealed class PaymentLinklyDeviceSelectionRecord
    {
        [SugarColumn(IsPrimaryKey = true)]
        public string Environment { get; set; } = string.Empty;
        [SugarColumn(IsPrimaryKey = true)]
        public string StoreCode { get; set; } = string.Empty;
        [SugarColumn(IsPrimaryKey = true)]
        public string DeviceCode { get; set; } = string.Empty;
        public Guid TerminalId { get; set; }
        public long Revision { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
    }

    [SugarTable("POSM_LinklyCloudConfigurationMode")]
    private sealed class PaymentLinklyConfigurationModeRecord
    {
        [SugarColumn(IsPrimaryKey = true)]
        public string Environment { get; set; } = string.Empty;
        [SugarColumn(IsPrimaryKey = true)]
        public string StoreCode { get; set; } = string.Empty;
        public string Mode { get; set; } = "Legacy";
        public Guid? LegacyPairingAttemptId { get; set; }
        public DateTime? LegacyPairingLeaseExpiresAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
    }

    [SugarTable("POSM_LinklyCloudBackendSession")]
    private sealed class PaymentLinklyBackendSessionRecord
    {
        public string Environment { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string DeviceCode { get; set; } = string.Empty;
        public Guid? TerminalId { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime? ClientAcknowledgedAt { get; set; }
        public bool IsActive { get; set; }
    }

    internal sealed class PaymentTerminalDeviceCandidate
    {
        public string DeviceCode { get; set; } = string.Empty;
        public string DeviceSystem { get; set; } = string.Empty;
        public int DeviceStatus { get; set; }
        public bool AllowsTransactions { get; set; }

        // SQL Server 不能在 SELECT 中直接返回布尔谓词；原始列落地后再计算启用状态。
        public bool Enabled => DeviceStatus == 1 && AllowsTransactions;
    }
}
