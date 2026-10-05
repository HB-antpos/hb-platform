using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using BlazorApp.Shared.Security;
using Hbpos.Api.Data;
using Hbpos.Contracts.Devices;
using SqlSugar;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace Hbpos.Api.Services;

public sealed record DeviceActivationRebindContext(
    string DeviceCode,
    string StoreCode,
    string HardwareId,
    string DeviceSystem);

public interface IDeviceActivationCodeService
{
    Task<DeviceActivationCodePreviewResponse> PreviewAsync(
        DeviceActivationCodePreviewRequest request,
        CancellationToken cancellationToken);

    Task<DeviceActivationCodeRedeemResponse> RedeemAsync(
        DeviceActivationCodeRedeemRequest request,
        bool recoveryOnly,
        CancellationToken cancellationToken);

    Task<DeviceActivationCodeRedeemResponse> RebindAsync(
        DeviceActivationCodeRebindRequest request,
        DeviceActivationRebindContext currentDevice,
        CancellationToken cancellationToken);
}

public sealed class DeviceActivationCodeService : IDeviceActivationCodeService
{
    private const int PendingStatus = -1;
    private const int DisabledStatus = 0;
    private const int EnabledStatus = 1;
    private const int LockedStatus = 2;
    private const int UnregisteredStatus = 3;
    private const string CreatedBy = "HBPOS_ACTIVATION";

    internal static IReadOnlyList<string> LockOrderForTests { get; } =
    [
        "store",
        "grant",
        "hardware",
    ];

    internal readonly record struct GrantGateDecision(
        bool IsAllowed,
        bool IsRecovery,
        string? ReasonCode);

    internal readonly record struct PreviewGrantGateDecision(
        bool IsAllowed,
        string DeviceSystem,
        string? ReasonCode);

    private const string AcquireApplicationLockSql = """
        DECLARE @Result int;
        EXEC @Result = sys.sp_getapplock
            @Resource = @Resource,
            @LockMode = N'Exclusive',
            @LockOwner = N'Transaction',
            @LockTimeout = 5000;
        SELECT @Result;
        """;

    private const string LockGrantSql = """
        SELECT TOP 1
            [GrantId], [SecretHash], [StoreCode], [DeviceSystem],
            [CreatedAtUtc], [CreatedBy], [Reason], [ExpiresAtUtc],
            [RevokedAtUtc], [RevokedBy], [RevokeReason],
            [ConsumedAtUtc], [ConsumedHardwareId], [ConsumedDeviceCode],
            [ConsumedDeviceRegistrationId], [ConsumedAuthorizationHash],
            [ConsumedDeviceSystem], [ConsumptionKind],
            [PreviousStoreCode], [PreviousDeviceCode], [RowVersion]
        FROM [dbo].[POSM_DeviceActivationGrant] WITH (UPDLOCK, HOLDLOCK)
        WHERE [GrantId] = @GrantId;
        """;

    private const string LockHardwareRegistrationsSql = """
        SELECT
            [ID] AS Id,
            [系统设备编号] AS DeviceCode,
            [分店代码] AS StoreCode,
            [设备硬件识别码] AS HardwareId,
            [设备状态] AS DeviceStatus,
            [设备授权码] AS AuthorizationCode,
            [设备系统] AS DeviceSystem
        FROM [dbo].[POSM_设备注册信息表] WITH (UPDLOCK, HOLDLOCK)
        WHERE [设备硬件识别码] = @HardwareId
        ORDER BY [ID] DESC;
        """;

    private readonly HbposSqlSugarContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeviceActivationCodeService> _logger;
    private readonly string _mainDatabaseName;

