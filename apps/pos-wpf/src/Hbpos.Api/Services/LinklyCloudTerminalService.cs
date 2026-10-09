using System.Net;
using BlazorApp.Shared.Security;
using Hbpos.Api.Data;
using Hbpos.Contracts.Linkly;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface ILinklyCloudTerminalService
{
    Task<LinklyCloudTerminalListResponse> GetTerminalsAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken);

    Task<string> GetConfigurationModeAsync(
        string environment,
        string storeCode,
        CancellationToken cancellationToken);

    Task<LinklyCloudTerminalSelectionResponse> SelectTerminalAsync(
        string storeCode,
        string deviceCode,
        LinklyCloudTerminalSelectionRequest request,
        string? updatedBy,
        CancellationToken cancellationToken);

    Task<LinklyCloudTerminalPairResponse> PairTerminalAsync(
        string storeCode,
        string deviceCode,
        Guid terminalId,
        LinklyCloudBackendPairRequest request,
        string? updatedBy,
        CancellationToken cancellationToken);

    Task<LinklyCloudTerminalPaymentContext?> ResolvePaymentTerminalAsync(
        string environment,
        string storeCode,
        string deviceCode,
        Guid? terminalId,
        long? selectionRevision,
        CancellationToken cancellationToken);

    Task<LinklyCloudTerminalRecord?> GetTerminalAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken);

    Task<LinklyCloudTerminalConnectionTestResponse> ConnectionTestAsync(
        string storeCode,
        string deviceCode,
        Guid terminalId,
        LinklyCloudTerminalConnectionTestRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Linkly Cloud terminal connection testing is not available.");

    Task<LinklyCloudTerminalListResponse> AssignTerminalAsync(
        string storeCode,
        string deviceCode,
        Guid terminalId,
        LinklyCloudTerminalAssignmentRequest request,
        string? updatedBy,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Linkly Cloud terminal assignment is not available.");

    // 响应展示只能读取非敏感元数据；默认返回空，禁止替代实现意外回退到凭据解密。
    Task<string?> GetTerminalDisplayNameAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    Task<LinklyCloudTerminalOperationLease> AcquireOperationLeaseAsync(
        string environment,
        string storeCode,
        string deviceCode,
        LinklyCloudTerminalPaymentContext terminalContext,
        CancellationToken cancellationToken);

    Task ReleaseOperationLeaseAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        Guid leaseId,
        CancellationToken cancellationToken);

    // Status/Logon 检测结果不明时调用：把覆盖整次 HTTP 往返的长租约缩短为短时长（检测类租约），
    // 不再沿用 9 分钟的交易租约。默认实现保持原租约，兼容不关心租约时长的测试替身。
    Task ShortenOperationLeaseAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        Guid leaseId,
        CancellationToken cancellationToken) => Task.CompletedTask;

    // 健康快照只记录已明确完成的实体终端探测；不得影响配置版本、配对或 POS 选择。
    Task<bool> RecordHealthAsync(
        LinklyCloudTerminalPaymentContext terminalContext,
        string healthStatus,
        DateTime checkedAt,
        CancellationToken cancellationToken);
}

public interface ILinklyCloudTerminalRepository
{
    Task<IReadOnlyList<LinklyCloudTerminalRecord>> ListAsync(
        string environment,
        string storeCode,
        CancellationToken cancellationToken);

    Task<LinklyCloudTerminalRecord?> GetAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken);

    // 元数据查询与运行时凭据查询分离，状态/回单响应不得触碰 Password 或 Secret。
    Task<string?> GetDisplayNameAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    Task<LinklyCloudDeviceSelectionRecord?> GetSelectionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken);

    Task<LinklyCloudDeviceSelectionRecord> UpsertSelectionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        Guid terminalId,
        long? expectedRevision,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken);

    Task<LinklyCloudDeviceSelectionRecord> UpsertSelectionWithSnapshotAsync(
        string environment, string storeCode, string deviceCode, Guid terminalId, long? expectedRevision,
        DateTime updatedAt, string? updatedBy, CancellationToken cancellationToken, Guid? expectedOldTerminalId) =>
        UpsertSelectionAsync(environment, storeCode, deviceCode, terminalId, expectedRevision, updatedAt, updatedBy, cancellationToken);

    Task<string> GetConfigurationModeAsync(
        string environment,
        string storeCode,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LinklyCloudAssignableDevice>> ListAssignableDevicesAsync(
        string environment,
        string storeCode,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task AssignTerminalAsync(
        string environment,
        string storeCode,
        string sourceDeviceCode,
        Guid sourceTerminalId,
        DateTime expectedSourceUpdatedAt,
        string? expectedSourceAssignedDeviceCode,
        long expectedSourceRevision,
        string? targetDeviceCode,
        Guid? expectedTargetTerminalId,
        long expectedTargetRevision,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<bool> TryAcquireConnectionTestLeaseAsync(
        string environment, string storeCode, Guid terminalId,
        DateTime expectedUpdatedAt, Guid leaseId, DateTime leaseExpiresAt, DateTime now,
        CancellationToken cancellationToken, string? expectedAssignedDeviceCode = null,
        long expectedAssignmentRevision = 0, string? operationDeviceCode = null) => throw new NotSupportedException();

    Task ReleaseConnectionTestLeaseAsync(
        string environment, string storeCode, Guid terminalId, Guid leaseId,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<LinklyCloudTerminalRecord?> TryBeginPairingAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        Guid pairingAttemptId,
        DateTime pairingLeaseExpiresAt,
        DateTime expectedUpdatedAt,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken);

    Task<LinklyCloudTerminalRecord> UpdatePairingAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        Guid expectedPairingAttemptId,
        DateTime expectedUpdatedAt,
        string pairingState,
        string? secret,
        string? posId,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken);

    Task ReleasePairingLeaseAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        Guid expectedPairingAttemptId,
        CancellationToken cancellationToken);

    Task<bool> TryAcquireOperationLeaseAsync(
        string environment,
        string storeCode,
        string deviceCode,
        Guid terminalId,
        long expectedSelectionRevision,
        DateTime expectedTerminalUpdatedAt,
        Guid operationLeaseId,
        DateTime operationLeaseExpiresAt,
        DateTime now,
        CancellationToken cancellationToken);

    Task ReleaseOperationLeaseAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        Guid expectedOperationLeaseId,
        CancellationToken cancellationToken);

    // 只允许把仍属于该租约的到期时间提前（缩短），不会延长，也不会动其他租约。
    Task ShortenOperationLeaseAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        Guid expectedOperationLeaseId,
        DateTime newExpiresAt,
        CancellationToken cancellationToken) => Task.CompletedTask;

    // 使用终端配置版本作 CAS，防止迟到的探测结果覆盖已重配对或已更新凭据的终端。
    Task<bool> TryRecordHealthAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        DateTime expectedTerminalUpdatedAt,
        string healthStatus,
        DateTime checkedAt,
        CancellationToken cancellationToken);
}

public sealed record LinklyCloudTerminalPaymentContext(
    LinklyCloudTerminalRecord Terminal,
    LinklyCloudDeviceSelectionRecord Selection);

public sealed record LinklyCloudTerminalOperationLease(
    Guid LeaseId,
    Guid TerminalId,
    DateTime ExpiresAt);

public sealed record LinklyCloudTerminalRecord
{
    public Guid TerminalId { get; init; }

    public string Environment { get; init; } = string.Empty;

    public string StoreCode { get; init; } = string.Empty;

    public int LaneNo { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string? Secret { get; init; }

    public byte CredentialProtectionVersion { get; init; } =
        LinklyCloudTerminalCredentialDataProtection.LegacyPlaintextVersion;

    internal bool HasUsableSecret { get; init; }

    public string? PosId { get; init; }

    public string PairingState { get; init; } = "Unpaired";

    public Guid? PairingAttemptId { get; init; }

    public DateTime? PairingLeaseExpiresAt { get; init; }

    public string? LastHealthStatus { get; init; }

    public DateTime? LastHealthAt { get; init; }

    public DateTime? CreatedAt { get; init; }

    public DateTime? UpdatedAt { get; init; }

    public string? CreatedBy { get; init; }

    public string? UpdatedBy { get; init; }
}

public sealed class LinklyCloudDeviceSelectionRecord
{
    public string Environment { get; set; } = string.Empty;

    public string StoreCode { get; set; } = string.Empty;

    public string DeviceCode { get; set; } = string.Empty;

    public Guid TerminalId { get; set; }

