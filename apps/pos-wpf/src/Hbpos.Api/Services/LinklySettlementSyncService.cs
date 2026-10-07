using System.Globalization;
using System.Text.Json;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Api.Services;

public interface ILinklySettlementSyncService
{
    Task<LinklySettlementSyncResponse> SyncAsync(
        LinklySettlementSyncRequest request,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken);
}

internal sealed class LinklySettlementSyncService(
    ILinklySettlementRepository repository,
    TimeProvider? timeProvider = null,
    ILogger<LinklySettlementSyncService>? logger = null) : ILinklySettlementSyncService
{
    // 修复前仓储按 SQL datetime（1/300 秒刻度）写入时间戳，旧值与客户端原值最多相差半个刻度（约 1.667ms）；
    // 再放宽 1 个 100ns 刻度覆盖 datetime→datetime2(7) 换算的进位。
    private const long LegacyDateTimeTolerance = TimeSpan.TicksPerSecond / 600 + 1;
    private const int MaximumReceiptCount = 16;
    private const int MaximumReceiptLength = 64 * 1024;
    private const int MaximumReceiptTotalLength = 512 * 1024;
    private const int MaximumSettlementDataLength = 256 * 1024;
    private const string CloudBackendMode = "CloudBackendAsync";
    private static readonly string[] AllowedModes = ["LocalIp", "CloudDirectSync", CloudBackendMode];
    private static readonly string[] AllowedEnvironments = ["Production", "Sandbox"];
    private static readonly string[] AllowedStatuses = ["Pending", "Unknown", "Succeeded", "Failed"];
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<LinklySettlementSyncResponse> SyncAsync(
        LinklySettlementSyncRequest request,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = ValidateAndNormalize(request, storeCode, deviceCode, clock.GetUtcNow());
        normalized = await AttachCloudBackendSessionAsync(normalized, cancellationToken);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await repository.GetAsync(
                normalized.StoreCode,
                normalized.DeviceCode,
                normalized.SettlementGuid,
                cancellationToken);
            var providerExisting = string.IsNullOrWhiteSpace(normalized.ProviderSessionId)
                ? null
                : await repository.GetByProviderSessionAsync(
                    normalized.ConnectionMode,
                    normalized.Environment,
                    normalized.StoreCode,
                    normalized.DeviceCode,
                    normalized.ProviderSessionId,
                    cancellationToken);
            if (providerExisting is not null && providerExisting.SettlementGuid != normalized.SettlementGuid)
            {
                throw Conflict(
                    "PROVIDER_SESSION_CONFLICT",
                    "The Linkly provider session is already linked to another settlement record.",
                    $"providerSessionId={normalized.ProviderSessionId} linkedSettlement={providerExisting.SettlementGuid}");
            }

            if (existing is null)
            {
                if (await repository.TryInsertAsync(normalized, cancellationToken))
                {
                    LogAccepted(normalized, "Inserted", storedRevision: null);
                    return new LinklySettlementSyncResponse(true, false, normalized.ClientRevision);
                }

                continue;
            }

            if (normalized.ClientRevision < existing.ClientRevision)
            {
                LogAccepted(normalized, "StaleRevision", existing.ClientRevision);
                return new LinklySettlementSyncResponse(true, true, existing.ClientRevision);
            }

            if (normalized.ClientRevision == existing.ClientRevision)
            {
                if (DescribeFirstDifference(existing, normalized) is { } difference)
                {
                    throw Conflict(
                        "REVISION_CONTENT_CONFLICT",
                        "The same Linkly settlement revision was uploaded with different content.",
                        difference);
                }

                LogAccepted(normalized, "DuplicateRevision", existing.ClientRevision);
                return new LinklySettlementSyncResponse(true, true, existing.ClientRevision);
            }

            ValidateHigherRevision(existing, normalized);
            normalized.Id = existing.Id;
            normalized.ReceivedAtUtc = existing.ReceivedAtUtc;
            if (await repository.TryUpdateAsync(normalized, existing.ClientRevision, cancellationToken))
            {
                LogAccepted(normalized, "Updated", existing.ClientRevision);
                return new LinklySettlementSyncResponse(true, false, normalized.ClientRevision);
            }
        }

        throw Conflict(
            "SETTLEMENT_SYNC_CONCURRENT_UPDATE",
            "The Linkly settlement changed concurrently. Retry the latest snapshot.");
    }

    private PosmLinklySettlementRecord ValidateAndNormalize(
        LinklySettlementSyncRequest request,
        string storeCode,
        string deviceCode,
        DateTimeOffset now)
    {
        if (request.SchemaVersion != 1)
        {
            throw Invalid("UNSUPPORTED_SCHEMA_VERSION", "schemaVersion must be 1.");
        }

        if (request.SettlementGuid == Guid.Empty)
        {
            throw Invalid("SETTLEMENT_GUID_REQUIRED", "settlementGuid is required.");
        }

        var normalizedStoreCode = Required(storeCode, 32, "STORE_CODE_REQUIRED", "Authenticated store code is required.");
        var normalizedDeviceCode = Required(deviceCode, 64, "DEVICE_CODE_REQUIRED", "Authenticated device code is required.");
        var connectionMode = Canonical(request.ConnectionMode, AllowedModes, "INVALID_CONNECTION_MODE");
        var environment = Canonical(request.Environment, AllowedEnvironments, "INVALID_ENVIRONMENT");
        var status = Canonical(request.Status, AllowedStatuses, "INVALID_SETTLEMENT_STATUS");
        var providerSessionId = Optional(request.ProviderSessionId, 64, "PROVIDER_SESSION_TOO_LONG");
        var providerSubmissionState = ResolveProviderSubmissionState(
            request.ProviderSubmissionState,
            status,
            providerSessionId);
        if (request.BusinessDate == default)
        {
            throw Invalid("BUSINESS_DATE_REQUIRED", "businessDate is required.");
        }

        if (request.RequestedAt == default)
        {
            throw Invalid("REQUESTED_AT_REQUIRED", "requestedAt is required.");
        }

        if (request.ClientRevision <= 0)
        {
            throw Invalid("INVALID_CLIENT_REVISION", "clientRevision must be greater than zero.");
        }

        if (request.PrintCount < 0)
        {
            throw Invalid("INVALID_PRINT_COUNT", "printCount cannot be negative.");
        }

        if (request.CompletedAt is { } completedAt && completedAt < request.RequestedAt)
        {
            throw Invalid("INVALID_COMPLETED_AT", "completedAt cannot be earlier than requestedAt.");
        }

        if (request.FirstPrintedAt is { } firstPrintedAt &&
            request.LastPrintedAt is { } lastPrintedAt &&
            lastPrintedAt < firstPrintedAt)
        {
            throw Invalid("INVALID_PRINT_TIMESTAMPS", "lastPrintedAt cannot be earlier than firstPrintedAt.");
        }

        if (request.PrintCount == 0 &&
            (request.FirstPrintedAt is not null || request.LastPrintedAt is not null))
        {
            throw Invalid("INVALID_PRINT_AUDIT", "print timestamps require a positive printCount.");
        }

        if (request.PrintCount > 0 &&
            (request.FirstPrintedAt is null || request.LastPrintedAt is null))
        {
            throw Invalid("INVALID_PRINT_AUDIT", "a positive printCount requires firstPrintedAt and lastPrintedAt.");
        }

        if (status is "Succeeded" or "Failed" && request.CompletedAt is null)
        {
            throw Invalid("COMPLETED_AT_REQUIRED", "Final settlement status requires completedAt.");
        }

        ValidateCloudBackendSubmissionState(
            connectionMode,
            status,
            providerSubmissionState,
            providerSessionId);

        var receiptTexts = LinklyReceiptTextSanitizer.SanitizeReceipts(request.ReceiptTexts);
        if (receiptTexts.Count > MaximumReceiptCount ||
            receiptTexts.Any(static receipt => receipt.Length > MaximumReceiptLength) ||
            receiptTexts.Sum(static receipt => receipt.Length) > MaximumReceiptTotalLength)
        {
            throw Invalid("RECEIPT_PAYLOAD_TOO_LARGE", "Linkly settlement receipt payload is too large.");
        }

        return new PosmLinklySettlementRecord
        {
            SettlementGuid = request.SettlementGuid,
            StoreCode = normalizedStoreCode,
            DeviceCode = normalizedDeviceCode,
            BusinessDate = request.BusinessDate.ToDateTime(TimeOnly.MinValue),
            ConnectionMode = connectionMode,
            Environment = environment,
            ProviderSessionId = providerSessionId,
            ProviderSubmissionState = providerSubmissionState.ToString(),
            Status = status,
            ResponseCode = Optional(request.ResponseCode, 32, "RESPONSE_CODE_TOO_LONG"),
            ResponseText = Optional(LinklyReceiptTextSanitizer.Sanitize(request.ResponseText), 512, "RESPONSE_TEXT_TOO_LONG"),
            SettlementData = Optional(LinklyReceiptTextSanitizer.SanitizeSettlementData(request.SettlementData), MaximumSettlementDataLength, "SETTLEMENT_DATA_TOO_LARGE"),
            ReceiptTextsJson = JsonSerializer.Serialize(receiptTexts),
            RequestedAtUtc = request.RequestedAt.ToUniversalTime(),
            CompletedAtUtc = request.CompletedAt?.ToUniversalTime(),
            FirstPrintedAtUtc = request.FirstPrintedAt?.ToUniversalTime(),
            LastPrintedAtUtc = request.LastPrintedAt?.ToUniversalTime(),
            PrintCount = request.PrintCount,
            LastPrintError = Optional(LinklyReceiptTextSanitizer.Sanitize(request.LastPrintError), 512, "PRINT_ERROR_TOO_LONG"),
            ClientRevision = request.ClientRevision,
            ReceivedAtUtc = now.ToUniversalTime(),
            UpdatedAtUtc = now.ToUniversalTime()
        };
    }

    private async Task<PosmLinklySettlementRecord> AttachCloudBackendSessionAsync(
        PosmLinklySettlementRecord settlement,
        CancellationToken cancellationToken)
    {
        if (settlement.ConnectionMode != CloudBackendMode ||
            string.IsNullOrWhiteSpace(settlement.ProviderSessionId))
        {
            return settlement;
        }

        var fact = await repository.GetCloudBackendSettlementAsync(
            settlement.Environment,
            settlement.StoreCode,
            settlement.DeviceCode,
            settlement.ProviderSessionId,
            cancellationToken);
        if (fact is null)
        {
            throw Conflict(
                "CLOUD_BACKEND_SESSION_NOT_FOUND",
                "The CloudBackendAsync settlement session was not found in the authenticated device scope.");
        }

        settlement.CloudBackendSessionId = fact.Id;
        var finalStatus = ResolveCloudBackendFinalStatus(fact);
        if (settlement.Status is "Succeeded" or "Failed")
        {
            if (finalStatus is null)
            {
                throw Conflict(
                    "CLOUD_BACKEND_SESSION_NOT_FINAL",
                    "The CloudBackendAsync session does not yet contain a final settlement result.");
            }

            if (!Same(finalStatus, settlement.Status))
            {
                throw Conflict(
                    "CLOUD_BACKEND_RESULT_CONFLICT",
                    "The uploaded settlement result does not match the linked CloudBackendAsync session.");
            }
        }

        // CloudBackend 原始事实保留在既有 session 表；这里只保存客户端快照和稳定关联，避免晚到回调破坏 revision 幂等。
        return settlement;
    }

    private static string? ResolveCloudBackendFinalStatus(LinklyCloudBackendSettlementFact fact)
    {
        if (LinklyCloudBackendStatusConstants.IsSettlementFailureStatus(fact.Status))
        {
            return "Failed";
        }

        if (fact.OperationSuccess == false)
        {
            return "Failed";
        }

        if (fact.OperationSuccess is not null && HasSettlementReceipt(fact.SettlementReceiptTexts))
        {
            return LinklyCloudBackendStatusConstants.IsSuccessfulSettlement(
                fact.OperationSuccess,
                fact.ResponseCode)
                ? "Succeeded"
                : "Failed";
        }

        return null;
    }

    private static bool HasSettlementReceipt(string? receiptTextsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(receiptTextsJson ?? "[]")?
                .Any(static receipt => !string.IsNullOrWhiteSpace(receipt)) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ProviderSubmissionState ResolveProviderSubmissionState(
        ProviderSubmissionState? requestedState,
        string status,
        string? providerSessionId)
    {
        if (status is "Pending" or "Unknown")
        {
            if (requestedState is { } state && state != ProviderSubmissionState.Unknown)
            {
                throw Invalid(
                    "INVALID_PROVIDER_SUBMISSION_STATE",
                    "Pending or unknown settlements must use providerSubmissionState Unknown.");
            }

            return ProviderSubmissionState.Unknown;
        }

        if (requestedState is { } explicitState)
        {
            return explicitState;
        }

        // 兼容 SchemaVersion=1 的旧客户端：只能根据既有会话关联做保守推断。
        return status == "Succeeded" || providerSessionId is not null
            ? ProviderSubmissionState.Submitted
            : status == "Failed"
                ? ProviderSubmissionState.NotSubmitted
                : ProviderSubmissionState.Unknown;
    }

    private static void ValidateCloudBackendSubmissionState(
        string connectionMode,
        string status,
        ProviderSubmissionState providerSubmissionState,
        string? providerSessionId)
    {
        if (status is "Pending" or "Unknown")
        {
            if (providerSubmissionState != ProviderSubmissionState.Unknown)
            {
                throw Invalid(
                    "INVALID_PROVIDER_SUBMISSION_STATE",
                    "Pending or unknown CloudBackendAsync settlements must have an unknown provider submission state.");
            }

            return;
        }

        if (status == "Succeeded")
        {
            if (providerSubmissionState != ProviderSubmissionState.Submitted)
            {
                throw Invalid(
                    "INVALID_PROVIDER_SUBMISSION_STATE",
                    "A successful settlement must have been submitted to the provider.");
            }

            if (Same(connectionMode, CloudBackendMode) && providerSessionId is null)
            {
                throw Invalid(
                    "PROVIDER_SESSION_REQUIRED",
                    "A submitted CloudBackendAsync settlement requires providerSessionId.");
            }

            return;
        }

        if (providerSubmissionState == ProviderSubmissionState.Unknown)
        {
            throw Invalid(
                "INVALID_PROVIDER_SUBMISSION_STATE",
                "A failed settlement must be Submitted or NotSubmitted.");
        }

        if (!Same(connectionMode, CloudBackendMode))
        {
            return;
        }

        if (providerSubmissionState == ProviderSubmissionState.Submitted)
        {
            if (providerSessionId is null)
            {
                throw Invalid(
                    "PROVIDER_SESSION_REQUIRED",
                    "A submitted CloudBackendAsync settlement requires providerSessionId.");
            }

            return;
        }

        if (providerSubmissionState == ProviderSubmissionState.NotSubmitted && providerSessionId is null)
        {
            return;
        }

        throw Invalid(
            "INVALID_PROVIDER_SUBMISSION_STATE",
            "A failed CloudBackendAsync settlement must be Submitted with providerSessionId or NotSubmitted without providerSessionId.");
    }

    private static ProviderSubmissionState GetStoredProviderSubmissionState(PosmLinklySettlementRecord settlement)
    {
        if (Enum.TryParse<ProviderSubmissionState>(settlement.ProviderSubmissionState, ignoreCase: false, out var state) &&
            Enum.IsDefined(state))
        {
            return state;
        }

        return ResolveProviderSubmissionState(
            requestedState: null,
            status: settlement.Status,
            providerSessionId: settlement.ProviderSessionId);
    }

    private static void ValidateHigherRevision(
        PosmLinklySettlementRecord existing,
        PosmLinklySettlementRecord incoming)
    {
        if (DescribeImmutableDifference(existing, incoming) is { } immutableDifference)
        {
            throw Conflict(
                "IMMUTABLE_FIELDS_CONFLICT",
                "Immutable Linkly settlement fields cannot change.",
                immutableDifference);
        }

        if (existing.ProviderSessionId is not null &&
            !Same(existing.ProviderSessionId, incoming.ProviderSessionId))
        {
            throw Conflict(
                "PROVIDER_SESSION_CONFLICT",
                "providerSessionId cannot change once assigned.",
                Field("ProviderSessionId", existing.ProviderSessionId, incoming.ProviderSessionId));
        }

        if (existing.CloudBackendSessionId is not null &&
            existing.CloudBackendSessionId != incoming.CloudBackendSessionId)
        {
            throw Conflict(
                "CLOUD_BACKEND_SESSION_CONFLICT",
                "CloudBackendAsync session linkage cannot change.",
                Field("CloudBackendSessionId", existing.CloudBackendSessionId?.ToString(CultureInfo.InvariantCulture), incoming.CloudBackendSessionId?.ToString(CultureInfo.InvariantCulture)));
        }

        if (!IsAllowedStatusProgression(existing.Status, incoming.Status))
        {
            throw Conflict(
                "STATUS_REGRESSION",
                "Linkly settlement status cannot regress or change between final states.",
                Field("Status", existing.Status, incoming.Status));
        }

        var statusAdvanced = !Same(existing.Status, incoming.Status);
        if (!statusAdvanced && DescribeBankEvidenceDifference(existing, incoming) is { } bankDifference)
        {
            throw Conflict(
                "BANK_EVIDENCE_CONFLICT",
                "A higher revision cannot rewrite bank evidence without a valid status progression.",
                bankDifference);
        }

        ValidatePrintProgression(existing, incoming);
    }

    private static void ValidatePrintProgression(
        PosmLinklySettlementRecord existing,
        PosmLinklySettlementRecord incoming)
    {
        if (incoming.PrintCount < existing.PrintCount ||
            existing.FirstPrintedAtUtc is not null &&
            !SameStoredInstant(existing.FirstPrintedAtUtc, incoming.FirstPrintedAtUtc) ||
            existing.LastPrintedAtUtc is not null &&
            IsBeforeStored(incoming.LastPrintedAtUtc, existing.LastPrintedAtUtc.Value))
        {
            throw Conflict(
                "PRINT_AUDIT_REGRESSION",
                "Linkly settlement print audit cannot regress.",
                DescribePrintAudit(existing, incoming));
        }

        if (incoming.PrintCount == existing.PrintCount &&
            (!SameStoredInstant(existing.FirstPrintedAtUtc, incoming.FirstPrintedAtUtc) ||
             !SameStoredInstant(existing.LastPrintedAtUtc, incoming.LastPrintedAtUtc)))
        {
            throw Conflict(
                "PRINT_AUDIT_CONFLICT",
                "Print timestamps cannot change without advancing printCount.",
                DescribePrintAudit(existing, incoming));
        }
    }

    private static string? DescribeImmutableDifference(
        PosmLinklySettlementRecord existing,
        PosmLinklySettlementRecord incoming)
    {
        if (existing.BusinessDate.Date != incoming.BusinessDate.Date)
        {
            return Field("BusinessDate", FormatDate(existing.BusinessDate), FormatDate(incoming.BusinessDate));
        }

        if (!Same(existing.ConnectionMode, incoming.ConnectionMode))
        {
            return Field("ConnectionMode", existing.ConnectionMode, incoming.ConnectionMode);
        }

        if (!Same(existing.Environment, incoming.Environment))
        {
            return Field("Environment", existing.Environment, incoming.Environment);
        }

        return SameStoredInstant(existing.RequestedAtUtc, incoming.RequestedAtUtc)
            ? null
            : Field("RequestedAtUtc", FormatInstant(existing.RequestedAtUtc), FormatInstant(incoming.RequestedAtUtc));
    }

    private static string? DescribeBankEvidenceDifference(
        PosmLinklySettlementRecord existing,
        PosmLinklySettlementRecord incoming)
    {
        if (GetStoredProviderSubmissionState(existing) != GetStoredProviderSubmissionState(incoming))
        {
            return Field(
                "ProviderSubmissionState",
                GetStoredProviderSubmissionState(existing).ToString(),
                GetStoredProviderSubmissionState(incoming).ToString());
        }

        if (!Same(existing.ResponseCode, incoming.ResponseCode))
        {
            return Field("ResponseCode", existing.ResponseCode, incoming.ResponseCode);
        }

        // 回执、结算数据可能带银行凭证内容，只记字段名和长度，不记原文。
        return TextField("ResponseText", existing.ResponseText, incoming.ResponseText)
            ?? TextField("SettlementData", existing.SettlementData, incoming.SettlementData)
            ?? TextField("ReceiptTextsJson", existing.ReceiptTextsJson, incoming.ReceiptTextsJson)
            ?? (SameStoredInstant(existing.CompletedAtUtc, incoming.CompletedAtUtc)
                ? null
                : Field("CompletedAtUtc", FormatInstant(existing.CompletedAtUtc), FormatInstant(incoming.CompletedAtUtc)));
    }

    /// <summary>
    /// 同修订号重复上传时返回第一个不一致的字段说明；完全一致返回 null（视为幂等重放）。
    /// 字段顺序与比较口径保持修复前的 Equivalent 一致，只有时间戳改用 SameStoredInstant 兼容旧数据。
    /// </summary>
    private static string? DescribeFirstDifference(PosmLinklySettlementRecord existing, PosmLinklySettlementRecord incoming)
    {
        if (existing.SettlementGuid != incoming.SettlementGuid)
        {
            return Field("SettlementGuid", existing.SettlementGuid.ToString(), incoming.SettlementGuid.ToString());
        }

        if (!Same(existing.StoreCode, incoming.StoreCode))
        {
            return Field("StoreCode", existing.StoreCode, incoming.StoreCode);
        }

        if (!Same(existing.DeviceCode, incoming.DeviceCode))
        {
            return Field("DeviceCode", existing.DeviceCode, incoming.DeviceCode);
        }

        if (DescribeImmutableDifference(existing, incoming) is { } immutableDifference)
        {
            return immutableDifference;
        }

        if (!Same(existing.ProviderSessionId, incoming.ProviderSessionId))
        {
            return Field("ProviderSessionId", existing.ProviderSessionId, incoming.ProviderSessionId);
        }

        if (existing.CloudBackendSessionId != incoming.CloudBackendSessionId)
        {
            return Field(
                "CloudBackendSessionId",
                existing.CloudBackendSessionId?.ToString(CultureInfo.InvariantCulture),
                incoming.CloudBackendSessionId?.ToString(CultureInfo.InvariantCulture));
        }

        if (!Same(existing.Status, incoming.Status))
        {
            return Field("Status", existing.Status, incoming.Status);
        }

        if (DescribeBankEvidenceDifference(existing, incoming) is { } bankDifference)
        {
            return bankDifference;
        }

        if (!SameStoredInstant(existing.FirstPrintedAtUtc, incoming.FirstPrintedAtUtc) ||
            !SameStoredInstant(existing.LastPrintedAtUtc, incoming.LastPrintedAtUtc) ||
            existing.PrintCount != incoming.PrintCount)
        {
            return DescribePrintAudit(existing, incoming);
        }

        if (TextField("LastPrintError", existing.LastPrintError, incoming.LastPrintError) is { } printErrorDifference)
        {
            return printErrorDifference;
        }

        return existing.ClientRevision == incoming.ClientRevision
            ? null
            : Field(
                "ClientRevision",
                existing.ClientRevision.ToString(CultureInfo.InvariantCulture),
                incoming.ClientRevision.ToString(CultureInfo.InvariantCulture));
    }

    private static string DescribePrintAudit(PosmLinklySettlementRecord existing, PosmLinklySettlementRecord incoming)
    {
        return $"field=PrintAudit printCount stored={existing.PrintCount} incoming={incoming.PrintCount} " +
            $"firstPrintedAtUtc stored={FormatInstant(existing.FirstPrintedAtUtc)} incoming={FormatInstant(incoming.FirstPrintedAtUtc)} " +
            $"lastPrintedAtUtc stored={FormatInstant(existing.LastPrintedAtUtc)} incoming={FormatInstant(incoming.LastPrintedAtUtc)}";
    }

    private static string Field(string name, string? stored, string? incoming)
    {
        return $"field={name} stored={stored ?? "null"} incoming={incoming ?? "null"}";
    }

    private static string? TextField(string name, string? stored, string? incoming)
    {
        return Same(stored, incoming)
            ? null
            : $"field={name} storedLength={stored?.Length ?? 0} incomingLength={incoming?.Length ?? 0}";
    }

    private static string FormatDate(DateTime value)
    {
        return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string FormatInstant(DateTimeOffset? value)
    {
        return value?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? "null";
    }

    private static bool IsAllowedStatusProgression(string existing, string incoming)
    {
        if (Same(existing, incoming))
        {
            return true;
        }

        return existing switch
        {
            "Pending" => incoming is "Unknown" or "Succeeded" or "Failed",
            "Unknown" => incoming is "Succeeded" or "Failed",
            _ => false
        };
    }

    /// <summary>
    /// 比较「库里已存的时间」与「客户端本次上传的时间」。完全相等即同一时刻；
    /// 另外兼容修复前按 SQL datetime 写入的旧行：库里值落在 1/300 秒刻度上、且与上传值相差不超过半个刻度时，
    /// 视为同一时刻（库里值就是上传原值被舍入的结果）。参数顺序有意义：第一个必须是库里的值。
    /// </summary>
    internal static bool SameStoredInstant(DateTimeOffset? stored, DateTimeOffset? incoming)
    {
        if (stored is null || incoming is null)
        {
            return stored is null && incoming is null;
        }

        var storedUtc = stored.Value.UtcDateTime;
        var incomingUtc = incoming.Value.UtcDateTime;
        return storedUtc == incomingUtc ||
            IsOnLegacySqlDateTimeGrid(storedUtc) &&
            Math.Abs((storedUtc - incomingUtc).Ticks) <= LegacyDateTimeTolerance;
    }

    /// <summary>上传值是否早于库里的值；库里是舍入过的旧值时，半个刻度内的差异不算「更早」。</summary>
    private static bool IsBeforeStored(DateTimeOffset? incoming, DateTimeOffset stored)
    {
        return incoming is null || incoming < stored && !SameStoredInstant(stored, incoming);
    }

    /// <summary>SQL datetime 以 1/300 秒为刻度；换算成 DATETIME2(7) 后落在刻度上（允许 1 个 100ns 的进位误差）。</summary>
    internal static bool IsOnLegacySqlDateTimeGrid(DateTime value)
    {
        var ticksInSecond = value.Ticks % TimeSpan.TicksPerSecond;
        var sqlTicks = Math.Round(ticksInSecond * 300d / TimeSpan.TicksPerSecond);
        return Math.Abs(ticksInSecond - sqlTicks * TimeSpan.TicksPerSecond / 300d) <= 1;
    }

    private void LogAccepted(PosmLinklySettlementRecord settlement, string result, long? storedRevision)
    {
        // 受理结果只写本地日志文件（Information 不进中心日志），用于事后还原某条结算各修订号的到达顺序。
        logger?.LogInformation(
            "Linkly settlement sync accepted result={Result} store={StoreCode} device={DeviceCode} settlement={SettlementGuid} revision={ClientRevision} storedRevision={StoredRevision} status={Status} printCount={PrintCount}",
            result,
            settlement.StoreCode,
            settlement.DeviceCode,
            settlement.SettlementGuid,
            settlement.ClientRevision,
            storedRevision,
            settlement.Status,
            settlement.PrintCount);
    }

    private static bool Same(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static string Canonical(string? value, IEnumerable<string> allowed, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid(errorCode, $"{errorCode}.");
        }

        var canonical = allowed.FirstOrDefault(candidate =>
            string.Equals(candidate, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return canonical ?? throw Invalid(errorCode, $"{errorCode}.");
    }

    private static string Required(string? value, int maxLength, string errorCode, string message)
    {
        return Optional(value, maxLength, errorCode) ?? throw Invalid(errorCode, message);
    }

    private static string? Optional(string? value, int maxLength, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : throw Invalid(errorCode, $"Value exceeds {maxLength} characters.");
    }

    private static LinklySettlementValidationException Invalid(string code, string message)
    {
        return new LinklySettlementValidationException(code, message);
    }

    private static LinklySettlementConflictException Conflict(string code, string message, string? detail = null)
    {
        return new LinklySettlementConflictException(code, message, detail);
    }
}

public sealed class LinklySettlementValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <param name="detail">
/// 冲突明细（哪个字段、库里值、上传值），只写服务端日志、不回给客户端；
/// 回执原文等敏感文本只记字段名与长度。
/// </param>
public sealed class LinklySettlementConflictException(string code, string message, string? detail = null) : Exception(message)
{
    public string Code { get; } = code;

    public string? Detail { get; } = detail;
}