    public DeviceActivationCodeService(
        HbposSqlSugarContext dbContext,
        IConfiguration configuration,
        ILogger<DeviceActivationCodeService> logger,
        TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _mainDatabaseName = ResolveDatabaseName(
            configuration.GetConnectionString("MainConnection")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Main database connection string is required."));
    }

    public async Task<DeviceActivationCodePreviewResponse> PreviewAsync(
        DeviceActivationCodePreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (!DeviceActivationCodeCodec.TryParse(request.ActivationCode, out var parsed))
        {
            return PreviewDenied(
                DeviceActivationReasonCodes.NotAvailable,
                "Device activation code is not available.");
        }
        var grant = await _dbContext.PosmDb.Queryable<DeviceActivationCodeGrant>()
            .Where(item => item.GrantId == parsed.GrantId)
            .FirstAsync(cancellationToken);
        var decision = EvaluatePreviewGrantGate(
            grant,
            parsed.Secret,
            request.DeviceSystem,
            UtcNow());
        if (!decision.IsAllowed)
        {
            return PreviewDenied(
                decision.ReasonCode!,
                decision.ReasonCode == DeviceActivationReasonCodes.PlatformMismatch
                    ? "Device platform does not match the activation code."
                    : "Device activation code is not available.");
        }

        var store = await LoadActiveStoreAsync(grant!.StoreCode, cancellationToken);
        if (store == null)
        {
            return PreviewDenied(
                DeviceActivationReasonCodes.StoreUnavailable,
                "Target store is unavailable.");
        }

        return new DeviceActivationCodePreviewResponse(
            true,
            null,
            store.StoreCode,
            SanitizeStoreNameForResponse(store.StoreName),
            decision.DeviceSystem,
            DeviceActivationCodeCodec.NormalizeUtcForWire(grant!.ExpiresAtUtc),
            "Device activation code is ready.");
    }

    public Task<DeviceActivationCodeRedeemResponse> RedeemAsync(
        DeviceActivationCodeRedeemRequest request,
        CancellationToken cancellationToken) =>
        RedeemAsync(request, recoveryOnly: false, cancellationToken);

    public async Task<DeviceActivationCodeRedeemResponse> RedeemAsync(
        DeviceActivationCodeRedeemRequest request,
        bool recoveryOnly,
        CancellationToken cancellationToken)
    {
        if (DeviceActivationCodeCodec.ContainsReservedActivationCode(request.HardwareId)
            || DeviceActivationCodeCodec.ContainsReservedActivationCode(request.TerminalName))
        {
            return RedeemDenied(DeviceActivationReasonCodes.DeviceStateConflict);
        }

        var hardwareId = Normalize(request.HardwareId);
        var terminalName = Normalize(request.TerminalName) ?? string.Empty;
        if (!DeviceActivationCodeCodec.TryParse(request.ActivationCode, out var parsed))
        {
            return RedeemDenied(DeviceActivationReasonCodes.NotAvailable);
        }
        if (hardwareId == null || hardwareId.Length > 100 || terminalName.Length > 200)
        {
            return RedeemDenied(DeviceActivationReasonCodes.DeviceStateConflict);
        }
        var platformIsValid = DeviceSystems.TryNormalize(request.DeviceSystem, out var deviceSystem);

        var initialGrant = await FindGrantAsync(parsed.GrantId, cancellationToken);
        if (initialGrant == null
            || !DeviceActivationCodeCodec.Matches(initialGrant.SecretHash, parsed.Secret))
        {
            return RedeemDenied(DeviceActivationReasonCodes.NotAvailable);
        }
        DeviceActivationCodeRedeemResponse? response = null;
        await ExecuteTransactionAsync(async () =>
        {
            await AcquireApplicationLockAsync($"HBPOS:ActivationStore:{initialGrant.StoreCode}");
            await AcquireGrantLockAsync(parsed.GrantId);
            var grant = await LockGrantAsync(parsed.GrantId);
            if (grant == null
                || !DeviceActivationCodeCodec.Matches(grant.SecretHash, parsed.Secret))
            {
                response = RedeemDenied(DeviceActivationReasonCodes.NotAvailable);
                return;
            }
            var decision = EvaluateRedeemGrantGate(
                grant,
                hardwareId,
                platformIsValid,
                deviceSystem,
                UtcNow(),
                recoveryOnly);
            if (!decision.IsAllowed)
            {
                response = RedeemDenied(decision.ReasonCode!);
                return;
            }

            if (decision.IsRecovery)
            {
                var recoveryStore = await LoadActiveStoreInActivationTransactionAsync(grant.StoreCode);
                if (recoveryStore == null)
                {
                    response = RedeemDenied(DeviceActivationReasonCodes.StoreUnavailable);
                    return;
                }
                await AcquireHardwareLockAsync(hardwareId);
                response = await RecoverConsumedGrantAsync(
                    grant,
                    hardwareId,
                    deviceSystem,
                    recoveryStore);
                return;
            }

            var lockedStore = await LoadActiveStoreInActivationTransactionAsync(grant.StoreCode);
            if (lockedStore == null)
            {
                response = RedeemDenied(DeviceActivationReasonCodes.StoreUnavailable);
                return;
            }
            await AcquireHardwareLockAsync(hardwareId);

            var registrations = await LockHardwareRegistrationsAsync(hardwareId);
            if (registrations.Any(item => item.DeviceStatus is EnabledStatus or LockedStatus))
            {
                response = RedeemDenied(DeviceActivationReasonCodes.DeviceConflict);
                return;
            }

            var authorizationCode = Guid.NewGuid().ToString("N");
            var target = registrations.FirstOrDefault(item =>
                item.DeviceStatus is PendingStatus or DisabledStatus
                && string.Equals(item.StoreCode, lockedStore.StoreCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.DeviceSystem, deviceSystem, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(item.DeviceCode));
            await DisableOtherPendingRegistrationsAsync(
                registrations,
                target?.Id,
                lockedStore.StoreCode);
            int registrationId;
            string deviceCode;
            if (target != null)
            {
                var affected = await EnableExistingRegistrationAsync(
                    target,
                    authorizationCode,
                    terminalName,
                    deviceSystem);
                if (affected != 1)
                {
                    throw new InvalidOperationException("Target device registration changed during activation.");
                }
                registrationId = target.Id;
                deviceCode = target.DeviceCode!;
            }
            else
            {
                deviceCode = await CreateAvailableDeviceCodeAsync(lockedStore.StoreCode);
                registrationId = await CreateEnabledRegistrationAsync(
                    hardwareId,
                    deviceCode,
                    lockedStore.StoreCode,
                    deviceSystem,
                    authorizationCode,
                    terminalName);
            }

            var consumed = await ConsumeGrantAsync(
                grant.GrantId,
                hardwareId,
                deviceCode,
                registrationId,
                deviceSystem,
                authorizationCode,
                "Initial",
                null,
                null);
            if (consumed != 1)
            {
                throw new InvalidOperationException("Device activation grant was not consumed atomically.");
            }

            response = Activated(
                deviceCode,
                lockedStore,
                authorizationCode,
                DeviceActivationReasonCodes.Activated);
        });

        if (response?.IsAllowed == true)
        {
            _logger.LogInformation(
                "设备开通码已兑换，GrantId={GrantId}, StoreCode={StoreCode}, DeviceCode={DeviceCode}, DeviceSystem={DeviceSystem}",
                parsed.GrantId,
                response.StoreCode,
                response.DeviceCode,
                deviceSystem);
        }
        return response ?? RedeemDenied(DeviceActivationReasonCodes.NotAvailable);
    }

    public async Task<DeviceActivationCodeRedeemResponse> RebindAsync(
        DeviceActivationCodeRebindRequest request,
        DeviceActivationRebindContext currentDevice,
        CancellationToken cancellationToken)
    {
        if (DeviceActivationCodeCodec.ContainsReservedActivationCode(currentDevice.HardwareId)
            || DeviceActivationCodeCodec.ContainsReservedActivationCode(request.TerminalName))
        {
            return RedeemDenied(DeviceActivationReasonCodes.DeviceStateConflict);
        }

        var currentDeviceCode = Normalize(currentDevice.DeviceCode);
        var currentStoreCode = Normalize(currentDevice.StoreCode);
        var hardwareId = Normalize(currentDevice.HardwareId);
        if (currentDeviceCode == null
            || currentStoreCode == null
            || hardwareId == null
            || hardwareId.Length > 100
            || !DeviceSystems.TryNormalize(currentDevice.DeviceSystem, out var deviceSystem))
        {
            return RedeemDenied(DeviceActivationReasonCodes.DeviceStateConflict);
        }
        if (!DeviceActivationCodeCodec.TryParse(request.ActivationCode, out var parsed))
        {
            return RedeemDenied(DeviceActivationReasonCodes.NotAvailable);
        }

        var initialGrant = await FindGrantAsync(parsed.GrantId, cancellationToken);
        if (initialGrant == null
            || !DeviceActivationCodeCodec.Matches(initialGrant.SecretHash, parsed.Secret))
        {
            return RedeemDenied(DeviceActivationReasonCodes.NotAvailable);
        }
        var terminalName = Normalize(request.TerminalName) ?? string.Empty;
        if (terminalName.Length > 200)
        {
            return RedeemDenied(DeviceActivationReasonCodes.DeviceStateConflict);
        }
        DeviceActivationCodeRedeemResponse? response = null;
        await ExecuteTransactionAsync(async () =>
        {
            await AcquireApplicationLockAsync($"HBPOS:ActivationStore:{initialGrant.StoreCode}");
            await AcquireGrantLockAsync(parsed.GrantId);
            var grant = await LockGrantAsync(parsed.GrantId);
            if (grant == null
                || !DeviceActivationCodeCodec.Matches(grant.SecretHash, parsed.Secret))
            {
                response = RedeemDenied(DeviceActivationReasonCodes.NotAvailable);
                return;
            }
            var decision = EvaluateRebindGrantGate(
                grant,
                hardwareId,
                deviceSystem,
                currentStoreCode,
                currentDeviceCode,
                UtcNow());
            if (!decision.IsAllowed)
            {
                response = RedeemDenied(decision.ReasonCode!);
                return;
            }

            if (decision.IsRecovery)
            {
                var recoveryStore = await LoadActiveStoreInActivationTransactionAsync(
                    grant.StoreCode);
                if (recoveryStore == null)
                {
                    response = RedeemDenied(DeviceActivationReasonCodes.StoreUnavailable);
                    return;
                }
                await AcquireHardwareLockAsync(hardwareId);
                response = await RecoverConsumedGrantAsync(
                    grant,
                    hardwareId,
                    deviceSystem,
                    recoveryStore);
                return;
            }

            var lockedTargetStore = await LoadActiveStoreInActivationTransactionAsync(
                grant.StoreCode);
            if (lockedTargetStore == null)
            {
                response = RedeemDenied(DeviceActivationReasonCodes.StoreUnavailable);
                return;
            }
            await AcquireHardwareLockAsync(hardwareId);

            var registrations = await LockHardwareRegistrationsAsync(hardwareId);
            var source = registrations.FirstOrDefault(item =>
                item.DeviceStatus == EnabledStatus
                && string.Equals(item.DeviceCode, currentDeviceCode, StringComparison.Ordinal)
                && string.Equals(item.StoreCode, currentStoreCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.DeviceSystem, deviceSystem, StringComparison.Ordinal));
            if (source == null
                || registrations.Any(item =>
                    item.Id != source.Id && item.DeviceStatus is EnabledStatus or LockedStatus))
            {
                response = RedeemDenied(DeviceActivationReasonCodes.DeviceStateConflict);
                return;
            }

            var target = registrations.FirstOrDefault(item =>
                item.DeviceStatus is PendingStatus or DisabledStatus
                && string.Equals(item.StoreCode, lockedTargetStore.StoreCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.DeviceSystem, deviceSystem, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(item.DeviceCode));
            await DisableOtherPendingRegistrationsAsync(
                registrations,
                target?.Id,
                lockedTargetStore.StoreCode);
            var disabled = await DisableSourceRegistrationAsync(source, lockedTargetStore.StoreCode);
            if (disabled != 1)
            {
                throw new InvalidOperationException("Current device registration changed during rebind.");
            }

            var authorizationCode = Guid.NewGuid().ToString("N");
            int targetRegistrationId;
            string targetDeviceCode;
            if (target != null)
            {
                var enabled = await EnableExistingRegistrationAsync(
                    target,
                    authorizationCode,
                    terminalName,
                    deviceSystem);
                if (enabled != 1)
                {
                    throw new InvalidOperationException("Target device registration changed during rebind.");
                }
                targetRegistrationId = target.Id;
                targetDeviceCode = target.DeviceCode!;
            }
            else
            {
                targetDeviceCode = await CreateAvailableDeviceCodeAsync(lockedTargetStore.StoreCode);
                targetRegistrationId = await CreateEnabledRegistrationAsync(
                    hardwareId,
                    targetDeviceCode,
                    lockedTargetStore.StoreCode,
                    deviceSystem,
                    authorizationCode,
                    terminalName);
            }

            var consumed = await ConsumeGrantAsync(
                grant.GrantId,
                hardwareId,
                targetDeviceCode,
                targetRegistrationId,
                deviceSystem,
                authorizationCode,
                "Rebind",
                currentStoreCode,
                currentDeviceCode);
            if (consumed != 1)
            {
                throw new InvalidOperationException("Device rebind grant was not consumed atomically.");
            }

            response = Activated(
                targetDeviceCode,
                lockedTargetStore,
                authorizationCode,
                DeviceActivationReasonCodes.Activated);
        });

        if (response?.IsAllowed == true)
        {
            _logger.LogInformation(
                "设备已用开通码换店，GrantId={GrantId}, PreviousStoreCode={PreviousStoreCode}, StoreCode={StoreCode}, DeviceCode={DeviceCode}",
                parsed.GrantId,
                currentStoreCode,
                response.StoreCode,
                response.DeviceCode);
        }
        return response ?? RedeemDenied(DeviceActivationReasonCodes.NotAvailable);
    }

    internal static GrantGateDecision EvaluateRedeemGrantGate(
        DeviceActivationCodeGrant grant,
        string hardwareId,
        bool platformIsValid,
        string deviceSystem,
        DateTime utcNow,
        bool recoveryOnly = false)
    {
        // 匿名入口必须先收敛不可用状态，避免通过平台或门店错误枚举真实开通码。
        if (grant.ConsumedAtUtc != null)
        {
            var ownsConsumption = platformIsValid
                && string.Equals(grant.ConsumedHardwareId, hardwareId, StringComparison.Ordinal)
                && string.Equals(grant.ConsumedDeviceSystem, deviceSystem, StringComparison.Ordinal);
            return ownsConsumption
                ? new GrantGateDecision(true, true, null)
                : new GrantGateDecision(false, false, DeviceActivationReasonCodes.NotAvailable);
        }
        if (grant.RevokedAtUtc != null || grant.ExpiresAtUtc <= utcNow)
        {
            return new GrantGateDecision(false, false, DeviceActivationReasonCodes.NotAvailable);
        }
        // 恢复专用请求绝不能消费仍可用的码，避免 rebind 认证失败后的 fallback 意外开通设备。
        if (recoveryOnly)
        {
            return new GrantGateDecision(false, false, DeviceActivationReasonCodes.NotAvailable);
        }
        if (!platformIsValid
            || !string.Equals(grant.DeviceSystem, deviceSystem, StringComparison.Ordinal))
        {
            return new GrantGateDecision(false, false, DeviceActivationReasonCodes.PlatformMismatch);
        }
        return new GrantGateDecision(true, false, null);
    }

    internal static PreviewGrantGateDecision EvaluatePreviewGrantGate(
        DeviceActivationCodeGrant? grant,
        ReadOnlySpan<byte> secret,
        string requestedDeviceSystem,
        DateTime utcNow)
    {
        // 先收敛未知、过期、撤销和已消费状态，匿名预览不能借平台错误枚举真实码。
        if (!IsAvailableGrant(grant, secret, utcNow))
        {
            return new PreviewGrantGateDecision(
                false,
                string.Empty,
                DeviceActivationReasonCodes.NotAvailable);
        }
        if (!DeviceSystems.TryNormalize(requestedDeviceSystem, out var deviceSystem)
            || !string.Equals(grant!.DeviceSystem, deviceSystem, StringComparison.Ordinal))
        {
            return new PreviewGrantGateDecision(
                false,
                string.Empty,
                DeviceActivationReasonCodes.PlatformMismatch);
        }
        return new PreviewGrantGateDecision(true, deviceSystem, null);
    }

    internal static GrantGateDecision EvaluateRebindGrantGate(
        DeviceActivationCodeGrant grant,
        string hardwareId,
        string deviceSystem,
        string currentStoreCode,
        string currentDeviceCode,
        DateTime utcNow)
    {
        if (grant.ConsumedAtUtc != null)
        {
            var ownsRebindConsumption = string.Equals(
                    grant.ConsumptionKind,
                    "Rebind",
                    StringComparison.Ordinal)
                && string.Equals(
                    grant.PreviousStoreCode,
                    currentStoreCode,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    grant.PreviousDeviceCode,
                    currentDeviceCode,
                    StringComparison.Ordinal)
                && string.Equals(
                    grant.ConsumedHardwareId,
                    hardwareId,
                    StringComparison.Ordinal)
                && string.Equals(
                    grant.ConsumedDeviceSystem,
                    deviceSystem,
                    StringComparison.Ordinal);
            return ownsRebindConsumption
                ? new GrantGateDecision(true, true, null)
                : new GrantGateDecision(false, false, DeviceActivationReasonCodes.NotAvailable);
        }
        if (grant.RevokedAtUtc != null || grant.ExpiresAtUtc <= utcNow)
        {
            return new GrantGateDecision(false, false, DeviceActivationReasonCodes.NotAvailable);
        }
        if (!string.Equals(grant.DeviceSystem, deviceSystem, StringComparison.Ordinal))
        {
            return new GrantGateDecision(false, false, DeviceActivationReasonCodes.PlatformMismatch);
        }
        if (string.Equals(grant.StoreCode, currentStoreCode, StringComparison.OrdinalIgnoreCase))
        {
            return new GrantGateDecision(false, false, DeviceActivationReasonCodes.TargetStoreUnchanged);
        }
        return new GrantGateDecision(true, false, null);
    }

    private async Task<DeviceActivationCodeRedeemResponse> RecoverConsumedGrantAsync(
        DeviceActivationCodeGrant grant,
        string hardwareId,
        string deviceSystem,
        DeviceStoreInfo store)
    {
        if (!string.Equals(grant.ConsumedHardwareId, hardwareId, StringComparison.Ordinal)
            || !string.Equals(grant.ConsumedDeviceSystem, deviceSystem, StringComparison.Ordinal)
            || grant.ConsumedDeviceRegistrationId == null
            || string.IsNullOrWhiteSpace(grant.ConsumedDeviceCode))
        {
            return RedeemDenied(DeviceActivationReasonCodes.NotAvailable);
        }

        var registrations = await LockHardwareRegistrationsAsync(hardwareId);
        var target = registrations.FirstOrDefault(item =>
            item.Id == grant.ConsumedDeviceRegistrationId
            && item.DeviceStatus == EnabledStatus
            && string.Equals(item.DeviceCode, grant.ConsumedDeviceCode, StringComparison.Ordinal)
            && string.Equals(item.StoreCode, grant.StoreCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.DeviceSystem, deviceSystem, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(item.AuthorizationCode));
        return target == null
            || !MatchesAuthorizationCode(
                grant.ConsumedAuthorizationHash,
                target.AuthorizationCode!)
            ? RedeemDenied(DeviceActivationReasonCodes.NotAvailable)
            : Activated(
                target.DeviceCode!,
                store,
                target.AuthorizationCode!,
                DeviceActivationReasonCodes.ActivationRecovered);
    }

    private Task AcquireGrantLockAsync(Guid grantId)
    {
        return AcquireApplicationLockAsync($"HBPOS:ActivationGrant:{grantId:N}");
    }

    private Task AcquireHardwareLockAsync(string hardwareId)
    {
        // 固定锁序最后才进入硬件 applock，随后读取并锁定精确设备行。
        return AcquireApplicationLockAsync($"HBPOS:ActivationHardware:{hardwareId}");
    }

    private async Task AcquireApplicationLockAsync(string resource)
    {
        var result = await _dbContext.PosmDb.Ado.GetIntAsync(
            AcquireApplicationLockSql,
            new SugarParameter("@Resource", resource));
        if (result < 0)
        {
            throw new InvalidOperationException("Could not acquire device activation lock.");
        }
    }

    private async Task<DeviceActivationCodeGrant?> FindGrantAsync(
        Guid grantId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.PosmDb.Queryable<DeviceActivationCodeGrant>()
            .Where(item => item.GrantId == grantId)
            .FirstAsync(cancellationToken);
    }

    private async Task<DeviceActivationCodeGrant?> LockGrantAsync(Guid grantId)
    {
        return await _dbContext.PosmDb.Ado.SqlQuerySingleAsync<DeviceActivationCodeGrant>(
            LockGrantSql,
            new SugarParameter("@GrantId", grantId));
    }

    private async Task<IReadOnlyList<ActivationDeviceRecord>> LockHardwareRegistrationsAsync(
        string hardwareId) =>
        await _dbContext.PosmDb.Ado.SqlQueryAsync<ActivationDeviceRecord>(
            LockHardwareRegistrationsSql,
            new SugarParameter("@HardwareId", hardwareId));

    private Task<int> EnableExistingRegistrationAsync(
        ActivationDeviceRecord target,
        string authorizationCode,
        string terminalName,
        string deviceSystem)
    {
        return _dbContext.PosmDb.Ado.ExecuteCommandAsync(
            """
            UPDATE [dbo].[POSM_设备注册信息表]
            SET [设备状态] = @EnabledStatus,
                [设备授权码] = @AuthorizationCode,
                [设备系统] = @DeviceSystem,
                [备注] = RIGHT(CONCAT(ISNULL([备注], ''), @RemarkSuffix), 500),
                [最后修改时间] = @ModifiedAt,
                [最后修改人] = @ModifiedBy,
                [是否在线] = 0,
                [最后心跳时间] = NULL,
                [当前收银员ID] = NULL,
                [当前收银员姓名] = NULL,
                [收银员登录时间] = NULL
            WHERE [ID] = @RegistrationId
              AND [设备硬件识别码] = @HardwareId
              AND [分店代码] = @StoreCode
              AND [系统设备编号] = @DeviceCode
              AND [设备状态] = @ExpectedStatus;
            """,
            new SugarParameter("@EnabledStatus", EnabledStatus),
            new SugarParameter("@AuthorizationCode", authorizationCode),
            new SugarParameter("@DeviceSystem", deviceSystem),
            new SugarParameter("@RemarkSuffix", BuildRemark("Activated", terminalName)),
            new SugarParameter("@ModifiedAt", LocalNow()),
            new SugarParameter("@ModifiedBy", CreatedBy),
            new SugarParameter("@RegistrationId", target.Id),
            new SugarParameter("@HardwareId", target.HardwareId),
            new SugarParameter("@StoreCode", target.StoreCode),
            new SugarParameter("@DeviceCode", target.DeviceCode),
            new SugarParameter("@ExpectedStatus", target.DeviceStatus));
    }

    private Task<int> DisableSourceRegistrationAsync(
        ActivationDeviceRecord source,
        string targetStoreCode)
    {
        return _dbContext.PosmDb.Ado.ExecuteCommandAsync(
            """
            UPDATE [dbo].[POSM_设备注册信息表]
            SET [设备状态] = @DisabledStatus,
                [备注] = RIGHT(CONCAT(ISNULL([备注], ''), @RemarkSuffix), 500),
                [最后修改时间] = @ModifiedAt,
                [最后修改人] = @ModifiedBy,
                [是否在线] = 0,
                [最后心跳时间] = NULL,
                [当前收银员ID] = NULL,
                [当前收银员姓名] = NULL,
                [收银员登录时间] = NULL
            WHERE [ID] = @RegistrationId
              AND [设备硬件识别码] = @HardwareId
              AND [分店代码] = @StoreCode
              AND [系统设备编号] = @DeviceCode
              AND [设备系统] = @DeviceSystem
              AND [设备状态] = @EnabledStatus;
            """,
            new SugarParameter("@DisabledStatus", DisabledStatus),
            new SugarParameter("@RemarkSuffix", $" | Rebound to {targetStoreCode} at {UtcNow():O}"),
            new SugarParameter("@ModifiedAt", LocalNow()),
            new SugarParameter("@ModifiedBy", CreatedBy),
            new SugarParameter("@RegistrationId", source.Id),
            new SugarParameter("@HardwareId", source.HardwareId),
            new SugarParameter("@StoreCode", source.StoreCode),
            new SugarParameter("@DeviceCode", source.DeviceCode),
            new SugarParameter("@DeviceSystem", source.DeviceSystem),
            new SugarParameter("@EnabledStatus", EnabledStatus));
    }

    private async Task DisableOtherPendingRegistrationsAsync(
        IReadOnlyList<ActivationDeviceRecord> registrations,
        int? targetRegistrationId,
        string targetStoreCode)
    {
        foreach (var pending in registrations.Where(item =>
                     item.DeviceStatus == PendingStatus
                     && item.Id != targetRegistrationId))
        {
            var affected = await _dbContext.PosmDb.Ado.ExecuteCommandAsync(
                """
                UPDATE [dbo].[POSM_设备注册信息表]
                SET [设备状态] = @DisabledStatus,
                    [备注] = RIGHT(CONCAT(ISNULL([备注], ''), @RemarkSuffix), 500),
                    [最后修改时间] = @ModifiedAt,
                    [最后修改人] = @ModifiedBy
                WHERE [ID] = @RegistrationId
                  AND [设备硬件识别码] = @HardwareId
                  AND [设备状态] = @PendingStatus;
                """,
                new SugarParameter("@DisabledStatus", DisabledStatus),
                new SugarParameter("@RemarkSuffix", $" | Disabled by activation switch to {targetStoreCode} at {UtcNow():O}"),
                new SugarParameter("@ModifiedAt", LocalNow()),
                new SugarParameter("@ModifiedBy", CreatedBy),
                new SugarParameter("@RegistrationId", pending.Id),
                new SugarParameter("@HardwareId", pending.HardwareId),
                new SugarParameter("@PendingStatus", PendingStatus));
            if (affected != 1)
            {
                throw new InvalidOperationException("Pending device registration changed during activation.");
            }
        }
    }

    private Task<int> CreateEnabledRegistrationAsync(
        string hardwareId,
        string deviceCode,
        string storeCode,
        string deviceSystem,
        string authorizationCode,
        string terminalName)
    {
        return _dbContext.PosmDb.Ado.GetIntAsync(
            """
            INSERT INTO [dbo].[POSM_设备注册信息表]
                ([设备硬件识别码], [系统设备编号], [分店代码], [设备类型], [设备系统],
                 [设备状态], [设备授权码], [备注], [创建时间], [创建人])
            VALUES
                (@HardwareId, @DeviceCode, @StoreCode, N'POS', @DeviceSystem,
                 @EnabledStatus, @AuthorizationCode, @Remark, @CreatedAt, @CreatedBy);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new SugarParameter("@HardwareId", hardwareId),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceSystem", deviceSystem),
            new SugarParameter("@EnabledStatus", EnabledStatus),
            new SugarParameter("@AuthorizationCode", authorizationCode),
            new SugarParameter("@Remark", BuildRemark("Activated", terminalName).TrimStart(' ', '|')),
            new SugarParameter("@CreatedAt", LocalNow()),
            new SugarParameter("@CreatedBy", CreatedBy));
    }

    private Task<string> CreateAvailableDeviceCodeAsync(string storeCode)
    {
        // 候选号规则与匿名注册、换店共用 DeviceService.CreateAvailableDeviceCodeAsync，占用检查在当前事务内加锁。
        return DeviceService.CreateAvailableDeviceCodeAsync(
            storeCode,
            LocalNow(),
            async candidate => await _dbContext.PosmDb.Ado.GetIntAsync(
                SqlSugarDeviceRegistrationRepository.IsDeviceCodeTakenForRegistrationSql,
                new SugarParameter("@StoreCode", storeCode),
                new SugarParameter("@DeviceCode", candidate)) > 0);
    }

    private async Task<int> ConsumeGrantAsync(
        Guid grantId,
        string hardwareId,
        string deviceCode,
        int registrationId,
        string deviceSystem,
        string authorizationCode,
        string consumptionKind,
        string? previousStoreCode,
        string? previousDeviceCode)
    {
        var authorizationHash = HashAuthorizationCode(authorizationCode);
        try
        {
            return await _dbContext.PosmDb.Ado.ExecuteCommandAsync(
                """
                UPDATE [dbo].[POSM_DeviceActivationGrant]
                SET [ConsumedAtUtc] = SYSUTCDATETIME(),
                    [ConsumedHardwareId] = @HardwareId,
                    [ConsumedDeviceCode] = @DeviceCode,
                    [ConsumedDeviceRegistrationId] = @RegistrationId,
                    [ConsumedAuthorizationHash] = @AuthorizationHash,
                    [ConsumedDeviceSystem] = @DeviceSystem,
                    [ConsumptionKind] = @ConsumptionKind,
                    [PreviousStoreCode] = @PreviousStoreCode,
                    [PreviousDeviceCode] = @PreviousDeviceCode
                WHERE [GrantId] = @GrantId
                  AND [RevokedAtUtc] IS NULL
                  AND [ConsumedAtUtc] IS NULL
                  AND [ExpiresAtUtc] > SYSUTCDATETIME();
                """,
                new SugarParameter("@HardwareId", hardwareId),
                new SugarParameter("@DeviceCode", deviceCode),
                new SugarParameter("@RegistrationId", registrationId),
                new SugarParameter("@AuthorizationHash", authorizationHash),
                new SugarParameter("@DeviceSystem", deviceSystem),
                new SugarParameter("@ConsumptionKind", consumptionKind),
                new SugarParameter("@PreviousStoreCode", previousStoreCode),
                new SugarParameter("@PreviousDeviceCode", previousDeviceCode),
                new SugarParameter("@GrantId", grantId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authorizationHash);
        }
    }

    internal static byte[] HashAuthorizationCode(string authorizationCode) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(authorizationCode));

    internal static bool MatchesAuthorizationCode(
        byte[]? expectedHash,
        string authorizationCode)
    {
        if (expectedHash is not { Length: 32 })
        {
            return false;
        }

        var actualHash = HashAuthorizationCode(authorizationCode);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualHash);
        }
    }

    private async Task ExecuteTransactionAsync(Func<Task> action)
    {
        await _dbContext.PosmDb.Ado.BeginTranAsync();
        try
        {
            await action();
            await _dbContext.PosmDb.Ado.CommitTranAsync();
        }
        catch
        {
            await _dbContext.PosmDb.Ado.RollbackTranAsync();
            throw;
        }
    }

    private async Task<DeviceStoreInfo?> LoadActiveStoreAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        var store = await _dbContext.MainDb.Queryable<Store>()
            .Where(item => item.StoreCode == storeCode && item.IsActive && !item.IsDeleted)
            .FirstAsync(cancellationToken);
        return store == null ? null : new DeviceStoreInfo(store.StoreCode, store.StoreName);
    }

    private async Task<DeviceStoreInfo?> LoadActiveStoreInActivationTransactionAsync(
        string storeCode)
    {
        const string sql = """
            DECLARE @StoreSql nvarchar(max) =
                N'SELECT TOP 1 [StoreCode], [StoreName] FROM '
                + QUOTENAME(@MainDatabaseName)
                + N'.[dbo].[Store] WITH (UPDLOCK, HOLDLOCK) '
                + N'WHERE [StoreCode] = @StoreCode AND [IsActive] = 1 AND [IsDeleted] = 0;';
            EXEC sys.sp_executesql
                @StoreSql,
                N'@StoreCode nvarchar(50)',
                @StoreCode = @StoreCode;
            """;
        // 关键逻辑：使用 POSM 当前事务连接跨库重读并锁住目标分店，门店停用与 grant 消费不能穿透同一事务。
        var store = await _dbContext.PosmDb.Ado.SqlQuerySingleAsync<ActivationStoreRow>(
            sql,
            new SugarParameter("@MainDatabaseName", _mainDatabaseName),
            new SugarParameter("@StoreCode", storeCode));
        return store == null ? null : new DeviceStoreInfo(store.StoreCode, store.StoreName);
    }

    // SqlSugar 的原始 SQL 映射需要可无参构造的可变类型，不能直接映射到位置 record。
    private sealed class ActivationStoreRow
    {
        public string StoreCode { get; set; } = string.Empty;

        public string StoreName { get; set; } = string.Empty;
    }

    private static bool IsAvailableGrant(
        DeviceActivationCodeGrant? grant,
        ReadOnlySpan<byte> secret,
        DateTime utcNow) =>
        grant != null
        && DeviceActivationCodeCodec.Matches(grant.SecretHash, secret)
        && grant.RevokedAtUtc == null
        && grant.ConsumedAtUtc == null
        && grant.ExpiresAtUtc > utcNow;

    private static DeviceActivationCodePreviewResponse PreviewDenied(
        string reasonCode,
        string message) =>
        new(false, reasonCode, null, null, null, null, message);

    private static DeviceActivationCodeRedeemResponse RedeemDenied(string reasonCode) =>
        new(
            string.Empty,
            string.Empty,
            string.Empty,
            UnregisteredStatus,
            false,
            ReasonMessage(reasonCode),
            null,
            reasonCode);

    private static DeviceActivationCodeRedeemResponse Activated(
        string deviceCode,
        DeviceStoreInfo store,
        string authorizationCode,
        string reasonCode) =>
        new(
            deviceCode,
            store.StoreCode,
            SanitizeStoreNameForResponse(store.StoreName),
            EnabledStatus,
            true,
            reasonCode == DeviceActivationReasonCodes.ActivationRecovered
                ? "Device activation credentials were recovered."
                : "Device was activated.",
            authorizationCode,
            reasonCode);

    internal static string SanitizeStoreNameForResponse(string? storeName) =>
        DeviceActivationCodeCodec.RedactReservedActivationMetadata(storeName) ?? string.Empty;

    private static string ReasonMessage(string reasonCode) => reasonCode switch
    {
        DeviceActivationReasonCodes.PlatformMismatch => "Device platform does not match the activation code.",
        DeviceActivationReasonCodes.StoreUnavailable => "Target store is unavailable.",
        DeviceActivationReasonCodes.DeviceConflict => "Device hardware is already registered.",
        DeviceActivationReasonCodes.TargetStoreUnchanged => "Device already belongs to the target store.",
        DeviceActivationReasonCodes.DeviceStateConflict => "Device registration state changed. Please retry.",
        _ => "Device activation code is not available.",
    };

    private static string BuildRemark(string action, string terminalName)
    {
        var remark = string.IsNullOrWhiteSpace(terminalName)
            ? $" | {action} by one-time device activation code"
            : $" | {action} by one-time device activation code: {terminalName}";
        return remark[..Math.Min(500, remark.Length)];
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private DateTime LocalNow() => _timeProvider.GetLocalNow().DateTime;

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static string ResolveDatabaseName(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (var key in new[] { "Initial Catalog", "Database" })
        {
            if (builder.TryGetValue(key, out var value)
                && value is string name
                && !string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }
        }
        throw new InvalidOperationException("Main database name is missing from the connection string.");
    }

    private sealed class ActivationDeviceRecord
    {
        public int Id { get; set; }
        public string? DeviceCode { get; set; }
        public string? StoreCode { get; set; }
        public string? HardwareId { get; set; }
        public int DeviceStatus { get; set; }
        public string? AuthorizationCode { get; set; }
        public string? DeviceSystem { get; set; }
    }
}