    public long Revision { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }
}

internal sealed class LinklyCloudAssignableDeviceRow
{
    public string DeviceCode { get; set; } = string.Empty;
    public string DeviceSystem { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public Guid? SelectedTerminalId { get; set; }
    public long SelectionRevision { get; set; }
}

internal sealed class LinklyLeaseResult
{
    public bool Acquired { get; set; }
}

public sealed class LinklyCloudTerminalNotFoundException()
    : Exception("Linkly Cloud terminal was not found in the authenticated store and environment.");

public sealed class LinklyCloudTerminalNotReadyException()
    : Exception("Linkly Cloud terminal is not paired and ready.");

public sealed class LinklyCloudTerminalSelectionConflictException(
    string message = "Linkly Cloud terminal selection changed or the terminal is busy.") : Exception(message);

public sealed class LinklyCloudTerminalAssignedException()
    : Exception("Linkly Cloud terminal is already assigned to another POS.");

public sealed class LinklyCloudTerminalSessionActiveException()
    : Exception("The Linkly Cloud terminal has an active or unacknowledged operation and cannot be paired.");

public sealed class LinklyCloudTerminalPairingConflictException()
    : Exception("The Linkly Cloud terminal changed while pairing. Refresh the terminal list and pair again.");

public sealed class LinklyCloudTerminalCredentialReentryRequiredException()
    : Exception("Linkly Cloud terminal credentials must be re-entered in the management portal.");

public sealed class LinklyCloudTerminalCredentialUnavailableException()
    : Exception("Linkly Cloud terminal credentials are unavailable. Re-enter them in the management portal.");

/// <summary>
/// 终端正被 Status/Logon/连接检测占用（结果不明时保留的短租约）。它不是“有未完成交易”：
/// 继承 ActiveTransaction 异常只是为了让没有专门处理的调用方仍按 409 冲突返回，
/// 有专门处理的控制器返回专用错误码并带上剩余秒数。
/// </summary>
public sealed class LinklyCloudTerminalProbingException(int retryAfterSeconds)
    : LinklyCloudBackendActiveTransactionException(null)
{
    public int RetryAfterSeconds { get; } = Math.Max(1, retryAfterSeconds);

    public override string Message =>
        $"The Linkly Cloud terminal is being checked. Retry in {RetryAfterSeconds} seconds.";
}

public sealed class LinklyCloudTerminalService(
    ILinklyCloudTerminalRepository repository,
    ILinklyCloudBackendAsyncRepository sessionRepository,
    ILinklyCloudPairingTransport pairingTransport,
    IOptions<LinklyCloudBackendAsyncOptions> options,
    ILinklyCloudBackendTokenProvider? tokenProvider = null,
    ILinklyCloudBackendAsyncTransport? backendTransport = null,
    ILogger<LinklyCloudTerminalService>? logger = null,
    TimeProvider? timeProvider = null) : ILinklyCloudTerminalService
{
    private static readonly TimeSpan PairingLeaseDuration =
        LinklyTimeoutConstants.HttpTimeout + TimeSpan.FromMinutes(1);
    private static readonly TimeSpan OperationLeaseDuration =
        LinklyTimeoutConstants.HttpTimeout + LinklyTimeoutConstants.HttpTimeout + TimeSpan.FromMinutes(1);

    // 检测类（Status/Logon/连接检测）结果不明后保留的租约时长。租约在请求飞行期间必须覆盖完整往返（上面的
    // OperationLeaseDuration），但结果不明之后继续按 9 分钟占住终端，营业中一次网络抖动就会让这台终端
    // 9 分钟不能刷卡；检测类请求不涉及扣款，短暂保护即可。
    internal static readonly TimeSpan ProbeAmbiguousLeaseDuration = TimeSpan.FromSeconds(60);

    public async Task<LinklyCloudTerminalListResponse> GetTerminalsAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var terminals = await repository.ListAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            cancellationToken);
        var selection = await repository.GetSelectionAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);
        var mode = await repository.GetConfigurationModeAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            cancellationToken);
        var devices = await ListDevicesSafeAsync(normalizedEnvironment, normalizedStoreCode, cancellationToken);

        var summaries = new List<LinklyCloudTerminalSummary>(terminals.Count);
        foreach (var terminal in terminals.OrderBy(item => item.LaneNo).ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var active = await sessionRepository.GetActiveSessionByTerminalAsync(
                normalizedEnvironment,
                normalizedStoreCode,
                terminal.TerminalId,
                cancellationToken);
            summaries.Add(new LinklyCloudTerminalSummary(
                terminal.TerminalId,
                terminal.LaneNo,
                terminal.DisplayName,
                terminal.PairingState,
                active is not null,
                IsReady(terminal),
                terminal.LastHealthStatus,
                ToDateTimeOffset(terminal.LastHealthAt),
                devices.FirstOrDefault(item => item.SelectedTerminalId == terminal.TerminalId)?.DeviceCode,
                devices.FirstOrDefault(item => item.SelectedTerminalId == terminal.TerminalId)?.SelectionRevision ?? 0,
                ToTerminalVersion(terminal.UpdatedAt)));
        }

        return new LinklyCloudTerminalListResponse(
            normalizedEnvironment,
            selection?.TerminalId,
            selection?.Revision,
            summaries,
            mode,
            devices);
    }

    public async Task<LinklyCloudTerminalListResponse> AssignTerminalAsync(
        string storeCode,
        string deviceCode,
        Guid terminalId,
        LinklyCloudTerminalAssignmentRequest request,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var environment = NormalizeEnvironment(request.Environment);
        var normalizedStore = NormalizeRequired(storeCode, "storeCode");
        var normalizedDevice = NormalizeRequired(deviceCode, "deviceCode");
        var normalizedTarget = request.TargetDeviceCode is null ? null : NormalizeRequired(request.TargetDeviceCode, "targetDeviceCode");
        if (terminalId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExpectedTerminalVersion) || !string.Equals(request.ExpectedTerminalVersion,
                request.ExpectedTerminalVersion.Trim(), StringComparison.Ordinal))
        {
            throw new LinklyCloudBackendValidationException("terminalId and expectedTerminalVersion are required.");
        }

        var source = await GetRequiredTerminalAsync(environment, normalizedStore, terminalId, cancellationToken);
        if (!string.Equals(ToTerminalVersion(source.UpdatedAt), request.ExpectedTerminalVersion, StringComparison.Ordinal))
        {
            throw new LinklyCloudTerminalSelectionConflictException();
        }
        if (normalizedTarget is not null && !IsReady(source))
            throw new LinklyCloudTerminalNotReadyException();

        var devices = await ListDevicesSafeAsync(environment, normalizedStore, cancellationToken);
        var current = devices.FirstOrDefault(item => item.SelectedTerminalId == terminalId);
        var targetBefore = normalizedTarget is null
            ? null
            : devices.FirstOrDefault(item => string.Equals(item.DeviceCode, normalizedTarget, StringComparison.OrdinalIgnoreCase));
        if (!AssignmentCasMatches(current, request.ExpectedAssignedDeviceCode, request.ExpectedAssignmentRevision))
        {
            throw new LinklyCloudTerminalSelectionConflictException();
        }

        if (normalizedTarget is { } target)
        {
            target = NormalizeRequired(target, "targetDeviceCode");
            var targetDevice = devices.FirstOrDefault(item =>
                string.Equals(item.DeviceCode, target, StringComparison.OrdinalIgnoreCase));
            if (targetDevice is null || !targetDevice.IsAvailable)
            {
                throw new LinklyCloudBackendValidationException("targetDeviceCode must be an active POS in the same store.");
            }
            if (targetDevice.SelectedTerminalId is not null &&
                (request.ExpectedTargetTerminalId != targetDevice.SelectedTerminalId ||
                 request.ExpectedTargetSelectionRevision != targetDevice.SelectionRevision))
            {
                throw new LinklyCloudTerminalSelectionConflictException();
            }
            if (targetDevice.SelectedTerminalId is null &&
                (request.ExpectedTargetTerminalId is not null || request.ExpectedTargetSelectionRevision != 0))
            {
                throw new LinklyCloudTerminalSelectionConflictException();
            }
        }
        else if (request.ExpectedTargetTerminalId is not null || request.ExpectedTargetSelectionRevision != 0)
        {
            throw new LinklyCloudTerminalSelectionConflictException();
        }

        logger?.LogInformation(
            "Linkly assignment requested operator={Operator} callerDevice={CallerDevice} environment={Environment} store={StoreCode} terminal={TerminalId} sourceOwner={SourceOwner} sourceRevision={SourceRevision} targetDevice={TargetDevice} targetTerminal={TargetTerminal} targetRevision={TargetRevision} outcome=requested",
            NormalizeOptional(updatedBy), normalizedDevice, environment, normalizedStore, terminalId,
            request.ExpectedAssignedDeviceCode, request.ExpectedAssignmentRevision, normalizedTarget,
            request.ExpectedTargetTerminalId, request.ExpectedTargetSelectionRevision);
        try
        {
            await repository.AssignTerminalAsync(
            environment,
            normalizedStore,
            normalizedDevice,
            terminalId,
            source.UpdatedAt ?? throw new LinklyCloudTerminalSelectionConflictException(),
            request.ExpectedAssignedDeviceCode,
            request.ExpectedAssignmentRevision,
            normalizedTarget,
            request.ExpectedTargetTerminalId,
            request.ExpectedTargetSelectionRevision,
            NextUpdatedAt(source.UpdatedAt),
            NormalizeOptional(updatedBy),
                cancellationToken);
        }
        catch (Exception exception)
        {
            // 只记录公开范围和异常类型，不将可能包含凭据的异常正文写入审计。
            logger?.LogWarning(
                "Linkly assignment not confirmed operator={Operator} callerDevice={CallerDevice} environment={Environment} store={StoreCode} terminal={TerminalId} sourceOwner={SourceOwner} targetDevice={TargetDevice} outcome=not-confirmed error={ErrorType}",
                NormalizeOptional(updatedBy), normalizedDevice, environment, normalizedStore, terminalId,
                request.ExpectedAssignedDeviceCode, normalizedTarget, exception.GetType().Name);
            throw;
        }

        var result = await GetTerminalsAsync(normalizedStore, normalizedDevice, environment, cancellationToken);
        var targetAfter = normalizedTarget is null
            ? null
            : result.Devices?.FirstOrDefault(item => string.Equals(item.DeviceCode, normalizedTarget, StringComparison.OrdinalIgnoreCase));
        var sourceAfter = result.Devices?.FirstOrDefault(item => item.SelectedTerminalId == terminalId);
        logger?.LogInformation(
            "Linkly Cloud terminal assignment completed operator={Operator} callerDevice={CallerDevice} environment={Environment} store={StoreCode} sourceDevice={SourceDeviceCode} sourceTerminalBefore={SourceTerminalBefore} sourceTerminalAfter={SourceTerminalAfter} sourceRevisionBefore={SourceRevisionBefore} sourceRevisionAfter={SourceRevisionAfter} targetDevice={TargetDeviceCode} targetTerminalBefore={TargetTerminalBefore} targetRevisionBefore={TargetRevisionBefore} targetTerminalAfter={TargetTerminalAfter} targetRevisionAfter={TargetRevisionAfter} outcome={Outcome}",
            NormalizeOptional(updatedBy), normalizedDevice, environment, normalizedStore, current?.DeviceCode, current?.SelectedTerminalId, sourceAfter?.SelectedTerminalId, current?.SelectionRevision ?? 0, sourceAfter?.SelectionRevision ?? 0,
            normalizedTarget, targetBefore?.SelectedTerminalId, targetBefore?.SelectionRevision ?? 0,
            targetAfter?.SelectedTerminalId, targetAfter?.SelectionRevision ?? 0, "success");
        return result;
    }

    public async Task<LinklyCloudTerminalConnectionTestResponse> ConnectionTestAsync(
        string storeCode,
        string deviceCode,
        Guid terminalId,
        LinklyCloudTerminalConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var environment = NormalizeEnvironment(request.Environment);
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var terminal = await GetRequiredTerminalAsync(
            environment, NormalizeRequired(storeCode, "storeCode"), terminalId, cancellationToken);
        var version = ToTerminalVersion(terminal.UpdatedAt);
        if (!string.Equals(version, request.ExpectedTerminalVersion, StringComparison.Ordinal))
            throw new LinklyCloudTerminalSelectionConflictException();
        var devices = await ListDevicesSafeAsync(environment, terminal.StoreCode, cancellationToken);
        var owner = devices.FirstOrDefault(item => item.SelectedTerminalId == terminalId);
        if (!AssignmentCasMatches(owner, request.ExpectedAssignedDeviceCode, request.ExpectedAssignmentRevision))
            throw new LinklyCloudTerminalSelectionConflictException();
        var effectiveDeviceCode = owner?.DeviceCode ?? normalizedDeviceCode;
        var checkedAt = DateTimeOffset.UtcNow;
        if (!IsReady(terminal))
            return ConnectionResult(terminal, environment, owner, false, "needs-repair", checkedAt, "Terminal credentials require repair.", null);
        if (tokenProvider is null || backendTransport is null)
            return ConnectionResult(terminal, environment, owner, false, "unknown", checkedAt, "Connection test transport is unavailable.", null);

        var leaseId = Guid.NewGuid();
        // 覆盖 token 和 Status 两段 HTTP 的完整上界，避免检测仍在飞时租约已过期。
        var leaseExpiresAt = DateTime.UtcNow.Add(OperationLeaseDuration);
        if (!await repository.TryAcquireConnectionTestLeaseAsync(
                environment, terminal.StoreCode, terminalId, terminal.UpdatedAt!.Value,
                leaseId, leaseExpiresAt, DateTime.UtcNow, cancellationToken,
                owner?.DeviceCode, owner?.SelectionRevision ?? 0, effectiveDeviceCode))
        {
            // 被检测类短租约占着时给出专用错误码和剩余秒数，而不是笼统的“忙/有未确认操作”。
            var held = await repository.GetAsync(environment, terminal.StoreCode, terminalId, cancellationToken);
            if (held?.PairingAttemptId is not null &&
                held.PairingLeaseExpiresAt is { } heldUntil &&
                heldUntil > DateTime.UtcNow)
            {
                throw CreateLeaseHeldException(held, DateTime.UtcNow);
            }

            throw new LinklyCloudTerminalSelectionConflictException("Terminal is busy or has an unacknowledged operation.");
        }

        var releaseLease = true;
        var terminalRequestStarted = false;
        var phase = "token";
        var outcome = "unknown";
        int? providerHttpStatus = null;
        string? errorType = null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // Token 与 Status 共用一次业务预算；单段 HTTP 超时不能重新获得完整等待时间。
        // 来访取消仍单独判断；Status 已发出而终态不明时继续保留原管理租约。
        using var budgetCancellation = new CancellationTokenSource(
            LinklyTimeoutConstants.BusinessWait, timeProvider ?? TimeProvider.System);
        using var testCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, budgetCancellation.Token);
        var testToken = testCancellation.Token;
        try
        {
            var token = await tokenProvider.GetTokenAsync(
                environment, terminal.StoreCode, effectiveDeviceCode,
                terminalId, testToken);
            testToken.ThrowIfCancellationRequested();
            phase = "status";
            terminalRequestStarted = true;
            var response = await backendTransport.SendStatusAsync(
                new LinklyCloudBackendTransportStatusRequest(
                    environment, token.RestBaseUrl, token.AccessToken,
                    // 供应商 sessionId 必须是标准 UUID；连接测试用途由传输层审计字段标识。
                    Guid.NewGuid().ToString("D"), terminal.StoreCode,
                    effectiveDeviceCode, terminalId), testToken);
            terminalRequestStarted = false;
            providerHttpStatus = (int)response.StatusCode;
            phase = "status-response";
            var parsed = ParseConnectionStatus(response.Body);
            var definitive = response.StatusCode == System.Net.HttpStatusCode.OK && parsed.IsComplete;
            var connected = definitive && parsed.Success;
            var credentialsRejected = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
            var status = connected ? "connected" : credentialsRejected ? "needs-repair"
                : definitive || IsUnreachable(response.StatusCode) ? "unreachable" : "unknown";
            outcome = status;
            // 408、网关错误和未完成回应可能已送达终端；保留租约，不伪造持久健康状态。
            // 明确 4xx 表示请求已拒绝，可释放管理租约，让用户修复凭据。
            releaseLease = definitive || ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500
                && response.StatusCode != System.Net.HttpStatusCode.RequestTimeout);
            if (definitive)
            {
                var healthRecorded = await repository.TryRecordHealthAsync(
                    environment, terminal.StoreCode, terminalId, terminal.UpdatedAt.Value,
                    connected ? "Healthy" : "Unhealthy", checkedAt.UtcDateTime, CancellationToken.None);
                if (!healthRecorded)
                {
                    outcome = "unknown";
                    return ConnectionResult(terminal, environment, owner, false, "unknown", checkedAt, "Terminal configuration changed during the test.", parsed.ResponseCode);
                }
            }
            return ConnectionResult(terminal, environment, owner, connected, status, checkedAt,
                connected ? "Terminal is connected." : credentialsRejected
                    ? "Terminal credentials require repair." : "Terminal status was not confirmed.", parsed.ResponseCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            releaseLease = !terminalRequestStarted;
            outcome = "timeout";
            errorType = nameof(OperationCanceledException);
            return ConnectionResult(terminal, environment, owner, false, "unknown", checkedAt, "Connection test timed out.", null);
        }
        catch (OperationCanceledException)
        {
            releaseLease = !terminalRequestStarted;
            outcome = "caller-canceled";
            errorType = nameof(OperationCanceledException);
            throw;
        }
        catch (LinklyCloudTerminalRepairRequiredException ex)
        {
            // Token 端点以 401/403 拒绝 secret：终端在 Linkly 侧已被解除配对或 secret 已轮换。
            // 还没碰到终端，租约随 finally 释放，让管理员立刻去重新配对。
            outcome = "needs-repair";
            errorType = nameof(LinklyCloudTerminalRepairRequiredException);
            providerHttpStatus = (int)ex.TokenHttpStatus;
            return ConnectionResult(terminal, environment, owner, false, "needs-repair", checkedAt,
                "Terminal credentials require repair.", null);
        }
        catch (LinklyCloudBackendTokenUnavailableException ex)
        {
            // Token 端点暂时不可用（429/5xx/网络）：尚未触达终端，检测结果为“未确认”，不是 500。
            outcome = "unknown";
            errorType = nameof(LinklyCloudBackendTokenUnavailableException);
            providerHttpStatus = (int)ex.StatusCode;
            return ConnectionResult(terminal, environment, owner, false, "unknown", checkedAt,
                "Linkly Cloud authentication is temporarily unavailable.", null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            outcome = "needs-repair";
            errorType = ex.GetType().Name;
            providerHttpStatus = (int)ex.StatusCode.Value;
            return ConnectionResult(terminal, environment, owner, false, "needs-repair", checkedAt,
                "Terminal credentials require repair.", null);
        }
        catch (HttpRequestException ex)
        {
            releaseLease = !terminalRequestStarted;
            outcome = "unreachable";
            errorType = ex.GetType().Name;
            return ConnectionResult(terminal, environment, owner, false, "unreachable", checkedAt, "Terminal could not be reached.", null);
        }
        catch (LinklyCloudTerminalCredentialReentryRequiredException)
        {
            outcome = "needs-repair";
            errorType = nameof(LinklyCloudTerminalCredentialReentryRequiredException);
            return ConnectionResult(terminal, environment, owner, false, "needs-repair", checkedAt, "Terminal credentials require repair.", null);
        }
        catch (LinklyCloudTerminalCredentialUnavailableException)
        {
            outcome = "needs-repair";
            errorType = nameof(LinklyCloudTerminalCredentialUnavailableException);
            return ConnectionResult(terminal, environment, owner, false, "needs-repair", checkedAt, "Terminal credentials are unavailable.", null);
        }
        catch (Exception ex)
        {
            // Token 阶段失败尚未触碰终端；送出 Status 后的未知异常仍须保留保护。
            releaseLease = !terminalRequestStarted;
            outcome = "error";
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            if (releaseLease)
            {
                try
                {
                    await repository.ReleaseConnectionTestLeaseAsync(environment, terminal.StoreCode, terminalId, leaseId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning("Linkly Cloud connection-test lease release failed environment={Environment} store={StoreCode} terminalId={TerminalId} errorType={ErrorType}", environment, terminal.StoreCode, terminalId, ex.GetType().Name);
                }
            }
            else
            {
                // 检测结果不明：只保留检测类短租约，不沿用覆盖整次往返的 9 分钟租约。
                try
                {
                    await repository.ShortenOperationLeaseAsync(
                        environment, terminal.StoreCode, terminalId, leaseId,
                        DateTime.UtcNow.Add(ProbeAmbiguousLeaseDuration), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning("Linkly Cloud connection-test lease shorten failed environment={Environment} store={StoreCode} terminalId={TerminalId} errorType={ErrorType}", environment, terminal.StoreCode, terminalId, ex.GetType().Name);
                }
            }
            // 只记录公开结果和异常类型，不记录 token、响应正文或异常正文。
            logger?.LogInformation(
                "Linkly connection-test completed environment={Environment} store={StoreCode} terminalId={TerminalId} phase={Phase} outcome={Outcome} providerHttpStatus={ProviderHttpStatus} elapsedMs={ElapsedMs} errorType={ErrorType} budgetExpired={BudgetExpired} callerCanceled={CallerCanceled}",
                environment, terminal.StoreCode, terminalId, phase, outcome, providerHttpStatus,
                stopwatch.ElapsedMilliseconds, errorType, budgetCancellation.IsCancellationRequested, cancellationToken.IsCancellationRequested);
        }
    }

    private static LinklyCloudTerminalConnectionTestResponse ConnectionResult(
        LinklyCloudTerminalRecord terminal, string environment, LinklyCloudAssignableDevice? owner,
        bool succeeded, string status, DateTimeOffset checkedAt, string message, string? responseCode) =>
        new(terminal.TerminalId, environment, ToTerminalVersion(terminal.UpdatedAt) ?? string.Empty,
            owner?.DeviceCode, owner?.SelectionRevision ?? 0, succeeded, status, checkedAt, message, responseCode);

    private static bool IsUnreachable(System.Net.HttpStatusCode statusCode) =>
        statusCode == System.Net.HttpStatusCode.RequestTimeout || (int)statusCode >= 500;

    private static (bool Success, string? ResponseCode, bool IsComplete) ParseConnectionStatus(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (false, null, false);
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return (false, null, false);
            if ((root.TryGetProperty("Response", out var nested) || root.TryGetProperty("response", out nested))
                && nested.ValueKind == System.Text.Json.JsonValueKind.Object)
                root = nested;
            var code = root.TryGetProperty("ResponseCode", out var codeProperty) ? codeProperty.ToString() :
                root.TryGetProperty("responseCode", out codeProperty) ? codeProperty.ToString() : null;
            code = code?.Trim();
            var hasSuccess = (root.TryGetProperty("Success", out var successProperty) || root.TryGetProperty("success", out successProperty))
                && successProperty.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False;
            var providerSuccess = hasSuccess && successProperty.ValueKind == System.Text.Json.JsonValueKind.True;
            var successCode = string.Equals(code, "00", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "T0", StringComparison.OrdinalIgnoreCase);
            var success = providerSuccess && successCode;
            return (success, code is { Length: > 0 and <= 32 } ? code : null, hasSuccess && code is { Length: > 0 and <= 32 });
        }
        catch (System.Text.Json.JsonException) { return (false, null, false); }
    }

    private static bool AssignmentCasMatches(
        LinklyCloudAssignableDevice? current,
        string? expectedDeviceCode,
        long expectedRevision)
    {
        if (current is null)
        {
            return expectedDeviceCode is null && expectedRevision == 0;
        }
        return string.Equals(current.DeviceCode, expectedDeviceCode, StringComparison.OrdinalIgnoreCase) &&
            current.SelectionRevision == expectedRevision;
    }

    private async Task<IReadOnlyList<LinklyCloudAssignableDevice>> ListDevicesSafeAsync(
        string environment,
        string storeCode,
        CancellationToken cancellationToken)
    {
        try
        {
            return await repository.ListAssignableDevicesAsync(environment, storeCode, cancellationToken);
        }
        catch (NotSupportedException)
        {
            // 兼容仅实现历史终端接口的测试替身；生产 SQL repository 始终提供同店设备列表。
            return Array.Empty<LinklyCloudAssignableDevice>();
        }
    }

    public Task<string> GetConfigurationModeAsync(
        string environment,
        string storeCode,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        return repository.GetConfigurationModeAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            cancellationToken);
    }

    public async Task<LinklyCloudTerminalSelectionResponse> SelectTerminalAsync(
        string storeCode,
        string deviceCode,
        LinklyCloudTerminalSelectionRequest request,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var environment = NormalizeEnvironment(request.Environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        if (request.TerminalId == Guid.Empty)
        {
            throw new LinklyCloudBackendValidationException("terminalId is required.");
        }

        var terminal = await GetRequiredTerminalAsync(
            environment,
            normalizedStoreCode,
            request.TerminalId,
            cancellationToken);
        if (!IsReady(terminal))
        {
            throw new LinklyCloudTerminalNotReadyException();
        }

        var resumable = await sessionRepository.GetResumableSessionAsync(
            environment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);
        if (resumable is not null)
        {
            throw new LinklyCloudTerminalSelectionConflictException(
                "Current POS has a Linkly Cloud transaction that must be recovered or acknowledged before switching terminals.");
        }

        var current = await repository.GetSelectionAsync(
            environment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);
        if (current is not null && request.ExpectedRevision != current.Revision ||
            current is null && request.ExpectedRevision is not null and not 0)
        {
            throw new LinklyCloudTerminalSelectionConflictException();
        }

        var saved = await repository.UpsertSelectionWithSnapshotAsync(
            environment,
            normalizedStoreCode,
            normalizedDeviceCode,
            terminal.TerminalId,
            request.ExpectedRevision,
            DateTime.UtcNow,
            NormalizeOptional(updatedBy),
            cancellationToken,
            current?.TerminalId);
        return new LinklyCloudTerminalSelectionResponse(environment, saved.TerminalId, saved.Revision);
    }

    public async Task<LinklyCloudTerminalPairResponse> PairTerminalAsync(
        string storeCode,
        string deviceCode,
        Guid terminalId,
        LinklyCloudBackendPairRequest request,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var environment = NormalizeEnvironment(request.Environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        _ = NormalizeRequired(deviceCode, "deviceCode");
        var pairCode = NormalizePairCode(request.PairCode);
        if (terminalId == Guid.Empty)
        {
            throw new LinklyCloudPairingValidationException("terminalId is required.");
        }

        var terminal = await GetRequiredTerminalAsync(
            environment,
            normalizedStoreCode,
            terminalId,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(terminal.Username) || string.IsNullOrWhiteSpace(terminal.Password))
        {
            throw new LinklyCloudPairingCredentialMissingException();
        }

        var username = terminal.Username.Trim();
        var password = terminal.Password.Trim();
        var expectedUpdatedAt = terminal.UpdatedAt
            ?? throw new LinklyCloudTerminalPairingConflictException();
        var originalPairingState = terminal.PairingState;
        var originalSecret = terminal.Secret;
        var originalPosId = terminal.PosId;
        var pairingAttemptId = Guid.NewGuid();
        var markerUpdatedAt = NextUpdatedAt(expectedUpdatedAt);
        // 数据库租约是跨 API 实例的唯一配对闸门；超时/网络不确定时保留租约，避免重复上游配对。
        var pairingMarker = await repository.TryBeginPairingAsync(
            environment,
            normalizedStoreCode,
            terminalId,
            pairingAttemptId,
            markerUpdatedAt.Add(PairingLeaseDuration),
            expectedUpdatedAt,
            markerUpdatedAt,
            NormalizeOptional(updatedBy),
            cancellationToken);
        if (pairingMarker is null)
        {
            var blockingSession = await sessionRepository.GetBlockingSessionByTerminalAsync(
                environment,
                normalizedStoreCode,
                terminalId,
                cancellationToken);
            if (blockingSession is not null)
            {
                throw new LinklyCloudTerminalSessionActiveException();
            }

            var current = await repository.GetAsync(
                environment,
                normalizedStoreCode,
                terminalId,
                cancellationToken);
            if (current?.PairingAttemptId is not null &&
                current.PairingLeaseExpiresAt is not null &&
                current.PairingLeaseExpiresAt > DateTime.UtcNow)
            {
                throw new LinklyCloudPairingInProgressException();
            }

            throw new LinklyCloudTerminalPairingConflictException();
        }

        if (pairingMarker.PairingAttemptId != pairingAttemptId || pairingMarker.UpdatedAt != markerUpdatedAt)
        {
            throw new LinklyCloudTerminalPairingConflictException();
        }

        LinklyCloudPairingTransportResponse upstream;
        try
        {
            upstream = await pairingTransport.PairAsync(
                GetAuthBaseUrl(environment),
                username,
                password,
                pairCode,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new LinklyCloudPairingTimeoutException();
        }
        catch (Exception ex)
        {
            LogPairFailure(environment, normalizedStoreCode, terminalId, ex);
            throw new LinklyCloudPairingUpstreamException();
        }

        if (upstream.StatusCode == HttpStatusCode.RequestTimeout)
        {
            throw new LinklyCloudPairingTimeoutException();
        }

        if ((int)upstream.StatusCode is >= 400 and < 500)
        {
            await RestorePairingStateAsync(
                pairingMarker,
                originalPairingState,
                originalSecret,
                originalPosId,
                updatedBy);
            throw new LinklyCloudPairingRejectedException();
        }

        if (!((int)upstream.StatusCode is >= 200 and < 300) || string.IsNullOrWhiteSpace(upstream.Secret))
        {
            throw new LinklyCloudPairingUpstreamException();
        }

        var posId = IsUuidV4(terminal.PosId)
            ? terminal.PosId!.Trim()
            : Guid.NewGuid().ToString("D");
        LinklyCloudTerminalRecord saved;
        try
        {
            saved = await repository.UpdatePairingAsync(
                environment,
                normalizedStoreCode,
                terminalId,
                pairingAttemptId,
                pairingMarker.UpdatedAt ?? throw new LinklyCloudTerminalPairingConflictException(),
                "Ready",
                upstream.Secret.Trim(),
                posId,
                NextUpdatedAt(pairingMarker.UpdatedAt),
                NormalizeOptional(updatedBy),
                CancellationToken.None);
        }
        catch (LinklyCloudTerminalPairingConflictException)
        {
            // 上游已经成功，Pair Code 可能已被消费；CAS 冲突属于未知结果，必须保留租约阻止重放。
            throw new LinklyCloudPairingPersistenceException();
        }
        catch (Exception ex)
        {
            LogPairFailure(environment, normalizedStoreCode, terminalId, ex);
            throw new LinklyCloudPairingPersistenceException();
        }

        if (!IsReady(saved))
        {
            throw new LinklyCloudPairingPersistenceException();
        }

        return new LinklyCloudTerminalPairResponse(
            saved.TerminalId,
            saved.Environment,
            saved.DisplayName,
            saved.PairingState,
            true,
            "Linkly Cloud terminal paired successfully.");
    }

    public async Task<LinklyCloudTerminalPaymentContext?> ResolvePaymentTerminalAsync(
        string environment,
        string storeCode,
        string deviceCode,
        Guid? terminalId,
        long? selectionRevision,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var mode = await repository.GetConfigurationModeAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            cancellationToken);
        if (!string.Equals(mode, "Active", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (terminalId is null || terminalId == Guid.Empty || selectionRevision is null)
        {
            throw new LinklyCloudTerminalSelectionConflictException(
                "Active Linkly Cloud multi-terminal mode requires terminalId and selectionRevision.");
        }

        var selection = await repository.GetSelectionAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);
        if (selection is null ||
            selection.TerminalId != terminalId.Value ||
            selection.Revision != selectionRevision.Value)
        {
            throw new LinklyCloudTerminalSelectionConflictException();
        }

        var terminal = await GetRequiredTerminalAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            terminalId.Value,
            cancellationToken);
        if (!IsReady(terminal))
        {
            throw new LinklyCloudTerminalNotReadyException();
        }

        return new LinklyCloudTerminalPaymentContext(terminal, selection);
    }

    public Task<LinklyCloudTerminalRecord?> GetTerminalAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken)
    {
        return repository.GetAsync(
            NormalizeEnvironment(environment),
            NormalizeRequired(storeCode, "storeCode"),
            terminalId,
            cancellationToken);
    }

    public Task<string?> GetTerminalDisplayNameAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken)
    {
        return repository.GetDisplayNameAsync(
            NormalizeEnvironment(environment),
            NormalizeRequired(storeCode, "storeCode"),
            terminalId,
            cancellationToken);
    }

    public async Task<LinklyCloudTerminalOperationLease> AcquireOperationLeaseAsync(
        string environment,
        string storeCode,
        string deviceCode,
        LinklyCloudTerminalPaymentContext terminalContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminalContext);
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var terminal = terminalContext.Terminal;
        var selection = terminalContext.Selection;
        var expectedTerminalUpdatedAt = terminal.UpdatedAt
            ?? throw new LinklyCloudTerminalSelectionConflictException();
        if (!string.Equals(terminal.Environment, normalizedEnvironment, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(terminal.StoreCode, normalizedStoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(selection.Environment, normalizedEnvironment, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(selection.StoreCode, normalizedStoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(selection.DeviceCode, normalizedDeviceCode, StringComparison.OrdinalIgnoreCase) ||
            selection.TerminalId != terminal.TerminalId)
        {
            throw new LinklyCloudTerminalSelectionConflictException();
        }

        var now = DateTime.UtcNow;
        var leaseId = Guid.NewGuid();
        var expiresAt = now.Add(OperationLeaseDuration);
        var acquired = await repository.TryAcquireOperationLeaseAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            terminal.TerminalId,
            selection.Revision,
            expectedTerminalUpdatedAt,
            leaseId,
            expiresAt,
            now,
            cancellationToken);
        if (!acquired)
        {
            var blockingSession = await sessionRepository.GetBlockingSessionByTerminalAsync(
                normalizedEnvironment,
                normalizedStoreCode,
                terminal.TerminalId,
                cancellationToken);
            if (blockingSession is not null)
            {
                throw new LinklyCloudBackendActiveTransactionException(blockingSession.SessionId);
            }

            var current = await repository.GetAsync(
                normalizedEnvironment,
                normalizedStoreCode,
                terminal.TerminalId,
                cancellationToken);
            if (current?.PairingAttemptId is not null &&
                current.PairingLeaseExpiresAt is not null &&
                current.PairingLeaseExpiresAt > now)
            {
                throw CreateLeaseHeldException(current, now);
            }

            throw new LinklyCloudTerminalSelectionConflictException();
        }

        return new LinklyCloudTerminalOperationLease(leaseId, terminal.TerminalId, expiresAt);
    }

    /// <summary>
    /// 租约列复用了配对租约：配对会把 PairingState 置为 Unknown，而检测类租约只能在 Ready 的终端上取得。
    /// 因此 Ready 状态下仍被租约占着，就是检测类操作（结果不明时保留的短租约），返回剩余秒数让调用方稍后重试，
    /// 而不是误报“有未完成交易”。
    /// </summary>
    internal static LinklyCloudBackendActiveTransactionException CreateLeaseHeldException(
        LinklyCloudTerminalRecord terminal,
        DateTime nowUtc)
    {
        if (string.Equals(terminal.PairingState, "Ready", StringComparison.OrdinalIgnoreCase) &&
            terminal.PairingLeaseExpiresAt is { } expiresAt)
        {
            // 请求仍在飞行时租约到期时间是覆盖完整往返的悲观上界（9 分钟），实际检测一结束就会释放；
            // 对外提示的剩余秒数不超过检测类短租约时长，调用方稍后重试即可。
            var remaining = (int)Math.Ceiling((expiresAt - nowUtc).TotalSeconds);
            return new LinklyCloudTerminalProbingException(
                Math.Min(remaining, (int)ProbeAmbiguousLeaseDuration.TotalSeconds));
        }

        return new LinklyCloudBackendActiveTransactionException(null);
    }

    public Task ShortenOperationLeaseAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        return repository.ShortenOperationLeaseAsync(
            NormalizeEnvironment(environment),
            NormalizeRequired(storeCode, "storeCode"),
            terminalId,
            leaseId,
            DateTime.UtcNow.Add(ProbeAmbiguousLeaseDuration),
            cancellationToken);
    }

    public Task ReleaseOperationLeaseAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        return repository.ReleaseOperationLeaseAsync(
            NormalizeEnvironment(environment),
            NormalizeRequired(storeCode, "storeCode"),
            terminalId,
            leaseId,
            cancellationToken);
    }

    public Task<bool> RecordHealthAsync(
        LinklyCloudTerminalPaymentContext terminalContext,
        string healthStatus,
        DateTime checkedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminalContext);
        var expectedUpdatedAt = terminalContext.Terminal.UpdatedAt
            ?? throw new LinklyCloudTerminalSelectionConflictException();
        var normalizedStatus = healthStatus.Trim();
        if (!string.Equals(normalizedStatus, "Healthy", StringComparison.Ordinal) &&
            !string.Equals(normalizedStatus, "Unhealthy", StringComparison.Ordinal))
        {
            throw new ArgumentException("healthStatus must be Healthy or Unhealthy.", nameof(healthStatus));
        }

        return repository.TryRecordHealthAsync(
            NormalizeEnvironment(terminalContext.Terminal.Environment),
            NormalizeRequired(terminalContext.Terminal.StoreCode, "storeCode"),
            terminalContext.Terminal.TerminalId,
            expectedUpdatedAt,
            normalizedStatus,
            DateTime.SpecifyKind(checkedAt, DateTimeKind.Utc),
            cancellationToken);
    }

    private async Task<LinklyCloudTerminalRecord> GetRequiredTerminalAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken)
    {
        return await repository.GetAsync(environment, storeCode, terminalId, cancellationToken)
            ?? throw new LinklyCloudTerminalNotFoundException();
    }

    private async Task RestorePairingStateAsync(
        LinklyCloudTerminalRecord pairingMarker,
        string pairingState,
        string? secret,
        string? posId,
        string? updatedBy)
    {
        try
        {
            await repository.UpdatePairingAsync(
                pairingMarker.Environment,
                pairingMarker.StoreCode,
                pairingMarker.TerminalId,
                pairingMarker.PairingAttemptId ?? throw new LinklyCloudTerminalPairingConflictException(),
                pairingMarker.UpdatedAt ?? throw new LinklyCloudTerminalPairingConflictException(),
                pairingState,
                secret,
                posId,
                NextUpdatedAt(pairingMarker.UpdatedAt),
                NormalizeOptional(updatedBy),
                CancellationToken.None);
        }
        catch (LinklyCloudTerminalPairingConflictException)
        {
            // Web 已写入更新版本时以最新状态为准，绝不恢复旧配对材料。
            if (pairingMarker.PairingAttemptId is { } attemptId)
            {
                await ReleasePairingLeaseSafelyAsync(
                    pairingMarker.Environment,
                    pairingMarker.StoreCode,
                    pairingMarker.TerminalId,
                    attemptId);
            }
        }
        catch (Exception ex)
        {
            LogPairFailure(pairingMarker.Environment, pairingMarker.StoreCode, pairingMarker.TerminalId, ex);
        }
    }

    private async Task ReleasePairingLeaseSafelyAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        Guid pairingAttemptId)
    {
        try
        {
            await repository.ReleasePairingLeaseAsync(
                environment,
                storeCode,
                terminalId,
                pairingAttemptId,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 上游结果已明确但并发管理端写入使 CAS 失效；租约清理失败时仅等待自然过期。
            LogPairFailure(environment, storeCode, terminalId, ex);
        }
    }

    private string GetAuthBaseUrl(string environment)
    {
        return string.Equals(environment, "Sandbox", StringComparison.Ordinal)
            ? options.Value.SandboxAuthBaseUrl
            : options.Value.ProductionAuthBaseUrl;
    }

    private void LogPairFailure(string environment, string storeCode, Guid terminalId, Exception exception)
    {
        // 配对日志只保留范围及异常类型；账号、密码、Pair Code、Secret 与 PosId 永不记录。
        logger?.LogWarning(
            "Linkly Cloud terminal pairing failed environment={Environment} store={StoreCode} terminalId={TerminalId} error={ErrorType}",
            environment,
            storeCode,
            terminalId,
            exception.GetType().Name);
    }

    private static bool IsReady(LinklyCloudTerminalRecord terminal)
    {
        return terminal.CredentialProtectionVersion ==
                LinklyCloudTerminalCredentialDataProtection.CurrentVersion &&
            string.Equals(terminal.PairingState, "Ready", StringComparison.OrdinalIgnoreCase) &&
            (terminal.HasUsableSecret || !string.IsNullOrWhiteSpace(terminal.Secret)) &&
            IsUuidV4(terminal.PosId);
    }

    private static string NormalizeEnvironment(string? environment)
    {
        return LinklyCloudCredentialService.NormalizeEnvironment(environment)
            ?? throw new LinklyCloudBackendValidationException("environment must be Production or Sandbox");
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new LinklyCloudBackendValidationException($"{fieldName} is required.")
            : value.Trim();
    }

    private static string NormalizePairCode(string? pairCode)
    {
        var normalized = pairCode?.Trim();
        if (normalized is null || normalized.Length != 6 || normalized.Any(character => character is < '0' or > '9'))
        {
            throw new LinklyCloudPairingValidationException("pairCode must contain exactly 6 digits.");
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static DateTime NextUpdatedAt(DateTime? current)
    {
        var now = DateTime.UtcNow;
        return current.HasValue && now <= current.Value ? current.Value.AddTicks(1) : now;
    }

    private static bool IsUuidV4(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return normalized.Length == 36 &&
            Guid.TryParse(normalized, out _) &&
            normalized[14] == '4' &&
            normalized[19] is '8' or '9' or 'a' or 'A' or 'b' or 'B';
    }

    private static DateTimeOffset? ToDateTimeOffset(DateTime? value)
    {
        return value.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc))
            : null;
    }

    private static string? ToTerminalVersion(DateTime? value)
    {
        return value is null ? null : value.Value.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

public sealed class SqlSugarLinklyCloudTerminalRepository(
    HbposSqlSugarContext dbContext,
    ILinklyCloudTerminalCredentialProtector credentialProtector,
    ILogger<SqlSugarLinklyCloudTerminalRepository>? logger = null) : ILinklyCloudTerminalRepository
{
    internal const string TryRecordHealthSql = """
        UPDATE [dbo].[POSM_LinklyCloudTerminal]
        SET [LastHealthStatus] = @HealthStatus,
            [LastHealthAt] = @CheckedAt
        WHERE [Environment] = @Environment
          AND [StoreCode] = @StoreCode
          AND [TerminalId] = @TerminalId
          AND [UpdatedAt] = @ExpectedTerminalUpdatedAt
          AND ([LastHealthAt] IS NULL OR [LastHealthAt] <= @CheckedAt);
        """;

    internal const string GetDisplayNameSql = """
        SELECT TOP 1 [DisplayName]
        FROM [dbo].[POSM_LinklyCloudTerminal]
        WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [TerminalId] = @TerminalId;
        """;

    internal const string ListAssignableDevicesSql = """
        WITH registrations AS (
            SELECT [系统设备编号], [设备系统], [设备状态], [设备类型], [是否允许交易],
                   ROW_NUMBER() OVER (PARTITION BY [分店代码], [系统设备编号] ORDER BY [ID] DESC) AS [RowNo]
            FROM [dbo].[POSM_设备注册信息表]
            WHERE [分店代码] = @StoreCode
        )
        SELECT
            registration.[系统设备编号] AS DeviceCode,
            COALESCE(NULLIF(registration.[设备系统], N''), N'Unknown') AS DeviceSystem,
            CAST(CASE WHEN registration.[设备状态] = 1 AND UPPER(LTRIM(RTRIM(registration.[设备类型]))) = N'POS' AND ISNULL(registration.[是否允许交易], 0) = 1 THEN 1 ELSE 0 END AS bit) AS IsAvailable,
            selection.[TerminalId] AS SelectedTerminalId,
            ISNULL(selection.[Revision], 0) AS SelectionRevision
        FROM registrations AS registration
        LEFT JOIN [dbo].[POSM_LinklyCloudDeviceSelection] AS selection
          ON selection.[Environment] = @Environment AND selection.[StoreCode] = @StoreCode AND selection.[DeviceCode] = registration.[系统设备编号]
        WHERE registration.[RowNo] = 1
          AND NULLIF(LTRIM(RTRIM(registration.[系统设备编号])), N'') IS NOT NULL
        UNION ALL
        SELECT selection.[DeviceCode], N'Unknown', CAST(0 AS bit), selection.[TerminalId], selection.[Revision]
        FROM [dbo].[POSM_LinklyCloudDeviceSelection] AS selection
        WHERE selection.[Environment] = @Environment AND selection.[StoreCode] = @StoreCode
          AND NOT EXISTS (SELECT 1 FROM registrations AS historical WHERE historical.[RowNo] = 1 AND historical.[系统设备编号] = selection.[DeviceCode])
        ORDER BY DeviceCode;
        """;

    // 管理端转绑和解绑共用一个 Serializable CAS；锁序固定为会话 -> 终端 -> 选择 -> 模式。
    internal const string AssignTerminalSql = """
        SET XACT_ABORT ON;
        SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
        BEGIN TRANSACTION;
        -- 先覆盖全部设备和终端会话；后续阶段不得回头取会话锁。
        -- SupervisorResolved 只由主管结案的 ack 写入，与 Linkly 终态一样视为已结束；本文件各闸门同此口径。
        IF EXISTS (SELECT 1 FROM [dbo].[POSM_LinklyCloudBackendSession] WITH (UPDLOCK, HOLDLOCK)
                   WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode
                     AND ([DeviceCode] = @SourceDeviceCode OR [DeviceCode] = @ExpectedSourceAssignedDeviceCode
                          OR [DeviceCode] = @TargetDeviceCode OR [TerminalId] = @SourceTerminalId
                          OR [TerminalId] = @ExpectedTargetTerminalId)
                     AND ([IsActive] = 1 OR [Status] IS NULL
                          OR [Status] NOT IN (N'Completed', N'Cancelled', N'Failed', N'NotSubmitted', N'SupervisorResolved')
                          OR [ClientAcknowledgedAt] IS NULL))
            THROW 51002, 'POS or terminal has an active, unknown or unacknowledged operation.', 1;

        -- 请求中的旧目标只确定锁集合，选择阶段必须再次 CAS；逐个按 SQL Guid 顺序锁终端。
        DECLARE @FirstTerminalId UNIQUEIDENTIFIER = CASE
            WHEN @ExpectedTargetTerminalId IS NULL OR @SourceTerminalId <= @ExpectedTargetTerminalId
            THEN @SourceTerminalId ELSE @ExpectedTargetTerminalId END;
        DECLARE @SecondTerminalId UNIQUEIDENTIFIER = CASE
            WHEN @ExpectedTargetTerminalId IS NULL OR @SourceTerminalId <= @ExpectedTargetTerminalId
            THEN @ExpectedTargetTerminalId ELSE @SourceTerminalId END;
        DECLARE @LockedTerminalId UNIQUEIDENTIFIER;
        SELECT @LockedTerminalId = [TerminalId] FROM [dbo].[POSM_LinklyCloudTerminal] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [TerminalId] = @FirstTerminalId;
        IF @SecondTerminalId IS NOT NULL AND @SecondTerminalId <> @FirstTerminalId
            SELECT @LockedTerminalId = [TerminalId] FROM [dbo].[POSM_LinklyCloudTerminal] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [TerminalId] = @SecondTerminalId;
        IF NOT EXISTS (SELECT 1 FROM [dbo].[POSM_LinklyCloudTerminal]
                       WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode
                         AND [TerminalId] = @SourceTerminalId AND [UpdatedAt] = @ExpectedSourceUpdatedAt)
            THROW 51001, 'Terminal version conflict.', 1;
        IF EXISTS (SELECT 1 FROM [dbo].[POSM_LinklyCloudTerminal]
                   WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode
                     AND ([TerminalId] = @SourceTerminalId OR [TerminalId] = @ExpectedTargetTerminalId)
                     AND [PairingAttemptId] IS NOT NULL AND [PairingLeaseExpiresAt] > @UpdatedAt)
            THROW 51002, 'Linkly Cloud terminal is leased.', 1;
        IF @TargetDeviceCode IS NOT NULL
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM [dbo].[POSM_LinklyCloudTerminal]
                           WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [TerminalId] = @SourceTerminalId
                             AND [CredentialProtectionVersion] = 1 AND [PairingState] = N'Ready'
                             AND NULLIF(LTRIM(RTRIM([Secret])), N'') IS NOT NULL
                             AND NULLIF(LTRIM(RTRIM([PosId])), N'') IS NOT NULL)
                THROW 51003, 'Linkly Cloud terminal is not paired and ready.', 1;
            IF NOT EXISTS (SELECT 1 FROM [dbo].[POSM_设备注册信息表] AS registration WITH (UPDLOCK, HOLDLOCK)
                           WHERE registration.[分店代码] = @StoreCode AND registration.[系统设备编号] = @TargetDeviceCode
                             AND registration.[设备状态] = 1 AND UPPER(LTRIM(RTRIM(registration.[设备类型]))) = N'POS'
                             AND ISNULL(registration.[是否允许交易], 0) = 1
                             AND NOT EXISTS (SELECT 1 FROM [dbo].[POSM_设备注册信息表] AS newer
                                             WHERE newer.[分店代码] = @StoreCode AND newer.[系统设备编号] = @TargetDeviceCode
                                               AND newer.[ID] > registration.[ID]))
                THROW 51005, 'Target POS is not active.', 1;
        END;

        DECLARE @FirstDeviceCode NVARCHAR(64) = CASE
            WHEN @ExpectedSourceAssignedDeviceCode IS NULL THEN @TargetDeviceCode
            WHEN @TargetDeviceCode IS NULL OR @ExpectedSourceAssignedDeviceCode <= @TargetDeviceCode
            THEN @ExpectedSourceAssignedDeviceCode ELSE @TargetDeviceCode END;
        DECLARE @SecondDeviceCode NVARCHAR(64) = CASE
            WHEN @ExpectedSourceAssignedDeviceCode IS NULL THEN NULL
            WHEN @TargetDeviceCode IS NULL OR @ExpectedSourceAssignedDeviceCode <= @TargetDeviceCode
            THEN @TargetDeviceCode ELSE @ExpectedSourceAssignedDeviceCode END;
        DECLARE @LockedDeviceCode NVARCHAR(64);
        IF @FirstDeviceCode IS NOT NULL
            SELECT @LockedDeviceCode = [DeviceCode] FROM [dbo].[POSM_LinklyCloudDeviceSelection] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [DeviceCode] = @FirstDeviceCode;
        IF @SecondDeviceCode IS NOT NULL AND @SecondDeviceCode <> @FirstDeviceCode
            SELECT @LockedDeviceCode = [DeviceCode] FROM [dbo].[POSM_LinklyCloudDeviceSelection] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [DeviceCode] = @SecondDeviceCode;
        DECLARE @ActualOwner NVARCHAR(64) = NULL;
        DECLARE @ActualSourceRevision BIGINT = 0;
        SELECT @ActualOwner = [DeviceCode], @ActualSourceRevision = [Revision]
        FROM [dbo].[POSM_LinklyCloudDeviceSelection] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [TerminalId] = @SourceTerminalId;
        IF (@ActualOwner IS NULL AND @ExpectedSourceAssignedDeviceCode IS NOT NULL)
           OR (@ActualOwner IS NOT NULL AND (@ExpectedSourceAssignedDeviceCode IS NULL OR @ActualOwner <> @ExpectedSourceAssignedDeviceCode))
           OR @ActualSourceRevision <> @ExpectedSourceRevision
            THROW 51001, 'Source assignment revision conflict.', 1;
        DECLARE @ActualTargetTerminalId UNIQUEIDENTIFIER = NULL;
        DECLARE @ActualTargetRevision BIGINT = 0;
        IF @TargetDeviceCode IS NOT NULL
            SELECT @ActualTargetTerminalId = [TerminalId], @ActualTargetRevision = [Revision]
            FROM [dbo].[POSM_LinklyCloudDeviceSelection]
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [DeviceCode] = @TargetDeviceCode;
        IF (@ActualTargetTerminalId IS NULL AND @ExpectedTargetTerminalId IS NOT NULL)
           OR (@ActualTargetTerminalId IS NOT NULL AND (@ExpectedTargetTerminalId IS NULL OR @ActualTargetTerminalId <> @ExpectedTargetTerminalId))
           OR @ActualTargetRevision <> @ExpectedTargetRevision
            THROW 51001, 'Target assignment revision conflict.', 1;

        -- 事实未变时幂等返回，不提升版本；解绑不能删除调用方的其他线路。
        IF (@ActualOwner IS NULL AND @TargetDeviceCode IS NULL) OR @ActualOwner = @TargetDeviceCode
        BEGIN
            COMMIT TRANSACTION;
            RETURN;
        END;
        DELETE FROM [dbo].[POSM_LinklyCloudDeviceSelection]
        WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [TerminalId] = @SourceTerminalId;
        IF @TargetDeviceCode IS NOT NULL
        BEGIN
            IF @ActualTargetTerminalId IS NOT NULL
            BEGIN
                IF @ActualTargetRevision >= @MaxRevision THROW 51001, 'Target assignment revision conflict.', 1;
                UPDATE [dbo].[POSM_LinklyCloudDeviceSelection]
                SET [TerminalId] = @SourceTerminalId, [Revision] = [Revision] + 1,
                    [UpdatedAt] = @UpdatedAt, [UpdatedBy] = @UpdatedBy
                WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [DeviceCode] = @TargetDeviceCode;
            END
            ELSE
                INSERT INTO [dbo].[POSM_LinklyCloudDeviceSelection]
                    ([Environment], [StoreCode], [DeviceCode], [TerminalId], [Revision], [UpdatedAt], [UpdatedBy])
                VALUES (@Environment, @StoreCode, @TargetDeviceCode, @SourceTerminalId, @NewRevision, @UpdatedAt, @UpdatedBy);
        END;
        -- 源线路和被替换线路均作废旧检测；Secret、PosId 和配对状态始终不变。
        UPDATE [dbo].[POSM_LinklyCloudTerminal]
        SET [UpdatedAt] = CASE WHEN [UpdatedAt] >= @UpdatedAt THEN DATEADD(NANOSECOND, 100, [UpdatedAt]) ELSE @UpdatedAt END,
            [UpdatedBy] = @UpdatedBy, [LastHealthStatus] = NULL, [LastHealthAt] = NULL
        WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode
          AND ([TerminalId] = @SourceTerminalId OR [TerminalId] = @ActualTargetTerminalId);
        COMMIT TRANSACTION;
        """;

    internal const string TryAcquireConnectionTestLeaseSql = """
        SET XACT_ABORT ON;
        SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
        BEGIN TRANSACTION;
        IF EXISTS (SELECT 1 FROM [dbo].[POSM_LinklyCloudBackendSession] WITH (UPDLOCK, HOLDLOCK)
                   WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode
                     -- 未分配线路仍以调用 POS 发起供应商请求，因此会话隔离必须使用实际操作设备。
                     AND ([TerminalId] = @TerminalId OR [DeviceCode] = @OperationDeviceCode)
                     AND ([IsActive] = 1 OR [Status] IS NULL OR [Status] NOT IN (N'Completed', N'Cancelled', N'Failed', N'NotSubmitted', N'SupervisorResolved') OR [ClientAcknowledgedAt] IS NULL))
            THROW 51002, 'Terminal has a blocking operation.', 1;
        DECLARE @Acquired bit = 1;
        UPDATE [dbo].[POSM_LinklyCloudTerminal] WITH (UPDLOCK, HOLDLOCK)
        SET [PairingAttemptId] = @LeaseId, [PairingLeaseExpiresAt] = @LeaseExpiresAt
        WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [TerminalId] = @TerminalId
          AND [UpdatedAt] = @ExpectedUpdatedAt
          AND ([PairingAttemptId] IS NULL OR [PairingLeaseExpiresAt] IS NULL OR [PairingLeaseExpiresAt] <= @Now);
        IF @@ROWCOUNT <> 1 SET @Acquired = 0;
        IF @Acquired = 1 AND @ExpectedAssignedDeviceCode IS NULL AND EXISTS (
            SELECT 1 FROM [dbo].[POSM_LinklyCloudDeviceSelection] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [TerminalId] = @TerminalId)
            SET @Acquired = 0;
        IF @Acquired = 1 AND @ExpectedAssignedDeviceCode IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM [dbo].[POSM_LinklyCloudDeviceSelection] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @ExpectedAssignedDeviceCode AND [TerminalId] = @TerminalId
              AND [Revision] = @ExpectedAssignmentRevision)
            SET @Acquired = 0;
        IF @Acquired = 0
            UPDATE [dbo].[POSM_LinklyCloudTerminal] SET [PairingAttemptId] = NULL, [PairingLeaseExpiresAt] = NULL
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [TerminalId] = @TerminalId AND [PairingAttemptId] = @LeaseId;
        COMMIT TRANSACTION;
        SELECT @Acquired AS [Acquired];
        """;

    internal const string ReleaseConnectionTestLeaseSql = """
        UPDATE [dbo].[POSM_LinklyCloudTerminal]
        SET [PairingAttemptId] = NULL, [PairingLeaseExpiresAt] = NULL
        WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [TerminalId] = @TerminalId
          AND [PairingAttemptId] = @LeaseId;
        """;

    internal const string UpsertSelectionSql = """
        SET XACT_ABORT ON;
        SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
        BEGIN TRANSACTION;

        -- 旧终端快照仅确定锁集合；选择锁取得后还要重新核对快照和修订。
        IF EXISTS (
            SELECT 1 FROM [dbo].[POSM_LinklyCloudBackendSession] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode
              AND ([DeviceCode] = @DeviceCode OR [TerminalId] = @TerminalId OR [TerminalId] = @ExpectedOldTerminalId)
              AND ([IsActive] = 1 OR [Status] IS NULL
                   OR [Status] NOT IN (N'Completed', N'Cancelled', N'Failed', N'NotSubmitted', N'SupervisorResolved')
                   OR [ClientAcknowledgedAt] IS NULL))
            THROW 51002, 'Current POS or terminal has an operation that must be recovered or acknowledged.', 1;

        -- 逐个按 SQL uniqueidentifier 的同一顺序取锁，不能先锁目标再排序结果。
        DECLARE @FirstTerminalId UNIQUEIDENTIFIER = CASE
            WHEN @ExpectedOldTerminalId IS NULL OR @TerminalId <= @ExpectedOldTerminalId
            THEN @TerminalId ELSE @ExpectedOldTerminalId END;
        DECLARE @SecondTerminalId UNIQUEIDENTIFIER = CASE
            WHEN @ExpectedOldTerminalId IS NULL OR @TerminalId <= @ExpectedOldTerminalId
            THEN @ExpectedOldTerminalId ELSE @TerminalId END;
        DECLARE @LockedTerminalId UNIQUEIDENTIFIER;
        SELECT @LockedTerminalId = [TerminalId] FROM [dbo].[POSM_LinklyCloudTerminal] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [TerminalId] = @FirstTerminalId;
        IF @SecondTerminalId IS NOT NULL AND @SecondTerminalId <> @FirstTerminalId
            SELECT @LockedTerminalId = [TerminalId] FROM [dbo].[POSM_LinklyCloudTerminal] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [TerminalId] = @SecondTerminalId;

        IF NOT EXISTS (
            SELECT 1 FROM [dbo].[POSM_LinklyCloudTerminal]
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [TerminalId] = @TerminalId
              AND [CredentialProtectionVersion] = 1 AND [PairingState] = N'Ready'
              AND NULLIF(LTRIM(RTRIM([Secret])), N'') IS NOT NULL
              AND NULLIF(LTRIM(RTRIM([PosId])), N'') IS NOT NULL)
            THROW 51003, 'Linkly Cloud terminal is not paired and ready.', 1;
        IF EXISTS (
            SELECT 1 FROM [dbo].[POSM_LinklyCloudTerminal]
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode
              AND ([TerminalId] = @TerminalId OR [TerminalId] = @ExpectedOldTerminalId)
              AND [PairingAttemptId] IS NOT NULL AND [PairingLeaseExpiresAt] > @UpdatedAt)
            THROW 51002, 'Linkly Cloud terminal is leased.', 1;

        DECLARE @ExistingTerminalId UNIQUEIDENTIFIER = NULL;
        DECLARE @ExistingRevision BIGINT = NULL;
        SELECT @ExistingTerminalId = [TerminalId], @ExistingRevision = [Revision]
        FROM [dbo].[POSM_LinklyCloudDeviceSelection] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [DeviceCode] = @DeviceCode;
        IF (@ExpectedOldTerminalId IS NULL AND @ExistingTerminalId IS NOT NULL) OR
           (@ExpectedOldTerminalId IS NOT NULL AND (@ExistingTerminalId IS NULL OR @ExistingTerminalId <> @ExpectedOldTerminalId))
            THROW 51001, 'Existing terminal snapshot changed.', 1;
        IF EXISTS (
            SELECT 1 FROM [dbo].[POSM_LinklyCloudDeviceSelection] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode
              AND [TerminalId] = @TerminalId AND [DeviceCode] <> @DeviceCode)
            THROW 51004, 'Linkly Cloud terminal is already assigned to another POS.', 1;

        IF @ExistingTerminalId IS NOT NULL
        BEGIN
            IF @ExpectedRevision IS NULL OR @ExpectedRevision <> @ExistingRevision
                OR @ExistingRevision >= @MaxRevision
                THROW 51001, 'Linkly Cloud terminal selection revision conflict.', 1;
            UPDATE [dbo].[POSM_LinklyCloudDeviceSelection]
            SET [TerminalId] = @TerminalId, [Revision] = [Revision] + 1,
                [UpdatedAt] = @UpdatedAt, [UpdatedBy] = @UpdatedBy
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [DeviceCode] = @DeviceCode;
        END
        ELSE
        BEGIN
            IF @ExpectedRevision IS NOT NULL AND @ExpectedRevision <> 0
                THROW 51001, 'Linkly Cloud terminal selection revision conflict.', 1;
            INSERT INTO [dbo].[POSM_LinklyCloudDeviceSelection]
                ([Environment], [StoreCode], [DeviceCode], [TerminalId], [Revision], [UpdatedAt], [UpdatedBy])
            VALUES (@Environment, @StoreCode, @DeviceCode, @TerminalId, @NewRevision, @UpdatedAt, @UpdatedBy);
        END;
        COMMIT TRANSACTION;
        """;

    internal const string TryBeginPairingSql = """
        SET XACT_ABORT ON;
        SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
        BEGIN TRANSACTION;
        DECLARE @HasBlockingSession BIT = 0;
        DECLARE @UpdatedTerminal TABLE (
            [TerminalId] UNIQUEIDENTIFIER,
            [Environment] NVARCHAR(32),
            [StoreCode] NVARCHAR(32),
            [LaneNo] INT,
            [DisplayName] NVARCHAR(128),
            [Username] NVARCHAR(128),
            [Password] NVARCHAR(2048),
            [Secret] NVARCHAR(2048),
            [CredentialProtectionVersion] TINYINT,
            [PosId] NVARCHAR(64),
            [PairingState] NVARCHAR(32),
            [PairingAttemptId] UNIQUEIDENTIFIER,
            [PairingLeaseExpiresAt] DATETIME2(7),
            [LastHealthStatus] NVARCHAR(32),
            [LastHealthAt] DATETIME2(7),
            [CreatedAt] DATETIME2(7),
            [UpdatedAt] DATETIME2(7),
            [CreatedBy] NVARCHAR(128),
            [UpdatedBy] NVARCHAR(128));

        SELECT TOP (1) @HasBlockingSession = 1
        FROM [dbo].[POSM_LinklyCloudBackendSession] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Environment] = @Environment
          AND [StoreCode] = @StoreCode
          AND [TerminalId] = @TerminalId
          AND ([IsActive] = 1 OR [Status] IS NULL OR [Status] NOT IN (N'Completed', N'Cancelled', N'Failed', N'NotSubmitted', N'SupervisorResolved') OR [ClientAcknowledgedAt] IS NULL);

        IF @HasBlockingSession = 0
        BEGIN
            UPDATE [dbo].[POSM_LinklyCloudTerminal] WITH (UPDLOCK, HOLDLOCK)
            SET [PairingState] = N'Unknown',
                [PairingAttemptId] = @PairingAttemptId,
                [PairingLeaseExpiresAt] = @PairingLeaseExpiresAt,
                [UpdatedAt] = @UpdatedAt,
                [UpdatedBy] = @UpdatedBy
            OUTPUT inserted.[TerminalId], inserted.[Environment], inserted.[StoreCode], inserted.[LaneNo],
                   inserted.[DisplayName], inserted.[Username], inserted.[Password], inserted.[Secret],
                   inserted.[CredentialProtectionVersion], inserted.[PosId], inserted.[PairingState], inserted.[PairingAttemptId],
                   inserted.[PairingLeaseExpiresAt], inserted.[LastHealthStatus], inserted.[LastHealthAt],
                   inserted.[CreatedAt], inserted.[UpdatedAt], inserted.[CreatedBy], inserted.[UpdatedBy]
            INTO @UpdatedTerminal
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [TerminalId] = @TerminalId
              AND [CredentialProtectionVersion] = 1
              AND [UpdatedAt] = @ExpectedUpdatedAt
              AND (
                  [PairingAttemptId] IS NULL
                  OR [PairingLeaseExpiresAt] IS NULL
                  OR [PairingLeaseExpiresAt] <= @UpdatedAt);
        END;

        COMMIT TRANSACTION;
        SELECT * FROM @UpdatedTerminal;
        """;

    internal const string TryAcquireOperationLeaseSql = """
        SET XACT_ABORT ON;
        SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
        BEGIN TRANSACTION;
        DECLARE @HasBlockingSession BIT = 0;
        DECLARE @StoredTerminalId UNIQUEIDENTIFIER = NULL;
        DECLARE @StoredSelectionRevision BIGINT = NULL;
        DECLARE @Mode NVARCHAR(16) = N'Legacy';
        DECLARE @Acquired BIT = 0;

        -- 固定锁序：会话 -> 实体终端 -> POS 选择 -> 配置模式。
        SELECT TOP (1) @HasBlockingSession = 1
        FROM [dbo].[POSM_LinklyCloudBackendSession] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Environment] = @Environment
          AND [StoreCode] = @StoreCode
          AND [DeviceCode] = @DeviceCode
          AND ([IsActive] = 1 OR [Status] IS NULL OR [Status] NOT IN (N'Completed', N'Cancelled', N'Failed', N'NotSubmitted', N'SupervisorResolved') OR [ClientAcknowledgedAt] IS NULL);

        IF @HasBlockingSession = 0
        BEGIN
            SELECT TOP (1) @HasBlockingSession = 1
            FROM [dbo].[POSM_LinklyCloudBackendSession] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [TerminalId] = @TerminalId
              AND ([IsActive] = 1 OR [Status] IS NULL OR [Status] NOT IN (N'Completed', N'Cancelled', N'Failed', N'NotSubmitted', N'SupervisorResolved') OR [ClientAcknowledgedAt] IS NULL);
        END;

        SELECT @StoredTerminalId = [TerminalId]
        FROM [dbo].[POSM_LinklyCloudTerminal] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Environment] = @Environment
          AND [StoreCode] = @StoreCode
          AND [TerminalId] = @TerminalId
          AND [CredentialProtectionVersion] = 1
          AND [UpdatedAt] = @ExpectedTerminalUpdatedAt
          AND [PairingState] = N'Ready'
          AND NULLIF(LTRIM(RTRIM([Secret])), N'') IS NOT NULL
          AND NULLIF(LTRIM(RTRIM([PosId])), N'') IS NOT NULL
          AND (
              [PairingAttemptId] IS NULL
              OR [PairingLeaseExpiresAt] IS NULL
              OR [PairingLeaseExpiresAt] <= @Now
          );

        SELECT @StoredSelectionRevision = [Revision]
        FROM [dbo].[POSM_LinklyCloudDeviceSelection] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Environment] = @Environment
          AND [StoreCode] = @StoreCode
          AND [DeviceCode] = @DeviceCode
          AND [TerminalId] = @TerminalId
          AND [Revision] = @ExpectedSelectionRevision;

        SELECT @Mode = [Mode]
        FROM [dbo].[POSM_LinklyCloudConfigurationMode] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode;

        IF @HasBlockingSession = 0
           AND @StoredTerminalId IS NOT NULL
           AND @StoredSelectionRevision IS NOT NULL
           AND ISNULL(@Mode, N'Legacy') = N'Active'
        BEGIN
            UPDATE [dbo].[POSM_LinklyCloudTerminal]
            SET [PairingAttemptId] = @OperationLeaseId,
                [PairingLeaseExpiresAt] = @OperationLeaseExpiresAt
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [TerminalId] = @TerminalId;
            IF @@ROWCOUNT = 1 SET @Acquired = 1;
        END;

        COMMIT TRANSACTION;
        SELECT @Acquired AS [Acquired];
        """;

    public async Task<IReadOnlyList<LinklyCloudTerminalRecord>> ListAsync(
        string environment,
        string storeCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [TerminalId], [Environment], [StoreCode], [LaneNo], [DisplayName],
                   [Username], [Password], [Secret], [CredentialProtectionVersion], [PosId], [PairingState],
                   [PairingAttemptId], [PairingLeaseExpiresAt],
                   [LastHealthStatus], [LastHealthAt], [CreatedAt], [UpdatedAt], [CreatedBy], [UpdatedBy]
            FROM [dbo].[POSM_LinklyCloudTerminal]
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode
            ORDER BY [LaneNo], [DisplayName];
            """;
        var stored = await dbContext.PosmDb.Ado.SqlQueryAsync<LinklyCloudTerminalRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode));
        return stored
            .Select(item => MaterializeListTerminal(item, credentialProtector))
            .ToArray();
    }

    public async Task<LinklyCloudTerminalRecord?> GetAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1 [TerminalId], [Environment], [StoreCode], [LaneNo], [DisplayName],
                   [Username], [Password], [Secret], [CredentialProtectionVersion], [PosId], [PairingState],
                   [PairingAttemptId], [PairingLeaseExpiresAt],
                   [LastHealthStatus], [LastHealthAt], [CreatedAt], [UpdatedAt], [CreatedBy], [UpdatedBy]
            FROM [dbo].[POSM_LinklyCloudTerminal]
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [TerminalId] = @TerminalId;
            """;
        var stored = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudTerminalRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@TerminalId", terminalId));
        return stored is null ? null : MaterializeRuntimeTerminalWithLogging(stored);
    }

    public async Task<string?> GetDisplayNameAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudTerminalDisplayNameRow>(
            GetDisplayNameSql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@TerminalId", terminalId));
        return row?.DisplayName;
    }

    public async Task<IReadOnlyList<LinklyCloudAssignableDevice>> ListAssignableDevicesAsync(
        string environment,
        string storeCode,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.PosmDb.Ado.SqlQueryAsync<LinklyCloudAssignableDeviceRow>(
            ListAssignableDevicesSql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode));
        return rows.Select(row => new LinklyCloudAssignableDevice(
            row.DeviceCode.Trim(),
            row.DeviceSystem.Trim(),
            row.IsAvailable,
            row.SelectedTerminalId,
            row.SelectionRevision)).ToArray();
    }

    public async Task AssignTerminalAsync(
        string environment,
        string storeCode,
        string sourceDeviceCode,
        Guid sourceTerminalId,
        DateTime expectedSourceUpdatedAt,
        string? expectedSourceAssignedDeviceCode,
        long expectedSourceRevision,
        string? targetDeviceCode,
        Guid? expectedTargetTerminalId,
        long expectedTargetRevision,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        try
        {
            var newRevision = LinklyCloudSelectionRevision.CreateInitial();
            await dbContext.PosmDb.Ado.ExecuteCommandAsync(
                AssignTerminalSql,
                new SugarParameter("@Environment", environment),
                new SugarParameter("@StoreCode", storeCode),
                new SugarParameter("@SourceDeviceCode", sourceDeviceCode),
                new SugarParameter("@SourceTerminalId", sourceTerminalId),
                DateTime2Parameter("@ExpectedSourceUpdatedAt", expectedSourceUpdatedAt),
                new SugarParameter("@ExpectedSourceAssignedDeviceCode", expectedSourceAssignedDeviceCode),
                new SugarParameter("@ExpectedSourceRevision", expectedSourceRevision),
                new SugarParameter("@TargetDeviceCode", targetDeviceCode),
                new SugarParameter("@ExpectedTargetTerminalId", expectedTargetTerminalId),
                new SugarParameter("@ExpectedTargetRevision", expectedTargetRevision),
                new SugarParameter("@NewRevision", newRevision),
                new SugarParameter("@MaxRevision", LinklyCloudSelectionRevision.MaxJsSafeRevision),
                DateTime2Parameter("@UpdatedAt", updatedAt),
                new SugarParameter("@UpdatedBy", updatedBy));
        }
        catch (Exception ex) when (ex.ToString().Contains("51005", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Target POS is not active", StringComparison.OrdinalIgnoreCase))
        {
            throw new LinklyCloudBackendValidationException("targetDeviceCode must be an active POS in the same store.");
        }
        catch (Exception ex) when (ex.ToString().Contains("51002", StringComparison.OrdinalIgnoreCase) ||
            ex.ToString().Contains("51001", StringComparison.OrdinalIgnoreCase))
        {
            throw new LinklyCloudTerminalSelectionConflictException();
        }
        catch (Exception ex) when (ex.ToString().Contains("51003", StringComparison.OrdinalIgnoreCase))
        {
            throw new LinklyCloudTerminalNotReadyException();
        }
    }

    public async Task<bool> TryAcquireConnectionTestLeaseAsync(
        string environment, string storeCode, Guid terminalId,
        DateTime expectedUpdatedAt, Guid leaseId, DateTime leaseExpiresAt, DateTime now,
        CancellationToken cancellationToken, string? expectedAssignedDeviceCode = null,
        long expectedAssignmentRevision = 0, string? operationDeviceCode = null)
    {
        try
        {
            var row = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyLeaseResult>(
                TryAcquireConnectionTestLeaseSql,
                new SugarParameter("@Environment", environment),
                new SugarParameter("@StoreCode", storeCode),
                new SugarParameter("@TerminalId", terminalId),
                DateTime2Parameter("@ExpectedUpdatedAt", expectedUpdatedAt),
                new SugarParameter("@LeaseId", leaseId),
                DateTime2Parameter("@LeaseExpiresAt", leaseExpiresAt),
                DateTime2Parameter("@Now", now),
                new SugarParameter("@ExpectedAssignedDeviceCode", expectedAssignedDeviceCode),
                new SugarParameter("@ExpectedAssignmentRevision", expectedAssignmentRevision),
                new SugarParameter(
                    "@OperationDeviceCode",
                    operationDeviceCode ?? expectedAssignedDeviceCode));
            return row?.Acquired == true;
        }
        catch (Exception ex) when (ex.ToString().Contains("51002", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }

    public Task ReleaseConnectionTestLeaseAsync(
        string environment, string storeCode, Guid terminalId, Guid leaseId,
        CancellationToken cancellationToken) =>
        dbContext.PosmDb.Ado.ExecuteCommandAsync(
            ReleaseConnectionTestLeaseSql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@TerminalId", terminalId),
            new SugarParameter("@LeaseId", leaseId));

    internal static LinklyCloudTerminalRecord MaterializeRuntimeTerminal(
        LinklyCloudTerminalRecord stored,
        ILinklyCloudTerminalCredentialProtector protector)
    {
        if (stored.CredentialProtectionVersion !=
            LinklyCloudTerminalCredentialDataProtection.CurrentVersion)
        {
            throw new LinklyCloudTerminalCredentialReentryRequiredException();
        }

        try
        {
            var password = protector.UnprotectPassword(stored.Password);
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new LinklyCloudTerminalCredentialUnavailableException();
            }

            string? secret = null;
            if (!string.IsNullOrWhiteSpace(stored.Secret))
            {
                secret = protector.UnprotectSecret(stored.Secret);
                if (string.IsNullOrWhiteSpace(secret))
                {
                    throw new LinklyCloudTerminalCredentialUnavailableException();
                }
            }

            return stored with
            {
                Password = password,
                Secret = secret,
                HasUsableSecret = !string.IsNullOrWhiteSpace(secret)
            };
        }
        catch (LinklyCloudTerminalCredentialUnavailableException)
        {
            throw;
        }
        catch (Exception)
        {
            // Data Protection 的底层异常和密文都不得越过 repository 边界。
            throw new LinklyCloudTerminalCredentialUnavailableException();
        }
    }

    internal static LinklyCloudTerminalRecord MaterializeListTerminal(
        LinklyCloudTerminalRecord stored,
        ILinklyCloudTerminalCredentialProtector protector)
    {
        try
        {
            var runtime = MaterializeRuntimeTerminal(stored, protector);
            return runtime with
            {
                Password = string.Empty,
                Secret = null,
                HasUsableSecret = !string.IsNullOrWhiteSpace(runtime.Secret)
            };
        }
        catch (LinklyCloudTerminalCredentialReentryRequiredException)
        {
            return MarkCredentialRepairRequired(stored);
        }
        catch (LinklyCloudTerminalCredentialUnavailableException)
        {
            return MarkCredentialRepairRequired(stored);
        }
    }

    internal static LinklyCloudTerminalRecord MaterializePairingCompletion(
        LinklyCloudTerminalRecord stored)
    {
        // Pair 完成写入已经提交；这里只返回响应所需状态，禁止再次解密或向 service 传递密文。
        return stored with
        {
            Username = string.Empty,
            Password = string.Empty,
            Secret = null,
            HasUsableSecret = stored.CredentialProtectionVersion ==
                LinklyCloudTerminalCredentialDataProtection.CurrentVersion &&
                !string.IsNullOrWhiteSpace(stored.Secret)
        };
    }

    internal static string? ProtectSecretForStorage(
        string? secret,
        ILinklyCloudTerminalCredentialProtector protector)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        try
        {
            var protectedSecret = protector.ProtectSecret(secret);
            return string.IsNullOrWhiteSpace(protectedSecret)
                ? throw new LinklyCloudTerminalCredentialUnavailableException()
                : protectedSecret;
        }
        catch (LinklyCloudTerminalCredentialUnavailableException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new LinklyCloudTerminalCredentialUnavailableException();
        }
    }

    private LinklyCloudTerminalRecord MaterializeRuntimeTerminalWithLogging(
        LinklyCloudTerminalRecord stored)
    {
        try
        {
            return MaterializeRuntimeTerminal(stored, credentialProtector);
        }
        catch (Exception ex) when (
            ex is LinklyCloudTerminalCredentialReentryRequiredException or
                LinklyCloudTerminalCredentialUnavailableException)
        {
            logger?.LogWarning(
                "Linkly Cloud terminal credential materialization failed environment={Environment} store={StoreCode} terminalId={TerminalId} error={ErrorType}",
                stored.Environment,
                stored.StoreCode,
                stored.TerminalId,
                ex.GetType().Name);
            throw;
        }
    }

    private static LinklyCloudTerminalRecord MarkCredentialRepairRequired(
        LinklyCloudTerminalRecord stored) => stored with
        {
            Password = string.Empty,
            Secret = null,
            PairingState = "NeedsRepair",
            HasUsableSecret = false
        };

    public async Task<LinklyCloudDeviceSelectionRecord?> GetSelectionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1 [Environment], [StoreCode], [DeviceCode], [TerminalId], [Revision], [UpdatedAt], [UpdatedBy]
            FROM [dbo].[POSM_LinklyCloudDeviceSelection]
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode AND [DeviceCode] = @DeviceCode;
            """;
        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudDeviceSelectionRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode));
    }

    internal static bool IsTerminalAssignmentViolation(Exception exception)
    {
        var details = exception.ToString();
        return details.Contains("51004", StringComparison.OrdinalIgnoreCase) ||
            details.Contains("UX_POSM_LinklyCloudDeviceSelection_Scope_Terminal", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("already assigned to another POS", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<LinklyCloudDeviceSelectionRecord> UpsertSelectionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        Guid terminalId,
        long? expectedRevision,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSelectionAsync(environment, storeCode, deviceCode, cancellationToken);
        return await UpsertSelectionWithSnapshotAsync(environment, storeCode, deviceCode, terminalId,
            expectedRevision, updatedAt, updatedBy, cancellationToken, snapshot?.TerminalId);
    }

    public async Task<LinklyCloudDeviceSelectionRecord> UpsertSelectionWithSnapshotAsync(
        string environment,
        string storeCode,
        string deviceCode,
        Guid terminalId,
        long? expectedRevision,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken,
        Guid? expectedOldTerminalId)
    {
        try
        {
            var newRevision = expectedRevision is null or 0
                ? LinklyCloudSelectionRevision.CreateInitial()
                : 0;
            await dbContext.PosmDb.Ado.ExecuteCommandAsync(
                UpsertSelectionSql,
                new SugarParameter("@Environment", environment),
                new SugarParameter("@StoreCode", storeCode),
                new SugarParameter("@DeviceCode", deviceCode),
                new SugarParameter("@TerminalId", terminalId),
                new SugarParameter("@ExpectedRevision", expectedRevision),
                new SugarParameter("@ExpectedOldTerminalId", expectedOldTerminalId),
                new SugarParameter("@NewRevision", newRevision),
                new SugarParameter("@MaxRevision", LinklyCloudSelectionRevision.MaxJsSafeRevision),
                DateTime2Parameter("@UpdatedAt", updatedAt),
                new SugarParameter("@UpdatedBy", updatedBy));
        }
        catch (Exception ex) when (IsTerminalAssignmentViolation(ex))
        {
            throw new LinklyCloudTerminalAssignedException();
        }
        catch (Exception ex) when (ex.ToString().Contains("51001", StringComparison.OrdinalIgnoreCase) ||
            ex.ToString().Contains("51002", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("must be recovered or acknowledged", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("selection revision conflict", StringComparison.OrdinalIgnoreCase))
        {
            throw new LinklyCloudTerminalSelectionConflictException();
        }
        catch (Exception ex) when (ex.ToString().Contains("51003", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("not paired and ready", StringComparison.OrdinalIgnoreCase))
        {
            throw new LinklyCloudTerminalNotReadyException();
        }

        return await GetSelectionAsync(environment, storeCode, deviceCode, cancellationToken)
            ?? throw new LinklyCloudTerminalSelectionConflictException();
    }

    public async Task<string> GetConfigurationModeAsync(
        string environment,
        string storeCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1 [Mode]
            FROM [dbo].[POSM_LinklyCloudConfigurationMode]
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode;
            """;
        var result = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudConfigurationModeRow>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode));
        return string.IsNullOrWhiteSpace(result?.Mode) ? "Legacy" : result.Mode.Trim();
    }

    public async Task<LinklyCloudTerminalRecord?> TryBeginPairingAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        Guid pairingAttemptId,
        DateTime pairingLeaseExpiresAt,
        DateTime expectedUpdatedAt,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        var stored = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudTerminalRecord>(
            TryBeginPairingSql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@TerminalId", terminalId),
            new SugarParameter("@PairingAttemptId", pairingAttemptId),
            DateTime2Parameter("@PairingLeaseExpiresAt", pairingLeaseExpiresAt),
            DateTime2Parameter("@ExpectedUpdatedAt", expectedUpdatedAt),
            DateTime2Parameter("@UpdatedAt", updatedAt),
            new SugarParameter("@UpdatedBy", updatedBy));
        return stored is null ? null : MaterializeRuntimeTerminalWithLogging(stored);
    }

    public async Task<LinklyCloudTerminalRecord> UpdatePairingAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        Guid expectedPairingAttemptId,
        DateTime expectedUpdatedAt,
        string pairingState,
        string? secret,
        string? posId,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DECLARE @UpdatedTerminal TABLE (
                [TerminalId] UNIQUEIDENTIFIER,
                [Environment] NVARCHAR(32),
                [StoreCode] NVARCHAR(32),
                [LaneNo] INT,
                [DisplayName] NVARCHAR(128),
                [Username] NVARCHAR(128),
                [Password] NVARCHAR(2048),
                [Secret] NVARCHAR(2048),
                [CredentialProtectionVersion] TINYINT,
                [PosId] NVARCHAR(64),
                [PairingState] NVARCHAR(32),
                [PairingAttemptId] UNIQUEIDENTIFIER,
                [PairingLeaseExpiresAt] DATETIME2(7),
                [LastHealthStatus] NVARCHAR(32),
                [LastHealthAt] DATETIME2(7),
                [CreatedAt] DATETIME2(7),
                [UpdatedAt] DATETIME2(7),
                [CreatedBy] NVARCHAR(128),
                [UpdatedBy] NVARCHAR(128));

            UPDATE [dbo].[POSM_LinklyCloudTerminal]
            SET [PairingState] = @PairingState,
                [Secret] = @Secret,
                [CredentialProtectionVersion] = @CredentialProtectionVersion,
                [PosId] = @PosId,
                [PairingAttemptId] = NULL,
                [PairingLeaseExpiresAt] = NULL,
                [UpdatedAt] = @UpdatedAt,
                [UpdatedBy] = @UpdatedBy
            OUTPUT inserted.[TerminalId], inserted.[Environment], inserted.[StoreCode], inserted.[LaneNo],
                   inserted.[DisplayName], inserted.[Username], inserted.[Password], inserted.[Secret],
                   inserted.[CredentialProtectionVersion], inserted.[PosId], inserted.[PairingState], inserted.[PairingAttemptId],
                   inserted.[PairingLeaseExpiresAt], inserted.[LastHealthStatus], inserted.[LastHealthAt],
                   inserted.[CreatedAt], inserted.[UpdatedAt], inserted.[CreatedBy], inserted.[UpdatedBy]
            INTO @UpdatedTerminal
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [TerminalId] = @TerminalId
              AND [PairingAttemptId] = @ExpectedPairingAttemptId
              AND [UpdatedAt] = @ExpectedUpdatedAt;

            SELECT * FROM @UpdatedTerminal;
            """;
        var protectedSecret = ProtectSecretForStorage(secret, credentialProtector);
        var updated = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudTerminalRecord>(
            sql,
            new SugarParameter("@PairingState", pairingState),
            new SugarParameter("@Secret", protectedSecret),
            new SugarParameter(
                "@CredentialProtectionVersion",
                LinklyCloudTerminalCredentialDataProtection.CurrentVersion),
            new SugarParameter("@PosId", posId),
            DateTime2Parameter("@UpdatedAt", updatedAt),
            new SugarParameter("@UpdatedBy", updatedBy),
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@TerminalId", terminalId),
            new SugarParameter("@ExpectedPairingAttemptId", expectedPairingAttemptId),
            DateTime2Parameter("@ExpectedUpdatedAt", expectedUpdatedAt));
        if (updated is null)
        {
            throw new LinklyCloudTerminalPairingConflictException();
        }

        return MaterializePairingCompletion(updated);
    }

    public async Task<bool> TryAcquireOperationLeaseAsync(
        string environment,
        string storeCode,
        string deviceCode,
        Guid terminalId,
        long expectedSelectionRevision,
        DateTime expectedTerminalUpdatedAt,
        Guid operationLeaseId,
        DateTime operationLeaseExpiresAt,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var result = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudOperationLeaseResult>(
            TryAcquireOperationLeaseSql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@TerminalId", terminalId),
            new SugarParameter("@ExpectedSelectionRevision", expectedSelectionRevision),
            DateTime2Parameter("@ExpectedTerminalUpdatedAt", expectedTerminalUpdatedAt),
            new SugarParameter("@OperationLeaseId", operationLeaseId),
            DateTime2Parameter("@OperationLeaseExpiresAt", operationLeaseExpiresAt),
            DateTime2Parameter("@Now", now));
        return result?.Acquired == true;
    }

    public async Task ReleasePairingLeaseAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        Guid expectedPairingAttemptId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [dbo].[POSM_LinklyCloudTerminal]
            SET [PairingAttemptId] = NULL,
                [PairingLeaseExpiresAt] = NULL
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [TerminalId] = @TerminalId
              AND [PairingAttemptId] = @ExpectedPairingAttemptId;
            """;
        await dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@TerminalId", terminalId),
            new SugarParameter("@ExpectedPairingAttemptId", expectedPairingAttemptId));
    }

    public async Task ReleaseOperationLeaseAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        Guid expectedOperationLeaseId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [dbo].[POSM_LinklyCloudTerminal]
            SET [PairingAttemptId] = NULL,
                [PairingLeaseExpiresAt] = NULL
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [TerminalId] = @TerminalId
              AND [PairingAttemptId] = @ExpectedOperationLeaseId;
            """;
        await dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@TerminalId", terminalId),
            new SugarParameter("@ExpectedOperationLeaseId", expectedOperationLeaseId));
    }

    internal const string ShortenOperationLeaseSql = """
        UPDATE [dbo].[POSM_LinklyCloudTerminal]
        SET [PairingLeaseExpiresAt] = @NewExpiresAt
        WHERE [Environment] = @Environment
          AND [StoreCode] = @StoreCode
          AND [TerminalId] = @TerminalId
          AND [PairingAttemptId] = @ExpectedOperationLeaseId
          AND [PairingLeaseExpiresAt] > @NewExpiresAt;
        """;

    public async Task ShortenOperationLeaseAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        Guid expectedOperationLeaseId,
        DateTime newExpiresAt,
        CancellationToken cancellationToken)
    {
        await dbContext.PosmDb.Ado.ExecuteCommandAsync(
            ShortenOperationLeaseSql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@TerminalId", terminalId),
            new SugarParameter("@ExpectedOperationLeaseId", expectedOperationLeaseId),
            DateTime2Parameter("@NewExpiresAt", newExpiresAt));
    }

    public async Task<bool> TryRecordHealthAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        DateTime expectedTerminalUpdatedAt,
        string healthStatus,
        DateTime checkedAt,
        CancellationToken cancellationToken)
    {
        var affected = await dbContext.PosmDb.Ado.ExecuteCommandAsync(
            TryRecordHealthSql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@TerminalId", terminalId),
            DateTime2Parameter("@ExpectedTerminalUpdatedAt", expectedTerminalUpdatedAt),
            new SugarParameter("@HealthStatus", healthStatus),
            DateTime2Parameter("@CheckedAt", checkedAt));
        return affected == 1;
    }

    private sealed class LinklyCloudConfigurationModeRow
    {
        public string? Mode { get; set; }
    }

    private sealed class LinklyCloudOperationLeaseResult
    {
        public bool Acquired { get; set; }
    }

    private sealed class LinklyCloudTerminalDisplayNameRow
    {
        public string? DisplayName { get; set; }
    }

    // DATETIME2(7) 需要显式绑定；默认 datetime 会舍入时间戳，破坏终端配置的 CAS。
    internal static SugarParameter DateTime2Parameter(string name, DateTime value) =>
        new(name, value) { DbType = System.Data.DbType.DateTime2 };

}
