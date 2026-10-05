using System.Security.Cryptography;
using System.Text;
using Hbpos.Api.Data;
using Hbpos.Contracts.Devices;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface IDeviceService
{
    Task<DeviceRegisterResponse> RegisterAsync(DeviceRegisterRequest request, CancellationToken cancellationToken);

    Task<DeviceRegisterResponse> RegisterForAppReviewAsync(
        DeviceRegisterRequest request,
        CancellationToken cancellationToken);

    Task<DeviceVerifyResponse> VerifyAsync(DeviceVerifyRequest request, CancellationToken cancellationToken);

    Task<DeviceReregisterResponse> ReregisterAsync(
        DeviceReregisterRequest request,
        DeviceReregisterContext currentDevice,
        CancellationToken cancellationToken);

    Task<DeviceRegistrationResetResponse> ResetRegistrationAsync(
        DeviceRegistrationResetRequest request,
        DeviceRegistrationResetContext currentDevice,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<bool> UpdateRuntimeStatusAsync(
        string hardwareId,
        string deviceCode,
        string storeCode,
        bool isOnline,
        string? cashierId,
        string? cashierName,
        CancellationToken cancellationToken);
}

public interface IDeviceRegistrationRepository
{
    Task<DeviceRegistrationRecord?> FindLatestByHardwareIdAsync(
        string hardwareId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 按（设备号, 门店）确定性取一条记录：与 <paramref name="preferredHardwareId"/> 一致的行优先，
    /// 否则取 ID 最大的最新行。只决定命中哪一行，是否放行仍由调用方按平台策略校验。
    /// </summary>
    Task<DeviceRegistrationRecord?> FindByDeviceCodeAsync(
        string deviceCode,
        string storeCode,
        string? preferredHardwareId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 在注册事务内检查门店下设备号是否已被任意记录占用，并持有范围锁直到事务结束，防止并发分配同号。
    /// </summary>
    Task<bool> IsDeviceCodeTakenForRegistrationAsync(
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken);

    Task<DeviceRegistrationRecord?> FindLatestByDeviceCodeAndHardwareIdAsync(
        string deviceCode,
        string storeCode,
        string hardwareId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Exact device identity lookup requires an explicit implementation.");

    Task<DeviceRegistrationRecord?> FindActiveOrLockedRegistrationAsync(
        string hardwareId,
        CancellationToken cancellationToken);

    Task<DeviceRegistrationRecord?> FindLatestByHardwareIdAndStoreCodeAsync(
        string hardwareId,
        string storeCode,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DeviceRegistrationRecord>> FindAllByHardwareIdForRegistrationAsync(
        string hardwareId,
        CancellationToken cancellationToken);

    Task<int> CountActiveOrLockedByStoreCodeForRegistrationAsync(
        string storeCode,
        CancellationToken cancellationToken);

    Task AcquireAppReviewGrantLockAsync(
        Guid grantId,
        string storeCode,
        CancellationToken cancellationToken);

    Task<DeviceRegistrationAppReviewGrantConsumption?> FindAppReviewGrantConsumptionAsync(
        Guid grantId,
        CancellationToken cancellationToken);

    Task<int> ConsumeAppReviewGrantAsync(
        DeviceRegistrationAppReviewGrantConsumption consumption,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken);

    Task<bool> IsAppReviewDeviceAsync(
        string storeCode,
        string deviceCode,
        string hardwareId,
        CancellationToken cancellationToken);

    Task<int> DisablePendingRegistrationAsync(
        DeviceRegistrationDisableRequest request,
        CancellationToken cancellationToken);

    Task<int> DisableActiveRegistrationAsync(
        string hardwareId,
        string deviceCode,
        string storeCode,
        string remarkSuffix,
        CancellationToken cancellationToken);

    Task<int> ResetActiveRegistrationAsync(
        DeviceRegistrationResetActiveRequest request,
        CancellationToken cancellationToken) => DisableActiveRegistrationAsync(
            request.HardwareId,
            request.DeviceCode,
            request.StoreCode,
            request.RemarkSuffix,
            cancellationToken);

    Task<int> ResetRegistrationForReregisterAsync(
        DeviceRegistrationResetForReregisterRequest request,
        CancellationToken cancellationToken);

    Task<int> ApproveRegistrationForAppReviewAsync(
        DeviceRegistrationAppReviewApprovalRequest request,
        CancellationToken cancellationToken);

    Task CreateRegistrationAsync(
        DeviceRegistrationCreateRequest request,
        CancellationToken cancellationToken);

    Task<int> UpdateRuntimeStatusAsync(
        DeviceRuntimeStatusUpdateRequest request,
        CancellationToken cancellationToken);

    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken);
}

public sealed record DeviceReregisterContext(
    string DeviceCode,
    string StoreCode,
    string HardwareId,
    string DeviceSystem = DeviceSystems.Windows);

public sealed record DeviceRegistrationResetContext(
    string DeviceCode,
    string StoreCode,
    string HardwareId,
    string CashierId);

public sealed record DeviceStoreInfo(
    string StoreCode,
    string StoreName);

public sealed class DeviceRegistrationRecord
{
    public int Id { get; set; }

    public string? DeviceCode { get; set; }

    public string? StoreCode { get; set; }

    public string? HardwareId { get; set; }

    public int DeviceStatus { get; set; }

    public string? AuthorizationCode { get; set; }

    public string? DeviceSystem { get; set; }
}

public sealed class DeviceRegistrationAppReviewGrantConsumption
{
    public Guid GrantId { get; set; }

    public string StoreCode { get; set; } = string.Empty;

    public string HardwareId { get; set; } = string.Empty;

    public string DeviceCode { get; set; } = string.Empty;

    public DateTime ConsumedAtUtc { get; set; }
}

public sealed class DeviceRegistrationDisableRequest
{
    public string HardwareId { get; init; } = string.Empty;

    public string StoreCode { get; init; } = string.Empty;

    public string DeviceCode { get; init; } = string.Empty;

    public string RemarkSuffix { get; init; } = string.Empty;
}

public sealed class DeviceRegistrationCreateRequest
{
    public string HardwareId { get; init; } = string.Empty;

    public string DeviceCode { get; init; } = string.Empty;

    public string StoreCode { get; init; } = string.Empty;

    public int DeviceStatus { get; init; }

    public string AuthorizationCode { get; init; } = string.Empty;

    public string Remark { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; }

    public string CreatedBy { get; init; } = string.Empty;

    public string DeviceType { get; init; } = "POS";

    public string DeviceSystem { get; init; } = "Windows";
}

public sealed class DeviceRegistrationResetForReregisterRequest
{
    public int RegistrationId { get; init; }

    public string HardwareId { get; init; } = string.Empty;

    public string StoreCode { get; init; } = string.Empty;

    public string DeviceCode { get; init; } = string.Empty;

    public int ExpectedDeviceStatus { get; init; }

    public string? ExpectedAuthorizationCode { get; init; }

    public string AuthorizationCode { get; init; } = string.Empty;

    public string RemarkSuffix { get; init; } = string.Empty;

    public string DeviceSystem { get; init; } = DeviceSystems.Windows;

    public DateTime ModifiedAt { get; init; }

    public string ModifiedBy { get; init; } = string.Empty;
}

public sealed class DeviceRegistrationResetActiveRequest
{
    public string HardwareId { get; init; } = string.Empty;

    public string DeviceCode { get; init; } = string.Empty;

    public string StoreCode { get; init; } = string.Empty;

    public string RemarkSuffix { get; init; } = string.Empty;

    public DateTime ModifiedAtUtc { get; init; }

    public string ModifiedBy { get; init; } = string.Empty;
}

public sealed class DeviceRegistrationAppReviewApprovalRequest
{
    public int RegistrationId { get; init; }

    public string HardwareId { get; init; } = string.Empty;

    public string StoreCode { get; init; } = string.Empty;

    public string DeviceCode { get; init; } = string.Empty;

    public int ExpectedDeviceStatus { get; init; }

    public string? ExpectedAuthorizationCode { get; init; }

    public string AuthorizationCode { get; init; } = string.Empty;

    public string RemarkSuffix { get; init; } = string.Empty;

    public string DeviceSystem { get; init; } = DeviceSystems.IpadOs;

    public DateTime ModifiedAt { get; init; }
}

public sealed record DeviceRuntimeStatusUpdateRequest(
    string HardwareId,
    string DeviceCode,
    string StoreCode,
    bool IsOnline,
    string? CashierId,
    string? CashierName,
    DateTime ReportedAt);

public sealed class DeviceService : IDeviceService
{
    private const int MinimumProvisioningCodeLength = 16;
    private const int PendingStatus = -1;
    private const int DisabledStatus = 0;
    private const int EnabledStatus = 1;
    private const int LockedStatus = 2;
    private const int UnregisteredStatus = 3;

    private readonly HbposSqlSugarContext? dbContext;
    private readonly IDeviceRegistrationRepository deviceRegistrationRepository;
    private readonly Func<string, CancellationToken, Task<DeviceStoreInfo?>> loadStoreAsync;
    private readonly Func<DateTime> nowProvider;
    private readonly Func<DateTimeOffset> utcNowProvider;
    private readonly PosIpadAppReviewOptions appReviewOptions;
    private readonly ILogger<DeviceService> logger;

    public DeviceService(
        HbposSqlSugarContext dbContext,
        IDeviceRegistrationRepository deviceRegistrationRepository,
        IOptions<PosIpadAppReviewOptions> appReviewOptions,
        ILogger<DeviceService> logger)
    {
        this.dbContext = dbContext;
        this.deviceRegistrationRepository = deviceRegistrationRepository;
        loadStoreAsync = LoadStoreAsync;
        nowProvider = () => DateTime.Now;
        utcNowProvider = () => DateTimeOffset.UtcNow;
        this.appReviewOptions = appReviewOptions.Value;
        this.logger = logger;
    }

    public DeviceService(
        IDeviceRegistrationRepository deviceRegistrationRepository,
        Func<string, CancellationToken, Task<DeviceStoreInfo?>> loadStoreAsync,
        Func<DateTime>? nowProvider = null,
        PosIpadAppReviewOptions? appReviewOptions = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        ILogger<DeviceService>? logger = null)
    {
        this.deviceRegistrationRepository = deviceRegistrationRepository;
        this.loadStoreAsync = loadStoreAsync;
        this.nowProvider = nowProvider ?? (() => DateTime.Now);
        this.appReviewOptions = appReviewOptions ?? new PosIpadAppReviewOptions();
        this.utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DeviceService>.Instance;
    }

    public Task<DeviceRegisterResponse> RegisterAsync(
        DeviceRegisterRequest request,
        CancellationToken cancellationToken)
    {
        return RegisterCoreAsync(request, appReviewOnly: false, cancellationToken);
    }

    public Task<DeviceRegisterResponse> RegisterForAppReviewAsync(
        DeviceRegisterRequest request,
        CancellationToken cancellationToken)
    {
        return RegisterCoreAsync(request, appReviewOnly: true, cancellationToken);
    }

    private async Task<DeviceRegisterResponse> RegisterCoreAsync(
        DeviceRegisterRequest request,
        bool appReviewOnly,
        CancellationToken cancellationToken)
    {
        var storeCode = Normalize(request.StoreCode);
        var hardwareId = Normalize(request.HardwareId);
        var terminalName = Normalize(request.TerminalName);
        if (!DeviceSystems.TryNormalize(request.DeviceSystem, out var deviceSystem))
        {
            return CreateRegisterResponse(string.Empty, storeCode, string.Empty, UnregisteredStatus, "deviceSystem is invalid");
        }

        if (string.IsNullOrEmpty(storeCode))
        {
            return CreateRegisterResponse(string.Empty, storeCode, string.Empty, UnregisteredStatus, "storeCode is required");
        }

        if (string.IsNullOrEmpty(hardwareId))
        {
            return CreateRegisterResponse(string.Empty, storeCode, string.Empty, UnregisteredStatus, "hardwareId is required");
        }

        var store = await loadStoreAsync(storeCode, cancellationToken);
        if (store is null)
        {
            return CreateRegisterResponse(string.Empty, storeCode, string.Empty, UnregisteredStatus, "Store was not found or inactive.");
        }

        var isConfiguredAppReviewTarget = IsConfiguredAppReviewTarget(storeCode, deviceSystem);
        var appReviewGrant = TryMatchAppReviewGrant(request, storeCode, deviceSystem);
        var appReviewWindowOpen = IsAppReviewWindowOpen();
        if (appReviewOnly && appReviewGrant is null)
        {
            // 关键逻辑：审核专用端点只接受精确门店、iPadOS 和匹配的开通码，其他组合零写入失败关闭。
            return CreateRegisterResponse(
                string.Empty,
                storeCode,
                store.StoreName,
                UnregisteredStatus,
                "App Review device activation code is invalid or expired.");
        }

        if (!appReviewOnly && appReviewWindowOpen && isConfiguredAppReviewTarget)
        {
            return CreateRegisterResponse(
                string.Empty,
                storeCode,
                store.StoreName,
                UnregisteredStatus,
                "App Review device registration requires the dedicated activation endpoint.");
        }

        if (!appReviewOnly)
        {
            appReviewGrant = null;
        }

        var now = nowProvider();
        DeviceRegisterResponse? response = null;

        // 关键逻辑：匿名注册的全量检查、旧待确认禁用与目标重置/新建必须在同一锁定事务中完成。
        await deviceRegistrationRepository.ExecuteInTransactionAsync(
            async token =>
            {
                IReadOnlyList<DeviceRegistrationRecord> registrations;

                if (appReviewGrant is not null)
                {
                    // 关键逻辑：审核注册统一按 store → grant → hardware 顺序加锁，避免跨硬件计数形成死锁环。
                    await deviceRegistrationRepository.AcquireAppReviewGrantLockAsync(
                        appReviewGrant.GrantId,
                        storeCode,
                        token);
                    registrations = await deviceRegistrationRepository
                        .FindAllByHardwareIdForRegistrationAsync(hardwareId, token);
                    var consumedGrant = await deviceRegistrationRepository
                        .FindAppReviewGrantConsumptionAsync(appReviewGrant.GrantId, token);
                    if (consumedGrant is not null)
                    {
                        var recoverableRegistration = registrations.FirstOrDefault(registration =>
                            registration.DeviceStatus == EnabledStatus
                            && string.Equals(registration.StoreCode, consumedGrant.StoreCode, StringComparison.Ordinal)
                            && string.Equals(registration.HardwareId, consumedGrant.HardwareId, StringComparison.Ordinal)
                            && string.Equals(registration.DeviceCode, consumedGrant.DeviceCode, StringComparison.Ordinal)
                            && string.Equals(consumedGrant.StoreCode, storeCode, StringComparison.Ordinal)
                            && string.Equals(consumedGrant.HardwareId, hardwareId, StringComparison.Ordinal)
                            && !string.IsNullOrWhiteSpace(registration.AuthorizationCode));
                        if (recoverableRegistration is not null)
                        {
                            // 关键逻辑：首次成功响应丢失时，相同 grant/store/hardware 只恢复原设备凭据，不产生第二次消费。
                            response = new DeviceRegisterResponse(
                                recoverableRegistration.DeviceCode!,
                                storeCode,
                                store.StoreName,
                                EnabledStatus,
                                true,
                                GetStatusMessage(EnabledStatus),
                                recoverableRegistration.AuthorizationCode);
                            return;
                        }

                        response = CreateRegisterResponse(
                            string.Empty,
                            storeCode,
                            store.StoreName,
                            UnregisteredStatus,
                            "App Review device activation code has already been used.");
                        return;
                    }

                    // 关键逻辑：取得 grant 锁后重新读取时钟，避免锁等待跨过到期时间仍完成首次消费。
                    if (!IsAppReviewWindowOpen())
                    {
                        response = CreateRegisterResponse(
                            string.Empty,
                            storeCode,
                            store.StoreName,
                            UnregisteredStatus,
                            "App Review device activation is unavailable.");
                        return;
                    }
                }
                else
                {
                    registrations = await deviceRegistrationRepository
                        .FindAllByHardwareIdForRegistrationAsync(hardwareId, token);
                }

                // 关键逻辑：同一硬件任意启用或锁定记录都会阻止匿名注册，且不得产生任何写入。
                var blockingRegistration = registrations.FirstOrDefault(static registration =>
                    registration.DeviceStatus is EnabledStatus or LockedStatus);
                if (blockingRegistration is not null)
                {
                    response = CreateRegisterResponse(
                        blockingRegistration.DeviceCode ?? string.Empty,
                        blockingRegistration.StoreCode ?? storeCode,
                        string.Equals(blockingRegistration.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase)
                            ? store.StoreName
                            : string.Empty,
                        blockingRegistration.DeviceStatus,
                        "Device hardware is already registered and cannot be registered anonymously.");
                    return;
                }

                var targetRegistration = registrations.FirstOrDefault(registration =>
                    string.Equals(registration.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase));

                var targetDeviceSystem = deviceSystem;
                if (targetRegistration is not null)
                {
                    if (!DeviceSystems.TryNormalize(targetRegistration.DeviceSystem, out targetDeviceSystem))
                    {
                        response = CreateRegisterResponse(
                            targetRegistration.DeviceCode ?? string.Empty,
                            storeCode,
                            store.StoreName,
                            targetRegistration.DeviceStatus,
                            "Registered device system is invalid.");
                        return;
                    }

                    // 关键逻辑：匿名客户端只能重用同平台记录，不能把 pending/disabled/unregistered 跨平台覆盖。
                    if (!string.Equals(targetDeviceSystem, deviceSystem, StringComparison.Ordinal))
                    {
                        response = CreateRegisterResponse(
                            targetRegistration.DeviceCode ?? string.Empty,
                            storeCode,
                            store.StoreName,
                            targetRegistration.DeviceStatus,
                            "Device system does not match existing registration.");
                        return;
                    }
                }

                // 关键逻辑：所有目标状态与设备号校验必须先于任何写入，拒绝请求不得顺带清理其他待确认记录。
                if (targetRegistration is not null
                    && targetRegistration.DeviceStatus is not PendingStatus and not DisabledStatus and not UnregisteredStatus)
                {
                    response = CreateRegisterResponse(
                        targetRegistration.DeviceCode ?? string.Empty,
                        storeCode,
                        store.StoreName,
                        targetRegistration.DeviceStatus,
                        "Device registration cannot be reused anonymously in its current status.");
                    return;
                }

                if (targetRegistration is not null && string.IsNullOrWhiteSpace(targetRegistration.DeviceCode))
                {
                    response = CreateRegisterResponse(
                        string.Empty,
                        storeCode,
                        store.StoreName,
                        targetRegistration.DeviceStatus == PendingStatus
                            ? DisabledStatus
                            : targetRegistration.DeviceStatus,
                        "Existing device registration has no reusable device code.");
                    return;
                }

                var autoApproveForAppReview = false;
                if (appReviewGrant is not null
                    && (targetRegistration is null || targetRegistration.DeviceStatus == PendingStatus))
                {
                    // 关键逻辑：设备上限计数与注册写入共享事务和范围锁，避免并发请求同时越过单设备上限。
                    var activeOrLockedCount = await deviceRegistrationRepository
                        .CountActiveOrLockedByStoreCodeForRegistrationAsync(storeCode, token);
                    autoApproveForAppReview = activeOrLockedCount < appReviewOptions.MaxActiveDevices;
                }

                if (appReviewGrant is not null && !autoApproveForAppReview)
                {
                    response = CreateRegisterResponse(
                        targetRegistration?.DeviceCode ?? string.Empty,
                        storeCode,
                        store.StoreName,
                        UnregisteredStatus,
                        "App Review device activation is unavailable.");
                    return;
                }

                // 关键逻辑：只保留目标店最新的待确认记录，其余同硬件待确认记录必须逐条条件禁用。
                foreach (var pendingRegistration in registrations.Where(registration =>
                             registration.DeviceStatus == PendingStatus
                             && (targetRegistration?.DeviceStatus != PendingStatus
                                 || registration.Id != targetRegistration.Id)))
                {
                    var disabledCount = await deviceRegistrationRepository.DisablePendingRegistrationAsync(
                        new DeviceRegistrationDisableRequest
                        {
                            HardwareId = hardwareId,
                            StoreCode = pendingRegistration.StoreCode ?? string.Empty,
                            DeviceCode = pendingRegistration.DeviceCode ?? string.Empty,
                            RemarkSuffix = $" | Disabled by registration switch to {storeCode} at {now:O}"
                        },
                        token);
                    if (disabledCount != 1)
                    {
                        throw new InvalidOperationException("Pending device registration changed during registration.");
                    }
                }

                if (autoApproveForAppReview && targetRegistration is not null)
                {
                    var authorizationCode = Guid.NewGuid().ToString("N");
                    var approvedCount = await deviceRegistrationRepository.ApproveRegistrationForAppReviewAsync(
                        new DeviceRegistrationAppReviewApprovalRequest
                        {
                            RegistrationId = targetRegistration.Id,
                            HardwareId = targetRegistration.HardwareId ?? hardwareId,
                            StoreCode = targetRegistration.StoreCode ?? storeCode,
                            DeviceCode = targetRegistration.DeviceCode!,
                            ExpectedDeviceStatus = targetRegistration.DeviceStatus,
                            ExpectedAuthorizationCode = targetRegistration.AuthorizationCode,
                            AuthorizationCode = authorizationCode,
                            RemarkSuffix = $" | {appReviewGrant!.Marker}; auto-approved at {utcNowProvider():O}",
                            DeviceSystem = targetDeviceSystem,
                            ModifiedAt = now
                        },
                        token);
                    if (approvedCount != 1)
                    {
                        throw new InvalidOperationException("Target device registration changed during App Review approval.");
                    }

                    await ConsumeAppReviewGrantAsync(
                        appReviewGrant!,
                        storeCode,
                        hardwareId,
                        targetRegistration.DeviceCode,
                        token);

                    response = new DeviceRegisterResponse(
                        targetRegistration.DeviceCode,
                        storeCode,
                        store.StoreName,
                        EnabledStatus,
                        true,
                        GetStatusMessage(EnabledStatus),
                        authorizationCode);
                    logger.LogInformation(
                        "App Review device auto-approved for store {StoreCode}, device {DeviceCode}.",
                        storeCode,
                        targetRegistration.DeviceCode);
                    return;
                }

                if (targetRegistration?.DeviceStatus == PendingStatus)
                {
                    response = new DeviceRegisterResponse(
                        targetRegistration.DeviceCode ?? string.Empty,
                        storeCode,
                        store.StoreName,
                        PendingStatus,
                        false,
                        GetStatusMessage(PendingStatus),
                        null);
                    return;
                }

                if (targetRegistration is not null)
                {
                    var authorizationCode = Guid.NewGuid().ToString("N");
                    var resetCount = await deviceRegistrationRepository.ResetRegistrationForReregisterAsync(
                        new DeviceRegistrationResetForReregisterRequest
                        {
                            RegistrationId = targetRegistration.Id,
                            HardwareId = targetRegistration.HardwareId ?? hardwareId,
                            StoreCode = targetRegistration.StoreCode ?? storeCode,
                            DeviceCode = targetRegistration.DeviceCode!,
                            ExpectedDeviceStatus = targetRegistration.DeviceStatus,
                            ExpectedAuthorizationCode = targetRegistration.AuthorizationCode,
                            AuthorizationCode = authorizationCode,
                            RemarkSuffix = $" | Reset by anonymous registration at {now:O}",
                            DeviceSystem = targetDeviceSystem,
                            ModifiedAt = now,
                            ModifiedBy = "HBPOS_CLIENT"
                        },
                        token);
                    if (resetCount != 1)
                    {
                        throw new InvalidOperationException("Target device registration changed during registration.");
                    }

                    response = new DeviceRegisterResponse(
                        targetRegistration.DeviceCode,
                        storeCode,
                        store.StoreName,
                        PendingStatus,
                        false,
                        GetStatusMessage(PendingStatus),
                        null);
                    return;
                }

                var newRegistration = autoApproveForAppReview
                    ? CreateAppReviewApprovedRegistration(
                        hardwareId,
                        storeCode,
                        terminalName,
                        now,
                        deviceSystem,
                        appReviewGrant!.Marker)
                    : CreatePendingRegistration(
                        hardwareId,
                        storeCode,
                        await AllocateDeviceCodeAsync(storeCode, now, token),
                        terminalName,
                        now,
                        deviceSystem: deviceSystem);
                await deviceRegistrationRepository.CreateRegistrationAsync(newRegistration, token);
                if (autoApproveForAppReview)
                {
                    await ConsumeAppReviewGrantAsync(
                        appReviewGrant!,
                        storeCode,
                        hardwareId,
                        newRegistration.DeviceCode,
                        token);
                }
                response = new DeviceRegisterResponse(
                    newRegistration.DeviceCode,
                    storeCode,
                    store.StoreName,
                    newRegistration.DeviceStatus,
                    autoApproveForAppReview,
                    GetStatusMessage(newRegistration.DeviceStatus),
                    autoApproveForAppReview ? newRegistration.AuthorizationCode : null);
                if (autoApproveForAppReview)
                {
                    logger.LogInformation(
                        "App Review device auto-approved for store {StoreCode}, device {DeviceCode}.",
                        storeCode,
                        newRegistration.DeviceCode);
                }
            },
            cancellationToken);

        return response ?? throw new InvalidOperationException("Device registration did not produce a response.");
    }

    public async Task<DeviceVerifyResponse> VerifyAsync(
        DeviceVerifyRequest request,
        CancellationToken cancellationToken)
    {
        var deviceCode = Normalize(request.DeviceCode);
        var storeCode = Normalize(request.StoreCode);
        var hardwareId = Normalize(request.HardwareId);
        if (!DeviceSystems.TryNormalize(request.DeviceSystem, out var submittedDeviceSystem))
        {
            return CreateVerifyResponse(deviceCode, storeCode, string.Empty, UnregisteredStatus, "deviceSystem is invalid");
        }

        var store = await loadStoreAsync(storeCode, cancellationToken);
        if (store is null)
        {
            return CreateVerifyResponse(deviceCode, storeCode, string.Empty, UnregisteredStatus, "Store was not found or inactive.");
        }

        var requiresExactHardwareId = DeviceSystems.RequiresExactHardwareId(submittedDeviceSystem);
        if (requiresExactHardwareId && string.IsNullOrWhiteSpace(hardwareId))
        {
            return CreateVerifyResponse(
                deviceCode,
                storeCode,
                store.StoreName,
                UnregisteredStatus,
                $"Device hardware id is required for {submittedDeviceSystem}.");
        }

        // iPadOS 等跨端设备必须按门店、设备码、硬件码取最新记录，避免复用设备码命中其他硬件。
        // Windows 兼容旧客户端可不带硬件码；带了硬件码时优先命中同硬件的行，避免历史撞号随机命中他人记录被持久化为拒绝。
        var device = requiresExactHardwareId
            ? await deviceRegistrationRepository.FindLatestByDeviceCodeAndHardwareIdAsync(
                deviceCode, storeCode, hardwareId, cancellationToken)
            : await deviceRegistrationRepository.FindByDeviceCodeAsync(
                deviceCode, storeCode, hardwareId, cancellationToken);
        if (device is null)
        {
            return CreateVerifyResponse(deviceCode, storeCode, store.StoreName, UnregisteredStatus, "Device is not registered.");
        }

        if (!DeviceSystems.TryNormalize(device.DeviceSystem, out var registeredDeviceSystem))
        {
            return CreateVerifyResponse(deviceCode, storeCode, store.StoreName, device.DeviceStatus, "Registered device system is invalid.");
        }

        if (!string.Equals(submittedDeviceSystem, registeredDeviceSystem, StringComparison.Ordinal))
        {
            return CreateVerifyResponse(deviceCode, storeCode, store.StoreName, device.DeviceStatus, "Device system does not match existing registration.");
        }

        if (!DeviceAuthorizationPlatformPolicy.IsHardwareIdAccepted(
                registeredDeviceSystem,
                device.HardwareId,
                hardwareId))
        {
            return CreateVerifyResponse(deviceCode, storeCode, store.StoreName, device.DeviceStatus, "Device hardware id does not match.");
        }

        return new DeviceVerifyResponse(
            deviceCode,
            storeCode,
            store.StoreName,
            device.DeviceStatus,
            device.DeviceStatus == EnabledStatus,
            GetStatusMessage(device.DeviceStatus),
            device.DeviceStatus == EnabledStatus ? device.AuthorizationCode : null,
            // 关键逻辑：走到这里的非 Windows 平台（iPadOS/iOS/Android）已按门店+设备码+硬件码精确命中，
            // 且平台一致、硬件码通过校验，才算精确身份证明；客户端「清除设备注册」中断后的
            // 启动恢复依赖它确认终态。Windows 只按设备码查询，永远不给该证明。
            ExactIdentityMatched: requiresExactHardwareId);
    }

    public async Task<DeviceReregisterResponse> ReregisterAsync(
        DeviceReregisterRequest request,
        DeviceReregisterContext currentDevice,
        CancellationToken cancellationToken)
    {
        var targetStoreCode = Normalize(request.TargetStoreCode);
        var hardwareId = Normalize(request.HardwareId);
        var currentDeviceCode = Normalize(currentDevice.DeviceCode);
        var currentStoreCode = Normalize(currentDevice.StoreCode);
        var currentHardwareId = Normalize(currentDevice.HardwareId);
        var terminalName = Normalize(request.TerminalName);

        if (!DeviceSystems.TryNormalize(currentDevice.DeviceSystem, out var currentDeviceSystem))
        {
            return CreateReregisterResponse(
                currentDeviceCode,
                currentStoreCode,
                string.Empty,
                DisabledStatus,
                "Current device system is invalid.");
        }

        if (string.IsNullOrEmpty(targetStoreCode))
        {
            return CreateReregisterResponse(string.Empty, targetStoreCode, string.Empty, UnregisteredStatus, "targetStoreCode is required");
        }

        if (string.IsNullOrEmpty(hardwareId))
        {
            return CreateReregisterResponse(string.Empty, targetStoreCode, string.Empty, UnregisteredStatus, "hardwareId is required");
        }

        if (!string.Equals(hardwareId, currentHardwareId, StringComparison.OrdinalIgnoreCase))
        {
            return CreateReregisterResponse(currentDeviceCode, currentStoreCode, string.Empty, DisabledStatus, "Device hardware id does not match.");
        }

        if (string.Equals(targetStoreCode, currentStoreCode, StringComparison.OrdinalIgnoreCase))
        {
            return CreateReregisterResponse(currentDeviceCode, currentStoreCode, string.Empty, DisabledStatus, "Please select a different store for device reregistration.");
        }

        var store = await loadStoreAsync(targetStoreCode, cancellationToken);
        if (store is null)
        {
            return CreateReregisterResponse(string.Empty, targetStoreCode, string.Empty, UnregisteredStatus, "Store was not found or inactive.");
        }

        var now = nowProvider();
        var authorizationCode = Guid.NewGuid().ToString("N");
        var deviceCode = string.Empty;
        var disableRemark = $" | Disabled by reregistration to {targetStoreCode} at {now:O}";
        var resetRemark = string.IsNullOrWhiteSpace(terminalName)
            ? $" | Reset by reregistration from {currentStoreCode}/{currentDeviceCode} at {now:O}"
            : $" | Reset by reregistration from {currentStoreCode}/{currentDeviceCode}: {terminalName} at {now:O}";

        // 关键逻辑：目标记录查询、当前设备禁用及目标记录重置/创建必须处于同一事务，任一步并发失配都整体回滚。
        await deviceRegistrationRepository.ExecuteInTransactionAsync(
            async token =>
            {
                var targetRegistration = await deviceRegistrationRepository
                    .FindLatestByHardwareIdAndStoreCodeAsync(hardwareId, targetStoreCode, token);

                var disabledCount = await deviceRegistrationRepository.DisableActiveRegistrationAsync(
                    hardwareId,
                    currentDeviceCode,
                    currentStoreCode,
                    disableRemark,
                    token);
                if (disabledCount != 1)
                {
                    throw new InvalidOperationException("Current device registration changed during reregistration.");
                }

                if (targetRegistration is not null && !string.IsNullOrWhiteSpace(targetRegistration.DeviceCode))
                {
                    // 关键逻辑：目标分店已有记录时保留原设备号，只刷新授权并重置为待确认状态。
                    var resetCount = await deviceRegistrationRepository.ResetRegistrationForReregisterAsync(
                        new DeviceRegistrationResetForReregisterRequest
                        {
                            RegistrationId = targetRegistration.Id,
                            HardwareId = targetRegistration.HardwareId ?? hardwareId,
                            StoreCode = targetRegistration.StoreCode ?? targetStoreCode,
                            DeviceCode = targetRegistration.DeviceCode,
                            ExpectedDeviceStatus = targetRegistration.DeviceStatus,
                            ExpectedAuthorizationCode = targetRegistration.AuthorizationCode,
                            AuthorizationCode = authorizationCode,
                            RemarkSuffix = resetRemark,
                            // 关键逻辑：平台只继承已认证设备的服务端记录，重新注册请求不可覆盖它。
                            DeviceSystem = currentDeviceSystem,
                            ModifiedAt = now,
                            ModifiedBy = "HBPOS_CLIENT"
                        },
                        token);
                    if (resetCount != 1)
                    {
                        throw new InvalidOperationException("Target device registration changed during reregistration.");
                    }

                    deviceCode = targetRegistration.DeviceCode;
                    return;
                }

                // 关键逻辑：只有目标分店没有可复用设备号时，才生成新的待确认设备记录和设备号。
                var pendingRegistration = CreatePendingRegistration(
                    hardwareId,
                    targetStoreCode,
                    await AllocateDeviceCodeAsync(targetStoreCode, now, token),
                    terminalName,
                    now,
                    authorizationCode,
                    currentDeviceSystem);
                await deviceRegistrationRepository.CreateRegistrationAsync(pendingRegistration, token);
                deviceCode = pendingRegistration.DeviceCode;
            },
            cancellationToken);

        return new DeviceReregisterResponse(
            deviceCode,
            targetStoreCode,
            store.StoreName,
            PendingStatus,
            false,
            GetStatusMessage(PendingStatus),
            null);
    }

    public async Task<DeviceRegistrationResetResponse> ResetRegistrationAsync(
        DeviceRegistrationResetRequest request,
        DeviceRegistrationResetContext currentDevice,
        CancellationToken cancellationToken)
    {
        if (request.OperationId == Guid.Empty)
        {
            throw new ArgumentException("operationId is required.", nameof(request));
        }

        var deviceCode = Normalize(currentDevice.DeviceCode);
        var storeCode = Normalize(currentDevice.StoreCode);
        var hardwareId = Normalize(currentDevice.HardwareId);
        var cashierId = Normalize(currentDevice.CashierId);
        if (deviceCode.Length == 0 || storeCode.Length == 0 || hardwareId.Length == 0 || cashierId.Length == 0)
        {
            throw new ArgumentException("Authenticated device and cashier identity are required.", nameof(currentDevice));
        }

        var disabledAtUtc = utcNowProvider().UtcDateTime;
        var modifiedAt = nowProvider();
        var remark = $" | Device registration reset operation {request.OperationId:D} at {disabledAtUtc:O}";
        await deviceRegistrationRepository.ExecuteInTransactionAsync(
            async token =>
            {
                var affected = await deviceRegistrationRepository.ResetActiveRegistrationAsync(
                    new DeviceRegistrationResetActiveRequest
                    {
                        HardwareId = hardwareId,
                        DeviceCode = deviceCode,
                        StoreCode = storeCode,
                        RemarkSuffix = remark,
                        ModifiedAtUtc = modifiedAt,
                        ModifiedBy = cashierId
                    },
                    token);
                if (affected != 1)
                {
                    throw new InvalidOperationException("Current device registration changed during reset.");
                }
            },
            cancellationToken);

        return new DeviceRegistrationResetResponse(
            request.OperationId,
            deviceCode,
            storeCode,
            disabledAtUtc);
    }

    public async Task<bool> UpdateRuntimeStatusAsync(
        string hardwareId,
        string deviceCode,
        string storeCode,
        bool isOnline,
        string? cashierId,
        string? cashierName,
        CancellationToken cancellationToken)
    {
        var normalizedHardwareId = Normalize(hardwareId);
        var normalizedDeviceCode = Normalize(deviceCode);
        var normalizedStoreCode = Normalize(storeCode);
        if (string.IsNullOrEmpty(normalizedHardwareId)
            || string.IsNullOrEmpty(normalizedDeviceCode)
            || string.IsNullOrEmpty(normalizedStoreCode))
        {
            return false;
        }

        var rows = await deviceRegistrationRepository.UpdateRuntimeStatusAsync(
            new DeviceRuntimeStatusUpdateRequest(
                normalizedHardwareId,
                normalizedDeviceCode,
                normalizedStoreCode,
                isOnline,
                NormalizeOptional(cashierId),
                NormalizeOptional(cashierName),
                nowProvider()),
            cancellationToken);
        return rows > 0;
    }

    internal static string CreateDeviceCode(string storeCode, DateTime localTime)
    {
        return $"POS_{storeCode}_{localTime:HHmm}";
    }

    /// <summary>
    /// 生成门店内未被占用的设备号：先用 POS_{门店}_{HHmm}，被占用时依次追加 _2.._99。
    /// 匿名注册、换店与开通码路径共用此规则；占用判断由调用方在事务内加锁完成。
    /// </summary>
    internal static async Task<string> CreateAvailableDeviceCodeAsync(
        string storeCode,
        DateTime localTime,
        Func<string, Task<bool>> isTakenAsync)
    {
        // 设备号列为 varchar(50)，截断基础号时保留后缀，保证候选号互不相同。
        const int maxDeviceCodeLength = 50;
        var baseCode = CreateDeviceCode(storeCode, localTime);
        baseCode = baseCode[..Math.Min(maxDeviceCodeLength, baseCode.Length)];
        for (var sequence = 1; sequence <= 99; sequence++)
        {
            var suffix = sequence == 1 ? string.Empty : $"_{sequence}";
            var candidateBase = baseCode[..Math.Min(maxDeviceCodeLength - suffix.Length, baseCode.Length)];
            var candidate = candidateBase + suffix;
            if (!await isTakenAsync(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not allocate a target store device code.");
    }

    private Task<string> AllocateDeviceCodeAsync(
        string storeCode,
        DateTime localTime,
        CancellationToken cancellationToken)
    {
        // 关键逻辑：HHmm 每店只有 1440 种取值，跨日期、跨平台都会撞号；必须在注册事务内带锁去重后再写入。
        return CreateAvailableDeviceCodeAsync(
            storeCode,
            localTime,
            candidate => deviceRegistrationRepository.IsDeviceCodeTakenForRegistrationAsync(
                storeCode,
                candidate,
                cancellationToken));
    }

    private async Task<DeviceStoreInfo?> LoadStoreAsync(string storeCode, CancellationToken cancellationToken)
    {
        var context = dbContext ?? throw new InvalidOperationException("Db context is required for store lookup.");

        var store = await context.MainDb.Queryable<BlazorApp.Shared.Models.Store>()
            .FirstAsync(x => x.StoreCode == storeCode && x.IsActive && !x.IsDeleted, cancellationToken);

        return store is null
            ? null
            : new DeviceStoreInfo(store.StoreCode, store.StoreName);
    }

    private static DeviceRegistrationCreateRequest CreatePendingRegistration(
        string hardwareId,
        string storeCode,
        string deviceCode,
        string terminalName,
        DateTime createdAt,
        string? authorizationCode = null,
        string deviceSystem = DeviceSystems.Windows)
    {
        return new DeviceRegistrationCreateRequest
        {
            HardwareId = hardwareId,
            DeviceCode = deviceCode,
            StoreCode = storeCode,
            DeviceStatus = PendingStatus,
            AuthorizationCode = authorizationCode ?? Guid.NewGuid().ToString("N"),
            Remark = string.IsNullOrWhiteSpace(terminalName)
                ? "HBPOS client registration"
                : $"HBPOS client registration: {terminalName}",
            CreatedAt = createdAt,
            CreatedBy = "HBPOS_CLIENT",
            DeviceSystem = deviceSystem
        };
    }

    private static DeviceRegistrationCreateRequest CreateAppReviewApprovedRegistration(
        string hardwareId,
        string storeCode,
        string terminalName,
        DateTime createdAt,
        string deviceSystem,
        string grantMarker)
    {
        // 审核设备始终保留完整随机后缀，长门店代码只能截断门店片段，不能牺牲唯一性。
        const int maxDeviceCodeLength = 50;
        const string prefix = "POS_";
        var randomSuffix = $"_REVIEW_{Guid.NewGuid():N}";
        var maxStoreCodeLength = maxDeviceCodeLength - prefix.Length - randomSuffix.Length;
        var storeCodeSegment = storeCode[..Math.Min(maxStoreCodeLength, storeCode.Length)];
        var reviewDeviceCode = $"{prefix}{storeCodeSegment}{randomSuffix}";
        return new DeviceRegistrationCreateRequest
        {
            HardwareId = hardwareId,
            DeviceCode = reviewDeviceCode,
            StoreCode = storeCode,
            DeviceStatus = EnabledStatus,
            AuthorizationCode = Guid.NewGuid().ToString("N"),
            Remark = string.IsNullOrWhiteSpace(terminalName)
                ? $"HBPOS {grantMarker}; auto-approved registration"
                : $"HBPOS {grantMarker}; auto-approved registration: {terminalName}",
            CreatedAt = createdAt,
            CreatedBy = "HBPOS_APP_REVIEW",
            DeviceSystem = deviceSystem
        };
    }

    private bool IsConfiguredAppReviewTarget(string storeCode, string deviceSystem)
    {
        return string.Equals(storeCode, appReviewOptions.StoreCode?.Trim(), StringComparison.Ordinal)
            && string.Equals(deviceSystem, DeviceSystems.IpadOs, StringComparison.Ordinal);
    }

    private bool IsAppReviewWindowOpen()
    {
        return appReviewOptions.Enabled
            && appReviewOptions.ExpiresAtUtc is not null
            && utcNowProvider() < appReviewOptions.ExpiresAtUtc.Value
            && appReviewOptions.MaxActiveDevices == 1;
    }

    private AppReviewGrantContext? TryMatchAppReviewGrant(
        DeviceRegisterRequest request,
        string storeCode,
        string deviceSystem)
    {
        if (!IsConfiguredAppReviewTarget(storeCode, deviceSystem)
            || !Guid.TryParse(appReviewOptions.GrantId, out var grantId))
        {
            return null;
        }

        var submittedCode = request.ProvisioningCode?.Trim();
        var configuredHash = appReviewOptions.RegistrationCodeSha256?.Trim();
        if (string.IsNullOrEmpty(submittedCode)
            || submittedCode.Length < MinimumProvisioningCodeLength
            || submittedCode.Length > 128
            || string.IsNullOrEmpty(configuredHash))
        {
            return null;
        }

        try
        {
            var expectedHash = Convert.FromHexString(configuredHash);
            var submittedHash = SHA256.HashData(Encoding.UTF8.GetBytes(submittedCode));
            return expectedHash.Length == submittedHash.Length
                && CryptographicOperations.FixedTimeEquals(expectedHash, submittedHash)
                    ? new AppReviewGrantContext(grantId, $"AppReviewGrant:{grantId:N}")
                    : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task ConsumeAppReviewGrantAsync(
        AppReviewGrantContext grant,
        string storeCode,
        string hardwareId,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var consumed = await deviceRegistrationRepository.ConsumeAppReviewGrantAsync(
            new DeviceRegistrationAppReviewGrantConsumption
            {
                GrantId = grant.GrantId,
                StoreCode = storeCode,
                HardwareId = hardwareId,
                DeviceCode = deviceCode,
                ConsumedAtUtc = utcNowProvider().UtcDateTime
            },
            appReviewOptions.ExpiresAtUtc?.UtcDateTime
                ?? throw new InvalidOperationException("App Review activation expiry is required."),
            cancellationToken);
        if (consumed != 1)
        {
            throw new InvalidOperationException("App Review grant consumption was not recorded.");
        }
    }

    private sealed record AppReviewGrantContext(Guid GrantId, string Marker);

    private static DeviceRegisterResponse CreateRegisterResponse(
        string deviceCode,
        string storeCode,
        string storeName,
        int status,
        string message)
    {
        return new DeviceRegisterResponse(deviceCode, storeCode, storeName, status, false, message);
    }

    private static DeviceVerifyResponse CreateVerifyResponse(
        string deviceCode,
        string storeCode,
        string storeName,
        int status,
        string message)
    {
        return new DeviceVerifyResponse(deviceCode, storeCode, storeName, status, false, message);
    }

    private static DeviceReregisterResponse CreateReregisterResponse(
        string deviceCode,
        string storeCode,
        string storeName,
        int status,
        string message)
    {
        return new DeviceReregisterResponse(deviceCode, storeCode, storeName, status, false, message);
    }

    private static string GetStatusMessage(int status)
    {
        return status switch
        {
            PendingStatus => "Device registration is pending approval.",
            DisabledStatus => "Device is disabled.",
            EnabledStatus => "Device is enabled.",
            LockedStatus => "Device is locked.",
            UnregisteredStatus => "Device is not registered.",
            _ => "Device status is unknown."
        };
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Length == 0 ? null : normalized;
    }
}

public sealed class SqlSugarDeviceRegistrationRepository(HbposSqlSugarContext dbContext) : IDeviceRegistrationRepository
{
    internal const string FindAllByHardwareIdForRegistrationSql = """
        SELECT
            [ID] AS Id,
            [系统设备编号] AS DeviceCode,
            [分店代码] AS StoreCode,
            [设备硬件识别码] AS HardwareId,
            [设备状态] AS DeviceStatus,
            [设备授权码] AS AuthorizationCode,
            [设备系统] AS DeviceSystem
        FROM [POSM_设备注册信息表] WITH (UPDLOCK, HOLDLOCK)
        WHERE [设备硬件识别码] = @HardwareId
        ORDER BY [ID] DESC;
        """;

    internal const string FindLatestByHardwareIdAndStoreCodeSql = """
        SELECT TOP 1
            [ID] AS Id,
            [系统设备编号] AS DeviceCode,
            [分店代码] AS StoreCode,
            [设备硬件识别码] AS HardwareId,
            [设备状态] AS DeviceStatus,
            [设备授权码] AS AuthorizationCode,
            [设备系统] AS DeviceSystem
        FROM [POSM_设备注册信息表] WITH (UPDLOCK, HOLDLOCK)
        WHERE [设备硬件识别码] = @HardwareId
          AND [分店代码] = @StoreCode
        ORDER BY [ID] DESC;
        """;

    // 硬件码比较与 DeviceAuthorizationPlatformPolicy 一致（去空格、忽略大小写），只影响命中顺序，不放宽过滤条件。
    internal const string FindByDeviceCodeSql = """
        SELECT TOP 1
            [系统设备编号] AS DeviceCode,
            [分店代码] AS StoreCode,
            [设备硬件识别码] AS HardwareId,
            [设备状态] AS DeviceStatus,
            [设备授权码] AS AuthorizationCode,
            [设备系统] AS DeviceSystem
        FROM [POSM_设备注册信息表]
        WHERE [系统设备编号] = @DeviceCode
          AND [分店代码] = @StoreCode
        ORDER BY
            CASE
                WHEN @HardwareId <> ''
                     AND UPPER(LTRIM(RTRIM([设备硬件识别码]))) = UPPER(@HardwareId) THEN 0
                ELSE 1
            END,
            [ID] DESC;
        """;

    internal const string IsDeviceCodeTakenForRegistrationSql = """
        SELECT COUNT(1)
        FROM [dbo].[POSM_设备注册信息表] WITH (UPDLOCK, HOLDLOCK)
        WHERE [分店代码] = @StoreCode
          AND [系统设备编号] = @DeviceCode;
        """;

    internal const string CountActiveOrLockedByStoreCodeForRegistrationSql = """
        SELECT COUNT(1)
        FROM [dbo].[POSM_AppReviewGrantConsumptions] AS consumption WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN [dbo].[POSM_设备注册信息表] AS registration WITH (UPDLOCK, HOLDLOCK)
            ON registration.[分店代码] = consumption.[StoreCode]
           AND registration.[设备硬件识别码] = consumption.[HardwareId]
           AND registration.[系统设备编号] = consumption.[DeviceCode]
        WHERE consumption.[StoreCode] = @StoreCode
          AND registration.[设备状态] IN (1, 2);
        """;

    internal const string FindAppReviewGrantConsumptionSql = """
        SELECT TOP 1
            [GrantId],
            [StoreCode],
            [HardwareId],
            [DeviceCode],
            [ConsumedAtUtc]
        FROM [dbo].[POSM_AppReviewGrantConsumptions] WITH (UPDLOCK, HOLDLOCK)
        WHERE [GrantId] = @GrantId;
        """;

    internal const string ResetRegistrationForReregisterSql = """
        UPDATE [POSM_设备注册信息表]
        SET [设备状态] = @PendingStatus,
            [设备授权码] = @AuthorizationCode,
            [设备系统] = @DeviceSystem,
            [备注] = CONCAT(ISNULL([备注], ''), @RemarkSuffix),
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
          AND [设备状态] = @ExpectedDeviceStatus
          AND (
              [设备授权码] = @ExpectedAuthorizationCode
              OR ([设备授权码] IS NULL AND @ExpectedAuthorizationCode IS NULL)
          );
        """;

    internal const string ApproveRegistrationForAppReviewSql = """
        UPDATE [POSM_设备注册信息表]
        SET [设备状态] = @EnabledStatus,
            [设备授权码] = @AuthorizationCode,
            [设备系统] = @DeviceSystem,
            [备注] = CONCAT(ISNULL([备注], ''), @RemarkSuffix),
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
          AND [设备状态] = @ExpectedDeviceStatus
          AND (
              [设备授权码] = @ExpectedAuthorizationCode
              OR ([设备授权码] IS NULL AND @ExpectedAuthorizationCode IS NULL)
          );
        """;

    public async Task<DeviceRegistrationRecord?> FindLatestByHardwareIdAsync(
        string hardwareId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [系统设备编号] AS DeviceCode,
                [分店代码] AS StoreCode,
                [设备硬件识别码] AS HardwareId,
                [设备状态] AS DeviceStatus,
                [设备授权码] AS AuthorizationCode,
                [设备系统] AS DeviceSystem
            FROM [POSM_设备注册信息表]
            WHERE [设备硬件识别码] = @HardwareId
            ORDER BY [ID] DESC;
            """;

        var record = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<DeviceRegistrationRecord>(
            sql,
            new SugarParameter("@HardwareId", hardwareId));

        return record;
    }

    public async Task<DeviceRegistrationRecord?> FindByDeviceCodeAsync(
        string deviceCode,
        string storeCode,
        string? preferredHardwareId,
        CancellationToken cancellationToken)
    {
        // 关键逻辑：生产表没有（分店代码, 系统设备编号）唯一约束，历史撞号时必须确定性命中；
        // 空硬件码统一传空串，使所有行落入同一优先级、退化为取最新行。
        var record = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<DeviceRegistrationRecord>(
            FindByDeviceCodeSql,
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@HardwareId", (preferredHardwareId ?? string.Empty).Trim()));

        return record;
    }

    public async Task<bool> IsDeviceCodeTakenForRegistrationAsync(
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        // 关键逻辑：UPDLOCK + HOLDLOCK 在注册事务内锁住该门店设备号的键范围，
        // 并发请求会在此排队，等前一个事务提交后看到新写入的号码再顺延，与开通码路径同一做法。
        return await dbContext.PosmDb.Ado.GetIntAsync(
            IsDeviceCodeTakenForRegistrationSql,
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode)) > 0;
    }

    public async Task<DeviceRegistrationRecord?> FindLatestByDeviceCodeAndHardwareIdAsync(
        string deviceCode,
        string storeCode,
        string hardwareId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [系统设备编号] AS DeviceCode,
                [分店代码] AS StoreCode,
                [设备硬件识别码] AS HardwareId,
                [设备状态] AS DeviceStatus,
                [设备授权码] AS AuthorizationCode,
                [设备系统] AS DeviceSystem
            FROM [POSM_设备注册信息表]
            WHERE [系统设备编号] = @DeviceCode
              AND [分店代码] = @StoreCode
              AND [设备硬件识别码] = @HardwareId
            ORDER BY [ID] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<DeviceRegistrationRecord>(
            sql,
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@HardwareId", hardwareId));
    }

    public async Task<DeviceRegistrationRecord?> FindActiveOrLockedRegistrationAsync(
        string hardwareId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [系统设备编号] AS DeviceCode,
                [分店代码] AS StoreCode,
                [设备硬件识别码] AS HardwareId,
                [设备状态] AS DeviceStatus,
                [设备授权码] AS AuthorizationCode,
                [设备系统] AS DeviceSystem
            FROM [POSM_设备注册信息表]
            WHERE [设备硬件识别码] = @HardwareId
              AND [设备状态] IN (1, 2)
            ORDER BY [ID] DESC;
            """;

        var record = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<DeviceRegistrationRecord>(
            sql,
            new SugarParameter("@HardwareId", hardwareId));
        return record;
    }

    public async Task<DeviceRegistrationRecord?> FindLatestByHardwareIdAndStoreCodeAsync(
        string hardwareId,
        string storeCode,
        CancellationToken cancellationToken)
    {
        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<DeviceRegistrationRecord>(
            FindLatestByHardwareIdAndStoreCodeSql,
            new SugarParameter("@HardwareId", hardwareId),
            new SugarParameter("@StoreCode", storeCode));
    }

    public async Task<IReadOnlyList<DeviceRegistrationRecord>> FindAllByHardwareIdForRegistrationAsync(
        string hardwareId,
        CancellationToken cancellationToken)
    {
        // 关键逻辑：锁定同一硬件的完整键范围，直到匿名注册事务提交或回滚，避免并发插入绕过状态检查。
        return await dbContext.PosmDb.Ado.SqlQueryAsync<DeviceRegistrationRecord>(
            FindAllByHardwareIdForRegistrationSql,
            new SugarParameter("@HardwareId", hardwareId));
    }

    public async Task<int> CountActiveOrLockedByStoreCodeForRegistrationAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        // 关键逻辑：范围锁与注册写入同事务持有，确保 App Review 单设备上限在并发下仍成立。
        return await dbContext.PosmDb.Ado.GetIntAsync(
            CountActiveOrLockedByStoreCodeForRegistrationSql,
            new SugarParameter("@StoreCode", storeCode));
    }

    public async Task AcquireAppReviewGrantLockAsync(
        Guid grantId,
        string storeCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DECLARE @StoreResult int;
            DECLARE @GrantResult int;
            EXEC @StoreResult = sys.sp_getapplock
                @Resource = @StoreResource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 5000;
            IF @StoreResult < 0
                SELECT @StoreResult;
            ELSE
            BEGIN
                EXEC @GrantResult = sys.sp_getapplock
                    @Resource = @GrantResource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 5000;
                SELECT @GrantResult;
            END;
            """;
        var result = await dbContext.PosmDb.Ado.GetIntAsync(
            sql,
            new SugarParameter("@StoreResource", $"HBPOS:AppReviewStore:{storeCode}"),
            new SugarParameter("@GrantResource", $"HBPOS:AppReviewGrant:{grantId:N}"));
        if (result < 0)
        {
            throw new InvalidOperationException("Could not acquire the App Review grant lock.");
        }
    }

    public async Task<DeviceRegistrationAppReviewGrantConsumption?> FindAppReviewGrantConsumptionAsync(
        Guid grantId,
        CancellationToken cancellationToken)
    {
        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<DeviceRegistrationAppReviewGrantConsumption>(
            FindAppReviewGrantConsumptionSql,
            new SugarParameter("@GrantId", grantId));
    }

    public Task<int> ConsumeAppReviewGrantAsync(
        DeviceRegistrationAppReviewGrantConsumption consumption,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [dbo].[POSM_AppReviewGrantConsumptions]
                ([GrantId], [StoreCode], [HardwareId], [DeviceCode], [ConsumedAtUtc])
            SELECT
                @GrantId, @StoreCode, @HardwareId, @DeviceCode, @ConsumedAtUtc
            WHERE SYSUTCDATETIME() < @ExpiresAtUtc;
            """;
        // 关键逻辑：GrantId 主键保证一次消费；数据库时钟门禁保证到期瞬间会让整个注册事务回滚。
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@GrantId", consumption.GrantId),
            new SugarParameter("@StoreCode", consumption.StoreCode),
            new SugarParameter("@HardwareId", consumption.HardwareId),
            new SugarParameter("@DeviceCode", consumption.DeviceCode),
            new SugarParameter("@ConsumedAtUtc", consumption.ConsumedAtUtc),
            new SugarParameter("@ExpiresAtUtc", expiresAtUtc));
    }

    public async Task<bool> IsAppReviewDeviceAsync(
        string storeCode,
        string deviceCode,
        string hardwareId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM [dbo].[POSM_AppReviewGrantConsumptions]
            WHERE [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND [HardwareId] = @HardwareId;
            """;
        return await dbContext.PosmDb.Ado.GetIntAsync(
            sql,
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@HardwareId", hardwareId)) > 0;
    }

    public Task<int> DisablePendingRegistrationAsync(
        DeviceRegistrationDisableRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [POSM_设备注册信息表]
            SET [设备状态] = @DisabledStatus,
                [备注] = CONCAT(ISNULL([备注], ''), @RemarkSuffix)
            WHERE [系统设备编号] = @DeviceCode
              AND [分店代码] = @StoreCode
              AND [设备硬件识别码] = @HardwareId
              AND [设备状态] = @PendingStatus;
            """;

        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@DisabledStatus", 0),
            new SugarParameter("@RemarkSuffix", request.RemarkSuffix),
            new SugarParameter("@DeviceCode", request.DeviceCode),
            new SugarParameter("@StoreCode", request.StoreCode),
            new SugarParameter("@HardwareId", request.HardwareId),
            new SugarParameter("@PendingStatus", -1));
    }

    public Task<int> DisableActiveRegistrationAsync(
        string hardwareId,
        string deviceCode,
        string storeCode,
        string remarkSuffix,
        CancellationToken cancellationToken)
    {
        // 关键逻辑：状态条件固定为启用，避免并发状态变化时误禁用非当前授权记录。
        const string sql = """
            UPDATE [POSM_设备注册信息表]
            SET [设备状态] = @DisabledStatus,
                [备注] = CONCAT(ISNULL([备注], ''), @RemarkSuffix)
            WHERE [系统设备编号] = @DeviceCode
              AND [分店代码] = @StoreCode
              AND [设备硬件识别码] = @HardwareId
              AND [设备状态] = @EnabledStatus;
            """;

        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@DisabledStatus", 0),
            new SugarParameter("@RemarkSuffix", remarkSuffix),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@HardwareId", hardwareId),
            new SugarParameter("@EnabledStatus", 1));
    }

    public Task<int> ResetActiveRegistrationAsync(
        DeviceRegistrationResetActiveRequest request,
        CancellationToken cancellationToken)
    {
        // 关键逻辑：清除入口只能停用当前已认证的启用记录，且同步清空运行时在线身份。
        const string sql = """
            UPDATE [POSM_设备注册信息表]
            SET [设备状态] = @DisabledStatus,
                [备注] = CONCAT(ISNULL([备注], ''), @RemarkSuffix),
                [最后修改时间] = @ModifiedAtUtc,
                [最后修改人] = @ModifiedBy,
                [是否在线] = 0,
                [最后心跳时间] = NULL,
                [当前收银员ID] = NULL,
                [当前收银员姓名] = NULL,
                [收银员登录时间] = NULL
            WHERE [系统设备编号] = @DeviceCode
              AND [分店代码] = @StoreCode
              AND [设备硬件识别码] = @HardwareId
              AND [设备状态] = @EnabledStatus;
            """;

        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@DisabledStatus", 0),
            new SugarParameter("@RemarkSuffix", request.RemarkSuffix),
            new SugarParameter("@ModifiedAtUtc", request.ModifiedAtUtc),
            new SugarParameter("@ModifiedBy", request.ModifiedBy),
            new SugarParameter("@DeviceCode", request.DeviceCode),
            new SugarParameter("@StoreCode", request.StoreCode),
            new SugarParameter("@HardwareId", request.HardwareId),
            new SugarParameter("@EnabledStatus", 1));
    }

    public Task<int> ResetRegistrationForReregisterAsync(
        DeviceRegistrationResetForReregisterRequest request,
        CancellationToken cancellationToken)
    {
        // 关键逻辑：同时匹配查询快照的身份、状态和旧授权码，任何并发变化都以 0 行更新触发事务回滚。
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            ResetRegistrationForReregisterSql,
            new SugarParameter("@PendingStatus", -1),
            new SugarParameter("@AuthorizationCode", request.AuthorizationCode),
            new SugarParameter("@DeviceSystem", request.DeviceSystem),
            new SugarParameter("@RemarkSuffix", request.RemarkSuffix),
            new SugarParameter("@ModifiedAt", request.ModifiedAt),
            new SugarParameter("@ModifiedBy", request.ModifiedBy),
            new SugarParameter("@RegistrationId", request.RegistrationId),
            new SugarParameter("@HardwareId", request.HardwareId),
            new SugarParameter("@StoreCode", request.StoreCode),
            new SugarParameter("@DeviceCode", request.DeviceCode),
            new SugarParameter("@ExpectedDeviceStatus", request.ExpectedDeviceStatus),
            new SugarParameter("@ExpectedAuthorizationCode", request.ExpectedAuthorizationCode));
    }

    public Task<int> ApproveRegistrationForAppReviewAsync(
        DeviceRegistrationAppReviewApprovalRequest request,
        CancellationToken cancellationToken)
    {
        // 关键逻辑：审批只更新事务中读取的精确快照，任何并发状态变化都触发 0 行并回滚。
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            ApproveRegistrationForAppReviewSql,
            new SugarParameter("@EnabledStatus", 1),
            new SugarParameter("@AuthorizationCode", request.AuthorizationCode),
            new SugarParameter("@DeviceSystem", request.DeviceSystem),
            new SugarParameter("@RemarkSuffix", request.RemarkSuffix),
            new SugarParameter("@ModifiedAt", request.ModifiedAt),
            new SugarParameter("@ModifiedBy", "HBPOS_APP_REVIEW"),
            new SugarParameter("@RegistrationId", request.RegistrationId),
            new SugarParameter("@HardwareId", request.HardwareId),
            new SugarParameter("@StoreCode", request.StoreCode),
            new SugarParameter("@DeviceCode", request.DeviceCode),
            new SugarParameter("@ExpectedDeviceStatus", request.ExpectedDeviceStatus),
            new SugarParameter("@ExpectedAuthorizationCode", request.ExpectedAuthorizationCode));
    }

    public Task CreateRegistrationAsync(
        DeviceRegistrationCreateRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [POSM_设备注册信息表]
                ([设备硬件识别码], [系统设备编号], [分店代码], [设备类型], [设备系统], [设备状态], [设备授权码], [备注], [创建时间], [创建人])
            VALUES
                (@HardwareId, @DeviceCode, @StoreCode, @DeviceType, @DeviceSystem, @DeviceStatus, @AuthorizationCode, @Remark, @CreatedAt, @CreatedBy);
            """;

        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@HardwareId", request.HardwareId),
            new SugarParameter("@DeviceCode", request.DeviceCode),
            new SugarParameter("@StoreCode", request.StoreCode),
            new SugarParameter("@DeviceType", request.DeviceType),
            new SugarParameter("@DeviceSystem", request.DeviceSystem),
            new SugarParameter("@DeviceStatus", request.DeviceStatus),
            new SugarParameter("@AuthorizationCode", request.AuthorizationCode),
            new SugarParameter("@Remark", request.Remark),
            new SugarParameter("@CreatedAt", request.CreatedAt),
            new SugarParameter("@CreatedBy", request.CreatedBy));
    }

    public Task<int> UpdateRuntimeStatusAsync(
        DeviceRuntimeStatusUpdateRequest request,
        CancellationToken cancellationToken)
    {
        // 关键逻辑：心跳只更新当前授权设备的运行态字段；同一收银员连续上报时保留原登录时间。
        const string sql = """
            UPDATE [POSM_设备注册信息表]
            SET [是否在线] = @IsOnline,
                [最后心跳时间] = @ReportedAt,
                [收银员登录时间] = CASE
                    WHEN @HasCashier = 0 THEN NULL
                    WHEN ISNULL([当前收银员ID], '') = ISNULL(@CashierId, '')
                         AND [收银员登录时间] IS NOT NULL THEN [收银员登录时间]
                    ELSE @ReportedAt
                END,
                [当前收银员ID] = @CashierId,
                [当前收银员姓名] = @CashierName
            WHERE [设备硬件识别码] = @HardwareId
              AND [系统设备编号] = @DeviceCode
              AND [分店代码] = @StoreCode;
            """;

        var hasCashier = request.CashierId is not null || request.CashierName is not null;
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@IsOnline", request.IsOnline),
            new SugarParameter("@ReportedAt", request.ReportedAt),
            new SugarParameter("@HasCashier", hasCashier ? 1 : 0),
            new SugarParameter("@CashierId", request.CashierId),
            new SugarParameter("@CashierName", request.CashierName),
            new SugarParameter("@HardwareId", request.HardwareId),
            new SugarParameter("@DeviceCode", request.DeviceCode),
            new SugarParameter("@StoreCode", request.StoreCode));
    }

    public async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        await dbContext.PosmDb.Ado.BeginTranAsync();
        try
        {
            await action(cancellationToken);
            await dbContext.PosmDb.Ado.CommitTranAsync();
        }
        catch
        {
            await dbContext.PosmDb.Ado.RollbackTranAsync();
            throw;
        }
    }
}
